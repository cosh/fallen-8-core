// MIT License
//
// PersistenceEncodingBenchmark.cs
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
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Save/load throughput + on-disk size harness for the Stage-B payload encoding (findings
    /// P2/M5/P7/N1). It builds a graph, checkpoints it, and reports elapsed time for save and load
    /// plus the total on-disk footprint of the checkpoint (all sidecars). It is <see cref="IgnoreAttribute"/>d
    /// so it never runs in the default suite; run it explicitly to capture real, reproducible numbers
    /// in this environment (nothing here is a fabricated figure). Set F8_BENCH_SCALE to enlarge the
    /// workload and F8_BENCH_LABEL to tag the run.
    /// </summary>
    [TestClass]
    public class PersistenceEncodingBenchmark
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

        [TestMethod]
        [TestCategory("Benchmark")]
        [Ignore("Benchmark harness; run explicitly (remove [Ignore] or run the method directly). Not part of the default suite.")]
        public void SaveLoad_Throughput_And_Size_Benchmark()
        {
            double scale = Scale;
            int vertexCount = (int)(100_000 * scale);
            int edgeCount = (int)(100_000 * scale);
            int partitions = Environment.ProcessorCount;

            var loggerFactory = TestLoggerFactory.Create();
            var tempDir = Path.Combine(Path.GetTempPath(), "f8_encoding_bench_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                // ---- Build a representative graph (shared labels + edge property ids so tokenization
                //      and var-int both have something to bite on). ----
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

                // ---- Save (timed). ----
                var saveTx = new SaveTransaction { Path = savePath, SavePartitions = partitions };
                var sw = Stopwatch.StartNew();
                fallen8.EnqueueTransaction(saveTx).WaitUntilFinished();
                sw.Stop();
                Assert.AreEqual(TransactionState.Finished, fallen8.GetTransactionState(saveTx.TransactionId));
                var actualPath = saveTx.ActualPath;
                double saveMs = sw.Elapsed.TotalMilliseconds;

                // ---- On-disk footprint: the header + every sidecar for this checkpoint. ----
                long totalBytes = Directory.GetFiles(tempDir)
                    .Where(f => Path.GetFileName(f).StartsWith(Path.GetFileName(actualPath)))
                    .Sum(f => new FileInfo(f).Length);

                // ---- Load (timed). ----
                var reloaded = new Fallen8(loggerFactory);
                var loadTx = new LoadTransaction { Path = actualPath };
                sw.Restart();
                reloaded.EnqueueTransaction(loadTx).WaitUntilFinished();
                sw.Stop();
                Assert.AreEqual(TransactionState.Finished, reloaded.GetTransactionState(loadTx.TransactionId));
                double loadMs = sw.Elapsed.TotalMilliseconds;

                Assert.AreEqual(vertexCount, reloaded.VertexCount);
                Assert.AreEqual(edgeCount, reloaded.EdgeCount);

                var elements = vertexCount + edgeCount;
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "[F8ENCBENCH] label={0} elements={1} partitions={2} save_ms={3:0.0} save_elem_per_s={4:0} load_ms={5:0.0} load_elem_per_s={6:0} size_bytes={7} bytes_per_elem={8:0.0}",
                    Label, elements, partitions, saveMs, elements / (saveMs / 1000.0), loadMs,
                    elements / (loadMs / 1000.0), totalBytes, (double)totalBytes / elements));

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
