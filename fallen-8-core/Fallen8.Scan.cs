// MIT License
//
// Fallen8.Scan.cs
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
using System.Collections.Immutable;
using System.Linq;
using NoSQL.GraphDB.Core.Expression;
using NoSQL.GraphDB.Core.Helper;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Index.Fulltext;
using NoSQL.GraphDB.Core.Index.Range;
using NoSQL.GraphDB.Core.Model;

namespace NoSQL.GraphDB.Core
{
    public sealed partial class Fallen8
    {
        /// <summary>
        ///   A PARALLEL query over the live elements (ids <c>[0, Count)</c>) of the given snapshot.
        ///   Reserved for the genuinely heavy full-graph scan with a user predicate
        ///   (<see cref="FindElements(ElementSeeker, String)" />); the light-predicate enumerations
        ///   (counts, GetAll*) use the sequential <see cref="LiveElementsSequential" /> instead, where
        ///   PLINQ's partition/merge overhead would exceed the per-element work (finding P7). Uses a
        ///   range partitioner (no source-array allocation), bounded by
        ///   <see cref="ParallelHelper.GetOptimalNumberOfTasks" /> (clamped to at least 1, since it can
        ///   compute 0 on a single-core host). Elements may be null (load-left gaps); callers filter.
        /// </summary>
        private static ParallelQuery<AGraphElementModel> LiveElements(Snapshot snap)
        {
            var segments = snap.Segments;
            return ParallelEnumerable.Range(0, snap.Count)
                .WithDegreeOfParallelism(Math.Max(1, ParallelHelper.GetOptimalNumberOfTasks()))
                .Select(i => segments[i >> SegmentShift][i & SegmentMask]);
        }

        /// <summary>
        ///   A SEQUENTIAL enumeration over the live elements (ids <c>[0, Count)</c>) of the given
        ///   snapshot, in id order. Used for the light-predicate scans (counts, GetAll*) where the
        ///   per-element work is a cheap null/removed/type/label check and PLINQ overhead is not worth
        ///   paying (finding P7). Consecutive ids map to consecutive slots within a segment, so the
        ///   walk is cache-friendly. Elements may be null (load-left gaps); callers filter.
        /// </summary>
        private static IEnumerable<AGraphElementModel> LiveElementsSequential(Snapshot snap)
        {
            var segments = snap.Segments;
            int count = snap.Count;
            for (int i = 0; i < count; i++)
            {
                yield return segments[i >> SegmentShift][i & SegmentMask];
            }
        }

        /// <summary>
        ///   THE live-element resolve (feature code-quality: one implementation instead of one
        ///   per element type). Captures the published snapshot once (volatile acquire) so the
        ///   bound check and the indexer operate on the same holder - a concurrent single-writer
        ///   append or Trim can never make this read observe a Count that disagrees with the
        ///   segments it indexes (no out-of-range, no torn/null slot within [0, Count)). The
        ///   <c>as</c> cast makes a type mismatch (asking for a vertex at an edge id) a clean
        ///   false, like a missing or tombstoned element.
        /// </summary>
        private bool TryGetLiveElement<T>(out T result, int id) where T : AGraphElementModel
        {
            var snap = _snapshot;

            if (id < 0 || id >= snap.Count)
            {
                result = null;
                return false;
            }

            result = snap.Segments[id >> SegmentShift][id & SegmentMask] as T;
            return result != null && !result._removed;
        }

        public override bool TryGetGraphElement(out AGraphElementModel result, int id)
        {
            return TryGetLiveElement(out result, id);
        }

        public override bool TryGetEdge(out EdgeModel result, int id)
        {
            return TryGetLiveElement(out result, id);
        }

        public override bool TryGetVertex(out VertexModel result, int id)
        {
            return TryGetLiveElement(out result, id);
        }

        public override bool GraphScan(out List<AGraphElementModel> result, String propertyId, IComparable literal,
            BinaryOperator binOp = BinaryOperator.Equals, String interestingLabel = null)
        {
            if (string.IsNullOrWhiteSpace(propertyId))
            {
                throw new ArgumentException("Property ID cannot be null or whitespace.", nameof(propertyId));
            }

            if (literal == null)
            {
                throw new ArgumentNullException(nameof(literal));
            }

            #region binary operation

            switch (binOp)
            {
                case BinaryOperator.Equals:
                    result = FindElements(BinaryEqualsMethod, literal, propertyId, interestingLabel);
                    break;

                case BinaryOperator.Greater:
                    result = FindElements(BinaryGreaterMethod, literal, propertyId, interestingLabel);
                    break;

                case BinaryOperator.GreaterOrEquals:
                    result = FindElements(BinaryGreaterOrEqualMethod, literal, propertyId, interestingLabel);
                    break;

                case BinaryOperator.LowerOrEquals:
                    result = FindElements(BinaryLowerOrEqualMethod, literal, propertyId, interestingLabel);
                    break;

                case BinaryOperator.Lower:
                    result = FindElements(BinaryLowerMethod, literal, propertyId, interestingLabel);
                    break;

                case BinaryOperator.NotEquals:
                    result = FindElements(BinaryNotEqualsMethod, literal, propertyId, interestingLabel);
                    break;

                default:
                    result = new List<AGraphElementModel>();
                    break;
            }

            #endregion

            return result.Count > 0;
        }

        public override bool IndexScan(out IReadOnlyList<AGraphElementModel> result, string indexId, IComparable literal, BinaryOperator binOp = BinaryOperator.Equals)
        {
            if (string.IsNullOrWhiteSpace(indexId))
            {
                throw new ArgumentException("Index ID cannot be null or whitespace.", nameof(indexId));
            }

            if (literal == null)
            {
                throw new ArgumentNullException(nameof(literal));
            }

            IIndex index;
            if (!IndexFactory.TryGetIndex(out index, indexId))
            {
                result = null;
                return false;
            }

            // P4 (engine-performance-followups): when the resolved index is a RangeIndex AND the
            // operator is an ordered one, route through the RangeIndex's O(log n + k) sorted methods
            // instead of the generic O(n) FindElementsIndex scan. The rerouted set is deduped exactly
            // like FindElementsIndex's cross-bucket .Distinct(), so the result is identical. Equals /
            // NotEquals and every non-range index keep the generic path below.
            var orderedRangeIndex = index as IRangeIndex;
            if (orderedRangeIndex != null && TryOrderedRangeIndexScan(out result, orderedRangeIndex, literal, binOp))
            {
                return result.Count > 0;
            }

            #region binary operation

            switch (binOp)
            {
                case BinaryOperator.Equals:
                    // The Equals fast path returns the index's OWN posting-list bucket, which the index
                    // retains and shares (its copy-on-write is load-bearing) - so keep it as-is, just
                    // widened to IReadOnlyList (feature scan-result-representation), then filtered to the
                    // LIVE elements (feature index-lifecycle 3.2) so a removed-but-still-indexed element
                    // never surfaces. FilterLive returns the same shared bucket when nothing is dead.
                    if (!index.TryGetValue(out var equalsBucket, literal))
                    {
                        result = null;
                        return false;
                    }
                    result = FilterLive(equalsBucket);
                    break;

                case BinaryOperator.Greater:
                    result = FindElementsIndex(BinaryGreaterMethod, literal, index);
                    break;

                case BinaryOperator.GreaterOrEquals:
                    result = FindElementsIndex(BinaryGreaterOrEqualMethod, literal, index);
                    break;

                case BinaryOperator.LowerOrEquals:
                    result = FindElementsIndex(BinaryLowerOrEqualMethod, literal, index);
                    break;

                case BinaryOperator.Lower:
                    result = FindElementsIndex(BinaryLowerMethod, literal, index);
                    break;

                case BinaryOperator.NotEquals:
                    result = FindElementsIndex(BinaryNotEqualsMethod, literal, index);
                    break;

                default:
                    result = null;
                    return false;
            }

            #endregion

            return result.Count > 0;
        }

        public override bool RangeIndexScan(out IReadOnlyList<AGraphElementModel> result, string indexId, IComparable leftLimit, IComparable rightLimit, bool includeLeft = true, bool includeRight = true)
        {
            if (string.IsNullOrWhiteSpace(indexId))
            {
                throw new ArgumentException("Index ID cannot be null or whitespace.", nameof(indexId));
            }

            if (leftLimit == null)
            {
                throw new ArgumentNullException(nameof(leftLimit));
            }

            if (rightLimit == null)
            {
                throw new ArgumentNullException(nameof(rightLimit));
            }

            IIndex index;
            if (!IndexFactory.TryGetIndex(out index, indexId))
            {
                result = null;
                return false;
            }

            var rangeIndex = index as IRangeIndex;
            if (rangeIndex != null)
            {
                // IRangeIndex.Between still returns the index's own ImmutableList bucket (IIndex return
                // types are unchanged - its copy-on-write is load-bearing); widen it to IReadOnlyList and
                // filter to the LIVE elements (feature index-lifecycle 3.2) so a removed element does not
                // surface through the range path either.
                var found = rangeIndex.Between(out var between, leftLimit, rightLimit, includeLeft, includeRight);
                result = FilterLive(between);
                return found;
            }

            result = null;
            return false;
        }

        public override bool FulltextIndexScan(out FulltextSearchResult result, string indexId, string searchQuery)
        {
            if (string.IsNullOrWhiteSpace(indexId))
            {
                throw new ArgumentException("Index ID cannot be null or whitespace.", nameof(indexId));
            }

            if (string.IsNullOrWhiteSpace(searchQuery))
            {
                throw new ArgumentException("Search query cannot be null or whitespace.", nameof(searchQuery));
            }

            IIndex index;
            if (!IndexFactory.TryGetIndex(out index, indexId))
            {
                result = null;
                return false;
            }

            var fulltextIndex = index as IFulltextIndex;
            if (fulltextIndex != null)
            {
                return fulltextIndex.TryQuery(out result, searchQuery);
            }

            result = null;
            return false;
        }

        /// <summary>
        ///   k-nearest-neighbour scan over a vector index (feature vector-index) - the vector
        ///   analogue of <see cref="FulltextIndexScan"/>: resolve the index, type-check
        ///   <see cref="Index.Vector.IVectorIndex"/>, delegate. False for an unknown/non-vector
        ///   index or invalid input.
        /// </summary>
        public override bool VectorIndexScan(out Index.Vector.VectorSearchResult result, string indexId,
            float[] query, int k, Index.Vector.VectorSearchConstraint constraint = null)
        {
            result = null;

            if (string.IsNullOrWhiteSpace(indexId) || query == null)
            {
                return false;
            }

            if (!IndexFactory.TryGetIndex(out var index, indexId))
            {
                return false;
            }

            if (index is Index.Vector.IVectorIndex vectorIndex)
            {
                return vectorIndex.TryNearestNeighbors(out result, query, k, constraint);
            }

            return false;
        }

        #region private helper methods

        /// <summary>
        ///   Finds the elements.
        /// </summary>
        /// <returns> The elements. </returns>
        /// <param name='finder'> Finder. </param>
        /// <param name='literal'> Literal. </param>
        /// <param name='propertyId'> Property identifier. </param>
        /// <param name='interestingLabel'> The interesting label. </param>
        private List<AGraphElementModel> FindElements(BinaryOperatorDelegate finder, IComparable literal, String propertyId,
            String interestingLabel = null)
        {
            return FindElements(
                aGraphElement =>
                {
                    Object property;
                    return aGraphElement.TryGetProperty(out property, propertyId) &&
                           finder(property as IComparable, literal);
                });
        }

        /// <summary>
        /// Find elements by scanning the list
        /// </summary>
        /// <param name="seeker">A delegate to search for the right element</param>
        /// <param name='interestingLabel'> The interesting label. </param>
        /// <returns>A list of matching graph elements</returns>
        private List<AGraphElementModel> FindElements(ElementSeeker seeker, String interestingLabel = null)
        {
            // One fused predicate over the parallel live scan instead of three chained Where stages:
            // identical short-circuit order (null/removed first, then label, then seeker), so removed
            // or null elements never reach the seeker and the result multiset is unchanged - but only
            // one PLINQ operator (and no per-query intermediate closures) instead of three.
            var labelMatches = CheckLabel(interestingLabel);
            return LiveElements(_snapshot)
                .Where(_ => _ != null && !_._removed && labelMatches(_) && seeker(_))
                .ToList();
        }

        private static Func<AGraphElementModel, Boolean> CheckLabel(String interestingLabel = null)
        {
            return _ => interestingLabel == null || interestingLabel != null && _.Label != null && _.Label.Equals(interestingLabel);
        }

        /// <summary>
        ///   Finds elements via an index.
        /// </summary>
        /// <returns> The elements. </returns>
        /// <param name='finder'> Finder delegate. </param>
        /// <param name='literal'> Literal. </param>
        /// <param name='index'> Index. </param>
        /// <summary>
        ///   Filters an index bucket down to its LIVE elements (feature index-lifecycle 3.2): drops any
        ///   <c>null</c> or <c>_removed</c> element so an index-serving scan returns exactly the live set
        ///   <see cref="FindElements(ElementSeeker)" /> / GraphScan would for the same logical query -
        ///   index membership is only valid while its element is live, and this is the read-end floor
        ///   that holds that contract even before the write-end purge runs. Returns the SAME reference
        ///   when nothing is dead (the common case), so the Equals fast path keeps handing back the
        ///   index's shared bucket with no allocation.
        /// </summary>
        private static IReadOnlyList<AGraphElementModel> FilterLive(IReadOnlyList<AGraphElementModel> bucket)
        {
            if (bucket == null)
            {
                return null;
            }

            var dead = 0;
            for (var i = 0; i < bucket.Count; i++)
            {
                var element = bucket[i];
                if (element == null || element._removed)
                {
                    dead++;
                }
            }

            if (dead == 0)
            {
                return bucket;
            }

            var live = new List<AGraphElementModel>(bucket.Count - dead);
            for (var i = 0; i < bucket.Count; i++)
            {
                var element = bucket[i];
                if (element != null && !element._removed)
                {
                    live.Add(element);
                }
            }
            return live;
        }

        private static List<AGraphElementModel> FindElementsIndex(BinaryOperatorDelegate finder,
                                                                  IComparable literal, IIndex index)
        {
            // Sequential (feature scan-result-representation): the finder is a light IComparable.CompareTo,
            // so the former .AsParallel() paid PLINQ partition/merge overhead over a cheap predicate; and
            // the result is a per-call throwaway, so a right-sized de-duplicating List replaces the AVL
            // tree. A reference-identity HashSet reproduces the former cross-bucket .Distinct() (an element
            // indexed under several matching keys appears once) in a single pass, no re-treeing.
            var result = new List<AGraphElementModel>();
            var seen = new HashSet<AGraphElementModel>();
            foreach (var indexElement in index.GetKeyValues())
            {
                if (!finder((IComparable)indexElement.Key, literal))
                {
                    continue;
                }

                foreach (var graphElement in indexElement.Value)
                {
                    // Skip null / _removed so a removed-but-still-indexed element never surfaces
                    // (feature index-lifecycle 3.2), then dedup across buckets.
                    if (graphElement != null && !graphElement._removed && seen.Add(graphElement))
                    {
                        result.Add(graphElement);
                    }
                }
            }
            return result;
        }

        /// <summary>
        ///   P4 (engine-performance-followups): answers an ORDERED IndexScan operator
        ///   (Greater / GreaterOrEquals / Lower / LowerOrEquals) on a <see cref="IRangeIndex" /> via its
        ///   O(log n + k) sorted range methods instead of the generic O(n) <see cref="FindElementsIndex" />
        ///   scan. Returns <c>false</c> for any non-ordered operator (Equals / NotEquals) so the caller
        ///   falls back to the generic path.
        ///
        ///   Result parity with <see cref="FindElementsIndex" />: the RangeIndex's sorted methods select
        ///   EXACTLY the keys the generic finder's per-key <c>CompareTo</c> predicate would - both order
        ///   keys by <see cref="IComparable.CompareTo" />, so the suffix/prefix the binary search brackets
        ///   is the same key set the linear scan keeps (GreaterOrEquals/LowerOrEquals include the boundary
        ///   key, Greater/Lower exclude it, matching the <c>&gt;=</c>/<c>&gt;</c>/<c>&lt;=</c>/<c>&lt;</c>
        ///   predicates). Those methods, however, concatenate the matched buckets WITHOUT deduping,
        ///   whereas <see cref="FindElementsIndex" /> applies a cross-bucket <c>.Distinct()</c>. This
        ///   method therefore reapplies the SAME <c>.Distinct()</c>, so a graph element indexed under
        ///   several matching keys appears exactly once - byte-identical to the generic output set.
        /// </summary>
        private static bool TryOrderedRangeIndexScan(out IReadOnlyList<AGraphElementModel> result,
                                                     IRangeIndex rangeIndex, IComparable literal, BinaryOperator binOp)
        {
            ImmutableList<AGraphElementModel> matched;
            switch (binOp)
            {
                case BinaryOperator.Greater:
                    rangeIndex.GreaterThan(out matched, literal, false);
                    break;

                case BinaryOperator.GreaterOrEquals:
                    rangeIndex.GreaterThan(out matched, literal, true);
                    break;

                case BinaryOperator.Lower:
                    rangeIndex.LowerThan(out matched, literal, false);
                    break;

                case BinaryOperator.LowerOrEquals:
                    rangeIndex.LowerThan(out matched, literal, true);
                    break;

                default:
                    // Equals / NotEquals are not ordered range operators - fall back to the generic path.
                    result = null;
                    return false;
            }

            // Reapply FindElementsIndex's cross-bucket .Distinct() so the deduped set is identical -
            // into a right-sized List (feature scan-result-representation), no throwaway tree - and skip
            // null / _removed so a removed element never surfaces (feature index-lifecycle 3.2).
            var deduped = new List<AGraphElementModel>(matched.Count);
            var seen = new HashSet<AGraphElementModel>();
            foreach (var graphElement in matched)
            {
                if (graphElement != null && !graphElement._removed && seen.Add(graphElement))
                {
                    deduped.Add(graphElement);
                }
            }
            result = deduped;
            return true;
        }

        /// <summary>
        ///   Method for binary comparison
        /// </summary>
        /// <returns> <c>true</c> for equality; otherwise, <c>false</c> . </returns>
        /// <param name='property'> Property. </param>
        /// <param name='literal'> Literal. </param>
        private static Boolean BinaryEqualsMethod(IComparable property, IComparable literal)
        {
            return property.Equals(literal);
        }

        /// <summary>
        ///   Method for binary comparison
        /// </summary>
        /// <returns> <c>true</c> for inequality; otherwise, <c>false</c> . </returns>
        /// <param name='property'> Property. </param>
        /// <param name='literal'> Literal. </param>
        private static Boolean BinaryNotEqualsMethod(IComparable property, IComparable literal)
        {
            return !property.Equals(literal);
        }

        /// <summary>
        ///   Method for binary comparison
        /// </summary>
        /// <returns> <c>true</c> for greater property; otherwise, <c>false</c> . </returns>
        /// <param name='property'> Property. </param>
        /// <param name='literal'> Literal. </param>
        private static Boolean BinaryGreaterMethod(IComparable property, IComparable literal)
        {
            return property.CompareTo(literal) > 0;
        }

        /// <summary>
        ///   Method for binary comparison
        /// </summary>
        /// <returns> <c>true</c> for lower property; otherwise, <c>false</c> . </returns>
        /// <param name='property'> Property. </param>
        /// <param name='literal'> Literal. </param>
        private static Boolean BinaryLowerMethod(IComparable property, IComparable literal)
        {
            return property.CompareTo(literal) < 0;
        }

        /// <summary>
        ///   Method for binary comparison
        /// </summary>
        /// <returns> <c>true</c> for lower or equal property; otherwise, <c>false</c> . </returns>
        /// <param name='property'> Property. </param>
        /// <param name='literal'> Literal. </param>
        private static Boolean BinaryLowerOrEqualMethod(IComparable property, IComparable literal)
        {
            return property.CompareTo(literal) <= 0;
        }

        /// <summary>
        ///   Method for binary comparison
        /// </summary>
        /// <returns> <c>true</c> for greater or equal property; otherwise, <c>false</c> . </returns>
        /// <param name='property'> Property. </param>
        /// <param name='literal'> Literal. </param>
        private static Boolean BinaryGreaterOrEqualMethod(IComparable property, IComparable literal)
        {
            return property.CompareTo(literal) >= 0;
        }

        public int GetCountOf<TInteresting>()
        {
            // Sequential count over the captured snapshot (finding P7): counting is a light
            // per-element predicate, so a direct walk avoids PLINQ's partition/merge overhead. A
            // single volatile capture keeps the Count consistent with the segments it indexes.
            var snap = _snapshot;
            var segments = snap.Segments;
            int count = snap.Count;
            int result = 0;
            for (int i = 0; i < count; i++)
            {
                var ge = segments[i >> SegmentShift][i & SegmentMask];
                if (ge != null && !ge._removed && ge is TInteresting)
                {
                    result++;
                }
            }
            return result;
        }

        public override IReadOnlyList<VertexModel> GetAllVertices(String interestingLabel = null)
        {
            // Fill a right-sized List directly instead of packing the sequential scan into an
            // ImmutableList (an AVL tree) the caller immediately drops (feature scan-result-representation).
            // The walk is the same cheap, cache-friendly, id-ordered scan (finding P7); only the result
            // packaging changes - a flat reference array (8 B/slot, contiguous) instead of ~48 B/node
            // tree. Capture the snapshot once (volatile read); VertexCount is a capacity hint only, so a
            // stale count costs at most a resize, never a wrong result (the snapshot walk is authoritative).
            var snap = _snapshot;
            var labelMatches = CheckLabel(interestingLabel);
            var segments = snap.Segments;
            int count = snap.Count;
            var result = new List<VertexModel>(interestingLabel == null ? VertexCount : 0);
            for (int i = 0; i < count; i++)
            {
                if (segments[i >> SegmentShift][i & SegmentMask] is VertexModel vertex && !vertex._removed && labelMatches(vertex))
                {
                    result.Add(vertex);
                }
            }
            return result;
        }

        public override IReadOnlyList<EdgeModel> GetAllEdges(String interestingLabel = null)
        {
            var snap = _snapshot;
            var labelMatches = CheckLabel(interestingLabel);
            var segments = snap.Segments;
            int count = snap.Count;
            var result = new List<EdgeModel>(interestingLabel == null ? EdgeCount : 0);
            for (int i = 0; i < count; i++)
            {
                if (segments[i >> SegmentShift][i & SegmentMask] is EdgeModel edge && !edge._removed && labelMatches(edge))
                {
                    result.Add(edge);
                }
            }
            return result;
        }

        public override IReadOnlyList<AGraphElementModel> GetAllGraphElements(String interestingLabel = null)
        {
            var snap = _snapshot;
            var labelMatches = CheckLabel(interestingLabel);
            var segments = snap.Segments;
            int count = snap.Count;
            var result = new List<AGraphElementModel>(interestingLabel == null ? VertexCount + EdgeCount : 0);
            for (int i = 0; i < count; i++)
            {
                var graphElement = segments[i >> SegmentShift][i & SegmentMask];
                if (graphElement != null && !graphElement._removed && labelMatches(graphElement))
                {
                    result.Add(graphElement);
                }
            }
            return result;
        }

        #endregion
    }
}
