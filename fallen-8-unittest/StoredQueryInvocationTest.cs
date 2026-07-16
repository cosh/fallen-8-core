// MIT License
//
// StoredQueryInvocationTest.cs
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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.App.Helper;
using NoSQL.GraphDB.Core.StoredQueries;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Tests for stored-query invocation by reference (feature stored-query-library Phase 1):
    /// the /path and /subgraph endpoints accepting "storedQuery" wherever they accept inline
    /// fragments, with stored-vs-inline result equivalence, the mixing/unknown/kind-mismatch/
    /// non-invocable error paths, untouched inline caching, and the invoke-during-remove race.
    /// </summary>
    [TestClass]
    public class StoredQueryInvocationTest
    {
        private Fallen8 _fallen8;
        private GraphController _graphController;
        private SubGraphController _subGraphController;
        private StoredQueriesController _storedQueriesController;

        private int _a, _b, _c, _d;

        [TestInitialize]
        public void TestInitialize()
        {
            var loggerFactory = TestLoggerFactory.Create();
            _fallen8 = new Fallen8(loggerFactory);
            _graphController = new GraphController(loggerFactory.CreateLogger<GraphController>(), _fallen8);
            _subGraphController = new SubGraphController(loggerFactory.CreateLogger<SubGraphController>(), _fallen8);
            _storedQueriesController = new StoredQueriesController(loggerFactory.CreateLogger<StoredQueriesController>(), _fallen8);

            // A weighted diamond: a -> b -> d (weight 1 + 1) and a -> c -> d (weight 5 + 1), all
            // "person" vertices except c ("robot"), all edges labeled "knows" except c's ("spam").
            var creationDate = 1u;
            var verticesTx = new CreateVerticesTransaction();
            verticesTx.AddVertex(creationDate, "person", new Dictionary<string, object> { { "name", "a" } });
            verticesTx.AddVertex(creationDate, "person", new Dictionary<string, object> { { "name", "b" } });
            verticesTx.AddVertex(creationDate, "robot", new Dictionary<string, object> { { "name", "c" } });
            verticesTx.AddVertex(creationDate, "person", new Dictionary<string, object> { { "name", "d" } });
            _fallen8.EnqueueTransaction(verticesTx).WaitUntilFinished();
            var v = verticesTx.GetCreatedVertices();
            _a = v[0].Id;
            _b = v[1].Id;
            _c = v[2].Id;
            _d = v[3].Id;

            var edgesTx = new CreateEdgesTransaction();
            edgesTx.AddEdge(_a, "knows", _b, creationDate, "knows", new Dictionary<string, object> { { "weight", 1.0 } });
            edgesTx.AddEdge(_b, "knows", _d, creationDate, "knows", new Dictionary<string, object> { { "weight", 1.0 } });
            edgesTx.AddEdge(_a, "knows", _c, creationDate, "spam", new Dictionary<string, object> { { "weight", 5.0 } });
            edgesTx.AddEdge(_c, "knows", _d, creationDate, "spam", new Dictionary<string, object> { { "weight", 1.0 } });
            _fallen8.EnqueueTransaction(edgesTx).WaitUntilFinished();
        }

        [TestCleanup]
        public void TestCleanup()
        {
            _fallen8.Dispose();
        }

        #region helpers

        private const string PersonVertexFilter = "return (v) => v.Label == \"person\";";
        private const string KnowsEdgeFilter = "return (e) => e.Label == \"knows\";";
        private const string WeightEdgeCost =
            "return (e) => e.TryGetProperty(out object w, \"weight\") ? Convert.ToDouble(w) : 1.0;";

        private void RegisterPathQuery(string name, string vertexFilter = null, string edgeFilter = null, string edgeCost = null)
        {
            var spec = new StoredQuerySpecification
            {
                Name = name,
                Kind = "Path",
                Path = new StoredPathQueryBlock()
            };
            if (vertexFilter != null || edgeFilter != null)
            {
                spec.Path.Filter = new PathFilterSpecification();
                if (vertexFilter != null)
                {
                    spec.Path.Filter.Vertex = vertexFilter;
                }
                if (edgeFilter != null)
                {
                    spec.Path.Filter.Edge = edgeFilter;
                }
            }
            if (edgeCost != null)
            {
                spec.Path.Cost = new PathCostSpecification { Edge = edgeCost };
            }

            var result = _storedQueriesController.RegisterStoredQuery(spec);
            Assert.AreEqual(201, ((ObjectResult)result).StatusCode, "Stored query registration must succeed.");
        }

        private static string PathSignature(List<PathREST> paths)
        {
            // A stable, order-preserving signature of every path's element sequence and weights.
            return String.Join("|", paths.Select(p =>
                p.TotalWeight.ToString("R") + ":" +
                String.Join(",", p.PathElements.Select(e => $"{e.SourceVertexId}-{e.EdgeId}-{e.TargetVertexId}"))));
        }

        private void RegisterFailedEntry(string name, StoredQueryKind kind)
        {
            var tx = new RegisterStoredQueryTransaction
            {
                Entry = new StoredQueryEntry(
                    new StoredQueryDefinition
                    {
                        Name = name,
                        Kind = kind,
                        SpecificationJson = "{}",
                        CreatedAt = DateTime.UtcNow
                    },
                    StoredQueryCompileState.Failed, null, "ID: CS0000, Message: simulated recompile failure")
            };
            var txInfo = _fallen8.EnqueueTransaction(tx);
            txInfo.WaitUntilFinished();
            Assert.AreEqual(TransactionState.Finished, txInfo.TransactionState);
        }

        #endregion

        #region stored-vs-inline equivalence

        [TestMethod]
        public void StoredPathQuery_BLS_IsElementForElementEquivalentToInline()
        {
            var inlineSpec = new PathSpecification
            {
                PathAlgorithmName = "BLS",
                MaxDepth = 4,
                Filter = new PathFilterSpecification { Vertex = PersonVertexFilter }
            };
            var inlinePaths = _graphController.CalculateShortestPath(_a, _d, inlineSpec).Value;
            Assert.IsTrue(inlinePaths.Count > 0, "The inline baseline must find at least one path.");

            RegisterPathQuery("bls-person", vertexFilter: PersonVertexFilter);
            var storedSpec = new PathSpecification
            {
                PathAlgorithmName = "BLS",
                MaxDepth = 4,
                StoredQuery = "bls-person"
            };
            var storedPaths = _graphController.CalculateShortestPath(_a, _d, storedSpec).Value;

            Assert.AreEqual(PathSignature(inlinePaths), PathSignature(storedPaths),
                "Stored invocation must return element-for-element identical paths to the same inline spec.");
        }

        [TestMethod]
        public void StoredPathQuery_Dijkstra_IsElementForElementEquivalentToInline()
        {
            var inlineSpec = new PathSpecification
            {
                PathAlgorithmName = "DIJKSTRA",
                MaxDepth = 4,
                MaxResults = 4,
                Cost = new PathCostSpecification { Edge = WeightEdgeCost }
            };
            var inlinePaths = _graphController.CalculateShortestPath(_a, _d, inlineSpec).Value;
            Assert.IsTrue(inlinePaths.Count >= 2, "The weighted diamond must yield both a->d paths.");
            // Weight ordering: a->b->d (edge weights 1+1) must rank strictly before a->c->d
            // (5+1). The absolute totals also include the default per-vertex cost, so only the
            // ordering is asserted here; the equivalence assertion below pins the exact values.
            Assert.IsTrue(inlinePaths[0].TotalWeight < inlinePaths[1].TotalWeight,
                "The low-weight route must rank first.");
            Assert.AreEqual(_b, inlinePaths[0].PathElements[0].TargetVertexId,
                "The low-weight route runs via b.");

            RegisterPathQuery("dijkstra-weighted", edgeCost: WeightEdgeCost);
            var storedSpec = new PathSpecification
            {
                PathAlgorithmName = "DIJKSTRA",
                MaxDepth = 4,
                MaxResults = 4,
                StoredQuery = "dijkstra-weighted"
            };
            var storedPaths = _graphController.CalculateShortestPath(_a, _d, storedSpec).Value;

            Assert.AreEqual(PathSignature(inlinePaths), PathSignature(storedPaths),
                "Stored invocation must return element-for-element identical weighted paths.");
        }

        [TestMethod]
        public void StoredSubGraph_IsEquivalentToInline()
        {
            var inlineSpec = new SubGraphSpecification
            {
                Name = "inline-persons",
                VertexFilter = PersonVertexFilter,
                EdgeFilter = KnowsEdgeFilter
            };
            var inlineResult = _subGraphController.CreateSubGraph(inlineSpec);
            Assert.AreEqual(201, ((ObjectResult)inlineResult).StatusCode);
            Assert.IsTrue(_fallen8.SubGraphFactory.TryGetSubGraph(out var inlineSubGraph, "inline-persons"));

            _storedQueriesController.RegisterStoredQuery(new StoredQuerySpecification
            {
                Name = "persons-template",
                Kind = "SubGraph",
                SubGraph = new StoredSubGraphQueryBlock
                {
                    VertexFilter = PersonVertexFilter,
                    EdgeFilter = KnowsEdgeFilter
                }
            });

            var storedResult = _subGraphController.CreateSubGraph(new SubGraphSpecification
            {
                Name = "stored-persons",
                StoredQuery = "persons-template"
            });
            Assert.AreEqual(201, ((ObjectResult)storedResult).StatusCode);
            Assert.IsTrue(_fallen8.SubGraphFactory.TryGetSubGraph(out var storedSubGraph, "stored-persons"));

            Assert.AreEqual(inlineSubGraph.SubGraph.VertexCount, storedSubGraph.SubGraph.VertexCount);
            Assert.AreEqual(inlineSubGraph.SubGraph.EdgeCount, storedSubGraph.SubGraph.EdgeCount);
            CollectionAssert.AreEquivalent(
                inlineSubGraph.SubGraph.GetAllVertices().Select(x => x.Id).ToList(),
                storedSubGraph.SubGraph.GetAllVertices().Select(x => x.Id).ToList(),
                "The stored template must extract the same vertices as the same inline specification.");
        }

        [TestMethod]
        public void StoredSubGraph_TemplateIsReusable_AcrossInstances()
        {
            _storedQueriesController.RegisterStoredQuery(new StoredQuerySpecification
            {
                Name = "reusable-template",
                Kind = "SubGraph",
                SubGraph = new StoredSubGraphQueryBlock { VertexFilter = PersonVertexFilter }
            });

            Assert.AreEqual(201, ((ObjectResult)_subGraphController.CreateSubGraph(
                new SubGraphSpecification { Name = "instance-1", StoredQuery = "reusable-template" })).StatusCode);
            Assert.AreEqual(201, ((ObjectResult)_subGraphController.CreateSubGraph(
                new SubGraphSpecification { Name = "instance-2", StoredQuery = "reusable-template" })).StatusCode);

            Assert.IsTrue(_fallen8.SubGraphFactory.TryGetSubGraph(out var one, "instance-1"));
            Assert.IsTrue(_fallen8.SubGraphFactory.TryGetSubGraph(out var two, "instance-2"));
            Assert.AreEqual(one.SubGraph.VertexCount, two.SubGraph.VertexCount);
        }

        #endregion

        #region inline caching untouched

        [TestMethod]
        public void StoredPathInvocation_NeverCompilesPerRequest()
        {
            RegisterPathQuery("pinned-no-recompile", vertexFilter: "return (v) => v.Label != \"stored-query-pin-marker\";");

            var before = CodeGenerationHelper.PathCompileCount;

            var spec = new PathSpecification { MaxDepth = 4, StoredQuery = "pinned-no-recompile" };
            _graphController.CalculateShortestPath(_a, _d, spec);
            _graphController.CalculateShortestPath(_a, _d, spec);
            _graphController.CalculateShortestPath(_a, _b, spec);

            Assert.AreEqual(0, CodeGenerationHelper.PathCompileCount - before,
                "Stored-query invocations must use the pinned artifact and never reach Roslyn.");
        }

        [TestMethod]
        public void InlineRequests_StillCompileAndCache_ExactlyAsBefore()
        {
            // The stored registration compiles once; identical INLINE requests compile once more
            // (the stored artifact is pinned outside the inline cache) and then hit the cache.
            const string marker = "stored-query-inline-cache-marker";
            var filter = "return (v) => v.Label != \"" + marker + "\";";

            RegisterPathQuery("cache-untouched", vertexFilter: filter);

            var before = CodeGenerationHelper.PathCompileCount;

            var inlineSpec = new PathSpecification
            {
                MaxDepth = 4,
                Filter = new PathFilterSpecification { Vertex = filter }
            };
            _graphController.CalculateShortestPath(_a, _d, inlineSpec);
            var inlineSpecAgain = new PathSpecification
            {
                MaxDepth = 7,
                Filter = new PathFilterSpecification { Vertex = filter }
            };
            _graphController.CalculateShortestPath(_a, _d, inlineSpecAgain);

            Assert.AreEqual(1, CodeGenerationHelper.PathCompileCount - before,
                "Inline requests keep their own compile-once (Filter, Cost) cache semantics.");
        }

        #endregion

        #region error paths

        [TestMethod]
        public void Path_MixingStoredQueryWithInlineFragments_Returns400()
        {
            RegisterPathQuery("mixing-check");

            var spec = new PathSpecification
            {
                StoredQuery = "mixing-check",
                Filter = new PathFilterSpecification { Vertex = PersonVertexFilter }
            };

            var result = _graphController.CalculateShortestPath(_a, _d, spec).Result;
            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult));
        }

        [TestMethod]
        public void Path_MixingStoredQueryWithCostBlock_Returns400()
        {
            RegisterPathQuery("mixing-cost-check");

            var spec = new PathSpecification
            {
                StoredQuery = "mixing-cost-check",
                Cost = new PathCostSpecification { Edge = "return (e) => 1.0;" }
            };

            var result = _graphController.CalculateShortestPath(_a, _d, spec).Result;
            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult));
        }

        [TestMethod]
        public void Path_UnknownStoredQuery_Returns404_NamingTheStoredQuery()
        {
            var spec = new PathSpecification { StoredQuery = "never-registered" };

            var result = _graphController.CalculateShortestPath(_a, _d, spec).Result;

            Assert.IsInstanceOfType(result, typeof(NotFoundObjectResult));
            StringAssert.Contains((string)((NotFoundObjectResult)result).Value, "stored query",
                "The 404 must name the stored query to disambiguate from the endpoint's vertex 404s.");
        }

        [TestMethod]
        public void Path_KindMismatch_Returns400()
        {
            _storedQueriesController.RegisterStoredQuery(new StoredQuerySpecification
            {
                Name = "a-subgraph-template",
                Kind = "SubGraph",
                SubGraph = new StoredSubGraphQueryBlock { VertexFilter = PersonVertexFilter }
            });

            var result = _graphController.CalculateShortestPath(_a, _d,
                new PathSpecification { StoredQuery = "a-subgraph-template" }).Result;

            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult));
        }

        [TestMethod]
        public void Path_FailedCompileState_Returns409_WithDiagnostics()
        {
            RegisterFailedEntry("failed-path-query", StoredQueryKind.Path);

            var result = _graphController.CalculateShortestPath(_a, _d,
                new PathSpecification { StoredQuery = "failed-path-query" }).Result;

            Assert.IsInstanceOfType(result, typeof(ConflictObjectResult));
            StringAssert.Contains((string)((ConflictObjectResult)result).Value, "CS0000");
        }

        [TestMethod]
        public void SubGraph_MixingStoredQueryWithInlineFragments_Returns400()
        {
            _storedQueriesController.RegisterStoredQuery(new StoredQuerySpecification
            {
                Name = "subgraph-mixing-check",
                Kind = "SubGraph",
                SubGraph = new StoredSubGraphQueryBlock { VertexFilter = PersonVertexFilter }
            });

            var result = _subGraphController.CreateSubGraph(new SubGraphSpecification
            {
                Name = "mixed",
                StoredQuery = "subgraph-mixing-check",
                VertexFilter = PersonVertexFilter
            });

            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult));
        }

        [TestMethod]
        public void SubGraph_UnknownStoredQuery_Returns404()
        {
            var result = _subGraphController.CreateSubGraph(new SubGraphSpecification
            {
                Name = "orphan",
                StoredQuery = "never-registered"
            });

            Assert.IsInstanceOfType(result, typeof(NotFoundObjectResult));
        }

        [TestMethod]
        public void SubGraph_KindMismatch_Returns400()
        {
            RegisterPathQuery("a-path-query");

            var result = _subGraphController.CreateSubGraph(new SubGraphSpecification
            {
                Name = "wrong-kind",
                StoredQuery = "a-path-query"
            });

            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult));
        }

        [TestMethod]
        public void SubGraph_FailedCompileState_Returns409()
        {
            RegisterFailedEntry("failed-subgraph-query", StoredQueryKind.SubGraph);

            var result = _subGraphController.CreateSubGraph(new SubGraphSpecification
            {
                Name = "from-failed",
                StoredQuery = "failed-subgraph-query"
            });

            Assert.IsInstanceOfType(result, typeof(ConflictObjectResult));
        }

        [TestMethod]
        public void SubGraph_InstanceNameStaysRequired_WithStoredQuery()
        {
            _storedQueriesController.RegisterStoredQuery(new StoredQuerySpecification
            {
                Name = "needs-instance-name",
                Kind = "SubGraph",
                SubGraph = new StoredSubGraphQueryBlock { VertexFilter = PersonVertexFilter }
            });

            var result = _subGraphController.CreateSubGraph(new SubGraphSpecification
            {
                StoredQuery = "needs-instance-name"
            });

            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult));
        }

        #endregion

        #region decoupling from the stored query's lifetime

        [TestMethod]
        public void SubGraphFromStoredTemplate_SurvivesStoredQueryDeletion()
        {
            _storedQueriesController.RegisterStoredQuery(new StoredQuerySpecification
            {
                Name = "short-lived-template",
                Kind = "SubGraph",
                SubGraph = new StoredSubGraphQueryBlock { VertexFilter = PersonVertexFilter }
            });

            Assert.AreEqual(201, ((ObjectResult)_subGraphController.CreateSubGraph(
                new SubGraphSpecification { Name = "survivor", StoredQuery = "short-lived-template" })).StatusCode);

            // Delete the stored query, then verify the subgraph is fully self-contained: it still
            // exists, its recipe is materialized (no stored-query reference), and it can be
            // recalculated against the live graph.
            Assert.AreEqual(204, ((StatusCodeResult)_storedQueriesController.DeleteStoredQuery("short-lived-template")).StatusCode);

            Assert.IsTrue(_fallen8.SubGraphFactory.TryGetSubGraph(out var survivor, "survivor"));
            Assert.IsNotNull(survivor.Recipe, "The subgraph must carry a persistable recipe.");

            // The recipe must be the MATERIALIZED specification: the template's fragments are
            // inlined and the stored-query reference is gone, so replaying it never depends on
            // the (deleted) stored query.
            var recipeSpec = System.Text.Json.JsonSerializer.Deserialize(
                survivor.Recipe.SpecificationJson, NoSQL.GraphDB.App.AppJsonContext.Default.SubGraphSpecification);
            Assert.IsNull(recipeSpec.StoredQuery, "The materialized recipe must not reference the stored query.");
            Assert.AreEqual(PersonVertexFilter, recipeSpec.VertexFilter,
                "The recipe must carry the template's materialized fragments.");

            Assert.IsTrue(_fallen8.SubGraphFactory.TryRecalculateSubGraph("survivor"),
                "The subgraph must stay recalculable after the stored query is gone.");
        }

        #endregion

        #region invoke-during-remove race

        [TestMethod]
        public void InvokeDuringRemove_CompletesAgainstOldArtifactOr404_NeverTorn()
        {
            // Repeatedly register + remove a stored query while invocations run concurrently:
            // every invocation must either complete against the captured artifact (a 200-shaped
            // list) or observe a clean 404 - never throw and never return a torn result.
            const string name = "raced-query";
            var stop = new ManualResetEventSlim(false);
            var failures = new List<string>();
            var invocations = 0;

            var invoker = Task.Run(() =>
            {
                while (!stop.IsSet)
                {
                    var spec = new PathSpecification { MaxDepth = 4, StoredQuery = name };
                    try
                    {
                        var outcome = _graphController.CalculateShortestPath(_a, _d, spec);
                        Interlocked.Increment(ref invocations);

                        if (outcome.Value != null)
                        {
                            continue; // completed against a captured artifact
                        }

                        if (outcome.Result is NotFoundObjectResult)
                        {
                            continue; // removal won before resolution
                        }

                        lock (failures)
                        {
                            failures.Add("Unexpected outcome: " + (outcome.Result?.GetType().Name ?? "null"));
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (failures)
                        {
                            failures.Add("Invocation threw: " + ex);
                        }
                    }
                }
            });

            for (var i = 0; i < 25; i++)
            {
                RegisterPathQuery(name, vertexFilter: "return (v) => v.Label != \"race-marker\";");
                Thread.Sleep(1);

                var remove = new RemoveStoredQueryTransaction { Name = name };
                _fallen8.EnqueueTransaction(remove).WaitUntilFinished();
            }

            stop.Set();
            invoker.Wait(TimeSpan.FromSeconds(30));

            Assert.AreEqual(0, failures.Count, String.Join("; ", failures));
            Assert.IsTrue(invocations > 0, "The race must actually have exercised invocations.");
        }

        #endregion
    }
}
