// MIT License
//
// EnginePerformanceFollowupsTest.cs
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
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Expression;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Behaviour regression tests for the "engine-performance-followups" feature.
    ///
    ///  - P4: <see cref="Fallen8.IndexScan"/> routes ORDERED operators (Greater / GreaterOrEquals /
    ///        Lower / LowerOrEquals) on a RangeIndex through the index's O(log n + k) sorted methods
    ///        instead of the generic O(n) FindElementsIndex scan. These tests pin that the rerouted
    ///        result set is IDENTICAL to the generic path's - including the cross-bucket .Distinct()
    ///        dedup - across every ordered operator and selectivity, and that a non-range index
    ///        (DictionaryIndex) is completely unaffected.
    ///
    /// The generic FindElementsIndex path (which a DictionaryIndex still uses) scans in nondeterministic
    /// PLINQ order, so parity is asserted as SET equivalence (CollectionAssert.AreEquivalent), never as
    /// a positional list comparison - matching the only ordering contract IndexScan has ever offered.
    /// </summary>
    [TestClass]
    public class EnginePerformanceFollowupsTest
    {
        private ILoggerFactory _loggerFactory;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
        }

        private VertexModel[] CreateVertices(Fallen8 fallen8, int count)
        {
            var tx = new CreateVerticesTransaction();
            for (int i = 0; i < count; i++)
            {
                tx.AddVertex(1u, "test");
            }
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.GetCreatedVertices().ToArray();
        }

        /// <summary>
        /// Populates the given index with a fixed key-&gt;element layout that deliberately exercises
        /// BOTH multi-value buckets (key 20 holds two elements) AND cross-bucket membership (v0 lives
        /// under keys 10 and 40; v1 under keys 20 and 50), so the cross-bucket .Distinct() dedup is
        /// forced. Returns the vertices, indexed v0..v4 (v5 is intentionally never indexed).
        /// </summary>
        private static void PopulateStandardLayout(IIndex index, VertexModel[] v)
        {
            index.AddOrUpdate(10, v[0]);
            index.AddOrUpdate(20, v[1]);
            index.AddOrUpdate(20, v[2]); // multi-value bucket
            index.AddOrUpdate(30, v[3]);
            index.AddOrUpdate(40, v[0]); // v0 also under 10 -> cross-bucket
            index.AddOrUpdate(50, v[4]);
            index.AddOrUpdate(50, v[1]); // v1 also under 20 -> cross-bucket, in a multi-value bucket
        }

        /// <summary>The ordered operators the reroute must cover.</summary>
        private static readonly BinaryOperator[] OrderedOperators =
        {
            BinaryOperator.Greater,
            BinaryOperator.GreaterOrEquals,
            BinaryOperator.Lower,
            BinaryOperator.LowerOrEquals
        };

        /// <summary>
        /// Literals chosen to span every selectivity per operator: below all keys, above all keys, the
        /// gaps between keys (partial), and EXACTLY each existing key (boundary inclusive-vs-exclusive,
        /// where Greater and GreaterOrEquals - and Lower / LowerOrEquals - must diverge by one bucket).
        /// </summary>
        private static readonly IComparable[] Literals =
        {
            5, 9, 10, 15, 20, 25, 30, 35, 40, 45, 50, 55, 100
        };

        [TestMethod]
        public void P4_OrderedIndexScan_RangeIndex_MatchesGenericPath_AcrossOperatorsAndSelectivities()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var v = CreateVertices(fallen8, 6);

            // Two indices, populated IDENTICALLY. The RangeIndex takes the new O(log n + k) reroute;
            // the DictionaryIndex (not an IRangeIndex) takes the untouched generic O(n) FindElementsIndex
            // path and therefore IS the reference "old" result for every case.
            IIndex rangeIndex, dictIndex;
            Assert.IsTrue(fallen8.IndexFactory.TryCreateIndex(out rangeIndex, "rangeIdx", "RangeIndex"));
            Assert.IsTrue(fallen8.IndexFactory.TryCreateIndex(out dictIndex, "dictIdx", "DictionaryIndex"));
            PopulateStandardLayout(rangeIndex, v);
            PopulateStandardLayout(dictIndex, v);

            foreach (var op in OrderedOperators)
            {
                foreach (var literal in Literals)
                {
                    ImmutableList<AGraphElementModel> rangeResult;
                    var rangeFound = fallen8.IndexScan(out rangeResult, "rangeIdx", literal, op);

                    ImmutableList<AGraphElementModel> genericResult;
                    var genericFound = fallen8.IndexScan(out genericResult, "dictIdx", literal, op);

                    var context = $"{op} literal={literal}";

                    // The bool contract is result.Count > 0 in both paths, so it must agree too.
                    Assert.AreEqual(genericFound, rangeFound, $"found flag must match ({context})");

                    // Neither path returns null for an ordered scan (empty -> empty list, false).
                    Assert.IsNotNull(rangeResult, $"range result must not be null ({context})");
                    Assert.IsNotNull(genericResult, $"generic result must not be null ({context})");

                    // Parity: identical SET (order is nondeterministic in the generic PLINQ path).
                    CollectionAssert.AreEquivalent(genericResult.ToList(), rangeResult.ToList(),
                        $"rerouted RangeIndex result must equal the generic FindElementsIndex result ({context})");

                    // Dedup preserved: no element appears more than once in the rerouted result.
                    Assert.AreEqual(rangeResult.Distinct().Count(), rangeResult.Count,
                        $"rerouted result must be deduped - each element exactly once ({context})");
                }
            }
        }

        [TestMethod]
        public void P4_OrderedIndexScan_RangeIndex_MatchesHandComputedSets_IncludingCrossBucketDedup()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var v = CreateVertices(fallen8, 6);

            IIndex rangeIndex;
            Assert.IsTrue(fallen8.IndexFactory.TryCreateIndex(out rangeIndex, "rangeIdx", "RangeIndex"));
            PopulateStandardLayout(rangeIndex, v);

            // Greater(25): keys {30,40,50} -> buckets {v3},{v0},{v4,v1}; v0/v1 already counted elsewhere
            // but here dedup within these buckets yields {v3,v0,v4,v1}.
            AssertScanEquivalent(fallen8, "rangeIdx", 25, BinaryOperator.Greater, v[3], v[0], v[4], v[1]);

            // GreaterOrEquals(40): keys {40,50} -> {v0},{v4,v1} -> {v0,v4,v1}. Boundary key 40 included.
            AssertScanEquivalent(fallen8, "rangeIdx", 40, BinaryOperator.GreaterOrEquals, v[0], v[4], v[1]);

            // Greater(40) EXCLUDES key 40: keys {50} -> {v4,v1}. Proves inclusive-vs-exclusive boundary.
            AssertScanEquivalent(fallen8, "rangeIdx", 40, BinaryOperator.Greater, v[4], v[1]);

            // Lower(25): keys {10,20} -> {v0},{v1,v2} -> {v0,v1,v2}.
            AssertScanEquivalent(fallen8, "rangeIdx", 25, BinaryOperator.Lower, v[0], v[1], v[2]);

            // LowerOrEquals(20) INCLUDES key 20: keys {10,20} -> {v0,v1,v2}.
            AssertScanEquivalent(fallen8, "rangeIdx", 20, BinaryOperator.LowerOrEquals, v[0], v[1], v[2]);

            // Lower(20) EXCLUDES key 20: keys {10} -> {v0}.
            AssertScanEquivalent(fallen8, "rangeIdx", 20, BinaryOperator.Lower, v[0]);

            // Cross-bucket dedup, the headline case: Greater(5) matches EVERY key. v0 sits under keys
            // 10 and 40, and v1 under keys 20 and 50, so the raw bucket concatenation has 7 entries;
            // the result must dedup to exactly the 5 distinct indexed vertices.
            ImmutableList<AGraphElementModel> all;
            Assert.IsTrue(fallen8.IndexScan(out all, "rangeIdx", 5, BinaryOperator.Greater));
            CollectionAssert.AreEquivalent(new AGraphElementModel[] { v[0], v[1], v[2], v[3], v[4] }, all.ToList(),
                "Greater(5) must return all five distinct indexed vertices");
            Assert.AreEqual(5, all.Count, "the cross-bucket duplicates (v0, v1) must be collapsed to one each");
        }

        [TestMethod]
        public void P4_OrderedIndexScan_EmptyAndFullSelectivity_MatchGenericPath()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var v = CreateVertices(fallen8, 6);

            IIndex rangeIndex;
            Assert.IsTrue(fallen8.IndexFactory.TryCreateIndex(out rangeIndex, "rangeIdx", "RangeIndex"));
            PopulateStandardLayout(rangeIndex, v);

            // Empty: nothing is greater than the maximum key.
            ImmutableList<AGraphElementModel> empty;
            Assert.IsFalse(fallen8.IndexScan(out empty, "rangeIdx", 50, BinaryOperator.Greater),
                "Greater(maxKey) must return false (empty)");
            Assert.IsNotNull(empty, "an empty ordered scan must return a non-null list, not null");
            Assert.AreEqual(0, empty.Count);

            // Empty the other way: nothing is lower than the minimum key.
            Assert.IsFalse(fallen8.IndexScan(out empty, "rangeIdx", 10, BinaryOperator.Lower),
                "Lower(minKey) must return false (empty)");
            Assert.AreEqual(0, empty.Count);

            // Full: everything is >= the minimum key.
            ImmutableList<AGraphElementModel> full;
            Assert.IsTrue(fallen8.IndexScan(out full, "rangeIdx", 10, BinaryOperator.GreaterOrEquals));
            CollectionAssert.AreEquivalent(new AGraphElementModel[] { v[0], v[1], v[2], v[3], v[4] }, full.ToList());
        }

        [TestMethod]
        public void P4_NonRangeIndex_And_NonOrderedOperators_UseGenericPath_Unaffected()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var v = CreateVertices(fallen8, 6);

            IIndex rangeIndex, dictIndex;
            Assert.IsTrue(fallen8.IndexFactory.TryCreateIndex(out rangeIndex, "rangeIdx", "RangeIndex"));
            Assert.IsTrue(fallen8.IndexFactory.TryCreateIndex(out dictIndex, "dictIdx", "DictionaryIndex"));
            PopulateStandardLayout(rangeIndex, v);
            PopulateStandardLayout(dictIndex, v);

            // A DictionaryIndex is not an IRangeIndex, so an ordered operator still runs the generic
            // O(n) FindElementsIndex path and must return the correct (deduped) set - proving the new
            // branch left the generic path untouched for non-range indices.
            ImmutableList<AGraphElementModel> dictGreater;
            Assert.IsTrue(fallen8.IndexScan(out dictGreater, "dictIdx", 25, BinaryOperator.Greater));
            CollectionAssert.AreEquivalent(new AGraphElementModel[] { v[3], v[0], v[4], v[1] }, dictGreater.ToList(),
                "the DictionaryIndex ordered scan must be unchanged by the RangeIndex reroute");

            // NotEquals is NOT an ordered operator; even on a RangeIndex it must keep the generic path.
            // Parity check against the same query on the DictionaryIndex.
            ImmutableList<AGraphElementModel> rangeNotEq, dictNotEq;
            var rangeFound = fallen8.IndexScan(out rangeNotEq, "rangeIdx", 20, BinaryOperator.NotEquals);
            var dictFound = fallen8.IndexScan(out dictNotEq, "dictIdx", 20, BinaryOperator.NotEquals);
            Assert.AreEqual(dictFound, rangeFound, "NotEquals found flag must match between range and dict");
            CollectionAssert.AreEquivalent(dictNotEq.ToList(), rangeNotEq.ToList(),
                "NotEquals on a RangeIndex must equal the generic-path result (not rerouted)");

            // Equals likewise keeps the generic TryGetValue path for both index kinds.
            ImmutableList<AGraphElementModel> rangeEq, dictEq;
            fallen8.IndexScan(out rangeEq, "rangeIdx", 20, BinaryOperator.Equals);
            fallen8.IndexScan(out dictEq, "dictIdx", 20, BinaryOperator.Equals);
            CollectionAssert.AreEquivalent(dictEq.ToList(), rangeEq.ToList(),
                "Equals(20) must return the key-20 bucket {v1, v2} on both index kinds");
            CollectionAssert.AreEquivalent(new AGraphElementModel[] { v[1], v[2] }, rangeEq.ToList());
        }

        private static void AssertScanEquivalent(Fallen8 fallen8, string indexId, IComparable literal,
            BinaryOperator op, params AGraphElementModel[] expected)
        {
            ImmutableList<AGraphElementModel> result;
            fallen8.IndexScan(out result, indexId, literal, op);
            Assert.IsNotNull(result, $"{op} literal={literal} must not return null");
            CollectionAssert.AreEquivalent(expected, result.ToList(), $"{op} literal={literal}");
            Assert.AreEqual(expected.Length, result.Count, $"{op} literal={literal} must be deduped to the expected count");
        }
    }
}
