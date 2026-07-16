// MIT License
//
// LabelPropagationAlgorithm.cs
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
    ///   Label propagation community detection (plugin name <c>LABELPROPAGATION</c>, feature
    ///   graph-analytics). Every vertex is seeded with its own id; SYNCHRONOUS rounds (each
    ///   round reads only the previous round's labels) in which each vertex adopts the most
    ///   frequent neighbour label, SMALLEST label winning ties — LP is normally order/tie
    ///   sensitive, and pinning the rule makes results hand-computable and reproducible. Stops
    ///   when a round changes nothing (<c>Converged == true</c>) or at MaxIterations (default
    ///   20). Neighbours over both directions unless <c>Direction</c> narrows.
    ///   <c>Statistics["CommunityCount"]</c> carries the number of distinct final labels.
    /// </summary>
    public sealed class LabelPropagationAlgorithm : AGraphAnalyticsAlgorithm
    {
        /// <summary>The default round cap.</summary>
        public const Int32 DefaultMaxIterations = 20;

        public override String PluginName => "LABELPROPAGATION";

        public override String Description =>
            "Label propagation community detection - synchronous rounds, smallest-label tie-break (deterministic)";

        protected override bool TryRunCore(out GraphAnalyticsResult result,
            GraphAnalyticsDefinition definition, Workspace workspace, BudgetGuard budget,
            Stopwatch stopwatch)
        {
            result = null;

            var maxIterations = definition.MaxIterations == 0 ? DefaultMaxIterations : definition.MaxIterations;
            var direction = definition.Direction ?? Direction.UndirectedEdge;
            var scope = workspace.DenseIndexById;
            var edgePropertyId = definition.EdgePropertyId;
            var n = workspace.Count;

            // Labels are vertex IDS (stable, meaningful community ids), held densely.
            var previous = new Int32[n];
            var next = new Int32[n];
            for (var i = 0; i < n; i++)
            {
                previous[i] = workspace.Vertices[i].Id;
            }

            var iterationsRun = 0;
            var converged = n == 0;
            var budgetExhausted = false;
            var counts = new Dictionary<Int32, Int32>();

            for (var round = 0; round < maxIterations && !converged; round++)
            {
                var changed = false;
                var aborted = false;

                for (var i = 0; i < n; i++)
                {
                    if ((i & (GraphAnalyticsDefinition.BudgetCheckInterval - 1)) == 0 && budget.IsExhausted)
                    {
                        aborted = true;
                        break;
                    }

                    counts.Clear();
                    var vertex = workspace.Vertices[i];
                    var tally = new TallyVisitor { PreviousLabels = previous, Counts = counts };

                    if (direction != Direction.IncomingEdge)
                    {
                        AnalyticsAdjacency.Visit(vertex.GetRawOutEdges(), neighborIsTarget: true, edgePropertyId, scope, ref tally);
                    }
                    if (direction != Direction.OutgoingEdge)
                    {
                        AnalyticsAdjacency.Visit(vertex.GetRawInEdges(), neighborIsTarget: false, edgePropertyId, scope, ref tally);
                    }

                    if (counts.Count == 0)
                    {
                        // No in-scope neighbours: the vertex keeps its label.
                        next[i] = previous[i];
                        continue;
                    }

                    var bestLabel = 0;
                    var bestCount = -1;
                    foreach (var pair in counts)
                    {
                        if (pair.Value > bestCount || (pair.Value == bestCount && pair.Key < bestLabel))
                        {
                            bestLabel = pair.Key;
                            bestCount = pair.Value;
                        }
                    }

                    next[i] = bestLabel;
                    if (bestLabel != previous[i])
                    {
                        changed = true;
                    }
                }

                if (aborted)
                {
                    // previous still holds the last COMPLETED round (the swap below never ran).
                    budgetExhausted = true;
                    break;
                }

                (previous, next) = (next, previous);
                iterationsRun++;

                if (!changed)
                {
                    converged = true;
                }
            }

            if (n > 0 && iterationsRun == 0 && budgetExhausted)
            {
                // The budget died before one full round - no usable result.
                return false;
            }

            var partitions = new Dictionary<Int32, Int32>(n);
            var distinct = new HashSet<Int32>();
            for (var i = 0; i < n; i++)
            {
                partitions[workspace.Vertices[i].Id] = previous[i];
                distinct.Add(previous[i]);
            }

            var statistics = new Dictionary<String, Object> { { "CommunityCount", distinct.Count } };

            result = new GraphAnalyticsResult(partitions, statistics, converged, iterationsRun,
                stopwatch.Elapsed, budgetExhausted);
            return true;
        }

        /// <summary>Tallies each in-scope neighbour's previous-round label, via the shared walk.</summary>
        private struct TallyVisitor : IInScopeNeighborVisitor
        {
            public Int32[] PreviousLabels;
            public Dictionary<Int32, Int32> Counts;

            public void OnNeighbor(Int32 denseIndex)
            {
                var label = PreviousLabels[denseIndex];
                Counts[label] = Counts.TryGetValue(label, out var current) ? current + 1 : 1;
            }
        }
    }
}
