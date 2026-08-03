// MIT License
//
// IngestionBindingTest.cs
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
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Feature semantic-layer FR-7: the semantic layer never creates indices implicitly.
    ///   Ingestion answers 428 until the operator binds the required indices explicitly
    ///   (POST /document/binding/ensure); GET /document/binding reports the state the Studio
    ///   "State" panel drives.
    /// </summary>
    [TestClass]
    public class IngestionBindingTest
    {
        private static async Task<JsonRoot> GetBinding(HttpClient client)
        {
            using var response = await client.GetAsync("/document/binding");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            return new JsonRoot(await IngestionTestHelper.ReadJson(response));
        }

        private readonly struct JsonRoot
        {
            private readonly System.Text.Json.JsonElement _root;
            public JsonRoot(System.Text.Json.JsonElement root) => _root = root;
            public Boolean Ready => _root.GetProperty("ready").GetBoolean();
            public Boolean RoleReady(String role) => _root.GetProperty(role).GetProperty("ready").GetBoolean();
            public Boolean RoleRequired(String role) => _root.GetProperty(role).GetProperty("required").GetBoolean();
            public Boolean RoleExists(String role) => _root.GetProperty(role).GetProperty("exists").GetBoolean();
            public String RoleDetail(String role) => _root.GetProperty(role).GetProperty("detail").GetString();
        }

        [TestMethod]
        public async Task Ingest_428_UntilBound_ThenSucceeds()
        {
            using var factory = new IngestionFactory();  // embeddings on, NLP off
            using var client = factory.CreateClient();
            var engine = IngestionTestHelper.EngineOf(factory);

            // Nothing is bound yet: the layer is not ready and ingestion is refused with 428,
            // creating no stub.
            var before = await GetBinding(client);
            Assert.IsFalse(before.Ready, "an unbound layer is not ready");
            Assert.IsTrue(before.RoleRequired("vector"), "embeddings on: the vector index is required");
            Assert.IsFalse(before.RoleExists("vector"));

            using var refused = await client.PostAsync("/document/text",
                IngestionTestHelper.Json("{ \"name\": \"n\", \"text\": \"# H\\n\\nbody\" }"));
            Assert.AreEqual((HttpStatusCode)428, refused.StatusCode, "ingestion is gated until bound");
            Assert.AreEqual(0, engine.GetAllVertices("Document").Count, "the 428 is pre-stub");

            // Bind explicitly: the required indices are created and the layer becomes ready.
            using var ensure = await client.PostAsync("/document/binding/ensure", null);
            Assert.AreEqual(HttpStatusCode.OK, ensure.StatusCode);
            var bound = new JsonRoot(await IngestionTestHelper.ReadJson(ensure));
            Assert.IsTrue(bound.Ready, "the layer is ready after an explicit ensure");
            Assert.IsTrue(bound.RoleReady("vector"));
            Assert.IsTrue(bound.RoleReady("fulltext"));
            Assert.IsTrue(engine.IndexFactory.TryGetIndex(out _, "documents"), "the vector index was created");
            Assert.IsTrue(engine.IndexFactory.TryGetIndex(out _, "documents-text"), "the fulltext index was created");

            // Now the same ingest is accepted and indexes.
            var summary = await IngestionTestHelper.IngestText(client, "n", "# H\n\nbody");
            Assert.AreEqual("indexed", summary.GetProperty("status").GetString());
        }

        [TestMethod]
        public async Task EnsureIsIdempotent()
        {
            using var factory = new IngestionFactory();
            using var client = factory.CreateClient();
            var engine = IngestionTestHelper.EngineOf(factory);

            using (var first = await client.PostAsync("/document/binding/ensure", null))
            {
                Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
            }

            using (var second = await client.PostAsync("/document/binding/ensure", null))
            {
                Assert.AreEqual(HttpStatusCode.OK, second.StatusCode, "a second ensure is a no-op, not a conflict");
            }

            Assert.IsTrue(engine.IndexFactory.TryGetIndex(out _, "documents"));
            Assert.IsTrue(engine.IndexFactory.TryGetIndex(out _, "documents-text"));
        }

        [TestMethod]
        public async Task WithNlpOn_TheEntityIndexIsRequired()
        {
            using var factory = new IngestionFactory(new Dictionary<String, String>
            {
                { "Fallen8:Nlp:Enabled", "true" }
            });
            using var client = factory.CreateClient();

            var before = await GetBinding(client);
            Assert.IsTrue(before.RoleRequired("entity"), "NLP on: the entity dedup index is required");
            Assert.IsFalse(before.Ready);

            using var ensure = await client.PostAsync("/document/binding/ensure", null);
            var bound = new JsonRoot(await IngestionTestHelper.ReadJson(ensure));
            Assert.IsTrue(bound.RoleReady("entity"), "ensure created the entity index");
            Assert.IsTrue(bound.Ready);
        }

        [TestMethod]
        public async Task ShapeConflict_ReportsAndRefuses()
        {
            using var factory = new IngestionFactory();
            using var client = factory.CreateClient();
            var engine = IngestionTestHelper.EngineOf(factory);

            // An operator created an index with the fulltext role's id but the WRONG shape.
            Assert.IsTrue(engine.IndexFactory.TryCreateIndex(out _, "documents-text", "DictionaryIndex"));

            var state = await GetBinding(client);
            Assert.IsTrue(state.RoleExists("fulltext"));
            Assert.IsFalse(state.RoleReady("fulltext"), "a wrong-shape index is not ready");
            StringAssert.Contains(state.RoleDetail("fulltext"), "not a fulltext index");

            using var ensure = await client.PostAsync("/document/binding/ensure", null);
            Assert.AreEqual(HttpStatusCode.Conflict, ensure.StatusCode, "ensure will not clobber a wrong-shape index");
        }

        [TestMethod]
        public async Task VectorShapeConflict_ReadAndEnforceViews_Agree()
        {
            // CA-12: the read view (GET /document/binding role detail) and the enforce view (the
            // ensure 409 body) derive from ONE shape decision (VectorShapeConflict), so a
            // wrong-shape vector index is reported and refused with the same reason - they cannot
            // drift into "reports ready, but bind rejects".
            using var factory = new IngestionFactory();
            using var client = factory.CreateClient();
            var engine = IngestionTestHelper.EngineOf(factory);

            // An operator created an index with the vector role's id but the WRONG shape.
            Assert.IsTrue(engine.IndexFactory.TryCreateIndex(out _, "documents", "DictionaryIndex"));

            var state = await GetBinding(client);
            Assert.IsTrue(state.RoleExists("vector"));
            Assert.IsFalse(state.RoleReady("vector"), "a wrong-shape index is not ready");
            StringAssert.Contains(state.RoleDetail("vector"), "not a vector index");

            using var ensure = await client.PostAsync("/document/binding/ensure", null);
            Assert.AreEqual(HttpStatusCode.Conflict, ensure.StatusCode, "ensure refuses a wrong-shape index");
            StringAssert.Contains(await ensure.Content.ReadAsStringAsync(), "not a vector index");
        }
    }
}
