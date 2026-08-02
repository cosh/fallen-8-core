// MIT License
//
// DocumentSearchEndpointTest.cs
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
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Ingestion;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Feature unstructured-ingestion, read path (FR-11/FR-12): fused retrieval where an
    ///   exact-identifier query provably wins via the lexical side, honest degrade paths with
    ///   <c>modeUsed</c>, sibling windows, per-document grouping, the validation matrix and
    ///   the model-identity 409.
    /// </summary>
    [TestClass]
    public class DocumentSearchEndpointTest
    {
        private static async Task<JsonElement> Search(System.Net.Http.HttpClient client, String body,
            HttpStatusCode expected = HttpStatusCode.OK)
        {
            using var response = await client.PostAsync("/document/search", IngestionTestHelper.Json(body));
            var payload = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(expected, response.StatusCode, payload);
            return JsonDocument.Parse(payload).RootElement;
        }

        [TestMethod]
        public async Task Fused_ExactIdentifierQuery_RanksItsChunkFirst()
        {
            using var factory = new IngestionFactory();
            using var client = factory.CreateClient();

            // Three documents; exactly one chunk carries the identifier. Whatever the dense
            // ranking does, only that chunk collects BOTH RRF contributions, so it is
            // deterministically first - the reason fused retrieval is the default.
            await IngestionTestHelper.IngestText(client, "a.md",
                "# Limits\n\nThe PORT_X9_LIMIT knob bounds the listener.");
            await IngestionTestHelper.IngestText(client, "b.md",
                "# Prose\n\nEntirely unrelated words about gardens.");
            await IngestionTestHelper.IngestText(client, "c.md",
                "# More\n\nOther text mentioning listeners without the knob.");

            var result = await Search(client, "{ \"queryText\": \"PORT_X9_LIMIT\", \"k\": 3 }");

            Assert.AreEqual("fused", result.GetProperty("modeUsed").GetString());
            var hits = result.GetProperty("hits");
            Assert.IsTrue(hits.GetArrayLength() >= 1);
            StringAssert.Contains(hits[0].GetProperty("text").GetString(), "PORT_X9_LIMIT",
                "the identifier chunk wins fused ranking");
            Assert.AreEqual("PORT_X9_LIMIT", hits[0].GetProperty("identifiers")[0].GetString());
            Assert.IsTrue(hits[0].TryGetProperty("documentId", out var documentId), "a hit carries its parent");
            Assert.IsTrue(documentId.GetInt32() >= 0);
        }

        [TestMethod]
        public async Task ModeDense_And_ModeLexical_AreExplicit()
        {
            using var factory = new IngestionFactory();
            using var client = factory.CreateClient();
            await IngestionTestHelper.IngestText(client, "a.md", "# One\n\nAlpha content here.");
            await IngestionTestHelper.IngestText(client, "b.md", "# Two\n\nBeta content there.");

            var dense = await Search(client, "{ \"queryText\": \"alpha\", \"mode\": \"dense\", \"k\": 2 }");
            Assert.AreEqual("dense", dense.GetProperty("modeUsed").GetString());
            Assert.AreEqual(2, dense.GetProperty("hits").GetArrayLength());

            var lexical = await Search(client, "{ \"queryText\": \"Alpha\", \"mode\": \"lexical\", \"k\": 5 }");
            Assert.AreEqual("lexical", lexical.GetProperty("modeUsed").GetString());
            Assert.AreEqual(1, lexical.GetProperty("hits").GetArrayLength(), "only the matching chunk");
        }

        [TestMethod]
        public async Task Fused_DegradesToLexical_WhenNoDenseSideExists()
        {
            using var factory = new IngestionFactory(new Dictionary<String, String>
            {
                { "Fallen8:Embedding:Enabled", "false" }
            });
            using var client = factory.CreateClient();
            await IngestionTestHelper.IngestText(client, "a.md", "# One\n\nAlpha content here.", "\"embed\": false");

            var result = await Search(client, "{ \"queryText\": \"alpha\" }");
            Assert.AreEqual("lexical", result.GetProperty("modeUsed").GetString(),
                "provider off and no vector index: the fused request degrades and says so");
            Assert.AreEqual(1, result.GetProperty("hits").GetArrayLength());
        }

        [TestMethod]
        public async Task Fused_DegradesToDense_WhenTheLexicalSideIsDisabled()
        {
            using var factory = new IngestionFactory(new Dictionary<String, String>
            {
                { "Fallen8:Ingestion:EnsureFulltextIndex", "false" }
            });
            using var client = factory.CreateClient();
            await IngestionTestHelper.IngestText(client, "a.md", "# One\n\nAlpha content here.");

            var result = await Search(client, "{ \"queryText\": \"alpha\" }");
            Assert.AreEqual("dense", result.GetProperty("modeUsed").GetString());
            Assert.AreEqual(1, result.GetProperty("hits").GetArrayLength());
        }

        [TestMethod]
        public async Task Window_ReturnsSiblings_InDocumentOrder_HitExcluded()
        {
            using var factory = new IngestionFactory();
            using var client = factory.CreateClient();
            await IngestionTestHelper.IngestText(client, "a.md",
                "# First\n\nOpening section prose.\n\n# Middle\n\nThe MIDDLE_TOKEN_X1 lives here.\n\n# Last\n\nClosing section prose.");

            var result = await Search(client,
                "{ \"queryText\": \"MIDDLE_TOKEN_X1\", \"k\": 1, \"window\": 1 }");

            var hit = result.GetProperty("hits")[0];
            Assert.AreEqual(1, hit.GetProperty("order").GetInt32(), "the middle chunk is the hit");
            var window = hit.GetProperty("window");
            Assert.AreEqual(2, window.GetArrayLength());
            Assert.AreEqual(0, window[0].GetProperty("order").GetInt32());
            Assert.AreEqual(2, window[1].GetProperty("order").GetInt32());
            StringAssert.Contains(window[0].GetProperty("text").GetString(), "Opening");
            StringAssert.Contains(window[1].GetProperty("text").GetString(), "Closing");
        }

        [TestMethod]
        public async Task GroupByDocument_OrdersChunksByPosition_CarriesTheSummary()
        {
            using var factory = new IngestionFactory();
            using var client = factory.CreateClient();
            await IngestionTestHelper.IngestText(client, "a.md",
                "# S1\n\nSHARED_TOKEN_Z9 alpha.\n\n# S2\n\nQuiet filler prose.\n\n# S3\n\nSHARED_TOKEN_Z9 gamma.");
            await IngestionTestHelper.IngestText(client, "b.md",
                "# T1\n\nSHARED_TOKEN_Z9 beta.");

            var result = await Search(client,
                "{ \"queryText\": \"SHARED_TOKEN_Z9\", \"k\": 10, \"groupByDocument\": true }");

            var documents = result.GetProperty("documents");
            Assert.AreEqual(2, documents.GetArrayLength());
            foreach (var group in documents.EnumerateArray())
            {
                Assert.IsTrue(group.GetProperty("document").GetProperty("name").GetString().EndsWith(".md"));
                var previousOrder = -1;
                foreach (var chunk in group.GetProperty("chunks").EnumerateArray())
                {
                    var order = chunk.GetProperty("order").GetInt32();
                    Assert.IsTrue(order > previousOrder, "chunks are in document position order");
                    previousOrder = order;
                }
            }
        }

        [TestMethod]
        public async Task QueryVector_DrivesTheDenseSide_WithoutTheProvider()
        {
            using var factory = new IngestionFactory();
            using var client = factory.CreateClient();
            await IngestionTestHelper.IngestText(client, "a.md", "# One\n\nAlpha content here.");

            var result = await Search(client,
                "{ \"queryVector\": [0.1, 0.2, 0.3, 0.4], \"mode\": \"dense\", \"k\": 1 }");
            Assert.AreEqual("dense", result.GetProperty("modeUsed").GetString());
            Assert.AreEqual(1, result.GetProperty("hits").GetArrayLength());
        }

        [TestMethod]
        public async Task ValidationMatrix_400s()
        {
            using var factory = new IngestionFactory();
            using var client = factory.CreateClient();
            await IngestionTestHelper.IngestText(client, "a.md", "# One\n\nAlpha content here.");

            await Search(client, "{ \"queryText\": \"x\", \"k\": 0 }", HttpStatusCode.BadRequest);
            await Search(client, "{ \"queryText\": \"x\", \"k\": 101 }", HttpStatusCode.BadRequest);
            await Search(client, "{ \"queryText\": \"x\", \"window\": 6 }", HttpStatusCode.BadRequest);
            await Search(client, "{ \"queryText\": \"x\", \"mode\": \"psychic\" }", HttpStatusCode.BadRequest);
            await Search(client, "{ }", HttpStatusCode.BadRequest);
            await Search(client, "{ \"queryVector\": [0.1], \"mode\": \"lexical\" }", HttpStatusCode.BadRequest);
            await Search(client, "{ \"queryVector\": [0.1, 0.2], \"mode\": \"dense\" }", HttpStatusCode.BadRequest);
        }

        [TestMethod]
        public async Task StaleIndexModel_Answers409_OnSearchAndIngest()
        {
            using var factory = new IngestionFactory();
            using var client = factory.CreateClient();
            var engine = IngestionTestHelper.EngineOf(factory);

            // An operator-created bound index declaring ANOTHER model identity: the existing
            // provider consistency contract must refuse both the dense query and the ingest.
            Assert.IsTrue(engine.IndexFactory.TryCreateIndex(out _, "documents", "VectorIndex",
                new Dictionary<String, Object>
                {
                    { "dimension", IngestionFactory.Dim },
                    { "embeddingName", "default" },
                    { "model", "other-model#4#Cosine" }
                }));

            await Search(client, "{ \"queryText\": \"anything\", \"mode\": \"dense\" }", HttpStatusCode.Conflict);

            using var ingest = await client.PostAsync("/document/text",
                IngestionTestHelper.Json("{ \"name\": \"n\", \"text\": \"content\" }"));
            Assert.AreEqual(HttpStatusCode.Conflict, ingest.StatusCode, "the 409 is pre-stub");
            Assert.AreEqual(0, engine.GetAllVertices("Document").Count);
        }

        [TestMethod]
        public void LexicalPattern_EscapesUserText()
        {
            Assert.AreEqual("alpha|beta", DocumentSearchService.BuildLexicalPattern("alpha beta"));
            Assert.AreEqual(@"a\.\*b", DocumentSearchService.BuildLexicalPattern("a.*b"),
                "regex metacharacters are escaped, never interpreted");
            Assert.IsNull(DocumentSearchService.BuildLexicalPattern("   "));
        }

        [TestMethod]
        public void Rrf_IsDeterministic_TiesByElementId()
        {
            var a = new VertexModelStub(1);
            var b = new VertexModelStub(2);
            var c = new VertexModelStub(3);

            var dense = new List<KeyValuePair<NoSQL.GraphDB.Core.Model.VertexModel, Single>>
            {
                new(a.Vertex, 0.9f), new(b.Vertex, 0.8f)
            };
            var lexical = new List<KeyValuePair<NoSQL.GraphDB.Core.Model.VertexModel, Single>>
            {
                new(b.Vertex, 5f), new(c.Vertex, 4f)
            };

            var fused = DocumentSearchService.Fuse(dense, lexical);

            Assert.AreEqual(b.Vertex.Id, fused[0].Key.Id, "present on both sides wins");
            Assert.AreEqual(a.Vertex.Id, fused[1].Key.Id, "equal single-side rank ties break by ascending id");
            Assert.AreEqual(c.Vertex.Id, fused[2].Key.Id);
            Assert.AreEqual(1f / 61 + 1f / 62, fused[0].Value, 1e-6f);
        }

        /// <summary>A bare vertex for pure fusion math tests.</summary>
        private sealed class VertexModelStub
        {
            public NoSQL.GraphDB.Core.Model.VertexModel Vertex
            {
                get;
            }

            public VertexModelStub(Int32 id)
            {
                Vertex = new NoSQL.GraphDB.Core.Model.VertexModel(id, 1u, null, null);
            }
        }
    }
}
