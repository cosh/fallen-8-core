// MIT License
//
// DurabilitySignalTest.cs
//
// Copyright (c) 2011-2026 Henning Rauch
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

using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Persistency;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The durability and recovery-integrity signal (feature platform-integrity-audit W5).
    ///
    ///   <para>Every fact in <see cref="DurabilityState" /> was already computed by the engine and
    ///   reachable nowhere outside it: the degraded-log state existed only as an OpenTelemetry gauge, so
    ///   it existed only if the operator had wired a collector, and a truncated recovery logged one
    ///   error and became an activity tag. A client could therefore write into a degraded log, receive
    ///   success for every write, and lose all of them on the next kill; and after a truncated replay it
    ///   would reconcile against a silent prefix of history.</para>
    ///
    ///   <para>This matters to a CLIENT and not only an operator because a writer that deletes state
    ///   because "nothing asserts it any more" reads that conclusion out of graph content. On truncated
    ///   history the conclusion is wrong and the deletion is the one mutation re-syncing cannot undo.</para>
    /// </summary>
    [TestClass]
    public class DurabilitySignalTest
    {
        private ILoggerFactory _loggerFactory;
        private TempDirectory _temp;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
            _temp = new TempDirectory("f8_w5_");
        }

        [TestCleanup]
        public void TestCleanup()
        {
            _temp?.Dispose();
        }

        private string WalPath => Path.Combine(_temp.FullName, "wal.f8log");

        private static int AddVertex(Fallen8 engine)
        {
            var tx = new CreateVerticesTransaction();
            tx.AddVertex(new VertexDefinition { Label = "device", CreationDate = 0 });
            engine.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.GetCreatedVertices().Single().Id;
        }

        [TestMethod]
        public void WithoutAWal_DurabilityIsNotDegraded_ItIsSimplyAbsent()
        {
            // The distinction the walEnabled flag exists for: no log is the documented volatile posture,
            // not a fault. Reporting it as "degraded" would cry wolf on every default dev run.
            using var engine = new Fallen8(_loggerFactory);

            var state = engine.Durability;

            Assert.IsFalse(state.WalEnabled, "no WAL is configured");
            Assert.IsFalse(state.Degraded, "absent is not degraded");
            Assert.IsFalse(state.RecoveryRan);
        }

        [TestMethod]
        public void WithAWal_AHealthyEngineReportsDurableAndUntruncated()
        {
            using var engine = new Fallen8(_loggerFactory, new WriteAheadLogOptions(WalPath));
            AddVertex(engine);

            var state = engine.Durability;

            Assert.IsTrue(state.WalEnabled);
            Assert.IsFalse(state.Degraded, "a healthy log is not degraded");
            Assert.IsFalse(state.LastRecoveryTruncated);
            Assert.AreEqual(0, state.LastCheckpointDroppedIndices);
        }

        [TestMethod]
        public void AFreshLogsRecoveryFlags_MeanWhatTheyClaim()
        {
            // Pins the MEANING of recoveryRan rather than assuming it. The replay path runs when a log
            // is unanchored at construction, which a brand-new empty log also is - so this asserts what
            // a first start actually reports, and that whatever it reports leads a client to the right
            // conclusion. The safety-relevant field is lastRecoveryTruncated: it must be false, and the
            // replayed count must be 0, so "nothing was lost" is the only reading available.
            using var engine = new Fallen8(_loggerFactory, new WriteAheadLogOptions(WalPath));

            var state = engine.Durability;

            Assert.IsFalse(state.LastRecoveryTruncated, "a fresh log has lost nothing");
            Assert.AreEqual(0, state.LastRecoveryReplayedEntries, "and replayed nothing");
        }

        [TestMethod]
        public void ACommittedTransactionReportsItsOwnDurability()
        {
            // The per-transaction half of the contract (transaction-retention R3), which the engine
            // already had: a caller that waits can see whether its own write reached the log. The block
            // on /status is the instance-level companion to this, not a replacement.
            using var engine = new Fallen8(_loggerFactory, new WriteAheadLogOptions(WalPath));

            var tx = new CreateVerticesTransaction();
            tx.AddVertex(new VertexDefinition { Label = "device", CreationDate = 0 });
            var info = engine.EnqueueTransaction(tx);
            info.WaitUntilFinished();

            Assert.AreEqual(TransactionState.Finished, info.TransactionState);
            Assert.IsTrue(info.Durable, "a healthy log commits durably");
            Assert.IsFalse(info.DurabilityDegraded);
            Assert.IsNull(info.Error, "durability is signalled on Durable, never on Error");
        }

        [TestMethod]
        public void RecoveryReportsHowMuchItReplayed_AndThatItWasNotTruncated()
        {
            // A clean replay: the count is populated and truncated stays false, so a client can tell
            // "recovery ran and was complete" from "recovery ran and stopped early".
            var snapshot = Path.Combine(_temp.FullName, "snapshot.f8s");
            string actualPath;
            using (var producer = new Fallen8(_loggerFactory, new WriteAheadLogOptions(WalPath)))
            {
                AddVertex(producer);
                var save = new SaveTransaction { Path = snapshot };
                producer.EnqueueTransaction(save).WaitUntilFinished();
                actualPath = save.ActualPath;

                // Post-snapshot, WAL-only work for recovery to replay.
                AddVertex(producer);
                AddVertex(producer);
            }

            using var recovered = new Fallen8(_loggerFactory, new WriteAheadLogOptions(WalPath));
            var info = recovered.EnqueueTransaction(new LoadTransaction { Path = actualPath });
            info.WaitUntilFinished();
            Assert.AreEqual(TransactionState.Finished, info.TransactionState, "load failed: " + info.Error);

            var state = recovered.Durability;
            Assert.IsTrue(state.RecoveryRan, "a recovery ran, so the recovery fields carry information");
            Assert.IsFalse(state.LastRecoveryTruncated, "a clean replay is not truncated");
            Assert.AreEqual(2, state.LastRecoveryReplayedEntries, "both post-snapshot entries replayed");
            Assert.AreEqual(3, recovered.VertexCount);
        }

        [TestMethod]
        public void ATornTail_IsDiscarded_WithoutReportingTruncatedHistory()
        {
            // A torn final entry is what a crash mid-append leaves. It was never acknowledged, so the
            // recovered graph is the whole committed history: the flag must stay false, or a client
            // reconciling against it would refuse a safe delete after every ordinary crash. The flag's
            // true case (acknowledged entries that could not be replayed) is pinned in
            // WriteAheadLogTest's "commits after a recovery" region.
            var snapshot = Path.Combine(_temp.FullName, "snapshot.f8s");
            string actualPath;
            using (var producer = new Fallen8(_loggerFactory, new WriteAheadLogOptions(WalPath)))
            {
                AddVertex(producer);
                var save = new SaveTransaction { Path = snapshot };
                producer.EnqueueTransaction(save).WaitUntilFinished();
                actualPath = save.ActualPath;
                AddVertex(producer);
            }

            // Truncate the log mid-entry: the torn tail is dropped by the reader, so recovery replays
            // fewer entries, and the count reflects what was actually applied.
            Assert.IsTrue(WalFile.Length(WalPath) > 8, "the log holds at least one entry");
            WalFile.CutTo(WalPath, WalFile.Length(WalPath) - 4);

            using var recovered = new Fallen8(_loggerFactory, new WriteAheadLogOptions(WalPath));
            var info = recovered.EnqueueTransaction(new LoadTransaction { Path = actualPath });
            info.WaitUntilFinished();

            Assert.AreEqual(TransactionState.Finished, info.TransactionState,
                "a torn tail is recovered from, not fatal: " + info.Error);
            var state = recovered.Durability;
            Assert.IsTrue(state.RecoveryRan);
            Assert.AreEqual(0, state.LastRecoveryReplayedEntries,
                "the torn entry was not applied, and the count says so rather than implying it was");
            Assert.IsFalse(state.LastRecoveryTruncated, "a torn entry was never acknowledged, so no history is missing");
            Assert.IsFalse(state.Degraded, "a torn tail is cut, not fenced");
            Assert.AreEqual(1, recovered.VertexCount, "only the snapshot's vertex is present");
        }

        [TestMethod]
        public void TheDroppedIndexCountIsZero_WhenEveryIndexPersisted()
        {
            // The W3-to-W5 move: a checkpoint that drops a failed index stays deliberate (aborting would
            // trade a lost index for a lost checkpoint), so what was missing is this SIGNAL. The happy
            // path must read zero, or the signal is noise.
            using var engine = new Fallen8(_loggerFactory);
            AddVertex(engine);
            Assert.IsTrue(engine.IndexFactory.TryCreateIndex(out _, "byName"), "index creation");

            var save = new SaveTransaction { Path = Path.Combine(_temp.FullName, "clean.f8s") };
            engine.EnqueueTransaction(save).WaitUntilFinished();

            Assert.AreEqual(0, engine.Durability.LastCheckpointDroppedIndices,
                "every index persisted, so nothing was dropped");
        }
    }
}
