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
using System.Linq;

namespace NoSQL.GraphDB.Core.Model
{
    /// <summary>
    ///   Vertex model.
    /// </summary>
    public sealed class VertexModel : AGraphElementModel
    {
        #region Data

        /// <summary>
        ///   The outgoing edges, grouped by edge-property-id and stored as a contiguous
        ///   <see cref="EdgeModel" /> array per group, or <c>null</c> when the vertex has no
        ///   outgoing edges (finding: adjacency-flattening). This replaces the former
        ///   <see cref="System.Collections.Immutable.ImmutableDictionary{TKey,TValue}" /> of
        ///   <see cref="System.Collections.Immutable.ImmutableList{T}" />, whose HAMT container and
        ///   per-edge AVL tree nodes carried ~96 B/edge of tree overhead plus ~200-400 B/vertex of
        ///   dictionary overhead; a per-group array is one object plus ~8 B per edge slot.
        ///
        ///   Concurrency: mutation is copy-on-write and runs only on the single transaction-writer
        ///   thread - a brand-new group map (and, for the changed group, a brand-new array) is built
        ///   and published by ONE <c>volatile</c> reference assignment; neither the live map nor any
        ///   published array is ever mutated in place. Lock-free readers capture the field into a
        ///   local once, so a reader always sees a fully built, self-consistent adjacency (correct on
        ///   weak-memory hardware too, via <c>volatile</c>) - exactly the discipline the immutable
        ///   dictionary provided and the property store (memory-footprint M1) uses.
        /// </summary>
        private volatile Dictionary<String, EdgeModel[]> _outEdges;

        /// <summary>
        ///   The incoming edges. Same representation and concurrency discipline as
        ///   <see cref="_outEdges" />; <c>null</c> when the vertex has no incoming edges.
        /// </summary>
        private volatile Dictionary<String, EdgeModel[]> _inEdges;

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
            if (outEdges != null)
            {
                _outEdges = BuildGroups(outEdges);
            }

            if (incEdges != null)
            {
                _inEdges = BuildGroups(incEdges);
            }

            ModificationDate = modificationDate;
        }

        #endregion

        #region copy-on-write helpers

        /// <summary>
        ///   Builds the array-backed group map from a <c>Dictionary&lt;string, List&lt;EdgeModel&gt;&gt;</c>
        ///   (the shape the persistency layer reconstructs adjacency in on load). Each group's list
        ///   is snapshotted into its own contiguous array.
        /// </summary>
        private static Dictionary<String, EdgeModel[]> BuildGroups(Dictionary<String, List<EdgeModel>> source)
        {
            var result = new Dictionary<String, EdgeModel[]>(source.Count);
            foreach (var kv in source)
            {
                result[kv.Key] = kv.Value.ToArray();
            }
            return result;
        }

        /// <summary>
        ///   Returns a NEW group map with <paramref name="edge" /> appended to the group
        ///   <paramref name="edgePropertyId" /> (creating the group, and the map, as needed).
        ///   Build-new-and-swap: the incoming map and every published array are treated as immutable
        ///   - only a fresh map and a fresh array for the one changed group are allocated - so the
        ///   caller can publish the result with a single volatile assignment without ever mutating a
        ///   structure a concurrent reader might be scanning.
        /// </summary>
        private static Dictionary<String, EdgeModel[]> WithEdgeAppended(
            Dictionary<String, EdgeModel[]> current, String edgePropertyId, EdgeModel edge)
        {
            if (current == null)
            {
                return new Dictionary<String, EdgeModel[]>(1) { { edgePropertyId, new[] { edge } } };
            }

            var next = new Dictionary<String, EdgeModel[]>(current);
            if (next.TryGetValue(edgePropertyId, out var existing))
            {
                var appended = new EdgeModel[existing.Length + 1];
                Array.Copy(existing, appended, existing.Length);
                appended[existing.Length] = edge;
                next[edgePropertyId] = appended;
            }
            else
            {
                next[edgePropertyId] = new[] { edge };
            }
            return next;
        }

        /// <summary>
        ///   Returns a NEW array with every entry whose <see cref="AGraphElementModel.Id" /> equals
        ///   <paramref name="id" /> removed, or the SAME array reference when nothing matches (so the
        ///   caller can skip republishing). Mirrors the former <c>ImmutableList.RemoveAll(_ =&gt; _.Id
        ///   == id)</c> - including that it dereferences each entry's <c>Id</c>, so a poisoned
        ///   <c>null</c> slot throws here (the exact fault the removal-rollback regression tests rely
        ///   on).
        /// </summary>
        private static EdgeModel[] RemoveById(EdgeModel[] source, Int32 id)
        {
            var survivors = 0;
            for (var i = 0; i < source.Length; i++)
            {
                if (source[i].Id != id)
                {
                    survivors++;
                }
            }

            if (survivors == source.Length)
            {
                return source;
            }

            var result = new EdgeModel[survivors];
            var idx = 0;
            for (var i = 0; i < source.Length; i++)
            {
                if (source[i].Id != id)
                {
                    result[idx++] = source[i];
                }
            }
            return result;
        }

        /// <summary>
        ///   Whether <paramref name="edge" /> is present in <paramref name="source" />. Mirrors
        ///   <c>ImmutableList.Contains</c> (default equality, which is reference equality for
        ///   <see cref="EdgeModel" />) and is null-slot-safe, so - like the original - the membership
        ///   probe never faults on a poisoned <c>null</c> entry (only <see cref="RemoveById" /> does).
        /// </summary>
        private static Boolean Contains(EdgeModel[] source, EdgeModel edge)
        {
            for (var i = 0; i < source.Length; i++)
            {
                if (ReferenceEquals(source[i], edge))
                {
                    return true;
                }
            }
            return false;
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
            _outEdges = WithEdgeAppended(_outEdges, edgePropertyId, outEdge);
        }

        /// <summary>
        ///   Replaces the whole set of out edges from a <c>Dictionary&lt;string, List&lt;EdgeModel&gt;&gt;</c>.
        /// </summary>
        /// <param name='outEdges'> Out edges. </param>
        internal void SetOutEdges(Dictionary<String, List<EdgeModel>> outEdges)
        {
            _outEdges = outEdges == null ? null : BuildGroups(outEdges);
        }

        /// <summary>
        ///   Adds the incoming edge.
        /// </summary>
        /// <param name='edgePropertyId'> Edge property identifier. </param>
        /// <param name='incomingEdge'> Incoming edge. </param>
        internal void AddIncomingEdge(String edgePropertyId, EdgeModel incomingEdge)
        {
            _inEdges = WithEdgeAppended(_inEdges, edgePropertyId, incomingEdge);
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

            if (!current.TryGetValue(edgePropertyId, out var existing))
            {
                return;
            }

            var filtered = RemoveById(existing, toBeRemovedEdge.Id);
            if (ReferenceEquals(filtered, existing))
            {
                return;
            }

            var next = new Dictionary<String, EdgeModel[]>(current);
            next[edgePropertyId] = filtered;
            _inEdges = next;
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

            // Build the replacement map lazily and publish it exactly once (a single volatile write).
            // If RemoveById faults on a poisoned entry the throw escapes BEFORE the publish, so the
            // live adjacency is left untouched - which is what the edge-removal rollback test asserts.
            Dictionary<String, EdgeModel[]> next = null;
            foreach (var group in current)
            {
                if (Contains(group.Value, toBeRemovedEdge))
                {
                    var filtered = RemoveById(group.Value, toBeRemovedEdge.Id);
                    next ??= new Dictionary<String, EdgeModel[]>(current);
                    next[group.Key] = filtered;
                    result.Add(group.Key);
                }
            }

            if (next != null)
            {
                _inEdges = next;
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

            if (!current.TryGetValue(edgePropertyId, out var existing))
            {
                return;
            }

            var filtered = RemoveById(existing, toBeRemovedEdge.Id);
            if (ReferenceEquals(filtered, existing))
            {
                return;
            }

            var next = new Dictionary<String, EdgeModel[]>(current);
            next[edgePropertyId] = filtered;
            _outEdges = next;
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

            Dictionary<String, EdgeModel[]> next = null;
            foreach (var group in current)
            {
                if (Contains(group.Value, toBeRemovedEdge))
                {
                    var filtered = RemoveById(group.Value, toBeRemovedEdge.Id);
                    next ??= new Dictionary<String, EdgeModel[]>(current);
                    next[group.Key] = filtered;
                    result.Add(group.Key);
                }
            }

            if (next != null)
            {
                _outEdges = next;
            }

            return result;
        }

        /// <summary>
        ///   Serialization/algorithm-only accessor to the raw outgoing-edge group map, or <c>null</c>
        ///   when the vertex has no outgoing edges. Returns the live copy-on-write snapshot (a single
        ///   volatile read); it is safe to iterate because the map and its arrays are never mutated in
        ///   place, but callers MUST NOT mutate the returned structure. Kept internal so the public
        ///   surface never hands out the mutable <c>Dictionary</c>/<c>EdgeModel[]</c>; the in-engine
        ///   hot paths (path/subgraph algorithms, removal cascade, persistence) use it to avoid the
        ///   per-read wrapper allocation the public read-only view incurs.
        /// </summary>
        internal Dictionary<String, EdgeModel[]> GetRawOutEdges()
        {
            return _outEdges;
        }

        /// <summary>
        ///   Raw incoming-edge counterpart of <see cref="GetRawOutEdges" />; <c>null</c> when empty.
        ///   Same "safe to read, must not mutate" contract.
        /// </summary>
        internal Dictionary<String, EdgeModel[]> GetRawInEdges()
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
            _outEdges = WithEdgeAppended(_outEdges, edgePropertyId, poison);
        }

        /// <summary>
        ///   TEST-ONLY fault-injection hook; incoming-edge counterpart of
        ///   <see cref="InjectRawOutEdgeForTesting" />. Replaces the former
        ///   <c>v.InEdges = v.InEdges.SetItem(id, v.InEdges[id].Add(poison))</c> injection.
        /// </summary>
        internal void InjectRawInEdgeForTesting(String edgePropertyId, EdgeModel poison)
        {
            _inEdges = WithEdgeAppended(_inEdges, edgePropertyId, poison);
        }

        #endregion

        #region IVertexModel implementation

        public uint GetInDegree()
        {
            var snapshot = _inEdges;
            if (snapshot == null)
            {
                return 0;
            }

            UInt32 degree = 0;
            foreach (var group in snapshot.Values)
            {
                degree += (UInt32)group.Length;
            }
            return degree;
        }

        public uint GetOutDegree()
        {
            var snapshot = _outEdges;
            if (snapshot == null)
            {
                return 0;
            }

            UInt32 degree = 0;
            foreach (var group in snapshot.Values)
            {
                degree += (UInt32)group.Length;
            }
            return degree;
        }

        /// <summary>
        ///   Gets all neighbors.
        /// </summary>
        /// <returns> The neighbors. </returns>
        public List<VertexModel> GetAllNeighbors()
        {
            var neighbors = new List<VertexModel>();

            var outSnapshot = _outEdges;
            if (outSnapshot != null)
            {
                neighbors.AddRange(outSnapshot.SelectMany(_ => _.Value).Select(TargetVertexExtractor));
            }

            var inSnapshot = _inEdges;
            if (inSnapshot != null)
            {
                neighbors.AddRange(inSnapshot.SelectMany(_ => _.Value).Select(TargetVertexExtractor));
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
            if (snapshot != null)
            {
                inEdges.AddRange(snapshot.Keys);
            }
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
            if (snapshot != null)
            {
                outEdges.AddRange(snapshot.Keys);
            }
            return outEdges;
        }

        /// <summary>
        ///   A read-only, snapshot-stable view of the outgoing edges grouped by edge-property-id, or
        ///   <c>null</c> when the vertex has no outgoing edges. The view captures the current
        ///   copy-on-write group map once, so iterating it is consistent even if the writer publishes
        ///   a new adjacency afterwards. It exposes <see cref="IReadOnlyList{T}" /> groups and never
        ///   the underlying mutable <c>EdgeModel[]</c>.
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
            if (snapshot != null && snapshot.TryGetValue(edgePropertyId, out var group))
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
            if (snapshot != null && snapshot.TryGetValue(edgePropertyId, out var group))
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

        #region private helper

        /// <summary>
        /// Target vertex extractor.
        /// </summary>
        /// <returns>
        /// The target vertex.
        /// </returns>
        /// <param name='edge'>
        /// Edge.
        /// </param>
        private static VertexModel TargetVertexExtractor(EdgeModel edge)
        {
            return edge.TargetVertex;
        }

        /// <summary>
        /// Source vertex extractor.
        /// </summary>
        /// <returns>
        /// The source vertex.
        /// </returns>
        /// <param name='edge'>
        /// Edge.
        /// </param>
        private static VertexModel SourceVertexExtractor(EdgeModel edge)
        {
            return edge.SourceVertex;
        }

        #endregion

        #region read-only adjacency view

        /// <summary>
        ///   A read-only projection of a copy-on-write group map (<c>Dictionary&lt;string,
        ///   EdgeModel[]&gt;</c>) onto <c>IReadOnlyDictionary&lt;string,
        ///   IReadOnlyList&lt;EdgeModel&gt;&gt;</c>. Holds the captured snapshot, so the view is stable
        ///   and self-consistent for its lifetime; each group is surfaced as a read-only list so the
        ///   backing array is never exposed for mutation.
        /// </summary>
        private sealed class ReadOnlyEdgeContainer : IReadOnlyDictionary<String, IReadOnlyList<EdgeModel>>
        {
            private readonly Dictionary<String, EdgeModel[]> _groups;

            internal ReadOnlyEdgeContainer(Dictionary<String, EdgeModel[]> groups)
            {
                _groups = groups;
            }

            public IReadOnlyList<EdgeModel> this[String key] => Array.AsReadOnly(_groups[key]);

            public IEnumerable<String> Keys => _groups.Keys;

            public IEnumerable<IReadOnlyList<EdgeModel>> Values
            {
                get
                {
                    foreach (var group in _groups.Values)
                    {
                        yield return Array.AsReadOnly(group);
                    }
                }
            }

            public int Count => _groups.Count;

            public Boolean ContainsKey(String key) => _groups.ContainsKey(key);

            public Boolean TryGetValue(String key, out IReadOnlyList<EdgeModel> value)
            {
                if (_groups.TryGetValue(key, out var group))
                {
                    value = Array.AsReadOnly(group);
                    return true;
                }

                value = null;
                return false;
            }

            public IEnumerator<KeyValuePair<String, IReadOnlyList<EdgeModel>>> GetEnumerator()
            {
                foreach (var group in _groups)
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
