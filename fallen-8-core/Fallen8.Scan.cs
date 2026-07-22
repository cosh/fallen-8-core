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
                    result = FindElements(ScanHelpers.BinaryEqualsMethod, literal, propertyId, interestingLabel);
                    break;

                case BinaryOperator.Greater:
                    result = FindElements(ScanHelpers.BinaryGreaterMethod, literal, propertyId, interestingLabel);
                    break;

                case BinaryOperator.GreaterOrEquals:
                    result = FindElements(ScanHelpers.BinaryGreaterOrEqualMethod, literal, propertyId, interestingLabel);
                    break;

                case BinaryOperator.LowerOrEquals:
                    result = FindElements(ScanHelpers.BinaryLowerOrEqualMethod, literal, propertyId, interestingLabel);
                    break;

                case BinaryOperator.Lower:
                    result = FindElements(ScanHelpers.BinaryLowerMethod, literal, propertyId, interestingLabel);
                    break;

                case BinaryOperator.NotEquals:
                    result = FindElements(ScanHelpers.BinaryNotEqualsMethod, literal, propertyId, interestingLabel);
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
            if (orderedRangeIndex != null && ScanHelpers.TryOrderedRangeIndexScan(out result, orderedRangeIndex, literal, binOp))
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
                    result = ScanHelpers.FilterLive(equalsBucket);
                    break;

                case BinaryOperator.Greater:
                    result = ScanHelpers.FindElementsIndex(ScanHelpers.BinaryGreaterMethod, literal, index);
                    break;

                case BinaryOperator.GreaterOrEquals:
                    result = ScanHelpers.FindElementsIndex(ScanHelpers.BinaryGreaterOrEqualMethod, literal, index);
                    break;

                case BinaryOperator.LowerOrEquals:
                    result = ScanHelpers.FindElementsIndex(ScanHelpers.BinaryLowerOrEqualMethod, literal, index);
                    break;

                case BinaryOperator.Lower:
                    result = ScanHelpers.FindElementsIndex(ScanHelpers.BinaryLowerMethod, literal, index);
                    break;

                case BinaryOperator.NotEquals:
                    result = ScanHelpers.FindElementsIndex(ScanHelpers.BinaryNotEqualsMethod, literal, index);
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
                result = ScanHelpers.FilterLive(between);
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
            var labelMatches = ScanHelpers.CheckLabel(interestingLabel);
            return LiveElements(_snapshot)
                .Where(_ => _ != null && !_._removed && labelMatches(_) && seeker(_))
                .ToList();
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
            var labelMatches = ScanHelpers.CheckLabel(interestingLabel);
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
            var labelMatches = ScanHelpers.CheckLabel(interestingLabel);
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
            var labelMatches = ScanHelpers.CheckLabel(interestingLabel);
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
