// MIT License
//
// WritePathThroughputTest.cs
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
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Behavioural tests for the write-path-throughput group commit: durable-before-ack still holds
    /// for a member of a concurrently-drained group (killing the engine right after the wait returns,
    /// then replaying, finds the write); the awaitable <see cref="TransactionInformation.Completion"/>
    /// and the bounded <see cref="TransactionInformation.WaitUntilFinished(TimeSpan)"/> behave as
    /// specified. The aggregate-throughput speedup itself is an opt-in benchmark (see
    /// <see cref="WritePathThroughputBenchmark"/>), since a wall-clock ratio is not a deterministic
    /// unit assertion.
    /// </summary>
    [TestClass]
    public class WritePathThroughputTest
    {
        private ILoggerFactory _loggerFactory;
        private string _tempDir;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
            _tempDir = Path.Combine(Path.GetTempPath(), "f8_wpt_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        [TestCleanup]
        public void TestCleanup()
        {
            try
            {
                if (_tempDir != null && Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }

        private string WalPath => Path.Combine(_tempDir, "wpt.f8s.wal");
        private Fallen8 NewEngineWithWal() => new Fallen8(_loggerFactory, new WriteAheadLogOptions(WalPath));

        private static TransactionInformation EnqueueVertex(Fallen8 fallen8, string name)
        {
            var tx = new CreateVerticesTransaction();
            tx.AddVertex(1u, "n", new Dictionary<string, object> { { "name", name } });
            return fallen8.EnqueueTransaction(tx);
        }

        [TestMethod]
        public void DurableBeforeAck_ForAMemberOfAConcurrentlyDrainedGroup()
        {
            const int producers = 32;

            using (var fallen8 = NewEngineWithWal())
            {
                // Fire many writes concurrently so the single writer drains several into one commit
                // group, then wait for ALL. When every wait has returned, every write was fsynced
                // (durable-before-ack) even though they shared a group with one fsync.
                var infos = new TransactionInformation[producers];
                Parallel.For(0, producers, i =>
                {
                    infos[i] = EnqueueVertex(fallen8, "p" + i);
                });
                foreach (var info in infos)
                {
                    info.WaitUntilFinished();
                    Assert.AreEqual(TransactionState.Finished, info.TransactionState);
                    Assert.IsTrue(info.Durable, "A committed write in a healthy WAL group must be reported durable.");
                }

                Assert.AreEqual(producers, fallen8.VertexCount);
                // Drop the engine WITHOUT a Save (simulated crash): the WAL is unanchored and holds
                // every fsynced frame.
            }

            using (var recovered = NewEngineWithWal())
            {
                Assert.AreEqual(producers, recovered.VertexCount,
                    "Every write whose wait returned must be durable in the log and recovered by replay - group commit preserves durable-before-ack.");
            }
        }

        [TestMethod]
        public async Task Completion_CompletesAfterTheWriteAndCarriesTheTerminalState()
        {
            using var fallen8 = NewEngineWithWal();
            var info = EnqueueVertex(fallen8, "awaited");

            await info.Completion;

            Assert.AreEqual(TransactionState.Finished, info.TransactionState,
                "Awaiting Completion must observe the terminal state (durable-before-ack point).");
            Assert.IsTrue(info.Durable);
            Assert.AreEqual(1, fallen8.VertexCount);
        }

        [TestMethod]
        public void WaitUntilFinished_WithTimeout_ReturnsTrueOnceFinished()
        {
            using var fallen8 = NewEngineWithWal();
            var info = EnqueueVertex(fallen8, "bounded");

            Assert.IsTrue(info.WaitUntilFinished(TimeSpan.FromSeconds(30)),
                "A transaction that finishes within the budget must return true.");
            Assert.AreEqual(TransactionState.Finished, info.TransactionState);
        }

        [TestMethod]
        public void ManyConcurrentWaitedWrites_AllCompleteWithoutDeadlock()
        {
            // A stress of concurrent waited writes must all complete (the group-commit writer drains
            // and completes them); a plain in-memory engine (WAL off) exercises the same dispatch.
            using var fallen8 = new Fallen8(_loggerFactory);
            const int count = 200;

            var infos = new List<TransactionInformation>(count);
            Parallel.For(0, count, i =>
            {
                var info = EnqueueVertex(fallen8, "c" + i);
                lock (infos) { infos.Add(info); }
            });

            foreach (var info in infos)
            {
                Assert.IsTrue(info.WaitUntilFinished(TimeSpan.FromSeconds(30)), "Every queued write must complete.");
                Assert.AreEqual(TransactionState.Finished, info.TransactionState);
            }
            Assert.AreEqual(count, fallen8.VertexCount);
        }
    }
}
