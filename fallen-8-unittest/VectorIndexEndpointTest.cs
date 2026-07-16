// MIT License
//
// VectorIndexEndpointTest.cs
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
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Pipeline tests for the vector index REST surface (feature vector-index): index creation
    /// through POST /index, the typed add endpoint (explicit + property modes, each 400 reason),
    /// and the kNN scan endpoint with its response shape.
    /// </summary>
    [TestClass]
    public class VectorIndexEndpointTest
    {
        private sealed class VectorFactory : WebApplicationFactory<Program>
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseSetting("Fallen8:Durability:Volatile", "true");
            }
        }

        private static StringContent Json(string body)
        {
            return new StringContent(body, Encoding.UTF8, "application/json");
        }

        private static Fallen8 EngineOf(VectorFactory factory)
        {
            return (Fallen8)factory.Services.GetRequiredService<IFallen8>();
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

        private static int SeedVertex(VectorFactory factory, string label = "person", Dictionary<string, object> properties = null)
        {
            var tx = new CreateVertexTransaction
            {
                Definition = new VertexDefinition { CreationDate = 1u, Label = label, Properties = properties }
            };
            EngineOf(factory).EnqueueTransaction(tx).WaitUntilFinished();
            return tx.VertexCreated.Id;
        }

        [TestMethod]
        public async Task AddAndScan_HappyPath_ReturnsBestFirstWithMetricSemantics()
        {
            using var factory = new VectorFactory();
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
            using var factory = new VectorFactory();
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
            using var factory = new VectorFactory();
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
        }

        [TestMethod]
        public async Task Scan_EveryDocumented400Reason_AndThe404()
        {
            using var factory = new VectorFactory();
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
            using (var r = await client.PostAsync("/scan/index/vector", Json("{\"indexId\":\"emb\",\"query\":[1,0,0],\"k\":1,\"kind\":\"hyperedge\"}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, r.StatusCode, "unknown kind");
            }
        }

        [TestMethod]
        public async Task Scan_KindAndLabelConstraints_WorkOverRest()
        {
            using var factory = new VectorFactory();
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
    }
}
