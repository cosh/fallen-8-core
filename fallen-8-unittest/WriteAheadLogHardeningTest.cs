// MIT License
//
// WriteAheadLogHardeningTest.cs
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
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Fault-injection tests for the crash-durability-hardening feature: a WAL append failure trips a
    /// sticky fence and marks every affected transaction non-durable without silently dropping
    /// committed work (D1); a failed load with the WAL enabled does not run an unlogged compaction that
    /// diverges the id space (D2); and an anchored WAL adopted at construction suspends logging until
    /// its paired snapshot is loaded (D3). Failures are injected purely through the public surface (the
    /// WAL file's read-only attribute; a non-existent load path; an anchored-then-reopened log).
    /// </summary>
    [TestClass]
    public class WriteAheadLogHardeningTest
    {
        private ILoggerFactory _loggerFactory;
        private string _tempDir;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
            _tempDir = Path.Combine(Path.GetTempPath(), "f8_walhard_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        [TestCleanup]
        public void TestCleanup()
        {
            try
            {
                if (_tempDir != null && Directory.Exists(_tempDir))
                {
                    foreach (var f in Directory.GetFiles(_tempDir, "*", SearchOption.AllDirectories))
                    {
                        try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                    }
                    Directory.Delete(_tempDir, true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }

        private string SavePath => Path.Combine(_tempDir, "savegame.f8s");
        private string WalPath => Path.Combine(_tempDir, "savegame.f8s.wal");

        private Fallen8 NewEngineWithWal() => new Fallen8(_loggerFactory, new WriteAheadLogOptions(WalPath));

        private static int AddVertex(Fallen8 fallen8, string name, out TransactionInformation info)
        {
            var tx = new CreateVerticesTransaction();
            tx.AddVertex(1u, "n", new Dictionary<string, object> { { "name", name } });
            info = fallen8.EnqueueTransaction(tx);
            info.WaitUntilFinished();
            return tx.GetCreatedVertices().Single().Id;
        }

        private static int AddVertex(Fallen8 fallen8, string name)
        {
            return AddVertex(fallen8, name, out _);
        }

        private static string Save(Fallen8 fallen8, string path)
        {
            var tx = new SaveTransaction { Path = path, SavePartitions = 1 };
            var info = fallen8.EnqueueTransaction(tx);
            info.WaitUntilFinished();
            Assert.AreEqual(TransactionState.Finished, info.TransactionState, "The save should finish.");
            return tx.ActualPath;
        }

        #region D1 - sticky WAL-failure fence

        [TestMethod]
        public void D1_AppendFailure_MarksNonDurable_AndNeverSilentlyDropsCommittedWork()
        {
            int v0, v1;
            using (var fallen8 = NewEngineWithWal())
            {
                v0 = AddVertex(fallen8, "v0");
                v1 = AddVertex(fallen8, "v1");

                // Trip the fence: make the WAL file read-only so the next append's open throws.
                File.SetAttributes(WalPath, FileAttributes.ReadOnly);

                var v2Info = default(TransactionInformation);
                AddVertex(fallen8, "v2", out v2Info);
                Assert.AreEqual(TransactionState.Finished, v2Info.TransactionState, "The transaction is still applied in memory.");
                Assert.IsFalse(v2Info.Durable, "A commit whose WAL append failed must be reported non-durable.");

                var v3Info = default(TransactionInformation);
                AddVertex(fallen8, "v3", out v3Info);
                Assert.IsFalse(v3Info.Durable, "The fence is sticky: every commit after the failure is non-durable, not only the first.");

                Assert.AreEqual(4, fallen8.VertexCount, "All four vertices are applied in memory regardless of WAL durability.");

                File.SetAttributes(WalPath, FileAttributes.Normal); // the disk 'recovers'
            }

            // Simulated crash + reopen: only the durable (pre-failure) commits replay. The non-durable
            // ones were never written - and the caller was told so - so nothing is silently dropped.
            using (var recovered = NewEngineWithWal())
            {
                Assert.AreEqual(2, recovered.VertexCount,
                    "Only the two durable commits replay; the post-failure commits were reported non-durable and were never written.");
                Assert.IsTrue(recovered.TryGetVertex(out _, v0));
                Assert.IsTrue(recovered.TryGetVertex(out _, v1));
            }
        }

        [TestMethod]
        public void D1_SuccessfulSave_ClearsTheFailureFence()
        {
            using var fallen8 = NewEngineWithWal();
            AddVertex(fallen8, "v0");

            File.SetAttributes(WalPath, FileAttributes.ReadOnly);
            var failedInfo = default(TransactionInformation);
            AddVertex(fallen8, "v1", out failedInfo);
            Assert.IsFalse(failedInfo.Durable, "The append failure tripped the fence.");
            File.SetAttributes(WalPath, FileAttributes.Normal);

            // A successful Save re-baselines the log and clears the fence.
            Save(fallen8, SavePath);

            var afterSaveInfo = default(TransactionInformation);
            AddVertex(fallen8, "v2", out afterSaveInfo);
            Assert.IsTrue(afterSaveInfo.Durable, "A successful Save must clear the failure fence so later commits are durable again.");
        }

        #endregion

        #region D2 - failed load must not run an unlogged compaction

        [TestMethod]
        public void D2_FailedLoad_WithWalEnabled_DoesNotDivergeTheIdSpace()
        {
            int liveLatecomerId;
            string latecomerName = "after-failed-load";

            using (var fallen8 = NewEngineWithWal())
            {
                // Create three vertices, then remove the middle one, leaving a tombstone at its id.
                var a = AddVertex(fallen8, "a");
                var b = AddVertex(fallen8, "b");
                AddVertex(fallen8, "c");
                fallen8.EnqueueTransaction(new RemoveGraphElementsTransaction { GraphElementIds = new List<int> { b } })
                    .WaitUntilFinished();

                // Trigger a FAILED load (a non-existent path returns false -> the else branch). Before
                // the D2 fix this ran an UNLOGGED Trim_internal, reassigning the surviving ids so a
                // later logged create landed on a different id than replay would reconstruct.
                var loadInfo = fallen8.EnqueueTransaction(new LoadTransaction { Path = Path.Combine(_tempDir, "does-not-exist.f8s") });
                loadInfo.WaitUntilFinished();

                // Commit a vertex AFTER the failed load and capture its live id.
                liveLatecomerId = AddVertex(fallen8, latecomerName);
                _ = a; // (a stays live; not otherwise needed)
            }

            // Simulated crash + reopen: replay must reconstruct the EXACT live id space. The latecomer
            // must resolve by its live id to itself - which fails if a phantom unlogged trim diverged
            // the ids on the original run.
            using (var recovered = NewEngineWithWal())
            {
                Assert.IsTrue(recovered.TryGetVertex(out var latecomer, liveLatecomerId),
                    "The vertex committed after the failed load must resolve by its LIVE id after replay (no phantom-trim divergence).");
                Assert.IsTrue(latecomer.TryGetProperty(out string name, "name"));
                Assert.AreEqual(latecomerName, name, "The id must resolve to the SAME vertex, not a shifted neighbour.");
            }
        }

        #endregion

        #region D3 - anchored WAL awaiting its paired load

        [TestMethod]
        public void D3_MutatingBeforePairedLoad_IsNonDurable_AndNotReplayedAsPhantomState()
        {
            // Engine A: create a vertex and Save, which anchors the WAL to the snapshot and resets it.
            string snapshotPath;
            using (var a = NewEngineWithWal())
            {
                AddVertex(a, "original");
                snapshotPath = Save(a, SavePath);
            }

            // Engine B reopens the SAME (now anchored, empty) WAL WITHOUT loading the snapshot first.
            using (var b = NewEngineWithWal())
            {
                Assert.AreEqual(0, b.VertexCount, "The reopened engine starts empty until the paired snapshot is loaded.");

                // Mutating before the paired Load must be reported non-durable (logging is suspended so
                // it is not recorded against the wrong baseline).
                var preLoadInfo = default(TransactionInformation);
                AddVertex(b, "pre-load-ghost", out preLoadInfo);
                Assert.AreEqual(TransactionState.Finished, preLoadInfo.TransactionState, "It is applied in memory.");
                Assert.IsFalse(preLoadInfo.Durable, "A mutation before the paired snapshot Load must be reported non-durable (D3).");

                // Load the paired snapshot: the replayed state is exactly snapshot + its own (empty)
                // log; the pre-load ghost is gone (it was never part of any consistent state).
                var loadInfo = b.EnqueueTransaction(new LoadTransaction { Path = snapshotPath });
                loadInfo.WaitUntilFinished();
                Assert.AreEqual(TransactionState.Finished, loadInfo.TransactionState);
                Assert.AreEqual(1, b.VertexCount, "After the paired Load the state is exactly the snapshot; the pre-load ghost is not present.");
            }
        }

        #endregion
    }
}
