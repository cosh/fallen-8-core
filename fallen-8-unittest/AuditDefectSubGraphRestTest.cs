// MIT License
//
// AuditDefectSubGraphRestTest.cs
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
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.SubGraph;
using NoSQL.GraphDB.Core.Plugins;
using NoSQL.GraphDB.Core.SubGraph;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Regression tests for two subgraph defects found by the code audit:
    /// <list type="bullet">
    ///   <item><description>B28: <c>PUT /subgraph</c> carried no algorithm selector, so a
    ///   registered <see cref="ISubGraphAlgorithm"/> was advertised by <c>GET /status</c> but could
    ///   never be run - every request silently used the built-in.</description></item>
    ///   <item><description>B27: recalculation ignored the subgraph quotas, so a subgraph whose
    ///   source grew could push both element ceilings past their limits.</description></item>
    /// </list>
    /// </summary>
    [TestClass]
    public class AuditDefectSubGraphRestTest
    {
        private Fallen8 _fallen8;
        private SubGraphController _controller;

        [TestInitialize]
        public void TestInitialize()
        {
            var loggerFactory = TestLoggerFactory.Create();
            _fallen8 = new Fallen8(loggerFactory);
            _controller = new SubGraphController(loggerFactory.CreateLogger<SubGraphController>(), _fallen8);

            AddPerson("Alice");
            AddPerson("Bob");
            AddPerson("Carol");
        }

        [TestCleanup]
        public void TestCleanup()
        {
            _fallen8.Dispose();
        }

        #region fixtures

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

        #endregion

        #region B28: the algorithm selector reaches the factory

        [TestMethod]
        public void Create_WithoutAlgorithm_UsesTheBuiltInBreadthFirstSearch()
        {
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
            var created = _controller.CreateSubGraph(AllPersonsSpecification("blank-algo", "   ")).Result as CreatedResult;

            Assert.IsNotNull(created, "a blank selector is 'unset', not 'unknown'");
            Assert.AreEqual(BreadthFirstSearchSubgraphAlgorithm.AlgorithmPluginName,
                ((SubGraphSummary)created.Value).AlgorithmPluginName);
        }

        [TestMethod]
        public void Create_WithRegisteredAlgorithm_RunsThatPlugin()
        {
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
            var tx = new CreateSubGraphTransaction { Definition = AllPersonsDefinition("engine-default") };
            _fallen8.EnqueueTransaction(tx).WaitUntilFinished();

            Assert.IsNotNull(tx.SubGraphCreated);
            Assert.AreEqual(BreadthFirstSearchSubgraphAlgorithm.AlgorithmPluginName, tx.SubGraphCreated.AlgorithmPluginName);
            Assert.AreEqual(3, tx.SubGraphCreated.SubGraph.VertexCount);
        }

        [TestMethod]
        public void CreateTransaction_WithSelector_UsesTheRegisteredPlugin()
        {
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

        #endregion

        #region B27: recalculation is quota-checked, and keeps the old contents on a breach

        [TestMethod]
        public void Recalculate_WithinQuota_StillSucceeds()
        {
            Assert.IsTrue(_fallen8.SubGraphFactory.TryCreateSubGraph(out var result, "persons", AllPersonsDefinition("persons")));
            Assert.AreEqual(3, result.SubGraph.VertexCount);

            AddPerson("Dave");

            Assert.IsTrue(_fallen8.SubGraphFactory.TryRecalculateSubGraph("persons", out var reason),
                "the generous default quota must not get in the way of an ordinary refresh");
            Assert.AreEqual(TransactionFailureReason.None, reason);
            Assert.AreEqual(4, Registered("persons").SubGraph.VertexCount);
        }

        [TestMethod]
        public void Recalculate_PastThePerSubGraphCeiling_FailsAndKeepsThePreviousContents()
        {
            Assert.IsTrue(_fallen8.SubGraphFactory.TryCreateSubGraph(out var result, "persons", AllPersonsDefinition("persons")));
            var instanceBefore = result.SubGraph;
            Assert.AreEqual(3, instanceBefore.VertexCount);

            // The ceiling is exactly what is materialized today, so only the GROWTH breaches it.
            _fallen8.SubGraphFactory.Quota = new SubGraphQuota { MaxElementsPerSubGraph = 3 };
            AddPerson("Dave");

            Assert.IsFalse(_fallen8.SubGraphFactory.TryRecalculateSubGraph("persons", out var reason),
                "a refresh that would exceed the per-subgraph element ceiling must be rejected");
            Assert.AreEqual(TransactionFailureReason.QuotaExceeded, reason,
                "the reason must be distinguishable from 'cannot be recalculated at all'");

            var after = Registered("persons");
            Assert.IsTrue(ReferenceEquals(instanceBefore, after.SubGraph),
                "the fresh extraction is discarded: the registered subgraph still holds its old engine");
            Assert.AreEqual(3, after.SubGraph.VertexCount, "the previous, in-quota contents are kept");
            Assert.AreEqual(BreadthFirstSearchSubgraphAlgorithm.AlgorithmPluginName, after.AlgorithmPluginName);
            Assert.IsTrue(_fallen8.SubGraphFactory.CanRecalculateSubGraph("persons"),
                "a quota rejection leaves the subgraph recalculable (raise the quota and retry)");
        }

        [TestMethod]
        public void Recalculate_PastTheAggregateCeiling_FailsAndKeepsThePreviousContents()
        {
            Assert.IsTrue(_fallen8.SubGraphFactory.TryCreateSubGraph(out var result, "persons", AllPersonsDefinition("persons")));
            Assert.AreEqual(3, result.SubGraph.VertexCount);

            _fallen8.SubGraphFactory.Quota = new SubGraphQuota { MaxTotalElements = 3 };
            AddPerson("Dave");

            Assert.IsFalse(_fallen8.SubGraphFactory.TryRecalculateSubGraph("persons", out var reason),
                "the aggregate ceiling bounds a refresh as well as a create");
            Assert.AreEqual(TransactionFailureReason.QuotaExceeded, reason);
            Assert.AreEqual(3, Registered("persons").SubGraph.VertexCount);
        }

        [TestMethod]
        public void Recalculate_AtTheAggregateCeiling_DoesNotDoubleCountItsOwnOldContents()
        {
            Assert.IsTrue(_fallen8.SubGraphFactory.TryCreateSubGraph(out var result, "persons", AllPersonsDefinition("persons")));
            Assert.AreEqual(3, result.SubGraph.VertexCount);

            // The aggregate is exactly full. The swap REPLACES this subgraph's 3 elements, so the
            // check must subtract its own current contribution; a naive "total + new" would reject a
            // refresh that consumes no additional memory at all.
            _fallen8.SubGraphFactory.Quota = new SubGraphQuota { MaxTotalElements = 3 };

            Assert.IsTrue(_fallen8.SubGraphFactory.TryRecalculateSubGraph("persons", out var reason),
                "an unchanged refresh at the aggregate ceiling must not fail spuriously");
            Assert.AreEqual(TransactionFailureReason.None, reason);
            Assert.AreEqual(3, Registered("persons").SubGraph.VertexCount);
        }

        [TestMethod]
        public void Recalculate_QuotaBreachOverRest_Returns409NamingTheQuota()
        {
            Assert.IsInstanceOfType(_controller.CreateSubGraph(AllPersonsSpecification("persons")).Result, typeof(CreatedResult));

            _fallen8.SubGraphFactory.Quota = new SubGraphQuota { MaxElementsPerSubGraph = 3 };
            AddPerson("Dave");

            var problem = ProblemAssert.AssertProblem(_controller.RecalculateSubGraph("persons"),
                StatusCodes.Status409Conflict, "quota");
            StringAssert.Contains(problem.Detail, "unchanged",
                "the message must say the previous contents survived");
            Assert.IsFalse(problem.Detail.Contains("missing source graph"),
                "the 409 must stop naming the wrong cause");

            var summary = (SubGraphSummary)((OkObjectResult)_controller.GetSubGraph("persons")).Value;
            Assert.AreEqual(3, summary.VertexCount, "the subgraph still reports its previous contents");
        }

        [TestMethod]
        public void Recalculate_UnrecalculableSubGraph_KeepsTheExistingMessage()
        {
            // A manually registered subgraph has no source and no plugin name: the pre-existing 409
            // path, which the quota split must not change.
            var manual = new SubGraphResult
            {
                SourceFallen8Id = _fallen8.Id,
                Definitions = new SubGraphDefinition { Name = "manual" },
                AlgorithmPluginName = null,
                SubGraph = new Fallen8(TestLoggerFactory.Create())
            };
            Assert.IsTrue(_fallen8.SubGraphFactory.TryRegisterSubGraph(manual));

            ProblemAssert.AssertProblem(_controller.RecalculateSubGraph("manual"),
                StatusCodes.Status409Conflict, "missing source graph or algorithm plugin");

            Assert.IsFalse(_fallen8.SubGraphFactory.TryRecalculateSubGraph("manual", out var reason));
            Assert.AreEqual(TransactionFailureReason.Conflict, reason,
                "an unrecalculable subgraph is a state conflict, not a quota breach");
        }

        [TestMethod]
        public void Recalculate_UnknownSubGraph_ReportsNotFound()
        {
            Assert.IsFalse(_fallen8.SubGraphFactory.TryRecalculateSubGraph("does-not-exist", out var reason));
            Assert.AreEqual(TransactionFailureReason.NotFound, reason);

            // The parameterless overload keeps its signature and its behaviour.
            Assert.IsFalse(_fallen8.SubGraphFactory.TryRecalculateSubGraph("does-not-exist"));
        }

        #endregion
    }
}
