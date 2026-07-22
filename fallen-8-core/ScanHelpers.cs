// MIT License
//
// ScanHelpers.cs
//
// Copyright (c) 2026 Henning Rauch
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
using NoSQL.GraphDB.Core.Expression;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Index.Range;
using NoSQL.GraphDB.Core.Model;

namespace NoSQL.GraphDB.Core
{
    /// <summary>
    ///   Binary operator delegate.
    /// </summary>
    internal delegate Boolean BinaryOperatorDelegate(IComparable property, IComparable literal);

    /// <summary>
    ///   The static, snapshot-free helpers of the <see cref="Fallen8" /> scan family: the binary
    ///   comparison predicates and the index-bucket selection/filtering they drive. Everything here
    ///   is a pure function over its arguments - no engine state, no snapshot capture (those walks
    ///   stay on <see cref="Fallen8" />, which owns the snapshot protocol).
    /// </summary>
    internal static class ScanHelpers
    {
        internal static Func<AGraphElementModel, Boolean> CheckLabel(String interestingLabel = null)
        {
            return _ => interestingLabel == null || interestingLabel != null && _.Label != null && _.Label.Equals(interestingLabel);
        }

        /// <summary>
        ///   Filters an index bucket down to its LIVE elements (feature index-lifecycle 3.2): drops any
        ///   <c>null</c> or <c>_removed</c> element so an index-serving scan returns exactly the live set
        ///   <see cref="Fallen8.FindElements(Fallen8.ElementSeeker, String)" /> / GraphScan would for the
        ///   same logical query - index membership is only valid while its element is live, and this is
        ///   the read-end floor that holds that contract even before the write-end purge runs. Returns
        ///   the SAME reference when nothing is dead (the common case), so the Equals fast path keeps
        ///   handing back the index's shared bucket with no allocation.
        /// </summary>
        internal static IReadOnlyList<AGraphElementModel> FilterLive(IReadOnlyList<AGraphElementModel> bucket)
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

        /// <summary>
        ///   Finds elements via an index.
        /// </summary>
        /// <returns> The elements. </returns>
        /// <param name='finder'> Finder delegate. </param>
        /// <param name='literal'> Literal. </param>
        /// <param name='index'> Index. </param>
        internal static List<AGraphElementModel> FindElementsIndex(BinaryOperatorDelegate finder,
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
        internal static bool TryOrderedRangeIndexScan(out IReadOnlyList<AGraphElementModel> result,
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
        internal static Boolean BinaryEqualsMethod(IComparable property, IComparable literal)
        {
            return property.Equals(literal);
        }

        /// <summary>
        ///   Method for binary comparison
        /// </summary>
        /// <returns> <c>true</c> for inequality; otherwise, <c>false</c> . </returns>
        /// <param name='property'> Property. </param>
        /// <param name='literal'> Literal. </param>
        internal static Boolean BinaryNotEqualsMethod(IComparable property, IComparable literal)
        {
            return !property.Equals(literal);
        }

        /// <summary>
        ///   Method for binary comparison
        /// </summary>
        /// <returns> <c>true</c> for greater property; otherwise, <c>false</c> . </returns>
        /// <param name='property'> Property. </param>
        /// <param name='literal'> Literal. </param>
        internal static Boolean BinaryGreaterMethod(IComparable property, IComparable literal)
        {
            return property.CompareTo(literal) > 0;
        }

        /// <summary>
        ///   Method for binary comparison
        /// </summary>
        /// <returns> <c>true</c> for lower property; otherwise, <c>false</c> . </returns>
        /// <param name='property'> Property. </param>
        /// <param name='literal'> Literal. </param>
        internal static Boolean BinaryLowerMethod(IComparable property, IComparable literal)
        {
            return property.CompareTo(literal) < 0;
        }

        /// <summary>
        ///   Method for binary comparison
        /// </summary>
        /// <returns> <c>true</c> for lower or equal property; otherwise, <c>false</c> . </returns>
        /// <param name='property'> Property. </param>
        /// <param name='literal'> Literal. </param>
        internal static Boolean BinaryLowerOrEqualMethod(IComparable property, IComparable literal)
        {
            return property.CompareTo(literal) <= 0;
        }

        /// <summary>
        ///   Method for binary comparison
        /// </summary>
        /// <returns> <c>true</c> for greater or equal property; otherwise, <c>false</c> . </returns>
        /// <param name='property'> Property. </param>
        /// <param name='literal'> Literal. </param>
        internal static Boolean BinaryGreaterOrEqualMethod(IComparable property, IComparable literal)
        {
            return property.CompareTo(literal) >= 0;
        }
    }
}
