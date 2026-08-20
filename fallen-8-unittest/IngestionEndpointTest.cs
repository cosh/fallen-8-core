// MIT License
//
// IngestionEndpointTest.cs
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
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App;
using NoSQL.GraphDB.App.Ingestion;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Index.Fulltext;
using NoSQL.GraphDB.Core.Index.Spatial;
using NoSQL.GraphDB.Core.Index.Spatial.Implementation.Geometry;
using NoSQL.GraphDB.Core.Index.Spatial.Implementation.Metric;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    #region shared fakes and factory

    /// <summary>A deterministic in-test docling: configurable conversion result or fault.</summary>
    internal sealed class FakeDoclingConverter : IDoclingConverter
    {
        internal Func<DoclingConversionResult> OnConvert = () => new DoclingConversionResult();
        internal Boolean ConfiguredFlag = true;
        internal Boolean Reachable = true;

        public Boolean Configured => ConfiguredFlag;

        public Task<DoclingConversionResult> ConvertAsync(Byte[] fileBytes, String fileName,
            CancellationToken cancellationToken)
        {
            if (!ConfiguredFlag)
            {
                throw new DoclingUnavailableException("not configured");
            }

            return Task.FromResult(OnConvert());
        }

        public Task<Boolean> IsReachableAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(ConfiguredFlag && Reachable);
        }
    }

    /// <summary>A deterministic in-test NLP client: entities and key terms are produced by a
    /// configurable rule (default: none). `ThrowUnavailable` simulates the sidecar being down.</summary>
    internal sealed class FakeNlpClient : INlpClient
    {
        internal Func<String, (List<NlpEntity> Entities, List<String> KeyTerms)> OnEnrich =
            _ => (new List<NlpEntity>(), new List<String>());
        internal Boolean ThrowUnavailable;
        internal Boolean ConfiguredFlag = true;

        public Boolean Configured => ConfiguredFlag;

        public Task<IReadOnlyList<NlpEnrichedItem>> EnrichAsync(
            IReadOnlyList<(String Id, String Text)> items, CancellationToken cancellationToken)
        {
            if (ThrowUnavailable)
            {
                throw new NlpUnavailableException("fake NLP down");
            }

            var result = new List<NlpEnrichedItem>(items.Count);
            foreach (var item in items)
            {
                var (entities, keyTerms) = OnEnrich(item.Text);
                result.Add(new NlpEnrichedItem
                {
                    Id = item.Id,
                    Language = "en",
                    Entities = entities,
                    KeyTerms = keyTerms
                });
            }

            return Task.FromResult<IReadOnlyList<NlpEnrichedItem>>(result);
        }

        public Task<Boolean> IsReachableAsync(CancellationToken cancellationToken) => Task.FromResult(ConfiguredFlag);
    }

    /// <summary>The full-stack test host for the /document surface: volatile engine, the
    /// deterministic embedding fake, the in-test docling, the in-test NLP client.</summary>
    internal sealed class IngestionFactory : WebApplicationFactory<Program>
    {
        internal const Int32 Dim = 4;

        internal readonly FakeDoclingConverter Docling = new FakeDoclingConverter();
        internal readonly FakeNlpClient Nlp = new FakeNlpClient();

        /// <summary>Held rather than newed inline so a test can read the BATCHING that happened, the
        /// same way it can read what was asked of the other two fakes.</summary>
        internal readonly FakeEmbeddingGenerator Embeddings;

        private readonly Dictionary<String, String> _settings;

        public IngestionFactory(Dictionary<String, String> settings = null, Int32 fakeDimension = Dim)
        {
            _settings = settings ?? new Dictionary<String, String>();
            Embeddings = new FakeEmbeddingGenerator(fakeDimension);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Fallen8:Durability:Volatile", "true");
            builder.UseSetting("Fallen8:Ingestion:Enabled", "true");
            builder.UseSetting("Fallen8:Ingestion:ChunkMinChars", "1");
            builder.UseSetting("Fallen8:Ingestion:Docling:Endpoint", "http://docling.test:5001");
            builder.UseSetting("Fallen8:Embedding:Enabled", "true");
            builder.UseSetting("Fallen8:Embedding:Backend", "Onnx"); // never constructed: the fake replaces it
            builder.UseSetting("Fallen8:Embedding:ModelName", "fake-model");
            builder.UseSetting("Fallen8:Embedding:Dimension", Dim.ToString());
            foreach (var setting in _settings)
            {
                builder.UseSetting(setting.Key, setting.Value);
            }

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(Embeddings);
                services.AddSingleton<IDoclingConverter>(Docling);
                services.AddSingleton<INlpClient>(Nlp);
            });
        }
    }

    internal static class IngestionTestHelper
    {
        internal static Fallen8 EngineOf(WebApplicationFactory<Program> factory)
            => factory.Services.GetRequiredService<NoSQL.GraphDB.App.Namespaces.Fallen8Namespaces>().Default.Engine;

        internal static StringContent Json(String json) => new StringContent(json, Encoding.UTF8, "application/json");

        internal static async Task<JsonElement> ReadJson(HttpResponseMessage response)
            => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        internal static String TextPayload(String name, String text, String extraJson) =>
            String.Format("{{ \"name\": {0}, \"text\": {1}{2} }}",
                JsonSerializer.Serialize(name), JsonSerializer.Serialize(text),
                String.IsNullOrEmpty(extraJson) ? "" : ", " + extraJson);

        /// <summary>Binds the semantic layer (FR-7) - creates the required indices - so ingestion
        /// is accepted. Explicit binding is required (ingestion never auto-creates); the happy-path
        /// helpers do it transparently. Idempotent. <paramref name="prefix"/> targets a namespace
        /// (e.g. "/ns/side").</summary>
        internal static async Task EnsureBinding(HttpClient client, String prefix = "")
        {
            using var response = await client.PostAsync(prefix + "/document/binding/ensure", null);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                "binding ensure should succeed: " + await response.Content.ReadAsStringAsync());
        }

        /// <summary>POSTs text, asserts the async 202 accept, and returns the stub's documentId.</summary>
        internal static async Task<Int32> PostText(HttpClient client, String name, String text, String extraJson = null)
        {
            await EnsureBinding(client);
            using var response = await client.PostAsync("/document/text", Json(TextPayload(name, text, extraJson)));
            var body = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, body);
            return JsonDocument.Parse(body).RootElement.GetProperty("documentId").GetInt32();
        }

        /// <summary>Polls GET /document/{id} until the status is terminal (indexed|failed);
        /// returns the document summary. The background worker processes the queued job.</summary>
        internal static async Task<JsonElement> AwaitTerminal(HttpClient client, Int32 documentId, Int32 timeoutMs = 20000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                using var get = await client.GetAsync($"/document/{documentId}");
                if (get.StatusCode == HttpStatusCode.OK)
                {
                    var summary = (await ReadJson(get)).GetProperty("summary");
                    var status = summary.GetProperty("status").GetString();
                    if (status == "indexed" || status == "failed")
                    {
                        return summary;
                    }
                }

                if (DateTime.UtcNow > deadline)
                {
                    Assert.Fail($"document {documentId} did not reach a terminal status within {timeoutMs} ms");
                }

                await Task.Delay(50);
            }
        }

        /// <summary>The common happy path: POST text, await, assert it indexed, return the summary.</summary>
        internal static async Task<JsonElement> IngestText(HttpClient client, String name, String text,
            String extraJson = null)
        {
            var documentId = await PostText(client, name, text, extraJson);
            var summary = await AwaitTerminal(client, documentId);
            Assert.AreEqual("indexed", summary.GetProperty("status").GetString(),
                "the ingest should reach 'indexed': " + summary.ToString());
            return summary;
        }

        /// <summary>POSTs a multipart upload, asserts the async 202 accept, returns the documentId.</summary>
        internal static async Task<Int32> PostFile(HttpClient client, MultipartFormDataContent upload)
        {
            await EnsureBinding(client);
            using var response = await client.PostAsync("/document", upload);
            var body = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, body);
            return JsonDocument.Parse(body).RootElement.GetProperty("documentId").GetInt32();
        }

        internal static MultipartFormDataContent Upload(String fileName, Byte[] bytes,
            params KeyValuePair<String, String>[] fields)
        {
            var content = new MultipartFormDataContent();
            content.Add(new ByteArrayContent(bytes), "file", fileName);
            foreach (var field in fields)
            {
                content.Add(new StringContent(field.Value), field.Key);
            }

            return content;
        }

        internal static Int32 CreateVertex(Fallen8 engine, String label, Dictionary<String, Object> properties = null)
        {
            var tx = new CreateVertexTransaction
            {
                Definition = new VertexDefinition { CreationDate = 1u, Label = label, Properties = properties }
            };
            engine.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.VertexCreated.Id;
        }
    }

    #endregion

    /// <summary>
    ///   Feature unstructured-ingestion, write path (FR-1..FR-5, FR-7, FR-13..FR-15): the
    ///   ingest lifecycle end to end, the error matrix, the "one failed Document vertex and
    ///   zero chunks" invariant, manage routes, structural linking, and the /status block.
    /// </summary>
    [TestClass]
    public class IngestionEndpointTest
    {
        #region happy path

        [TestMethod]
        public async Task TextIngest_EndToEnd_CreatesDocumentChunksEdgesEmbeddingsAndIndices()
        {
            using var factory = new IngestionFactory();
            using var client = factory.CreateClient();
            var engine = IngestionTestHelper.EngineOf(factory);

            var summary = await IngestionTestHelper.IngestText(client, "handbook.md",
                "# Alpha\n\nThe RETRY_BUDGET_MS knob.\n\n# Beta\n\nMore prose here.");

            Assert.AreEqual("indexed", summary.GetProperty("status").GetString());
            Assert.AreEqual(2, summary.GetProperty("chunkCount").GetInt32());
            Assert.IsTrue(summary.GetProperty("embedded").GetBoolean());
            Assert.AreEqual("none", summary.GetProperty("converter").GetString());
            Assert.AreEqual(64, summary.GetProperty("contentHash").GetString().Length, "sha-256 hex");
            var documentId = summary.GetProperty("documentId").GetInt32();

            // The graph model (FR-4): labels, order, edges.
            Assert.AreEqual(1, engine.GetAllVertices("Document").Count);
            var chunks = engine.GetAllVertices("Chunk");
            Assert.AreEqual(2, chunks.Count);

            Assert.IsTrue(engine.TryGetVertex(out var document, documentId));
            Assert.IsTrue(document.TryGetOutEdge(out var contains, "contains"));
            Assert.AreEqual(2, contains.Count);
            Assert.IsTrue(document.TryGetProperty<String>(out var status, "status"));
            Assert.AreEqual("indexed", status);

            var first = chunks[0].Id < chunks[1].Id ? chunks[0] : chunks[1];
            Assert.IsTrue(first.TryGetOutEdge(out var next, "next"), "chunk order is wired via next edges");
            Assert.AreEqual(1, next.Count);
            Assert.IsTrue(first.TryGetProperty<String>(out var identifiers, "identifiers"));
            Assert.AreEqual("RETRY_BUDGET_MS", identifiers);

            // Embeddings ride the normal element surface.
            using var embedding = await client.GetAsync($"/graphelement/{first.Id}/embedding/default");
            Assert.AreEqual(HttpStatusCode.OK, embedding.StatusCode);

            // Ensured indices (FR-5): the bound vector index and the fulltext mirror.
            Assert.IsTrue(engine.IndexFactory.TryGetIndex(out var vectorIndex, "documents"));
            Assert.AreEqual(2, vectorIndex.CountOfValues());
            Assert.IsTrue(engine.IndexFactory.TryGetIndex(out var fulltextIndex, "documents-text"));
            Assert.IsInstanceOfType(fulltextIndex, typeof(IFulltextIndex));
            Assert.AreEqual(2, fulltextIndex.CountOfKeys());
        }

        [TestMethod]
        public async Task FileIngest_TextFile_WorksWithoutDocling()
        {
            using var factory = new IngestionFactory();
            factory.Docling.ConfiguredFlag = false;
            using var client = factory.CreateClient();

            using var upload = IngestionTestHelper.Upload("notes.txt", Encoding.UTF8.GetBytes("Plain note content."));
            var documentId = await IngestionTestHelper.PostFile(client, upload);
            var summary = await IngestionTestHelper.AwaitTerminal(client, documentId);

            Assert.AreEqual("indexed", summary.GetProperty("status").GetString());
            Assert.AreEqual("txt", summary.GetProperty("sourceFormat").GetString());
            Assert.AreEqual("none", summary.GetProperty("converter").GetString());
            Assert.AreEqual(1, summary.GetProperty("chunkCount").GetInt32());
        }

        [TestMethod]
        public async Task BinaryIngest_UsesStructuredOutput_TablesAndPages()
        {
            using var factory = new IngestionFactory();
            factory.Docling.OnConvert = () => new DoclingConversionResult
            {
                Markdown = "# ignored (structured wins)",
                PageCount = 2,
                Document = new DoclingDocumentModel
                {
                    Texts = new List<DoclingTextItem>
                    {
                        new DoclingTextItem { SelfRef = "#/texts/0", Label = "section_header", Level = 1, Text = "Ports",
                            Prov = new List<DoclingProvenance> { new DoclingProvenance { PageNo = 1 } } },
                        new DoclingTextItem { SelfRef = "#/texts/1", Label = "text", Text = "The EDGE_TLS_01 server.",
                            Prov = new List<DoclingProvenance> { new DoclingProvenance { PageNo = 1 } } }
                    },
                    Tables = new List<DoclingTableItem>
                    {
                        new DoclingTableItem
                        {
                            SelfRef = "#/tables/0",
                            Prov = new List<DoclingProvenance> { new DoclingProvenance { PageNo = 2 } },
                            Data = new DoclingTableData
                            {
                                Grid = new List<List<DoclingTableCell>>
                                {
                                    new List<DoclingTableCell> { new DoclingTableCell { Text = "Port" } },
                                    new List<DoclingTableCell> { new DoclingTableCell { Text = "443" } }
                                }
                            }
                        }
                    },
                    Body = new DoclingNodeItem
                    {
                        Children = new List<DoclingRef>
                        {
                            new DoclingRef { Ref = "#/texts/0" },
                            new DoclingRef { Ref = "#/texts/1" },
                            new DoclingRef { Ref = "#/tables/0" }
                        }
                    }
                }
            };
            using var client = factory.CreateClient();

            using var upload = IngestionTestHelper.Upload("spec.pdf", new Byte[] { 0x25, 0x50 });
            var documentId = await IngestionTestHelper.PostFile(client, upload);
            var summary = await IngestionTestHelper.AwaitTerminal(client, documentId);

            Assert.AreEqual("indexed", summary.GetProperty("status").GetString(), summary.ToString());
            Assert.AreEqual("docling-serve", summary.GetProperty("converter").GetString());
            Assert.AreEqual(2, summary.GetProperty("pageCount").GetInt32());
            Assert.AreEqual(2, summary.GetProperty("chunkCount").GetInt32(), "one prose chunk, one table chunk");
            StringAssert.StartsWith(summary.GetProperty("chunkerConfig").GetString(), "structured/v1");

            using var detail = await client.GetAsync($"/document/{documentId}");
            var detailJson = await IngestionTestHelper.ReadJson(detail);
            var chunks = detailJson.GetProperty("chunks");
            Assert.AreEqual("text", chunks[0].GetProperty("kind").GetString());
            Assert.AreEqual("table", chunks[1].GetProperty("kind").GetString());
            Assert.AreEqual(2, chunks[1].GetProperty("pageFrom").GetInt32());
        }

        #endregion

        #region gates and validation (pre-stub: the graph stays untouched)

        [TestMethod]
        public async Task CapabilityOff_Answers403()
        {
            // The capability answers 403 to an AUTHENTICATED caller (the api-security-boundary
            // posture; unauthenticated on a keyed server is 401 like everywhere else).
            using var factory = new IngestionFactory(new Dictionary<String, String>
            {
                { "Fallen8:Ingestion:Enabled", "false" },
                { "Fallen8:Security:ApiKey", "ingestion-test-key" }
            });
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Api-Key", "ingestion-test-key");

            using var text = await client.PostAsync("/document/text",
                IngestionTestHelper.Json("{ \"name\": \"n\", \"text\": \"t\" }"));
            Assert.AreEqual(HttpStatusCode.Forbidden, text.StatusCode);

            using var list = await client.GetAsync("/document");
            Assert.AreEqual(HttpStatusCode.Forbidden, list.StatusCode);

            using var search = await client.PostAsync("/document/search",
                IngestionTestHelper.Json("{ \"queryText\": \"x\" }"));
            Assert.AreEqual(HttpStatusCode.Forbidden, search.StatusCode);
        }

        [TestMethod]
        public async Task EmbedWithProviderOff_403_UnembeddedIngestStillWorks()
        {
            using var factory = new IngestionFactory(new Dictionary<String, String>
            {
                { "Fallen8:Embedding:Enabled", "false" }
            });
            using var client = factory.CreateClient();
            var engine = IngestionTestHelper.EngineOf(factory);

            using var refused = await client.PostAsync("/document/text",
                IngestionTestHelper.Json("{ \"name\": \"n\", \"text\": \"some text\" }"));
            Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode, "embed defaults to true");
            Assert.AreEqual(0, engine.GetAllVertices("Document").Count, "a 403 is pre-stub");

            var summary = await IngestionTestHelper.IngestText(client, "n", "some text", "\"embed\": false");
            Assert.IsFalse(summary.GetProperty("embedded").GetBoolean());
            Assert.IsFalse(engine.IndexFactory.TryGetIndex(out _, "documents"),
                "no vector index is ensured for an unembedded ingest");
        }

        [TestMethod]
        public async Task UnsupportedFormat_400_OversizeUpload_413()
        {
            using var factory = new IngestionFactory(new Dictionary<String, String>
            {
                { "Fallen8:Ingestion:MaxUploadBytes", "8" }
            });
            using var client = factory.CreateClient();

            using var unsupported = IngestionTestHelper.Upload("evil.exe", new Byte[] { 1 });
            using var badFormat = await client.PostAsync("/document", unsupported);
            Assert.AreEqual(HttpStatusCode.BadRequest, badFormat.StatusCode);

            using var oversize = IngestionTestHelper.Upload("big.txt", new Byte[16]);
            using var tooLarge = await client.PostAsync("/document", oversize);
            Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);

            using var textTooLarge = await client.PostAsync("/document/text",
                IngestionTestHelper.Json("{ \"name\": \"n\", \"text\": \"way more than eight bytes\" }"));
            Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, textTooLarge.StatusCode);
        }

        [TestMethod]
        public async Task BinaryWithoutDocling_503_PreStub()
        {
            using var factory = new IngestionFactory();
            factory.Docling.ConfiguredFlag = false;
            using var client = factory.CreateClient();
            var engine = IngestionTestHelper.EngineOf(factory);

            using var upload = IngestionTestHelper.Upload("spec.pdf", new Byte[] { 1 });
            using var response = await client.PostAsync("/document", upload);

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.AreEqual(0, engine.GetAllVertices("Document").Count);
        }

        [TestMethod]
        public async Task ReservedTagKey_400()
        {
            using var factory = new IngestionFactory();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync("/document/text", IngestionTestHelper.Json(
                "{ \"name\": \"n\", \"text\": \"t\", \"properties\": { \"status\": \"boom\" } }"));
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        }

        #endregion

        #region post-stub failures (the invariant: one failed Document, zero chunks)

        // Async now: the POST accepts (202) and the failure surfaces on the document's terminal
        // status. This awaits that terminal state and asserts the FR-2 invariant.
        private static async Task AssertFailedStub(IngestionFactory factory, HttpClient client, Int32 documentId)
        {
            var summary = await IngestionTestHelper.AwaitTerminal(client, documentId);
            Assert.AreEqual("failed", summary.GetProperty("status").GetString(), "the ingest should fail");
            Assert.IsTrue(summary.TryGetProperty("error", out var error) && !String.IsNullOrWhiteSpace(error.GetString()),
                "the failed document carries a reason");

            var engine = IngestionTestHelper.EngineOf(factory);
            Assert.AreEqual(0, engine.GetAllVertices("Chunk").Count, "a failed ingest leaves zero chunks");
            Assert.IsTrue(engine.TryGetVertex(out _, documentId), "the failed stub survives");
        }

        [TestMethod]
        public async Task PageCap_FailsTheDocument()
        {
            using var factory = new IngestionFactory(new Dictionary<String, String>
            {
                { "Fallen8:Ingestion:MaxPages", "10" }
            });
            factory.Docling.OnConvert = () => new DoclingConversionResult { Markdown = "# X\n\ncontent", PageCount = 11 };
            using var client = factory.CreateClient();

            using var upload = IngestionTestHelper.Upload("long.pdf", new Byte[] { 1 });
            var documentId = await IngestionTestHelper.PostFile(client, upload);
            await AssertFailedStub(factory, client, documentId);
        }

        [TestMethod]
        public async Task DoclingFault_FailsTheDocument()
        {
            using var factory = new IngestionFactory();
            factory.Docling.OnConvert = () => throw new DoclingUnavailableException("sidecar melted");
            using var client = factory.CreateClient();

            using var upload = IngestionTestHelper.Upload("spec.pdf", new Byte[] { 1 });
            var documentId = await IngestionTestHelper.PostFile(client, upload);
            await AssertFailedStub(factory, client, documentId);
        }

        [TestMethod]
        public async Task EmptyConversion_FailsTheDocument()
        {
            using var factory = new IngestionFactory();
            factory.Docling.OnConvert = () => new DoclingConversionResult();
            using var client = factory.CreateClient();

            using var upload = IngestionTestHelper.Upload("empty.pdf", new Byte[] { 1 });
            var documentId = await IngestionTestHelper.PostFile(client, upload);
            await AssertFailedStub(factory, client, documentId);
        }

        [TestMethod]
        public async Task ProviderDimensionLie_FailsTheDocument_BeforeAnyChunkWrite()
        {
            // The fake emits 3 components while the provider declares 4: the provider latches
            // unavailable (embedding-provider FR-8) and the ingest fails embed-before-write.
            using var factory = new IngestionFactory(fakeDimension: 3);
            using var client = factory.CreateClient();

            var documentId = await IngestionTestHelper.PostText(client, "n", "some text");
            await AssertFailedStub(factory, client, documentId);
        }

        [TestMethod]
        public async Task ChunkCapPerDocument_FailsTheDocument()
        {
            using var factory = new IngestionFactory(new Dictionary<String, String>
            {
                { "Fallen8:Ingestion:MaxChunksPerDocument", "1" }
            });
            using var client = factory.CreateClient();

            var documentId = await IngestionTestHelper.PostText(client, "n", "# A\n\none\n\n# B\n\ntwo");
            await AssertFailedStub(factory, client, documentId);
        }

        #endregion

        #region ceiling (FR-14)

        [TestMethod]
        public async Task Ceiling_PreCheck507_AndPostChunk507()
        {
            using var factory = new IngestionFactory(new Dictionary<String, String>
            {
                { "Fallen8:Ingestion:MaxChunksPerNamespace", "2" }
            });
            using var client = factory.CreateClient();
            var engine = IngestionTestHelper.EngineOf(factory);

            // Post-chunk check: 3 chunks would cross the ceiling of 2 -> the async job fails the
            // stub (the pre-stub count was under the ceiling, so it accepts then fails on write).
            var tooManyId = await IngestionTestHelper.PostText(client, "big", "# A\n\na\n\n# B\n\nb\n\n# C\n\nc");
            await AssertFailedStub(factory, client, tooManyId);

            await IngestionTestHelper.IngestText(client, "ok", "# A\n\na\n\n# B\n\nb");

            // Pre-check: the namespace is AT the ceiling -> 507 before any stub.
            var documentsBefore = engine.GetAllVertices("Document").Count;
            using var full = await client.PostAsync("/document/text",
                IngestionTestHelper.Json("{ \"name\": \"more\", \"text\": \"content\" }"));
            Assert.AreEqual((HttpStatusCode)507, full.StatusCode);
            Assert.AreEqual(documentsBefore, engine.GetAllVertices("Document").Count, "the pre-check creates no stub");

            // The budget is visible on GET /document.
            using var list = await client.GetAsync("/document");
            var listJson = await IngestionTestHelper.ReadJson(list);
            Assert.AreEqual(2, listJson.GetProperty("namespaceChunkCount").GetInt32());
            Assert.AreEqual(2, listJson.GetProperty("chunkCeiling").GetInt32());
        }

        #endregion

        #region re-ingestion (FR-15)

        [TestMethod]
        public async Task DuplicateHash_409_ReplaceDocumentId_Swaps()
        {
            using var factory = new IngestionFactory();
            using var client = factory.CreateClient();
            var engine = IngestionTestHelper.EngineOf(factory);

            var original = await IngestionTestHelper.IngestText(client, "v1", "# A\n\noriginal content");
            var originalId = original.GetProperty("documentId").GetInt32();

            using var duplicate = await client.PostAsync("/document/text", IngestionTestHelper.Json(
                "{ \"name\": \"other-name\", \"text\": \"# A\\n\\noriginal content\" }"));
            Assert.AreEqual(HttpStatusCode.Conflict, duplicate.StatusCode, "identical bytes are a 409");

            var replaced = await IngestionTestHelper.IngestText(client, "v2", "# A\n\nedited content",
                $"\"replaceDocumentId\": {originalId}");
            Assert.AreEqual("indexed", replaced.GetProperty("status").GetString());

            // The old doc is removed by the worker right after the new one indexes; give that a
            // moment (it is a separate committed transaction in the same job).
            Assert.IsTrue(SpinWait.SpinUntil(() => !engine.TryGetVertex(out _, originalId), 5000),
                "the replaced document is gone");
            Assert.AreEqual(1, engine.GetAllVertices("Document").Count);

            // Re-uploading the SAME bytes with replace of the target is not a duplicate.
            var newId = replaced.GetProperty("documentId").GetInt32();
            var again = await IngestionTestHelper.IngestText(client, "v2", "# A\n\nedited content",
                $"\"replaceDocumentId\": {newId}");
            Assert.AreEqual("indexed", again.GetProperty("status").GetString());
        }

        [TestMethod]
        public async Task ReplaceTarget_Missing404_NonDocument400()
        {
            using var factory = new IngestionFactory();
            using var client = factory.CreateClient();
            var engine = IngestionTestHelper.EngineOf(factory);
            var plainVertex = IngestionTestHelper.CreateVertex(engine, "person");

            using var missing = await client.PostAsync("/document/text", IngestionTestHelper.Json(
                "{ \"name\": \"n\", \"text\": \"t\", \"replaceDocumentId\": 4711 }"));
            Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);

            using var wrongLabel = await client.PostAsync("/document/text", IngestionTestHelper.Json(
                $"{{ \"name\": \"n\", \"text\": \"t\", \"replaceDocumentId\": {plainVertex} }}"));
            Assert.AreEqual(HttpStatusCode.BadRequest, wrongLabel.StatusCode);
        }

        #endregion

        #region manage routes (FR-7)

        [TestMethod]
        public async Task List_Get_Delete_RoundTrip()
        {
            using var factory = new IngestionFactory();
            using var client = factory.CreateClient();
            var engine = IngestionTestHelper.EngineOf(factory);

            var first = await IngestionTestHelper.IngestText(client, "alpha.md",
                "# One\n\nThe RETRY_BUDGET_MS knob explained at length.");
            await IngestionTestHelper.IngestText(client, "beta.md", "# Two\n\nUnrelated prose.");
            var firstId = first.GetProperty("documentId").GetInt32();

            using var list = await client.GetAsync("/document");
            var listJson = await IngestionTestHelper.ReadJson(list);
            Assert.AreEqual(2, listJson.GetProperty("documents").GetArrayLength());
            Assert.AreEqual("fake-model#4#Cosine", listJson.GetProperty("currentEmbeddingModel").GetString());

            using var detail = await client.GetAsync($"/document/{firstId}");
            var detailJson = await IngestionTestHelper.ReadJson(detail);
            Assert.AreEqual("alpha.md", detailJson.GetProperty("summary").GetProperty("name").GetString());
            var chunk = detailJson.GetProperty("chunks")[0];
            Assert.AreEqual(0, chunk.GetProperty("order").GetInt32());
            Assert.AreEqual("RETRY_BUDGET_MS", chunk.GetProperty("identifiers")[0].GetString());
            Assert.IsTrue(chunk.GetProperty("textPreview").GetString().Length > 0);

            using var delete = await client.DeleteAsync($"/document/{firstId}?waitForCompletion=true");
            Assert.AreEqual(HttpStatusCode.Accepted, delete.StatusCode);

            Assert.AreEqual(1, engine.GetAllVertices("Document").Count);
            Assert.AreEqual(1, engine.GetAllVertices("Chunk").Count, "the document's chunks cascade");
            Assert.IsTrue(engine.IndexFactory.TryGetIndex(out var fulltextIndex, "documents-text"));
            Assert.AreEqual(1, fulltextIndex.CountOfKeys(), "the fulltext mirror is cleaned");
            Assert.IsTrue(engine.IndexFactory.TryGetIndex(out var vectorIndex, "documents"));
            Assert.AreEqual(1, vectorIndex.CountOfValues(), "the bound index purges removed chunks");

            using var gone = await client.GetAsync($"/document/{firstId}");
            Assert.AreEqual(HttpStatusCode.NotFound, gone.StatusCode);
        }

        [TestMethod]
        public async Task Get_And_Delete_RejectNonDocuments()
        {
            using var factory = new IngestionFactory();
            using var client = factory.CreateClient();
            var engine = IngestionTestHelper.EngineOf(factory);
            var plainVertex = IngestionTestHelper.CreateVertex(engine, "person");

            using var get = await client.GetAsync($"/document/{plainVertex}");
            Assert.AreEqual(HttpStatusCode.BadRequest, get.StatusCode);

            using var delete = await client.DeleteAsync($"/document/{plainVertex}");
            Assert.AreEqual(HttpStatusCode.BadRequest, delete.StatusCode);

            using var missing = await client.GetAsync("/document/4711");
            Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
        }

        #endregion

        #region structural linking (FR-13)

        private static Int32 IndexedVertex(Fallen8 engine, String indexId, String key)
        {
            var id = IngestionTestHelper.CreateVertex(engine, "server",
                new Dictionary<String, Object> { { "sku", key } });
            Assert.IsTrue(engine.IndexFactory.TryGetIndex(out var index, indexId));
            Assert.IsTrue(engine.TryGetVertex(out var vertex, id));
            index.AddOrUpdate(key, vertex);
            return id;
        }

        [TestMethod]
        public async Task Linking_ExactMatch_CreatesMentionsEdges()
        {
            using var factory = new IngestionFactory();
            using var client = factory.CreateClient();
            var engine = IngestionTestHelper.EngineOf(factory);

            Assert.IsTrue(engine.IndexFactory.TryCreateIndex(out _, "sku-idx", "DictionaryIndex"));
            var target = IndexedVertex(engine, "sku-idx", "EDGE_TLS_01");
            IndexedVertex(engine, "sku-idx", "OTHER_BOX_02");

            await IngestionTestHelper.IngestText(client, "n",
                "# Servers\n\nThe EDGE_TLS_01 box terminates tls.",
                "\"link\": { \"indexIds\": [\"sku-idx\"] }");

            var chunk = engine.GetAllVertices("Chunk")[0];
            Assert.IsTrue(chunk.TryGetOutEdge(out var mentions, "mentions"));
            Assert.AreEqual(1, mentions.Count);
            Assert.AreEqual(target, mentions[0].TargetVertex.Id);
        }

        [TestMethod]
        public async Task Linking_IsExactAndCaseSensitive()
        {
            using var factory = new IngestionFactory();
            using var client = factory.CreateClient();
            var engine = IngestionTestHelper.EngineOf(factory);

            Assert.IsTrue(engine.IndexFactory.TryCreateIndex(out _, "sku-idx", "DictionaryIndex"));
            IndexedVertex(engine, "sku-idx", "Edge_Tls_01");        // different case
            IndexedVertex(engine, "sku-idx", "EDGE_TLS_01_EXTRA");  // superstring

            await IngestionTestHelper.IngestText(client, "n",
                "EDGE_TLS_01 appears here.", "\"link\": { \"indexIds\": [\"sku-idx\"] }");

            var chunk = engine.GetAllVertices("Chunk")[0];
            Assert.IsFalse(chunk.TryGetOutEdge(out _, "mentions"),
                "no fuzzy matching, no substring matching, ordinal case: nothing links");
        }

        [TestMethod]
        public async Task Linking_CapIsDeterministic_LowestElementIdsWin()
        {
            using var factory = new IngestionFactory();
            using var client = factory.CreateClient();
            var engine = IngestionTestHelper.EngineOf(factory);

            Assert.IsTrue(engine.IndexFactory.TryCreateIndex(out _, "sku-idx", "DictionaryIndex"));
            var a = IndexedVertex(engine, "sku-idx", "EDGE_TLS_01");
            var b = IndexedVertex(engine, "sku-idx", "EDGE_TLS_01");
            IndexedVertex(engine, "sku-idx", "EDGE_TLS_01");

            await IngestionTestHelper.IngestText(client, "n",
                "EDGE_TLS_01 appears here.",
                "\"link\": { \"indexIds\": [\"sku-idx\"], \"maxLinksPerChunk\": 2 }");

            var chunk = engine.GetAllVertices("Chunk")[0];
            Assert.IsTrue(chunk.TryGetOutEdge(out var mentions, "mentions"));
            var targets = new List<Int32> { mentions[0].TargetVertex.Id, mentions[1].TargetVertex.Id };
            targets.Sort();
            CollectionAssert.AreEqual(new List<Int32> { a, b }, targets, "ascending element ids win the cap");
        }

        [TestMethod]
        public async Task Linking_Validation400s_ArePreStub()
        {
            using var factory = new IngestionFactory();
            using var client = factory.CreateClient();
            var engine = IngestionTestHelper.EngineOf(factory);

            using var unknown = await client.PostAsync("/document/text", IngestionTestHelper.Json(
                "{ \"name\": \"n\", \"text\": \"t\", \"link\": { \"indexIds\": [\"nope\"] } }"));
            Assert.AreEqual(HttpStatusCode.BadRequest, unknown.StatusCode);

            using var overCap = await client.PostAsync("/document/text", IngestionTestHelper.Json(
                "{ \"name\": \"n\", \"text\": \"t\", \"link\": { \"indexIds\": [\"x\"], \"maxLinksPerChunk\": 999 } }"));
            Assert.AreEqual(HttpStatusCode.BadRequest, overCap.StatusCode);

            Assert.AreEqual(0, engine.GetAllVertices("Document").Count, "link validation is pre-stub");
        }

        [TestMethod]
        public async Task Linking_RejectsANonEqualityCapableIndex_With400()
        {
            // CA-1: a spatial index is not equality-capable (its keys are geometries; an identifier
            // token can never TryGetValue against it), so naming it in a link allowlist must be a
            // pre-stub 400 - not silently accepted and then yield zero links. Vector is rejected
            // already; spatial is the family that slipped through the former "is vector || is
            // fulltext" test, so it is the honest reproducing case.
            using var factory = new IngestionFactory();
            using var client = factory.CreateClient();
            var engine = IngestionTestHelper.EngineOf(factory);

            Assert.IsTrue(engine.IndexFactory.TryCreateIndex(out _, "geo-idx", "SpatialIndex",
                new Dictionary<String, Object>
                {
                    { "IMetric", new EuclidianMetric() },
                    { "MinCount", 2 },
                    { "MaxCount", 5 },
                    { "Space", new List<IDimension> { new RealDimension(), new RealDimension() } }
                }));

            using var response = await client.PostAsync("/document/text", IngestionTestHelper.Json(
                "{ \"name\": \"n\", \"text\": \"anything\", \"link\": { \"indexIds\": [\"geo-idx\"] } }"));

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode,
                "a spatial (non-equality-capable) link index must be a pre-stub 400");
            StringAssert.Contains(await response.Content.ReadAsStringAsync(), "equality");
            Assert.AreEqual(0, engine.GetAllVertices("Document").Count, "rejection is pre-stub");
        }

        [TestMethod]
        public async Task Linking_AcceptsAFulltextIndex_AsEqualityCapable()
        {
            // CA-1: a fulltext RegExIndex does exact-key AddOrUpdate/TryGetValue (the regex lives
            // only in the scan path), so it IS equality-capable and valid as a link index - matching
            // what /status reports via IndexCapabilities. The former predicate wrongly rejected it.
            using var factory = new IngestionFactory();
            using var client = factory.CreateClient();
            var engine = IngestionTestHelper.EngineOf(factory);

            Assert.IsTrue(engine.IndexFactory.TryCreateIndex(out _, "ft-idx", "RegExIndex"));
            var target = IndexedVertex(engine, "ft-idx", "EDGE_TLS_01");

            await IngestionTestHelper.IngestText(client, "n",
                "# Servers\n\nThe EDGE_TLS_01 box terminates tls.",
                "\"link\": { \"indexIds\": [\"ft-idx\"] }");

            var chunk = engine.GetAllVertices("Chunk")[0];
            Assert.IsTrue(chunk.TryGetOutEdge(out var mentions, "mentions"));
            Assert.AreEqual(1, mentions.Count);
            Assert.AreEqual(target, mentions[0].TargetVertex.Id);
        }

        #endregion

        #region status block (FR-1)

        [TestMethod]
        public async Task Status_CarriesTheIngestionBlock()
        {
            using var factory = new IngestionFactory();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/status");
            var status = await IngestionTestHelper.ReadJson(response);
            var ingestion = status.GetProperty("ingestion");

            Assert.IsTrue(ingestion.GetProperty("enabled").GetBoolean());
            Assert.IsTrue(ingestion.GetProperty("docling").GetProperty("configured").GetBoolean());
            Assert.IsTrue(ingestion.GetProperty("docling").GetProperty("reachable").GetBoolean());
            Assert.AreEqual("documents", ingestion.GetProperty("vectorIndexId").GetString());
            Assert.AreEqual("documents-text", ingestion.GetProperty("fulltextIndexId").GetString());
            Assert.AreEqual(500, ingestion.GetProperty("limits").GetProperty("maxPages").GetInt32());
            Assert.IsTrue(ingestion.GetProperty("textFormats").GetArrayLength() > 0);
        }

        [TestMethod]
        public async Task Status_ReportsDisabledAndUnreachable()
        {
            using var factory = new IngestionFactory(new Dictionary<String, String>
            {
                { "Fallen8:Ingestion:Enabled", "false" }
            });
            factory.Docling.Reachable = false;
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/status");
            var ingestion = (await IngestionTestHelper.ReadJson(response)).GetProperty("ingestion");
            Assert.IsFalse(ingestion.GetProperty("enabled").GetBoolean());
            Assert.IsFalse(ingestion.GetProperty("docling").GetProperty("reachable").GetBoolean());
        }

        #endregion

        #region namespace scoping (FR-8)

        [TestMethod]
        public async Task NamespaceTwins_IsolatePerNamespace()
        {
            using var factory = new IngestionFactory();
            using var client = factory.CreateClient();

            using var create = await client.PutAsync("/ns/side", null);
            Assert.IsTrue(create.IsSuccessStatusCode, await create.Content.ReadAsStringAsync());

            // Binding is per-namespace and explicit (FR-7): bind 'side' before ingesting into it.
            await IngestionTestHelper.EnsureBinding(client, "/ns/side");

            using var response = await client.PostAsync("/ns/side/document/text",
                IngestionTestHelper.Json("{ \"name\": \"n\", \"text\": \"# S\\n\\nside content\" }"));
            var body = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, body);
            var documentId = JsonDocument.Parse(body).RootElement.GetProperty("documentId").GetInt32();

            // The worker must resolve the job's namespace ('side'), NOT the default: poll the side
            // route until it indexes there. This is the end-to-end proof of the job-carries-
            // namespace design (the request-thread AsyncLocal does not flow to the worker).
            var deadline = DateTime.UtcNow.AddSeconds(20);
            String status = null;
            while (DateTime.UtcNow < deadline)
            {
                using var poll = await client.GetAsync($"/ns/side/document/{documentId}");
                if (poll.StatusCode == HttpStatusCode.OK)
                {
                    status = (await IngestionTestHelper.ReadJson(poll)).GetProperty("summary").GetProperty("status").GetString();
                    if (status == "indexed" || status == "failed")
                    {
                        break;
                    }
                }

                await Task.Delay(50);
            }

            Assert.AreEqual("indexed", status, "the document indexed in the 'side' namespace");

            using var sideList = await client.GetAsync("/ns/side/document");
            var sideJson = await IngestionTestHelper.ReadJson(sideList);
            Assert.AreEqual(1, sideJson.GetProperty("documents").GetArrayLength());
            Assert.IsTrue(sideJson.GetProperty("documents")[0].GetProperty("chunkCount").GetInt32() >= 1,
                "the chunks were written into the 'side' engine");

            using var defaultList = await client.GetAsync("/document");
            var defaultJson = await IngestionTestHelper.ReadJson(defaultList);
            Assert.AreEqual(0, defaultJson.GetProperty("documents").GetArrayLength(),
                "the default namespace saw nothing");
        }

        #endregion
    }
}
