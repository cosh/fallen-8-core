// MIT License
//
// EdgeAdjacency.cs
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

namespace NoSQL.GraphDB.Core.Model
{
    /// <summary>
    ///   The out- or in-adjacency of a single <see cref="VertexModel" />, grouped by edge-property-id
    ///   and stored as a contiguous <see cref="EdgeModel" /> array per group. Immutable after
    ///   construction: every field is <c>readonly</c> and no method ever mutates <c>this</c> - the
    ///   mutators (<see cref="WithEdgeAppended" />, <see cref="WithEdgesAppended" />,
    ///   <see cref="WithEdgeRemovedFromGroup" />, <see cref="WithEdgeRemovedEverywhere" />) return a
    ///   brand-new instance which the owning <see cref="VertexModel" /> publishes by ONE
    ///   <c>volatile</c> reference assignment. This is the whole-reference copy-on-write swap the
    ///   lock-free-reader / single-writer contract requires: a reader captures the <c>volatile</c> field
    ///   into a local once and then sees a fully built, self-consistent adjacency (correct on
    ///   weak-memory hardware, because the acquiring volatile read pairs with the releasing volatile
    ///   write that published this instance).
    ///
    ///   <para>
    ///   Layout (finding: adjacency-flattening, INLINE-SMALL-MAP). The overwhelmingly common vertex
    ///   has all of its edges under a SINGLE edge-property-id (one group per direction). That case is
    ///   stored INLINE - just the group key <see cref="_soleKey" /> plus its array
    ///   <see cref="_soleArray" /> and logical count <see cref="_soleCount" /> - carrying NO
    ///   <see cref="Dictionary{TKey,TValue}" /> at all, so it avoids the dictionary's fixed
    ///   buckets+entries overhead. Only a genuinely multi-group vertex allocates the fallback
    ///   <see cref="_map" />. Exactly one shape is populated: single-group =&gt; <c>_map == null</c>,
    ///   <c>_soleArray != null</c>; multi-group =&gt; <c>_map != null</c>.
    ///   </para>
    ///
    ///   <para>
    ///   Amortised capacity (feature supernode-adjacency-build). Each group has an explicit logical
    ///   <b>count</b> distinct from its backing array's length: <c>array.Length &gt;= count</c>, with
    ///   the tail <c>[count, array.Length)</c> being spare, reader-invisible slots. An append usually
    ///   writes a spare slot in place and publishes <c>count + 1</c> sharing the SAME array (×2 growth
    ///   only when full), so building a degree-d group is O(d) with O(log d) reallocations, not O(d²)
    ///   whole-array copies. This is exactly the master store's <c>AppendGraphElement</c> discipline:
    ///   write the spare slot (index &gt;= every published count) FIRST, publish the incremented count
    ///   LAST via the owning vertex's releasing volatile store; a reader holding an older instance sees
    ///   the smaller count and never touches the new slot, and a reader acquiring the new instance is
    ///   guaranteed to see the fully written slot. Single-writer guarantees the slot written is always
    ///   at an index no published count has reached, so no torn/null read is possible. Every read/derived
    ///   path slices <c>[0, count)</c> and never exposes a spare slot (the enumerator hands out an
    ///   <see cref="ArraySegment{T}" /> bounded to the count, never the raw array).
    ///   </para>
    /// </summary>
    internal sealed class EdgeAdjacency
    {
        /// <summary>A single edge-property-id group: its backing array plus the logical edge count
        /// (<c>Array.Length &gt;= Count</c>; the tail is spare capacity). Immutable value.</summary>
        private readonly struct EdgeGroup
        {
            internal readonly EdgeModel[] Array;
            internal readonly Int32 Count;

            internal EdgeGroup(EdgeModel[] array, Int32 count)
            {
                Array = array;
                Count = count;
            }
        }

        /// <summary>The sole group's edge-property-id in the single-group (inline) shape; <c>null</c> in the multi-group shape.</summary>
        private readonly String _soleKey;

        /// <summary>The sole group's backing edge array in the single-group (inline) shape; <c>null</c> in the multi-group shape. May carry spare capacity beyond <see cref="_soleCount" />.</summary>
        private readonly EdgeModel[] _soleArray;

        /// <summary>The sole group's logical edge count (the vertex's degree in this direction) in the inline shape.</summary>
        private readonly Int32 _soleCount;

        /// <summary>The group map in the multi-group (fallback) shape; <c>null</c> in the single-group shape. Never mutated after construction.</summary>
        private readonly Dictionary<String, EdgeGroup> _map;

        /// <summary>Constructs the single-group (inline) shape. The key mirrors a
        /// <c>Dictionary&lt;string, ...&gt;</c> key, so it is never <c>null</c>.</summary>
        private EdgeAdjacency(String soleKey, EdgeModel[] soleArray, Int32 soleCount)
        {
            _soleKey = soleKey;
            _soleArray = soleArray;
            _soleCount = soleCount;
        }

        /// <summary>Constructs the multi-group (fallback) shape around an already-built, never-again-mutated map.</summary>
        private EdgeAdjacency(Dictionary<String, EdgeGroup> map)
        {
            _map = map;
        }

        /// <summary>
        ///   Builds the initial adjacency of a vertex with a single edge in one group (the first edge
        ///   ever added to a direction). Inline by construction - no dictionary.
        /// </summary>
        internal static EdgeAdjacency SingleEdge(String edgePropertyId, EdgeModel edge)
        {
            return new EdgeAdjacency(edgePropertyId, new[] { edge }, 1);
        }

        /// <summary>
        ///   Builds an adjacency from the <c>Dictionary&lt;string, List&lt;EdgeModel&gt;&gt;</c> shape the
        ///   persistency layer reconstructs on load (and the internal ctor takes). Each group is snapshot
        ///   into its own right-sized contiguous array (<c>count == length</c>, no spare). A single-entry
        ///   source becomes the inline shape; a multi-entry source a fresh map. A <c>null</c>/empty source
        ///   has no adjacency and returns <c>null</c> (empty =&gt; <c>null</c>, matching the property-store
        ///   convention).
        /// </summary>
        internal static EdgeAdjacency FromListGroups(Dictionary<String, List<EdgeModel>> source)
        {
            if (source == null || source.Count == 0)
            {
                return null;
            }

            if (source.Count == 1)
            {
                foreach (var kv in source)
                {
                    var array = kv.Value.ToArray();
                    return new EdgeAdjacency(kv.Key, array, array.Length);
                }
            }

            var map = new Dictionary<String, EdgeGroup>(source.Count);
            foreach (var kv in source)
            {
                var array = kv.Value.ToArray();
                map[kv.Key] = new EdgeGroup(array, array.Length);
            }
            return new EdgeAdjacency(map);
        }

        /// <summary>The number of edge-property-id groups (1 in the inline shape).</summary>
        internal Int32 Count => _map?.Count ?? 1;

        /// <summary>The total number of edges across all groups (the vertex's degree in this direction).</summary>
        internal UInt32 TotalDegree()
        {
            if (_map == null)
            {
                return (UInt32)_soleCount;
            }

            UInt32 degree = 0;
            foreach (var group in _map.Values)
            {
                degree += (UInt32)group.Count;
            }
            return degree;
        }

        /// <summary>
        ///   Looks up a group by edge-property-id and returns it as a count-bounded
        ///   <see cref="ArraySegment{T}" /> (never the raw backing array, so spare slots are never
        ///   exposed). Uses ordinal string equality in the inline shape, matching the default
        ///   <c>Dictionary&lt;string, ...&gt;</c> comparer the multi-group shape uses, so the two shapes
        ///   are indistinguishable to callers.
        /// </summary>
        internal Boolean TryGetGroup(String key, out ArraySegment<EdgeModel> group)
        {
            if (_map != null)
            {
                if (_map.TryGetValue(key, out var mapped))
                {
                    group = new ArraySegment<EdgeModel>(mapped.Array, 0, mapped.Count);
                    return true;
                }

                group = default;
                return false;
            }

            if (String.Equals(_soleKey, key, StringComparison.Ordinal))
            {
                group = new ArraySegment<EdgeModel>(_soleArray, 0, _soleCount);
                return true;
            }

            group = default;
            return false;
        }

        /// <summary>Appends this adjacency's group keys to <paramref name="into" /> (backs <c>GetIncoming/OutgoingEdgeIds</c>).</summary>
        internal void CollectKeys(List<String> into)
        {
            if (_map == null)
            {
                into.Add(_soleKey);
            }
            else
            {
                into.AddRange(_map.Keys);
            }
        }

        /// <summary>
        ///   Returns a NEW adjacency with <paramref name="edge" /> appended to group
        ///   <paramref name="edgePropertyId" /> (creating the group as needed). Amortised O(1): the edge
        ///   is written into a spare slot of the SAME backing array when capacity allows (publishing
        ///   <c>count + 1</c>), otherwise the group array is grown ×2. The inline shape is kept as long as
        ///   there is one group and only promotes to a map when a genuinely new group is added.
        /// </summary>
        internal EdgeAdjacency WithEdgeAppended(String edgePropertyId, EdgeModel edge)
        {
            if (_map == null)
            {
                if (String.Equals(_soleKey, edgePropertyId, StringComparison.Ordinal))
                {
                    // Append within the sole group - stays inline.
                    AppendWithCapacity(_soleArray, _soleCount, edge, out var array, out var count);
                    return new EdgeAdjacency(_soleKey, array, count);
                }

                // A second, distinct group appears - promote to the map shape (sole group carried as-is).
                var promoted = new Dictionary<String, EdgeGroup>(2)
                {
                    { _soleKey, new EdgeGroup(_soleArray, _soleCount) },
                    { edgePropertyId, new EdgeGroup(new[] { edge }, 1) }
                };
                return new EdgeAdjacency(promoted);
            }

            var next = new Dictionary<String, EdgeGroup>(_map);
            if (next.TryGetValue(edgePropertyId, out var existing))
            {
                AppendWithCapacity(existing.Array, existing.Count, edge, out var array, out var count);
                next[edgePropertyId] = new EdgeGroup(array, count);
            }
            else
            {
                next[edgePropertyId] = new EdgeGroup(new[] { edge }, 1);
            }
            return new EdgeAdjacency(next);
        }

        /// <summary>
        ///   Returns a NEW adjacency with all of <paramref name="edges" /> appended to group
        ///   <paramref name="edgePropertyId" /> in one shot (feature supernode-adjacency-build Step 1):
        ///   k edges to one vertex/direction/group cost ONE array rebuild (or one spare-capacity fill)
        ///   and one published instance instead of k. Append order (encounter order) is preserved. The
        ///   inline shape is kept for a single group; a genuinely new key promotes to the map. Returns
        ///   <c>this</c> when <paramref name="edges" /> is empty.
        /// </summary>
        internal EdgeAdjacency WithEdgesAppended(String edgePropertyId, IReadOnlyList<EdgeModel> edges)
        {
            if (edges == null || edges.Count == 0)
            {
                return this;
            }

            if (_map == null)
            {
                if (String.Equals(_soleKey, edgePropertyId, StringComparison.Ordinal))
                {
                    AppendManyWithCapacity(_soleArray, _soleCount, edges, out var array, out var count);
                    return new EdgeAdjacency(_soleKey, array, count);
                }

                var promoted = new Dictionary<String, EdgeGroup>(2)
                {
                    { _soleKey, new EdgeGroup(_soleArray, _soleCount) },
                    { edgePropertyId, BuildGroup(edges) }
                };
                return new EdgeAdjacency(promoted);
            }

            var next = new Dictionary<String, EdgeGroup>(_map);
            if (next.TryGetValue(edgePropertyId, out var existing))
            {
                AppendManyWithCapacity(existing.Array, existing.Count, edges, out var array, out var count);
                next[edgePropertyId] = new EdgeGroup(array, count);
            }
            else
            {
                next[edgePropertyId] = BuildGroup(edges);
            }
            return new EdgeAdjacency(next);
        }

        /// <summary>
        ///   Returns a NEW adjacency with every edge whose <see cref="AGraphElementModel.Id" /> equals
        ///   <paramref name="id" /> removed from the single group <paramref name="edgePropertyId" />, or
        ///   <c>this</c> (the SAME reference) when the group is absent or nothing matched - so the caller
        ///   can skip republishing. Scans only the logical <c>[0, count)</c> (never spare slots) and
        ///   dereferences each entry's <c>Id</c>, so a poisoned <c>null</c> slot within the count throws
        ///   HERE - before any new instance is returned and thus before the caller publishes - leaving the
        ///   live adjacency untouched (the removal-rollback regression tests rely on this).
        /// </summary>
        internal EdgeAdjacency WithEdgeRemovedFromGroup(String edgePropertyId, Int32 id)
        {
            if (_map == null)
            {
                if (!String.Equals(_soleKey, edgePropertyId, StringComparison.Ordinal))
                {
                    return this;
                }

                if (!RemoveById(_soleArray, _soleCount, id, out var filtered))
                {
                    return this;
                }
                return new EdgeAdjacency(_soleKey, filtered, filtered.Length);
            }

            if (!_map.TryGetValue(edgePropertyId, out var existing))
            {
                return this;
            }

            if (!RemoveById(existing.Array, existing.Count, id, out var filteredGroup))
            {
                return this;
            }

            var next = new Dictionary<String, EdgeGroup>(_map);
            next[edgePropertyId] = new EdgeGroup(filteredGroup, filteredGroup.Length);
            return new EdgeAdjacency(next);
        }

        /// <summary>
        ///   Returns a NEW adjacency with <paramref name="edge" /> removed from EVERY group that
        ///   contains it (by reference), recording each affected edge-property-id in
        ///   <paramref name="affectedKeys" /> so the removal cascade can replay them on rollback; or
        ///   <c>this</c> when the edge is present in no group. The membership probe is null-slot-safe,
        ///   but the actual removal dereferences each entry's <c>Id</c> over <c>[0, count)</c>, so a
        ///   poisoned <c>null</c> slot faults here, before any new instance is returned (and thus before
        ///   the caller publishes) and before the affected key is recorded, so the live adjacency and the
        ///   returned key list stay consistent.
        /// </summary>
        internal EdgeAdjacency WithEdgeRemovedEverywhere(EdgeModel edge, List<String> affectedKeys)
        {
            if (_map == null)
            {
                if (!Contains(_soleArray, _soleCount, edge))
                {
                    return this;
                }

                RemoveById(_soleArray, _soleCount, edge.Id, out var filtered);
                affectedKeys.Add(_soleKey);
                return new EdgeAdjacency(_soleKey, filtered, filtered.Length);
            }

            Dictionary<String, EdgeGroup> next = null;
            foreach (var group in _map)
            {
                if (Contains(group.Value.Array, group.Value.Count, edge))
                {
                    RemoveById(group.Value.Array, group.Value.Count, edge.Id, out var filtered);
                    next ??= new Dictionary<String, EdgeGroup>(_map);
                    next[group.Key] = new EdgeGroup(filtered, filtered.Length);
                    affectedKeys.Add(group.Key);
                }
            }

            return next == null ? this : new EdgeAdjacency(next);
        }

        #region enumeration

        /// <summary>
        ///   A struct enumerator over the groups as
        ///   <c>KeyValuePair&lt;string, ArraySegment&lt;EdgeModel&gt;&gt;</c> - a count-bounded slice of
        ///   each group's backing array, never the raw array, so spare capacity is never observable. The
        ///   in-engine hot-path consumers (path/subgraph, removal cascade, persistence) <c>foreach</c> the
        ///   segment (allocation-free) or read <c>.Value.Count</c>/<c>.Value[i]</c> exactly as they read a
        ///   plain array, in both shapes. Groups are surfaced in a stable order (the sole group, or the
        ///   map's insertion order); within a group the slice preserves append order (so a poison
        ///   appended last is enumerated last).
        /// </summary>
        public Enumerator GetEnumerator() => new Enumerator(this);

        public struct Enumerator
        {
            private readonly EdgeAdjacency _adjacency;
            private readonly Boolean _isMap;
            private Dictionary<String, EdgeGroup>.Enumerator _mapEnumerator;
            // 0 = before the sole group, 1 = positioned on it, 2 = past it (single-group shape only).
            private Int32 _singleState;

            internal Enumerator(EdgeAdjacency adjacency)
            {
                _adjacency = adjacency;
                _isMap = adjacency._map != null;
                _mapEnumerator = _isMap ? adjacency._map.GetEnumerator() : default;
                _singleState = 0;
            }

            public KeyValuePair<String, ArraySegment<EdgeModel>> Current
            {
                get
                {
                    if (_isMap)
                    {
                        var current = _mapEnumerator.Current;
                        return new KeyValuePair<String, ArraySegment<EdgeModel>>(
                            current.Key, new ArraySegment<EdgeModel>(current.Value.Array, 0, current.Value.Count));
                    }

                    return new KeyValuePair<String, ArraySegment<EdgeModel>>(
                        _adjacency._soleKey, new ArraySegment<EdgeModel>(_adjacency._soleArray, 0, _adjacency._soleCount));
                }
            }

            public Boolean MoveNext()
            {
                if (_isMap)
                {
                    return _mapEnumerator.MoveNext();
                }

                if (_singleState == 0)
                {
                    _singleState = 1;
                    return true;
                }

                _singleState = 2;
                return false;
            }
        }

        #endregion

        #region private helpers

        /// <summary>
        ///   Appends <paramref name="edge" /> to the group <c>(array, count)</c> with amortised O(1)
        ///   capacity. When a spare slot exists (<c>count &lt; array.Length</c>) the edge is written into
        ///   <c>array[count]</c> - a slot no reader can observe because every reader slices
        ///   <c>[0, count)</c> - and the SAME array is returned with <c>count + 1</c>; the owning vertex's
        ///   volatile publish is the release that orders this write before any reader can acquire the new
        ///   count. Otherwise the array is grown ×2, copied, and the new slot written. Mirrors
        ///   <c>Fallen8.AppendGraphElement</c>.
        /// </summary>
        private static void AppendWithCapacity(EdgeModel[] array, Int32 count, EdgeModel edge,
                                               out EdgeModel[] resultArray, out Int32 resultCount)
        {
            if (count < array.Length)
            {
                array[count] = edge;          // write the spare slot FIRST (index >= every published count)
                resultArray = array;          // ... share the SAME array ...
                resultCount = count + 1;       // ... and publish count+1 LAST (release at the owning volatile store)
                return;
            }

            var grown = new EdgeModel[count == 0 ? 1 : count * 2];
            Array.Copy(array, grown, count);
            grown[count] = edge;
            resultArray = grown;
            resultCount = count + 1;
        }

        /// <summary>
        ///   Batch counterpart of <see cref="AppendWithCapacity" />: appends all of
        ///   <paramref name="edges" /> in encounter order, filling spare slots in place when they suffice
        ///   (each written index is <c>&gt;= count</c>, so reader-invisible) or growing once to
        ///   <c>max(count + edges, count*2)</c>. One rebuild for k edges instead of k.
        /// </summary>
        private static void AppendManyWithCapacity(EdgeModel[] array, Int32 count, IReadOnlyList<EdgeModel> edges,
                                                    out EdgeModel[] resultArray, out Int32 resultCount)
        {
            var need = count + edges.Count;
            if (need <= array.Length)
            {
                for (var i = 0; i < edges.Count; i++)
                {
                    array[count + i] = edges[i]; // spare slots (indices >= published count) - reader-invisible
                }
                resultArray = array;
                resultCount = need;
                return;
            }

            var newLength = Math.Max(need, count == 0 ? edges.Count : count * 2);
            var grown = new EdgeModel[newLength];
            Array.Copy(array, grown, count);
            for (var i = 0; i < edges.Count; i++)
            {
                grown[count + i] = edges[i];
            }
            resultArray = grown;
            resultCount = need;
        }

        /// <summary>Builds a fresh right-sized group (<c>count == length</c>) from <paramref name="edges" />.</summary>
        private static EdgeGroup BuildGroup(IReadOnlyList<EdgeModel> edges)
        {
            var array = new EdgeModel[edges.Count];
            for (var i = 0; i < edges.Count; i++)
            {
                array[i] = edges[i];
            }
            return new EdgeGroup(array, edges.Count);
        }

        /// <summary>
        ///   Removes every entry in <c>[0, count)</c> of <paramref name="source" /> whose <c>Id</c> equals
        ///   <paramref name="id" /> into a fresh COMPACTED array (<c>result.Length == survivors</c>, no
        ///   spare), returning <c>true</c> when something changed; returns <c>false</c> (and
        ///   <paramref name="result" /> <c>null</c>) when nothing matched, so the caller keeps the current
        ///   instance. Scans only the logical count (spare slots beyond it are ignored) and dereferences
        ///   each entry's <c>Id</c> (so a <c>null</c> slot within the count throws), matching the former
        ///   <c>ImmutableList.RemoveAll(_ =&gt; _.Id == id)</c>.
        /// </summary>
        private static Boolean RemoveById(EdgeModel[] source, Int32 count, Int32 id, out EdgeModel[] result)
        {
            var survivors = 0;
            for (var i = 0; i < count; i++)
            {
                if (source[i].Id != id)
                {
                    survivors++;
                }
            }

            if (survivors == count)
            {
                result = null;
                return false;
            }

            result = new EdgeModel[survivors];
            var idx = 0;
            for (var i = 0; i < count; i++)
            {
                if (source[i].Id != id)
                {
                    result[idx++] = source[i];
                }
            }
            return true;
        }

        /// <summary>
        ///   Whether <paramref name="edge" /> is present in <c>[0, count)</c> of <paramref name="source" />
        ///   by reference (the equality <see cref="EdgeModel" /> uses). Null-slot-safe, mirroring
        ///   <c>ImmutableList.Contains</c>, so the membership probe never faults on a poisoned <c>null</c>
        ///   entry (only <see cref="RemoveById" /> does).
        /// </summary>
        private static Boolean Contains(EdgeModel[] source, Int32 count, EdgeModel edge)
        {
            for (var i = 0; i < count; i++)
            {
                if (ReferenceEquals(source[i], edge))
                {
                    return true;
                }
            }
            return false;
        }

        #endregion
    }
}
