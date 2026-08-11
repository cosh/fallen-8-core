// MIT License
//
// AddressedFallen8.cs
//
// Copyright (c) 2011-2026 Henning Rauch
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
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.Expression;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Index.Fulltext;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Plugins;
using NoSQL.GraphDB.Core.Service;
using NoSQL.GraphDB.Core.StoredQueries;
using NoSQL.GraphDB.Core.SubGraph;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.App.Namespaces
{
    /// <summary>
    ///   The <see cref="IFallen8"/> the controllers see: every member delegates to the ADDRESSED
    ///   namespace's engine, resolved per call from the ambient request's <c>ns</c> route value
    ///   (bare routes carry none and alias the default namespace; outside a request - hosted
    ///   services, tests resolving from the root provider - it is the default namespace's engine).
    ///
    ///   Deliberately NOT <see cref="IDisposable"/>: the DI container disposes IDisposable
    ///   instances its factories return (per scope for non-singletons), which would tear an engine
    ///   down at the end of the first request. Engine lifetime belongs exclusively to
    ///   <see cref="Fallen8Namespaces"/>.
    /// </summary>
    public sealed class AddressedFallen8 : IFallen8
    {
        private readonly Fallen8Namespaces _namespaces;
        private readonly IHttpContextAccessor _httpContextAccessor;

        /// <summary>
        ///   An off-request-thread namespace override (feature semantic-layer). Requests address
        ///   a namespace through the route value on <see cref="IHttpContextAccessor"/>, but the
        ///   ingestion worker runs OFF the request thread, where that ambient does not flow. A job
        ///   carries its namespace name and the worker pushes it here for the duration of
        ///   processing, so the existing resolution keeps working unchanged. It is an
        ///   <see cref="AsyncLocal{T}"/>, so it is isolated per async flow (no leak between jobs
        ///   or to request threads, which never set it). The override wins over the route ONLY
        ///   when set (worker context); every request path is unaffected.
        /// </summary>
        private static readonly AsyncLocal<String> _ambientNamespace = new AsyncLocal<String>();

        public AddressedFallen8(Fallen8Namespaces namespaces, IHttpContextAccessor httpContextAccessor)
        {
            _namespaces = namespaces;
            _httpContextAccessor = httpContextAccessor;
        }

        /// <summary>Binds the addressed namespace for the current async flow (the ingestion
        /// worker uses this around one job); dispose restores the prior binding.</summary>
        public static IDisposable PushNamespace(String name)
        {
            var previous = _ambientNamespace.Value;
            _ambientNamespace.Value = name;
            return new NamespaceScope(previous);
        }

        private sealed class NamespaceScope : IDisposable
        {
            private readonly String _previous;
            public NamespaceScope(String previous) => _previous = previous;
            public void Dispose() => _ambientNamespace.Value = _previous;
        }

        private Fallen8 Engine
        {
            get
            {
                // The worker-pushed override wins when present; otherwise the request route value.
                var name = _ambientNamespace.Value ?? _httpContextAccessor.HttpContext?
                    .Request.RouteValues[NamespaceRouteConvention.RouteParameterName] as String;
                if (name == null)
                {
                    return _namespaces.Default.Engine;
                }

                if (!_namespaces.TryGet(name, out var ns))
                {
                    // The validation filter answered 404 before the action ran, so reaching this
                    // line means a drop/rename raced the request; the exception filter renders it
                    // as the same 404 problem+json. On the worker path the job is skipped instead.
                    throw new UnknownNamespaceException(name);
                }

                return ns.Engine;
            }
        }

        #region IFallen8Read

        public Guid Id => Engine.Id;

        /// <summary>Durability/recovery state of the ADDRESSED namespace's engine (feature
        /// platform-integrity-audit W5): each namespace owns its own log and its own recovery outcome,
        /// so this is per-engine and not per-process.</summary>
        public DurabilityState Durability => Engine.Durability;

        public Int32 VertexCount => Engine.VertexCount;

        public Int32 EdgeCount => Engine.EdgeCount;

        public Boolean TryGetGraphElement(out AGraphElementModel result, Int32 id)
            => Engine.TryGetGraphElement(out result, id);

        public Boolean TryGetEdge(out EdgeModel result, Int32 id)
            => Engine.TryGetEdge(out result, id);

        public Boolean TryGetVertex(out VertexModel result, Int32 id)
            => Engine.TryGetVertex(out result, id);

        public IReadOnlyList<VertexModel> GetAllVertices(String interestingLabel = null)
            => Engine.GetAllVertices(interestingLabel);

        public IReadOnlyList<EdgeModel> GetAllEdges(String interestingLabel = null)
            => Engine.GetAllEdges(interestingLabel);

        public IReadOnlyList<AGraphElementModel> GetAllGraphElements(String interestingLabel = null)
            => Engine.GetAllGraphElements(interestingLabel);

        public Boolean GraphScan(out List<AGraphElementModel> result, String propertyId, IComparable literal,
                                 BinaryOperator binOp = BinaryOperator.Equals, String interestingLabel = null)
            => Engine.GraphScan(out result, propertyId, literal, binOp, interestingLabel);

        public Boolean GraphScanAllProperties(out List<AGraphElementModel> result, String searchTerm,
                                              String interestingLabel = null)
            => Engine.GraphScanAllProperties(out result, searchTerm, interestingLabel);

        public Boolean IndexScan(out IReadOnlyList<AGraphElementModel> result, String indexId, IComparable literal,
                                 BinaryOperator binOp = BinaryOperator.Equals)
            => Engine.IndexScan(out result, indexId, literal, binOp);

        public Boolean RangeIndexScan(out IReadOnlyList<AGraphElementModel> result, String indexId, IComparable leftLimit,
                                      IComparable rightLimit, Boolean includeLeft = true, Boolean includeRight = true)
            => Engine.RangeIndexScan(out result, indexId, leftLimit, rightLimit, includeLeft, includeRight);

        public Boolean FulltextIndexScan(out FulltextSearchResult result, String indexId, String searchQuery)
            => Engine.FulltextIndexScan(out result, indexId, searchQuery);

        public Boolean VectorIndexScan(out Core.Index.Vector.VectorSearchResult result, String indexId,
            Single[] query, Int32 k, Core.Index.Vector.VectorSearchConstraint constraint = null)
            => Engine.VectorIndexScan(out result, indexId, query, k, constraint);

        // The string-named overloads resolve their plugin through discovery, so the engine declares them
        // not trim-safe; an implementation of an annotated interface member must repeat the annotation
        // (the analyzer requires them to match exactly). The message is the engine's own const, so the
        // forwarder and the member it forwards to never say different things.
        [RequiresUnreferencedCode(Core.Plugin.PluginFactory.DiscoveryIsNotTrimSafe)]
        public bool TryCalculateShortestPath(out List<Path> result, string plugin, ShortestPathDefinition definition)
            => Engine.TryCalculateShortestPath(out result, plugin, definition);

        // The annotation on T is REQUIRED to match the interface declaration: without it, forwarding to
        // the engine's annotated overload is a trim-analysis mismatch (the engine reflectively constructs
        // T, so its constructor must be kept).
        public bool TryCalculateShortestPath<
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(
            out List<Path> result, ShortestPathDefinition definition)
            where T : IShortestPathAlgorithm
            => Engine.TryCalculateShortestPath<T>(out result, definition);

        [RequiresUnreferencedCode(Core.Plugin.PluginFactory.DiscoveryIsNotTrimSafe)]
        public bool TryRunAnalytics(out Core.Algorithms.Analytics.GraphAnalyticsResult result,
            string algorithmName, Core.Algorithms.Analytics.GraphAnalyticsDefinition definition)
            => Engine.TryRunAnalytics(out result, algorithmName, definition);

        public bool TryInvokeGraphFunction(out Core.Plugins.GraphFunctionResult result, string name,
            IDictionary<String, Object> parameters)
            => Engine.TryInvokeGraphFunction(out result, name, parameters);

        #endregion

        #region IFallen8Write

        public TransactionInformation EnqueueTransaction(ATransaction tx)
            => Engine.EnqueueTransaction(tx);

        public TransactionState GetTransactionState(String txId)
            => Engine.GetTransactionState(txId);

        #endregion

        #region IFallen8Admin

        public IndexFactory IndexFactory => Engine.IndexFactory;

        public ServiceFactory ServiceFactory => Engine.ServiceFactory;

        public SubGraphFactory SubGraphFactory => Engine.SubGraphFactory;

        public ISubGraphRecipeCompiler SubGraphRecipeCompiler
        {
            get => Engine.SubGraphRecipeCompiler;
            set => Engine.SubGraphRecipeCompiler = value;
        }

        public StoredQueryLibrary StoredQueries => Engine.StoredQueries;

        public PluginRegistry Plugins => Engine.Plugins;

        public Core.ChangeFeed.ChangeFeedDispatcher ChangeFeed => Engine.ChangeFeed;

        public IStoredQueryCompiler StoredQueryCompiler
        {
            get => Engine.StoredQueryCompiler;
            set => Engine.StoredQueryCompiler = value;
        }

        public IPluginCompiler PluginCompiler
        {
            get => Engine.PluginCompiler;
            set => Engine.PluginCompiler = value;
        }

        public ILoggerFactory LoggerFactory => Engine.LoggerFactory;

        public void SetId(Guid id) => Engine.SetId(id);

        public void ConfigureAutoTrim(bool enabled, int tombstoneThreshold)
            => Engine.ConfigureAutoTrim(enabled, tombstoneThreshold);

        #endregion
    }
}
