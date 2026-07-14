// MIT License
//
// PluginWriteTransactionsTest.cs
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
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Persistency;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Tests for the "plugin-write-transactions" feature (mode (a)): a public <see cref="DelegateTransaction"/>
    /// lets a plugin run a composed mutation on the single writer thread via <see cref="IFallen8WriterContext"/>,
    /// with a rolled-back body leaving no create/property effect, and durability via the snapshot (not the WAL).
    /// Mode (b) (WAL-loggable descriptor + replay) is deferred.
    /// </summary>
    [TestClass]
    public class PluginWriteTransactionsTest
    {
        private ILoggerFactory _loggerFactory;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
        }

        [TestMethod]
        public void DelegateTransaction_ComposedMutation_IsVisibleAfterCommit_AndRunsOnTheWriter()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            string bodyThreadName = null;

            var tx = new DelegateTransaction(ctx =>
            {
                bodyThreadName = Thread.CurrentThread.Name;
                var a = ctx.CreateVertex(1u, "person", new Dictionary<string, object> { { "name", "alice" } });
                var b = ctx.CreateVertex(1u, "person");
                Assert.IsTrue(ctx.TryCreateEdge(out var edge, a.Id, "knows", b.Id, 1u, "knows"));
                Assert.IsNotNull(edge);
                ctx.SetProperty(b.Id, "role", "friend");
            }, "compose-alice-bob");

            var info = fallen8.EnqueueTransaction(tx);
            info.WaitUntilFinished();

            Assert.AreEqual(TransactionState.Finished, info.TransactionState, "The composed mutation must commit. " + info.Error);
            Assert.IsTrue(info.Durable, "A DelegateTransaction is snapshot-durable (mode a), reported Durable.");

            // The body ran on the single transaction-writer thread, never the test thread.
            Assert.AreEqual("Fallen8-Transaction-Writer", bodyThreadName,
                "The delegate body must run on the single writer thread.");

            // The composed effect is visible to lock-free readers.
            Assert.AreEqual(2, fallen8.VertexCount);
            Assert.AreEqual(1, fallen8.EdgeCount);

            fallen8.Dispose();
        }

        [TestMethod]
        public void DelegateTransaction_ThrowingBody_RollsBackWithNoCreateOrPropertyEffect()
        {
            var fallen8 = new Fallen8(_loggerFactory);

            // A pre-existing vertex with a property the body will (transiently) change.
            var seed = new CreateVerticesTransaction();
            seed.AddVertex(1u, "person", new Dictionary<string, object> { { "role", "original" } });
            fallen8.EnqueueTransaction(seed).WaitUntilFinished();
            var existing = seed.GetCreatedVertices()[0];

            var beforeVertexCount = fallen8.VertexCount;

            var tx = new DelegateTransaction(ctx =>
            {
                ctx.CreateVertex(1u, "person");                 // will be undone
                ctx.SetProperty(existing.Id, "role", "changed"); // will be restored
                throw new InvalidOperationException("plugin body failure");
            });

            var info = fallen8.EnqueueTransaction(tx);
            info.WaitUntilFinished();

            Assert.AreEqual(TransactionState.RolledBack, info.TransactionState, "A throwing body must roll back.");
            Assert.IsNotNull(info.Error, "The escaped exception must be recorded on Error.");
            Assert.AreEqual(TransactionFailureReason.InternalError, info.FailureReason);

            // No observable effect: the created vertex is gone and the property is restored.
            Assert.AreEqual(beforeVertexCount, fallen8.VertexCount, "The created vertex must be removed on rollback.");
            Assert.IsTrue(existing.TryGetProperty(out string role, "role"));
            Assert.AreEqual("original", role, "The property change must be reverted on rollback.");

            // The single writer survived: a subsequent transaction still commits.
            var after = new CreateVerticesTransaction();
            after.AddVertex(1u, "person");
            var afterInfo = fallen8.EnqueueTransaction(after);
            afterInfo.WaitUntilFinished();
            Assert.AreEqual(TransactionState.Finished, afterInfo.TransactionState, "The writer must survive a faulting delegate body.");

            fallen8.Dispose();
        }

        [TestMethod]
        public void DelegateWriterContext_UsedAfterTheBody_Throws()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            IFallen8WriterContext captured = null;

            var tx = new DelegateTransaction(ctx =>
            {
                captured = ctx; // stash it - not allowed to use later
                ctx.CreateVertex(1u, "person");
            });
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();

            Assert.IsNotNull(captured);
            Assert.ThrowsException<InvalidOperationException>(() => captured.CreateVertex(1u, "late"),
                "The context must be invalid once the body has returned (no off-thread mutation).");

            fallen8.Dispose();
        }

        // ---- mode (a) durability: snapshot yes, WAL-only replay no --------------------------------

        [TestMethod]
        public void DelegateTransaction_ModeA_SurvivesSnapshot_ButNotWalOnlyReplay()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "f8_pluginwrite_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var walPath = Path.Combine(tempDir, "savegame.f8s.wal");
            var savePath = Path.Combine(tempDir, "savegame.f8s");
            try
            {
                // (1) WAL on: a DelegateTransaction commits but is NOT logged (mode a). After a
                // crash+reopen with no intervening snapshot, its element is absent.
                using (var fallen8 = new Fallen8(_loggerFactory, new WriteAheadLogOptions(walPath)))
                {
                    var tx = new DelegateTransaction(ctx => ctx.CreateVertex(1u, "ephemeral"));
                    var info = fallen8.EnqueueTransaction(tx);
                    info.WaitUntilFinished();
                    Assert.AreEqual(TransactionState.Finished, info.TransactionState);
                    Assert.AreEqual(1, fallen8.VertexCount);
                }

                using (var recovered = new Fallen8(_loggerFactory, new WriteAheadLogOptions(walPath)))
                {
                    Assert.AreEqual(0, recovered.VertexCount,
                        "A mode-(a) DelegateTransaction is not WAL-logged, so it does not replay without a snapshot.");
                }

                // (2) Fresh WAL location: commit a DelegateTransaction, THEN Save (snapshot). After
                // reopen from that snapshot its element IS present.
                var walPath2 = Path.Combine(tempDir, "snap.f8s.wal");
                var savePath2 = Path.Combine(tempDir, "snap.f8s");
                string actualSavePath;
                using (var fallen8 = new Fallen8(_loggerFactory, new WriteAheadLogOptions(walPath2)))
                {
                    fallen8.EnqueueTransaction(new DelegateTransaction(ctx => ctx.CreateVertex(1u, "persisted")))
                        .WaitUntilFinished();

                    var saveTx = new SaveTransaction { Path = savePath2, SavePartitions = 1 };
                    var saveInfo = fallen8.EnqueueTransaction(saveTx);
                    saveInfo.WaitUntilFinished();
                    Assert.AreEqual(TransactionState.Finished, saveInfo.TransactionState);
                    actualSavePath = saveTx.ActualPath;
                }

                using (var reloaded = new Fallen8(_loggerFactory))
                {
                    reloaded.EnqueueTransaction(new LoadTransaction { Path = actualSavePath }).WaitUntilFinished();
                    Assert.AreEqual(1, reloaded.VertexCount,
                        "A mode-(a) DelegateTransaction's effect is captured by the snapshot.");
                }
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
    }
}
