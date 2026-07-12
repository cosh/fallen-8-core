// MIT License
//
// MemoryFootprintTest.cs
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
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Tests for the removed-element reclamation of the memory-footprint theme (finding M4):
    /// under sustained add/remove churn the master store must stay bounded rather than growing
    /// one tombstone per removed element forever, and the graph must remain correct across the
    /// automatic compaction (which reassigns ids, exactly as an explicit Trim does).
    ///
    /// The engine declares no <c>InternalsVisibleTo</c>, so - as elsewhere in this suite - the
    /// internal auto-trim threshold and the private snapshot size are reached by reflection rather
    /// than by widening visibility.
    /// </summary>
    [TestClass]
    public class MemoryFootprintTest
    {
        private ILoggerFactory _loggerFactory;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
        }

        private static void SetAutoTrimThreshold(Fallen8 fallen8, int threshold)
        {
            var field = typeof(Fallen8).GetField("_autoTrimTombstoneThreshold",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "The internal auto-trim threshold field must exist.");
            field.SetValue(fallen8, threshold);
        }

        /// <summary>
        /// Reads the number of live slots (Count) of the private master-store snapshot - the true
        /// size of the store, tombstones included. This is the quantity M4 must keep bounded.
        /// </summary>
        private static int SnapshotCount(Fallen8 fallen8)
        {
            var snapField = typeof(Fallen8).GetField("_snapshot",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(snapField, "The internal snapshot field must exist.");
            var snap = snapField.GetValue(fallen8);
            Assert.IsNotNull(snap, "The snapshot must never be null.");
            var countField = snap.GetType().GetField("Count", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(countField, "The snapshot Count field must exist.");
            return (int)countField.GetValue(snap);
        }

        private static List<int> CreateBatch(Fallen8 fallen8, int count)
        {
            var tx = new CreateVerticesTransaction();
            for (int i = 0; i < count; i++)
            {
                tx.AddVertex(1u, "churn", new Dictionary<string, object>
                {
                    { "seq", i },
                    { "name", "v" + i },
                    { "active", (i & 1) == 0 },
                });
            }
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            // Read the created ids BEFORE the following removal commits (after which M3 releases
            // the input and a later auto-trim may reassign ids).
            return tx.GetCreatedVertices().Select(v => v.Id).ToList();
        }

        [TestMethod]
        public void AutoTrim_UnderRepeatedAddRemoveChurn_KeepsStoreBounded()
        {
            var fallen8 = new Fallen8(_loggerFactory);

            const int threshold = 4000;
            const int batch = 1000;
            const int iterations = 40;
            SetAutoTrimThreshold(fallen8, threshold);

            long totalChurned = 0;
            int maxCount = 0;

            for (int it = 0; it < iterations; it++)
            {
                var ids = CreateBatch(fallen8, batch);
                totalChurned += ids.Count;

                // Peak store size for this iteration is observed right after the batch is appended.
                maxCount = Math.Max(maxCount, SnapshotCount(fallen8));

                var removeTx = new RemoveGraphElementsTransaction { GraphElementIds = ids };
                var removeInfo = fallen8.EnqueueTransaction(removeTx);
                removeInfo.WaitUntilFinished();

                // Assert on the held TransactionInformation, not GetTransactionState(txId): a
                // churn-triggered auto-compaction runs Trim, which (like an explicit TrimTransaction)
                // evicts finished transactions from the state map, so the by-id lookup may already
                // read NotExist. The caller's own handle still reports the terminal state.
                Assert.AreEqual(TransactionState.Finished, removeInfo.TransactionState,
                    "Each churn removal must commit cleanly.");
            }

            // Bounded growth: although far more elements were churned than the threshold, the store
            // never grew past roughly one threshold plus one in-flight batch. Without M4's auto-trim
            // the store would hold one tombstone per churned element (totalChurned slots).
            Assert.IsTrue(totalChurned > threshold * 4,
                "Sanity: the workload must churn far more than the threshold so the bound is meaningful.");
            Assert.IsTrue(maxCount <= threshold + batch,
                string.Format("The store must stay bounded (<= {0}); observed peak {1} against {2} churned elements.",
                    threshold + batch, maxCount, totalChurned));

            // And after churn, everything removed is gone and the store has been compacted well
            // below the total churned count.
            Assert.AreEqual(0, fallen8.VertexCount, "Every churned vertex was removed.");
            Assert.IsTrue(SnapshotCount(fallen8) <= threshold + batch,
                "The final store size must be bounded, not proportional to the total churned.");
        }

        [TestMethod]
        public void AutoTrim_AfterCompaction_GraphRemainsQueryableAndConsistent()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            SetAutoTrimThreshold(fallen8, 2000);

            // Churn enough to force at least one auto-compaction.
            for (int it = 0; it < 10; it++)
            {
                var ids = CreateBatch(fallen8, 500);
                var removeTx = new RemoveGraphElementsTransaction { GraphElementIds = ids };
                fallen8.EnqueueTransaction(removeTx).WaitUntilFinished();
            }

            Assert.AreEqual(0, fallen8.VertexCount, "All churned vertices removed.");
            Assert.IsTrue(SnapshotCount(fallen8) <= 2000 + 500, "Store bounded after churn.");

            // The graph must be fully usable after auto-compaction: create a small connected graph
            // and verify the elements resolve and adjacency is intact (ids were reassigned by the
            // compaction, exactly as an explicit Trim does).
            var vtx = new CreateVerticesTransaction();
            vtx.AddVertex(1u, "person", new Dictionary<string, object> { { "name", "Alice" } });
            vtx.AddVertex(1u, "person", new Dictionary<string, object> { { "name", "Bob" } });
            fallen8.EnqueueTransaction(vtx).WaitUntilFinished();
            var created = vtx.GetCreatedVertices();
            int aliceId = created[0].Id;
            int bobId = created[1].Id;

            var etx = new CreateEdgesTransaction();
            etx.AddEdge(aliceId, "knows", bobId, 1u, "knows");
            fallen8.EnqueueTransaction(etx).WaitUntilFinished();

            Assert.AreEqual(2, fallen8.VertexCount);
            Assert.AreEqual(1, fallen8.EdgeCount);

            VertexModel alice;
            Assert.IsTrue(fallen8.TryGetVertex(out alice, aliceId), "Alice must resolve after auto-compaction.");
            string name;
            Assert.IsTrue(alice.TryGetProperty(out name, "name"));
            Assert.AreEqual("Alice", name, "Property fidelity must survive auto-compaction.");
            Assert.AreEqual(1u, alice.GetOutDegree(), "Adjacency must survive auto-compaction.");
        }

        [TestMethod]
        public void AutoTrim_BelowThreshold_DoesNotReassignIds()
        {
            // With the conservative default threshold, a small number of removals must NOT trigger a
            // compaction: ids stay stable, so ordinary workloads see no surprising reassignment.
            var fallen8 = new Fallen8(_loggerFactory);

            var ids = CreateBatch(fallen8, 10);
            int survivorId = ids[ids.Count - 1];

            // Remove the first few; well under the (default, high) threshold.
            var removeTx = new RemoveGraphElementsTransaction { GraphElementIds = ids.Take(5).ToList() };
            fallen8.EnqueueTransaction(removeTx).WaitUntilFinished();

            Assert.AreEqual(5, fallen8.VertexCount, "Five of ten vertices remain.");

            // The surviving vertex keeps its original id (no auto-compaction happened).
            VertexModel survivor;
            Assert.IsTrue(fallen8.TryGetVertex(out survivor, survivorId),
                "A surviving vertex must keep its id when the tombstone count is below the threshold.");
            Assert.AreEqual(survivorId, survivor.Id);
        }
    }
}
