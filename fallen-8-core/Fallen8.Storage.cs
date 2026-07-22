// MIT License
//
// Fallen8.Storage.cs
//
// Copyright (c) 2025 Henning Rauch
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Model;

namespace NoSQL.GraphDB.Core
{
    public sealed partial class Fallen8
    {
        #region master store mutation (single-writer, append-only segmented array)

        /// <summary>
        ///   Appends one element to the master store. Runs ONLY on the single TransactionManager
        ///   writer thread. The new element's id equals its index (<c>= current Count</c>).
        ///
        ///   Publication ordering (the crux of the lock-free contract):
        ///   1. The spare slot being written is at index &gt;= the currently published Count, so
        ///      no reader (which only reads ids in <c>[0, Count)</c>) can observe it yet.
        ///   2. We write that slot FIRST, then publish a NEW holder whose Count is one larger.
        ///      Count is therefore published LAST, and the volatile store of <c>_snapshot</c> is a
        ///      release: a reader that acquires the new holder (and thus the new Count) is
        ///      guaranteed to also see the fully written slot and the fully constructed element.
        ///   3. A reader that still holds the old holder sees the old Count and never touches the
        ///      new slot. So a reader observes either "element absent" or "element fully present",
        ///      never a torn or null slot.
        ///   Growing only allocates a new 32 KB segment (no whole-store copy, no LOH churn); the
        ///   top-level segment array is copied only when a new segment is added (rare).
        /// </summary>
        private void AppendGraphElement(AGraphElementModel element)
        {
            var snap = _snapshot;
            int index = snap.Count;                 // new id == index
            int seg = index >> SegmentShift;
            int slot = index & SegmentMask;

            AGraphElementModel[][] segments = snap.Segments;
            if (seg >= segments.Length)
            {
                // Last segment is full: grow the top-level array (copy-on-write) and add a fresh
                // segment. The old segments array is never mutated, so old-holder readers are safe.
                var grown = new AGraphElementModel[seg + 1][];
                Array.Copy(segments, grown, segments.Length);
                grown[seg] = new AGraphElementModel[SegmentSize];
                segments = grown;
            }

            segments[seg][slot] = element;          // (1)+(2): write the spare slot FIRST ...
            _snapshot = new Snapshot(segments, index + 1); // ... then publish Count LAST (release).
        }

        /// <summary>
        ///   Appends a batch of elements in ONE publication (single Count bump). Same ordering
        ///   guarantee as <see cref="AppendGraphElement" />: every slot written is at index
        ///   &gt;= the old Count, all slots are written before the new holder (with the larger
        ///   Count) is published. <paramref name="elements"/> is a covariant read-only list so a
        ///   <c>List&lt;VertexModel&gt;</c>/<c>List&lt;EdgeModel&gt;</c> can be passed directly.
        /// </summary>
        private void AppendGraphElements(IReadOnlyList<AGraphElementModel> elements)
        {
            int n = elements?.Count ?? 0;
            if (n == 0)
            {
                return;
            }

            var snap = _snapshot;
            int startCount = snap.Count;
            int newCount = startCount + n;

            // Ensure enough segments for newCount (copy-on-write the top-level array if it grows).
            int neededSegments = (newCount + SegmentMask) >> SegmentShift;
            AGraphElementModel[][] segments = snap.Segments;
            if (neededSegments > segments.Length)
            {
                var grown = new AGraphElementModel[neededSegments][];
                Array.Copy(segments, grown, segments.Length);
                for (int s = segments.Length; s < neededSegments; s++)
                {
                    grown[s] = new AGraphElementModel[SegmentSize];
                }
                segments = grown;
            }

            // Write every element into its (index >= startCount) slot BEFORE publishing the Count.
            for (int i = 0; i < n; i++)
            {
                int index = startCount + i;
                segments[index >> SegmentShift][index & SegmentMask] = elements[i];
            }

            _snapshot = new Snapshot(segments, newCount); // publish Count LAST (release).
        }

        /// <summary>
        ///   Resolves an element by id for a single-writer mutation. Preserves the historical
        ///   contract that an out-of-range id throws <see cref="ArgumentOutOfRangeException"/>:
        ///   the original <c>ImmutableList</c> indexer threw that type, whereas a raw array
        ///   indexer would throw <see cref="IndexOutOfRangeException"/>. The returned element may
        ///   itself be null (a slot left empty by a load).
        /// </summary>
        private AGraphElementModel GetGraphElementForMutation(Int32 graphElementId)
        {
            var snap = _snapshot;
            if (graphElementId < 0 || graphElementId >= snap.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(graphElementId));
            }
            return snap.Segments[graphElementId >> SegmentShift][graphElementId & SegmentMask];
        }

        /// <summary>
        ///   Resolves a vertex by id for WIRING AN EDGE, returning <c>null</c> - rather than
        ///   throwing - when the id is out of range, the slot is empty (left by a load), the element
        ///   is not a vertex, or the vertex has been removed. Unlike
        ///   <see cref="GetGraphElementForMutation" /> (whose historical out-of-range throw is relied
        ///   on by the removal/property paths), this lets <c>CreateEdge(s)</c> fail a client-caused
        ///   missing/removed endpoint CLEANLY (NotFound) instead of letting the master-store bounds
        ///   check throw - which used to surface as a misleading 500. Single-writer, so a vertex
        ///   resolved here cannot be concurrently removed before the edge is wired.
        /// </summary>
        private VertexModel TryResolveLiveVertexForEdge(Int32 vertexId)
        {
            var snap = _snapshot;
            if (vertexId < 0 || vertexId >= snap.Count)
            {
                return null;
            }

            var vertex = snap.Segments[vertexId >> SegmentShift][vertexId & SegmentMask] as VertexModel;
            if (vertex == null || vertex._removed)
            {
                return null;
            }

            return vertex;
        }

        #endregion

        /// <summary>
        ///   Interns a schema-like string (label / property key / edge-property-id) so all
        ///   elements that use the same value share one instance (finding M2). Returns the
        ///   argument unchanged for <c>null</c>. The returned string is always value-equal to the
        ///   argument, so callers observe no change.
        /// </summary>
        internal String Intern(String value)
        {
            if (value == null)
            {
                return null;
            }

            // Bound the table (see _internTableCap): once it holds a cap's worth of distinct
            // strings, stop adding new ones and return the argument unchanged, so interning becomes
            // a no-op past the cap. The Count read races with a concurrent writer, but the table is
            // populated only from the single transaction-writer thread and the goal is bounding,
            // not an exact size, so a slight overshoot is harmless. Correctness is unaffected
            // either way: the result is always value-equal to the argument.
            if (_internTable.Count >= _internTableCap)
            {
                return value;
            }

            return _internTable.GetOrAdd(value, value);
        }

        /// <summary>
        ///   Returns a property dictionary whose KEYS are interned (finding M2). The values are
        ///   never touched (they are user data, not schema). Returns the input unchanged when it
        ///   is null or empty (an empty/absent property map allocates no container, per M1).
        /// </summary>
        private Dictionary<String, Object> InternPropertyKeys(Dictionary<String, Object> properties)
        {
            if (properties == null || properties.Count == 0)
            {
                return properties;
            }

            var interned = new Dictionary<String, Object>(properties.Count, StringComparer.Ordinal);
            foreach (var kv in properties)
            {
                interned[Intern(kv.Key)] = kv.Value;
            }
            return interned;
        }

        internal VertexModel CreateVertex_internal(UInt32 creationDate, String label, Dictionary<String, Object> properties = null)
        {
            //create the new vertex (interning the label and property keys, finding M2)
            var newVertex = new VertexModel(_currentId, creationDate, Intern(label), InternPropertyKeys(properties));

            //insert it
            AppendGraphElement(newVertex);

            //increment the id (single-writer field: a plain increment, consistent with the
            //VertexCount/EdgeCount counters beside it - finding P10)
            _currentId++;

            //Increase the vertex count
            VertexCount++;

            // Elements created WITH embedding properties feed bound vector indices
            // (feature element-embeddings; the bulk-import path). A residual rollback
            // compensates via the standard removal, whose index purge undoes this.
            ProjectAllEmbeddingsOf(newVertex);

            return newVertex;
        }

        internal List<VertexModel> CreateVertices_internal(List<VertexDefinition> definitions, out Boolean inputValid)
        {
            // Construct-then-commit (feature transaction-atomicity): nothing is mutated - not the id
            // counter, not VertexCount, not the store - until every model in the batch has been built
            // successfully. So a throw (or a structurally invalid definition) before the atomic append
            // leaves the engine byte-for-byte as it was, and the id == index invariant is preserved
            // under every failure path (the old code advanced _currentId/VertexCount per definition
            // BEFORE the append, so a mid-loop throw left _currentId > Count permanently).
            inputValid = true;
            var newVertices = new List<VertexModel>();

            if (definitions == null || definitions.Count == 0)
            {
                return newVertices;
            }

            // 1. Validate structure up front WITHOUT mutating: a null definition (a JSON array element
            //    can be null) rolls the whole batch back cleanly as InvalidInput rather than NRE-ing
            //    mid-loop after some ids were already consumed.
            foreach (var aVertexDef in definitions)
            {
                if (aVertexDef == null)
                {
                    inputValid = false;
                    return newVertices; // nothing built, nothing mutated
                }
            }

            // 2. Build every model against a LOCAL id counter seeded from _currentId. A throw here
            //    (e.g. OOM) still leaves the engine untouched - no compensation needed.
            var nextId = _currentId;
            foreach (var aVertexDef in definitions)
            {
                //create the new vertex (interning the label and property keys, finding M2)
                newVertices.Add(new VertexModel(nextId, aVertexDef.CreationDate, Intern(aVertexDef.Label), InternPropertyKeys(aVertexDef.Properties)));
                nextId++;
            }

            // 3. Commit atomically: one append (one Count bump), THEN advance the counters. The append
            //    publishes Count last, so a reader sees either none or all of the batch.
            AppendGraphElements(newVertices);
            _currentId = nextId;
            VertexCount += newVertices.Count;

            // Bound-index projection of creation-time embeddings (feature element-embeddings);
            // a residual rollback compensates via the standard removal purge.
            foreach (var newVertex in newVertices)
            {
                ProjectAllEmbeddingsOf(newVertex);
            }

            return newVertices;
        }

        internal EdgeModel CreateEdge_internal(Int32 sourceVertexId, String edgePropertyId, Int32 targetVertexId,
            UInt32 creationDate, String label, Dictionary<String, Object> properties)
        {
            EdgeModel outgoingEdge = null;

            // Verify both endpoints exist and are live BEFORE wiring. A missing/removed/out-of-range
            // endpoint resolves to null here (no throw), so the edge is simply not created and the
            // caller rolls back cleanly with NotFound - instead of the old bounds-check throw -> 500.
            var sourceVertex = TryResolveLiveVertexForEdge(sourceVertexId);
            var targetVertex = TryResolveLiveVertexForEdge(targetVertexId);

            //get the related vertices
            if (sourceVertex != null && targetVertex != null)
            {
                //intern the label, edge-property-id and property keys (finding M2)
                edgePropertyId = Intern(edgePropertyId);
                outgoingEdge = new EdgeModel(_currentId, creationDate, targetVertex, sourceVertex, Intern(label), edgePropertyId, InternPropertyKeys(properties));

                //add the edge to the graph elements
                AppendGraphElement(outgoingEdge);

                //increment the ids (single-writer field, plain increment - finding P10)
                _currentId++;

                //add the edge to the source vertex
                sourceVertex.AddOutEdge(edgePropertyId, outgoingEdge);

                //link the vertices
                targetVertex.AddIncomingEdge(edgePropertyId, outgoingEdge);

                //increase the edgeCount
                EdgeCount++;

                // Bound-index projection of creation-time embeddings (feature element-embeddings).
                ProjectAllEmbeddingsOf(outgoingEdge);
            }

            return outgoingEdge;
        }

        internal void CreateEdges_internal(List<EdgeDefinition> definitions, List<EdgeModel> createdEdges,
            out Boolean inputValid, out Boolean allEndpointsResolved)
        {
            // Construct-then-commit with store-then-adjacency (feature transaction-atomicity). The old
            // code bumped _currentId/EdgeCount and wired adjacency per edge BEFORE the batch append,
            // so a throw mid-loop left the id space corrupt (_currentId > Count) plus dangling
            // adjacency. Now: validate the whole batch, build all models against a local id counter,
            // append to the store FIRST, then wire adjacency (matching the single-edge order so a
            // reader can never traverse to an edge TryGetEdge cannot resolve). The appended edges are
            // recorded into the caller's <paramref name="createdEdges"/> list BEFORE wiring, so if a
            // wiring step still throws (OOM), the transaction's Rollback removes exactly the edges
            // that reached the store.
            inputValid = true;
            allEndpointsResolved = true;

            if (definitions == null || definitions.Count == 0)
            {
                return;
            }

            // 1. Structural validation (no null definitions) - a clean InvalidInput, no throw.
            foreach (var aEdgeDefinition in definitions)
            {
                if (aEdgeDefinition == null)
                {
                    inputValid = false;
                    return; // nothing built, nothing mutated
                }
            }

            // 2. Endpoint validation: EVERY referenced vertex must be live BEFORE anything is built or
            //    wired, so a missing/removed endpoint rolls the whole batch back cleanly (NotFound) and
            //    atomically. Single-writer, so no endpoint can be removed between here and the wiring.
            foreach (var aEdgeDefinition in definitions)
            {
                if (TryResolveLiveVertexForEdge(aEdgeDefinition.SourceVertexId) == null
                    || TryResolveLiveVertexForEdge(aEdgeDefinition.TargetVertexId) == null)
                {
                    allEndpointsResolved = false;
                    return; // nothing wired
                }
            }

            // 3. Build every model against a LOCAL id counter, WITHOUT touching store/adjacency/counters.
            var newEdges = new List<EdgeModel>(definitions.Count);
            var nextId = _currentId;
            foreach (var aEdgeDefinition in definitions)
            {
                // Guaranteed non-null by the pre-validation passes above.
                var sourceVertex = TryResolveLiveVertexForEdge(aEdgeDefinition.SourceVertexId);
                var targetVertex = TryResolveLiveVertexForEdge(aEdgeDefinition.TargetVertexId);

                //intern the label, edge-property-id and property keys (finding M2)
                var edgePropertyId = Intern(aEdgeDefinition.EdgePropertyId);
                newEdges.Add(new EdgeModel(nextId, aEdgeDefinition.CreationDate, targetVertex, sourceVertex,
                    Intern(aEdgeDefinition.Label), edgePropertyId, InternPropertyKeys(aEdgeDefinition.Properties)));
                nextId++;
            }

            // 4. Commit: append to the store FIRST (store-then-adjacency), advance the counters, record
            //    the now-committed edges for rollback, THEN wire adjacency. Recording before wiring
            //    means a wiring throw is fully recoverable (Rollback removes every appended edge,
            //    detaching any partial adjacency and restoring EdgeCount).
            AppendGraphElements(newEdges);
            _currentId = nextId;
            EdgeCount += newEdges.Count;
            createdEdges.AddRange(newEdges);

            // Bound-index projection of creation-time embeddings (feature element-embeddings);
            // recorded in createdEdges already, so a residual wiring throw still purges these.
            foreach (var newEdge in newEdges)
            {
                ProjectAllEmbeddingsOf(newEdge);
            }

            // Batch adjacency wiring (feature supernode-adjacency-build Step 1). The old loop wired one
            // edge at a time (source.AddOutEdge + target.AddIncomingEdge), so k edges landing on one
            // vertex/direction in this batch cost k separate whole-group array rebuilds - O(d²) to build
            // a hub. Group the new edges by (vertex, direction, edge-property-id) FIRST, preserving
            // encounter order, then publish each vertex/direction adjacency ONCE via the batch
            // AddOutEdges/AddIncomingEdges. The edges are already recorded in createdEdges above, so a
            // wiring throw stays fully recoverable (Rollback removes every appended edge, detaching any
            // partial adjacency); a vertex touched under several keys chains the per-key builds and
            // publishes its final instance once.
            var outByVertex = new Dictionary<VertexModel, Dictionary<String, List<EdgeModel>>>();
            var inByVertex = new Dictionary<VertexModel, Dictionary<String, List<EdgeModel>>>();
            for (var i = 0; i < newEdges.Count; i++)
            {
                var edge = newEdges[i];
                GroupEdgeForWiring(outByVertex, edge.SourceVertex, edge.EdgePropertyId, edge);
                GroupEdgeForWiring(inByVertex, edge.TargetVertex, edge.EdgePropertyId, edge);
            }

            foreach (var vertexGroups in outByVertex)
            {
                vertexGroups.Key.AddOutEdges(vertexGroups.Value);
            }
            foreach (var vertexGroups in inByVertex)
            {
                vertexGroups.Key.AddIncomingEdges(vertexGroups.Value);
            }
        }

        /// <summary>
        ///   Buckets an edge under <c>vertex -&gt; edge-property-id -&gt; edges</c>, creating the inner
        ///   maps/lists on demand and preserving encounter order within each group, so
        ///   <see cref="CreateEdges_internal" /> can wire a whole batch with one adjacency publish per
        ///   vertex/direction (feature supernode-adjacency-build Step 1).
        /// </summary>
        private static void GroupEdgeForWiring(Dictionary<VertexModel, Dictionary<String, List<EdgeModel>>> byVertex,
            VertexModel vertex, String edgePropertyId, EdgeModel edge)
        {
            if (!byVertex.TryGetValue(vertex, out var byKey))
            {
                byKey = new Dictionary<String, List<EdgeModel>>();
                byVertex[vertex] = byKey;
            }

            if (!byKey.TryGetValue(edgePropertyId, out var list))
            {
                list = new List<EdgeModel>();
                byKey[edgePropertyId] = list;
            }

            list.Add(edge);
        }

        internal void SetProperty_internal(Int32 graphElementId, String propertyId, Object property)
        {
            AGraphElementModel graphElement = GetGraphElementForMutation(graphElementId);
            if (graphElement != null)
            {
                //intern the property key (finding M2)
                graphElement.SetProperty(Intern(propertyId), property);
            }
        }

        /// <summary>
        ///   Sets a single property and records its inverse into <paramref name="undo"/> ONLY after the
        ///   set has succeeded (feature transaction-atomicity). <see cref="AGraphElementModel.SetProperty"/>
        ///   throws before mutating on a value conflict, so a rolled-back single set leaves nothing to
        ///   undo; the recorded inverse guards the invariant uniformly and covers a residual post-set
        ///   throw. An out-of-range id throws here before any mutation, exactly as the plain setter did.
        /// </summary>
        internal void SetPropertyWithUndo_internal(Int32 graphElementId, String propertyId, Object property,
            List<Transaction.PropertyMutationUndo> undo)
        {
            AGraphElementModel graphElement = GetGraphElementForMutation(graphElementId);
            if (graphElement == null)
            {
                return; // no-op target (empty slot): nothing set, nothing to undo
            }

            var hadValueBefore = graphElement.TryGetProperty<Object>(out var priorValue, propertyId);

            //intern the property key (finding M2)
            graphElement.SetProperty(Intern(propertyId), property);

            // Recorded only after SetProperty returns, so a conflict throw leaves undo empty.
            undo.Add(new Transaction.PropertyMutationUndo(graphElementId, propertyId, hadValueBefore, priorValue));

            // A raw property write to a reserved embedding key feeds bound indices too
            // (feature element-embeddings) - the bulk/import surface writes embeddings this way.
            ProjectEmbeddingPropertyWrite(graphElement, propertyId, property);
        }

        /// <summary>
        ///   Removes a property from an element. Returns whether a property was ACTUALLY removed
        ///   (false for an empty slot or a key the element does not carry), so the change feed can
        ///   report exactly what changed.
        /// </summary>
        internal bool RemoveProperty_internal(Int32 graphElementId, String propertyId)
        {
            var graphElement = GetGraphElementForMutation(graphElementId);
            var removed = graphElement != null && graphElement.RemoveProperty(propertyId);

            if (removed)
            {
                // Removing a reserved embedding key purges the element from bound vector
                // indices of that name (feature element-embeddings).
                ProjectEmbeddingPropertyWrite(graphElement, propertyId, null);
            }

            return removed;
        }

        internal bool TryRemoveGraphElement_private(Int32 graphElementId)
        {
            AGraphElementModel graphElement = GetGraphElementForMutation(graphElementId);

            if (graphElement == null || graphElement._removed)
            {
                return false;
            }

            //used if an edge is removed
            List<String> inEdgeRemovals = null;
            List<String> outEdgeRemovals = null;

            try
            {
                #region remove element

                graphElement.MarkAsRemoved();

                if (graphElement is VertexModel)
                {
                    #region remove vertex

                    var vertex = (VertexModel)graphElement;

                    // Count the DISTINCT cascaded edges that actually transition from live to removed,
                    // so the counters can be maintained incrementally instead of via an O(n) recount
                    // (finding P3). Guarding each MarkAsRemoved on !_removed makes the count exact even
                    // for a self-loop (present in both OutEdges and InEdges): the out-edge pass marks
                    // and counts it, and the target-side detach removes it from InEdges before the
                    // in-edge pass is captured, so it is never double-counted.
                    int removedEdgeCount = 0;

                    #region out edges

                    var outgoingEdgeContainer = vertex.GetRawOutEdges();
                    if (outgoingEdgeContainer != null)
                    {
                        foreach (var aOutEdgeProperty in outgoingEdgeContainer)
                        {
                            foreach (var aOutEdge in aOutEdgeProperty.Value)
                            {
                                //remove from incoming edges of target vertex
                                aOutEdge.TargetVertex.RemoveIncomingEdge(aOutEdgeProperty.Key, aOutEdge);

                                //remove the edge itself (counting only a genuine live->removed transition)
                                if (!aOutEdge._removed)
                                {
                                    aOutEdge.MarkAsRemoved();
                                    removedEdgeCount++;
                                }
                            }
                        }
                    }

                    #endregion

                    #region in edges

                    var incomingEdgeContainer = vertex.GetRawInEdges();
                    if (incomingEdgeContainer != null)
                    {
                        foreach (var aInEdgeProperty in incomingEdgeContainer)
                        {
                            foreach (var aInEdge in aInEdgeProperty.Value)
                            {
                                //remove from outgoing edges of source vertex
                                aInEdge.SourceVertex.RemoveOutGoingEdge(aInEdgeProperty.Key, aInEdge);

                                //remove the edge itself (counting only a genuine live->removed transition)
                                if (!aInEdge._removed)
                                {
                                    aInEdge.MarkAsRemoved();
                                    removedEdgeCount++;
                                }
                            }
                        }
                    }

                    #endregion

                    // Maintain the counts incrementally (finding P3): the vertex itself and its distinct
                    // cascaded edges leave the live set. This runs only on the commit path; if any step
                    // above threw, control is in the catch/finally below, which restores adjacency and
                    // does a full RecalculateGraphElementCounter, so a rolled-back removal is unaffected
                    // and the counts stay exactly correct.
                    VertexCount--;
                    EdgeCount -= removedEdgeCount;

                    #endregion
                }
                else
                {
                    #region remove edge

                    var edge = (EdgeModel)graphElement;

                    //remove from incoming edges of target vertex
                    inEdgeRemovals = edge.TargetVertex.RemoveIncomingEdge(edge);

                    //remove from outgoing edges of source vertex
                    outEdgeRemovals = edge.SourceVertex.RemoveOutGoingEdge(edge);

                    //update the EdgeCount --> easy way
                    EdgeCount--;

                    #endregion
                }

                #endregion
            }
            catch (Exception)
            {
                #region restore

                // Restoring the graph can itself fault (for example on a half-constructed edge).
                // Guard the restore so that a secondary failure neither skips the counter recompute
                // nor masks the original removal failure, which must remain the observed exception.
                try
                {
                    // Removal is a soft-delete: the element is only flagged via MarkAsRemoved and is
                    // never taken out of _graphElements. The correct rollback is therefore to clear that
                    // flag again. (Re-inserting into _graphElements would duplicate the still-present
                    // element and break the id==index invariant, and would not clear the removed flag.)
                    graphElement.MarkAsNotRemoved();

                    if (graphElement is VertexModel)
                    {
                        #region restore vertex

                        var vertex = (VertexModel)graphElement;

                        #region out edges

                        var outgoingEdgeContainer = vertex.GetRawOutEdges();
                        if (outgoingEdgeContainer != null)
                        {
                            foreach (var aOutEdgeProperty in outgoingEdgeContainer)
                            {
                                foreach (var aOutEdge in aOutEdgeProperty.Value)
                                {
                                    //restore into the incoming edges of the target vertex
                                    aOutEdge.TargetVertex.AddIncomingEdge(aOutEdgeProperty.Key, aOutEdge);

                                    //reset the edge
                                    aOutEdge.MarkAsNotRemoved();
                                }
                            }
                        }

                        #endregion

                        #region in edges

                        var incomingEdgeContainer = vertex.GetRawInEdges();
                        if (incomingEdgeContainer != null)
                        {
                            foreach (var aInEdgeProperty in incomingEdgeContainer)
                            {
                                foreach (var aInEdge in aInEdgeProperty.Value)
                                {
                                    //restore into the outgoing edges of the source vertex
                                    //(removal detached it via RemoveOutGoingEdge, so the inverse is AddOutEdge)
                                    aInEdge.SourceVertex.AddOutEdge(aInEdgeProperty.Key, aInEdge);

                                    //reset the edge
                                    aInEdge.MarkAsNotRemoved();
                                }
                            }
                        }

                        #endregion

                        #endregion
                    }
                    else
                    {
                        #region restore edge

                        var edge = (EdgeModel)graphElement;

                        if (inEdgeRemovals != null)
                        {
                            for (var i = 0; i < inEdgeRemovals.Count; i++)
                            {
                                edge.TargetVertex.AddIncomingEdge(inEdgeRemovals[i], edge);
                            }
                        }

                        if (outEdgeRemovals != null)
                        {
                            for (var i = 0; i < outEdgeRemovals.Count; i++)
                            {
                                edge.SourceVertex.AddOutEdge(outEdgeRemovals[i], edge);
                            }
                        }

                        #endregion
                    }
                }
                catch (Exception restoreException)
                {
                    // Swallow (but log) the restore failure so it does not replace the original
                    // removal failure that is rethrown below.
                    _logger.LogError(restoreException,
                        "Failed to fully restore graph element {GraphElementId} after a faulted removal; rolling back with the original failure.",
                        graphElementId);
                }
                finally
                {
                    //recalculate the counter (must run even when the restore above faulted)
                    RecalculateGraphElementCounter();
                }

                #endregion

                throw;
            }

            // Write-end index purge (feature index-lifecycle 3.3). The live->removed transition has now
            // COMMITTED (we are past the try/catch, so a rolled-back removal never reaches here), so drop
            // the element - and, for a vertex, its cascaded-removed incident edges - from every registered
            // index. This stops a removed element being pinned by an index bucket (its body becomes
            // collectable) and complements the read-end FilterLive floor. RemoveValue is O(affected keys)
            // via each index's reverse map, so the fan-out over indices is bounded; it runs here on the
            // single writer, serialised against request-thread index writes by the index's own lock.
            PurgeRemovedElementFromIndices(graphElement);

            return true;
        }

        /// <summary>
        ///   Removes <paramref name="removedElement" /> (and, when it is a vertex, every edge in its
        ///   adjacency - the edges the cascade just removed) from every registered index. Enumerates a
        ///   snapshot of the indices so it cannot race a concurrent create/delete. A best-effort per
        ///   index: an index whose <c>RemoveValue</c> throws is logged and skipped so one faulty index
        ///   cannot fail an otherwise-committed removal.
        /// </summary>
        private void PurgeRemovedElementFromIndices(AGraphElementModel removedElement)
        {
            var indices = IndexFactory?.GetIndicesSnapshot();
            if (indices == null || indices.Count == 0)
            {
                return;
            }

            foreach (var index in indices)
            {
                PurgeValueFromIndex(index, removedElement);

                // A removed vertex takes its incident edges out of the live set too; purge those from
                // the index as well. The removed vertex's OWN adjacency still lists them at this point
                // (it is freed only later, on trim), so it is the authoritative source of the cascaded
                // edges. RemoveValue is an O(1) reverse-map miss for any edge the index never held.
                if (removedElement is VertexModel vertex)
                {
                    PurgeVertexEdgesFromIndex(index, vertex);
                }
            }
        }

        private void PurgeVertexEdgesFromIndex(IIndex index, VertexModel vertex)
        {
            var outEdges = vertex.GetRawOutEdges();
            if (outEdges != null)
            {
                foreach (var group in outEdges)
                {
                    foreach (var edge in group.Value)
                    {
                        PurgeValueFromIndex(index, edge);
                    }
                }
            }

            var inEdges = vertex.GetRawInEdges();
            if (inEdges != null)
            {
                foreach (var group in inEdges)
                {
                    foreach (var edge in group.Value)
                    {
                        PurgeValueFromIndex(index, edge);
                    }
                }
            }
        }

        private void PurgeValueFromIndex(IIndex index, AGraphElementModel element)
        {
            try
            {
                index.RemoveValue(element);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to purge graph element {GraphElementId} from an index after removal; the read-end live filter still hides it, but its body may stay pinned.",
                    element?.Id);
            }
        }

        /// <summary>
        ///   Applies a batch of property sets atomically (feature transaction-atomicity). Pre-validates
        ///   the WHOLE batch before mutating anything - structural validity (no null definitions,
        ///   populated to a clean <see cref="TransactionFailureReason.InvalidInput"/>) and
        ///   conflict-freedom, accounting for intra-batch pending writes so a self-conflicting batch is
        ///   caught too (<see cref="TransactionFailureReason.Conflict"/>). An out-of-range id keeps the
        ///   historical throw (<see cref="ArgumentOutOfRangeException"/> - the worker maps it to
        ///   InternalError/500, per transaction-failure-reasons), but now during validation, before any
        ///   set is applied, so the batch is still atomic. On the happy path each set is applied and its
        ///   inverse recorded into <paramref name="undo"/> (in apply order), so a residual post-validation
        ///   throw (e.g. OOM) is undone by <see cref="RestoreProperties_internal"/>.
        /// </summary>
        internal Boolean SetProperties_internal(List<PropertyAddDefinition> definitions,
            List<Transaction.PropertyMutationUndo> undo, out Transaction.TransactionFailureReason reason)
        {
            reason = Transaction.TransactionFailureReason.None;

            if (definitions == null)
            {
                reason = Transaction.TransactionFailureReason.InvalidInput;
                return false;
            }

            if (definitions.Count == 0)
            {
                return true;
            }

            // 1. Structural validation (no null definitions) - a clean InvalidInput, no throw.
            foreach (var aDefinition in definitions)
            {
                if (aDefinition == null)
                {
                    reason = Transaction.TransactionFailureReason.InvalidInput;
                    return false;
                }
            }

            // 2. Conflict validation. Simulate the batch against the live store WITHOUT mutating,
            //    tracking each (element,key)'s effective value after prior items in this batch, so an
            //    intra-batch conflict (two different values for the same new key) is caught as well as
            //    a conflict with the element's existing value. An out-of-range id throws here (before
            //    any apply), preserving the InternalError/500 boundary while keeping the batch atomic.
            //    A missing (null) slot stays a no-op, matching SetProperty_internal.
            var pending = new Dictionary<(Int32, String), Object>();
            foreach (var aDefinition in definitions)
            {
                var graphElement = GetGraphElementForMutation(aDefinition.GraphElementId);
                if (graphElement == null)
                {
                    continue; // no-op target (empty slot), as today
                }

                // Canonicalize the candidate so the comparison is canonical-to-canonical (the store
                // holds canonical values, and SetProperty compares after canonicalizing): a genuine
                // no-op update (equal value) is not a conflict, a different value is.
                var candidate = AGraphElementModel.CanonicalizeProperty(aDefinition.Property);
                var key = (aDefinition.GraphElementId, aDefinition.PropertyId);

                // The effective current value is the one a prior item in THIS batch set (pending), or
                // else the element's stored value. Either is already canonical.
                Boolean hasEffective;
                Object effective;
                if (!pending.TryGetValue(key, out effective))
                {
                    hasEffective = graphElement.TryGetProperty<Object>(out effective, aDefinition.PropertyId);
                }
                else
                {
                    hasEffective = true;
                }

                if (hasEffective && !AGraphElementModel.ArePropertyValuesEqual(effective, candidate))
                {
                    reason = Transaction.TransactionFailureReason.Conflict;
                    return false;
                }

                pending[key] = candidate;
            }

            // 3. Apply, recording the inverse of each set (in apply order) for a residual-throw undo.
            foreach (var aDefinition in definitions)
            {
                var graphElement = GetGraphElementForMutation(aDefinition.GraphElementId);
                if (graphElement == null)
                {
                    continue;
                }

                var hadValueBefore = graphElement.TryGetProperty<Object>(out var priorValue, aDefinition.PropertyId);
                undo.Add(new Transaction.PropertyMutationUndo(aDefinition.GraphElementId, aDefinition.PropertyId, hadValueBefore, priorValue));

                //intern the property key (finding M2)
                graphElement.SetProperty(Intern(aDefinition.PropertyId), aDefinition.Property);
            }

            // 4. Project reserved embedding keys into bound vector indices (feature
            //    element-embeddings), after every set applied - see SetEmbeddings_internal.
            foreach (var aDefinition in definitions)
            {
                var graphElement = GetGraphElementForMutation(aDefinition.GraphElementId);
                if (graphElement != null)
                {
                    ProjectEmbeddingPropertyWrite(graphElement, aDefinition.PropertyId, aDefinition.Property);
                }
            }

            return true;
        }

        /// <summary>
        ///   Restores the property state recorded by <see cref="SetProperties_internal"/> when the
        ///   batch is rolled back (feature transaction-atomicity). Replays the recorded inverses in
        ///   REVERSE apply order so a key set more than once in the batch is returned to its original
        ///   value/absence.
        /// </summary>
        internal void RestoreProperties_internal(List<Transaction.PropertyMutationUndo> undo)
        {
            if (undo == null)
            {
                return;
            }

            for (var i = undo.Count - 1; i >= 0; i--)
            {
                var entry = undo[i];
                var graphElement = GetGraphElementForMutation(entry.GraphElementId);
                graphElement?.RestoreProperty(entry.PropertyId, entry.HadValueBefore, entry.PriorValue);

                if (graphElement != null)
                {
                    // Keep bound vector indices in step with the restored embedding state
                    // (feature element-embeddings): the restored prior value re-projects, an
                    // absent prior purges.
                    ProjectEmbeddingPropertyWrite(graphElement, entry.PropertyId,
                        entry.HadValueBefore ? entry.PriorValue : null);
                }
            }
        }

        /// <summary>
        ///   Applies a batch of embedding writes atomically (feature element-embeddings). Validates
        ///   the WHOLE batch before mutating anything - structural validity and per-write bounds
        ///   (valid name, dimension within [1, <see cref="Index.Vector.VectorIndex.MaxDimension" />],
        ///   finite components) to a clean <see cref="TransactionFailureReason.InvalidInput" />.
        ///   Embedding writes have REPLACE semantics, so unlike
        ///   <see cref="SetProperties_internal" /> there is no conflict validation - the last write
        ///   for a (element, name) pair wins, intra-batch included. A missing (null) slot stays a
        ///   no-op, matching the property path. On the happy path each write is applied and its
        ///   inverse recorded into <paramref name="undo" /> (in apply order) for a
        ///   residual-throw rollback via <see cref="RestoreEmbeddings_internal" />.
        /// </summary>
        internal Boolean SetEmbeddings_internal(List<EmbeddingSetDefinition> definitions,
            List<Transaction.PropertyMutationUndo> undo, out Transaction.TransactionFailureReason reason)
        {
            reason = Transaction.TransactionFailureReason.None;

            if (definitions == null)
            {
                reason = Transaction.TransactionFailureReason.InvalidInput;
                return false;
            }

            if (definitions.Count == 0)
            {
                return true;
            }

            // 1. Validate the whole batch before mutating anything (atomicity).
            foreach (var aDefinition in definitions)
            {
                if (aDefinition == null || !AGraphElementModel.IsValidEmbeddingName(aDefinition.Name))
                {
                    reason = Transaction.TransactionFailureReason.InvalidInput;
                    return false;
                }

                var vector = aDefinition.Vector;
                if (vector == null)
                {
                    continue; // removal - always structurally valid
                }

                if (vector.Length < 1 || vector.Length > Index.Vector.VectorIndex.MaxDimension ||
                    Index.Vector.VectorIndex.HasNonFiniteComponent(vector))
                {
                    reason = Transaction.TransactionFailureReason.InvalidInput;
                    return false;
                }
            }

            // 2. Apply, recording the inverse of each write. The mutation primitive is
            //    RestoreProperty (remove + conditional set on the reserved key): it IS the
            //    "set to exactly this state" operation, so replace semantics never hit
            //    SetProperty's same-key conflict throw.
            foreach (var aDefinition in definitions)
            {
                var graphElement = GetGraphElementForMutation(aDefinition.GraphElementId);
                if (graphElement == null)
                {
                    continue; // no-op target (empty slot), matching the property path
                }

                var propertyId = Intern(AGraphElementModel.GetEmbeddingPropertyId(aDefinition.Name));
                var hadValueBefore = graphElement.TryGetProperty<Object>(out var priorValue, propertyId);
                undo.Add(new Transaction.PropertyMutationUndo(aDefinition.GraphElementId, propertyId, hadValueBefore, priorValue));

                graphElement.RestoreProperty(propertyId, aDefinition.Vector != null, aDefinition.Vector);

                // The model stamp replaces with every write (feature embedding-provider): a
                // provider write carries its stamp, a raw write clears a stale one, a removal
                // drops it - the stamp always reflects the LAST write. A write that neither
                // sets nor clears a stamp is a no-op here (no undo entry, no feed event).
                var stampId = Intern(AGraphElementModel.GetEmbeddingModelStampPropertyId(aDefinition.Name));
                var stampValue = aDefinition.Vector != null ? aDefinition.ModelStamp : null;
                var hadStampBefore = graphElement.TryGetProperty<Object>(out var priorStamp, stampId);
                if (stampValue != null || hadStampBefore)
                {
                    undo.Add(new Transaction.PropertyMutationUndo(aDefinition.GraphElementId, stampId, hadStampBefore, priorStamp));
                    graphElement.RestoreProperty(stampId, stampValue != null, stampValue);
                }
            }

            // 3. Project into bound vector indices AFTER every mutation applied: a mid-apply
            //    throw rolls back plain property state with no projections to compensate, and a
            //    projection fault is best-effort (logged) and never fails the commit.
            foreach (var aDefinition in definitions)
            {
                var graphElement = GetGraphElementForMutation(aDefinition.GraphElementId);
                if (graphElement != null)
                {
                    ProjectEmbeddingToBoundIndices(graphElement, aDefinition.Name, aDefinition.Vector);
                }
            }

            return true;
        }

        /// <summary>
        ///   Restores the embedding state recorded by <see cref="SetEmbeddings_internal" /> when the
        ///   batch is rolled back - the reserved keys are properties, so the property restore
        ///   applies verbatim (reverse apply order).
        /// </summary>
        internal void RestoreEmbeddings_internal(List<Transaction.PropertyMutationUndo> undo)
        {
            RestoreProperties_internal(undo);
        }

        /// <summary>
        ///   Removes a batch of graph elements atomically (feature transaction-atomicity). Every id is
        ///   range-checked BEFORE any removal, so an out-of-range id still throws
        ///   <see cref="ArgumentOutOfRangeException"/> (InternalError/500, per transaction-failure-reasons)
        ///   but leaves the batch atomic (nothing removed). Each id that genuinely transitions
        ///   live -> removed is recorded into <paramref name="removedIds"/> so that, if a later removal
        ///   throws (e.g. a poisoned/corrupt adjacency), <see cref="RestoreRemovedElements_private"/>
        ///   undoes the earlier removals of the same batch.
        /// </summary>
        internal void RemoveGraphElements_internal(List<Int32> graphElementIds, List<Int32> removedIds,
            out Transaction.TransactionFailureReason reason)
        {
            reason = Transaction.TransactionFailureReason.None;

            if (graphElementIds == null)
            {
                reason = Transaction.TransactionFailureReason.InvalidInput;
                return;
            }

            if (graphElementIds.Count == 0)
            {
                return;
            }

            // Range-check the whole batch up front (out-of-range throws BEFORE any removal, so the
            // batch is atomic - the historical throw contract is preserved, just moved earlier).
            var snap = _snapshot;
            foreach (var id in graphElementIds)
            {
                if (id < 0 || id >= snap.Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(graphElementIds));
                }
            }

            // Apply, tracking genuine live -> removed transitions. If a removal throws, it self-restores
            // that element and rethrows; the throw escapes to the worker, whose Rollback restores the
            // earlier tracked ids of this batch.
            foreach (var id in graphElementIds)
            {
                if (TryRemoveGraphElement_private(id))
                {
                    removedIds.Add(id);
                }
            }
        }

        /// <summary>
        ///   Restores a set of fully-removed elements when a remove batch is rolled back (feature
        ///   transaction-atomicity), then recomputes the counters. Restores in REVERSE removal order so
        ///   a vertex removed before one of its (cascaded) edges is restored after that edge.
        /// </summary>
        internal void RestoreRemovedElements_private(List<Int32> removedIds)
        {
            if (removedIds == null || removedIds.Count == 0)
            {
                return;
            }

            for (var i = removedIds.Count - 1; i >= 0; i--)
            {
                var element = GetGraphElementForMutation(removedIds[i]);
                if (element != null)
                {
                    RestoreRemovedElement_private(element);
                }
            }

            //the removals maintained counts incrementally; a full recompute after the restore keeps
            //them exactly correct without re-deriving the inverse of each cascade.
            RecalculateGraphElementCounter();
        }

        /// <summary>
        ///   Inverse of a COMPLETED soft-removal (feature transaction-atomicity): clears the removed
        ///   flag and re-attaches the element to the adjacency the removal detached it from. For a
        ///   vertex, the raw out/in adjacency snapshots are captured up front (both are immutable
        ///   copy-on-write references, so re-attaching a self-loop via the out pass does not leak it
        ///   into the in pass), then each edge is re-attached to the OTHER endpoint's adjacency it was
        ///   detached from - the exact inverse of the removal cascade, including self-loops without
        ///   duplication. For an edge, it is re-attached to both endpoints.
        /// </summary>
        private void RestoreRemovedElement_private(AGraphElementModel element)
        {
            element.MarkAsNotRemoved();

            if (element is VertexModel vertex)
            {
                // Capture BOTH adjacency snapshots before mutating either: the removal detached each
                // out-edge from its target's InEdges and each in-edge from its source's OutEdges, so
                // re-attaching those (and only those) exactly inverts it. A self-loop sits in this
                // vertex's OutEdges only after removal (the out pass detached it from InEdges), so it
                // is absent from the in-snapshot and is not re-attached twice.
                var outSnapshot = vertex.GetRawOutEdges();
                var inSnapshot = vertex.GetRawInEdges();

                if (outSnapshot != null)
                {
                    foreach (var group in outSnapshot)
                    {
                        foreach (var outEdge in group.Value)
                        {
                            outEdge.MarkAsNotRemoved();
                            outEdge.TargetVertex.AddIncomingEdge(group.Key, outEdge);
                        }
                    }
                }

                if (inSnapshot != null)
                {
                    foreach (var group in inSnapshot)
                    {
                        foreach (var inEdge in group.Value)
                        {
                            inEdge.MarkAsNotRemoved();
                            inEdge.SourceVertex.AddOutEdge(group.Key, inEdge);
                        }
                    }
                }
            }
            else if (element is EdgeModel edge)
            {
                edge.TargetVertex.AddIncomingEdge(edge.EdgePropertyId, edge);
                edge.SourceVertex.AddOutEdge(edge.EdgePropertyId, edge);
            }
        }

        /// <summary>
        /// Recalculates the count of the graph elements
        /// </summary>
        private void RecalculateGraphElementCounter()
        {
            EdgeCount = GetCountOf<EdgeModel>();
            VertexCount = GetCountOf<VertexModel>();
        }
    }
}
