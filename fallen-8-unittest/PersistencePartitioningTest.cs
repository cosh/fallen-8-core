// MIT License
//
// PersistencePartitioningTest.cs
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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Helper;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Covers Stage C (Phase 4) of the persistence-hardening theme: right-sized save partitioning
    /// (P6) - tiny, empty and large graphs all round-trip and the bunch count is sized to the work,
    /// the cores and any explicit caller cap - and the single-writer invariant that makes every
    /// checkpoint a consistent point-in-time snapshot (the reason the file writing stays on the
    /// worker thread and P3 is deferred). The load-memory change (P5) is behaviour-preserving and is
    /// covered by these round-trip assertions plus the Stage-A save-after-remove test.
    /// </summary>
    [TestClass]
    public class PersistencePartitioningTest
    {
        private ILoggerFactory _loggerFactory;
        private string _tempDir;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
            _tempDir = Path.Combine(Path.GetTempPath(), "f8_persist_partitioning_" + Guid.NewGuid().ToString("N"));
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

        #region helpers

        private string SavePath => Path.Combine(_tempDir, "savegame.f8s");

        private string Save(Fallen8 fallen8, int partitions, string path = null)
        {
            var tx = new SaveTransaction { Path = path ?? SavePath, SavePartitions = partitions };
            var info = fallen8.EnqueueTransaction(tx);
            info.WaitUntilFinished();
            Assert.AreEqual(TransactionState.Finished, info.TransactionState, "The save should finish. " + info.Error);
            Assert.IsFalse(String.IsNullOrEmpty(tx.ActualPath), "The save should report a path.");
            return tx.ActualPath;
        }

        private static Fallen8 Load(ILoggerFactory loggerFactory, string path)
        {
            var loaded = new Fallen8(loggerFactory);
            var tx = new LoadTransaction { Path = path };
            var info = loaded.EnqueueTransaction(tx);
            info.WaitUntilFinished();
            Assert.AreEqual(TransactionState.Finished, info.TransactionState, "The load should finish. " + info.Error);
            return loaded;
        }

        /// <summary>Counts the graph-element bunch sidecars that belong to exactly THIS save.</summary>
        private int BunchFileCount(string actualPath)
        {
            var prefix = Path.GetFileName(actualPath) + Constants.GraphElementsSaveString;
            return Directory.GetFiles(_tempDir)
                .Count(f => Path.GetFileName(f).StartsWith(prefix, StringComparison.Ordinal)
                         && !f.Contains(Constants.TempSaveSuffix));
        }

        private static Fallen8 BuildVertexGraph(ILoggerFactory loggerFactory, int vertexCount, out VertexModel[] created)
        {
            var fallen8 = new Fallen8(loggerFactory);
            var tx = new CreateVerticesTransaction();
            for (var i = 0; i < vertexCount; i++)
            {
                tx.AddVertex(1u, "person", new Dictionary<string, object> { { "seq", i } });
            }
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            created = tx.GetCreatedVertices().ToArray();
            return fallen8;
        }

        private static int InvokeComputePartitionCount(int elementCount, int requestedMax)
        {
            var type = typeof(Fallen8).Assembly.GetType("NoSQL.GraphDB.Core.Persistency.PersistencyFactory", throwOnError: true);
            var method = type.GetMethod("ComputePartitionCount", BindingFlags.Static | BindingFlags.NonPublic,
                null, new[] { typeof(int), typeof(int) }, null);
            Assert.IsNotNull(method, "PersistencyFactory.ComputePartitionCount(int,int) should exist.");
            return (int)method.Invoke(null, new object[] { elementCount, requestedMax });
        }

        #endregion

        #region P6 - right-sized partitioning (tiny / empty / large round-trip)

        [TestMethod]
        public void P6_EmptyGraph_WritesNoBunches_AndRoundTripsToEmpty()
        {
            var source = new Fallen8(_loggerFactory);
            Assert.AreEqual(0, source.VertexCount);

            var actualPath = Save(source, partitions: 5);
            Assert.AreEqual(0, BunchFileCount(actualPath), "An empty graph writes no graph-element bunch files.");

            var loaded = Load(_loggerFactory, actualPath);
            Assert.AreEqual(0, loaded.VertexCount, "An empty graph round-trips to empty.");
            Assert.AreEqual(0, loaded.EdgeCount);
        }

        [TestMethod]
        public void P6_TinyGraph_CollapsesToSingleBunch_NotOnePerElement()
        {
            // Three elements with the OLD default of five partitions used to produce a degenerate
            // one-file-per-element fan-out (finding P6). Right-sizing collapses it to a single bunch.
            var source = BuildVertexGraph(_loggerFactory, 3, out _);

            var actualPath = Save(source, partitions: 5);

            Assert.AreEqual(1, BunchFileCount(actualPath),
                "A tiny graph must be written as a single bunch, not one file per element.");

            var loaded = Load(_loggerFactory, actualPath);
            Assert.AreEqual(3, loaded.VertexCount, "Every element still round-trips from the single bunch.");
        }

        [TestMethod]
        public void P6_LargeGraph_SplitsUpToCoreCount_AndRoundTrips()
        {
            // A graph larger than one target chunk must split - but never into more bunches than
            // cores (min(cores, ceil(count/targetChunk))). With ~1.25 chunks of work the work-side
            // limit is 2, so the expected count is min(cores, 2).
            var vertexCount = Constants.SaveTargetPartitionSize + (Constants.SaveTargetPartitionSize / 4);
            var source = BuildVertexGraph(_loggerFactory, vertexCount, out var vertices);

            // Add cross-partition edges: consecutive ids land in different partitions, so these
            // exercise the loader's cross-bunch edge resolution.
            var edgeTx = new CreateEdgesTransaction();
            for (var i = 0; i < 50; i++)
            {
                var src = vertices[i].Id;
                var tgt = vertices[vertexCount - 1 - i].Id;
                edgeTx.AddEdge(src, "knows", tgt, 1u, "knows");
            }
            source.EnqueueTransaction(edgeTx).WaitUntilFinished();
            Assert.AreEqual(50, source.EdgeCount);

            // A large cap so the caller request never binds; the work/cores limit governs.
            var actualPath = Save(source, partitions: 1024);

            var expectedBunches = Math.Min(Environment.ProcessorCount, 2);
            Assert.AreEqual(expectedBunches, BunchFileCount(actualPath),
                "A graph of just over one target chunk splits into min(cores, 2) bunches.");

            var loaded = Load(_loggerFactory, actualPath);
            Assert.AreEqual(vertexCount, loaded.VertexCount, "Every vertex is written exactly once and round-trips.");
            Assert.AreEqual(50, loaded.EdgeCount, "Every cross-partition edge round-trips.");
        }

        [TestMethod]
        public void P6_ExplicitPartitionCap_IsHonoured_EvenWhenWorkWouldSplitMore()
        {
            // Two-plus chunks of work (ceil = 3) would allow up to 3 bunches on a multi-core host,
            // but an explicit request for a single partition must still yield exactly one bunch.
            var vertexCount = (Constants.SaveTargetPartitionSize * 2) + 7;
            var source = BuildVertexGraph(_loggerFactory, vertexCount, out _);

            var actualPath = Save(source, partitions: 1);

            Assert.AreEqual(1, BunchFileCount(actualPath),
                "An explicit SavePartitions = 1 must be honoured regardless of work/cores.");

            var loaded = Load(_loggerFactory, actualPath);
            Assert.AreEqual(vertexCount, loaded.VertexCount, "The whole graph round-trips from the single bunch.");
        }

        #endregion

        #region P6 - the partition-count formula

        [TestMethod]
        public void P6_ComputePartitionCount_RightSizesToWorkCoresAndCap()
        {
            var cores = Environment.ProcessorCount;
            var chunk = Constants.SaveTargetPartitionSize;

            // Empty -> no bunches at all.
            Assert.AreEqual(0, InvokeComputePartitionCount(0, 5));
            Assert.AreEqual(0, InvokeComputePartitionCount(-10, 5));

            // A single element (and up to a full chunk) is one partition (work-limited to 1).
            Assert.AreEqual(1, InvokeComputePartitionCount(1, 5));
            Assert.AreEqual(1, InvokeComputePartitionCount(chunk, 100));

            // Just over one chunk -> two chunks of work, capped by cores.
            Assert.AreEqual(Math.Min(cores, 2), InvokeComputePartitionCount(chunk + 1, 1024));

            // Many chunks of work, generous cap -> limited by the core count.
            Assert.AreEqual(cores, InvokeComputePartitionCount(chunk * 1000, Int32.MaxValue));

            // An explicit cap smaller than both work and cores binds.
            Assert.AreEqual(Math.Min(2, cores), InvokeComputePartitionCount(chunk * 100, 2));

            // A non-positive cap is ignored (falls back to the work/cores limit).
            Assert.AreEqual(Math.Min(cores, 100), InvokeComputePartitionCount(chunk * 100, 0));
            Assert.AreEqual(Math.Min(cores, 100), InvokeComputePartitionCount(chunk * 100, -1));

            // Never zero for a non-empty graph, never above the core count.
            for (var count = 1; count <= chunk * 8; count += chunk)
            {
                var result = InvokeComputePartitionCount(count, Int32.MaxValue);
                Assert.IsTrue(result >= 1 && result <= cores,
                    $"count={count} produced {result}, expected in [1, {cores}].");
            }
        }

        #endregion

        #region single-writer invariant: every checkpoint is a consistent snapshot (P3 deferral guard)

        [TestMethod]
        public void SingleWriter_SavesInterleavedWithMutations_YieldConsistentCheckpoints()
        {
            // The save runs on the single transaction-writer thread, so - even though reads and
            // enqueues happen from many threads - a checkpoint is always a consistent point-in-time
            // snapshot: no mutation can tear it mid-write. This is exactly the guarantee that lets the
            // file writing stay on the worker (P3 deferred). If the writing were naively moved
            // off-thread while the worker kept mutating, an interleaved Trim/removal/edge-add could
            // desynchronise a checkpoint, and this test would catch it.
            var source = new Fallen8(_loggerFactory);

            // A stable base set of vertices that concurrently-added edges always have valid endpoints in.
            const int baseVertices = 64;
            var baseTx = new CreateVerticesTransaction();
            for (var i = 0; i < baseVertices; i++)
            {
                baseTx.AddVertex(1u, "base", new Dictionary<string, object> { { "seq", i } });
            }
            source.EnqueueTransaction(baseTx).WaitUntilFinished();
            var baseIds = baseTx.GetCreatedVertices().Select(v => v.Id).ToArray();

            var savedPaths = new ConcurrentBag<string>();
            const int threads = 4;
            const int roundsPerThread = 16;

            var workers = new List<Thread>();
            for (var t = 0; t < threads; t++)
            {
                var threadIndex = t;
                var worker = new Thread(() =>
                {
                    var rng = new Random(1000 + threadIndex);
                    for (var round = 0; round < roundsPerThread; round++)
                    {
                        switch (rng.Next(3))
                        {
                            case 0:
                                {
                                    // Grow the graph with brand-new vertices.
                                    var vtx = new CreateVerticesTransaction();
                                    for (var k = 0; k < 5; k++)
                                    {
                                        vtx.AddVertex(1u, "extra", new Dictionary<string, object> { { "r", round } });
                                    }
                                    source.EnqueueTransaction(vtx).WaitUntilFinished();
                                    break;
                                }
                            case 1:
                                {
                                    // Add edges between existing base vertices (always valid endpoints).
                                    var etx = new CreateEdgesTransaction();
                                    for (var k = 0; k < 3; k++)
                                    {
                                        var a = baseIds[rng.Next(baseIds.Length)];
                                        var b = baseIds[rng.Next(baseIds.Length)];
                                        etx.AddEdge(a, "knows", b, 1u, "knows");
                                    }
                                    source.EnqueueTransaction(etx).WaitUntilFinished();
                                    break;
                                }
                            default:
                                {
                                    // Checkpoint the live graph to a unique path.
                                    var path = Path.Combine(_tempDir, $"cp_{threadIndex}_{round}.f8s");
                                    var saveTx = new SaveTransaction { Path = path, SavePartitions = 4 };
                                    var info = source.EnqueueTransaction(saveTx);
                                    info.WaitUntilFinished();
                                    if (info.TransactionState == TransactionState.Finished)
                                    {
                                        savedPaths.Add(saveTx.ActualPath);
                                    }
                                    break;
                                }
                        }
                    }
                });
                workers.Add(worker);
                worker.Start();
            }

            foreach (var worker in workers)
            {
                Assert.IsTrue(worker.Join(TimeSpan.FromSeconds(60)), "A worker thread did not finish in time.");
            }

            Assert.IsTrue(savedPaths.Count > 0, "The interleaving should have produced at least one checkpoint.");

            // Every checkpoint, taken at an arbitrary interleaving point, must load to a fully
            // self-consistent graph: the persisted counts agree with the rehydrated elements, and
            // every edge resolves both of its endpoints to a live vertex (no dangling references).
            foreach (var path in savedPaths)
            {
                var loaded = Load(_loggerFactory, path);

                Assert.AreEqual(loaded.VertexCount, loaded.GetAllVertices().Count,
                    "Persisted vertex count must match the rehydrated vertices.");
                Assert.AreEqual(loaded.EdgeCount, loaded.GetAllEdges().Count,
                    "Persisted edge count must match the rehydrated edges.");

                foreach (var edge in loaded.GetAllEdges())
                {
                    Assert.IsTrue(loaded.TryGetVertex(out _, edge.SourceVertex.Id),
                        "Every checkpointed edge must resolve its source vertex.");
                    Assert.IsTrue(loaded.TryGetVertex(out _, edge.TargetVertex.Id),
                        "Every checkpointed edge must resolve its target vertex.");
                }

                loaded.Dispose();
            }

            source.Dispose();
        }

        #endregion
    }
}
