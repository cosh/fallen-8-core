// MIT License
//
// GraphAnalyticsResult.cs
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

namespace NoSQL.GraphDB.Core.Algorithms.Analytics
{
    /// <summary>
    ///   The outcome of one analytics run (feature graph-analytics). Exactly one of the two
    ///   vertex maps is populated, per algorithm: score algorithms (PageRank, degree, triangle
    ///   counting) fill <see cref="VertexScores"/>; partition algorithms (WCC, label
    ///   propagation) fill <see cref="VertexPartitions"/>. The other is an empty map, never null.
    /// </summary>
    public sealed class GraphAnalyticsResult
    {
        private static readonly IReadOnlyDictionary<Int32, Double> _emptyScores =
            new Dictionary<Int32, Double>();

        private static readonly IReadOnlyDictionary<Int32, Int32> _emptyPartitions =
            new Dictionary<Int32, Int32>();

        private static readonly IReadOnlyDictionary<String, Object> _emptyStatistics =
            new Dictionary<String, Object>();

        /// <summary>Creates a score-algorithm result.</summary>
        public GraphAnalyticsResult(IReadOnlyDictionary<Int32, Double> vertexScores,
            IReadOnlyDictionary<String, Object> statistics, Boolean converged,
            Int32 iterationsRun, TimeSpan elapsed, Boolean budgetExhausted)
        {
            VertexScores = vertexScores ?? _emptyScores;
            VertexPartitions = _emptyPartitions;
            Statistics = statistics ?? _emptyStatistics;
            Converged = converged;
            IterationsRun = iterationsRun;
            Elapsed = elapsed;
            BudgetExhausted = budgetExhausted;
        }

        /// <summary>Creates a partition-algorithm result.</summary>
        public GraphAnalyticsResult(IReadOnlyDictionary<Int32, Int32> vertexPartitions,
            IReadOnlyDictionary<String, Object> statistics, Boolean converged,
            Int32 iterationsRun, TimeSpan elapsed, Boolean budgetExhausted)
        {
            VertexScores = _emptyScores;
            VertexPartitions = vertexPartitions ?? _emptyPartitions;
            Statistics = statistics ?? _emptyStatistics;
            Converged = converged;
            IterationsRun = iterationsRun;
            Elapsed = elapsed;
            BudgetExhausted = budgetExhausted;
        }

        /// <summary>Vertex id -&gt; score (PageRank, degree, triangles); empty for partition algorithms.</summary>
        public IReadOnlyDictionary<Int32, Double> VertexScores
        {
            get;
        }

        /// <summary>Vertex id -&gt; partition id (WCC, label propagation); empty for score algorithms.</summary>
        public IReadOnlyDictionary<Int32, Int32> VertexPartitions
        {
            get;
        }

        /// <summary>Run-level aggregates, e.g. <c>"TriangleCount"</c>, <c>"ComponentCount"</c>,
        /// <c>"CommunityCount"</c>, degree <c>"Min"/"Max"/"Mean"</c>.</summary>
        public IReadOnlyDictionary<String, Object> Statistics
        {
            get;
        }

        /// <summary>Whether an iterative algorithm converged; single-pass algorithms
        /// (degree, WCC, triangles) always report true.</summary>
        public Boolean Converged
        {
            get;
        }

        /// <summary>Completed iterations (single-pass algorithms report 1).</summary>
        public Int32 IterationsRun
        {
            get;
        }

        /// <summary>Wall-clock duration of the run.</summary>
        public TimeSpan Elapsed
        {
            get;
        }

        /// <summary>True when the run was stopped by the time budget or cancellation and the
        /// values are the last completed iteration's (partial relative to the requested work).</summary>
        public Boolean BudgetExhausted
        {
            get;
        }
    }
}
