// MIT License
//
// SubGraphControllerTest.cs
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
using System.Collections.Immutable;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.App.Controllers.Model;
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
    /// End-to-end tests for <see cref="SubGraphController"/> against a real in-memory
    /// Fallen8 instance (controller → code generation → transaction → factory → algorithm).
    /// </summary>
    [TestClass]
    public class SubGraphControllerTest
    {
        private Fallen8 _fallen8;
        private SubGraphController _controller;

        [TestInitialize]
        public void TestInitialize()
        {
            var loggerFactory = TestLoggerFactory.Create();
            _fallen8 = new Fallen8(loggerFactory);
            _controller = new SubGraphController(loggerFactory.CreateLogger<SubGraphController>(), _fallen8);

            var creationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds());
            var verticesTx = new CreateVerticesTransaction();
            verticesTx.AddVertex(creationDate, "person", new Dictionary<string, object>() { { "name", "Alice" } });
            verticesTx.AddVertex(creationDate, "person", new Dictionary<string, object>() { { "name", "Bob" } });
            verticesTx.AddVertex(creationDate, "company", new Dictionary<string, object>() { { "name", "TechCorp" } });
            _fallen8.EnqueueTransaction(verticesTx).WaitUntilFinished();
            var v = verticesTx.GetCreatedVertices();

            var edgesTx = new CreateEdgesTransaction();
            edgesTx.AddEdge(v[0].Id, "knows", v[1].Id, creationDate, "knows");        // Alice -> Bob
            edgesTx.AddEdge(v[0].Id, "works_at", v[2].Id, creationDate, "works_at");   // Alice -> TechCorp
            _fallen8.EnqueueTransaction(edgesTx).WaitUntilFinished();
        }

        private static SubGraphSpecification PersonKnowsPerson(string name = "people")
        {
            return new SubGraphSpecification
            {
                Name = name,
                Patterns = new List<PatternSpecification>
                {
                    new PatternSpecification { Type = "Vertex", PatternName = "p1", GraphElementFilter = "return (ge) => ge.Label == \"person\";" },
                    new PatternSpecification { Type = "Edge", PatternName = "knows", Direction = "OutgoingEdge", EdgePropertyFilter = "return (p) => p == \"knows\";" },
                    new PatternSpecification { Type = "Vertex", PatternName = "p2", GraphElementFilter = "return (ge) => ge.Label == \"person\";" }
                }
            };
        }

        private static SubGraphSpecification AllPersons(string name = "persons")
        {
            return new SubGraphSpecification
            {
                Name = name,
                Patterns = new List<PatternSpecification>
                {
                    new PatternSpecification { Type = "Vertex", PatternName = "p", GraphElementFilter = "return (ge) => ge.Label == \"person\";" }
                }
            };
        }

        /// <summary>
        /// A pattern sequence that compiles fine but is structurally invalid: two vertex patterns
        /// in a row. Code generation accepts it (validation happens at execution), but the
        /// algorithm's ValidatePattern rejects it (a vertex pattern must be followed by an edge),
        /// so the create transaction returns false - a clean rollback, not a fault.
        /// </summary>
        private static SubGraphSpecification VertexThenVertex(string name = "invalid")
        {
            return new SubGraphSpecification
            {
                Name = name,
                Patterns = new List<PatternSpecification>
                {
                    new PatternSpecification { Type = "Vertex", PatternName = "a", GraphElementFilter = "return (ge) => ge.Label == \"person\";" },
                    new PatternSpecification { Type = "Vertex", PatternName = "b", GraphElementFilter = "return (ge) => ge.Label == \"person\";" }
                }
            };
        }

        [TestMethod]
        public void Create_ValidSpecification_Returns201WithSummary()
        {
            var result = _controller.CreateSubGraph(PersonKnowsPerson()).Result;

            var created = result as CreatedResult;
            Assert.IsNotNull(created, "Expected a 201 Created result");
            Assert.AreEqual(StatusCodes.Status201Created, created.StatusCode);

            var summary = created.Value as SubGraphSummary;
            Assert.IsNotNull(summary, "Created result must carry a summary");
            Assert.AreEqual("people", summary.Name);
            Assert.AreEqual(2, summary.VertexCount, "Alice and Bob are on the knows path");
            Assert.AreEqual(1, summary.EdgeCount, "Only the Alice->Bob knows edge is kept");
            Assert.IsTrue(summary.CanRecalculate, "Algorithm-created subgraphs can be recalculated");
        }

        [TestMethod]
        public void Create_DuplicateName_Returns409()
        {
            Assert.IsInstanceOfType(_controller.CreateSubGraph(PersonKnowsPerson()).Result, typeof(CreatedResult));

            var second = _controller.CreateSubGraph(PersonKnowsPerson()).Result;

            Assert.IsInstanceOfType(second, typeof(ConflictObjectResult), "Re-using a name must conflict");
        }

        [TestMethod]
        public void Create_NullSpecification_Returns400()
        {
            Assert.IsInstanceOfType(_controller.CreateSubGraph(null).Result, typeof(BadRequestObjectResult));
        }

        [TestMethod]
        public void Create_MissingName_Returns400()
        {
            var spec = PersonKnowsPerson();
            spec.Name = "   ";
            Assert.IsInstanceOfType(_controller.CreateSubGraph(spec).Result, typeof(BadRequestObjectResult));
        }

        [TestMethod]
        public void Create_InvalidFilterCode_Returns400WithDiagnostics()
        {
            var spec = AllPersons("bad");
            spec.Patterns[0].GraphElementFilter = "return (ge) => ge.Nope == 1;"; // will not compile

            var result = _controller.CreateSubGraph(spec).Result;

            var badRequest = result as BadRequestObjectResult;
            Assert.IsNotNull(badRequest, "Uncompilable filter must yield 400, not 500");
            Assert.IsNotNull(badRequest.Value, "The compiler diagnostics should be returned");
        }

        [TestMethod]
        public void GetAllNames_ReturnsRegisteredNames()
        {
            _ = _controller.CreateSubGraph(PersonKnowsPerson("a")).Result;
            _ = _controller.CreateSubGraph(AllPersons("b")).Result;
            var ok = _controller.GetAllSubGraphNames() as OkObjectResult;
            Assert.IsNotNull(ok);
            var names = ((IEnumerable<string>)ok.Value).ToList();
            CollectionAssert.AreEquivalent(new[] { "a", "b" }, names);
        }

        [TestMethod]
        public void GetSubGraph_Existing_Returns200_Missing_Returns404()
        {
            _ = _controller.CreateSubGraph(PersonKnowsPerson()).Result;
            var ok = _controller.GetSubGraph("people") as OkObjectResult;
            Assert.IsNotNull(ok, "Existing subgraph should return 200");
            Assert.AreEqual("people", ((SubGraphSummary)ok.Value).Name);

            Assert.IsInstanceOfType(_controller.GetSubGraph("does-not-exist"), typeof(NotFoundObjectResult));
        }

        [TestMethod]
        public void GetSubGraphContents_ReturnsVerticesAndEdges()
        {
            _ = _controller.CreateSubGraph(PersonKnowsPerson()).Result;
            var ok = _controller.GetSubGraphContents("people") as OkObjectResult;
            Assert.IsNotNull(ok);
            var graph = ok.Value as Graph;
            Assert.IsNotNull(graph);
            Assert.AreEqual(2, graph.Vertices.Count, "Alice and Bob");
            Assert.AreEqual(1, graph.Edges.Count, "The knows edge");
        }

        [TestMethod]
        public void GetSubGraphContents_Missing_Returns404()
        {
            Assert.IsInstanceOfType(_controller.GetSubGraphContents("nope"), typeof(NotFoundObjectResult));
        }

        [TestMethod]
        public void Recalculate_ReflectsSourceChanges()
        {
            _ = _controller.CreateSubGraph(AllPersons()).Result;
            var before = (SubGraphSummary)((OkObjectResult)_controller.GetSubGraph("persons")).Value;
            Assert.AreEqual(2, before.VertexCount, "Alice and Bob");

            // Add another person to the source graph.
            var creationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds());
            var tx = new CreateVerticesTransaction();
            tx.AddVertex(creationDate, "person", new Dictionary<string, object>() { { "name", "Carol" } });
            _fallen8.EnqueueTransaction(tx).WaitUntilFinished();

            var recalc = _controller.RecalculateSubGraph("persons") as OkObjectResult;
            Assert.IsNotNull(recalc, "Recalculation of an algorithm-created subgraph should succeed");
            Assert.AreEqual(3, ((SubGraphSummary)recalc.Value).VertexCount, "Carol should now be included");
        }

        [TestMethod]
        public void Recalculate_Missing_Returns404()
        {
            Assert.IsInstanceOfType(_controller.RecalculateSubGraph("nope"), typeof(NotFoundObjectResult));
        }

        [TestMethod]
        public void Delete_Existing_Returns204_ThenGoneAnd404OnSecondDelete()
        {
            _ = _controller.CreateSubGraph(PersonKnowsPerson()).Result;
            Assert.IsInstanceOfType(_controller.DeleteSubGraph("people"), typeof(NoContentResult));
            Assert.IsInstanceOfType(_controller.GetSubGraph("people"), typeof(NotFoundObjectResult),
                "After deletion the subgraph must be gone");
            Assert.IsInstanceOfType(_controller.DeleteSubGraph("people"), typeof(NotFoundObjectResult),
                "Deleting a non-existent subgraph must be 404");
        }

        [TestMethod]
        public void Create_DoesNotMutateSourceGraph()
        {
            var sourceVertexCount = _fallen8.VertexCount;
            var sourceEdgeCount = _fallen8.EdgeCount;

            _ = _controller.CreateSubGraph(PersonKnowsPerson()).Result;
            Assert.AreEqual(sourceVertexCount, _fallen8.VertexCount, "Source graph vertices must be unchanged");
            Assert.AreEqual(sourceEdgeCount, _fallen8.EdgeCount, "Source graph edges must be unchanged");
        }

        // ---- CreateSubGraph outcome mapping: a clean rollback is 400, a genuine fault is 500 ----

        [TestMethod]
        public void Create_OnEmptyGraph_Returns201WithEmptySubGraph()
        {
            // MIGRATED (transaction-failure-reasons): the empty-graph and populated-no-match paths
            // now behave IDENTICALLY. A syntactically-valid pattern that matches nothing (here
            // because the source graph is empty) is a valid EMPTY result -> 201 with an empty
            // subgraph, NOT the former 400. This is the exact same outcome as
            // Create_WhenPatternMatchesNothingOnPopulatedGraph_Returns201; the two are pinned
            // together so the "no-match" contract cannot silently diverge again.
            var emptyLoggerFactory = TestLoggerFactory.Create();
            var emptyFallen8 = new Fallen8(emptyLoggerFactory);
            var controller = new SubGraphController(
                emptyLoggerFactory.CreateLogger<SubGraphController>(), emptyFallen8);

            var result = controller.CreateSubGraph(AllPersons()).Result;

            Assert.AreEqual(StatusCodes.Status201Created, StatusCodeOf(result),
                "An empty source graph with a valid pattern is a valid empty result -> 201, not 400.");

            var created = result as CreatedResult;
            Assert.IsNotNull(created, "Expected a 201 Created result carrying a summary.");
            var summary = created.Value as SubGraphSummary;
            Assert.IsNotNull(summary, "A summary must be returned even when the subgraph is empty.");
            Assert.AreEqual(0, summary.VertexCount, "An empty source graph yields an empty subgraph.");
            Assert.AreEqual(0, summary.EdgeCount, "An empty subgraph has no edges.");
        }

        [TestMethod]
        public void Create_WhenPatternMatchesNothingOnPopulatedGraph_Returns201()
        {
            // Contract pin: on the POPULATED fixture graph, a valid, compilable pattern whose filter
            // matches no vertex returns 201 with an EMPTY subgraph. Since transaction-failure-reasons
            // this is IDENTICAL to the empty-source-graph case (Create_OnEmptyGraph_Returns201WithEmptySubGraph):
            // a syntactically-valid pattern that matches nothing is always a valid empty result (201),
            // never a 400. 400 is reserved for a structurally-invalid pattern; 409 for a quota breach.
            var spec = AllPersons("empty-match");
            spec.Patterns[0].GraphElementFilter = "return (ge) => ge.Label == \"nonexistent\";";

            var result = _controller.CreateSubGraph(spec).Result;

            Assert.AreEqual(StatusCodes.Status201Created, StatusCodeOf(result),
                "A populated-graph pattern that matches nothing returns 201 with an empty subgraph.");

            var created = result as CreatedResult;
            Assert.IsNotNull(created, "Expected a 201 Created result carrying a summary.");
            var summary = created.Value as SubGraphSummary;
            Assert.IsNotNull(summary, "A summary must be returned even when the subgraph is empty.");
            Assert.AreEqual("empty-match", summary.Name);
            Assert.AreEqual(0, summary.VertexCount, "The pattern matched no vertex, so the subgraph is empty.");
            Assert.AreEqual(0, summary.EdgeCount, "An empty subgraph has no edges.");
        }

        [TestMethod]
        public void Create_WhenPatternStructurallyInvalid_Returns400()
        {
            // Two vertex patterns in a row compile fine but fail the algorithm's ValidatePattern at
            // execution, so the create transaction returns false: a clean rollback, hence 400.
            var result = _controller.CreateSubGraph(VertexThenVertex()).Result;

            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult),
                "A structurally-invalid pattern is a clean rollback and must be 400, not 500.");
            Assert.AreEqual(StatusCodes.Status400BadRequest, StatusCodeOf(result));
        }

        [TestMethod]
        public void Create_WhenElementQuotaExceeded_Returns409()
        {
            // MIGRATED (transaction-failure-reasons): a post-materialization element-quota breach is
            // a clean QuotaExceeded rollback. ALL quota breaches (this per-subgraph/total element
            // ceiling AND the up-front subgraph-count ceiling) now share ONE status - 409 - instead
            // of the former 400-vs-409 split. AllPersons materializes 2 person vertices; cap at 1.
            _fallen8.SubGraphFactory.Quota = new SubGraphQuota { MaxElementsPerSubGraph = 1 };

            var result = _controller.CreateSubGraph(AllPersons()).Result;

            Assert.IsInstanceOfType(result, typeof(ConflictObjectResult),
                "A quota breach must be 409 (QuotaExceeded), consistent with the count-ceiling breach.");
            Assert.AreEqual(StatusCodes.Status409Conflict, StatusCodeOf(result));
        }

        [TestMethod]
        public void Create_WhenTransactionFaults_Returns500()
        {
            // Drive CreateSubGraph against a Fallen8 whose create transaction reports RolledBack AND
            // carries a genuine exception (txInfo.Error != null). Only a real fault - not an empty
            // match, invalid pattern or quota breach - must map to 500.
            var faultingFallen8 = new RollbackForcingFallen8(
                _fallen8, new InvalidOperationException("simulated internal fault"));
            var controller = new SubGraphController(
                TestLoggerFactory.Create().CreateLogger<SubGraphController>(), faultingFallen8);

            var result = controller.CreateSubGraph(AllPersons("faulting")).Result;

            Assert.AreEqual(StatusCodes.Status500InternalServerError, StatusCodeOf(result),
                "A create whose transaction faulted with an exception must be reported as 500.");
        }

        [TestMethod]
        public void Delete_WhenRemoveTransactionRollsBack_Returns500()
        {
            // Register the subgraph so the controller's existence check passes (would 404 otherwise)...
            Assert.IsInstanceOfType(_controller.CreateSubGraph(PersonKnowsPerson()).Result, typeof(CreatedResult));

            // ...then drive DeleteSubGraph against a Fallen8 whose remove transaction reports
            // RolledBack. Before the fix DeleteSubGraph returned 204 regardless; it must now return 500.
            var rollbackFallen8 = new RollbackForcingFallen8(_fallen8);
            var controller = new SubGraphController(
                TestLoggerFactory.Create().CreateLogger<SubGraphController>(), rollbackFallen8);

            var result = controller.DeleteSubGraph("people");

            Assert.AreEqual(StatusCodes.Status500InternalServerError, StatusCodeOf(result),
                "A delete whose remove transaction rolled back must be reported as 500, not 204.");
        }

        private static int StatusCodeOf(IActionResult result)
        {
            var statusResult = result as IStatusCodeActionResult;
            Assert.IsNotNull(statusResult,
                "Expected a status-code action result but got " + (result?.GetType().Name ?? "null") + ".");
            Assert.IsTrue(statusResult.StatusCode.HasValue, "Expected an explicit status code.");
            return statusResult.StatusCode.Value;
        }

        /// <summary>
        /// An <see cref="IFallen8"/> decorator whose <see cref="EnqueueTransaction"/> reports every
        /// transaction as <see cref="TransactionState.RolledBack"/> without running it; every other
        /// member forwards to a real inner instance so the controller's pre-checks (e.g.
        /// <see cref="SubGraphFactory"/> lookups) behave normally. Lets a controller test drive the
        /// "the worker rolled the write back" branch deterministically. When an <c>error</c> is
        /// supplied it is exposed as <see cref="TransactionInformation.Error"/>, mirroring a
        /// genuine fault (versus a clean rollback when it is null).
        /// </summary>
        private sealed class RollbackForcingFallen8 : IFallen8
        {
            private readonly IFallen8 _inner;
            private readonly Exception _error;

            public RollbackForcingFallen8(IFallen8 inner, Exception error = null)
            {
                _inner = inner;
                _error = error;
            }

            public TransactionInformation EnqueueTransaction(ATransaction tx)
                => new TransactionInformation(null) { Transaction = tx, TransactionState = TransactionState.RolledBack, Error = _error };

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
            public bool TryRunAnalytics(out NoSQL.GraphDB.Core.Algorithms.Analytics.GraphAnalyticsResult result, string algorithmName, NoSQL.GraphDB.Core.Algorithms.Analytics.GraphAnalyticsDefinition definition)
                => _inner.TryRunAnalytics(out result, algorithmName, definition);
        }
    }
}
