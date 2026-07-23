// MIT License
//
// ElementEmbeddingEndpointTest.cs
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
    ///   Feature element-embeddings, Phase 4: the typed embedding REST surface
    ///   (PUT/GET/DELETE /graphelement/{id}/embedding/{name}) and the bound-index guards on
    ///   the write path and on the raw vector-index add endpoint.
    /// </summary>
    [TestClass]
    public class ElementEmbeddingEndpointTest
    {
        private sealed class EmbeddingFactory : WebApplicationFactory<Program>
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

        [TestMethod]
        public async Task PutGetDelete_RoundTrip()
        {
            using var factory = new EmbeddingFactory();
            var engine = EngineOf(factory);
            var a = Vertex(engine);
            using var client = factory.CreateClient();

            using var put = await client.PutAsync($"/graphelement/{a}/embedding/default?waitForCompletion=true",
                Json("{ \"vector\": [0.5, -1.5] }"));
            Assert.AreEqual(HttpStatusCode.Accepted, put.StatusCode, await put.Content.ReadAsStringAsync());

            using var get = await client.GetAsync($"/graphelement/{a}/embedding/default");
            Assert.AreEqual(HttpStatusCode.OK, get.StatusCode);
            var body = JsonDocument.Parse(await get.Content.ReadAsStringAsync()).RootElement;
            Assert.AreEqual("default", body.GetProperty("name").GetString());
            Assert.AreEqual(2, body.GetProperty("vector").GetArrayLength());
            Assert.AreEqual(0.5f, body.GetProperty("vector")[0].GetSingle());
            Assert.AreEqual(JsonValueKind.Null, body.GetProperty("model").ValueKind,
                "a bring-your-own-vector embedding carries no model stamp");

            using var delete = await client.DeleteAsync($"/graphelement/{a}/embedding/default?waitForCompletion=true");
            Assert.AreEqual(HttpStatusCode.Accepted, delete.StatusCode);

            using var gone = await client.GetAsync($"/graphelement/{a}/embedding/default");
            Assert.AreEqual(HttpStatusCode.NotFound, gone.StatusCode);
        }

        [TestMethod]
        public async Task Put_Replaces_AndProjectsIntoBoundIndex()
        {
            using var factory = new EmbeddingFactory();
            var engine = EngineOf(factory);
            var a = Vertex(engine);
            Assert.IsTrue(engine.IndexFactory.TryCreateIndex(out var index, "emb", "VectorIndex",
                new Dictionary<string, object> { { "dimension", 2 }, { "embeddingName", "default" } }));
            using var client = factory.CreateClient();

            using var put = await client.PutAsync($"/graphelement/{a}/embedding/default?waitForCompletion=true",
                Json("{ \"vector\": [1, 0] }"));
            Assert.AreEqual(HttpStatusCode.Accepted, put.StatusCode);
            Assert.AreEqual(1, index.CountOfValues(), "the committed write projected into the bound index");

            using var replace = await client.PutAsync($"/graphelement/{a}/embedding/default?waitForCompletion=true",
                Json("{ \"vector\": [0, 1] }"));
            Assert.AreEqual(HttpStatusCode.Accepted, replace.StatusCode);
            Assert.AreEqual(1, index.CountOfValues(), "replace, not duplicate");
        }

        [TestMethod]
        public async Task Put_400Table_And404()
        {
            using var factory = new EmbeddingFactory();
            var engine = EngineOf(factory);
            var a = Vertex(engine);
            Assert.IsTrue(engine.IndexFactory.TryCreateIndex(out _, "emb", "VectorIndex",
                new Dictionary<string, object> { { "dimension", 2 }, { "embeddingName", "default" } }));
            using var client = factory.CreateClient();

            foreach (var (url, body, reason) in new (string, string, string)[]
            {
                ($"/graphelement/{a}/embedding/bad name", "{ \"vector\": [1, 0] }", "invalid name"),
                ($"/graphelement/{a}/embedding/default", "{ \"vector\": [] }", "empty vector"),
                ($"/graphelement/{a}/embedding/default", "{ \"vector\": [1, 0, 0] }", "bound-index dimension conflict"),
                ($"/graphelement/{a}/embedding/default", "{ \"vector\": [0, 0] }", "zero-norm with a bound Cosine index"),
            })
            {
                using var response = await client.PutAsync(url, Json(body));
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, reason);
            }

            // Non-finite components are rejected by System.Text.Json already (no NaN literal in
            // strict JSON) - the model-level guard is pinned by ElementEmbeddingTest.

            using var missing = await client.PutAsync("/graphelement/424242/embedding/default", Json("{ \"vector\": [1, 0] }"));
            Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);

            using var missingGet = await client.GetAsync("/graphelement/424242/embedding/default");
            Assert.AreEqual(HttpStatusCode.NotFound, missingGet.StatusCode);

            using var missingDelete = await client.DeleteAsync("/graphelement/424242/embedding/default");
            Assert.AreEqual(HttpStatusCode.NotFound, missingDelete.StatusCode);
        }

        [TestMethod]
        public async Task ExplicitAdd_OnABoundIndex_Is400()
        {
            using var factory = new EmbeddingFactory();
            var engine = EngineOf(factory);
            var a = Vertex(engine);
            Assert.IsTrue(engine.IndexFactory.TryCreateIndex(out _, "emb", "VectorIndex",
                new Dictionary<string, object> { { "dimension", 2 }, { "embeddingName", "default" } }));
            using var client = factory.CreateClient();

            using var response = await client.PutAsync("/index/vector/emb",
                Json($"{{ \"graphElementId\": {a}, \"vector\": [1, 0] }}"));
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode,
                "a bound index maintains itself; explicit adds would be a second membership authority");
        }

        [TestMethod]
        public async Task Get_SurfacesTheModelStamp_WhenPresent()
        {
            using var factory = new EmbeddingFactory();
            var engine = EngineOf(factory);
            var a = Vertex(engine);
            engine.EnqueueTransaction(new SetEmbeddingsTransaction().SetEmbedding(a, "default", new[] { 1f, 0f }))
                .WaitUntilFinished();
            engine.EnqueueTransaction(new AddPropertyTransaction
            {
                Definition = new PropertyAddDefinition
                {
                    GraphElementId = a,
                    PropertyId = "$embeddingModel:default",
                    Property = "bge-micro-v2#2#Cosine"
                }
            }).WaitUntilFinished();
            using var client = factory.CreateClient();

            using var get = await client.GetAsync($"/graphelement/{a}/embedding/default");
            Assert.AreEqual(HttpStatusCode.OK, get.StatusCode);
            var body = JsonDocument.Parse(await get.Content.ReadAsStringAsync()).RootElement;
            Assert.AreEqual("bge-micro-v2#2#Cosine", body.GetProperty("model").GetString());
        }
    }
}
