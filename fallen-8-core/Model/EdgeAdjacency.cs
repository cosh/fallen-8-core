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
    ///   mutators (<see cref="WithEdgeAppended" />, <see cref="WithEdgeRemovedFromGroup" />,
    ///   <see cref="WithEdgeRemovedEverywhere" />) return a brand-new instance which the owning
    ///   <see cref="VertexModel" /> publishes by ONE <c>volatile</c> reference assignment. This is the
    ///   whole-reference copy-on-write swap the lock-free-reader / single-writer contract requires: a
    ///   reader captures the <c>volatile</c> field into a local once and then sees a fully built,
    ///   self-consistent adjacency (correct on weak-memory hardware, because the acquiring volatile
    ///   read pairs with the releasing volatile write that published this instance).
    ///
    ///   <para>
    ///   Layout (finding: adjacency-flattening, INLINE-SMALL-MAP). The overwhelmingly common vertex
    ///   has all of its edges under a SINGLE edge-property-id (one group per direction). That case is
    ///   stored INLINE - just the group key <see cref="_soleKey" /> plus its
    ///   <see cref="_soleGroup" /> array - carrying NO <see cref="Dictionary{TKey,TValue}" /> at all,
    ///   so it avoids the dictionary's fixed buckets+entries overhead (which made a degree-2 vertex a
    ///   small regression vs. the former 1-entry <c>ImmutableDictionary</c>). Only a genuinely
    ///   multi-group vertex allocates the fallback <see cref="_map" />. Exactly one shape is populated:
    ///   single-group =&gt; <c>_map == null</c>, <c>_soleGroup != null</c>; multi-group =&gt;
    ///   <c>_map != null</c>.
    ///   </para>
    /// </summary>
    internal sealed class EdgeAdjacency
    {
        /// <summary>The sole group's edge-property-id in the single-group (inline) shape; <c>null</c> in the multi-group shape.</summary>
        private readonly String _soleKey;

        /// <summary>The sole group's contiguous edge array in the single-group (inline) shape; <c>null</c> in the multi-group shape.</summary>
        private readonly EdgeModel[] _soleGroup;

        /// <summary>The group map in the multi-group (fallback) shape; <c>null</c> in the single-group shape. Never mutated after construction.</summary>
        private readonly Dictionary<String, EdgeModel[]> _map;

        /// <summary>Constructs the single-group (inline) shape. The key mirrors a
        /// <c>Dictionary&lt;string, ...&gt;</c> key, so it is never <c>null</c>.</summary>
        private EdgeAdjacency(String soleKey, EdgeModel[] soleGroup)
        {
            _soleKey = soleKey;
            _soleGroup = soleGroup;
        }

        /// <summary>Constructs the multi-group (fallback) shape around an already-built, never-again-mutated map.</summary>
        private EdgeAdjacency(Dictionary<String, EdgeModel[]> map)
        {
            _map = map;
        }

        /// <summary>
        ///   Builds the initial adjacency of a vertex with a single edge in one group (the first edge
        ///   ever added to a direction). Inline by construction - no dictionary.
        /// </summary>
        internal static EdgeAdjacency SingleEdge(String edgePropertyId, EdgeModel edge)
        {
            return new EdgeAdjacency(edgePropertyId, new[] { edge });
        }

        /// <summary>
        ///   Builds an adjacency from the <c>Dictionary&lt;string, List&lt;EdgeModel&gt;&gt;</c> shape the
        ///   persistency layer reconstructs on load (and the internal ctor / <c>SetOutEdges</c> take).
        ///   A single-entry source becomes the inline shape; a multi-entry source snapshots each group
        ///   into its own contiguous array under a fresh map. A <c>null</c>/empty source has no
        ///   adjacency and returns <c>null</c> (empty =&gt; <c>null</c>, matching the property-store
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
                    return new EdgeAdjacency(kv.Key, kv.Value.ToArray());
                }
            }

            var map = new Dictionary<String, EdgeModel[]>(source.Count);
            foreach (var kv in source)
            {
                map[kv.Key] = kv.Value.ToArray();
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
                return (UInt32)_soleGroup.Length;
            }

            UInt32 degree = 0;
            foreach (var group in _map.Values)
            {
                degree += (UInt32)group.Length;
            }
            return degree;
        }

        /// <summary>
        ///   Looks up a group's contiguous array by edge-property-id. Uses ordinal string equality in
        ///   the inline shape, matching the default <c>Dictionary&lt;string, ...&gt;</c> comparer the
        ///   multi-group shape uses, so the two shapes are indistinguishable to callers.
        /// </summary>
        internal Boolean TryGetGroup(String key, out EdgeModel[] group)
        {
            if (_map != null)
            {
                return _map.TryGetValue(key, out group);
            }

            if (String.Equals(_soleKey, key, StringComparison.Ordinal))
            {
                group = _soleGroup;
                return true;
            }

            group = null;
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
        ///   <paramref name="edgePropertyId" /> (creating the group as needed). Build-new-and-swap: the
        ///   existing arrays/map are treated as immutable, so only a fresh array for the changed group
        ///   - and, when a second group appears, a fresh map - is allocated. The inline shape is kept
        ///   as long as there is one group and only promotes to a map when a genuinely new group is
        ///   added.
        /// </summary>
        internal EdgeAdjacency WithEdgeAppended(String edgePropertyId, EdgeModel edge)
        {
            if (_map == null)
            {
                if (String.Equals(_soleKey, edgePropertyId, StringComparison.Ordinal))
                {
                    // Append within the sole group - stays inline.
                    return new EdgeAdjacency(_soleKey, Append(_soleGroup, edge));
                }

                // A second, distinct group appears - promote to the map shape.
                var promoted = new Dictionary<String, EdgeModel[]>(2)
                {
                    { _soleKey, _soleGroup },
                    { edgePropertyId, new[] { edge } }
                };
                return new EdgeAdjacency(promoted);
            }

            var next = new Dictionary<String, EdgeModel[]>(_map);
            if (next.TryGetValue(edgePropertyId, out var existing))
            {
                next[edgePropertyId] = Append(existing, edge);
            }
            else
            {
                next[edgePropertyId] = new[] { edge };
            }
            return new EdgeAdjacency(next);
        }

        /// <summary>
        ///   Returns a NEW adjacency with every edge whose <see cref="AGraphElementModel.Id" /> equals
        ///   <paramref name="id" /> removed from the single group <paramref name="edgePropertyId" />, or
        ///   <c>this</c> (the SAME reference) when the group is absent or nothing matched - so the
        ///   caller can skip republishing. Like the former <c>ImmutableList.RemoveAll(_ =&gt; _.Id ==
        ///   id)</c>, it dereferences each entry's <c>Id</c>, so a poisoned <c>null</c> slot throws HERE
        ///   - before any new instance is returned and thus before the caller publishes - leaving the
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

                var filtered = RemoveById(_soleGroup, id);
                return ReferenceEquals(filtered, _soleGroup)
                    ? this
                    : new EdgeAdjacency(_soleKey, filtered);
            }

            if (!_map.TryGetValue(edgePropertyId, out var existing))
            {
                return this;
            }

            var filteredGroup = RemoveById(existing, id);
            if (ReferenceEquals(filteredGroup, existing))
            {
                return this;
            }

            var next = new Dictionary<String, EdgeModel[]>(_map);
            next[edgePropertyId] = filteredGroup;
            return new EdgeAdjacency(next);
        }

        /// <summary>
        ///   Returns a NEW adjacency with <paramref name="edge" /> removed from EVERY group that
        ///   contains it (by reference), recording each affected edge-property-id in
        ///   <paramref name="affectedKeys" /> so the removal cascade can replay them on rollback; or
        ///   <c>this</c> when the edge is present in no group. The membership probe is null-slot-safe,
        ///   but the actual removal dereferences each entry's <c>Id</c>, so - exactly as the former
        ///   per-group <c>RemoveAll</c> - a poisoned <c>null</c> slot faults here, before any new
        ///   instance is returned (and thus before the caller publishes) and before the affected key is
        ///   recorded, so the live adjacency and the returned key list stay consistent.
        /// </summary>
        internal EdgeAdjacency WithEdgeRemovedEverywhere(EdgeModel edge, List<String> affectedKeys)
        {
            if (_map == null)
            {
                if (!Contains(_soleGroup, edge))
                {
                    return this;
                }

                var filtered = RemoveById(_soleGroup, edge.Id);
                affectedKeys.Add(_soleKey);
                return new EdgeAdjacency(_soleKey, filtered);
            }

            Dictionary<String, EdgeModel[]> next = null;
            foreach (var group in _map)
            {
                if (Contains(group.Value, edge))
                {
                    var filtered = RemoveById(group.Value, edge.Id);
                    next ??= new Dictionary<String, EdgeModel[]>(_map);
                    next[group.Key] = filtered;
                    affectedKeys.Add(group.Key);
                }
            }

            return next == null ? this : new EdgeAdjacency(next);
        }

        #region enumeration

        /// <summary>
        ///   A struct enumerator over the groups as <c>KeyValuePair&lt;string, EdgeModel[]&gt;</c> - the
        ///   exact element the former raw <c>Dictionary&lt;string, EdgeModel[]&gt;</c> yielded - so the
        ///   in-engine hot-path consumers (path/subgraph, removal cascade, persistence) can
        ///   <c>foreach</c> over the adjacency unchanged and allocation-free in both shapes. Groups are
        ///   surfaced in a stable order (the sole group, or the map's insertion order); within a group
        ///   the array preserves append order (so a poison appended last is enumerated last).
        /// </summary>
        public Enumerator GetEnumerator() => new Enumerator(this);

        public struct Enumerator
        {
            private readonly EdgeAdjacency _adjacency;
            private readonly Boolean _isMap;
            private Dictionary<String, EdgeModel[]>.Enumerator _mapEnumerator;
            // 0 = before the sole group, 1 = positioned on it, 2 = past it (single-group shape only).
            private Int32 _singleState;

            internal Enumerator(EdgeAdjacency adjacency)
            {
                _adjacency = adjacency;
                _isMap = adjacency._map != null;
                _mapEnumerator = _isMap ? adjacency._map.GetEnumerator() : default;
                _singleState = 0;
            }

            public KeyValuePair<String, EdgeModel[]> Current => _isMap
                ? _mapEnumerator.Current
                : new KeyValuePair<String, EdgeModel[]>(_adjacency._soleKey, _adjacency._soleGroup);

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

        /// <summary>Returns a new array that is <paramref name="existing" /> with <paramref name="edge" /> appended.</summary>
        private static EdgeModel[] Append(EdgeModel[] existing, EdgeModel edge)
        {
            var appended = new EdgeModel[existing.Length + 1];
            Array.Copy(existing, appended, existing.Length);
            appended[existing.Length] = edge;
            return appended;
        }

        /// <summary>
        ///   Returns a new array with every entry whose <c>Id</c> equals <paramref name="id" /> removed,
        ///   or the SAME reference when nothing matches. Dereferences each entry's <c>Id</c> (so a
        ///   <c>null</c> slot throws), matching the former <c>ImmutableList.RemoveAll(_ =&gt; _.Id ==
        ///   id)</c>.
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
        ///   Whether <paramref name="edge" /> is present in <paramref name="source" /> by reference
        ///   (the equality <see cref="EdgeModel" /> uses). Null-slot-safe, mirroring
        ///   <c>ImmutableList.Contains</c>, so the membership probe never faults on a poisoned
        ///   <c>null</c> entry (only <see cref="RemoveById" /> does).
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
    }
}
