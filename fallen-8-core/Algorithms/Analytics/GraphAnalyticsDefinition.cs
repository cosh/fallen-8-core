// MIT License
//
// GraphAnalyticsDefinition.cs
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
using System.Threading;

namespace NoSQL.GraphDB.Core.Algorithms.Analytics
{
    /// <summary>
    ///   Scoping, budgets and algorithm parameters for one analytics run (feature
    ///   graph-analytics). All scoping is data-only (plain string comparison against labels /
    ///   edge-property-ids) — no compiled predicates, so analytics stays entirely outside the
    ///   dynamic-code trust boundary.
    /// </summary>
    public sealed class GraphAnalyticsDefinition
    {
        /// <summary>The hard ceiling on <see cref="MaxIterations"/>.</summary>
        public const Int32 MaxIterationsCeiling = 10_000;

        /// <summary>How often (in vertices processed) the wall-clock budget and cancellation
        /// token are checked cooperatively inside a pass.</summary>
        public const Int32 BudgetCheckInterval = 4_096;

        #region scoping (all optional; null => whole graph)

        /// <summary>Only vertices with exactly this label participate; a scoped run computes
        /// over the induced subgraph (only edges between two in-scope vertices are traversed).</summary>
        public String VertexLabel
        {
            get; set;
        }

        /// <summary>Only edges in this adjacency group (edge-property-id) are traversed.</summary>
        public String EdgePropertyId
        {
            get; set;
        }

        /// <summary>Per-algorithm edge-direction interpretation; null selects the algorithm's
        /// documented default (PageRank: outgoing; degree and label propagation: undirected;
        /// WCC and triangle counting ignore direction entirely).</summary>
        public Direction? Direction
        {
            get; set;
        }

        #endregion

        #region budgets

        /// <summary>Iteration cap for iterative algorithms; 0 selects the per-algorithm default
        /// (PageRank 100, label propagation 20). Values above <see cref="MaxIterationsCeiling"/>
        /// or below 0 make the run invalid. Reaching the cap is a NORMAL outcome
        /// (<c>Converged == false</c>, values usable).</summary>
        public Int32 MaxIterations
        {
            get; set;
        }

        /// <summary>Convergence threshold (PageRank; L1 delta between iterations); 0 selects
        /// the default 1e-6. Negative values make the run invalid.</summary>
        public Double Epsilon
        {
            get; set;
        }

        /// <summary>Wall-clock budget, checked cooperatively every
        /// <see cref="BudgetCheckInterval"/> vertices. Zero or negative means unbounded (an
        /// embedded caller's choice; the REST layer always sets a budget). Exhaustion mid-run:
        /// iterative algorithms return the last COMPLETED iteration's values with
        /// <c>BudgetExhausted == true</c> if at least one full pass finished, otherwise the run
        /// returns false; single-pass algorithms return false (a partial single pass is
        /// meaningless).</summary>
        public TimeSpan TimeBudget
        {
            get; set;
        }

        /// <summary>Cooperative cancellation, checked together with the time budget. Analytics
        /// runs execute no user-supplied code, so cooperative cancellation is genuinely
        /// sufficient — there is no hostile-delegate residual here.</summary>
        public CancellationToken CancellationToken
        {
            get; set;
        }

        #endregion

        /// <summary>Algorithm-specific knobs, the plugin parameter convention
        /// (e.g. <c>"DampingFactor" -&gt; 0.85</c> for PageRank).</summary>
        public IDictionary<String, Object> Parameters
        {
            get; set;
        }
    }
}
