// MIT License
//
// EnginePerformanceBenchmark.cs
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
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Index.Range;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Opt-in Stopwatch benchmarks for the engine-performance acceptance criteria. They follow the
    /// existing convention (Benchmark category + [Ignore]) so they are NOT part of the default run;
    /// remove the [Ignore] (or run the method explicitly) to capture numbers. Output is prefixed
    /// "[EPBENCH]".
    /// </summary>
    [TestClass]
    public class EnginePerformanceBenchmark
    {
        private static void Emit(string line)
        {
            Console.WriteLine("[EPBENCH] " + line);
        }

        private static VertexModel[] CreateVertices(Fallen8 fallen8, int count, string label = "v")
        {
            var tx = new CreateVerticesTransaction();
            for (int i = 0; i < count; i++)
            {
                tx.AddVertex(1u, label);
            }
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.GetCreatedVertices().ToArray();
        }

        // ---- P1: /path compiles once under repeated identical requests --------------------------

        [TestMethod]
        [TestCategory("Benchmark")]
        [Ignore("Benchmark harness; opt-in. Not part of the default suite.")]
        public void P1_PathCompile_OnceUnderRepeatedRequests()
        {
            var loggerFactory = TestLoggerFactory.Create();
            var fallen8 = new Fallen8(loggerFactory);
            var vertices = CreateVertices(fallen8, 2);
            var edgeTx = new CreateEdgesTransaction();
            edgeTx.AddEdge(vertices[0].Id, "e", vertices[1].Id, 1u);
            fallen8.EnqueueTransaction(edgeTx).WaitUntilFinished();

            // Each iteration uses a FRESH controller (a new request) but a value-equal spec. With the
            // process-wide cache the first call compiles and the rest hit; without it every call
            // would recompile with Roslyn.
            const int iterations = 40;
            PathSpecification MakeSpec() => new PathSpecification { PathAlgorithmName = "BLS", MaxDepth = 6, MaxResults = 9 };

            var swFirst = Stopwatch.StartNew();
            new GraphController(new UnitTestLogger<GraphController>(), fallen8)
                .CalculateShortestPath(vertices[0].Id, vertices[1].Id, MakeSpec());
            swFirst.Stop();

            var swRest = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                new GraphController(new UnitTestLogger<GraphController>(), fallen8)
                    .CalculateShortestPath(vertices[0].Id, vertices[1].Id, MakeSpec());
            }
            swRest.Stop();

            double firstMs = swFirst.Elapsed.TotalMilliseconds;
            double avgRestMs = swRest.Elapsed.TotalMilliseconds / iterations;
            Emit(string.Format(CultureInfo.InvariantCulture,
                "P1 path-compile: first(compile)={0:0.00} ms; avg subsequent(cache hit over {1} fresh controllers)={2:0.000} ms; speedup={3:0}x",
                firstMs, iterations, avgRestMs, avgRestMs > 0 ? firstMs / avgRestMs : 0));
            fallen8.Dispose();
        }

        // ---- P3: vertex delete is O(degree), not O(n) -------------------------------------------

        [TestMethod]
        [TestCategory("Benchmark")]
        [Ignore("Benchmark harness; opt-in. Not part of the default suite.")]
        public void P3_VertexDelete_IsOrderDegreeNotOrderN()
        {
            var loggerFactory = TestLoggerFactory.Create();

            // Measure the time to delete a fixed-degree vertex as the surrounding graph grows. If
            // removal were O(n) (the old double full recount), the time would grow with N; O(degree)
            // keeps it roughly flat.
            foreach (var n in new[] { 20_000, 100_000, 500_000 })
            {
                var fallen8 = new Fallen8(loggerFactory);
                var vertices = CreateVertices(fallen8, n);

                // Give one target vertex a small fixed degree (10 edges), independent of n.
                int target = vertices[0].Id;
                var edgeTx = new CreateEdgesTransaction();
                for (int k = 1; k <= 10; k++)
                {
                    edgeTx.AddEdge(target, "e", vertices[k].Id, 1u);
                }
                fallen8.EnqueueTransaction(edgeTx).WaitUntilFinished();

                var removeTx = new RemoveGraphElementTransaction { GraphElementId = target };
                var sw = Stopwatch.StartNew();
                fallen8.EnqueueTransaction(removeTx).WaitUntilFinished();
                sw.Stop();

                Emit(string.Format(CultureInfo.InvariantCulture,
                    "P3 vertex-delete: n={0,8} degree=10 -> {1:0.000} ms (state={2})",
                    n, sw.Elapsed.TotalMilliseconds, fallen8.GetTransactionState(removeTx.TransactionId)));
                fallen8.Dispose();
            }
        }

        // ---- P4: range query scales O(log n + k) ------------------------------------------------

        [TestMethod]
        [TestCategory("Benchmark")]
        [Ignore("Benchmark harness; opt-in. Not part of the default suite.")]
        public void P4_RangeQuery_ScalesWithLogNPlusK()
        {
            var loggerFactory = TestLoggerFactory.Create();
            var fallen8 = new Fallen8(loggerFactory);

            const int n = 500_000;
            var vertices = CreateVertices(fallen8, n);
            var index = new RangeIndex();
            index.Initialize(fallen8, null);
            for (int i = 0; i < n; i++)
            {
                index.AddOrUpdate(i, vertices[i]);
            }

            // Warm up (builds the sorted-key cache once).
            ImmutableList<AGraphElementModel> warm;
            index.Between(out warm, 0, 10, true, true);

            // Vary selectivity k while n is fixed: time should track k (+ the log n search), not n.
            foreach (var k in new[] { 10, 100, 1_000, 10_000, 100_000 })
            {
                const int reps = 20;
                var sw = Stopwatch.StartNew();
                int lastCount = 0;
                for (int r = 0; r < reps; r++)
                {
                    ImmutableList<AGraphElementModel> res;
                    index.Between(out res, 0, k - 1, true, true);
                    lastCount = res.Count;
                }
                sw.Stop();
                Emit(string.Format(CultureInfo.InvariantCulture,
                    "P4 range-query: n={0} k={1,7} -> {2:0.000} ms/query (result={3})",
                    n, k, sw.Elapsed.TotalMilliseconds / reps, lastCount));
            }
            index.Dispose();
            fallen8.Dispose();
        }
    }
}
