// MIT License
//
// AuditDefectBoundIndexTest.cs
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
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Audit defect B14: the generic index-content REMOVE routes used to write straight through
    ///   to a BOUND vector index, dropping a slot while the element kept its embedding, so kNN
    ///   silently lost a live element. Both removals now share the add path's refusal; the
    ///   contract itself is documented once, on GraphController's bound-index refusal helper.
    /// </summary>
    [TestClass]
    public class AuditDefectBoundIndexTest
    {
        private sealed class IndexFactoryHost : WebApplicationFactory<Program>
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseSetting("Fallen8:Durability:Volatile", "true");
            }
        }

        private static Fallen8 EngineOf(WebApplicationFactory<Program> factory)
            => factory.Services.GetRequiredService<NoSQL.GraphDB.App.Namespaces.Fallen8Namespaces>().Default.Engine;

        private static int Vertex(Fallen8 engine)
        {
            var tx = new CreateVertexTransaction { Definition = new VertexDefinition { CreationDate = 1u, Label = "p" } };
            engine.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.VertexCreated.Id;
        }

        private static StringContent Json(string json) => new StringContent(json, Encoding.UTF8, "application/json");

        /// <summary>A bound vector index over embedding "default", dimension 2.</summary>
        private static IIndex BoundIndex(Fallen8 engine, string indexId)
        {
            Assert.IsTrue(engine.IndexFactory.TryCreateIndex(out var index, indexId, "VectorIndex",
                new Dictionary<string, object> { { "dimension", 2 }, { "embeddingName", "default" } }));
            return index;
        }

        /// <summary>An UNBOUND vector index (no embeddingName), dimension 2: it owns its content.</summary>
        private static IIndex UnboundIndex(Fallen8 engine, string indexId)
        {
            Assert.IsTrue(engine.IndexFactory.TryCreateIndex(out var index, indexId, "VectorIndex",
                new Dictionary<string, object> { { "dimension", 2 } }));
            return index;
        }

        private static void SetEmbedding(Fallen8 engine, int elementId, float[] vector)
        {
            engine.EnqueueTransaction(new SetEmbeddingsTransaction().SetEmbedding(elementId, "default", vector))
                .WaitUntilFinished();
        }

        private static Task<HttpResponseMessage> DeleteWithBody(HttpClient client, string url, string body)
        {
            var request = new HttpRequestMessage(HttpMethod.Delete, url) { Content = Json(body) };
            return client.SendAsync(request);
        }

        /// <summary>The ids the kNN endpoint still returns for the given index.</summary>
        private static async Task<List<int>> VectorScanIds(HttpClient client, string indexId)
        {
            using var response = await client.PostAsync("/scan/index/vector",
                Json("{\"indexId\":\"" + indexId + "\",\"query\":[1,0],\"k\":10}"));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
                .GetProperty("results").EnumerateArray()
                .Select(hit => hit.GetProperty("graphElementId").GetInt32()).ToList();
        }

        private static async Task AssertBoundRefusal(HttpResponseMessage response, string reason)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, reason + " (body: " + body + ")");
            Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType.MediaType,
                "the refusal goes out through the shared problem+json envelope");
            StringAssert.Contains(JsonDocument.Parse(body).RootElement.GetProperty("detail").GetString(),
                "bound to embedding 'default'",
                "the refusal comes from the one shared bound-index message, not a second rule");
        }

        [TestMethod]
        public async Task ElementRemoval_OnABoundIndex_Is400_AndTheProjectionSurvives()
        {
            using var factory = new IndexFactoryHost();
            var engine = EngineOf(factory);
            var a = Vertex(engine);
            var index = BoundIndex(engine, "emb");
            SetEmbedding(engine, a, new[] { 1f, 0f });
            Assert.AreEqual(1, index.CountOfValues(), "the committed embedding projected into the bound index");
            using var client = factory.CreateClient();

            using (var response = await client.DeleteAsync("/index/emb/" + a))
            {
                await AssertBoundRefusal(response,
                    "a bound index is a projection; removing a still-embedded element would desync it");
            }

            Assert.AreEqual(1, index.CountOfValues(), "the refused removal left the projection untouched");
            CollectionAssert.AreEquivalent(new List<int> { a }, await VectorScanIds(client, "emb"),
                "the element is still reachable by kNN, which the silent removal used to break");
        }

        [TestMethod]
        public async Task KeyRemoval_OnABoundIndex_Is400_InsteadOfASilentFalse()
        {
            using var factory = new IndexFactoryHost();
            var engine = EngineOf(factory);
            var a = Vertex(engine);
            var index = BoundIndex(engine, "emb");
            SetEmbedding(engine, a, new[] { 1f, 0f });
            using var client = factory.CreateClient();

            using (var response = await DeleteWithBody(client, "/index/emb/propertyValue",
                "{\"propertyId\":\"key\",\"propertyValue\":\"1\",\"fullQualifiedTypeName\":\"System.Single\"}"))
            {
                await AssertBoundRefusal(response,
                    "the key removal route refuses a bound index too, rather than reporting a silent false");
            }

            Assert.AreEqual(1, index.CountOfValues(), "nothing left the projection");
        }

        [TestMethod]
        public async Task BoundRefusal_PrecedesTheElementLookup_ButAnIndexMissStillAnswersFalse()
        {
            using var factory = new IndexFactoryHost();
            var engine = EngineOf(factory);
            var a = Vertex(engine);
            BoundIndex(engine, "emb");
            SetEmbedding(engine, a, new[] { 1f, 0f });
            using var client = factory.CreateClient();

            // Index-level contract first, exactly as on the add path: an absent element on a bound
            // index is still a refusal, not the 200/false element miss.
            using (var response = await client.DeleteAsync("/index/emb/424242"))
            {
                await AssertBoundRefusal(response, "the bound refusal is decided before the element lookup");
            }

            // An unknown index keeps its documented miss shape (200 with a false body) on both routes.
            using (var response = await client.DeleteAsync("/index/no-such-index/" + a))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("false", await response.Content.ReadAsStringAsync(),
                    "an index miss is unchanged by the guard");
            }

            using (var response = await DeleteWithBody(client, "/index/no-such-index/propertyValue",
                "{\"propertyId\":\"key\",\"propertyValue\":\"x\",\"fullQualifiedTypeName\":\"System.String\"}"))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("false", await response.Content.ReadAsStringAsync(),
                    "an index miss is unchanged by the guard on the key route as well");
            }
        }

        [TestMethod]
        public async Task NamespaceTwinRoute_RefusesTheBoundRemovalToo()
        {
            using var factory = new IndexFactoryHost();
            var engine = EngineOf(factory);
            var a = Vertex(engine);
            var index = BoundIndex(engine, "emb");
            SetEmbedding(engine, a, new[] { 1f, 0f });
            using var client = factory.CreateClient();

            using (var response = await client.DeleteAsync("/ns/default/index/emb/" + a))
            {
                await AssertBoundRefusal(response, "the /ns/{ns} twin resolves the same action, so it refuses too");
            }

            Assert.AreEqual(1, index.CountOfValues());
        }

        [TestMethod]
        public async Task Removals_OnAnUnboundVectorIndex_StillSucceed()
        {
            using var factory = new IndexFactoryHost();
            var engine = EngineOf(factory);
            var a = Vertex(engine);
            var index = UnboundIndex(engine, "free");
            using var client = factory.CreateClient();

            using (var add = await client.PutAsync("/index/vector/free",
                Json("{\"graphElementId\":" + a + ",\"vector\":[1,0]}")))
            {
                Assert.AreEqual(HttpStatusCode.OK, add.StatusCode, await add.Content.ReadAsStringAsync());
            }
            Assert.AreEqual(1, index.CountOfValues());

            // An unbound vector index owns its content: a float[] key cannot be expressed through
            // a PropertySpecification, so the key route can only ever report a miss here.
            using (var response = await DeleteWithBody(client, "/index/free/propertyValue",
                "{\"propertyId\":\"key\",\"propertyValue\":\"1\",\"fullQualifiedTypeName\":\"System.Single\"}"))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("false", await response.Content.ReadAsStringAsync(),
                    "a Single key is not the float[] the vector index stores, so nothing matches");
            }
            Assert.AreEqual(1, index.CountOfValues(), "the key miss removed nothing");

            using (var response = await client.DeleteAsync("/index/free/" + a))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("true", await response.Content.ReadAsStringAsync(),
                    "explicit removal stays available where the caller owns the membership");
            }
            Assert.AreEqual(0, index.CountOfValues(), "the element left the unbound index");
            Assert.AreEqual(0, (await VectorScanIds(client, "free")).Count);
        }

        [TestMethod]
        public async Task Removals_OnANonVectorIndex_AreUnaffected()
        {
            using var factory = new IndexFactoryHost();
            var engine = EngineOf(factory);
            var a = Vertex(engine);
            var b = Vertex(engine);
            using var client = factory.CreateClient();

            using (var created = await client.PostAsync("/index",
                Json("{\"uniqueId\":\"dict\",\"pluginType\":\"DictionaryIndex\"}")))
            {
                Assert.AreEqual(HttpStatusCode.OK, created.StatusCode);
                Assert.AreEqual("true", await created.Content.ReadAsStringAsync());
            }

            foreach (var (elementId, key) in new[] { (a, "alpha"), (b, "beta") })
            {
                using var add = await client.PutAsync("/index/dict", Json(
                    "{\"graphElementId\":" + elementId + ",\"key\":{\"propertyId\":\"key\",\"propertyValue\":\"" +
                    key + "\",\"fullQualifiedTypeName\":\"System.String\"}}"));
                Assert.AreEqual(HttpStatusCode.OK, add.StatusCode);
                Assert.AreEqual("true", await add.Content.ReadAsStringAsync());
            }

            using (var response = await client.DeleteAsync("/index/dict/" + a))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("true", await response.Content.ReadAsStringAsync(),
                    "the guard only bites a bound VECTOR index");
            }

            using (var response = await DeleteWithBody(client, "/index/dict/propertyValue",
                "{\"propertyId\":\"key\",\"propertyValue\":\"beta\",\"fullQualifiedTypeName\":\"System.String\"}"))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("true", await response.Content.ReadAsStringAsync(),
                    "key removal on a non-vector index is untouched");
            }

            Assert.IsTrue(engine.IndexFactory.TryGetIndex(out var dictionaryIndex, "dict"));
            Assert.AreEqual(0, dictionaryIndex.CountOfValues(), "both removals actually took effect");
        }
    }
}
