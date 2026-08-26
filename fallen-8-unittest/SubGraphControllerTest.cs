// MIT License
//
// SubGraphControllerTest.cs
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
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
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
using NoSQL.GraphDB.Core.Algorithms.SubGraph;
using NoSQL.GraphDB.Core.App.Controllers.Model;
using NoSQL.GraphDB.Core.Expression;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Index.Fulltext;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Plugins;
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
                    new PatternSpecification { Type = "Vertex", PatternName = "p1", VertexFilter = "return (v) => v.Label == \"person\";" },
                    new PatternSpecification { Type = "Edge", PatternName = "knows", Direction = "OutgoingEdge", EdgePropertyFilter = "return (p) => p == \"knows\";" },
                    new PatternSpecification { Type = "Vertex", PatternName = "p2", VertexFilter = "return (v) => v.Label == \"person\";" }
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
                    new PatternSpecification { Type = "Vertex", PatternName = "p", VertexFilter = "return (v) => v.Label == \"person\";" }
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
                    new PatternSpecification { Type = "Vertex", PatternName = "a", VertexFilter = "return (v) => v.Label == \"person\";" },
                    new PatternSpecification { Type = "Vertex", PatternName = "b", VertexFilter = "return (v) => v.Label == \"person\";" }
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

            ProblemAssert.AssertProblem(second, StatusCodes.Status409Conflict);
        }

        [TestMethod]
        public void Create_NullSpecification_Returns400()
        {
            ProblemAssert.AssertProblem(_controller.CreateSubGraph(null).Result, StatusCodes.Status400BadRequest);
        }

        [TestMethod]
        public void Create_MissingName_Returns400()
        {
            var spec = PersonKnowsPerson();
            spec.Name = "   ";
            ProblemAssert.AssertProblem(_controller.CreateSubGraph(spec).Result, StatusCodes.Status400BadRequest);
        }

        [TestMethod]
        public void Create_InvalidFilterCode_Returns400WithDiagnostics()
        {
            var spec = AllPersons("bad");
            spec.Patterns[0].VertexFilter = "return (v) => v.Nope == 1;"; // will not compile

            var result = _controller.CreateSubGraph(spec).Result;

            var problem = ProblemAssert.AssertProblem(result, StatusCodes.Status400BadRequest);
            Assert.IsNotNull(problem.Detail, "The compiler diagnostics should be returned");
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

            ProblemAssert.AssertProblem(_controller.GetSubGraph("does-not-exist"), StatusCodes.Status404NotFound);
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
            ProblemAssert.AssertProblem(_controller.GetSubGraphContents("nope"), StatusCodes.Status404NotFound);
        }

        [TestMethod]
        public void GetAvailableSubGraphPlugins_DiscoversTheBuiltInBfsAlgorithm()
        {
            // Feature: subgraph discovery parity with path/analytics/index. The accessor (surfaced on
            // GET /status via AvailableSubGraphPlugins) must include the built-in BFS algorithm by its
            // registered plugin name; before wiring it was an unused, never-surfaced enumerator.
            var available = _fallen8.SubGraphFactory.GetAvailableSubGraphPlugins().ToList();
            Assert.IsTrue(available.Contains("Breadth First Search Subgraph Algorithm"),
                "The built-in BFS subgraph algorithm must be discoverable. Got: " + string.Join(", ", available));
        }

        [TestMethod]
        public void GetSubGraphContents_ClampsAndHandlesNegativeMaxElements()
        {
            // Feature api-error-contract E6: the bounded-read clamp is pinned for GetSubGraphContents,
            // not only GetGraph. A huge maxElements must clamp (never an unbounded materialization) and
            // a negative must not fall through to Take(negative) - mirroring GetGraph_ClampsAndHandlesNegativeMaxElements.
            _ = _controller.CreateSubGraph(PersonKnowsPerson()).Result;

            var big = _controller.GetSubGraphContents("people", int.MaxValue) as OkObjectResult;
            Assert.IsNotNull(big);
            var bigGraph = big.Value as Graph;
            Assert.IsNotNull(bigGraph);
            Assert.AreEqual(2, bigGraph.Vertices.Count, "A clamped read still returns every available vertex here.");
            Assert.AreEqual(1, bigGraph.Edges.Count);

            var negative = _controller.GetSubGraphContents("people", -5) as OkObjectResult;
            Assert.IsNotNull(negative);
            var negGraph = negative.Value as Graph;
            Assert.IsNotNull(negGraph);
            Assert.AreEqual(0, negGraph.Vertices.Count, "A negative maxElements yields an empty page, not a Take(negative) crash.");
            Assert.AreEqual(0, negGraph.Edges.Count);
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
            ProblemAssert.AssertProblem(_controller.RecalculateSubGraph("nope"), StatusCodes.Status404NotFound);
        }

        [TestMethod]
        public void Delete_Existing_Returns204_ThenGoneAnd404OnSecondDelete()
        {
            _ = _controller.CreateSubGraph(PersonKnowsPerson()).Result;
            Assert.IsInstanceOfType(_controller.DeleteSubGraph("people").Result, typeof(NoContentResult));
            ProblemAssert.AssertProblem(_controller.GetSubGraph("people"), StatusCodes.Status404NotFound);
            ProblemAssert.AssertProblem(_controller.DeleteSubGraph("people").Result, StatusCodes.Status404NotFound);
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
            spec.Patterns[0].VertexFilter = "return (v) => v.Label == \"nonexistent\";";

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

            ProblemAssert.AssertProblem(result, StatusCodes.Status400BadRequest);
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

            ProblemAssert.AssertProblem(result, StatusCodes.Status409Conflict);
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

            var result = controller.DeleteSubGraph("people").Result;

            Assert.AreEqual(StatusCodes.Status500InternalServerError, StatusCodeOf(result),
                "A delete whose remove transaction rolled back must be reported as 500, not 204.");
        }

        // ---- The algorithm selector reaches the factory (audit defect B28) ----
        // PUT /subgraph carried no algorithm selector, so a registered ISubGraphAlgorithm was
        // advertised by GET /status but could never be run: every request silently used the
        // built-in. These tests run against their own three-person source graph, built by
        // UseThreePersonGraph, rather than against the class fixture.

        /// <summary>
        /// Repoints <c>_fallen8</c> and <c>_controller</c> at a source graph of exactly three
        /// person vertices and no edges: the fixture the algorithm-selector tests below were
        /// written against. The class fixture (two persons, one company, two edges) would change
        /// every element count they assert, so they build their own graph, exactly as the
        /// empty-graph and faulting-transaction tests above do.
        /// </summary>
        private void UseThreePersonGraph()
        {
            var loggerFactory = TestLoggerFactory.Create();
            _fallen8 = new Fallen8(loggerFactory);
            _controller = new SubGraphController(loggerFactory.CreateLogger<SubGraphController>(), _fallen8);

            AddPerson("Alice");
            AddPerson("Bob");
            AddPerson("Carol");
        }

        private void AddPerson(String name)
        {
            var tx = new CreateVerticesTransaction();
            tx.AddVertex(Convert.ToUInt32(DateTimeOffset.UtcNow.ToUnixTimeSeconds()), "person",
                new Dictionary<String, Object> { { "name", name } });
            _fallen8.EnqueueTransaction(tx).WaitUntilFinished();
        }

        /// <summary>The REST specification selecting every person vertex (no pruning edges).</summary>
        private static SubGraphSpecification AllPersonsSpecification(String name, String algorithm = null)
        {
            return new SubGraphSpecification
            {
                Name = name,
                Algorithm = algorithm,
                Patterns = new List<PatternSpecification>
                {
                    new PatternSpecification { Type = "Vertex", PatternName = "p", VertexFilter = "return (v) => v.Label == \"person\";" }
                }
            };
        }

        /// <summary>The engine-level equivalent of <see cref="AllPersonsSpecification"/>.</summary>
        private static SubGraphDefinition AllPersonsDefinition(String name)
        {
            return new SubGraphDefinition
            {
                Name = name,
                Pattern = new List<APattern>
                {
                    new VertexPattern { PatternName = "p", Vertex = v => v.Label == "person" }
                }
            };
        }

        /// <summary>
        /// Registers a plugin straight through the register transaction with a pre-pinned artifact
        /// type (the engine never compiles - the registry does not inspect the artifact, exactly as
        /// PluginRegistryTest does it), so these tests need no Roslyn.
        /// </summary>
        private void RegisterPlugin(String name, PluginContract contract, Type artifact)
        {
            var definition = new PluginDefinition
            {
                Name = name,
                Category = PluginCategory.Algorithm,
                Contract = contract,
                SourceCode = "// pinned artifact; the engine never compiles",
                Description = "audit-defect test plugin",
                CreatedAt = DateTime.UtcNow
            };

            var info = _fallen8.EnqueueTransaction(new RegisterPluginTransaction
            {
                Entry = new PluginEntry(definition, PluginCompileState.Compiled, artifact)
            });
            info.WaitUntilFinished();

            Assert.AreEqual(TransactionState.Finished, info.TransactionState,
                "the test plugin must register before it can be selected");
        }

        private SubGraphResult Registered(String name)
        {
            Assert.IsTrue(_fallen8.SubGraphFactory.TryGetSubGraph(out var result, name),
                String.Format("subgraph '{0}' should be registered", name));
            return result;
        }

        /// <summary>
        /// A runtime-registerable subgraph algorithm whose output is unmistakably NOT the built-in
        /// breadth-first search: exactly ONE vertex labelled <c>marker</c>, carrying the source
        /// graph's vertex count at extraction time. The count makes a re-run observable, so a
        /// recalculation cannot be mistaken for a cached result. Its <c>PluginName</c> equals
        /// the name it is registered under, which is what the API's plugin compiler enforces for a
        /// real registration.
        /// </summary>
        public sealed class MarkerSubGraphAlgorithm : ISubGraphAlgorithm
        {
            /// <summary>The registration name (and therefore the plugin name).</summary>
            public const String RegisteredName = "marker-subgraph-algo";

            /// <summary>The label of the single vertex this algorithm materializes.</summary>
            public const String MarkerLabel = "marker";

            /// <summary>The property carrying the source vertex count seen at extraction time.</summary>
            public const String SourceCountProperty = "sourceVertexCount";

            private IFallen8 _source;

            /// <summary>Public parameterless constructor: the registry activates by <c>Activator</c>.</summary>
            public MarkerSubGraphAlgorithm()
            {
            }

            /// <inheritdoc />
            public String PluginName => RegisteredName;

            /// <inheritdoc />
            public Type PluginCategory => typeof(ISubGraphAlgorithm);

            /// <inheritdoc />
            public String Description => "Materializes a single marker vertex; used by the audit-defect tests.";

            /// <inheritdoc />
            public String Manufacturer => "Fallen-8 test suite";

            /// <inheritdoc />
            public void Initialize(IFallen8 fallen8, IDictionary<String, Object> parameter)
            {
                _source = fallen8;
            }

            /// <inheritdoc />
            public Boolean TryCreateSubgraph(out SubGraphResult result, SubGraphDefinition definition)
            {
                result = null;

                if (definition == null || _source == null)
                {
                    return false;
                }

                var subgraph = new Fallen8(_source.LoggerFactory);

                var tx = new CreateVerticesTransaction();
                tx.AddVertex(Convert.ToUInt32(DateTimeOffset.UtcNow.ToUnixTimeSeconds()), MarkerLabel,
                    new Dictionary<String, Object> { { SourceCountProperty, _source.VertexCount } });
                subgraph.EnqueueTransaction(tx).WaitUntilFinished();

                result = new SubGraphResult
                {
                    Definitions = definition,
                    SubGraph = subgraph,
                    SourceFallen8 = _source,
                    SourceFallen8Id = _source.Id,
                    AlgorithmPluginName = PluginName,
                    AlgorithmParameters = null
                };

                return true;
            }

            /// <inheritdoc />
            public void Dispose()
            {
            }
        }

        /// <summary>The marker vertex's recorded source vertex count.</summary>
        private static int MarkerSourceCount(SubGraphResult result)
        {
            var vertices = result.SubGraph.GetAllVertices().ToList();
            Assert.AreEqual(1, vertices.Count, "the marker algorithm materializes exactly one vertex");
            Assert.AreEqual(MarkerSubGraphAlgorithm.MarkerLabel, vertices[0].Label,
                "the single vertex must be the marker, proving the registered plugin ran");
            Assert.IsTrue(vertices[0].TryGetProperty(out object count, MarkerSubGraphAlgorithm.SourceCountProperty),
                "the marker carries the source vertex count");
            return Convert.ToInt32(count);
        }

        [TestMethod]
        public void Create_WithoutAlgorithm_UsesTheBuiltInBreadthFirstSearch()
        {
            UseThreePersonGraph();

            var created = _controller.CreateSubGraph(AllPersonsSpecification("default-algo")).Result as CreatedResult;

            Assert.IsNotNull(created, "a specification without an algorithm must still create the subgraph");
            var summary = created.Value as SubGraphSummary;
            Assert.IsNotNull(summary);
            Assert.AreEqual(BreadthFirstSearchSubgraphAlgorithm.AlgorithmPluginName, summary.AlgorithmPluginName,
                "the built-in stays the default, so every pre-existing request behaves identically");
            Assert.AreEqual(3, summary.VertexCount, "BFS copies all three persons");
        }

        [TestMethod]
        public void Create_WithTheBuiltInNameSpelledOut_BehavesLikeTheDefault()
        {
            UseThreePersonGraph();

            var created = _controller.CreateSubGraph(
                AllPersonsSpecification("explicit-builtin", BreadthFirstSearchSubgraphAlgorithm.AlgorithmPluginName)).Result as CreatedResult;

            Assert.IsNotNull(created, "the built-in is a selectable name, not only an implicit default");
            var summary = (SubGraphSummary)created.Value;
            Assert.AreEqual(BreadthFirstSearchSubgraphAlgorithm.AlgorithmPluginName, summary.AlgorithmPluginName);
            Assert.AreEqual(3, summary.VertexCount);
        }

        [TestMethod]
        public void Create_WithBlankAlgorithm_FallsBackToTheBuiltIn()
        {
            UseThreePersonGraph();

            var created = _controller.CreateSubGraph(AllPersonsSpecification("blank-algo", "   ")).Result as CreatedResult;

            Assert.IsNotNull(created, "a blank selector is 'unset', not 'unknown'");
            Assert.AreEqual(BreadthFirstSearchSubgraphAlgorithm.AlgorithmPluginName,
                ((SubGraphSummary)created.Value).AlgorithmPluginName);
        }

        [TestMethod]
        public void Create_WithRegisteredAlgorithm_RunsThatPlugin()
        {
            UseThreePersonGraph();

            RegisterPlugin(MarkerSubGraphAlgorithm.RegisteredName, PluginContract.SubGraph, typeof(MarkerSubGraphAlgorithm));

            // The plugin is advertised (GET /status reads this very list) ...
            CollectionAssert.Contains(_fallen8.SubGraphFactory.GetAvailableSubGraphPlugins().ToList(),
                MarkerSubGraphAlgorithm.RegisteredName, "a registered SubGraph plugin must be discoverable");

            // ... and now it is also invocable.
            var created = _controller.CreateSubGraph(
                AllPersonsSpecification("marked", MarkerSubGraphAlgorithm.RegisteredName)).Result as CreatedResult;

            Assert.IsNotNull(created, "a registered subgraph algorithm must be selectable over REST");
            var summary = (SubGraphSummary)created.Value;
            Assert.AreEqual(MarkerSubGraphAlgorithm.RegisteredName, summary.AlgorithmPluginName,
                "the summary must report the registered plugin, not the built-in");
            Assert.AreEqual(1, summary.VertexCount, "the marker algorithm produced the extract, not BFS");
            Assert.AreEqual(3, MarkerSourceCount(Registered("marked")), "it saw the three persons in the source graph");
        }

        [TestMethod]
        public void Create_NestedWithRegisteredAlgorithm_RunsThatPluginToo()
        {
            UseThreePersonGraph();

            RegisterPlugin(MarkerSubGraphAlgorithm.RegisteredName, PluginContract.SubGraph, typeof(MarkerSubGraphAlgorithm));

            Assert.IsInstanceOfType(_controller.CreateSubGraph(AllPersonsSpecification("parent")).Result, typeof(CreatedResult));

            // The nested branch of the create transaction is a separate call site: pin that the
            // selector reaches it too.
            var created = _controller.CreateSubGraph(
                AllPersonsSpecification("child", MarkerSubGraphAlgorithm.RegisteredName), "parent").Result as CreatedResult;

            Assert.IsNotNull(created, "a nested create must honour the selector as well");
            var summary = (SubGraphSummary)created.Value;
            Assert.AreEqual(MarkerSubGraphAlgorithm.RegisteredName, summary.AlgorithmPluginName);
            Assert.AreEqual(1, summary.VertexCount);
            Assert.AreEqual(3, MarkerSourceCount(Registered("child")),
                "the nested subgraph extracted from the parent's three persons");
        }

        [TestMethod]
        public void Create_WithUnknownAlgorithm_Returns400AndRegistersNothing()
        {
            UseThreePersonGraph();

            var result = _controller.CreateSubGraph(AllPersonsSpecification("bogus", "no-such-algorithm")).Result;

            var problem = ProblemAssert.AssertProblem(result, StatusCodes.Status400BadRequest, "no-such-algorithm");
            StringAssert.Contains(problem.Detail, BreadthFirstSearchSubgraphAlgorithm.AlgorithmPluginName,
                "the error should name what IS available");
            Assert.IsFalse(_fallen8.SubGraphFactory.TryGetSubGraph(out _, "bogus"),
                "an unknown algorithm must not silently fall back to the built-in and create the subgraph");
        }

        [TestMethod]
        public void Create_WithAPluginRegisteredForAnotherContract_Returns400()
        {
            UseThreePersonGraph();

            // Same artifact, registered as a Path plugin: it is not a subgraph algorithm, so it must
            // not be selectable as one.
            RegisterPlugin("path-contract-plugin", PluginContract.Path, typeof(MarkerSubGraphAlgorithm));

            ProblemAssert.AssertProblem(
                _controller.CreateSubGraph(AllPersonsSpecification("wrong-contract", "path-contract-plugin")).Result,
                StatusCodes.Status400BadRequest, "path-contract-plugin");
            Assert.IsFalse(_fallen8.SubGraphFactory.TryGetSubGraph(out _, "wrong-contract"));
        }

        [TestMethod]
        public void Recalculate_OfASubGraphCreatedByARegisteredAlgorithm_ReResolvesThatPlugin()
        {
            UseThreePersonGraph();

            RegisterPlugin(MarkerSubGraphAlgorithm.RegisteredName, PluginContract.SubGraph, typeof(MarkerSubGraphAlgorithm));

            Assert.IsInstanceOfType(
                _controller.CreateSubGraph(AllPersonsSpecification("marked", MarkerSubGraphAlgorithm.RegisteredName)).Result,
                typeof(CreatedResult));
            Assert.AreEqual(3, MarkerSourceCount(Registered("marked")));

            AddPerson("Dave");

            var ok = _controller.RecalculateSubGraph("marked") as OkObjectResult;
            Assert.IsNotNull(ok, "the stored plugin name must round-trip so recalculation can re-resolve it");
            Assert.AreEqual(MarkerSubGraphAlgorithm.RegisteredName, ((SubGraphSummary)ok.Value).AlgorithmPluginName);
            Assert.AreEqual(4, MarkerSourceCount(Registered("marked")),
                "the registered plugin ran again against the grown source graph");
        }

        [TestMethod]
        public void CreateTransaction_WithoutSelector_UsesTheBuiltIn()
        {
            UseThreePersonGraph();

            var tx = new CreateSubGraphTransaction { Definition = AllPersonsDefinition("engine-default") };
            _fallen8.EnqueueTransaction(tx).WaitUntilFinished();

            Assert.IsNotNull(tx.SubGraphCreated);
            Assert.AreEqual(BreadthFirstSearchSubgraphAlgorithm.AlgorithmPluginName, tx.SubGraphCreated.AlgorithmPluginName);
            Assert.AreEqual(3, tx.SubGraphCreated.SubGraph.VertexCount);
        }

        [TestMethod]
        public void CreateTransaction_WithSelector_UsesTheRegisteredPlugin()
        {
            UseThreePersonGraph();

            RegisterPlugin(MarkerSubGraphAlgorithm.RegisteredName, PluginContract.SubGraph, typeof(MarkerSubGraphAlgorithm));

            var tx = new CreateSubGraphTransaction
            {
                Definition = AllPersonsDefinition("engine-marked"),
                AlgorithmPluginName = MarkerSubGraphAlgorithm.RegisteredName
            };
            _fallen8.EnqueueTransaction(tx).WaitUntilFinished();

            Assert.IsNotNull(tx.SubGraphCreated, "the transaction is the seam the REST layer uses");
            Assert.AreEqual(MarkerSubGraphAlgorithm.RegisteredName, tx.SubGraphCreated.AlgorithmPluginName);
            Assert.AreEqual(1, tx.SubGraphCreated.SubGraph.VertexCount);
        }

        [TestMethod]
        public void CreateTransaction_WithUnknownSelector_RollsBackInsteadOfFallingBack()
        {
            UseThreePersonGraph();

            var tx = new CreateSubGraphTransaction
            {
                Definition = AllPersonsDefinition("engine-bogus"),
                AlgorithmPluginName = "no-such-algorithm"
            };
            var info = _fallen8.EnqueueTransaction(tx);
            info.WaitUntilFinished();

            Assert.AreEqual(TransactionState.RolledBack, info.TransactionState,
                "an unresolvable algorithm must fail the create, never fall back to the built-in");
            Assert.AreEqual(TransactionFailureReason.InternalError, info.FailureReason,
                "a missing plugin is a server-side infrastructure fault at the engine level");
            Assert.IsNull(tx.SubGraphCreated);
            Assert.IsFalse(_fallen8.SubGraphFactory.TryGetSubGraph(out _, "engine-bogus"));
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

            public DurabilityState Durability => _inner.Durability;
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
            public NoSQL.GraphDB.Core.Plugins.PluginRegistry Plugins => _inner.Plugins;
            public NoSQL.GraphDB.Core.Plugins.IPluginCompiler PluginCompiler
            {
                get => _inner.PluginCompiler;
                set => _inner.PluginCompiler = value;
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
            public bool GraphScanAllProperties(out List<AGraphElementModel> result, string searchTerm, string interestingLabel = null)
                => _inner.GraphScanAllProperties(out result, searchTerm, interestingLabel);
            public bool IndexScan(out IReadOnlyList<AGraphElementModel> result, string indexId, IComparable literal, BinaryOperator binOp = BinaryOperator.Equals)
                => _inner.IndexScan(out result, indexId, literal, binOp);
            public bool RangeIndexScan(out IReadOnlyList<AGraphElementModel> result, string indexId, IComparable leftLimit, IComparable rightLimit, bool includeLeft = true, bool includeRight = true)
                => _inner.RangeIndexScan(out result, indexId, leftLimit, rightLimit, includeLeft, includeRight);
            public bool FulltextIndexScan(out FulltextSearchResult result, string indexId, string searchQuery)
                => _inner.FulltextIndexScan(out result, indexId, searchQuery);
            public bool VectorIndexScan(out NoSQL.GraphDB.Core.Index.Vector.VectorSearchResult result, string indexId, float[] query, int k, NoSQL.GraphDB.Core.Index.Vector.VectorSearchConstraint constraint = null)
                => _inner.VectorIndexScan(out result, indexId, query, k, constraint);
            // A test double repeats the trim annotations of the interface members it implements: the
            // trim analyzer requires an implementation to match the declaration exactly. This project
            // does not enable the analyzer, so nothing checks it here today - the annotations are what
            // keeps the double compiling the day it is switched on.
            [RequiresUnreferencedCode(NoSQL.GraphDB.Core.Plugin.PluginFactory.DiscoveryIsNotTrimSafe)]
            public bool TryCalculateShortestPath(out List<NoSQL.GraphDB.Core.Algorithms.Path.Path> result, string plugin, ShortestPathDefinition definition)
                => _inner.TryCalculateShortestPath(out result, plugin, definition);
            public bool TryCalculateShortestPath<
                [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(
                out List<NoSQL.GraphDB.Core.Algorithms.Path.Path> result, ShortestPathDefinition definition) where T : IShortestPathAlgorithm
                => _inner.TryCalculateShortestPath<T>(out result, definition);
            [RequiresUnreferencedCode(NoSQL.GraphDB.Core.Plugin.PluginFactory.DiscoveryIsNotTrimSafe)]
            public bool TryRunAnalytics(out NoSQL.GraphDB.Core.Algorithms.Analytics.GraphAnalyticsResult result, string algorithmName, NoSQL.GraphDB.Core.Algorithms.Analytics.GraphAnalyticsDefinition definition)
                => _inner.TryRunAnalytics(out result, algorithmName, definition);

            public bool TryInvokeGraphFunction(out NoSQL.GraphDB.Core.Plugins.GraphFunctionResult result, string name, IDictionary<string, object> parameters)
                => _inner.TryInvokeGraphFunction(out result, name, parameters);
        }
    }
}
