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
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Feature element-embeddings, Phase 2: the traversal context and the declarative
    ///   semantic block - code-free semantic filter/cost with dynamic code OFF, context-aware
    ///   fragments and stored queries with it ON, one-owner-per-slot conflicts, and the
    ///   subgraph registration-time binding.
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
    }
}
