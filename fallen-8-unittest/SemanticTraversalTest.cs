// MIT License
//
// SemanticTraversalTest.cs
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
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.SubGraph;
using NoSQL.GraphDB.Core.App.Helper;
using NoSQL.GraphDB.Core.SubGraph;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Feature element-embeddings, Phase 2: the traversal context and the declarative
    ///   semantic block - code-free semantic filter/cost with dynamic code OFF, context-aware
    ///   fragments and stored queries with it ON, one-owner-per-slot conflicts, and the
    ///   subgraph registration-time binding. Plus feature subgraph-semantic-thresholds: the
    ///   declarative per-pattern vertex thresholds (own region below).
    /// </summary>
    [TestClass]
    public class SemanticTraversalTest
    {
        private sealed class SemanticFactory : WebApplicationFactory<Program>
        {
            private readonly bool _enableDynamicCode;

            public SemanticFactory(bool enableDynamicCode)
            {
                _enableDynamicCode = enableDynamicCode;
            }

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseSetting("Fallen8:Durability:Volatile", "true");
                builder.UseSetting("Fallen8:Security:EnableDynamicCodeExecution", _enableDynamicCode ? "true" : "false");
            }
        }

        private static Fallen8 EngineOf(WebApplicationFactory<Program> factory)
            => (Fallen8)factory.Services.GetRequiredService<IFallen8>();

        /// <summary>
        ///   The fixture: a diamond a -> b -> d / a -> c -> d. Embeddings (2-dim, "default"):
        ///   a, b, d point along the query [1, 0]; c is orthogonal. A semantic filter or cost
        ///   against [1, 0] must prefer the b route and exclude/penalize c.
        /// </summary>
        private static (int a, int b, int c, int d) Diamond(Fallen8 engine)
        {
            var vtx = new CreateVerticesTransaction();
            for (var i = 0; i < 4; i++)
            {
                vtx.AddVertex(1u, "node");
            }
            engine.EnqueueTransaction(vtx).WaitUntilFinished();
            var v = vtx.GetCreatedVertices();
            var (a, b, c, d) = (v[0].Id, v[1].Id, v[2].Id, v[3].Id);

            var edges = new CreateEdgesTransaction();
            edges.AddEdge(a, "knows", b, 1u, "knows");
            edges.AddEdge(b, "knows", d, 1u, "knows");
            edges.AddEdge(a, "knows", c, 1u, "knows");
            edges.AddEdge(c, "knows", d, 1u, "knows");
            engine.EnqueueTransaction(edges).WaitUntilFinished();

            engine.EnqueueTransaction(new SetEmbeddingsTransaction()
                    .SetEmbedding(a, "default", new[] { 0.9f, 0.1f })
                    .SetEmbedding(b, "default", new[] { 1f, 0f })
                    .SetEmbedding(c, "default", new[] { 0f, 1f })
                    .SetEmbedding(d, "default", new[] { 0.8f, 0.2f }))
                .WaitUntilFinished();

            // Subgraph instances renumber element ids, so identify vertices by a name property.
            engine.EnqueueTransaction(new AddPropertiesTransaction()
                    .AddProperty(a, "name", "a").AddProperty(b, "name", "b")
                    .AddProperty(c, "name", "c").AddProperty(d, "name", "d"))
                .WaitUntilFinished();

            return (a, b, c, d);
        }

        private static List<string> VertexNames(NoSQL.GraphDB.Core.IFallen8 subGraph)
            => subGraph.GetAllVertices()
                .Select(v => v.TryGetProperty<string>(out var name, "name") ? name : "?")
                .OrderBy(n => n).ToList();

        private static Task<HttpResponseMessage> PostJson(HttpClient client, string url, string json)
            => client.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));

        private static Task<HttpResponseMessage> PutJson(HttpClient client, string url, string json)
            => client.PutAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));

        private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
            => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        private static List<int> FirstPathVertexIds(JsonElement paths)
        {
            // Path elements are edge hops (sourceVertexId -> targetVertexId): the vertex
            // sequence is the first hop's source followed by every hop's target.
            var elements = paths[0].GetProperty("pathElements").EnumerateArray().ToList();
            var ids = new List<int> { elements[0].GetProperty("sourceVertexId").GetInt32() };
            ids.AddRange(elements.Select(e => e.GetProperty("targetVertexId").GetInt32()));
            return ids;
        }

        #region declarative block, dynamic code OFF

        [TestMethod]
        public async Task DeclarativeMinScore_FiltersThePath_WithDynamicCodeOff()
        {
            using var factory = new SemanticFactory(enableDynamicCode: false);
            var (a, b, c, d) = Diamond(EngineOf(factory));
            using var client = factory.CreateClient();

            // Without semantic: both diamond routes are shortest (2 hops).
            using var plain = await PostJson(client, $"/path/{a}/to/{d}", "{}");
            Assert.AreEqual(HttpStatusCode.OK, plain.StatusCode);
            Assert.AreEqual(2, (await ReadJson(plain)).GetArrayLength(), "both diamond routes are 2 hops");

            // With minScore 0.5 against [1,0]: c (orthogonal) is filtered, one route remains.
            using var filtered = await PostJson(client, $"/path/{a}/to/{d}",
                "{ \"semantic\": { \"queryVector\": [1, 0], \"minScore\": 0.5 } }");
            Assert.AreEqual(HttpStatusCode.OK, filtered.StatusCode, await filtered.Content.ReadAsStringAsync());
            var paths = await ReadJson(filtered);
            Assert.AreEqual(1, paths.GetArrayLength(), "the orthogonal route must be filtered");
            CollectionAssert.AreEqual(new List<int> { a, b, d }, FirstPathVertexIds(paths));
        }

        [TestMethod]
        public async Task DeclarativeCostBySimilarity_PrefersTheCloserRoute_WithDynamicCodeOff()
        {
            using var factory = new SemanticFactory(enableDynamicCode: false);
            var (a, b, c, d) = Diamond(EngineOf(factory));
            using var client = factory.CreateClient();

            using var response = await PostJson(client, $"/path/{a}/to/{d}",
                "{ \"pathAlgorithmName\": \"DIJKSTRA\", \"maxResults\": 1, " +
                "  \"semantic\": { \"queryVector\": [1, 0], \"costBySimilarity\": true } }");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var paths = await ReadJson(response);
            Assert.AreEqual(1, paths.GetArrayLength());
            CollectionAssert.AreEqual(new List<int> { a, b, d }, FirstPathVertexIds(paths),
                "the semantically closer b route must win under cost-by-similarity");
            Assert.IsTrue(paths[0].GetProperty("totalWeight").GetDouble() > 0.0,
                "DIJKSTRA consumed the semantic vertex cost");
        }

        [TestMethod]
        public async Task DeclarativeMinScore_UnderL2_IsADistanceCeiling_FiltersThePath()
        {
            using var factory = new SemanticFactory(enableDynamicCode: false);
            var (a, b, c, d) = Diamond(EngineOf(factory));
            using var client = factory.CreateClient();

            // Under L2, minScore is a DISTANCE ceiling (lower-is-better): the orthogonal c is far
            // from [1,0] (dist ~1.41), the b-route vertices are near (<= ~0.28), so a ceiling of 0.5
            // keeps the b route and drops the c route. Exercises the L2 (lower-is-better) minScore
            // branch that the Cosine tests never reach.
            using var filtered = await PostJson(client, $"/path/{a}/to/{d}",
                "{ \"semantic\": { \"queryVector\": [1, 0], \"metric\": \"L2\", \"minScore\": 0.5 } }");
            Assert.AreEqual(HttpStatusCode.OK, filtered.StatusCode, await filtered.Content.ReadAsStringAsync());
            var paths = await ReadJson(filtered);
            Assert.AreEqual(1, paths.GetArrayLength(), "the far (orthogonal) route must be filtered under L2");
            CollectionAssert.AreEqual(new List<int> { a, b, d }, FirstPathVertexIds(paths));
        }

        [TestMethod]
        public async Task DeclarativeCostBySimilarity_UnderL2_PrefersTheCloserRoute()
        {
            using var factory = new SemanticFactory(enableDynamicCode: false);
            var (a, b, c, d) = Diamond(EngineOf(factory));
            using var client = factory.CreateClient();

            // Under L2 the vertex cost is the raw distance (lower-is-better): the b route is
            // metrically nearer to [1,0] than the orthogonal c route, so DIJKSTRA picks it.
            // Exercises the L2 branch of the cost mapping (SemanticTraversalHelper's non-Cosine cost).
            using var response = await PostJson(client, $"/path/{a}/to/{d}",
                "{ \"pathAlgorithmName\": \"DIJKSTRA\", \"maxResults\": 1, " +
                "  \"semantic\": { \"queryVector\": [1, 0], \"metric\": \"L2\", \"costBySimilarity\": true } }");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var paths = await ReadJson(response);
            Assert.AreEqual(1, paths.GetArrayLength());
            CollectionAssert.AreEqual(new List<int> { a, b, d }, FirstPathVertexIds(paths),
                "the metrically closer b route must win under L2 cost-by-similarity");
        }

        [TestMethod]
        public async Task DeclarativeSemantic_400Table()
        {
            using var factory = new SemanticFactory(enableDynamicCode: false);
            var (a, _, _, d) = Diamond(EngineOf(factory));
            using var client = factory.CreateClient();

            foreach (var (body, reason) in new (string, string)[]
            {
                ("{ \"semantic\": { } }", "missing queryVector"),
                ("{ \"semantic\": { \"queryVector\": [] } }", "empty queryVector"),
                ("{ \"semantic\": { \"queryVector\": [1, 0], \"metric\": \"Chebyshev\" } }", "unknown metric"),
                ("{ \"semantic\": { \"queryVector\": [1, 0], \"embeddingName\": \"no good\" } }", "invalid embedding name"),
                ("{ \"semantic\": { \"queryVector\": [0, 0] } }", "zero-norm under Cosine"),
                ("{ \"semantic\": { \"queryVector\": [1, 0], \"metric\": \"DotProduct\", \"costBySimilarity\": true } }", "DotProduct cost"),
            })
            {
                using var response = await PostJson(client, $"/path/{a}/to/{d}", body);
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, reason);
            }
        }

        [TestMethod]
        public async Task DeclarativeSemantic_CarriesNoCode_SoTheDynamicCodeGateStaysShut()
        {
            using var factory = new SemanticFactory(enableDynamicCode: false);
            var (a, _, _, d) = Diamond(EngineOf(factory));
            using var client = factory.CreateClient();

            // An inline fragment is still 403 with the switch off - the semantic block
            // does not widen the gate.
            using var forbidden = await PostJson(client, $"/path/{a}/to/{d}",
                "{ \"filter\": { \"vertexFilter\": \"return (v) => true;\" }, " +
                "  \"semantic\": { \"queryVector\": [1, 0] } }");
            Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        #endregion

        #region context-aware fragments + stored queries, dynamic code ON

        [TestMethod]
        public async Task ContextFragment_MatchesTheDeclarativeResult()
        {
            using var factory = new SemanticFactory(enableDynamicCode: true);
            var (a, b, c, d) = Diamond(EngineOf(factory));
            using var client = factory.CreateClient();

            // The same threshold, written as a C# fragment over context.TrySimilarity.
            using var viaFragment = await PostJson(client, $"/path/{a}/to/{d}",
                "{ \"filter\": { \"vertexFilter\": \"return (v) => context.TrySimilarity(v, out var s) && s >= 0.5f;\" }, " +
                "  \"semantic\": { \"queryVector\": [1, 0] } }");
            Assert.AreEqual(HttpStatusCode.OK, viaFragment.StatusCode, await viaFragment.Content.ReadAsStringAsync());
            var fragmentPaths = await ReadJson(viaFragment);

            Assert.AreEqual(1, fragmentPaths.GetArrayLength());
            CollectionAssert.AreEqual(new List<int> { a, b, d }, FirstPathVertexIds(fragmentPaths),
                "fragment and declarative semantics must agree on the fixture");
        }

        [TestMethod]
        public async Task StoredPathQuery_ReadsTheInvocationContext()
        {
            using var factory = new SemanticFactory(enableDynamicCode: true);
            var (a, b, c, d) = Diamond(EngineOf(factory));
            using var client = factory.CreateClient();

            using var registration = await PostJson(client, "/storedquery",
                "{ \"name\": \"close-nodes\", \"kind\": \"Path\", \"path\": { \"filter\": { " +
                "\"vertexFilter\": \"return (v) => context.TrySimilarity(v, out var s) && s >= 0.5f;\", " +
                "\"edgeFilter\": \"return (e) => true;\", \"edgePropertyFilter\": \"return (p) => true;\" } } }");
            Assert.AreEqual(HttpStatusCode.Created, registration.StatusCode, await registration.Content.ReadAsStringAsync());

            // Invocation supplies the query vector through the same semantic block.
            using var response = await PostJson(client, $"/path/{a}/to/{d}",
                "{ \"storedQuery\": \"close-nodes\", \"semantic\": { \"queryVector\": [1, 0] } }");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var paths = await ReadJson(response);
            Assert.AreEqual(1, paths.GetArrayLength());
            CollectionAssert.AreEqual(new List<int> { a, b, d }, FirstPathVertexIds(paths));

            // Without a vector the stored fragment sees the empty context: every similarity is
            // false, so the filter drops everything - deterministic, not an error.
            using var noVector = await PostJson(client, $"/path/{a}/to/{d}", "{ \"storedQuery\": \"close-nodes\" }");
            Assert.AreEqual(HttpStatusCode.OK, noVector.StatusCode);
            Assert.AreEqual(0, (await ReadJson(noVector)).GetArrayLength());
        }

        [TestMethod]
        public async Task OneOwnerPerSlot_Conflicts_Are400()
        {
            using var factory = new SemanticFactory(enableDynamicCode: true);
            var (a, _, _, d) = Diamond(EngineOf(factory));
            using var client = factory.CreateClient();

            // minScore vs. vertex filter fragment.
            using var filterClash = await PostJson(client, $"/path/{a}/to/{d}",
                "{ \"filter\": { \"vertexFilter\": \"return (v) => true;\" }, " +
                "  \"semantic\": { \"queryVector\": [1, 0], \"minScore\": 0.5 } }");
            Assert.AreEqual(HttpStatusCode.BadRequest, filterClash.StatusCode);

            // costBySimilarity vs. vertex cost fragment.
            using var costClash = await PostJson(client, $"/path/{a}/to/{d}",
                "{ \"cost\": { \"vertexCost\": \"return (v) => 1.0;\" }, " +
                "  \"semantic\": { \"queryVector\": [1, 0], \"costBySimilarity\": true } }");
            Assert.AreEqual(HttpStatusCode.BadRequest, costClash.StatusCode);
        }

        #endregion

        #region subgraph

        [TestMethod]
        public async Task SubGraph_DeclarativeMinScore_PreFilters_WithDynamicCodeOff()
        {
            using var factory = new SemanticFactory(enableDynamicCode: false);
            var engine = EngineOf(factory);
            var (a, b, c, d) = Diamond(engine);
            using var client = factory.CreateClient();

            using var created = await PutJson(client, "/subgraph",
                "{ \"name\": \"close\", \"semantic\": { \"queryVector\": [1, 0], \"minScore\": 0.5 } }");
            Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());

            Assert.IsTrue(engine.SubGraphFactory.TryGetSubGraph(out var subGraph, "close"));
            CollectionAssert.AreEqual(new List<string> { "a", "b", "d" }, VertexNames(subGraph.SubGraph),
                "the orthogonal vertex must not be copied into the subgraph");
        }

        [TestMethod]
        public async Task SubGraph_Semantic400Table()
        {
            using var factory = new SemanticFactory(enableDynamicCode: true);
            Diamond(EngineOf(factory));
            using var client = factory.CreateClient();

            // Registration-time conflict: minScore vs. inline vertexFilter fragment.
            using var clash = await PutJson(client, "/subgraph",
                "{ \"name\": \"x1\", \"vertexFilter\": \"return (ge) => true;\", " +
                "  \"semantic\": { \"queryVector\": [1, 0], \"minScore\": 0.5 } }");
            Assert.AreEqual(HttpStatusCode.BadRequest, clash.StatusCode);

            // costBySimilarity is a path concept.
            using var cost = await PutJson(client, "/subgraph",
                "{ \"name\": \"x2\", \"semantic\": { \"queryVector\": [1, 0], \"costBySimilarity\": true } }");
            Assert.AreEqual(HttpStatusCode.BadRequest, cost.StatusCode);

            // Stored-template invocation cannot rebind a context (spec non-goal).
            using var registration = await PostJson(client, "/storedquery",
                "{ \"name\": \"tpl\", \"kind\": \"SubGraph\", \"subGraph\": { \"vertexFilter\": \"return (ge) => true;\" } }");
            Assert.AreEqual(HttpStatusCode.Created, registration.StatusCode, await registration.Content.ReadAsStringAsync());
            using var stored = await PutJson(client, "/subgraph",
                "{ \"name\": \"x3\", \"storedQuery\": \"tpl\", \"semantic\": { \"queryVector\": [1, 0] } }");
            Assert.AreEqual(HttpStatusCode.BadRequest, stored.StatusCode);
        }

        [TestMethod]
        public async Task SubGraph_SemanticBinding_SurvivesRecalculation_WithoutReEmbedding()
        {
            using var factory = new SemanticFactory(enableDynamicCode: false);
            var engine = EngineOf(factory);
            var (a, b, c, d) = Diamond(engine);
            using var client = factory.CreateClient();

            using var created = await PutJson(client, "/subgraph",
                "{ \"name\": \"close\", \"semantic\": { \"queryVector\": [1, 0], \"minScore\": 0.5 } }");
            Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);

            // A new vertex with a close embedding joins the graph AFTER registration...
            var vtx = new CreateVertexTransaction { Definition = new Core.Model.VertexDefinition { CreationDate = 1u, Label = "node" } };
            engine.EnqueueTransaction(vtx).WaitUntilFinished();
            var e = vtx.VertexCreated.Id;
            engine.EnqueueTransaction(new SetEmbeddingsTransaction().SetEmbedding(e, "default", new[] { 1f, 0.1f }))
                .WaitUntilFinished();
            engine.EnqueueTransaction(new AddPropertiesTransaction().AddProperty(e, "name", "e")).WaitUntilFinished();

            // ...and recalculation - the registration-time context, no inference - picks it up.
            using var recalc = await PostJson(client, "/subgraph/close/recalculate", "{}");
            Assert.AreEqual(HttpStatusCode.OK, recalc.StatusCode, await recalc.Content.ReadAsStringAsync());

            Assert.IsTrue(engine.SubGraphFactory.TryGetSubGraph(out var subGraph, "close"));
            CollectionAssert.AreEqual(new List<string> { "a", "b", "d", "e" }, VertexNames(subGraph.SubGraph));
        }

        #endregion

        #region pattern thresholds (feature subgraph-semantic-thresholds)

        [TestMethod]
        public async Task SubGraph_PatternThreshold_AppliesAtItsStep_WithDynamicCodeOff()
        {
            using var factory = new SemanticFactory(enableDynamicCode: false);
            var engine = EngineOf(factory);
            Diamond(engine);
            using var client = factory.CreateClient();

            // Threshold on the SECOND vertex step only: c (orthogonal to [1,0]) may still START
            // a match (c -> d) but never END one (a -> c is pruned). Distinguishes a step-level
            // filter from the top-level pre-filter, which would drop c entirely.
            using var created = await PutJson(client, "/subgraph",
                "{ \"name\": \"steps\", \"semantic\": { \"queryVector\": [1, 0] }, " +
                "  \"patterns\": [ { \"type\": \"Vertex\" }, { \"type\": \"Edge\" }, " +
                "                  { \"type\": \"Vertex\", \"semanticMinScore\": 0.5 } ] }");
            Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());

            Assert.IsTrue(engine.SubGraphFactory.TryGetSubGraph(out var subGraph, "steps"));
            CollectionAssert.AreEqual(new List<string> { "a", "b", "c", "d" }, VertexNames(subGraph.SubGraph),
                "c starts the c->d match and must survive");
            Assert.AreEqual(3, subGraph.SubGraph.GetAllEdges().Count,
                "a->c must be pruned (its target fails the step threshold); a->b, b->d, c->d remain");
        }

        [TestMethod]
        public async Task SubGraph_PatternThreshold_MatchesContextFragmentParity()
        {
            using var factory = new SemanticFactory(enableDynamicCode: true);
            var engine = EngineOf(factory);
            Diamond(engine);
            using var client = factory.CreateClient();

            const string fragment = "return (v) => context.TrySimilarity(v, out var s) && s >= 0.5f;";
            using var viaFragment = await PutJson(client, "/subgraph",
                "{ \"name\": \"frag\", \"semantic\": { \"queryVector\": [1, 0] }, " +
                "  \"patterns\": [ { \"type\": \"Vertex\", \"vertexFilter\": \"" + fragment + "\" }, " +
                "                  { \"type\": \"Edge\" }, " +
                "                  { \"type\": \"Vertex\", \"vertexFilter\": \"" + fragment + "\" } ] }");
            Assert.AreEqual(HttpStatusCode.Created, viaFragment.StatusCode, await viaFragment.Content.ReadAsStringAsync());

            using var viaThreshold = await PutJson(client, "/subgraph",
                "{ \"name\": \"decl\", \"semantic\": { \"queryVector\": [1, 0] }, " +
                "  \"patterns\": [ { \"type\": \"Vertex\", \"semanticMinScore\": 0.5 }, " +
                "                  { \"type\": \"Edge\" }, " +
                "                  { \"type\": \"Vertex\", \"semanticMinScore\": 0.5 } ] }");
            Assert.AreEqual(HttpStatusCode.Created, viaThreshold.StatusCode, await viaThreshold.Content.ReadAsStringAsync());

            Assert.IsTrue(engine.SubGraphFactory.TryGetSubGraph(out var fromFragment, "frag"));
            Assert.IsTrue(engine.SubGraphFactory.TryGetSubGraph(out var fromThreshold, "decl"));
            CollectionAssert.AreEqual(new List<string> { "a", "b", "d" }, VertexNames(fromFragment.SubGraph));
            CollectionAssert.AreEqual(VertexNames(fromFragment.SubGraph), VertexNames(fromThreshold.SubGraph),
                "the declarative threshold and the hand-rolled context fragment must select the same vertices");
            Assert.AreEqual(fromFragment.SubGraph.GetAllEdges().Count, fromThreshold.SubGraph.GetAllEdges().Count);
        }

        [TestMethod]
        public async Task SubGraph_PatternThreshold_400Table()
        {
            using var factory = new SemanticFactory(enableDynamicCode: true);
            Diamond(EngineOf(factory));
            using var client = factory.CreateClient();

            // A threshold is a vertex concept: explicit 400 on an edge step, not silently ignored.
            using var onEdge = await PutJson(client, "/subgraph",
                "{ \"name\": \"x1\", \"semantic\": { \"queryVector\": [1, 0] }, " +
                "  \"patterns\": [ { \"type\": \"Vertex\" }, { \"type\": \"Edge\", \"semanticMinScore\": 0.5 }, { \"type\": \"Vertex\" } ] }");
            Assert.AreEqual(HttpStatusCode.BadRequest, onEdge.StatusCode);
            StringAssert.Contains(await onEdge.Content.ReadAsStringAsync(), "Vertex patterns only");

            // Same rule on the other edge kind.
            using var onVariableEdge = await PutJson(client, "/subgraph",
                "{ \"name\": \"x1b\", \"semantic\": { \"queryVector\": [1, 0] }, " +
                "  \"patterns\": [ { \"type\": \"Vertex\" }, " +
                "                  { \"type\": \"VariableLengthEdge\", \"minLength\": 1, \"maxLength\": 2, \"semanticMinScore\": 0.5 }, " +
                "                  { \"type\": \"Vertex\" } ] }");
            Assert.AreEqual(HttpStatusCode.BadRequest, onVariableEdge.StatusCode);
            StringAssert.Contains(await onVariableEdge.Content.ReadAsStringAsync(), "Vertex patterns only");

            // The threshold scores against the request's semantic query - required.
            using var noQuery = await PutJson(client, "/subgraph",
                "{ \"name\": \"x2\", " +
                "  \"patterns\": [ { \"type\": \"Vertex\", \"semanticMinScore\": 0.5 }, { \"type\": \"Edge\" }, { \"type\": \"Vertex\" } ] }");
            Assert.AreEqual(HttpStatusCode.BadRequest, noQuery.StatusCode);
            StringAssert.Contains(await noQuery.Content.ReadAsStringAsync(), "requires a 'semantic' block");

            // One owner per slot, per step.
            using var clash = await PutJson(client, "/subgraph",
                "{ \"name\": \"x3\", \"semantic\": { \"queryVector\": [1, 0] }, " +
                "  \"patterns\": [ { \"type\": \"Vertex\", \"vertexFilter\": \"return (v) => true;\", \"semanticMinScore\": 0.5 } ] }");
            Assert.AreEqual(HttpStatusCode.BadRequest, clash.StatusCode);
            StringAssert.Contains(await clash.Content.ReadAsStringAsync(), "own the same slot");

            // A stored template binds its delegates at ITS registration, where no semantic query
            // exists - a threshold would close over the empty context and match nothing.
            using var template = await PostJson(client, "/storedquery",
                "{ \"name\": \"tplthr\", \"kind\": \"SubGraph\", " +
                "  \"subGraph\": { \"patterns\": [ { \"type\": \"Vertex\", \"semanticMinScore\": 0.5 } ] } }");
            Assert.AreEqual(HttpStatusCode.BadRequest, template.StatusCode);
            StringAssert.Contains(await template.Content.ReadAsStringAsync(), "stored SubGraph template");
        }

        [TestMethod]
        public void SubGraph_PatternThreshold_NonFinite_IsRejected()
        {
            // NaN cannot arrive over JSON; this pins the defensive validation for direct callers
            // (recipes, embedded engine use) below the HTTP layer - for BOTH threshold fields.
            var patternLevel = new SubGraphSpecification
            {
                Name = "nan",
                Semantic = new SemanticTraversalSpecification { QueryVector = new[] { 1f, 0f } },
                Patterns = new List<PatternSpecification>
                {
                    new PatternSpecification { Type = "Vertex", SemanticMinScore = Double.NaN }
                }
            };

            var patternError = CodeGenerationHelper.TryGenerateSubGraphDefinition(patternLevel, out var patternDefinition);
            Assert.IsNull(patternDefinition);
            StringAssert.Contains(patternError, "finite");

            var topLevel = new SubGraphSpecification
            {
                Name = "nan",
                Semantic = new SemanticTraversalSpecification
                {
                    QueryVector = new[] { 1f, 0f },
                    MinScore = Double.PositiveInfinity
                }
            };

            var topLevelError = CodeGenerationHelper.TryGenerateSubGraphDefinition(topLevel, out var topLevelDefinition);
            Assert.IsNull(topLevelDefinition);
            StringAssert.Contains(topLevelError, "finite");
        }

        [TestMethod]
        public async Task SubGraph_PatternThreshold_BindingSurvivesRecalculation_WithoutReEmbedding()
        {
            using var factory = new SemanticFactory(enableDynamicCode: false);
            var engine = EngineOf(factory);
            var (a, b, c, d) = Diamond(engine);
            using var client = factory.CreateClient();

            using var created = await PutJson(client, "/subgraph",
                "{ \"name\": \"steps\", \"semantic\": { \"queryVector\": [1, 0] }, " +
                "  \"patterns\": [ { \"type\": \"Vertex\", \"semanticMinScore\": 0.5 }, " +
                "                  { \"type\": \"Edge\" }, " +
                "                  { \"type\": \"Vertex\", \"semanticMinScore\": 0.5 } ] }");
            Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());

            // A close vertex and an edge to it join AFTER registration...
            var vtx = new CreateVertexTransaction { Definition = new Core.Model.VertexDefinition { CreationDate = 1u, Label = "node" } };
            engine.EnqueueTransaction(vtx).WaitUntilFinished();
            var e = vtx.VertexCreated.Id;
            engine.EnqueueTransaction(new SetEmbeddingsTransaction().SetEmbedding(e, "default", new[] { 1f, 0.1f }))
                .WaitUntilFinished();
            engine.EnqueueTransaction(new AddPropertiesTransaction().AddProperty(e, "name", "e")).WaitUntilFinished();
            var edge = new CreateEdgesTransaction();
            edge.AddEdge(d, "knows", e, 1u, "knows");
            engine.EnqueueTransaction(edge).WaitUntilFinished();

            // ...and recalculation - the registration-bound context, no inference - matches d -> e.
            using var recalc = await PostJson(client, "/subgraph/steps/recalculate", "{}");
            Assert.AreEqual(HttpStatusCode.OK, recalc.StatusCode, await recalc.Content.ReadAsStringAsync());

            Assert.IsTrue(engine.SubGraphFactory.TryGetSubGraph(out var subGraph, "steps"));
            CollectionAssert.AreEqual(new List<string> { "a", "b", "d", "e" }, VertexNames(subGraph.SubGraph));
        }

        [TestMethod]
        public async Task SubGraph_PatternThreshold_UnderL2_IsADistanceCeiling()
        {
            using var factory = new SemanticFactory(enableDynamicCode: false);
            var engine = EngineOf(factory);
            Diamond(engine);
            using var client = factory.CreateClient();

            // Under L2 the threshold is a distance CEILING: were the comparison direction wrong,
            // only the far pair (a -> c) would match and the set would collapse to { a, c }.
            using var created = await PutJson(client, "/subgraph",
                "{ \"name\": \"near\", \"semantic\": { \"queryVector\": [1, 0], \"metric\": \"L2\" }, " +
                "  \"patterns\": [ { \"type\": \"Vertex\" }, { \"type\": \"Edge\" }, " +
                "                  { \"type\": \"Vertex\", \"semanticMinScore\": 0.5 } ] }");
            Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());

            Assert.IsTrue(engine.SubGraphFactory.TryGetSubGraph(out var subGraph, "near"));
            CollectionAssert.AreEqual(new List<string> { "a", "b", "c", "d" }, VertexNames(subGraph.SubGraph));
            Assert.AreEqual(3, subGraph.SubGraph.GetAllEdges().Count, "a->c must be pruned: c is ~1.41 from [1,0]");
        }

        [TestMethod]
        public async Task SubGraph_PatternThreshold_MissingEmbedding_NeverMatches()
        {
            using var factory = new SemanticFactory(enableDynamicCode: false);
            var engine = EngineOf(factory);
            var (a, b, c, d) = Diamond(engine);
            using var client = factory.CreateClient();

            // f has NO embedding; b -> f exists. Threshold -1 admits EVERY embedded vertex under
            // Cosine, so f's exclusion can only come from the missing embedding.
            var vtx = new CreateVertexTransaction { Definition = new Core.Model.VertexDefinition { CreationDate = 1u, Label = "node" } };
            engine.EnqueueTransaction(vtx).WaitUntilFinished();
            var f = vtx.VertexCreated.Id;
            engine.EnqueueTransaction(new AddPropertiesTransaction().AddProperty(f, "name", "f")).WaitUntilFinished();
            var edge = new CreateEdgesTransaction();
            edge.AddEdge(b, "knows", f, 1u, "knows");
            engine.EnqueueTransaction(edge).WaitUntilFinished();

            using var created = await PutJson(client, "/subgraph",
                "{ \"name\": \"embedded\", \"semantic\": { \"queryVector\": [1, 0] }, " +
                "  \"patterns\": [ { \"type\": \"Vertex\" }, { \"type\": \"Edge\" }, " +
                "                  { \"type\": \"Vertex\", \"semanticMinScore\": -1 } ] }");
            Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());

            Assert.IsTrue(engine.SubGraphFactory.TryGetSubGraph(out var subGraph, "embedded"));
            CollectionAssert.AreEqual(new List<string> { "a", "b", "c", "d" }, VertexNames(subGraph.SubGraph),
                "the unembedded vertex must never match a semantic threshold");
        }

        [TestMethod]
        public async Task SubGraph_PatternThreshold_MixedOwnershipAcrossSteps()
        {
            using var factory = new SemanticFactory(enableDynamicCode: true);
            var engine = EngineOf(factory);
            Diamond(engine);
            using var client = factory.CreateClient();

            // Ownership is per STEP: a declarative threshold on step 1 and a compiled context
            // fragment on step 3 coexist in one request.
            using var created = await PutJson(client, "/subgraph",
                "{ \"name\": \"mixed\", \"semantic\": { \"queryVector\": [1, 0] }, " +
                "  \"patterns\": [ { \"type\": \"Vertex\", \"semanticMinScore\": 0.5 }, " +
                "                  { \"type\": \"Edge\" }, " +
                "                  { \"type\": \"Vertex\", \"vertexFilter\": \"return (v) => context.TrySimilarity(v, out var s) && s >= 0.5f;\" } ] }");
            Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());

            Assert.IsTrue(engine.SubGraphFactory.TryGetSubGraph(out var subGraph, "mixed"));
            CollectionAssert.AreEqual(new List<string> { "a", "b", "d" }, VertexNames(subGraph.SubGraph));
        }

        #endregion

        #region semantic summary echo (feature subgraph-semantic-thresholds)

        private static void AssertEcho(JsonElement summary)
        {
            var semantic = summary.GetProperty("semantic");
            Assert.AreEqual("default", semantic.GetProperty("embeddingName").GetString());
            Assert.AreEqual("Cosine", semantic.GetProperty("metric").GetString());
            Assert.AreEqual(2, semantic.GetProperty("dimension").GetInt32());
            Assert.AreEqual(0.5, semantic.GetProperty("minScore").GetDouble(), 1e-9);
            var thresholds = semantic.GetProperty("patternThresholds");
            Assert.AreEqual(1, thresholds.GetArrayLength());
            Assert.AreEqual("start", thresholds[0].GetProperty("pattern").GetString());
            Assert.AreEqual(0.6, thresholds[0].GetProperty("minScore").GetDouble(), 1e-9);
        }

        [TestMethod]
        public async Task SubGraphSummary_EchoesBoundSemanticState_NeverTheVector()
        {
            using var factory = new SemanticFactory(enableDynamicCode: false);
            Diamond(EngineOf(factory));
            using var client = factory.CreateClient();

            using var created = await PutJson(client, "/subgraph",
                "{ \"name\": \"echo\", \"semantic\": { \"queryVector\": [1, 0], \"minScore\": 0.5 }, " +
                "  \"patterns\": [ { \"type\": \"Vertex\", \"patternName\": \"start\", \"semanticMinScore\": 0.6 }, " +
                "                  { \"type\": \"Edge\" }, { \"type\": \"Vertex\" } ] }");
            Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());
            AssertEcho(await ReadJson(created));

            using var fetched = await client.GetAsync("/subgraph/echo");
            Assert.AreEqual(HttpStatusCode.OK, fetched.StatusCode);
            var body = await fetched.Content.ReadAsStringAsync();
            Assert.IsFalse(body.Contains("queryVector"), "the bound vector must never ride a summary");
            AssertEcho(JsonDocument.Parse(body).RootElement);

            // The recipe - and with it the echo - survives recalculation (in-place result update).
            using var recalc = await PostJson(client, "/subgraph/echo/recalculate", "{}");
            Assert.AreEqual(HttpStatusCode.OK, recalc.StatusCode);
            AssertEcho(await ReadJson(recalc));
        }

        [TestMethod]
        public async Task SubGraphSummary_NonSemanticSubgraph_CarriesNoEcho()
        {
            using var factory = new SemanticFactory(enableDynamicCode: false);
            Diamond(EngineOf(factory));
            using var client = factory.CreateClient();

            using var created = await PutJson(client, "/subgraph", "{ \"name\": \"plain\" }");
            Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());
            Assert.IsFalse((await ReadJson(created)).TryGetProperty("semantic", out _),
                "a non-semantic subgraph must not carry the echo property at all");
        }

        [TestMethod]
        public void SubGraphSummary_EchoesQueryText_AndIndexesUnnamedSteps()
        {
            // queryText persists in the recipe NEXT TO the resolved vector (the resolver fills
            // queryVector without clearing the text); the echo documents that intent. Unit-level
            // because the e2e path would need a live embedding provider.
            var result = new SubGraphResult
            {
                Definitions = new SubGraphDefinition { Name = "t" },
                Recipe = new SubGraphRecipe
                {
                    SpecificationJson =
                        "{ \"name\": \"t\", " +
                        "  \"semantic\": { \"queryVector\": [0.1, 0.2, 0.3], \"queryText\": \"red bicycles\" }, " +
                        "  \"patterns\": [ { \"type\": \"Vertex\", \"semanticMinScore\": 0.6 } ] }"
                }
            };

            var summary = SubGraphSummary.FromResult(result, canRecalculate: true);

            Assert.AreEqual("red bicycles", summary.Semantic.QueryText);
            Assert.AreEqual(3, summary.Semantic.Dimension);
            Assert.IsNull(summary.Semantic.MinScore, "no top-level threshold was set");
            Assert.AreEqual("0", summary.Semantic.PatternThresholds[0].Pattern,
                "an unnamed step is identified by its zero-based index");
            Assert.AreEqual(0.6, summary.Semantic.PatternThresholds[0].MinScore, 1e-9);
        }

        #endregion
    }
}
