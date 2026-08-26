// MIT License
//
// EnginePerformanceBenchmark.cs
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
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.Expression;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Index.Range;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Opt-in Stopwatch / allocation benchmarks for the engine-performance acceptance criteria and
    /// for its followups (P4's IndexScan reroute, P6's deferred BLS reconstruction rewrite). They
    /// follow the existing convention (Benchmark category + [Ignore]) so they are NOT part of the
    /// default run; remove the [Ignore] (or run the method explicitly) to capture numbers. Output is
    /// prefixed "[EPBENCH]".
    /// </summary>
    [TestClass]
    public class EnginePerformanceBenchmark
    {
        private static void Emit(string line)
        {
            Console.WriteLine("[EPBENCH] " + line);
        }

        // ---- P1: /path compiles once under repeated identical requests --------------------------

        [TestMethod]
        [TestCategory("Benchmark")]
        [Ignore("Benchmark harness; opt-in. Not part of the default suite.")]
        public void P1_PathCompile_OnceUnderRepeatedRequests()
        {
            var loggerFactory = TestLoggerFactory.Create();
            var fallen8 = new Fallen8(loggerFactory);
            var vertices = TestVertices.Create(fallen8, 2);
            var edgeTx = new CreateEdgesTransaction();
            edgeTx.AddEdge(vertices[0].Id, "e", vertices[1].Id, 1u);
            fallen8.EnqueueTransaction(edgeTx).WaitUntilFinished();

            // Each iteration uses a FRESH controller (a new request) but a value-equal spec. With the
            // process-wide cache the first call compiles and the rest hit; without it every call
            // would recompile with Roslyn.
            const int iterations = 40;
            PathSpecification MakeSpec() => new PathSpecification { PathAlgorithmName = "BLS", MaxDepth = 6, MaxResults = 9 };

            var swFirst = Stopwatch.StartNew();
            _ = new GraphController(new UnitTestLogger<GraphController>(), fallen8)
                .CalculateShortestPath(vertices[0].Id, vertices[1].Id, MakeSpec()).Result;
            swFirst.Stop();

            var swRest = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                _ = new GraphController(new UnitTestLogger<GraphController>(), fallen8)
                    .CalculateShortestPath(vertices[0].Id, vertices[1].Id, MakeSpec()).Result;
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
                var vertices = TestVertices.Create(fallen8, n);

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
        //
        // The same O(log n + k) claim measured at BOTH layers it has to hold at, in one benchmark:
        //
        //  (a) On the RangeIndex itself: with n fixed, Between() time tracks the result size k
        //      (plus the log n search), not n.
        //  (b) Through Fallen8.IndexScan, one layer up: the same data lives in a RangeIndex
        //      (ordered operators rerouted to the sorted binary search) and in a DictionaryIndex
        //      (the untouched generic FindElementsIndex O(n) PLINQ scan). For a fixed large n the
        //      rerouted query time tracks k; the generic scan tracks n regardless of k. The ratio
        //      is the P4 win.
        //
        // Each layer keeps its own graph and its own n, so the numbers stay comparable with the ones
        // each of the two was captured at before they shared a method.

        [TestMethod]
        [TestCategory("Benchmark")]
        [Ignore("Benchmark harness; opt-in. Not part of the default suite.")]
        public void P4_RangeQuery_ScalesWithLogNPlusK()
        {
            // (a) the RangeIndex itself.
            {
                var loggerFactory = TestLoggerFactory.Create();
                var fallen8 = new Fallen8(loggerFactory);

                const int n = 500_000;
                var vertices = TestVertices.Create(fallen8, n);
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

            // (b) through Fallen8.IndexScan: the rerouted RangeIndex against the generic O(n) scan.
            {
                var loggerFactory = TestLoggerFactory.Create();
                var fallen8 = new Fallen8(loggerFactory);

                const int n = 200_000;
                var vertices = TestVertices.Create(fallen8, n);

                IIndex rangeIndex, dictIndex;
                fallen8.IndexFactory.TryCreateIndex(out rangeIndex, "benchRange", "RangeIndex");
                fallen8.IndexFactory.TryCreateIndex(out dictIndex, "benchDict", "DictionaryIndex");
                for (int i = 0; i < n; i++)
                {
                    rangeIndex.AddOrUpdate(i, vertices[i]);
                    dictIndex.AddOrUpdate(i, vertices[i]);
                }

                // Warm up both paths (build the RangeIndex sorted-key cache; JIT the PLINQ scan).
                IReadOnlyList<AGraphElementModel> warm;
                fallen8.IndexScan(out warm, "benchRange", n - 2, BinaryOperator.Greater);
                fallen8.IndexScan(out warm, "benchDict", n - 2, BinaryOperator.Greater);

                foreach (var k in new[] { 1, 100, 10_000, 100_000 })
                {
                    // Greater(literal) returns the keys strictly greater than literal; pick literal so
                    // exactly k keys qualify.
                    IComparable literal = n - 1 - k;

                    const int fastReps = 50;
                    var swFast = Stopwatch.StartNew();
                    int fastCount = 0;
                    for (int r = 0; r < fastReps; r++)
                    {
                        IReadOnlyList<AGraphElementModel> res;
                        fallen8.IndexScan(out res, "benchRange", literal, BinaryOperator.Greater);
                        fastCount = res.Count;
                    }
                    swFast.Stop();

                    const int slowReps = 5;
                    var swSlow = Stopwatch.StartNew();
                    int slowCount = 0;
                    for (int r = 0; r < slowReps; r++)
                    {
                        IReadOnlyList<AGraphElementModel> res;
                        fallen8.IndexScan(out res, "benchDict", literal, BinaryOperator.Greater);
                        slowCount = res.Count;
                    }
                    swSlow.Stop();

                    double fastMs = swFast.Elapsed.TotalMilliseconds / fastReps;
                    double slowMs = swSlow.Elapsed.TotalMilliseconds / slowReps;
                    Emit(string.Format(CultureInfo.InvariantCulture,
                        "P4 ordered-IndexScan: n={0} k={1,7} -> range(reroute)={2:0.0000} ms (result={3}), generic O(n)={4:0.0000} ms (result={5}), speedup={6:0}x",
                        n, k, fastMs, fastCount, slowMs, slowCount, fastMs > 0 ? slowMs / fastMs : 0));
                }

                fallen8.Dispose();
            }
        }

        // ---- P6 (DEFERRED): quantify the current copy-on-extend reconstruction cost --------------
        //
        // P6's parent-pointer rewrite was deferred (see features/done/engine-performance-followups/plan.md):
        // the reconstruction's reversal seam makes a byte-identical rewrite high-risk, while the reward
        // is small. This benchmark measures the payoff AT STAKE - the current cost of copy-on-extend
        // reconstruction - so the "low reward" half of that trade-off rests on real numbers. It is NOT
        // a before/after; there is no "after" because the rewrite was not landed.
        //
        // Two facts it surfaces:
        //  1) BLS reconstructs only ~(number of meeting/"middle" vertices) paths, NOT one per distinct
        //     route: the shared `visitedVertices` set gives every frontier vertex exactly ONE
        //     predecessor edge, so the predecessor structure is a spanning TREE. The layered graph
        //     below has width^depth distinct equal-length S->T routes yet BLS returns only `width`
        //     paths - printed so the claim is visible, not asserted.
        //  2) Because there are few paths, the copy-on-extend cost is driven by path LENGTH (each path
        //     is built by copying a growing element list, ~O(L^2)). Varying depth shows how the
        //     allocated bytes/call grow with length - the quantity the rewrite would turn into ~O(L).

        [TestMethod]
        [TestCategory("Benchmark")]
        [Ignore("Benchmark harness; opt-in. Not part of the default suite.")]
        public void P6_BlsReconstruction_CurrentAllocationCost()
        {
            const int width = 2;

            foreach (var depth in new[] { 8, 16, 32 })
            {
                var loggerFactory = TestLoggerFactory.Create();
                var fallen8 = new Fallen8(loggerFactory);

                // S -> layer0 -> ... -> layer(depth-1) -> T; each layer holds `width` vertices, adjacent
                // layers fully connected. width^depth distinct S->T routes, all of length depth+1.
                var s = TestVertices.Create(fallen8, 1)[0];
                var layers = new VertexModel[depth][];
                for (int d = 0; d < depth; d++)
                {
                    layers[d] = TestVertices.Create(fallen8, width);
                }
                var t = TestVertices.Create(fallen8, 1)[0];

                var edgeTx = new CreateEdgesTransaction();
                foreach (var first in layers[0])
                {
                    edgeTx.AddEdge(s.Id, "e", first.Id, 1u);
                }
                for (int d = 0; d < depth - 1; d++)
                {
                    foreach (var from in layers[d])
                    {
                        foreach (var to in layers[d + 1])
                        {
                            edgeTx.AddEdge(from.Id, "e", to.Id, 1u);
                        }
                    }
                }
                foreach (var last in layers[depth - 1])
                {
                    edgeTx.AddEdge(last.Id, "e", t.Id, 1u);
                }
                fallen8.EnqueueTransaction(edgeTx).WaitUntilFinished();

                var definition = new ShortestPathDefinition
                {
                    SourceVertexId = s.Id,
                    DestinationVertexId = t.Id,
                    MaxDepth = depth + 1,
                    MaxResults = 500
                };

                // Warm up (also captures how many paths BLS actually reconstructs).
                List<Path> warm;
                fallen8.TryCalculateShortestPath(out warm, "BLS", definition);
                int reconstructed = warm.Count;
                int length = warm.Count > 0 ? warm[0].GetLength() : 0;

                const int reps = 200;
                long before = GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew();
                for (int r = 0; r < reps; r++)
                {
                    List<Path> paths;
                    fallen8.TryCalculateShortestPath(out paths, "BLS", definition);
                }
                sw.Stop();
                long after = GC.GetAllocatedBytesForCurrentThread();

                double bytesPerCall = (after - before) / (double)reps;
                Emit(string.Format(CultureInfo.InvariantCulture,
                    "P6 current BLS cost: depth={0,2} (length={1,2}) width^depth={2} routes -> BLS reconstructs {3} path(s); {4:0.000} ms/call, {5:0} bytes/call (whole calculate) over {6} reps",
                    depth, length, Math.Pow(width, depth), reconstructed,
                    sw.Elapsed.TotalMilliseconds / reps, bytesPerCall, reps));

                fallen8.Dispose();
            }
        }
    }
}
