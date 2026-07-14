// MIT License
//
// SupernodeAdjacencyBuildBenchmark.cs
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
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Opt-in Stopwatch/allocation benchmark for the "supernode-adjacency-build" acceptance criteria. It
    /// builds a single high-degree hub across a range of degrees — three ways: (a) one batch
    /// <see cref="CreateEdgesTransaction"/>, (b) one edge per transaction, and (c) a save → load
    /// round-trip — and reports, per degree, the wall time and process-wide bytes allocated
    /// (<c>GC.GetTotalAllocatedBytes</c>, since the adjacency build runs on the writer thread). With
    /// amortised O(1) append the per-degree cost should scale LINEARLY (doubling the degree ~doubles
    /// time and bytes) instead of the former O(d²) whole-group-copy-per-edge build (~4·d² bytes copied).
    ///
    /// Degrees are env-tunable via <c>F8_SUPERNODE_DEGREES</c> (comma-separated). Follows the repo
    /// benchmark convention (Benchmark category + [Ignore]) so it is NOT part of the default run; remove
    /// the [Ignore] (or run the method explicitly) to capture numbers. Output is prefixed "[SNBENCH]".
    /// </summary>
    [TestClass]
    public class SupernodeAdjacencyBuildBenchmark
    {
        private const string EdgeKey = "A";

        private static void Emit(string line)
        {
            Console.WriteLine("[SNBENCH] " + line);
        }

        private static int[] Degrees()
        {
            var env = Environment.GetEnvironmentVariable("F8_SUPERNODE_DEGREES");
            if (!String.IsNullOrWhiteSpace(env))
            {
                var parsed = env.Split(',')
                    .Select(s => Int32.TryParse(s.Trim(), out var d) ? d : 0)
                    .Where(d => d > 0)
                    .ToArray();
                if (parsed.Length > 0)
                {
                    return parsed;
                }
            }
            return new[] { 10_000, 20_000, 40_000, 80_000 };
        }

        private static VertexModel[] SeedHubAndLeaves(Fallen8 fallen8, int degree)
        {
            var tx = new CreateVerticesTransaction();
            for (var i = 0; i <= degree; i++)
            {
                tx.AddVertex(1u, "v");
            }
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.GetCreatedVertices().ToArray();
        }

        private static (double ms, long bytes) Measure(Action action)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetTotalAllocatedBytes(true);
            var sw = Stopwatch.StartNew();
            action();
            sw.Stop();
            var bytes = GC.GetTotalAllocatedBytes(true) - before;
            return (sw.Elapsed.TotalMilliseconds, bytes);
        }

        [TestMethod]
        [TestCategory("Benchmark")]
        [Ignore("Benchmark harness; opt-in. Not part of the default suite.")]
        public void HubBuild_Batch_ScalesLinearly()
        {
            var loggerFactory = TestLoggerFactory.Create();
            foreach (var degree in Degrees())
            {
                var fallen8 = new Fallen8(loggerFactory);
                var v = SeedHubAndLeaves(fallen8, degree);
                var hub = v[0];

                var edgeTx = new CreateEdgesTransaction();
                for (var i = 1; i <= degree; i++)
                {
                    edgeTx.AddEdge(hub.Id, EdgeKey, v[i].Id, 1u, "e");
                }

                var (ms, bytes) = Measure(() => fallen8.EnqueueTransaction(edgeTx).WaitUntilFinished());
                Emit(FormatLine("batch", degree, ms, bytes));
                fallen8.Dispose();
            }
        }

        [TestMethod]
        [TestCategory("Benchmark")]
        [Ignore("Benchmark harness; opt-in. Not part of the default suite.")]
        public void HubBuild_OneEdgePerTransaction_ScalesLinearly()
        {
            var loggerFactory = TestLoggerFactory.Create();
            foreach (var degree in Degrees())
            {
                var fallen8 = new Fallen8(loggerFactory);
                var v = SeedHubAndLeaves(fallen8, degree);
                var hub = v[0];

                var (ms, bytes) = Measure(() =>
                {
                    for (var i = 1; i <= degree; i++)
                    {
                        var edgeTx = new CreateEdgesTransaction();
                        edgeTx.AddEdge(hub.Id, EdgeKey, v[i].Id, 1u, "e");
                        fallen8.EnqueueTransaction(edgeTx).WaitUntilFinished();
                    }
                });
                Emit(FormatLine("single", degree, ms, bytes));
                fallen8.Dispose();
            }
        }

        [TestMethod]
        [TestCategory("Benchmark")]
        [Ignore("Benchmark harness; opt-in. Not part of the default suite.")]
        public void HubLoad_RoundTrip_ScalesLinearly()
        {
            var loggerFactory = TestLoggerFactory.Create();
            var tempDir = Path.Combine(Path.GetTempPath(), "f8_supernode_bench_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                foreach (var degree in Degrees())
                {
                    var fallen8 = new Fallen8(loggerFactory);
                    var v = SeedHubAndLeaves(fallen8, degree);
                    var hub = v[0];

                    var edgeTx = new CreateEdgesTransaction();
                    for (var i = 1; i <= degree; i++)
                    {
                        edgeTx.AddEdge(hub.Id, EdgeKey, v[i].Id, 1u, "e");
                        edgeTx.AddEdge(v[i].Id, EdgeKey, hub.Id, 1u, "e");
                    }
                    fallen8.EnqueueTransaction(edgeTx).WaitUntilFinished();

                    var savePath = Path.Combine(tempDir, "hub_" + degree + ".f8s");
                    var saveTx = new SaveTransaction { Path = savePath, SavePartitions = Environment.ProcessorCount };
                    fallen8.EnqueueTransaction(saveTx).WaitUntilFinished();
                    var actualPath = saveTx.ActualPath;
                    fallen8.Dispose();

                    Fallen8 loaded = null;
                    var (ms, bytes) = Measure(() =>
                    {
                        loaded = new Fallen8(loggerFactory);
                        loaded.EnqueueTransaction(new LoadTransaction { Path = actualPath }).WaitUntilFinished();
                    });
                    Emit(FormatLine("load", degree, ms, bytes));
                    loaded?.Dispose();
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static string FormatLine(string kind, int degree, double ms, long bytes)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0,-6} degree={1,8} -> {2,10:0.000} ms, {3,8:0.0} MB ({4:0.0} B/edge)",
                kind, degree, ms, bytes / (1024.0 * 1024.0), degree > 0 ? (double)bytes / degree : 0.0);
        }
    }
}
