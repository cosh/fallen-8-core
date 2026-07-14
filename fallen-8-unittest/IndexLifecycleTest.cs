// MIT License
//
// IndexLifecycleTest.cs
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
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Expression;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Index.Range;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Tests for the "index-lifecycle" feature. Index membership is now derived state whose validity
    /// is tied to element liveness. This pins the three parts landed in this pass:
    ///   3.2 read-end filter — no index-serving scan (IndexScan Equals / ordered / NotEquals,
    ///       RangeIndexScan, the generic FindElementsIndex) returns a _removed element, and the result
    ///       is identical to GraphScan for the same logical query;
    ///   3.4 reverse-map removal — RemoveValue touches only the keys an element appears under, dropping
    ///       emptied keys and leaving the rest intact, and survives a save/load round-trip;
    ///   3.3 write-end purge — a committed removal drops the element from every registered index (so its
    ///       body is collectable), while a rolled-back removal leaves the indices untouched.
    /// Routing index writes through the single writer (3.5) and WAL-logging them (3.6) are deferred.
    /// </summary>
    [TestClass]
    public class IndexLifecycleTest
    {
        private ILoggerFactory _loggerFactory;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
        }

        private static VertexModel[] CreateVerticesWithProperty(Fallen8 fallen8, int[] pValues)
        {
            var tx = new CreateVerticesTransaction();
            foreach (var p in pValues)
            {
                tx.AddVertex(1u, "v", new Dictionary<string, object> { { "p", p } });
            }
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.GetCreatedVertices().ToArray();
        }

        private static VertexModel[] CreateVertices(Fallen8 fallen8, int count)
        {
            var tx = new CreateVerticesTransaction();
            for (var i = 0; i < count; i++)
            {
                tx.AddVertex(1u, "v");
            }
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.GetCreatedVertices().ToArray();
        }

        private static void InjectRawOutEdge(VertexModel vertex, string edgePropertyId, EdgeModel poison)
        {
            typeof(VertexModel)
                .GetMethod("InjectRawOutEdgeForTesting", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(vertex, new object[] { edgePropertyId, poison });
        }

        // ---- 3.2 read-end filter: consistency with GraphScan across every index path ---------------

        /// <summary>
        /// Indexes N elements in a DictionaryIndex and a RangeIndex under a property value, removes a
        /// subset, then asserts every index-serving path returns exactly the live set GraphScan does for
        /// the same logical query — and never a removed element.
        /// </summary>
        [TestMethod]
        public void RemovedElements_DoNotSurfaceFromAnyIndexPath_AndMatchGraphScan()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var pValues = new[] { 10, 10, 10, 20, 20, 20, 30, 30, 30 };
            var v = CreateVerticesWithProperty(fallen8, pValues);

            Assert.IsTrue(fallen8.IndexFactory.TryCreateIndex(out var dict, "dict", "DictionaryIndex"));
            Assert.IsTrue(fallen8.IndexFactory.TryCreateIndex(out var range, "range", "RangeIndex"));
            for (var i = 0; i < v.Length; i++)
            {
                dict.AddOrUpdate(pValues[i], v[i]);
                range.AddOrUpdate(pValues[i], v[i]);
            }

            // Remove one vertex from each property group.
            var removedIds = new[] { v[0].Id, v[3].Id, v[6].Id };
            fallen8.EnqueueTransaction(new RemoveGraphElementsTransaction { GraphElementIds = removedIds.ToList() })
                .WaitUntilFinished();

            int[] IndexScanIds(string indexId, IComparable literal, BinaryOperator op)
            {
                fallen8.IndexScan(out var result, indexId, literal, op);
                return (result ?? new List<AGraphElementModel>()).Select(e => e.Id).OrderBy(x => x).ToArray();
            }

            int[] GraphScanIds(IComparable literal, BinaryOperator op)
            {
                fallen8.GraphScan(out var result, "p", literal, op);
                return result.Select(e => e.Id).OrderBy(x => x).ToArray();
            }

            // Equals: both index kinds equal the GraphScan live set for key 20.
            var expectedEquals20 = GraphScanIds(20, BinaryOperator.Equals);
            CollectionAssert.AreEqual(expectedEquals20, IndexScanIds("dict", 20, BinaryOperator.Equals),
                "DictionaryIndex Equals must match GraphScan (removed hidden).");
            CollectionAssert.AreEqual(expectedEquals20, IndexScanIds("range", 20, BinaryOperator.Equals),
                "RangeIndex Equals must match GraphScan (removed hidden).");

            // Ordered Greater(15): the RangeIndex ordered path AND the DictionaryIndex generic
            // FindElementsIndex path must both equal GraphScan's live { p in {20,30} }.
            var expectedGreater15 = GraphScanIds(15, BinaryOperator.Greater);
            CollectionAssert.AreEqual(expectedGreater15, IndexScanIds("range", 15, BinaryOperator.Greater),
                "RangeIndex ordered Greater must match GraphScan.");
            CollectionAssert.AreEqual(expectedGreater15, IndexScanIds("dict", 15, BinaryOperator.Greater),
                "DictionaryIndex generic Greater must match GraphScan.");

            // NotEquals(20): live { p in {10,30} } through the generic path.
            var expectedNotEquals20 = GraphScanIds(20, BinaryOperator.NotEquals);
            CollectionAssert.AreEqual(expectedNotEquals20, IndexScanIds("dict", 20, BinaryOperator.NotEquals),
                "DictionaryIndex NotEquals must match GraphScan.");

            // RangeIndexScan [15,25]: only the live p=20 vertices.
            fallen8.RangeIndexScan(out var rangeResult, "range", 15, 25, true, true);
            var rangeIds = (rangeResult ?? new List<AGraphElementModel>()).Select(e => e.Id).OrderBy(x => x).ToArray();
            var liveP20 = new[] { v[4].Id, v[5].Id }.OrderBy(x => x).ToArray();
            CollectionAssert.AreEqual(liveP20, rangeIds, "RangeIndexScan must return only the live p=20 vertices.");

            // No removed id may appear in ANY path.
            foreach (var removed in removedIds)
            {
                Assert.IsFalse(IndexScanIds("dict", 20, BinaryOperator.NotEquals).Contains(removed));
                Assert.IsFalse(IndexScanIds("range", 15, BinaryOperator.Greater).Contains(removed));
                Assert.IsFalse(rangeIds.Contains(removed));
            }
        }

        /// <summary>
        /// The read-end filter in isolation: an index that still HOLDS a since-removed element (e.g. a
        /// stale entry added after removal) must not surface it through a scan.
        /// </summary>
        [TestMethod]
        public void IndexScan_WithAStaleRemovedEntry_FiltersItOut()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var v = CreateVertices(fallen8, 2);

            Assert.IsTrue(fallen8.IndexFactory.TryCreateIndex(out var dict, "dict", "DictionaryIndex"));
            dict.AddOrUpdate("k", v[0]); // live

            // Remove v[1] via the pipeline, THEN add it to the index (a stale entry the purge never saw).
            fallen8.EnqueueTransaction(new RemoveGraphElementTransaction { GraphElementId = v[1].Id }).WaitUntilFinished();
            dict.AddOrUpdate("k", v[1]); // v[1] is _removed but now sits in the bucket

            Assert.IsTrue(fallen8.IndexScan(out var result, "dict", "k", BinaryOperator.Equals));
            var ids = result.Select(e => e.Id).ToArray();
            Assert.IsTrue(ids.Contains(v[0].Id), "The live element must be returned.");
            Assert.IsFalse(ids.Contains(v[1].Id), "The read-end filter must drop the removed element even though it is in the bucket.");
        }

        // ---- 3.3 write-end purge -------------------------------------------------------------------

        /// <summary>
        /// A committed removal purges the element from a registered index — it is gone from the bucket
        /// itself (not merely hidden by the read filter), so no strong reference pins its body.
        /// </summary>
        [TestMethod]
        public void CommittedRemoval_PurgesTheElementFromRegisteredIndices()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var v = CreateVertices(fallen8, 3);

            Assert.IsTrue(fallen8.IndexFactory.TryCreateIndex(out var dict, "dict", "DictionaryIndex"));
            dict.AddOrUpdate("k", v[0]);
            dict.AddOrUpdate("k", v[1]);
            dict.AddOrUpdate("k", v[2]);
            Assert.AreEqual(3, dict.CountOfValues());

            fallen8.EnqueueTransaction(new RemoveGraphElementTransaction { GraphElementId = v[1].Id }).WaitUntilFinished();

            Assert.AreEqual(2, dict.CountOfValues(), "The purge must physically drop the removed element from the index.");
            Assert.IsTrue(dict.TryGetValue(out var bucket, "k"));
            Assert.IsFalse(bucket.Any(e => ReferenceEquals(e, v[1])), "The removed element must not remain in the bucket.");
            Assert.AreEqual(2, bucket.Count);
            Assert.IsTrue(bucket.Any(e => ReferenceEquals(e, v[0])) && bucket.Any(e => ReferenceEquals(e, v[2])),
                "The surviving elements must remain indexed.");
        }

        /// <summary>
        /// A rolled-back removal must leave the indices exactly as they were — the purge runs only on the
        /// commit path. Poisoning the vertex's out-adjacency makes its removal cascade fault and roll back.
        /// </summary>
        [TestMethod]
        public void RolledBackRemoval_LeavesRegisteredIndicesIntact()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var v = CreateVertices(fallen8, 2);

            Assert.IsTrue(fallen8.IndexFactory.TryCreateIndex(out var dict, "dict", "DictionaryIndex"));
            dict.AddOrUpdate("k", v[0]);
            dict.AddOrUpdate("k", v[1]);

            // Poison v[0]'s out-adjacency: iterating it during the removal cascade dereferences a null
            // edge and throws, so the removal faults and rolls back (v[0] is un-removed).
            InjectRawOutEdge(v[0], "knows", null);

            var info = fallen8.EnqueueTransaction(new RemoveGraphElementTransaction { GraphElementId = v[0].Id });
            info.WaitUntilFinished();
            Assert.AreEqual(TransactionState.RolledBack, info.TransactionState, "The poisoned removal must roll back.");

            // The index still holds v[0], and a scan returns it (it is live again after rollback).
            Assert.AreEqual(2, dict.CountOfValues(), "A rolled-back removal must not purge the index.");
            Assert.IsTrue(fallen8.IndexScan(out var result, "dict", "k", BinaryOperator.Equals));
            Assert.IsTrue(result.Any(e => ReferenceEquals(e, v[0])), "The rolled-back element must still be indexed and live.");
        }

        // ---- 3.4 reverse-map removal ---------------------------------------------------------------

        /// <summary>
        /// RemoveValue must touch only the keys the element appears under: it disappears from those
        /// buckets, an emptied key is dropped, and every other key is untouched.
        /// </summary>
        [TestMethod]
        public void DictionaryIndex_RemoveValue_TouchesOnlyAffectedKeys()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var v = CreateVertices(fallen8, 3);

            var dict = new DictionaryIndex();
            dict.Initialize(fallen8, null);
            dict.AddOrUpdate("a", v[0]); // v0 under a and b
            dict.AddOrUpdate("b", v[0]);
            dict.AddOrUpdate("a", v[1]); // a also has v1
            dict.AddOrUpdate("c", v[2]); // c has only v2

            dict.RemoveValue(v[0]);

            Assert.IsTrue(dict.TryGetValue(out var a, "a"));
            Assert.AreEqual(1, a.Count);
            Assert.AreSame(v[1], a[0], "Key 'a' must keep v1 after v0 is removed.");

            Assert.IsFalse(dict.TryGetValue(out _, "b"), "Key 'b' held only v0 and must be dropped when emptied.");

            Assert.IsTrue(dict.TryGetValue(out var c, "c"));
            Assert.AreEqual(1, c.Count);
            Assert.AreSame(v[2], c[0], "An unrelated key must be untouched.");
        }

        /// <summary>
        /// The RangeIndex reverse-map removal must also invalidate the sorted-key snapshot exactly when a
        /// key is emptied, so a subsequent range query reflects the dropped key.
        /// </summary>
        [TestMethod]
        public void RangeIndex_RemoveValue_DropsEmptiedKey_AndRangeQueryReflectsIt()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var v = CreateVertices(fallen8, 3);

            var range = new RangeIndex();
            range.Initialize(fallen8, null);
            range.AddOrUpdate(10, v[0]);
            range.AddOrUpdate(20, v[1]);
            range.AddOrUpdate(20, v[2]);

            // Warm the sorted-key cache.
            Assert.IsTrue(range.Between(out var before, 5, 25, true, true));
            Assert.AreEqual(3, before.Count);

            // Remove v0 (empties key 10) and v1 (key 20 keeps v2).
            range.RemoveValue(v[0]);
            range.RemoveValue(v[1]);

            Assert.IsTrue(range.Between(out var after, 5, 25, true, true));
            Assert.AreEqual(1, after.Count, "Only v2 (under key 20) should remain in range.");
            Assert.AreSame(v[2], after[0]);
            Assert.IsFalse(range.TryGetValue(out _, 10), "The emptied key 10 must be dropped.");
        }

        /// <summary>
        /// The reverse map must be rebuilt on load so RemoveValue stays O(affected keys) — and correct —
        /// after a save/load round-trip through the engine.
        /// </summary>
        [TestMethod]
        public void ReverseMap_SurvivesSaveLoad_AndPurgeStillWorks()
        {
            var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "f8_idxlife_" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(tempDir);
            try
            {
                var source = new Fallen8(_loggerFactory);
                var v = CreateVertices(source, 3);
                Assert.IsTrue(source.IndexFactory.TryCreateIndex(out var dict, "dict", "DictionaryIndex"));
                dict.AddOrUpdate("k", v[0]);
                dict.AddOrUpdate("k", v[1]);
                dict.AddOrUpdate("k", v[2]);

                var savePath = System.IO.Path.Combine(tempDir, "idx.f8s");
                var saveTx = new SaveTransaction { Path = savePath, SavePartitions = 1 };
                source.EnqueueTransaction(saveTx).WaitUntilFinished();

                var loaded = new Fallen8(_loggerFactory);
                loaded.EnqueueTransaction(new LoadTransaction { Path = saveTx.ActualPath }).WaitUntilFinished();

                Assert.IsTrue(loaded.IndexFactory.TryGetIndex(out var reloaded, "dict"), "The index must reload.");
                Assert.AreEqual(3, reloaded.CountOfValues());

                // Remove a reloaded element through the pipeline: the write-end purge uses the loaded
                // index's rebuilt reverse map, so the element leaves the bucket.
                Assert.IsTrue(loaded.TryGetVertex(out var reloadedV1, v[1].Id));
                loaded.EnqueueTransaction(new RemoveGraphElementTransaction { GraphElementId = reloadedV1.Id }).WaitUntilFinished();

                Assert.AreEqual(2, reloaded.CountOfValues(), "The purge must work against the reloaded index's reverse map.");
                Assert.IsTrue(reloaded.TryGetValue(out var bucket, "k"));
                Assert.IsFalse(bucket.Any(e => e.Id == v[1].Id), "The removed element must be gone from the reloaded bucket.");

                loaded.Dispose();
                source.Dispose();
            }
            finally
            {
                try { System.IO.Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
