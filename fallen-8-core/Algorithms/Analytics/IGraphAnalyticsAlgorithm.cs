// MIT License
//
// IGraphAnalyticsAlgorithm.cs
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

using NoSQL.GraphDB.Core.Plugin;

namespace NoSQL.GraphDB.Core.Algorithms.Analytics
{
    /// <summary>
    ///   The third plugin-discovered algorithm family (feature graph-analytics), alongside
    ///   path finding (<c>IShortestPathAlgorithm</c>) and subgraph extraction
    ///   (<c>ISubGraphAlgorithm</c>): whole-graph analytics that score or partition every
    ///   in-scope vertex (PageRank, connected components, communities, centrality, triangles).
    ///
    ///   <para>Implementations are discovered via <c>PluginFactory</c>, initialized with the
    ///   engine through <c>IPlugin.Initialize(IFallen8, parameters)</c>, cached in
    ///   <c>PluginCache.Analytics</c>, and invoked through the
    ///   <c>Fallen8.TryRunAnalytics(out result, name, definition)</c> facade.</para>
    ///
    ///   <para>Consistency (honest): a run is a lock-free read concurrent with the single
    ///   writer; there is no global snapshot. Every individual read is torn-free, but the
    ///   result is only exact for a quiescent graph — under concurrent mutation it is a
    ///   best-effort mixture of states. Callers needing exactness run against a quiet graph.</para>
    /// </summary>
    public interface IGraphAnalyticsAlgorithm : IPlugin
    {
        /// <summary>
        ///   Runs the analytics computation over the graph the plugin was Initialize()d with.
        /// </summary>
        /// <param name="result">The result: per-vertex scores or partitions plus run metadata.</param>
        /// <param name="definition">Scoping, budgets and algorithm parameters.</param>
        /// <returns>
        ///   False on an invalid definition or a run that produced no usable result (e.g. the
        ///   wall-clock budget exhausted before one full pass) — the Try* convention.
        /// </returns>
        bool TryRunAnalytics(out GraphAnalyticsResult result, GraphAnalyticsDefinition definition);
    }
}
