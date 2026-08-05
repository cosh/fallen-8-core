// MIT License
//
// AuditDefectDocumentContractTest.cs
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
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App;
using NoSQL.GraphDB.App.Ingestion;
using NoSQL.GraphDB.Mcp.Configuration;
using NoSQL.GraphDB.Mcp.Tools;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Pins two audit-defect fixes on the document surface.
    ///   <para>B16 - the two ingest routes declared statuses the ASYNCHRONOUS pipeline can never
    ///   answer with: a 502 for a bad embedding response, and a 413 naming
    ///   <c>MaxPages</c>/<c>MaxChunksPerDocument</c>. Those ceilings and every embedding fault are
    ///   enforced on the worker, long after the request answered 202, so they surface as a
    ///   <c>failed</c> document instead. The runtime side is already pinned by IngestionEndpointTest
    ///   (PageCap_FailsTheDocument, ChunkCapPerDocument_FailsTheDocument,
    ///   ProviderDimensionLie_FailsTheDocument_BeforeAnyChunkWrite, Ceiling_PreCheck507_AndPostChunk507);
    ///   what was wrong is the DESCRIBED contract, so it is the served OpenAPI document that is
    ///   asserted here.</para>
    ///   <para>B17 - the entity-type filter example claimed PER/ORG/LOC, but the shipped English
    ///   spaCy models (en_core_web_lg / en_core_web_trf) are OntoNotes-trained and emit
    ///   PERSON/ORG/GPE, and the label is stored and compared verbatim. Following the old example
    ///   returned an empty page with no hint, which is what the behavioural test below reproduces.</para>
    /// </summary>
    [TestClass]
    public class AuditDefectDocumentContractTest
    {
        private const String DocumentPath = "/openapi/v0.1.json";

        /// <summary>The ingest routes and their /ns/{ns} twins: the twin is generated, so it must
        /// carry the corrected contract too.</summary>
        private static readonly String[] IngestPaths =
        {
            "/document", "/ns/{ns}/document", "/document/text", "/ns/{ns}/document/text"
        };

        /// <summary>Exactly the statuses the REQUEST thread can produce: format/tag/link validation
        /// (400), the credential (401), the disabled capability or a disabled embedding provider
        /// (403), a missing replace target (404), a duplicate hash or an index conflict (409), the
        /// upload cap (413), the unbound semantic layer (428), a missing docling endpoint or a full
        /// queue (503), and the pre-check on the namespace chunk ceiling (507). Anything else is
        /// decided on the worker and cannot be an HTTP status of this call.
        /// </summary>
        private static readonly String[] ReachableIngestStatuses =
        {
            "202", "400", "401", "403", "404", "409", "413", "428", "503", "507"
        };

        /// <summary>
        /// Boots the app in Development (only there are /openapi and Scalar mapped) with a volatile
        /// engine, so generating the document writes no checkpoint or WAL.
        /// </summary>
        private sealed class DevelopmentApiFactory : WebApplicationFactory<Program>
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseEnvironment("Development");
                builder.UseSetting("Fallen8:Durability:Volatile", "true");
            }
        }

        private static async Task<JsonDocument> ServedDocument(WebApplicationFactory<Program> factory)
        {
            using var client = factory.CreateClient();
            using var response = await client.GetAsync(DocumentPath);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                "The framework must serve the OpenAPI document at " + DocumentPath + " in Development.");
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }

        private static JsonElement PostResponses(JsonDocument document, String path)
        {
            Assert.IsTrue(document.RootElement.GetProperty("paths").TryGetProperty(path, out var pathItem),
                "the document must describe " + path);
            return pathItem.GetProperty("post").GetProperty("responses");
        }

        private static String Description(JsonElement responses, String status)
        {
            Assert.IsTrue(responses.TryGetProperty(status, out var response),
                "the operation must declare " + status);
            return response.TryGetProperty("description", out var description)
                ? description.GetString() ?? String.Empty
                : String.Empty;
        }

        private static void AssertOmits(String text, String phrase, String because)
        {
            Assert.IsTrue(text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) < 0,
                "'" + phrase + "' must not appear in \"" + text + "\": " + because);
        }

        /// <summary>Asserts a label does not appear as a WHOLE token, so "PER" is rejected while
        /// "PERSON" is not. Only PER is wrong: LOC and DATE are genuine OntoNotes labels, so this
        /// deliberately does not blacklist the rest of the old example set.</summary>
        private static void AssertOmitsLabel(String text, String label, String because)
        {
            Assert.IsFalse(Regex.IsMatch(text, @"\b" + Regex.Escape(label) + @"\b"),
                "the label '" + label + "' must not appear in \"" + text + "\": " + because);
        }

        #region B16 - the ingest routes describe only request-thread statuses

        [TestMethod]
        public async Task IngestRoutes_DeclareOnlyTheStatusesTheRequestThreadCanAnswerWith()
        {
            using var factory = new DevelopmentApiFactory();
            using var document = await ServedDocument(factory);

            foreach (var path in IngestPaths)
            {
                var responses = PostResponses(document, path);
                var declared = responses.EnumerateObject().Select(p => p.Name).ToList();
                declared.Sort(StringComparer.Ordinal);

                CollectionAssert.AreEqual(ReachableIngestStatuses, declared,
                    "POST " + path + " must describe exactly the request-thread statuses; a 502 (a bad " +
                    "embedding response) is decided on the worker after this call already answered 202. " +
                    "Seen: " + String.Join(", ", declared));
            }
        }

        [TestMethod]
        public async Task IngestRoutes_413NamesOnlyTheUploadCap_AndNoWorkerOnlyCeiling()
        {
            using var factory = new DevelopmentApiFactory();
            using var document = await ServedDocument(factory);

            foreach (var path in IngestPaths)
            {
                var responses = PostResponses(document, path);

                // The ONLY 413 the request thread produces is the byte cap on the payload.
                var payloadTooLarge = Description(responses, "413");
                StringAssert.Contains(payloadTooLarge, "MaxUploadBytes",
                    "the 413 on POST " + path + " must name the cap that actually produces it");
                AssertOmits(payloadTooLarge, "MaxPages",
                    "the page cap is checked after conversion, on the worker, and fails the document");
                AssertOmits(payloadTooLarge, "MaxChunksPerDocument",
                    "the per-document chunk cap is checked after chunking, on the worker");
                AssertOmits(payloadTooLarge, "chunk cap",
                    "no chunk cap can answer 413: chunking happens after the 202");

                // The 400 must not promise worker-only conversion outcomes either.
                var badRequest = Description(responses, "400");
                AssertOmits(badRequest, "empty conversion", "the conversion runs on the worker");
                AssertOmits(badRequest, "no chunks", "chunking runs on the worker");

                // 503 keeps only its two request-thread causes; an embedding backend that is off is
                // a 403 here, and one that faults mid-pipeline fails the document.
                var unavailable = Description(responses, "503");
                AssertOmits(unavailable, "embedding",
                    "embed=true with the provider off is 403; a provider fault happens on the worker");
                StringAssert.Contains(unavailable, "queue",
                    "a full ingestion queue is a real 503 on POST " + path);

                // The namespace ceiling IS pre-checked before the stub, so 507 stays a real status.
                StringAssert.Contains(Description(responses, "507"), "ceiling",
                    "the namespace chunk ceiling is pre-checked on the request thread and must stay declared");
            }
        }

        [TestMethod]
        public async Task IngestRemarks_TellTheCallerWhereTheWorkerOnlyOutcomesShowUp()
        {
            using var factory = new DevelopmentApiFactory();
            using var document = await ServedDocument(factory);

            // The file route is the one home for the async-failure story; /document/text points at it
            // ("Same lifecycle and failure semantics as the file route"), so only the home is asserted.
            var remarks = document.RootElement.GetProperty("paths").GetProperty("/document")
                .GetProperty("post").GetProperty("description").GetString() ?? String.Empty;

            foreach (var expected in new[] { "MaxPages", "MaxChunksPerDocument", "failed", "GET /document/" })
            {
                StringAssert.Contains(remarks, expected,
                    "the remark must state where a worker-only outcome becomes observable, but does " +
                    "not mention '" + expected + "': " + remarks);
            }
        }

        #endregion

        #region B17 - the entity-type filter is the raw NLP label

        private static Dictionary<String, String> NlpOn() => new Dictionary<String, String>
        {
            { "Fallen8:Nlp:Enabled", "true" }
        };

        private static NlpEntity Entity(String text, String label) =>
            new NlpEntity { Text = text, Label = label, Start = 0, End = text.Length };

        private static async Task<JsonElement> Entities(HttpClient client, String query)
        {
            using var response = await client.GetAsync("/document/entities" + query);
            var body = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
            return JsonDocument.Parse(body).RootElement;
        }

        [TestMethod]
        public async Task EntityTypeFilter_MatchesTheRawOntoNotesLabel_AndTheOldExampleMatchedNothing()
        {
            using var factory = new IngestionFactory(NlpOn());
            // What the shipped models really emit: OntoNotes labels, stored verbatim on the vertex.
            factory.Nlp.OnEnrich = _ => (
                new List<NlpEntity> { Entity("Erika Mustermann", "PERSON"), Entity("Muenchen", "GPE") },
                new List<String>());
            using var client = factory.CreateClient();

            await IngestionTestHelper.IngestText(client, "notes.md", "# H\n\nErika Mustermann in Muenchen.");

            var all = await Entities(client, String.Empty);
            Assert.AreEqual(2, all.GetProperty("total").GetInt32(), "both entities are in the corpus");

            // The documented examples must actually select something, case-insensitively.
            var people = await Entities(client, "?type=PERSON");
            Assert.AreEqual(1, people.GetProperty("total").GetInt32(), "PERSON is a real label");
            Assert.AreEqual("Erika Mustermann", people.GetProperty("entities")[0].GetProperty("text").GetString());

            var places = await Entities(client, "?type=gpe");
            Assert.AreEqual(1, places.GetProperty("total").GetInt32(), "the compare is case-insensitive");
            Assert.AreEqual("GPE", places.GetProperty("entities")[0].GetProperty("type").GetString(),
                "the stored type is the model's own label, never a normalized one");

            // The old example value: an exact compare against a label no shipped model emits, so it
            // silently returns an empty page - which is exactly why the example was wrong (B17).
            var underDocumented = await Entities(client, "?type=PER");
            Assert.AreEqual(0, underDocumented.GetProperty("total").GetInt32(),
                "'PER' is not an OntoNotes label; it can only ever match nothing");
            Assert.AreEqual(0, underDocumented.GetProperty("entities").GetArrayLength());

            // A label the corpus does not mention behaves the same way (no partial matching).
            var unmentioned = await Entities(client, "?type=ORG");
            Assert.AreEqual(0, unmentioned.GetProperty("total").GetInt32(),
                "the filter is an exact label compare, not a substring one");
        }

        [TestMethod]
        public async Task EntitiesParameter_DocumentsTheRealLabelSet()
        {
            using var factory = new DevelopmentApiFactory();
            using var document = await ServedDocument(factory);

            foreach (var path in new[] { "/document/entities", "/ns/{ns}/document/entities" })
            {
                var parameters = document.RootElement.GetProperty("paths").GetProperty(path)
                    .GetProperty("get").GetProperty("parameters");
                var type = parameters.EnumerateArray()
                    .First(p => p.GetProperty("name").GetString() == "type");
                var description = type.GetProperty("description").GetString() ?? String.Empty;

                StringAssert.Contains(description, "PERSON", "GET " + path + " must show a real label");
                StringAssert.Contains(description, "GPE", "GET " + path + " must show a real label");
                AssertOmitsLabel(description, "PER",
                    "no shipped model emits PER, so the example could only ever match nothing (B17)");
            }
        }

        [TestMethod]
        public void McpEntityTypeSchema_DocumentsTheRealLabelSet()
        {
            // Describe() never calls the bridge; a handler that would fail loudly proves it.
            var bridge = McpTestSupport.Bridge(new McpTestSupport.LambdaHandler(
                _ => throw new InvalidOperationException("Describe must not reach the REST API.")));
            var tool = new DocumentsTool(bridge);

            var schema = JsonSerializer.SerializeToElement(tool.Describe(new McpToolsOptions()).InputSchema);
            var description = schema.GetProperty("properties").GetProperty("entityType")
                .GetProperty("description").GetString() ?? String.Empty;

            // The schema string is the ONLY label guidance an agent gets, so it must be the real set.
            StringAssert.Contains(description, "PERSON", "an agent needs a label that matches something");
            StringAssert.Contains(description, "GPE", "an agent needs a label that matches something");
            AssertOmitsLabel(description, "PER",
                "no shipped model emits PER, so the example could only ever match nothing (B17)");
        }

        #endregion
    }
}
