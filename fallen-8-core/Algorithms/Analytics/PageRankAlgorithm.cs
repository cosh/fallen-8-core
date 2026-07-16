// MIT License
//
// PageRankAlgorithm.cs
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
    ///   PageRank (plugin name <c>PAGERANK</c>, feature graph-analytics): power iteration over
    ///   the flattened adjacency until the L1 delta drops below Epsilon (default 1e-6) or
    ///   MaxIterations (default 100) is reached — the iteration-cap stop is a NORMAL outcome
    ///   (<c>Converged == false</c>, scores usable). Scores sum to 1 over the in-scope vertices.
    ///
    ///   <para>Semantics (spec §3.3): damping factor via <c>Parameters["DampingFactor"]</c>
    ///   (default 0.85); directed over out-edges by default (<c>Direction</c> selects
    ///   out / in / undirected interpretation); DANGLING vertices (no in-scope out-edges under
    ///   the chosen direction) redistribute their rank uniformly; parallel edges carry rank
    ///   multiply; self-loops feed rank back to their vertex.</para>
    /// </summary>
    public sealed class PageRankAlgorithm : AGraphAnalyticsAlgorithm
    {
        /// <summary>The default damping factor.</summary>
        public const Double DefaultDampingFactor = 0.85d;

        /// <summary>The default convergence threshold (L1 delta).</summary>
        public const Double DefaultEpsilon = 1e-6d;

        /// <summary>The default iteration cap.</summary>
        public const Int32 DefaultMaxIterations = 100;

        public override String PluginName => "PAGERANK";

        public override String Description =>
            "PageRank via power iteration - damping 0.85 default, L1-delta convergence, dangling-mass redistribution";

        protected override bool TryRunCore(out GraphAnalyticsResult result,
            GraphAnalyticsDefinition definition, Workspace workspace, BudgetGuard budget,
            Stopwatch stopwatch)
        {
            result = null;

            var damping = DefaultDampingFactor;
            if (TryGetDoubleParameter(definition, "DampingFactor", out var suppliedDamping, out var invalid))
            {
                damping = suppliedDamping;
            }
            if (invalid || damping < 0d || damping > 1d)
            {
                return false;
            }

            var epsilon = definition.Epsilon == 0d ? DefaultEpsilon : definition.Epsilon;
            var maxIterations = definition.MaxIterations == 0 ? DefaultMaxIterations : definition.MaxIterations;
            var direction = definition.Direction ?? Direction.OutgoingEdge;
            var scope = workspace.DenseIndexById;
            var edgePropertyId = definition.EdgePropertyId;
            var n = workspace.Count;

            if (n == 0)
            {
                result = new GraphAnalyticsResult(new Dictionary<Int32, Double>(),
                    new Dictionary<String, Object>(), converged: true, iterationsRun: 0,
                    stopwatch.Elapsed, budgetExhausted: false);
                return true;
            }

            // The in-scope degree under the chosen direction, computed once - the divisor for
            // each vertex's rank share. A vertex with degree 0 is DANGLING.
            var degree = new Int32[n];
            for (var i = 0; i < n; i++)
            {
                if ((i & (GraphAnalyticsDefinition.BudgetCheckInterval - 1)) == 0 && budget.IsExhausted)
                {
                    return false;
                }

                var vertex = workspace.Vertices[i];
                var d = 0L;
                if (direction != Direction.IncomingEdge)
                {
                    d += AnalyticsAdjacency.CountInScope(vertex.GetRawOutEdges(), neighborIsTarget: true, edgePropertyId, scope);
                }
                if (direction != Direction.OutgoingEdge)
                {
                    d += AnalyticsAdjacency.CountInScope(vertex.GetRawInEdges(), neighborIsTarget: false, edgePropertyId, scope);
                }
                degree[i] = (Int32)d;
            }

            var rank = new Double[n];
            var next = new Double[n];
            var initial = 1d / n;
            for (var i = 0; i < n; i++)
            {
                rank[i] = initial;
            }

            var iterationsRun = 0;
            var converged = false;
            var budgetExhausted = false;

            for (var iteration = 0; iteration < maxIterations; iteration++)
            {
                Array.Clear(next, 0, n);
                var danglingSum = 0d;
                var aborted = false;

                for (var i = 0; i < n; i++)
                {
                    if ((i & (GraphAnalyticsDefinition.BudgetCheckInterval - 1)) == 0 && budget.IsExhausted)
                    {
                        aborted = true;
                        break;
                    }

                    if (degree[i] == 0)
                    {
                        danglingSum += rank[i];
                        continue;
                    }

                    var share = rank[i] / degree[i];
                    var vertex = workspace.Vertices[i];
                    var distributor = new DistributeVisitor { Next = next, Share = share };

                    if (direction != Direction.IncomingEdge)
                    {
                        AnalyticsAdjacency.Visit(vertex.GetRawOutEdges(), neighborIsTarget: true, edgePropertyId, scope, ref distributor);
                    }
                    if (direction != Direction.OutgoingEdge)
                    {
                        AnalyticsAdjacency.Visit(vertex.GetRawInEdges(), neighborIsTarget: false, edgePropertyId, scope, ref distributor);
                    }
                }

                if (aborted)
                {
                    // rank still holds the last COMPLETED iteration's values (commits happen
                    // only after a full pass) - the iterative partial-result rule.
                    budgetExhausted = true;
                    break;
                }

                var baseRank = (1d - damping) / n + damping * danglingSum / n;
                var delta = 0d;
                for (var i = 0; i < n; i++)
                {
                    var updated = damping * next[i] + baseRank;
                    delta += Math.Abs(updated - rank[i]);
                    rank[i] = updated;
                }

                iterationsRun++;

                if (delta < epsilon)
                {
                    converged = true;
                    break;
                }
            }

            if (iterationsRun == 0)
            {
                // The budget died before one full pass - no usable result.
                return false;
            }

            var scores = new Dictionary<Int32, Double>(n);
            for (var i = 0; i < n; i++)
            {
                scores[workspace.Vertices[i].Id] = rank[i];
            }

            result = new GraphAnalyticsResult(scores, new Dictionary<String, Object>(),
                converged, iterationsRun, stopwatch.Elapsed, budgetExhausted);
            return true;
        }

        /// <summary>Adds the source vertex's rank share onto each in-scope neighbour
        /// (the push step of the power iteration), via the shared walk.</summary>
        private struct DistributeVisitor : IInScopeNeighborVisitor
        {
            public Double[] Next;
            public Double Share;

            public void OnNeighbor(Int32 denseIndex)
            {
                Next[denseIndex] += Share;
            }
        }
    }
}
