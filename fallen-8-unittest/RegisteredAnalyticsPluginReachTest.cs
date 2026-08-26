// MIT License
//
// RegisteredAnalyticsPluginReachTest.cs
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
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Services;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The ONLY coverage of RUNTIME-REGISTERED Analytics plugins reaching
    ///   <see cref="AnalyticsController"/> (audit defect B04): the run and partition-membership
    ///   endpoints pre-check the algorithm name, and that pre-check used to consult only the
    ///   base-directory DLL scan (the built-ins), so a plugin registered through
    ///   <c>POST /plugins/algorithm</c> with contract Analytics answered a permanent 404 even though
    ///   the engine resolves it first and <c>GET /analytics/algorithms</c> advertised it. The
    ///   contract pinned here: the LIST and the PRE-CHECK are the same set, that set is built-ins
    ///   unioned with the addressed namespace's registry, and it is read live (a delete puts the
    ///   name back to 404). Registration goes through the real <see cref="PluginsController"/>
    ///   (Roslyn compile + contract validation), so the artifact is a genuine collectible-ALC plugin
    ///   type, exactly as at runtime.
    ///   <para>
    ///   No other file combines runtime plugins with <see cref="AnalyticsController"/>:
    ///   <c>AnalyticsEndpointTest</c> exercises the same endpoints over HTTP but only ever with the
    ///   built-ins and an empty registry.
    ///   </para>
    /// </summary>
    [TestClass]
    public class RegisteredAnalyticsPluginReachTest
    {
        private Fallen8 _fallen8;
        private AnalyticsController _analytics;
        private PluginsController _plugins;

        [TestInitialize]
        public void TestInitialize()
        {
            var loggerFactory = TestLoggerFactory.Create();
            _fallen8 = new Fallen8(loggerFactory);

            var options = Options.Create(new Fallen8AnalyticsOptions());
            _analytics = new AnalyticsController(loggerFactory.CreateLogger<AnalyticsController>(),
                _fallen8, options, new AnalyticsRunGate(options));
            _plugins = new PluginsController(loggerFactory.CreateLogger<PluginsController>(), _fallen8);
        }

        [TestCleanup]
        public void TestCleanup()
        {
            _fallen8.Dispose();
        }

        #region plugin sources + helpers

        /// <summary>A score analytics plugin: one point per in-scope vertex.</summary>
        private const String ScoreAnalyticsSource = @"
using System;
using System.Collections.Generic;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.Analytics;
using NoSQL.GraphDB.Core.Plugin;

public sealed class AuditScoreAnalytics : IGraphAnalyticsAlgorithm
{
    private IFallen8 _graph;
    public string PluginName => ""auditScore"";
    public Type PluginCategory => typeof(IGraphAnalyticsAlgorithm);
    public string Description => ""one point per vertex"";
    public string Manufacturer => ""test"";
    public void Initialize(IFallen8 fallen8, IDictionary<string, object> parameter) { _graph = fallen8; }
    public void Dispose() { }
    public bool TryRunAnalytics(out GraphAnalyticsResult result, GraphAnalyticsDefinition definition)
    {
        var scores = new Dictionary<int, double>();
        foreach (var vertex in _graph.GetAllVertices(definition == null ? null : definition.VertexLabel))
        {
            scores[vertex.Id] = 1.0;
        }
        result = new GraphAnalyticsResult(scores, new Dictionary<string, object>(), true, 1, TimeSpan.Zero, false);
        return true;
    }
}";

        /// <summary>A partition analytics plugin: every in-scope vertex lands in partition 7.</summary>
        private const String PartitionAnalyticsSource = @"
using System;
using System.Collections.Generic;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.Analytics;
using NoSQL.GraphDB.Core.Plugin;

public sealed class AuditPartitionAnalytics : IGraphAnalyticsAlgorithm
{
    private IFallen8 _graph;
    public string PluginName => ""auditPartition"";
    public Type PluginCategory => typeof(IGraphAnalyticsAlgorithm);
    public string Description => ""everything in partition 7"";
    public string Manufacturer => ""test"";
    public void Initialize(IFallen8 fallen8, IDictionary<string, object> parameter) { _graph = fallen8; }
    public void Dispose() { }
    public bool TryRunAnalytics(out GraphAnalyticsResult result, GraphAnalyticsDefinition definition)
    {
        var partitions = new Dictionary<int, int>();
        foreach (var vertex in _graph.GetAllVertices(definition == null ? null : definition.VertexLabel))
        {
            partitions[vertex.Id] = 7;
        }
        result = new GraphAnalyticsResult(partitions, new Dictionary<string, object>(), true, 1, TimeSpan.Zero, false);
        return true;
    }
}";

        /// <summary>A PATH plugin: registered in the same registry, but must never be reachable
        /// through the analytics endpoints (the contract filter).</summary>
        private const String PathAlgorithmSource = @"
using System;
using System.Collections.Generic;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.Plugin;

public sealed class AuditPath : IShortestPathAlgorithm
{
    public string PluginName => ""auditPath"";
    public Type PluginCategory => typeof(IShortestPathAlgorithm);
    public string Description => ""x"";
    public string Manufacturer => ""test"";
    public void Initialize(IFallen8 fallen8, IDictionary<string, object> parameter) { }
    public void Dispose() { }
    public bool TryCalculateShortestPath(out List<Path> result, ShortestPathDefinition definition)
    { result = new List<Path>(); return true; }
}";

        private static Int32 StatusCodeOf(IActionResult result)
        {
            switch (result)
            {
                case ObjectResult o when o.StatusCode.HasValue:
                    return o.StatusCode.Value;
                case StatusCodeResult s:
                    return s.StatusCode;
                default:
                    Assert.Fail("Unexpected result type " + result.GetType().Name + ".");
                    return 0;
            }
        }

        private static Int32 StatusCodeOf(Task<IActionResult> result) => StatusCodeOf(result.Result);

        /// <summary>Asserts the result is a problem+json <see cref="ProblemDetails"/> of the expected
        /// status (the central error envelope) and returns it.</summary>
        private static ProblemDetails AssertProblem(IActionResult result, Int32 expectedStatus)
        {
            var objectResult = result as ObjectResult;
            Assert.IsNotNull(objectResult, "an error must be an ObjectResult carrying a problem document");
            Assert.AreEqual(expectedStatus, objectResult.StatusCode);
            Assert.IsTrue(objectResult.ContentTypes.Contains("application/problem+json"),
                "errors stay application/problem+json on the wire");
            var problem = objectResult.Value as ProblemDetails;
            Assert.IsNotNull(problem);
            Assert.AreEqual(expectedStatus, problem.Status);
            return problem;
        }

        private void RegisterAlgorithm(String name, String contract, String source)
        {
            var registration = new AlgorithmPluginRegistration
            {
                Name = name,
                Contract = contract,
                SourceCode = source,
                Description = name + " description"
            };

            Assert.AreEqual(StatusCodes.Status201Created, StatusCodeOf(_plugins.RegisterAlgorithm(registration)),
                "registering the plugin must succeed for this test to say anything");
        }

        private void AddVertices(Int32 count)
        {
            var vertices = new List<VertexDefinition>(count);
            for (var i = 0; i < count; i++)
            {
                vertices.Add(new VertexDefinition { CreationDate = 1u, Label = "person" });
            }

            _fallen8.EnqueueTransaction(new CreateVerticesTransaction { Vertices = vertices }).WaitUntilFinished();
        }

        private Dictionary<String, String> ListedAlgorithms()
        {
            var listed = _analytics.GetAvailableAlgorithms() as OkObjectResult;
            Assert.IsNotNull(listed);
            var algorithms = listed.Value as Dictionary<String, String>;
            Assert.IsNotNull(algorithms);
            return algorithms;
        }

        private AnalyticsResultREST RunOk(String algorithmName, AnalyticsSpecification specification = null)
        {
            var result = _analytics.RunAnalytics(algorithmName, specification ?? new AnalyticsSpecification()).Result;
            Assert.AreEqual(StatusCodes.Status200OK, StatusCodeOf(result),
                "'" + algorithmName + "' must be invocable");
            var payload = (result as OkObjectResult)?.Value as AnalyticsResultREST;
            Assert.IsNotNull(payload);
            return payload;
        }

        #endregion

        [TestMethod]
        public void RegisteredAnalyticsPlugin_RunsThroughTheEndpoint_InsteadOf404()
        {
            // THE defect: this returned 404 while the engine would happily have run the plugin.
            AddVertices(2);
            RegisterAlgorithm("auditScore", "Analytics", ScoreAnalyticsSource);

            var payload = RunOk("auditScore");

            Assert.AreEqual("auditScore", payload.Algorithm);
            Assert.AreEqual(2, payload.VertexCount);
            Assert.IsNotNull(payload.Results);
            Assert.AreEqual(2, payload.Results.Count);
            Assert.AreEqual(1.0d, payload.Results[0].Score);
        }

        [TestMethod]
        public void EveryListedAlgorithm_IsInvocable_BuiltInsAndRegistered()
        {
            // The contract that keeps the two from diverging again: every name the picker lists is
            // invocable (no 404), and the registered plugin is listed with its description.
            AddVertices(2);
            RegisterAlgorithm("auditScore", "Analytics", ScoreAnalyticsSource);

            var algorithms = ListedAlgorithms();
            foreach (var builtIn in new[] { "PAGERANK", "WCC", "LABELPROPAGATION", "DEGREE", "TRIANGLECOUNT" })
            {
                Assert.IsTrue(algorithms.ContainsKey(builtIn), builtIn + " must stay listed");
            }
            Assert.IsTrue(algorithms.ContainsKey("auditScore"), "the registered analytics plugin must be listed");
            Assert.AreEqual("auditScore description", algorithms["auditScore"],
                "the registry entry's description is what the picker shows");

            foreach (var name in new List<String>(algorithms.Keys))
            {
                Assert.AreNotEqual(StatusCodes.Status404NotFound,
                    StatusCodeOf(_analytics.RunAnalytics(name, new AnalyticsSpecification()).Result),
                    "a listed algorithm must never be a 404: " + name);
            }
        }

        [TestMethod]
        public void UnknownAlgorithm_Still404ProblemJson_OnBothEndpoints()
        {
            // The union widens the set; it must not turn the pre-check into a blanket allow, even
            // with a registered plugin present.
            RegisterAlgorithm("auditScore", "Analytics", ScoreAnalyticsSource);

            var runProblem = AssertProblem(_analytics.RunAnalytics("NoSuchAlgorithm", new AnalyticsSpecification()).Result,
                StatusCodes.Status404NotFound);
            StringAssert.Contains(runProblem.Detail, "NoSuchAlgorithm");

            var partitionProblem = AssertProblem(
                _analytics.GetPartitionMembers("NoSuchAlgorithm", 0, new AnalyticsSpecification()),
                StatusCodes.Status404NotFound);
            StringAssert.Contains(partitionProblem.Detail, "NoSuchAlgorithm");
        }

        [TestMethod]
        public void RegisteredAnalyticsPlugin_NameMatchStaysOrdinal()
        {
            // The pre-check must be exactly as case-sensitive as the resolution it guards (the
            // registry snapshot and PluginFactory's name map are both ordinal), otherwise a name it
            // accepts would die as a 408 inside the run.
            AddVertices(1);
            RegisterAlgorithm("auditScore", "Analytics", ScoreAnalyticsSource);

            AssertProblem(_analytics.RunAnalytics("AUDITSCORE", new AnalyticsSpecification()).Result,
                StatusCodes.Status404NotFound);
            AssertProblem(_analytics.RunAnalytics("pagerank", new AnalyticsSpecification()).Result,
                StatusCodes.Status404NotFound);
        }

        [TestMethod]
        public void DeletedAnalyticsPlugin_IsA404Again()
        {
            // The pre-check reads the live registry, not a set captured once: after the delete the
            // name is neither listed nor invocable.
            AddVertices(1);
            RegisterAlgorithm("auditScore", "Analytics", ScoreAnalyticsSource);
            RunOk("auditScore");

            Assert.AreEqual(StatusCodes.Status204NoContent, StatusCodeOf(_plugins.DeletePlugin("auditScore")));

            Assert.IsFalse(ListedAlgorithms().ContainsKey("auditScore"));
            AssertProblem(_analytics.RunAnalytics("auditScore", new AnalyticsSpecification()).Result,
                StatusCodes.Status404NotFound);
        }

        [TestMethod]
        public void RegisteredPathPlugin_IsNotReachableThroughAnalytics()
        {
            // Same registry, different contract: only Analytics entries join the analytics set.
            RegisterAlgorithm("auditPath", "Path", PathAlgorithmSource);

            Assert.IsFalse(ListedAlgorithms().ContainsKey("auditPath"));
            AssertProblem(_analytics.RunAnalytics("auditPath", new AnalyticsSpecification()).Result,
                StatusCodes.Status404NotFound);
            AssertProblem(_analytics.GetPartitionMembers("auditPath", 0, new AnalyticsSpecification()),
                StatusCodes.Status404NotFound);
        }

        [TestMethod]
        public void RegisteredPartitionPlugin_MembershipPage_IsReachable()
        {
            // The partition endpoint carried the same pre-check, so it needs the same proof.
            AddVertices(3);
            RegisterAlgorithm("auditPartition", "Analytics", PartitionAnalyticsSource);

            var result = _analytics.GetPartitionMembers("auditPartition", 7, new AnalyticsSpecification());
            Assert.AreEqual(StatusCodes.Status200OK, StatusCodeOf(result));
            var page = (result as OkObjectResult)?.Value as PartitionMembersREST;
            Assert.IsNotNull(page);
            Assert.AreEqual(7, page.PartitionId);
            Assert.AreEqual(3, page.Size);
            Assert.AreEqual(3, page.Members.Count);

            var expected = new List<Int32>();
            foreach (var vertex in _fallen8.GetAllVertices())
            {
                expected.Add(vertex.Id);
            }
            expected.Sort();
            CollectionAssert.AreEqual(expected, page.Members, "members come back ascending");

            // A partition the run did not produce stays the documented 404 (reached the plugin,
            // then found no such partition - not the unknown-algorithm 404).
            AssertProblem(_analytics.GetPartitionMembers("auditPartition", 99, new AnalyticsSpecification()),
                StatusCodes.Status404NotFound);
        }

        [TestMethod]
        public void RegisteredScorePlugin_OnThePartitionEndpoint_Is400NotA404()
        {
            // Reaching the plugin is exactly what makes this the documented "not a partition
            // algorithm" 400; before the fix the request never got past the 404 pre-check.
            AddVertices(2);
            RegisterAlgorithm("auditScore", "Analytics", ScoreAnalyticsSource);

            var problem = AssertProblem(
                _analytics.GetPartitionMembers("auditScore", 0, new AnalyticsSpecification()),
                StatusCodes.Status400BadRequest);
            StringAssert.Contains(problem.Detail, "not a partition algorithm");
        }
    }
}
