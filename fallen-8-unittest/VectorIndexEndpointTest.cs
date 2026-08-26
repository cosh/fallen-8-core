// MIT License
//
// VectorIndexEndpointTest.cs
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
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Pipeline tests for the vector index REST surface (feature vector-index): index creation
    /// through POST /index, the typed add endpoint (explicit + property modes, each 400 reason),
    /// and the kNN scan endpoint with its response shape.
    /// </summary>
    /// <remarks>
    ///   The removal half (audit defect B14) lives here too: the generic index-content REMOVE routes
    ///   used to write straight through to a BOUND vector index, dropping a slot while the element
    ///   kept its embedding, so kNN silently lost a live element. Both removals now share the add
    ///   path's refusal; the contract itself is documented once, on GraphController's bound-index
    ///   refusal helper.
    /// </remarks>
    [TestClass]
    public class VectorIndexEndpointTest
    {
        private static StringContent Json(string body)
        {
            return new StringContent(body, Encoding.UTF8, "application/json");
        }

        private static Fallen8 EngineOf(VolatileAppFactory factory)
        {
            return factory.Services.GetRequiredService<NoSQL.GraphDB.App.Namespaces.Fallen8Namespaces>().Default.Engine;
        }

        private static async Task CreateVectorIndex(HttpClient client, string name = "emb", int dimension = 3, string metric = "Cosine")
        {
            var body = "{\"uniqueId\":\"" + name + "\",\"pluginType\":\"VectorIndex\",\"pluginOptions\":{" +
                       "\"dimension\":{\"propertyValue\":\"" + dimension + "\",\"fullQualifiedTypeName\":\"System.Int32\"}," +
                       "\"metric\":{\"propertyValue\":\"" + metric + "\",\"fullQualifiedTypeName\":\"System.String\"}}}";
            using var response = await client.PostAsync("/index", Json(body));
            response.EnsureSuccessStatusCode();
            Assert.AreEqual("true", await response.Content.ReadAsStringAsync(),
                "index creation through the EXISTING surface must succeed");
        }

        private static int SeedVertex(VolatileAppFactory factory, string label = "person", Dictionary<string, object> properties = null)
        {
            var tx = new CreateVertexTransaction
            {
                Definition = new VertexDefinition { CreationDate = 1u, Label = label, Properties = properties }
            };
            EngineOf(factory).EnqueueTransaction(tx).WaitUntilFinished();
            return tx.VertexCreated.Id;
        }

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
        public async Task AddAndScan_HappyPath_ReturnsBestFirstWithMetricSemantics()
        {
            using var factory = new VolatileAppFactory();
            using var client = factory.CreateClient();
            await CreateVectorIndex(client);

            var a = SeedVertex(factory);
            var b = SeedVertex(factory);

            (await client.PutAsync("/index/vector/emb",
                Json("{\"graphElementId\":" + a + ",\"vector\":[1,0,0]}"))).EnsureSuccessStatusCode();
            (await client.PutAsync("/index/vector/emb",
                Json("{\"graphElementId\":" + b + ",\"vector\":[0,1,0]}"))).EnsureSuccessStatusCode();

            using var scan = await client.PostAsync("/scan/index/vector",
                Json("{\"indexId\":\"emb\",\"query\":[1,0,0],\"k\":2}"));
            Assert.AreEqual(HttpStatusCode.OK, scan.StatusCode);

            var result = JsonDocument.Parse(await scan.Content.ReadAsStringAsync()).RootElement;
            Assert.AreEqual("Cosine", result.GetProperty("metric").GetString());
            Assert.IsTrue(result.GetProperty("higherIsBetter").GetBoolean());

            var hits = result.GetProperty("results");
            Assert.AreEqual(2, hits.GetArrayLength());
            Assert.AreEqual(a, hits[0].GetProperty("graphElementId").GetInt32());
            Assert.AreEqual(1f, hits[0].GetProperty("score").GetSingle(), 1e-6f);
            Assert.AreEqual(b, hits[1].GetProperty("graphElementId").GetInt32());
        }

        [TestMethod]
        public async Task PropertyMode_ReadsTheElementsFloatArrayProperty()
        {
            using var factory = new VolatileAppFactory();
            using var client = factory.CreateClient();
            await CreateVectorIndex(client, dimension: 2, metric: "L2");

            var withEmbedding = SeedVertex(factory, properties: new Dictionary<string, object>
            {
                { "embedding", new[] { 3f, 4f } }
            });

            using var add = await client.PutAsync("/index/vector/emb",
                Json("{\"graphElementId\":" + withEmbedding + ",\"propertyId\":\"embedding\"}"));
            Assert.AreEqual(HttpStatusCode.OK, add.StatusCode, await add.Content.ReadAsStringAsync());

            using var scan = await client.PostAsync("/scan/index/vector",
                Json("{\"indexId\":\"emb\",\"query\":[0,0],\"k\":1}"));
            var result = JsonDocument.Parse(await scan.Content.ReadAsStringAsync()).RootElement;
            Assert.AreEqual(5f, result.GetProperty("results")[0].GetProperty("score").GetSingle(), 1e-6f,
                "|(3,4)| = 5 - the vector came from the property");
        }

        [TestMethod]
        public async Task Add_EveryDocumented400Reason_AndThe404s()
        {
            using var factory = new VolatileAppFactory();
            using var client = factory.CreateClient();
            await CreateVectorIndex(client, dimension: 3);
            var v = SeedVertex(factory);

            // Unknown index -> 404; non-vector index -> 400.
            using (var r = await client.PutAsync("/index/vector/nope", Json("{\"graphElementId\":" + v + ",\"vector\":[1,0,0]}")))
            {
                Assert.AreEqual(HttpStatusCode.NotFound, r.StatusCode);
            }
            using (var create = await client.PostAsync("/index", Json(
                "{\"uniqueId\":\"dict\",\"pluginType\":\"DictionaryIndex\",\"pluginOptions\":{}}")))
            {
                create.EnsureSuccessStatusCode();
            }
            using (var r = await client.PutAsync("/index/vector/dict", Json("{\"graphElementId\":" + v + ",\"vector\":[1,0,0]}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, r.StatusCode);
            }

            // Unknown element -> 404.
            using (var r = await client.PutAsync("/index/vector/emb", Json("{\"graphElementId\":4242,\"vector\":[1,0,0]}")))
            {
                Assert.AreEqual(HttpStatusCode.NotFound, r.StatusCode);
            }

            // Neither/both modes -> 400.
            using (var r = await client.PutAsync("/index/vector/emb", Json("{\"graphElementId\":" + v + "}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, r.StatusCode);
            }
            using (var r = await client.PutAsync("/index/vector/emb",
                Json("{\"graphElementId\":" + v + ",\"vector\":[1,0,0],\"propertyId\":\"x\"}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, r.StatusCode);
            }

            // Wrong dimension, zero-norm under cosine, missing / non-float[] property -> 400.
            using (var r = await client.PutAsync("/index/vector/emb", Json("{\"graphElementId\":" + v + ",\"vector\":[1,0]}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, r.StatusCode);
            }
            using (var r = await client.PutAsync("/index/vector/emb", Json("{\"graphElementId\":" + v + ",\"vector\":[0,0,0]}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, r.StatusCode);
            }
            using (var r = await client.PutAsync("/index/vector/emb", Json("{\"graphElementId\":" + v + ",\"propertyId\":\"missing\"}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, r.StatusCode);
            }
            var withStringProp = SeedVertex(factory, properties: new Dictionary<string, object> { { "embedding", "not a vector" } });
            using (var r = await client.PutAsync("/index/vector/emb",
                Json("{\"graphElementId\":" + withStringProp + ",\"propertyId\":\"embedding\"}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, r.StatusCode, "property exists but is not a float[]");
            }

            // 3.5e39 overflows Single to Infinity during JSON deserialization - a non-finite
            // component must be a 400, never a stored ranking poison.
            using (var r = await client.PutAsync("/index/vector/emb",
                Json("{\"graphElementId\":" + v + ",\"vector\":[3.5e39,0,0]}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, r.StatusCode, "Infinity component");
            }
        }

        [TestMethod]
        public async Task Scan_EveryDocumented400Reason_AndThe404()
        {
            using var factory = new VolatileAppFactory();
            using var client = factory.CreateClient();
            await CreateVectorIndex(client, dimension: 3);

            using (var r = await client.PostAsync("/scan/index/vector", Json("{\"indexId\":\"nope\",\"query\":[1,0,0],\"k\":1}")))
            {
                Assert.AreEqual(HttpStatusCode.NotFound, r.StatusCode);
            }
            using (var r = await client.PostAsync("/scan/index/vector", Json("{\"indexId\":\"emb\",\"query\":[1,0],\"k\":1}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, r.StatusCode, "wrong query dimension");
            }
            using (var r = await client.PostAsync("/scan/index/vector", Json("{\"indexId\":\"emb\",\"query\":[1,0,0],\"k\":0}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, r.StatusCode, "k below 1");
            }
            using (var r = await client.PostAsync("/scan/index/vector", Json("{\"indexId\":\"emb\",\"query\":[1,0,0],\"k\":1025}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, r.StatusCode, "k above MaxK");
            }
            using (var r = await client.PostAsync("/scan/index/vector", Json("{\"indexId\":\"emb\",\"query\":[0,0,0],\"k\":1}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, r.StatusCode, "zero-norm under cosine");
            }
            using (var r = await client.PostAsync("/scan/index/vector", Json("{\"indexId\":\"emb\",\"query\":[3.5e39,0,0],\"k\":1}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, r.StatusCode, "non-finite query component");
            }
            using (var r = await client.PostAsync("/scan/index/vector", Json("{\"indexId\":\"emb\",\"query\":[1,0,0],\"k\":1,\"kind\":\"hyperedge\"}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, r.StatusCode, "unknown kind");
            }
        }

        [TestMethod]
        public async Task Scan_KindAndLabelConstraints_WorkOverRest()
        {
            using var factory = new VolatileAppFactory();
            using var client = factory.CreateClient();
            await CreateVectorIndex(client, dimension: 2, metric: "L2");

            var person = SeedVertex(factory, "person");
            var robot = SeedVertex(factory, "robot");
            (await client.PutAsync("/index/vector/emb", Json("{\"graphElementId\":" + person + ",\"vector\":[1,0]}"))).EnsureSuccessStatusCode();
            (await client.PutAsync("/index/vector/emb", Json("{\"graphElementId\":" + robot + ",\"vector\":[0,0]}"))).EnsureSuccessStatusCode();

            using var scan = await client.PostAsync("/scan/index/vector",
                Json("{\"indexId\":\"emb\",\"query\":[0,0],\"k\":10,\"kind\":\"vertex\",\"label\":\"person\"}"));
            var result = JsonDocument.Parse(await scan.Content.ReadAsStringAsync()).RootElement;

            Assert.AreEqual(1, result.GetProperty("results").GetArrayLength());
            Assert.AreEqual(person, result.GetProperty("results")[0].GetProperty("graphElementId").GetInt32());
        }

        #region removal routes and the bound-index refusal (audit B14)

        [TestMethod]
        public async Task ElementRemoval_OnABoundIndex_Is400_AndTheProjectionSurvives()
        {
            using var factory = new VolatileAppFactory();
            var engine = EngineOf(factory);
            var a = SeedVertex(factory, "p");
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
            using var factory = new VolatileAppFactory();
            var engine = EngineOf(factory);
            var a = SeedVertex(factory, "p");
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
            using var factory = new VolatileAppFactory();
            var engine = EngineOf(factory);
            var a = SeedVertex(factory, "p");
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
            using var factory = new VolatileAppFactory();
            var engine = EngineOf(factory);
            var a = SeedVertex(factory, "p");
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
            using var factory = new VolatileAppFactory();
            var engine = EngineOf(factory);
            var a = SeedVertex(factory, "p");
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
            using var factory = new VolatileAppFactory();
            var engine = EngineOf(factory);
            var a = SeedVertex(factory, "p");
            var b = SeedVertex(factory, "p");
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

        #endregion
    }
}
