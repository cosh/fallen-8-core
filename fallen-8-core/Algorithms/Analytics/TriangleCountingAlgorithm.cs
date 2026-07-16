// MIT License
//
// TriangleCountingAlgorithm.cs
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
using NoSQL.GraphDB.Core.Model;

namespace NoSQL.GraphDB.Core.Algorithms.Analytics
{
    /// <summary>
    ///   Triangle counting (plugin name <c>TRIANGLECOUNT</c>, feature graph-analytics) under
    ///   the UNDIRECTED SIMPLE-GRAPH interpretation (the standard definition): adjacency is
    ///   deduplicated per vertex pair and self-loops are ignored before counting. Per-vertex
    ///   counts in <c>VertexScores</c>; <c>Statistics["TriangleCount"]</c> is the global count
    ///   (= sum of per-vertex counts / 3). Neighbour-intersection over sorted dense neighbour
    ///   lists — O(sum of degree squared) worst case, bounded cooperatively by the time budget.
    /// </summary>
    public sealed class TriangleCountingAlgorithm : AGraphAnalyticsAlgorithm
    {
        public override String PluginName => "TRIANGLECOUNT";

        public override String Description =>
            "Triangle counting (undirected simple-graph): per-vertex counts + global total via sorted-neighbour intersection";

        protected override bool TryRunCore(out GraphAnalyticsResult result,
            GraphAnalyticsDefinition definition, Workspace workspace, BudgetGuard budget,
            Stopwatch stopwatch)
        {
            result = null;

            var scope = workspace.DenseIndexById;
            var edgePropertyId = definition.EdgePropertyId;
            var n = workspace.Count;

            // Pass 1: the undirected simple-graph reduction - per vertex, the SORTED set of
            // distinct in-scope neighbour dense indices (parallel edges deduplicated, self-loops
            // dropped). Direction is deliberately ignored: a triangle is a triangle.
            var neighbors = new Int32[n][];
            var neighborSet = new HashSet<Int32>();
            for (var i = 0; i < n; i++)
            {
                if ((i & (GraphAnalyticsDefinition.BudgetCheckInterval - 1)) == 0 && budget.IsExhausted)
                {
                    return false;
                }

                neighborSet.Clear();
                var vertex = workspace.Vertices[i];
                var collector = new CollectVisitor { Self = i, NeighborSet = neighborSet };
                AnalyticsAdjacency.Visit(vertex.GetRawOutEdges(), neighborIsTarget: true, edgePropertyId, scope, ref collector);
                AnalyticsAdjacency.Visit(vertex.GetRawInEdges(), neighborIsTarget: false, edgePropertyId, scope, ref collector);

                var list = new Int32[neighborSet.Count];
                neighborSet.CopyTo(list);
                Array.Sort(list);
                neighbors[i] = list;
            }

            // Pass 2: for every edge (u, v) with u < v, count common neighbours w with w > v -
            // each triangle (u < v < w) is found exactly once and credited to all three corners.
            var counts = new Int64[n];
            var total = 0L;
            var operations = 0;

            for (var u = 0; u < n; u++)
            {
                if (budget.IsExhausted)
                {
                    return false;
                }

                var nu = neighbors[u];
                for (var vi = 0; vi < nu.Length; vi++)
                {
                    var v = nu[vi];
                    if (v <= u)
                    {
                        continue;
                    }

                    var nv = neighbors[v];
                    var a = 0;
                    var b = 0;
                    while (a < nu.Length && b < nv.Length)
                    {
                        // The intersection is the hot loop at a hub - keep the budget
                        // cooperative here too, not only per outer vertex.
                        if ((++operations & (GraphAnalyticsDefinition.BudgetCheckInterval - 1)) == 0 && budget.IsExhausted)
                        {
                            return false;
                        }

                        if (nu[a] < nv[b])
                        {
                            a++;
                        }
                        else if (nu[a] > nv[b])
                        {
                            b++;
                        }
                        else
                        {
                            var w = nu[a];
                            if (w > v)
                            {
                                counts[u]++;
                                counts[v]++;
                                counts[w]++;
                                total++;
                            }
                            a++;
                            b++;
                        }
                    }
                }
            }

            var scores = new Dictionary<Int32, Double>(n);
            for (var i = 0; i < n; i++)
            {
                scores[workspace.Vertices[i].Id] = counts[i];
            }

            var statistics = new Dictionary<String, Object> { { "TriangleCount", total } };

            result = new GraphAnalyticsResult(scores, statistics, converged: true,
                iterationsRun: n == 0 ? 0 : 1, stopwatch.Elapsed, budgetExhausted: false);
            return true;
        }

        /// <summary>Collects distinct in-scope neighbours (the simple-graph reduction:
        /// parallel edges deduplicate via the set, self-loops are dropped), via the shared walk.</summary>
        private struct CollectVisitor : IInScopeNeighborVisitor
        {
            public Int32 Self;
            public HashSet<Int32> NeighborSet;

            public void OnNeighbor(Int32 denseIndex)
            {
                if (denseIndex != Self)
                {
                    NeighborSet.Add(denseIndex);
                }
            }
        }
    }
}
