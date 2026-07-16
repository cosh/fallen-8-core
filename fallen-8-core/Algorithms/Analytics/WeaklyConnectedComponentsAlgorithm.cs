// MIT License
//
// WeaklyConnectedComponentsAlgorithm.cs
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

namespace NoSQL.GraphDB.Core.Algorithms.Analytics
{
    /// <summary>
    ///   Weakly connected components (plugin name <c>WCC</c>, feature graph-analytics):
    ///   union-find (iterative, path-halving — no recursion) over all in-scope edges regardless
    ///   of direction. Deterministic component id = the SMALLEST vertex id in the component.
    ///   Not iterative: always <c>Converged == true</c>; <c>Statistics["ComponentCount"]</c>
    ///   carries the number of components.
    /// </summary>
    public sealed class WeaklyConnectedComponentsAlgorithm : AGraphAnalyticsAlgorithm
    {
        public override String PluginName => "WCC";

        public override String Description =>
            "Weakly connected components via union-find - component id = smallest member vertex id";

        protected override bool TryRunCore(out GraphAnalyticsResult result,
            GraphAnalyticsDefinition definition, Workspace workspace, BudgetGuard budget,
            Stopwatch stopwatch)
        {
            result = null;

            var scope = workspace.DenseIndexById;
            var edgePropertyId = definition.EdgePropertyId;
            var n = workspace.Count;

            // Every in-scope vertex starts as its own root. Unions attach the LARGER-id root
            // under the smaller-id root, so a root is always its component's smallest vertex id.
            var parent = new Int32[n];
            for (var i = 0; i < n; i++)
            {
                parent[i] = i;
            }

            // Walking only OUT-adjacencies of every in-scope vertex covers every induced edge
            // exactly once per parallel occurrence (an edge's source is in scope by definition
            // of the walk; its target's scope membership is checked below). Direction is
            // deliberately ignored - "weakly" connected.
            for (var i = 0; i < n; i++)
            {
                if ((i & (GraphAnalyticsDefinition.BudgetCheckInterval - 1)) == 0 && budget.IsExhausted)
                {
                    // Single pass: a partially unioned forest is meaningless.
                    return false;
                }

                var adjacency = workspace.Vertices[i].GetRawOutEdges();
                if (adjacency == null)
                {
                    continue;
                }

                foreach (var group in adjacency)
                {
                    if (edgePropertyId != null && !String.Equals(group.Key, edgePropertyId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var segment = group.Value;
                    for (var e = 0; e < segment.Count; e++)
                    {
                        var edge = segment.Array[segment.Offset + e];
                        if (edge == null || edge._removed)
                        {
                            continue;
                        }

                        var neighbor = edge.TargetVertex;
                        if (neighbor == null || !scope.TryGetValue(neighbor.Id, out var j))
                        {
                            continue;
                        }

                        Union(parent, workspace, i, j);
                    }
                }
            }

            var partitions = new Dictionary<Int32, Int32>(n);
            var roots = new HashSet<Int32>();
            for (var i = 0; i < n; i++)
            {
                var root = Find(parent, i);
                roots.Add(root);
                partitions[workspace.Vertices[i].Id] = workspace.Vertices[root].Id;
            }

            var statistics = new Dictionary<String, Object> { { "ComponentCount", roots.Count } };

            result = new GraphAnalyticsResult(partitions, statistics, converged: true,
                iterationsRun: n == 0 ? 0 : 1, stopwatch.Elapsed, budgetExhausted: false);
            return true;
        }

        /// <summary>Find with path halving - iterative, no recursion.</summary>
        private static Int32 Find(Int32[] parent, Int32 x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        /// <summary>Union by smallest vertex id: the root with the larger id is attached under
        /// the root with the smaller id, keeping component ids deterministic. This is arbitrary
        /// linking (not union-by-rank), so Find is amortized O(log n) with path halving instead
        /// of inverse-Ackermann - the deliberate trade for deterministic smallest-id roots,
        /// bounded like everything else by the run's time budget.</summary>
        private static void Union(Int32[] parent, Workspace workspace, Int32 a, Int32 b)
        {
            var rootA = Find(parent, a);
            var rootB = Find(parent, b);
            if (rootA == rootB)
            {
                return;
            }

            if (workspace.Vertices[rootA].Id < workspace.Vertices[rootB].Id)
            {
                parent[rootB] = rootA;
            }
            else
            {
                parent[rootA] = rootB;
            }
        }
    }
}
