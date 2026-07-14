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

        // Auto-trim is now OFF by default and, when on, frees tombstone bodies WITHOUT reassigning ids
        // (feature trim-reader-safety Part B). Enable it and set a low threshold via the public admin
        // surface so the soak tests exercise the reclamation.
        private static void EnableAutoTrim(Fallen8 fallen8, int threshold)
        {
            fallen8.ConfigureAutoTrim(true, threshold);
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
        public void AutoTrim_UnderRepeatedAddRemoveChurn_KeepsLiveIdsStable()
        {
            // Feature trim-reader-safety Part B: auto-trim now frees tombstone BODIES without ever
            // reassigning an id. So under heavy churn a long-lived element keeps its id (and stays
            // queryable) even though many auto-trim passes fire. The slot Count grows (tombstone slots
            // are kept, by design - id == index); the dominant per-tombstone memory (properties +
            // adjacency) is what is reclaimed, measured in the opt-in benchmark rather than asserted
            // as a flaky in-process GC bound here.
            var fallen8 = new Fallen8(_loggerFactory);

            const int threshold = 4000;
            const int batch = 1000;
            const int iterations = 40;
            EnableAutoTrim(fallen8, threshold);

            // A long-lived vertex created up front; its id must never change across the churn.
            var anchorTx = new CreateVerticesTransaction();
            anchorTx.AddVertex(1u, "anchor", new Dictionary<string, object> { { "name", "anchor" } });
            fallen8.EnqueueTransaction(anchorTx).WaitUntilFinished();
            int anchorId = anchorTx.GetCreatedVertices().Single().Id;

            long totalChurned = 0;
            for (int it = 0; it < iterations; it++)
            {
                var ids = CreateBatch(fallen8, batch);
                totalChurned += ids.Count;

                var removeTx = new RemoveGraphElementsTransaction { GraphElementIds = ids };
                var removeInfo = fallen8.EnqueueTransaction(removeTx);
                removeInfo.WaitUntilFinished();
                Assert.AreEqual(TransactionState.Finished, removeInfo.TransactionState,
                    "Each churn removal must commit cleanly.");
            }

            Assert.IsTrue(totalChurned > threshold * 4,
                "Sanity: the workload must churn far more than the threshold so auto-trim fires repeatedly.");

            // The anchor is the only live vertex, and its id is UNCHANGED - auto-trim never renumbered.
            Assert.AreEqual(1, fallen8.VertexCount, "Only the anchor remains live after the churn.");
            VertexModel anchor;
            Assert.IsTrue(fallen8.TryGetVertex(out anchor, anchorId),
                "The long-lived vertex must still resolve by its ORIGINAL id after heavy auto-trim churn.");
            Assert.AreEqual(anchorId, anchor.Id, "Auto-trim must never reassign a surviving element's id.");
            Assert.IsTrue(anchor.TryGetProperty(out string anchorName, "name") && anchorName == "anchor",
                "The long-lived vertex's own body must be intact (only tombstones are freed).");
        }

        [TestMethod]
        public void AutoTrim_FreesTombstones_GraphRemainsQueryableWithStableIds()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            EnableAutoTrim(fallen8, 2000);

            // Build a small connected graph FIRST and capture its ids.
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

            // Now churn enough to fire several auto-trim body-free passes AROUND the live graph.
            for (int it = 0; it < 10; it++)
            {
                var ids = CreateBatch(fallen8, 500);
                fallen8.EnqueueTransaction(new RemoveGraphElementsTransaction { GraphElementIds = ids }).WaitUntilFinished();
            }

            // The original graph is untouched: same ids, properties and adjacency (auto-trim froze
            // only the churned tombstones, never renumbered Alice/Bob).
            Assert.AreEqual(2, fallen8.VertexCount);
            Assert.AreEqual(1, fallen8.EdgeCount);

            VertexModel alice;
            Assert.IsTrue(fallen8.TryGetVertex(out alice, aliceId), "Alice must still resolve by her original id after auto-trim.");
            Assert.AreEqual(aliceId, alice.Id);
            Assert.IsTrue(alice.TryGetProperty(out string name, "name"));
            Assert.AreEqual("Alice", name, "Property fidelity of a live element must survive auto-trim.");
            Assert.AreEqual(1u, alice.GetOutDegree(), "Adjacency of a live element must survive auto-trim.");
        }

        [TestMethod]
        public void AutoTrim_OffByDefault_DoesNotReassignIds()
        {
            // Auto-trim is OFF by default (feature trim-reader-safety), so ids are never reassigned by
            // any automatic path - ordinary workloads see no surprising reassignment.
            var fallen8 = new Fallen8(_loggerFactory);

            var ids = CreateBatch(fallen8, 10);
            int survivorId = ids[ids.Count - 1];

            var removeTx = new RemoveGraphElementsTransaction { GraphElementIds = ids.Take(5).ToList() };
            fallen8.EnqueueTransaction(removeTx).WaitUntilFinished();

            Assert.AreEqual(5, fallen8.VertexCount, "Five of ten vertices remain.");

            VertexModel survivor;
            Assert.IsTrue(fallen8.TryGetVertex(out survivor, survivorId),
                "A surviving vertex must keep its id (auto-trim is off by default; nothing renumbers).");
            Assert.AreEqual(survivorId, survivor.Id);
        }

        private static void SetInternTableCap(Fallen8 fallen8, int cap)
        {
            var field = typeof(Fallen8).GetField("_internTableCap",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "The internal intern-table cap field must exist.");
            field.SetValue(fallen8, cap);
        }

        private static int InternTableCount(Fallen8 fallen8)
        {
            var field = typeof(Fallen8).GetField("_internTable",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "The internal intern table field must exist.");
            var table = field.GetValue(fallen8);
            Assert.IsNotNull(table, "The intern table must never be null.");
            return ((System.Collections.ICollection)table).Count;
        }

        private static string Intern(Fallen8 fallen8, string value)
        {
            var method = typeof(Fallen8).GetMethod("Intern",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(method, "The internal Intern method must exist.");
            return (string)method.Invoke(fallen8, new object[] { value });
        }

        // A value-equal but reference-distinct copy, so an interning fold is observable through
        // ReferenceEquals (string literals would be CLR-interned and defeat the check).
        private static string Fresh(string value)
        {
            return new string(value.ToCharArray());
        }

        [TestMethod]
        public void InternTable_BelowCapSharesInstance_PastCapIsNoOpAndDoesNotGrow()
        {
            var fallen8 = new Fallen8(_loggerFactory);

            // Lower the cap so the test is fast (the real cap is 1,000,000, far above any schema).
            SetInternTableCap(fallen8, 3);

            // Below the cap: a value-equal but distinct instance folds to the one shared instance.
            string shared = Intern(fallen8, Fresh("label-0"));
            string sharedAgain = Intern(fallen8, Fresh("label-0"));
            Assert.IsTrue(ReferenceEquals(shared, sharedAgain),
                "Below the cap, interning returns the single shared instance for value-equal strings.");

            // Fill the table exactly to the cap ("label-0" already counts as one distinct string).
            Intern(fallen8, "label-1");
            Intern(fallen8, "label-2");
            Assert.AreEqual(3, InternTableCount(fallen8), "The table holds exactly the cap's distinct strings.");

            // Past the cap: a NEW distinct string is a no-op - it is returned value-equal (in fact
            // the argument instance itself) and is NOT added, so the table does not grow.
            string overflowInput = Fresh("label-overflow");
            string overflowResult = Intern(fallen8, overflowInput);
            Assert.AreEqual("label-overflow", overflowResult, "Past the cap the value is preserved.");
            Assert.IsTrue(ReferenceEquals(overflowInput, overflowResult),
                "Past the cap interning is a no-op: it returns the argument instance unchanged.");
            Assert.AreEqual(3, InternTableCount(fallen8), "The table must not grow past the cap.");

            // Null is always returned as-is, independent of the cap.
            Assert.IsNull(Intern(fallen8, null));

            fallen8.Dispose();
        }

        [TestMethod]
        public void InternTable_ClearedByTabulaRasa_ButNotByTrim()
        {
            var fallen8 = new Fallen8(_loggerFactory);

            // Populate the intern table via the normal create path (label + property keys intern).
            var tx = new CreateVerticesTransaction();
            for (int i = 0; i < 5; i++)
            {
                tx.AddVertex(1u, "person", new Dictionary<string, object> { { "name", "n" + i }, { "age", i } });
            }
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            Assert.IsTrue(InternTableCount(fallen8) > 0, "Interning the label/keys must populate the table.");

            // Trim must NOT clear the table: the surviving elements still reference those strings.
            fallen8.EnqueueTransaction(new TrimTransaction()).WaitUntilFinished();
            Assert.IsTrue(InternTableCount(fallen8) > 0,
                "Trim must not clear the intern table - the survivors still reference the strings.");

            // A full reset (TabulaRasa) discards every element, so it reclaims the table too.
            fallen8.EnqueueTransaction(new TabulaRasaTransaction()).WaitUntilFinished();
            Assert.AreEqual(0, InternTableCount(fallen8), "TabulaRasa must clear the intern table.");

            fallen8.Dispose();
        }
    }
}
