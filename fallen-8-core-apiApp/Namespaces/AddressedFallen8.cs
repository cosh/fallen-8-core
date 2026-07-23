// MIT License
//
// AddressedFallen8.cs
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
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.Expression;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Index.Fulltext;
using NoSQL.GraphDB.Core.Model;
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

        public AddressedFallen8(Fallen8Namespaces namespaces, IHttpContextAccessor httpContextAccessor)
        {
            _namespaces = namespaces;
            _httpContextAccessor = httpContextAccessor;
        }

        private Fallen8 Engine
        {
            get
            {
                var name = _httpContextAccessor.HttpContext?
                    .Request.RouteValues[NamespaceRouteConvention.RouteParameterName] as String;
                if (name == null)
                {
                    return _namespaces.Default.Engine;
                }

                if (!_namespaces.TryGet(name, out var ns))
                {
                    // The validation filter answered 404 before the action ran, so reaching this
                    // line means a drop/rename raced the request; the exception filter renders it
                    // as the same 404 problem+json.
                    throw new UnknownNamespaceException(name);
                }

                return ns.Engine;
            }
        }

        #region IFallen8Read

        public Guid Id => Engine.Id;

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

        public bool TryCalculateShortestPath(out List<Path> result, string plugin, ShortestPathDefinition definition)
            => Engine.TryCalculateShortestPath(out result, plugin, definition);

        public bool TryCalculateShortestPath<T>(out List<Path> result, ShortestPathDefinition definition)
            where T : IShortestPathAlgorithm
            => Engine.TryCalculateShortestPath<T>(out result, definition);

        public bool TryRunAnalytics(out Core.Algorithms.Analytics.GraphAnalyticsResult result,
            string algorithmName, Core.Algorithms.Analytics.GraphAnalyticsDefinition definition)
            => Engine.TryRunAnalytics(out result, algorithmName, definition);

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

        public Core.ChangeFeed.ChangeFeedDispatcher ChangeFeed => Engine.ChangeFeed;

        public IStoredQueryCompiler StoredQueryCompiler
        {
            get => Engine.StoredQueryCompiler;
            set => Engine.StoredQueryCompiler = value;
        }

        public ILoggerFactory LoggerFactory => Engine.LoggerFactory;

        public void SetId(Guid id) => Engine.SetId(id);

        public void ConfigureAutoTrim(bool enabled, int tombstoneThreshold)
            => Engine.ConfigureAutoTrim(enabled, tombstoneThreshold);

        #endregion
    }
}
