// MIT License
//
// StorageBenchmark.cs
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
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// A lightweight, self-contained Stopwatch/GC harness for the master-store hot paths called
    /// out in features/done/core-storage-representation: id lookup, single insert, bulk insert, full
    /// scan and edge wire-up. It runs as a normal (fast) test so the numbers are always real and
    /// reproducible in this environment; BenchmarkDotNet is intentionally avoided because it needs
    /// a Release build + separate process and does not fit the in-suite guardrail model.
    ///
    /// Results are printed (prefix "[F8BENCH]") and appended to a file so they can be captured for
    /// the before/after comparison without relying on the test console being surfaced. Set
    /// F8_BENCH_SCALE (double, default 1.0) to enlarge the workload and F8_BENCH_LABEL to tag the run.
    /// </summary>
    [TestClass]
    public class StorageBenchmark
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

        private static string ResultsPath => Path.Combine(Path.GetTempPath(), "fallen8-storage-bench.txt");

        [TestMethod]
        [TestCategory("Benchmark")]
        [Ignore("Benchmark harness; run explicitly, e.g. remove [Ignore] or run the method directly. Not part of the default suite.")]
        public void MasterStore_HotPaths_Benchmark()
        {
            double scale = Scale;
            int vertexCount = (int)(50_000 * scale);
            int edgeCount = (int)(50_000 * scale);
            int singleInsertCount = (int)(5_000 * scale);
            long lookupCount = (long)(1_000_000 * scale);
            int scanIterations = 50;

            var report = new StringBuilder();
            void Emit(string op, double elapsedMs, double units, double allocMb)
            {
                double perSec = elapsedMs > 0 ? units / (elapsedMs / 1000.0) : 0;
                var line = string.Format(CultureInfo.InvariantCulture,
                    "[F8BENCH] label={0} op={1,-22} units={2,10:0} elapsed_ms={3,10:0.00} per_sec={4,14:0} alloc_mb={5,9:0.0}",
                    Label, op, units, elapsedMs, perSec, allocMb);
                Console.WriteLine(line);
                report.AppendLine(line);
            }

            var loggerFactory = TestLoggerFactory.Create();

            // ---- Warm up (JIT the hot paths) so the timed runs are steady-state. ----
            WarmUp(loggerFactory);

            // ---- Bulk insert vertices (one transaction). ----
            var fallen8 = new Fallen8(loggerFactory);
            {
                var tx = new CreateVerticesTransaction();
                for (var i = 0; i < vertexCount; i++)
                {
                    tx.AddVertex(1u, "person", new Dictionary<string, object> { { "seq", i } });
                }
                long alloc0 = GC.GetTotalAllocatedBytes(true);
                var sw = Stopwatch.StartNew();
                fallen8.EnqueueTransaction(tx).WaitUntilFinished();
                sw.Stop();
                long alloc1 = GC.GetTotalAllocatedBytes(true);
                Assert.AreEqual(vertexCount, fallen8.VertexCount);
                Emit("bulk_insert_vertices", sw.Elapsed.TotalMilliseconds, vertexCount, (alloc1 - alloc0) / 1048576.0);
            }

            // ---- Edge wire-up: bulk insert edges (master append + adjacency wiring). ----
            {
                var rng = new Random(12345);
                var tx = new CreateEdgesTransaction();
                for (var j = 0; j < edgeCount; j++)
                {
                    tx.AddEdge(rng.Next(0, vertexCount), "friend", rng.Next(0, vertexCount), 1u, "knows");
                }
                long alloc0 = GC.GetTotalAllocatedBytes(true);
                var sw = Stopwatch.StartNew();
                fallen8.EnqueueTransaction(tx).WaitUntilFinished();
                sw.Stop();
                long alloc1 = GC.GetTotalAllocatedBytes(true);
                Assert.AreEqual(edgeCount, fallen8.EdgeCount);
                Emit("bulk_insert_edges", sw.Elapsed.TotalMilliseconds, edgeCount, (alloc1 - alloc0) / 1048576.0);
            }

            // ---- Id lookup (the hottest REST op): random TryGetVertex over the full graph. ----
            {
                var rng = new Random(999);
                // Precompute ids to keep RNG cost out of the timed section.
                var ids = new int[65536];
                for (var i = 0; i < ids.Length; i++)
                {
                    ids[i] = rng.Next(0, vertexCount);
                }
                long hits = 0;
                long alloc0 = GC.GetTotalAllocatedBytes(true);
                var sw = Stopwatch.StartNew();
                for (long i = 0; i < lookupCount; i++)
                {
                    VertexModel v;
                    if (fallen8.TryGetVertex(out v, ids[(int)(i & 0xFFFF)]))
                    {
                        hits++;
                    }
                }
                sw.Stop();
                long alloc1 = GC.GetTotalAllocatedBytes(true);
                Assert.AreEqual(lookupCount, hits, "Every random id in [0,vertexCount) must resolve.");
                Emit("id_lookup", sw.Elapsed.TotalMilliseconds, lookupCount, (alloc1 - alloc0) / 1048576.0);
            }

            // ---- Full scan: GetAllVertices repeated. ----
            {
                long produced = 0;
                long alloc0 = GC.GetTotalAllocatedBytes(true);
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < scanIterations; i++)
                {
                    produced += fallen8.GetAllVertices().Count;
                }
                sw.Stop();
                long alloc1 = GC.GetTotalAllocatedBytes(true);
                Assert.AreEqual((long)vertexCount * scanIterations, produced);
                Emit("full_scan_vertices", sw.Elapsed.TotalMilliseconds, produced, (alloc1 - alloc0) / 1048576.0);
            }

            // ---- Single insert: many one-vertex transactions on a fresh instance. ----
            {
                var single = new Fallen8(loggerFactory);
                TransactionInformation last = null;
                long alloc0 = GC.GetTotalAllocatedBytes(true);
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < singleInsertCount; i++)
                {
                    var tx = new CreateVerticesTransaction();
                    tx.AddVertex(1u, "person");
                    last = single.EnqueueTransaction(tx);
                }
                last.WaitUntilFinished();
                sw.Stop();
                long alloc1 = GC.GetTotalAllocatedBytes(true);
                Assert.AreEqual(singleInsertCount, single.VertexCount);
                // Note: this includes per-transaction machinery, not only the storage append.
                Emit("single_insert_vertices", sw.Elapsed.TotalMilliseconds, singleInsertCount, (alloc1 - alloc0) / 1048576.0);
                single.Dispose();
            }

            fallen8.Dispose();

            // Persist the report for out-of-band capture.
            var header = string.Format(CultureInfo.InvariantCulture,
                "==== fallen8 storage benchmark  label={0}  scale={1}  utc={2:o} ====",
                Label, scale.ToString(CultureInfo.InvariantCulture), DateTime.UtcNow);
            try
            {
                File.AppendAllText(ResultsPath, header + Environment.NewLine + report + Environment.NewLine);
                Console.WriteLine("[F8BENCH] results appended to " + ResultsPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[F8BENCH] could not write results file: " + ex.Message);
            }
        }

        private static void WarmUp(ILoggerFactory loggerFactory)
        {
            var f = new Fallen8(loggerFactory);
            var vtx = new CreateVerticesTransaction();
            for (var i = 0; i < 500; i++)
            {
                vtx.AddVertex(1u, "person", new Dictionary<string, object> { { "seq", i } });
            }
            f.EnqueueTransaction(vtx).WaitUntilFinished();

            var etx = new CreateEdgesTransaction();
            var rng = new Random(1);
            for (var j = 0; j < 500; j++)
            {
                etx.AddEdge(rng.Next(0, 500), "friend", rng.Next(0, 500), 1u, "knows");
            }
            f.EnqueueTransaction(etx).WaitUntilFinished();

            for (var i = 0; i < 50_000; i++)
            {
                VertexModel v;
                f.TryGetVertex(out v, i % 500);
            }
            f.GetAllVertices();
            f.Dispose();
        }
    }
}
