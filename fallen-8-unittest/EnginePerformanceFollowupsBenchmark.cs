// MIT License
//
// EnginePerformanceFollowupsBenchmark.cs
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
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.Expression;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Opt-in Stopwatch / allocation benchmarks for the engine-performance-followups feature. They
    /// follow the existing convention (Benchmark category + [Ignore]) so they are NOT part of the
    /// default run; remove the [Ignore] (or run the method explicitly) to capture numbers. Output is
    /// prefixed "[EPFBENCH]".
    /// </summary>
    [TestClass]
    public class EnginePerformanceFollowupsBenchmark
    {
        private static void Emit(string line)
        {
            Console.WriteLine("[EPFBENCH] " + line);
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

        // ---- P4: ordered IndexScan on a RangeIndex is O(log n + k), not O(n) --------------------
        //
        // Same data in a RangeIndex (rerouted, sorted binary search) and a DictionaryIndex (the
        // untouched generic FindElementsIndex O(n) PLINQ scan). For a fixed large n, the rerouted
        // query time tracks the result size k (+ the log n search); the generic scan tracks n
        // regardless of k. The ratio is the P4 win.

        [TestMethod]
        [TestCategory("Benchmark")]
        [Ignore("Benchmark harness; opt-in. Not part of the default suite.")]
        public void P4_OrderedIndexScan_RangeVsGeneric_ScalesWithK()
        {
            var loggerFactory = TestLoggerFactory.Create();
            var fallen8 = new Fallen8(loggerFactory);

            const int n = 200_000;
            var vertices = CreateVertices(fallen8, n);

            IIndex rangeIndex, dictIndex;
            fallen8.IndexFactory.TryCreateIndex(out rangeIndex, "benchRange", "RangeIndex");
            fallen8.IndexFactory.TryCreateIndex(out dictIndex, "benchDict", "DictionaryIndex");
            for (int i = 0; i < n; i++)
            {
                rangeIndex.AddOrUpdate(i, vertices[i]);
                dictIndex.AddOrUpdate(i, vertices[i]);
            }

            // Warm up both paths (build the RangeIndex sorted-key cache; JIT the PLINQ scan).
            ImmutableList<AGraphElementModel> warm;
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
                    ImmutableList<AGraphElementModel> res;
                    fallen8.IndexScan(out res, "benchRange", literal, BinaryOperator.Greater);
                    fastCount = res.Count;
                }
                swFast.Stop();

                const int slowReps = 5;
                var swSlow = Stopwatch.StartNew();
                int slowCount = 0;
                for (int r = 0; r < slowReps; r++)
                {
                    ImmutableList<AGraphElementModel> res;
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

        // ---- P6 (DEFERRED): quantify the current copy-on-extend reconstruction cost --------------
        //
        // P6's parent-pointer rewrite was deferred (see features/engine-performance-followups/plan.md):
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
                var s = CreateVertices(fallen8, 1)[0];
                var layers = new VertexModel[depth][];
                for (int d = 0; d < depth; d++)
                {
                    layers[d] = CreateVertices(fallen8, width);
                }
                var t = CreateVertices(fallen8, 1)[0];

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
