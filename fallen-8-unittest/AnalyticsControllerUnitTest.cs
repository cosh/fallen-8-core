// MIT License
//
// AnalyticsControllerUnitTest.cs
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
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Services;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.Expression;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Index.Fulltext;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Service;
using NoSQL.GraphDB.Core.StoredQueries;
using NoSQL.GraphDB.Core.SubGraph;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Direct controller tests for the analytics error paths a hosted pipeline cannot reach
    /// deterministically (feature graph-analytics): the 408 no-usable-result mapping and the
    /// 500 problem+json when a write-back chunk rolls back mid-way.
    /// </summary>
    [TestClass]
    public class AnalyticsControllerUnitTest
    {
        private Fallen8 _fallen8;

        [TestInitialize]
        public void TestInitialize()
        {
            _fallen8 = new Fallen8(TestLoggerFactory.Create());
        }

        [TestCleanup]
        public void TestCleanup()
        {
            _fallen8.Dispose();
        }

        private static AnalyticsController Controller(IFallen8 fallen8)
        {
            var options = Options.Create(new Fallen8AnalyticsOptions());
            return new AnalyticsController(
                TestLoggerFactory.Create().CreateLogger<AnalyticsController>(),
                fallen8, options, new AnalyticsRunGate(options));
        }

        [TestMethod]
        public void BudgetDeathWithNoUsableResult_Maps408ProblemJson()
        {
            // The stub's TryRunAnalytics returns false for a definition the controller already
            // validated - exactly the engine's budget-died-before-one-pass signal.
            var controller = Controller(new AnalyticsStubFallen8(_fallen8) { AnalyticsFails = true });

            var result = controller.RunAnalytics("DEGREE", new AnalyticsSpecification()).Result;

            var objectResult = result as ObjectResult;
            Assert.IsNotNull(objectResult);
            Assert.AreEqual(408, objectResult.StatusCode);
            Assert.IsTrue(objectResult.ContentTypes.Contains("application/problem+json"));
            var problem = objectResult.Value as ProblemDetails;
            Assert.IsNotNull(problem);
            Assert.AreEqual(408, problem.Status);
        }

        [TestMethod]
        public void WriteBackChunkRollback_Maps500ProblemJson_ReportingAppliedChunks()
        {
            // The run itself succeeds against the real engine; every write transaction is
            // reported rolled back - the executor stops, the controller answers a 500 problem
            // that states the chunk-atomic / not-run-atomic contract.
            var tx = new CreateVertexTransaction
            {
                Definition = new VertexDefinition { CreationDate = 1u, Label = "person" }
            };
            _fallen8.EnqueueTransaction(tx).WaitUntilFinished();

            var controller = Controller(new AnalyticsStubFallen8(_fallen8) { FailWrites = true });

            var result = controller.RunAnalytics("DEGREE", new AnalyticsSpecification { WriteBack = true }).Result;

            var objectResult = result as ObjectResult;
            Assert.IsNotNull(objectResult);
            Assert.AreEqual(500, objectResult.StatusCode);
            Assert.IsTrue(objectResult.ContentTypes.Contains("application/problem+json"));
            var problem = objectResult.Value as ProblemDetails;
            Assert.IsNotNull(problem);
            StringAssert.Contains(problem.Detail, "0 chunks", "no chunk was applied before the failure");
        }

        /// <summary>
        /// An <see cref="IFallen8"/> decorator with two knobs: <see cref="AnalyticsFails"/> makes
        /// <see cref="TryRunAnalytics"/> return false (the budget-death signal), and
        /// <see cref="FailWrites"/> reports every enqueued transaction as rolled back (a
        /// write-back chunk failure). Everything else forwards to the real engine.
        /// </summary>
        private sealed class AnalyticsStubFallen8 : IFallen8
        {
            private readonly IFallen8 _inner;

            public AnalyticsStubFallen8(IFallen8 inner)
            {
                _inner = inner;
            }

            public Boolean AnalyticsFails
            {
                get; set;
            }

            public Boolean FailWrites
            {
                get; set;
            }

            public bool TryRunAnalytics(out NoSQL.GraphDB.Core.Algorithms.Analytics.GraphAnalyticsResult result, string algorithmName, NoSQL.GraphDB.Core.Algorithms.Analytics.GraphAnalyticsDefinition definition)
            {
                if (AnalyticsFails)
                {
                    result = null;
                    return false;
                }

                return _inner.TryRunAnalytics(out result, algorithmName, definition);
            }

            public TransactionInformation EnqueueTransaction(ATransaction tx)
                => FailWrites
                    ? new TransactionInformation(null) { Transaction = tx, TransactionState = TransactionState.RolledBack }
                    : _inner.EnqueueTransaction(tx);

            // Everything below simply forwards to the real instance.
            public Guid Id => _inner.Id;
            public int VertexCount => _inner.VertexCount;
            public int EdgeCount => _inner.EdgeCount;
            public IndexFactory IndexFactory => _inner.IndexFactory;
            public ServiceFactory ServiceFactory => _inner.ServiceFactory;
            public SubGraphFactory SubGraphFactory => _inner.SubGraphFactory;
            public ISubGraphRecipeCompiler SubGraphRecipeCompiler
            {
                get => _inner.SubGraphRecipeCompiler;
                set => _inner.SubGraphRecipeCompiler = value;
            }
            public StoredQueryLibrary StoredQueries => _inner.StoredQueries;
            public NoSQL.GraphDB.Core.ChangeFeed.ChangeFeedDispatcher ChangeFeed => _inner.ChangeFeed;
            public IStoredQueryCompiler StoredQueryCompiler
            {
                get => _inner.StoredQueryCompiler;
                set => _inner.StoredQueryCompiler = value;
            }
            public ILoggerFactory LoggerFactory => _inner.LoggerFactory;
            public void SetId(Guid id) => _inner.SetId(id);
            public void ConfigureAutoTrim(bool enabled, int tombstoneThreshold) => _inner.ConfigureAutoTrim(enabled, tombstoneThreshold);
            public TransactionState GetTransactionState(string txId) => _inner.GetTransactionState(txId);
            public bool TryGetGraphElement(out AGraphElementModel result, int id) => _inner.TryGetGraphElement(out result, id);
            public bool TryGetEdge(out EdgeModel result, int id) => _inner.TryGetEdge(out result, id);
            public bool TryGetVertex(out VertexModel result, int id) => _inner.TryGetVertex(out result, id);
            public IReadOnlyList<VertexModel> GetAllVertices(string interestingLabel = null) => _inner.GetAllVertices(interestingLabel);
            public IReadOnlyList<EdgeModel> GetAllEdges(string interestingLabel = null) => _inner.GetAllEdges(interestingLabel);
            public IReadOnlyList<AGraphElementModel> GetAllGraphElements(string interestingLabel = null) => _inner.GetAllGraphElements(interestingLabel);
            public bool GraphScan(out List<AGraphElementModel> result, string propertyId, IComparable literal, BinaryOperator binOp = BinaryOperator.Equals, string interestingLabel = null)
                => _inner.GraphScan(out result, propertyId, literal, binOp, interestingLabel);
            public bool IndexScan(out IReadOnlyList<AGraphElementModel> result, string indexId, IComparable literal, BinaryOperator binOp = BinaryOperator.Equals)
                => _inner.IndexScan(out result, indexId, literal, binOp);
            public bool RangeIndexScan(out IReadOnlyList<AGraphElementModel> result, string indexId, IComparable leftLimit, IComparable rightLimit, bool includeLeft = true, bool includeRight = true)
                => _inner.RangeIndexScan(out result, indexId, leftLimit, rightLimit, includeLeft, includeRight);
            public bool FulltextIndexScan(out FulltextSearchResult result, string indexId, string searchQuery)
                => _inner.FulltextIndexScan(out result, indexId, searchQuery);
            public bool VectorIndexScan(out NoSQL.GraphDB.Core.Index.Vector.VectorSearchResult result, string indexId, float[] query, int k, NoSQL.GraphDB.Core.Index.Vector.VectorSearchConstraint constraint = null)
                => _inner.VectorIndexScan(out result, indexId, query, k, constraint);
            public bool TryCalculateShortestPath(out List<NoSQL.GraphDB.Core.Algorithms.Path.Path> result, string plugin, ShortestPathDefinition definition)
                => _inner.TryCalculateShortestPath(out result, plugin, definition);
            public bool TryCalculateShortestPath<T>(out List<NoSQL.GraphDB.Core.Algorithms.Path.Path> result, ShortestPathDefinition definition) where T : IShortestPathAlgorithm
                => _inner.TryCalculateShortestPath<T>(out result, definition);
        }
    }
}
