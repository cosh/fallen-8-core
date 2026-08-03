// MIT License
//
// GraphAnalyticsBenchmark.cs
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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.Analytics;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Opt-in micro-timing for the direction-aware analytics algorithms (PAGERANK, DEGREE,
    ///   LABELPROPAGATION) - the ones that share the direction dispatch and the budget check.
    ///   Its purpose is the hot-path before/after guard for consolidation-audit CA-6/CA-23:
    ///   capture ms/call and bytes/call, apply the change, re-capture, and revert the change if
    ///   it regresses. Deterministic by construction (fixed seed, fixed iteration count,
    ///   Epsilon set so PageRank never early-exits, unbounded budget) so two runs measure
    ///   identical work.
    ///
    ///   <para>Benchmark-gated and <see cref="IgnoreAttribute"/>-marked like every benchmark
    ///   here, so it is not part of the default suite. Capture a run in Release by temporarily
    ///   removing the [Ignore] (or invoking the method directly). Tune the workload with
    ///   <c>F8_ANALYTICS_SCALE</c> (double, default 1.0) and tag the run with
    ///   <c>F8_ANALYTICS_LABEL</c> ("before"/"after"); results are appended to
    ///   <see cref="ResultsPath"/> so they survive the test host's stdout capture.</para>
    /// </summary>
    [TestClass]
    public class GraphAnalyticsBenchmark
    {
        private const int BaseVertices = 20_000;
        private const int EdgesPerVertex = 5;
        private const int MaxIterations = 50;
        private const int Reps = 21;
        private const int Seed = 4242;

        private static string ResultsPath => Path.Combine(Path.GetTempPath(), "fallen8-analytics-benchmark.txt");
        private static string Label => Environment.GetEnvironmentVariable("F8_ANALYTICS_LABEL") ?? "unlabeled";

        private static double Scale
        {
            get
            {
                var raw = Environment.GetEnvironmentVariable("F8_ANALYTICS_SCALE");
                return double.TryParse(raw, out var value) && value > 0 ? value : 1.0;
            }
        }

        private static void Emit(string line)
        {
            Console.WriteLine("[ANBENCH] " + line);
        }

        [TestMethod]
        [TestCategory("Benchmark")]
        [Ignore("Benchmark harness; opt-in. Not part of the default suite.")]
        public void Analytics_Direction_And_Budget_HotPath()
        {
            var vertexCount = (int)(BaseVertices * Scale);
            using var fallen8 = new Fallen8(TestLoggerFactory.Create());
            BuildRandomGraph(fallen8, vertexCount, EdgesPerVertex);

            // Deterministic work: a fixed iteration count and an Epsilon below any achievable
            // delta keep PageRank/LabelPropagation from early-exiting, so before and after run
            // exactly the same passes. Unbounded budget: never truncate the measurement.
            var definition = new GraphAnalyticsDefinition { MaxIterations = MaxIterations, Epsilon = Double.Epsilon };

            var header = String.Format(
                "== analytics benchmark label={0} vertices={1} edgesPerVertex={2} maxIterations={3} reps={4} ==",
                Label, vertexCount, EdgesPerVertex, MaxIterations, Reps);
            Emit(header);

            var report = new List<string> { header };
            foreach (var algorithm in new[] { "PAGERANK", "DEGREE", "LABELPROPAGATION" })
            {
                var line = MeasureAlgorithm(fallen8, algorithm, definition);
                Emit(line);
                report.Add(line);
            }

            File.AppendAllText(ResultsPath, String.Join(Environment.NewLine, report) + Environment.NewLine);
            Emit("results appended to " + ResultsPath);
        }

        private static string MeasureAlgorithm(Fallen8 fallen8, string algorithm, GraphAnalyticsDefinition definition)
        {
            // Warm up: JIT + build the reverse-adjacency/scratch state so the timed reps measure
            // steady-state work, not first-call setup.
            Assert.IsTrue(fallen8.TryRunAnalytics(out _, algorithm, definition), algorithm + " must run");

            var samples = new double[Reps];
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var r = 0; r < Reps; r++)
            {
                var sw = Stopwatch.StartNew();
                fallen8.TryRunAnalytics(out _, algorithm, definition);
                sw.Stop();
                samples[r] = sw.Elapsed.TotalMilliseconds;
            }
            var bytesPerCall = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / (double)Reps;

            Array.Sort(samples);
            var median = samples[Reps / 2];
            var min = samples[0];
            return String.Format("{0,-16} median={1,8:F3} ms  min={2,8:F3} ms  bytes/call={3,12:F0}",
                algorithm, median, min, bytesPerCall);
        }

        private static void BuildRandomGraph(Fallen8 fallen8, int vertexCount, int edgesPerVertex)
        {
            var verticesTx = new CreateVerticesTransaction();
            for (var i = 0; i < vertexCount; i++)
            {
                verticesTx.AddVertex(1u, "v");
            }
            fallen8.EnqueueTransaction(verticesTx).WaitUntilFinished();
            var ids = verticesTx.GetCreatedVertices().Select(v => v.Id).ToArray();

            // A fixed seed makes the structure identical across runs, so a before/after diff
            // reflects the code change, not a different random graph.
            var random = new Random(Seed);
            var edgesTx = new CreateEdgesTransaction();
            for (var i = 0; i < ids.Length; i++)
            {
                for (var e = 0; e < edgesPerVertex; e++)
                {
                    edgesTx.AddEdge(ids[i], "e", ids[random.Next(ids.Length)], 1u);
                }
            }
            fallen8.EnqueueTransaction(edgesTx).WaitUntilFinished();
        }
    }
}
