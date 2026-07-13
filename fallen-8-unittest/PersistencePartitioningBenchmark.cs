// MIT License
//
// PersistencePartitioningBenchmark.cs
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
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Save/load MEMORY + throughput harness for Stage C (Phase 4): the right-sized partitioning
    /// (P6) and the load-memory release (P5). It builds a graph, checkpoints it and reloads it, and
    /// reports elapsed time plus an approximate managed-memory peak (sampled from
    /// <see cref="GC.GetTotalMemory(bool)"/> on a background thread) and the cumulative bytes
    /// allocated per phase (<see cref="GC.GetTotalAllocatedBytes(bool)"/>). It is <see cref="IgnoreAttribute"/>d
    /// so it never runs in the default suite; run it explicitly to capture real, reproducible numbers
    /// in this environment (every figure printed here is measured, nothing is fabricated). Set
    /// F8_BENCH_SCALE to enlarge the workload and F8_BENCH_LABEL to tag the run.
    /// </summary>
    [TestClass]
    public class PersistencePartitioningBenchmark
    {
        private static double Scale
        {
            get
            {
                var raw = Environment.GetEnvironmentVariable("F8_BENCH_SCALE");
                double parsed;
                if (!string.IsNullOrWhiteSpace(raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) && parsed > 0)
                {
                    return parsed;
                }
                return 1.0;
            }
        }

        private static string Label => Environment.GetEnvironmentVariable("F8_BENCH_LABEL") ?? "unlabeled";

        /// <summary>
        /// Samples the approximate managed heap size (<see cref="GC.GetTotalMemory(bool)"/>, no forced
        /// collection) on a background thread and records the maximum seen. This is an approximate,
        /// reproducible proxy for the managed-memory peak of the operation it brackets - not an exact
        /// figure, but a measured one.
        /// </summary>
        private sealed class ManagedMemorySampler
        {
            private readonly Thread _thread;
            private volatile bool _stop;
            private long _peak;

            public ManagedMemorySampler()
            {
                _peak = GC.GetTotalMemory(false);
                _thread = new Thread(() =>
                {
                    while (!_stop)
                    {
                        var now = GC.GetTotalMemory(false);
                        if (now > _peak)
                        {
                            _peak = now;
                        }
                        Thread.Sleep(2);
                    }
                })
                { IsBackground = true, Name = "f8-mem-sampler" };
                _thread.Start();
            }

            public long StopAndPeak()
            {
                _stop = true;
                _thread.Join(TimeSpan.FromSeconds(2));
                var now = GC.GetTotalMemory(false);
                if (now > _peak)
                {
                    _peak = now;
                }
                return _peak;
            }
        }

        [TestMethod]
        [TestCategory("Benchmark")]
        [Ignore("Benchmark harness; run explicitly (remove [Ignore] or run the method directly). Not part of the default suite.")]
        public void SaveLoad_Memory_And_Partitioning_Benchmark()
        {
            double scale = Scale;
            int vertexCount = (int)(100_000 * scale);
            int edgeCount = (int)(100_000 * scale);
            int partitions = Environment.ProcessorCount;

            var loggerFactory = TestLoggerFactory.Create();
            var tempDir = Path.Combine(Path.GetTempPath(), "f8_partitioning_bench_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var fallen8 = new Fallen8(loggerFactory);
                var vtx = new CreateVerticesTransaction();
                for (var i = 0; i < vertexCount; i++)
                {
                    vtx.AddVertex(1u, i % 2 == 0 ? "person" : "company",
                        new Dictionary<string, object> { { "name", "name-" + (i % 1000) }, { "seq", i } });
                }
                fallen8.EnqueueTransaction(vtx).WaitUntilFinished();

                var rng = new Random(12345);
                var etx = new CreateEdgesTransaction();
                for (var j = 0; j < edgeCount; j++)
                {
                    etx.AddEdge(rng.Next(0, vertexCount), "knows", rng.Next(0, vertexCount), 1u, "knows");
                }
                fallen8.EnqueueTransaction(etx).WaitUntilFinished();

                var savePath = Path.Combine(tempDir, "bench.f8s");

                // ---- Save (timed + sampled). ----
                GC.Collect();
                GC.WaitForPendingFinalizers();
                long saveAllocBefore = GC.GetTotalAllocatedBytes(true);
                var saveSampler = new ManagedMemorySampler();
                var saveTx = new SaveTransaction { Path = savePath, SavePartitions = partitions };
                var sw = Stopwatch.StartNew();
                fallen8.EnqueueTransaction(saveTx).WaitUntilFinished();
                sw.Stop();
                long savePeak = saveSampler.StopAndPeak();
                long saveAlloc = GC.GetTotalAllocatedBytes(true) - saveAllocBefore;
                Assert.AreEqual(TransactionState.Finished, fallen8.GetTransactionState(saveTx.TransactionId));
                var actualPath = saveTx.ActualPath;
                double saveMs = sw.Elapsed.TotalMilliseconds;

                int bunchFiles = 0;
                foreach (var f in Directory.GetFiles(tempDir))
                {
                    var name = Path.GetFileName(f);
                    if (name.Contains("_graphElements_") && !name.Contains(".f8tmp"))
                    {
                        bunchFiles++;
                    }
                }

                // ---- Load (timed + sampled). ----
                var reloaded = new Fallen8(loggerFactory);
                var loadTx = new LoadTransaction { Path = actualPath };
                GC.Collect();
                GC.WaitForPendingFinalizers();
                long loadAllocBefore = GC.GetTotalAllocatedBytes(true);
                var loadSampler = new ManagedMemorySampler();
                sw.Restart();
                reloaded.EnqueueTransaction(loadTx).WaitUntilFinished();
                sw.Stop();
                long loadPeak = loadSampler.StopAndPeak();
                long loadAlloc = GC.GetTotalAllocatedBytes(true) - loadAllocBefore;
                Assert.AreEqual(TransactionState.Finished, reloaded.GetTransactionState(loadTx.TransactionId));
                double loadMs = sw.Elapsed.TotalMilliseconds;

                Assert.AreEqual(vertexCount, reloaded.VertexCount);
                Assert.AreEqual(edgeCount, reloaded.EdgeCount);

                var elements = vertexCount + edgeCount;
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "[F8PARTBENCH] label={0} elements={1} cores={2} partitions_req={3} bunch_files={4} " +
                    "save_ms={5:0.0} save_peak_mb={6:0.0} save_alloc_mb={7:0.0} " +
                    "load_ms={8:0.0} load_peak_mb={9:0.0} load_alloc_mb={10:0.0}",
                    Label, elements, Environment.ProcessorCount, partitions, bunchFiles,
                    saveMs, savePeak / (1024.0 * 1024.0), saveAlloc / (1024.0 * 1024.0),
                    loadMs, loadPeak / (1024.0 * 1024.0), loadAlloc / (1024.0 * 1024.0)));

                fallen8.Dispose();
                reloaded.Dispose();
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { /* best-effort */ }
            }
        }
    }
}
