// MIT License
//
// DegreeCentralityAlgorithm.cs
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
    ///   Degree centrality (plugin name <c>DEGREE</c>, feature graph-analytics): a single pass
    ///   over the flattened adjacency counting each in-scope vertex's in / out / both degree
    ///   (both = in + out, the default). Parallel edges and self-loops count as they exist in
    ///   the adjacency (a self-loop contributes 1 to out and 1 to in). Statistics carry
    ///   Min/Max/Mean over the in-scope scores.
    /// </summary>
    public sealed class DegreeCentralityAlgorithm : AGraphAnalyticsAlgorithm
    {
        public override String PluginName => "DEGREE";

        public override String Description =>
            "Degree centrality (in/out/both) per vertex - single pass, no iteration knobs";

        protected override bool TryRunCore(out GraphAnalyticsResult result,
            GraphAnalyticsDefinition definition, Workspace workspace, BudgetGuard budget,
            Stopwatch stopwatch)
        {
            result = null;

            var direction = definition.Direction ?? Direction.UndirectedEdge;
            var scope = workspace.DenseIndexById;
            var edgePropertyId = definition.EdgePropertyId;
            var n = workspace.Count;

            var scores = new Dictionary<Int32, Double>(n);
            var min = Double.MaxValue;
            var max = Double.MinValue;
            var sum = 0d;

            for (var i = 0; i < n; i++)
            {
                if ((i & (GraphAnalyticsDefinition.BudgetCheckInterval - 1)) == 0 && budget.IsExhausted)
                {
                    // Single pass: a partial degree map is meaningless.
                    return false;
                }

                var vertex = workspace.Vertices[i];
                var degree = 0L;

                if (direction != Direction.IncomingEdge)
                {
                    degree += AnalyticsAdjacency.CountInScope(vertex.GetRawOutEdges(), neighborIsTarget: true, edgePropertyId, scope);
                }

                if (direction != Direction.OutgoingEdge)
                {
                    degree += AnalyticsAdjacency.CountInScope(vertex.GetRawInEdges(), neighborIsTarget: false, edgePropertyId, scope);
                }

                var score = (Double)degree;
                scores[vertex.Id] = score;
                if (score < min)
                {
                    min = score;
                }
                if (score > max)
                {
                    max = score;
                }
                sum += score;
            }

            var statistics = new Dictionary<String, Object>
            {
                { "Min", n == 0 ? 0d : min },
                { "Max", n == 0 ? 0d : max },
                { "Mean", n == 0 ? 0d : sum / n }
            };

            result = new GraphAnalyticsResult(scores, statistics, converged: true,
                iterationsRun: n == 0 ? 0 : 1, stopwatch.Elapsed, budgetExhausted: false);
            return true;
        }

    }
}
