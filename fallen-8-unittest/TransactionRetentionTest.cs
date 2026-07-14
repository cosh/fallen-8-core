// MIT License
//
// TransactionRetentionTest.cs
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
using System.IO;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Persistency;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Tests for the "transaction-retention" feature:
    ///   R1 — terminal-transaction bookkeeping is bounded on an insert-only (no-removal) workload, so it
    ///        does not leak; a recent id still resolves, a long-superseded one reads NotExist;
    ///   R2 — GetCreatedVertices()/GetCreatedEdges() never throw because an unrelated trim ran, and now
    ///        return the ACTUAL created models (not empty) after a trim;
    ///   R3 — a committed-but-non-durable transaction is Finished + DurabilityDegraded + Error == null,
    ///        distinct from a faulted one (RolledBack + Error != null + not degraded);
    ///   F14 — the transaction id round-trips through GetTransactionState (held as a Guid, stringified
    ///        on demand), and an unparseable/unknown id reads NotExist.
    /// </summary>
    [TestClass]
    public class TransactionRetentionTest
    {
        private ILoggerFactory _loggerFactory;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
        }

        #region reflection helpers (the engine declares no InternalsVisibleTo)

        private static object GetTxManager(Fallen8 fallen8)
        {
            return typeof(Fallen8).GetField("_txManager", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(fallen8);
        }

        private static void SetMaxRetained(object txManager, int value)
        {
            txManager.GetType()
                .GetProperty("MaxRetainedTerminalTransactions", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                .SetValue(txManager, value);
        }

        private static int TransactionStateCount(object txManager)
        {
            var dict = (IDictionary)txManager.GetType()
                .GetField("transactionState", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(txManager);
            return dict.Count;
        }

        #endregion

        // ---- R1 bounded retention ------------------------------------------------------------------

        [TestMethod]
        public void InsertOnlyWorkload_BoundsTerminalRetention()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var txManager = GetTxManager(fallen8);
            SetMaxRetained(txManager, 50); // lower the bound so the test stays small

            // The FIRST transaction's id — after we blow past the bound it must be evicted.
            var firstTx = new CreateVerticesTransaction();
            firstTx.AddVertex(1u, "v");
            fallen8.EnqueueTransaction(firstTx).WaitUntilFinished();
            var firstId = firstTx.TransactionId;

            // Sustained inserts, NO removals (so nothing auto-trims): the only thing bounding the
            // bookkeeping is the retention FIFO.
            TransactionInformation recent = null;
            string recentId = null;
            for (var i = 0; i < 500; i++)
            {
                var tx = new CreateVerticesTransaction();
                tx.AddVertex(1u, "v");
                recent = fallen8.EnqueueTransaction(tx);
                recent.WaitUntilFinished();
                recentId = tx.TransactionId;
            }

            var count = TransactionStateCount(txManager);
            Assert.IsTrue(count <= 55,
                $"Terminal retention must stay bounded (was {count}, bound 50) — the insert-only workload must not leak.");

            // A recent transaction still resolves for polling; the long-superseded first one is gone.
            Assert.AreEqual(TransactionState.Finished, fallen8.GetTransactionState(recentId),
                "A recent transaction must still resolve by id.");
            Assert.AreEqual(TransactionState.NotExist, fallen8.GetTransactionState(firstId),
                "A long-superseded transaction id must read NotExist (evicted), like a trimmed id.");

            fallen8.Dispose();
        }

        // ---- R2 handles never throw ----------------------------------------------------------------

        [TestMethod]
        public void GetCreatedVertices_AfterAnUnrelatedTrim_ReturnsTheActualModels()
        {
            var fallen8 = new Fallen8(_loggerFactory);

            var tx = new CreateVerticesTransaction();
            tx.AddVertex(1u, "v");
            tx.AddVertex(1u, "v");
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();

            // A foreign trim runs between the wait and the read (the exact race a caller cannot avoid).
            fallen8.EnqueueTransaction(new TrimTransaction()).WaitUntilFinished();

            var created = tx.GetCreatedVertices();
            Assert.IsNotNull(created, "GetCreatedVertices must never return null.");
            Assert.AreEqual(2, created.Count,
                "After a trim, GetCreatedVertices must return the ACTUAL created models (Cleanup no longer nulls them), never throw.");

            fallen8.Dispose();
        }

        [TestMethod]
        public void GetCreatedEdges_AfterAnUnrelatedTrim_ReturnsTheActualModels()
        {
            var fallen8 = new Fallen8(_loggerFactory);

            var vtx = new CreateVerticesTransaction();
            vtx.AddVertex(1u, "v");
            vtx.AddVertex(1u, "v");
            fallen8.EnqueueTransaction(vtx).WaitUntilFinished();
            var v = vtx.GetCreatedVertices();

            var etx = new CreateEdgesTransaction();
            etx.AddEdge(v[0].Id, "e", v[1].Id, 1u);
            fallen8.EnqueueTransaction(etx).WaitUntilFinished();

            fallen8.EnqueueTransaction(new TrimTransaction()).WaitUntilFinished();

            var created = etx.GetCreatedEdges();
            Assert.IsNotNull(created);
            Assert.AreEqual(1, created.Count,
                "After a trim, GetCreatedEdges must return the ACTUAL created edge, never throw.");

            fallen8.Dispose();
        }

        [TestMethod]
        public void GetCreatedVertices_WhenBackingListIsNull_ReturnsEmptyNotThrow()
        {
            // The null-safe guardrail: even if the captured list is dropped (a legacy Cleanup, or a
            // future code path), the accessor must read empty rather than throw ArgumentNullException.
            var tx = new CreateVerticesTransaction();
            typeof(CreateVerticesTransaction)
                .GetField("_verticesCreated", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(tx, null);

            var result = tx.GetCreatedVertices();
            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Count);
        }

        // ---- R3 durability distinguishable ---------------------------------------------------------

        [TestMethod]
        public void DurabilityDegraded_DistinguishesCommittedNonDurableFromFaulted()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "f8_txretention_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var walPath = Path.Combine(tempDir, "savegame.f8s.wal");
            try
            {
                using var fallen8 = new Fallen8(_loggerFactory, new WriteAheadLogOptions(walPath));

                // A normal durable commit: Finished, not degraded, no error.
                var okTx = new CreateVerticesTransaction();
                okTx.AddVertex(1u, "v");
                var okInfo = fallen8.EnqueueTransaction(okTx);
                okInfo.WaitUntilFinished();
                Assert.AreEqual(TransactionState.Finished, okInfo.TransactionState);
                Assert.IsTrue(okInfo.Durable);
                Assert.IsFalse(okInfo.DurabilityDegraded);
                Assert.IsNull(okInfo.Error);

                // Trip the WAL fence so the append fails: committed in memory, durability degraded, but
                // NOT a fault — the failure is on DurabilityDegraded, never on Error.
                File.SetAttributes(walPath, FileAttributes.ReadOnly);
                var degradedTx = new CreateVerticesTransaction();
                degradedTx.AddVertex(1u, "v");
                var degradedInfo = fallen8.EnqueueTransaction(degradedTx);
                degradedInfo.WaitUntilFinished();
                File.SetAttributes(walPath, FileAttributes.Normal);

                Assert.AreEqual(TransactionState.Finished, degradedInfo.TransactionState,
                    "A WAL-append failure still commits the transaction in memory.");
                Assert.IsTrue(degradedInfo.DurabilityDegraded, "The committed-but-non-durable transaction must be flagged degraded.");
                Assert.IsNull(degradedInfo.Error, "A WAL-append failure must NOT be recorded on Error (Error means execution faulted).");

                // A genuinely faulted transaction: rolled back, with an Error, and NOT degraded.
                var faultInfo = fallen8.EnqueueTransaction(new RemoveGraphElementsTransaction
                {
                    GraphElementIds = new List<int> { int.MaxValue }
                });
                faultInfo.WaitUntilFinished();
                Assert.AreEqual(TransactionState.RolledBack, faultInfo.TransactionState);
                Assert.IsNotNull(faultInfo.Error, "A genuine execution fault must be recorded on Error.");
                Assert.IsFalse(faultInfo.DurabilityDegraded, "A rolled-back transaction is not a durability-degraded commit.");
            }
            finally
            {
                try
                {
                    foreach (var f in Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories))
                    {
                        try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                    }
                    Directory.Delete(tempDir, true);
                }
                catch { }
            }
        }

        // ---- F14 id round-trip ---------------------------------------------------------------------

        [TestMethod]
        public void TransactionId_RoundTripsThroughGetTransactionState()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var txManager = GetTxManager(fallen8);
            SetMaxRetained(txManager, 1000); // keep the id resolvable for the assertion

            var tx = new CreateVerticesTransaction();
            tx.AddVertex(1u, "v");
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();

            var id = tx.TransactionId;
            Assert.IsTrue(Guid.TryParse(id, out _), "TransactionId must be a parseable GUID string.");
            Assert.AreEqual(TransactionState.Finished, fallen8.GetTransactionState(id),
                "A valid recent id must resolve to its terminal state.");
            Assert.AreEqual(TransactionState.NotExist, fallen8.GetTransactionState("not-a-guid"),
                "An unparseable id must read NotExist, not throw.");
            Assert.AreEqual(TransactionState.NotExist, fallen8.GetTransactionState(Guid.NewGuid().ToString()),
                "An unknown (but valid) guid must read NotExist.");

            fallen8.Dispose();
        }
    }
}
