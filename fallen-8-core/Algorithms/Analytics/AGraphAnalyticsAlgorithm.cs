// MIT License
//
// AGraphAnalyticsAlgorithm.cs
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
using System.Threading;
using NoSQL.GraphDB.Core.Model;

namespace NoSQL.GraphDB.Core.Algorithms.Analytics
{
    /// <summary>
    ///   Shared machinery for the built-in analytics algorithms (feature graph-analytics):
    ///   definition validation, the in-scope vertex snapshot with its dense id-&gt;index map,
    ///   and the cooperative wall-clock/cancellation budget. Third-party plugins are free to
    ///   implement <see cref="IGraphAnalyticsAlgorithm"/> directly — this base is a convenience,
    ///   not part of the contract.
    ///
    ///   <para>Per-run working state is dense arrays over the snapshot order (element ids are
    ///   not assumed contiguous — Trim/removals can leave gaps), O(V) extra memory per run,
    ///   freed when the run ends.</para>
    /// </summary>
    public abstract class AGraphAnalyticsAlgorithm : IGraphAnalyticsAlgorithm
    {
        /// <summary>The engine this plugin was initialized with.</summary>
        protected IFallen8 _fallen8;

        #region IPlugin implementation

        public abstract String PluginName
        {
            get;
        }

        public Type PluginCategory => typeof(IGraphAnalyticsAlgorithm);

        public abstract String Description
        {
            get;
        }

        public String Manufacturer => "Henning Rauch";

        public void Initialize(IFallen8 fallen8, IDictionary<String, Object> parameter)
        {
            _fallen8 = fallen8;
        }

        public void Dispose()
        {
            _fallen8 = null;
        }

        #endregion

        public bool TryRunAnalytics(out GraphAnalyticsResult result, GraphAnalyticsDefinition definition)
        {
            result = null;

            if (definition == null || _fallen8 == null)
            {
                return false;
            }

            if (definition.MaxIterations < 0 ||
                definition.MaxIterations > GraphAnalyticsDefinition.MaxIterationsCeiling ||
                definition.Epsilon < 0)
            {
                return false;
            }

            var stopwatch = Stopwatch.StartNew();
            var budget = new BudgetGuard(stopwatch, definition.TimeBudget, definition.CancellationToken);

            if (!TryBuildWorkspace(out var workspace, definition, budget))
            {
                // The budget died during the snapshot walk - no usable result.
                return false;
            }

            return TryRunCore(out result, definition, workspace, budget, stopwatch);
        }

        /// <summary>The algorithm body. The workspace holds the in-scope live snapshot; the
        /// budget must be consulted every <see cref="GraphAnalyticsDefinition.BudgetCheckInterval"/>
        /// vertices inside every pass.</summary>
        protected abstract bool TryRunCore(out GraphAnalyticsResult result,
            GraphAnalyticsDefinition definition, Workspace workspace, BudgetGuard budget,
            Stopwatch stopwatch);

        #region workspace

        /// <summary>
        ///   The per-run substrate: the in-scope, live vertex snapshot (id-ordered, from
        ///   <c>GetAllVertices</c>) and the dense id-&gt;index map that lets working state live
        ///   in flat arrays instead of per-vertex dictionaries.
        /// </summary>
        protected sealed class Workspace
        {
            internal Workspace(IReadOnlyList<VertexModel> vertices, Dictionary<Int32, Int32> denseIndexById)
            {
                Vertices = vertices;
                DenseIndexById = denseIndexById;
            }

            /// <summary>The in-scope live vertices, in ascending-id snapshot order.</summary>
            public IReadOnlyList<VertexModel> Vertices
            {
                get;
            }

            /// <summary>Vertex id -&gt; dense index into <see cref="Vertices"/>. Membership is
            /// the in-scope check: an edge endpoint absent from this map is out of scope (or
            /// was removed) and its edge is not traversed — induced-subgraph semantics.</summary>
            public Dictionary<Int32, Int32> DenseIndexById
            {
                get;
            }

            /// <summary>The number of in-scope vertices.</summary>
            public Int32 Count => Vertices.Count;
        }

        private Boolean TryBuildWorkspace(out Workspace workspace, GraphAnalyticsDefinition definition,
            BudgetGuard budget)
        {
            workspace = null;

            // GetAllVertices is the id-ordered, tombstone-filtered, label-filtered snapshot
            // (scan-result-representation): one cheap materialisation per run.
            var vertices = _fallen8.GetAllVertices(definition.VertexLabel);

            var denseIndexById = new Dictionary<Int32, Int32>(vertices.Count);
            for (var i = 0; i < vertices.Count; i++)
            {
                if ((i & (GraphAnalyticsDefinition.BudgetCheckInterval - 1)) == 0 && budget.IsExhausted)
                {
                    return false;
                }

                denseIndexById[vertices[i].Id] = i;
            }

            workspace = new Workspace(vertices, denseIndexById);
            return true;
        }

        #endregion

        #region budget

        /// <summary>
        ///   The cooperative run budget: wall-clock deadline (zero/negative budget = unbounded)
        ///   plus cancellation. Checked every <see cref="GraphAnalyticsDefinition.BudgetCheckInterval"/>
        ///   vertices — analytics runs execute no user-supplied code, so cooperative checking is
        ///   genuinely sufficient (no hostile-delegate residual).
        /// </summary>
        protected readonly struct BudgetGuard
        {
            private readonly Stopwatch _stopwatch;
            private readonly TimeSpan _budget;
            private readonly CancellationToken _cancellationToken;

            internal BudgetGuard(Stopwatch stopwatch, TimeSpan budget, CancellationToken cancellationToken)
            {
                _stopwatch = stopwatch;
                _budget = budget;
                _cancellationToken = cancellationToken;
            }

            /// <summary>Whether the run must stop now (deadline passed or cancellation requested).</summary>
            public Boolean IsExhausted =>
                _cancellationToken.IsCancellationRequested ||
                (_budget > TimeSpan.Zero && _stopwatch.Elapsed >= _budget);
        }

        #endregion

        #region shared helpers

        /// <summary>Reads a Double algorithm parameter (invariant conversion); false when absent,
        /// throws nothing — an unconvertible value reports as invalid via the out flag.</summary>
        protected static Boolean TryGetDoubleParameter(GraphAnalyticsDefinition definition,
            String name, out Double value, out Boolean invalid)
        {
            value = 0d;
            invalid = false;

            if (definition.Parameters == null || !definition.Parameters.TryGetValue(name, out var raw) || raw == null)
            {
                return false;
            }

            try
            {
                value = Convert.ToDouble(raw, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException)
            {
                invalid = true;
                return false;
            }
        }

        #endregion
    }
}
