// MIT License
//
// GraphAnalyticsWriteBackTest.cs
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
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.Analytics;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Persistency;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Engine-level pins for the analytics write-back mechanism (feature graph-analytics,
    /// spec §3.5): the values land through DelegateTransaction + SetProperty and inherit that
    /// path's durability (mode a: snapshot yes, WAL-only replay no) and its chunk-atomic /
    /// not-run-atomic failure shape. The REST-level behaviour (keys, types, chunk counts,
    /// idempotency) is pinned in <see cref="AnalyticsEndpointTest"/>.
    /// </summary>
    [TestClass]
    public class GraphAnalyticsWriteBackTest
    {
        private ILoggerFactory _loggerFactory;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
        }

        [TestMethod]
        public void WriteBack_ModeA_SurvivesSnapshot_ButNotWalOnlyReplay()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "f8_analytics_wb_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                // (1) WAL on: the vertex creation IS logged; the analytics write-back property
                // (a DelegateTransaction SetProperty - the exact write-back shape) is NOT.
                var walPath = Path.Combine(tempDir, "wal.f8s.wal");
                Int32 vertexId;
                using (var fallen8 = new Fallen8(_loggerFactory, new WriteAheadLogOptions(walPath)))
                {
                    var createTx = new CreateVertexTransaction
                    {
                        Definition = new VertexDefinition { CreationDate = 1u, Label = "person" }
                    };
                    fallen8.EnqueueTransaction(createTx).WaitUntilFinished();
                    vertexId = createTx.VertexCreated.Id;

                    Assert.IsTrue(fallen8.TryRunAnalytics(out var run, "PAGERANK", new GraphAnalyticsDefinition()));
                    var score = run.VertexScores[vertexId];

                    var writeBack = new DelegateTransaction(
                        ctx => ctx.SetProperty(vertexId, "analytics.pagerank", score),
                        name: "analytics-writeback:analytics.pagerank");
                    var info = fallen8.EnqueueTransaction(writeBack);
                    info.WaitUntilFinished();
                    Assert.AreEqual(TransactionState.Finished, info.TransactionState);
                }

                using (var recovered = new Fallen8(_loggerFactory, new WriteAheadLogOptions(walPath)))
                {
                    Assert.IsTrue(recovered.TryGetVertex(out var vertex, vertexId),
                        "the vertex itself is WAL-logged and replays");
                    Assert.IsFalse(vertex.TryGetProperty<Object>(out _, "analytics.pagerank"),
                        "the write-back property is mode (a): NOT WAL-logged, absent after a WAL-only replay - re-run to restore");
                }

                // (2) The same write-back IS captured by a snapshot.
                var walPath2 = Path.Combine(tempDir, "snap.f8s.wal");
                var savePath = Path.Combine(tempDir, "snap.f8s");
                String actualSavePath;
                Int32 vertexId2;
                using (var fallen8 = new Fallen8(_loggerFactory, new WriteAheadLogOptions(walPath2)))
                {
                    var createTx = new CreateVertexTransaction
                    {
                        Definition = new VertexDefinition { CreationDate = 1u, Label = "person" }
                    };
                    fallen8.EnqueueTransaction(createTx).WaitUntilFinished();
                    vertexId2 = createTx.VertexCreated.Id;

                    fallen8.EnqueueTransaction(new DelegateTransaction(
                        ctx => ctx.SetProperty(vertexId2, "analytics.pagerank", 1d))).WaitUntilFinished();

                    var saveTx = new SaveTransaction { Path = savePath, SavePartitions = 1 };
                    fallen8.EnqueueTransaction(saveTx).WaitUntilFinished();
                    actualSavePath = saveTx.ActualPath;
                }

                using (var reloaded = new Fallen8(_loggerFactory))
                {
                    reloaded.EnqueueTransaction(new LoadTransaction { Path = actualSavePath }).WaitUntilFinished();
                    Assert.IsTrue(reloaded.TryGetVertex(out var vertex, vertexId2));
                    Assert.IsTrue(vertex.TryGetProperty<Double>(out var score, "analytics.pagerank"),
                        "snapshot-durable");
                    Assert.AreEqual(1d, score);
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch { /* best effort */ }
            }
        }

        [TestMethod]
        public void WriteBack_ChunkAtomic_NotRunAtomic_EarlierChunksStayApplied()
        {
            using var fallen8 = new Fallen8(_loggerFactory);
            var createTx = new CreateVertexTransaction
            {
                Definition = new VertexDefinition { CreationDate = 1u, Label = "person" }
            };
            fallen8.EnqueueTransaction(createTx).WaitUntilFinished();
            var vertexId = createTx.VertexCreated.Id;

            // Chunk 1 commits...
            var chunk1 = fallen8.EnqueueTransaction(new DelegateTransaction(
                ctx => ctx.SetProperty(vertexId, "analytics.degree.both", (UInt32)1)));
            chunk1.WaitUntilFinished();
            Assert.AreEqual(TransactionState.Finished, chunk1.TransactionState);

            // ...chunk 2 faults mid-body and rolls back - ITS effect is undone, chunk 1's is not.
            var chunk2 = fallen8.EnqueueTransaction(new DelegateTransaction(ctx =>
            {
                ctx.SetProperty(vertexId, "analytics.degree.both", (UInt32)2);
                throw new InvalidOperationException("induced mid-chunk failure");
            }));
            chunk2.WaitUntilFinished();
            Assert.AreEqual(TransactionState.RolledBack, chunk2.TransactionState);

            Assert.IsTrue(fallen8.TryGetVertex(out var vertex, vertexId));
            Assert.IsTrue(vertex.TryGetProperty<UInt32>(out var value, "analytics.degree.both"));
            Assert.AreEqual((UInt32)1, value,
                "chunk-atomic: the faulted chunk rolled back to the earlier chunk's value; the write-back as a whole is not atomic (re-run to complete)");
        }
    }
}
