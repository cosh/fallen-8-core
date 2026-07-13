// MIT License
//
// VertexModel.cs
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
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace NoSQL.GraphDB.Core.Model
{
    /// <summary>
    ///   Vertex model.
    /// </summary>
    public sealed class VertexModel : AGraphElementModel
    {
        #region Data

        /// <summary>
        ///   The outgoing edges as an <see cref="EdgeAdjacency" /> (grouped by edge-property-id, each
        ///   group a contiguous <see cref="EdgeModel" /> array), or <c>null</c> when the vertex has no
        ///   outgoing edges (finding: adjacency-flattening). This replaces the former
        ///   <see cref="System.Collections.Immutable.ImmutableDictionary{TKey,TValue}" /> of
        ///   <see cref="System.Collections.Immutable.ImmutableList{T}" />, whose HAMT container and
        ///   per-edge AVL tree nodes carried ~96 B/edge of tree overhead plus ~200-400 B/vertex of
        ///   dictionary overhead. <see cref="EdgeAdjacency" /> stores the common single-group vertex
        ///   INLINE (no dictionary at all), so even a degree-2 vertex is now a memory win, and falls
        ///   back to a <c>Dictionary&lt;string, EdgeModel[]&gt;</c> only for a genuinely multi-group
        ///   vertex.
        ///
        ///   Concurrency: mutation is copy-on-write and runs only on the single transaction-writer
        ///   thread - a brand-new, immutable <see cref="EdgeAdjacency" /> is built and published by ONE
        ///   <c>volatile</c> reference assignment; the live instance is never mutated in place. Lock-free
        ///   readers capture the field into a local once, so a reader always sees a fully built,
        ///   self-consistent adjacency (correct on weak-memory hardware too, via <c>volatile</c>) -
        ///   exactly the discipline the immutable dictionary provided and the property store
        ///   (memory-footprint M1) uses.
        /// </summary>
        private volatile EdgeAdjacency _outEdges;

        /// <summary>
        ///   The incoming edges. Same representation and concurrency discipline as
        ///   <see cref="_outEdges" />; <c>null</c> when the vertex has no incoming edges.
        /// </summary>
        private volatile EdgeAdjacency _inEdges;

        #endregion

        #region Constructor

        /// <summary>
        ///   Initializes a new instance of the <see cref="VertexModel" /> class.
        /// </summary>
        /// <param name='id'> Identifier. </param>
        /// <param name='creationDate'> Creation date. </param>
        /// <param name='label'> Label. </param>
        /// <param name='properties'> Properties. </param>
        public VertexModel(Int32 id, UInt32 creationDate, String label = null, Dictionary<String, Object> properties = null)
            : base(id, creationDate, label, properties)
        {
        }

        /// <summary>
        ///   Initializes a new instance of the VertexModel class. For internal usage only
        /// </summary>
        /// <param name='id'> Identifier. </param>
        /// <param name='creationDate'> Creation date. </param>
        /// <param name='modificationDate'> Modification date. </param>
        /// <param name='label'> Label. </param>
        /// <param name='properties'> Properties. </param>
        /// <param name='outEdges'> Out edges. </param>
        /// <param name='incEdges'> Inc edges. </param>
        internal VertexModel(Int32 id, UInt32 creationDate, UInt32 modificationDate, String label = null,
                             Dictionary<String, Object> properties = null, Dictionary<String, List<EdgeModel>> outEdges = null, Dictionary<String, List<EdgeModel>> incEdges = null)
            : base(id, creationDate, label, properties)
        {
            // FromListGroups maps null/empty -> null (empty adjacency holds no container), a single
            // group -> the inline shape, and >1 group -> the dictionary fallback.
            _outEdges = EdgeAdjacency.FromListGroups(outEdges);
            _inEdges = EdgeAdjacency.FromListGroups(incEdges);

            ModificationDate = modificationDate;
        }

        #endregion

        #region internal methods

        /// <summary>
        ///   Adds the out edge.
        /// </summary>
        /// <param name='edgePropertyId'> Edge property identifier. </param>
        /// <param name='outEdge'> Out edge. </param>
        internal void AddOutEdge(String edgePropertyId, EdgeModel outEdge)
        {
            var current = _outEdges;
            _outEdges = current == null
                ? EdgeAdjacency.SingleEdge(edgePropertyId, outEdge)
                : current.WithEdgeAppended(edgePropertyId, outEdge);
        }

        /// <summary>
        ///   Replaces the whole set of out edges from a <c>Dictionary&lt;string, List&lt;EdgeModel&gt;&gt;</c>.
        /// </summary>
        /// <param name='outEdges'> Out edges. </param>
        internal void SetOutEdges(Dictionary<String, List<EdgeModel>> outEdges)
        {
            _outEdges = EdgeAdjacency.FromListGroups(outEdges);
        }

        /// <summary>
        ///   Adds the incoming edge.
        /// </summary>
        /// <param name='edgePropertyId'> Edge property identifier. </param>
        /// <param name='incomingEdge'> Incoming edge. </param>
        internal void AddIncomingEdge(String edgePropertyId, EdgeModel incomingEdge)
        {
            var current = _inEdges;
            _inEdges = current == null
                ? EdgeAdjacency.SingleEdge(edgePropertyId, incomingEdge)
                : current.WithEdgeAppended(edgePropertyId, incomingEdge);
        }

        /// <summary>
        ///   Removes an incoming edge
        /// </summary>
        /// <param name="edgePropertyId"> Edge property identifier. </param>
        /// <param name="toBeRemovedEdge"> The to be removed edge </param>
        internal void RemoveIncomingEdge(String edgePropertyId, EdgeModel toBeRemovedEdge)
        {
            var current = _inEdges;
            if (current == null)
            {
                return;
            }

            var updated = current.WithEdgeRemovedFromGroup(edgePropertyId, toBeRemovedEdge.Id);
            if (!ReferenceEquals(updated, current))
            {
                _inEdges = updated;
            }
        }

        /// <summary>
        ///   Removes an incoming edge
        /// </summary>
        /// <param name="toBeRemovedEdge"> The to be removed edge </param>
        /// <returns> The edge property identifiers where the edge was deleted </returns>
        internal List<String> RemoveIncomingEdge(EdgeModel toBeRemovedEdge)
        {
            var result = new List<String>();

            var current = _inEdges;
            if (current == null)
            {
                return result;
            }

            // WithEdgeRemovedEverywhere builds the replacement instance and, if RemoveById faults on a
            // poisoned entry, that throw escapes BEFORE the volatile publish below - so the live
            // adjacency is left untouched, which is what the edge-removal rollback test asserts. It
            // returns the SAME reference (skipping the publish) when nothing matched.
            var updated = current.WithEdgeRemovedEverywhere(toBeRemovedEdge, result);
            if (!ReferenceEquals(updated, current))
            {
                _inEdges = updated;
            }

            return result;
        }

        /// <summary>
        ///   Remove outgoing edge
        /// </summary>
        /// <param name="edgePropertyId"> The edge property identifier. </param>
        /// <param name="toBeRemovedEdge"> The to be removed edge </param>
        internal void RemoveOutGoingEdge(String edgePropertyId, EdgeModel toBeRemovedEdge)
        {
            var current = _outEdges;
            if (current == null)
            {
                return;
            }

            var updated = current.WithEdgeRemovedFromGroup(edgePropertyId, toBeRemovedEdge.Id);
            if (!ReferenceEquals(updated, current))
            {
                _outEdges = updated;
            }
        }

        /// <summary>
        ///   Removes an outgoing edge
        /// </summary>
        /// <param name="toBeRemovedEdge"> The to be removed edge </param>
        /// <returns> The edge property identifiers where the edge was deleted </returns>
        internal List<String> RemoveOutGoingEdge(EdgeModel toBeRemovedEdge)
        {
            var result = new List<String>();

            var current = _outEdges;
            if (current == null)
            {
                return result;
            }

            var updated = current.WithEdgeRemovedEverywhere(toBeRemovedEdge, result);
            if (!ReferenceEquals(updated, current))
            {
                _outEdges = updated;
            }

            return result;
        }

        /// <summary>
        ///   Serialization/algorithm-only accessor to the raw outgoing <see cref="EdgeAdjacency" />, or
        ///   <c>null</c> when the vertex has no outgoing edges. Returns the live copy-on-write snapshot
        ///   (a single volatile read); it is safe to iterate because the instance is immutable after
        ///   construction, but callers MUST NOT mutate the arrays it hands out. Kept internal so the
        ///   public surface never exposes the mutable <c>EdgeModel[]</c>; the in-engine hot paths
        ///   (path/subgraph algorithms, removal cascade, persistence) use it to <c>foreach</c> the
        ///   groups allocation-free (via <see cref="EdgeAdjacency.GetEnumerator" />) and avoid the
        ///   per-read wrapper the public read-only view incurs.
        /// </summary>
        internal EdgeAdjacency GetRawOutEdges()
        {
            return _outEdges;
        }

        /// <summary>
        ///   Raw incoming-edge counterpart of <see cref="GetRawOutEdges" />; <c>null</c> when empty.
        ///   Same "safe to read, must not mutate" contract.
        /// </summary>
        internal EdgeAdjacency GetRawInEdges()
        {
            return _inEdges;
        }

        /// <summary>
        ///   TEST-ONLY fault-injection hook. Appends a raw, unvalidated (possibly <c>null</c> /
        ///   poisoned) edge to the outgoing group <paramref name="edgePropertyId" /> using the same
        ///   copy-on-write append as the production path, bypassing the read-only public surface. It
        ///   replaces the former <c>source.OutEdges = source.OutEdges.SetItem(id,
        ///   source.OutEdges[id].Add(poison))</c> injection the removal-rollback regression tests use
        ///   to force a mid-removal fault. NOT a supported API; invoked by tests via reflection (the
        ///   engine declares no InternalsVisibleTo).
        /// </summary>
        internal void InjectRawOutEdgeForTesting(String edgePropertyId, EdgeModel poison)
        {
            var current = _outEdges;
            _outEdges = current == null
                ? EdgeAdjacency.SingleEdge(edgePropertyId, poison)
                : current.WithEdgeAppended(edgePropertyId, poison);
        }

        /// <summary>
        ///   TEST-ONLY fault-injection hook; incoming-edge counterpart of
        ///   <see cref="InjectRawOutEdgeForTesting" />. Replaces the former
        ///   <c>v.InEdges = v.InEdges.SetItem(id, v.InEdges[id].Add(poison))</c> injection.
        /// </summary>
        internal void InjectRawInEdgeForTesting(String edgePropertyId, EdgeModel poison)
        {
            var current = _inEdges;
            _inEdges = current == null
                ? EdgeAdjacency.SingleEdge(edgePropertyId, poison)
                : current.WithEdgeAppended(edgePropertyId, poison);
        }

        #endregion

        #region IVertexModel implementation

        public uint GetInDegree()
        {
            var snapshot = _inEdges;
            return snapshot == null ? 0u : snapshot.TotalDegree();
        }

        public uint GetOutDegree()
        {
            var snapshot = _outEdges;
            return snapshot == null ? 0u : snapshot.TotalDegree();
        }

        /// <summary>
        ///   Gets all neighbors.
        /// </summary>
        /// <returns> The neighbors. </returns>
        public List<VertexModel> GetAllNeighbors()
        {
            var neighbors = new List<VertexModel>();

            // Behaviour preserved verbatim from the prior representation: BOTH directions are projected
            // through the edge's target vertex (the former code applied the same target extractor to the
            // out- and in-edge lists). Kept identical so neighbour semantics do not change under the
            // storage swap.
            var outSnapshot = _outEdges;
            if (outSnapshot != null)
            {
                foreach (var group in outSnapshot)
                {
                    var edges = group.Value;
                    for (var i = 0; i < edges.Length; i++)
                    {
                        neighbors.Add(edges[i].TargetVertex);
                    }
                }
            }

            var inSnapshot = _inEdges;
            if (inSnapshot != null)
            {
                foreach (var group in inSnapshot)
                {
                    var edges = group.Value;
                    for (var i = 0; i < edges.Length; i++)
                    {
                        neighbors.Add(edges[i].TargetVertex);
                    }
                }
            }

            return neighbors;
        }

        /// <summary>
        ///   Gets the incoming edge identifiers.
        /// </summary>
        /// <returns> The incoming edge identifiers. </returns>
        public List<String> GetIncomingEdgeIds()
        {
            var inEdges = new List<String>();

            var snapshot = _inEdges;
            snapshot?.CollectKeys(inEdges);
            return inEdges;
        }

        /// <summary>
        ///   Gets the outgoing edge identifiers.
        /// </summary>
        /// <returns> The outgoing edge identifiers. </returns>
        public List<String> GetOutgoingEdgeIds()
        {
            var outEdges = new List<String>();

            var snapshot = _outEdges;
            snapshot?.CollectKeys(outEdges);
            return outEdges;
        }

        /// <summary>
        ///   A read-only, snapshot-stable view of the outgoing edges grouped by edge-property-id, or
        ///   <c>null</c> when the vertex has no outgoing edges. The view captures the current
        ///   copy-on-write <see cref="EdgeAdjacency" /> once, so iterating it is consistent even if the
        ///   writer publishes a new adjacency afterwards, and it transparently presents the single-group
        ///   (inline) case as a 1-entry dictionary. It exposes <see cref="IReadOnlyList{T}" /> groups
        ///   and never the underlying mutable <c>EdgeModel[]</c>.
        /// </summary>
        public IReadOnlyDictionary<String, IReadOnlyList<EdgeModel>> OutEdges
        {
            get
            {
                var snapshot = _outEdges;
                return snapshot == null ? null : new ReadOnlyEdgeContainer(snapshot);
            }
        }

        /// <summary>
        ///   A read-only, snapshot-stable view of the incoming edges; counterpart of
        ///   <see cref="OutEdges" />. <c>null</c> when the vertex has no incoming edges.
        /// </summary>
        public IReadOnlyDictionary<String, IReadOnlyList<EdgeModel>> InEdges
        {
            get
            {
                var snapshot = _inEdges;
                return snapshot == null ? null : new ReadOnlyEdgeContainer(snapshot);
            }
        }

        /// <summary>
        ///   Tries to get an out edge group.
        /// </summary>
        /// <returns> <c>true</c> if something was found; otherwise, <c>false</c> . </returns>
        /// <param name='result'> Result: a read-only list of the edges in the group (never the mutable array). </param>
        /// <param name='edgePropertyId'> Edge property identifier. </param>
        public Boolean TryGetOutEdge(out IReadOnlyList<EdgeModel> result, String edgePropertyId)
        {
            var snapshot = _outEdges;
            if (snapshot != null && snapshot.TryGetGroup(edgePropertyId, out var group))
            {
                result = Array.AsReadOnly(group);
                return true;
            }

            result = null;
            return false;
        }

        /// <summary>
        ///   Tries to get an in edge group.
        /// </summary>
        /// <returns> <c>true</c> if something was found; otherwise, <c>false</c> . </returns>
        /// <param name='result'> Result: a read-only list of the edges in the group (never the mutable array). </param>
        /// <param name='edgePropertyId'> Edge property identifier. </param>
        public Boolean TryGetInEdge(out IReadOnlyList<EdgeModel> result, String edgePropertyId)
        {
            var snapshot = _inEdges;
            if (snapshot != null && snapshot.TryGetGroup(edgePropertyId, out var group))
            {
                result = Array.AsReadOnly(group);
                return true;
            }

            result = null;
            return false;
        }

        #endregion

        #region AGraphElement

        /// <summary>
        ///   The overide of the trim method
        /// </summary>
        internal override void Trim()
        {
            //NOP
        }

        #endregion

        #region Equals Overrides

        public override Boolean Equals(Object obj)
        {
            // If parameter is null return false.
            if (obj == null)
            {
                return false;
            }

            // If parameter cannot be cast to PathElement return false.
            var p = obj as VertexModel;

            return p != null && Equals(p);
        }

        public Boolean Equals(VertexModel p)
        {
            // If parameter is null return false:
            return (object)p != null && ReferenceEquals(this, p);
        }

        public static Boolean operator ==(VertexModel a, VertexModel b)
        {
            // If both are null, or both are same instance, return true.
            if (ReferenceEquals(a, b))
            {
                return true;
            }

            // If one is null, but not both, return false.
            if (((object)a == null) || ((object)b == null))
            {
                return false;
            }

            // Return true if the fields match:
            return a.Equals(b);
        }

        public static Boolean operator !=(VertexModel a, VertexModel b)
        {
            return !(a == b);
        }

        public override int GetHashCode()
        {
            return Id;
        }

        #endregion

        #region misc overrides

        public override string ToString()
        {
            return Id.ToString(CultureInfo.InvariantCulture);
        }

        #endregion

        #region read-only adjacency view

        /// <summary>
        ///   A read-only projection of a captured <see cref="EdgeAdjacency" /> onto
        ///   <c>IReadOnlyDictionary&lt;string, IReadOnlyList&lt;EdgeModel&gt;&gt;</c>. Holds the captured
        ///   snapshot, so the view is stable and self-consistent for its lifetime; each group is
        ///   surfaced as a read-only list so the backing array is never exposed for mutation. Both the
        ///   inline single-group and the dictionary-backed multi-group shapes are presented uniformly as
        ///   a keyed dictionary.
        /// </summary>
        private sealed class ReadOnlyEdgeContainer : IReadOnlyDictionary<String, IReadOnlyList<EdgeModel>>
        {
            private readonly EdgeAdjacency _adjacency;

            internal ReadOnlyEdgeContainer(EdgeAdjacency adjacency)
            {
                _adjacency = adjacency;
            }

            public IReadOnlyList<EdgeModel> this[String key]
            {
                get
                {
                    if (_adjacency.TryGetGroup(key, out var group))
                    {
                        return Array.AsReadOnly(group);
                    }
                    throw new KeyNotFoundException();
                }
            }

            public IEnumerable<String> Keys
            {
                get
                {
                    foreach (var group in _adjacency)
                    {
                        yield return group.Key;
                    }
                }
            }

            public IEnumerable<IReadOnlyList<EdgeModel>> Values
            {
                get
                {
                    foreach (var group in _adjacency)
                    {
                        yield return Array.AsReadOnly(group.Value);
                    }
                }
            }

            public int Count => _adjacency.Count;

            public Boolean ContainsKey(String key) => _adjacency.TryGetGroup(key, out _);

            public Boolean TryGetValue(String key, out IReadOnlyList<EdgeModel> value)
            {
                if (_adjacency.TryGetGroup(key, out var group))
                {
                    value = Array.AsReadOnly(group);
                    return true;
                }

                value = null;
                return false;
            }

            public IEnumerator<KeyValuePair<String, IReadOnlyList<EdgeModel>>> GetEnumerator()
            {
                foreach (var group in _adjacency)
                {
                    yield return new KeyValuePair<String, IReadOnlyList<EdgeModel>>(group.Key, Array.AsReadOnly(group.Value));
                }
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }

        #endregion
    }
}
