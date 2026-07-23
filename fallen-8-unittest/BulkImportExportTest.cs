// MIT License
//
// BulkImportExportTest.cs
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
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Tests for streaming JSONL bulk export/import (feature bulk-import-export): the format's
    /// per-type round-trip fidelity, the export's internal-consistency construction, the
    /// import's id remapping / batching / empty-target gate, the line-numbered fail-fast error
    /// contract, limits, and the full export-import round trip.
    /// </summary>
    [TestClass]
    public class BulkImportExportTest
    {
        private sealed class BulkFactory : WebApplicationFactory<Program>
        {
            private readonly IReadOnlyDictionary<string, string> _settings;

            public BulkFactory(IReadOnlyDictionary<string, string> settings = null)
            {
                _settings = settings ?? new Dictionary<string, string>();
            }

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseSetting("Fallen8:Durability:Volatile", "true");
                foreach (var kv in _settings)
                {
                    builder.UseSetting(kv.Key, kv.Value);
                }
            }
        }

        #region helpers

        private static Fallen8 EngineOf(BulkFactory factory)
        {
            return (Fallen8)factory.Services.GetRequiredService<IFallen8>();
        }

        private static StringContent Ndjson(string body)
        {
            return new StringContent(body, Encoding.UTF8, "application/x-ndjson");
        }

        private static async Task<HttpResponseMessage> Import(HttpClient client, string body)
        {
            return await client.PostAsync("/bulk/import", Ndjson(body));
        }

        /// <summary>Seeds two labeled vertices and one edge and returns their ids.</summary>
        private static (int a, int b, int edge) SeedSmallGraph(Fallen8 engine)
        {
            var vtx = new CreateVerticesTransaction();
            vtx.AddVertex(100u, "person", new Dictionary<string, object> { { "name", "Alice" } });
            vtx.AddVertex(200u, "robot");
            engine.EnqueueTransaction(vtx).WaitUntilFinished();
            var v = vtx.GetCreatedVertices();

            var etx = new CreateEdgesTransaction();
            etx.AddEdge(v[0].Id, "knows", v[1].Id, 300u, "friendship", new Dictionary<string, object> { { "weight", 2.5d } });
            engine.EnqueueTransaction(etx).WaitUntilFinished();

            return (v[0].Id, v[1].Id, etx.GetCreatedEdges()[0].Id);
        }

        private static List<JsonElement> ParseNdjson(string body)
        {
            return body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line).RootElement.Clone())
                .ToList();
        }

        #endregion

        #region format fidelity (unit level)

        /// <summary>One representative value per AllowedLiteralTypes type, incl. the hard cases:
        /// Int64 above 2^53, Decimal scale, Double needing round-trip, DateTime sub-second ticks.</summary>
        private static readonly (string Key, object Value)[] AllTypedValues =
        {
            ("p_string", "hello \"world\" \n umlaut ä"),
            ("p_bool", true),
            ("p_byte", (byte)255),
            ("p_sbyte", (sbyte)-128),
            ("p_short", (short)-32768),
            ("p_ushort", (ushort)65535),
            ("p_int", -2147483648),
            ("p_uint", 4294967295u),
            ("p_long", 9007199254740993L),            // 2^53 + 1: breaks JSON-number consumers
            ("p_ulong", 18446744073709551615ul),
            ("p_float", 0.1f),
            ("p_double", 0.30000000000000004d),       // needs round-trip formatting
            ("p_decimal", 1.2300m),                   // trailing-zero scale must survive
            ("p_char", 'ß'),
            ("p_datetime", new DateTime(2026, 7, 15, 12, 34, 56, 789, DateTimeKind.Utc).AddTicks(1234)),
            ("p_datetimeoffset", new DateTimeOffset(2026, 7, 15, 12, 34, 56, TimeSpan.FromHours(5.5))),
            ("p_timespan", new TimeSpan(1, 2, 3, 4, 5)),
            ("p_guid", Guid.Parse("0b1e4c2e-1111-2222-3333-444455556666"))
        };

        [TestMethod]
        public void EveryAllowListedType_RoundTrips_ValueAndClrTypeExactly()
        {
            foreach (var (key, value) in AllTypedValues)
            {
                Assert.IsTrue(JsonlGraphFormat.TryFormatValue(value, out var typeName, out var formatted),
                    key + " must be formattable");
                Assert.AreEqual(value.GetType().FullName, typeName);

                var parseError = JsonlGraphFormat.TryParseValue(typeName, formatted, out var roundTripped);
                Assert.IsNull(parseError, key + ": " + parseError);
                Assert.AreEqual(value.GetType(), roundTripped.GetType(), key + " must preserve the CLR type");
                Assert.AreEqual(value, roundTripped, key + " must preserve the value exactly");
            }

            // The decimal's scale (trailing zeros) survives - value equality alone would hide it.
            JsonlGraphFormat.TryFormatValue(1.2300m, out _, out var decimalString);
            Assert.AreEqual("1.2300", decimalString);
        }

        [TestMethod]
        public void NullValues_AndNonAllowListedTypes_AreNotFormattable()
        {
            Assert.IsFalse(JsonlGraphFormat.TryFormatValue(null, out _, out _));
            Assert.IsFalse(JsonlGraphFormat.TryFormatValue(new int[] { 1, 2 }, out _, out _));
            Assert.IsFalse(JsonlGraphFormat.TryFormatValue(new object(), out _, out _));
        }

        [TestMethod]
        public void PinnedFormats_ProduceExactlyTheDocumentedStrings()
        {
            // The on-disk version-1 contract: a self-consistent format CHANGE would still pass a
            // round-trip test, silently breaking existing files - so the exact strings are pinned.
            string Format(object value)
            {
                Assert.IsTrue(JsonlGraphFormat.TryFormatValue(value, out _, out var formatted));
                return formatted;
            }

            Assert.AreEqual("0.30000000000000004", Format(0.30000000000000004d)); // Double "R"
            Assert.AreEqual("0.1", Format(0.1f));                                  // Single "R"
            Assert.AreEqual("NaN", Format(double.NaN));
            Assert.AreEqual("-Infinity", Format(double.NegativeInfinity));
            Assert.AreEqual("1.2300", Format(1.2300m));                            // Decimal scale
            Assert.AreEqual("2026-07-15T12:34:56.7890000Z",
                Format(new DateTime(2026, 7, 15, 12, 34, 56, 789, DateTimeKind.Utc)));   // "O"
            Assert.AreEqual("2026-07-15T12:34:56.0000000+05:30",
                Format(new DateTimeOffset(2026, 7, 15, 12, 34, 56, new TimeSpan(5, 30, 0)))); // "O"
            Assert.AreEqual("1.02:03:04.0050000", Format(new TimeSpan(1, 2, 3, 4, 5)));  // "c"
            Assert.AreEqual("0b1e4c2e-1111-2222-3333-444455556666",
                Format(Guid.Parse("0b1e4c2e-1111-2222-3333-444455556666")));             // "D"
            Assert.AreEqual("9007199254740993", Format(9007199254740993L));
            Assert.AreEqual("true", Format(true));
        }

        [TestMethod]
        public void UnpairedSurrogates_AreRejectedAtFormatTime()
        {
            // Invalid UTF-16 would be silently replaced with U+FFFD by the JSON writer, breaking
            // the exact round-trip - so it is refused up front (a 422 at export).
            Assert.IsFalse(JsonlGraphFormat.TryFormatValue('\ud800', out _, out _), "lone surrogate char");
            Assert.IsFalse(JsonlGraphFormat.TryFormatValue("bad\ud800tail", out _, out _), "lone high surrogate in string");
            Assert.IsFalse(JsonlGraphFormat.TryFormatValue("\udc00head", out _, out _), "lone low surrogate in string");
            Assert.IsTrue(JsonlGraphFormat.TryFormatValue("ok 😀 pair", out _, out _), "a valid surrogate PAIR is fine");
        }

        [TestMethod]
        public void TryParseLine_RejectsTheDocumentedErrorShapes()
        {
            string Parse(string line)
            {
                var bytes = new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(line));
                return JsonlGraphFormat.TryParseLine(bytes, out _);
            }

            StringAssert.Contains(Parse("not json"), "malformed JSON");
            StringAssert.Contains(Parse("[1,2]"), "not a JSON object");
            StringAssert.Contains(Parse("{\"id\":1}"), "'type'");
            StringAssert.Contains(Parse("{\"type\":\"hyperedge\"}"), "unknown line type");
            StringAssert.Contains(Parse("{\"type\":\"vertex\",\"id\":1,\"creationDate\":1,\"surprise\":true}"), "unknown field 'surprise'");
            StringAssert.Contains(Parse("{\"type\":\"vertex\",\"creationDate\":1}"), "'id'");
            StringAssert.Contains(Parse("{\"type\":\"edge\",\"id\":1,\"creationDate\":1,\"source\":0,\"target\":1}"), "edgePropertyId");
            StringAssert.Contains(Parse("{\"type\":\"vertex\",\"id\":1,\"creationDate\":1,\"properties\":{\"x\":{\"type\":\"System.Xml.XmlDocument\",\"value\":\"\"}}}"), "not an allow-listed");
            StringAssert.Contains(Parse("{\"type\":\"vertex\",\"id\":1,\"creationDate\":1,\"properties\":{\"x\":{\"type\":\"System.Int32\",\"value\":\"abc\"}}}"), "not a valid Int32");
            StringAssert.Contains(Parse("{\"type\":\"meta\",\"format\":\"fallen8-jsonl\",\"version\":3}"), "unsupported format version");
            StringAssert.Contains(Parse("{\"type\":\"meta\",\"format\":\"other\",\"version\":1}"), "'format'");

            // Council findings: strictness holes that used to slip through.
            StringAssert.Contains(
                Parse("{\"type\":\"vertex\",\"id\":1,\"creationDate\":1} {\"type\":\"vertex\",\"id\":2,\"creationDate\":1}"),
                "malformed JSON", "two objects on one line must never silently drop the second");
            StringAssert.Contains(
                Parse("{\"type\":\"vertex\",\"id\":1,\"creationDate\":1} 42"),
                "malformed JSON", "any trailing content after the object is rejected");
            StringAssert.Contains(
                Parse("{\"type\":\"vertex\",\"id\":1,\"id\":2,\"creationDate\":1}"),
                "duplicate field 'id'", "duplicate top-level keys must not silently last-write-win");
            StringAssert.Contains(
                Parse("{\"type\":\"vertex\",\"id\":1,\"creationDate\":1,\"properties\":{\"x\":{\"type\":\"System.Int32\",\"value\":\"1\"},\"x\":{\"type\":\"System.Int32\",\"value\":\"2\"}}}"),
                "duplicate property key 'x'", "a duplicate property key is a 400, never a 500");
        }

        [TestMethod]
        public void SingleArray_RoundTrips_AndPinsItsGrammar()
        {
            // The version-2 addition: System.Single[] as comma-joined "R" floats - the embedding
            // carrier. The exact strings are pinned like every other type's.
            var vector = new[] { 0.1f, -1.5f, float.NaN, float.PositiveInfinity, 3.4028235E+38f };
            Assert.IsTrue(JsonlGraphFormat.TryFormatValue(vector, out var typeName, out var formatted));
            Assert.AreEqual("System.Single[]", typeName);
            Assert.AreEqual("0.1,-1.5,NaN,Infinity,3.4028235E+38", formatted);

            var parseError = JsonlGraphFormat.TryParseValue(typeName, formatted, out var roundTripped);
            Assert.IsNull(parseError, parseError);
            CollectionAssert.AreEqual(vector, (float[])roundTripped);

            // The empty array is the empty string, exactly, in both directions.
            Assert.IsTrue(JsonlGraphFormat.TryFormatValue(Array.Empty<float>(), out _, out var empty));
            Assert.AreEqual("", empty);
            Assert.IsNull(JsonlGraphFormat.TryParseValue("System.Single[]", "", out var emptyValue));
            Assert.AreEqual(0, ((float[])emptyValue).Length);

            // A malformed component names its position.
            StringAssert.Contains(JsonlGraphFormat.TryParseValue("System.Single[]", "1.0,,2.0", out _), "component 1");
            StringAssert.Contains(JsonlGraphFormat.TryParseValue("System.Single[]", "abc", out _), "component 0");

            // Single[] is the ONE array type; the others stay out.
            Assert.IsFalse(JsonlGraphFormat.TryFormatValue(new double[] { 1d }, out _, out _));
        }

        [TestMethod]
        public void SingleArray_UnderAVersion1Context_IsRejected()
        {
            // A version-1 stamp is a promise to older readers; a file that breaks it is refused.
            var error = JsonlGraphFormat.TryParseValue("System.Single[]", "1,2", out _, formatVersion: 1);
            StringAssert.Contains(error, "requires format version 2");
        }

        #endregion

        #region round trip

        [TestMethod]
        public async Task ExportImportRoundTrip_PreservesStructureAndEveryPropertyType()
        {
            using var source = new BulkFactory();
            var sourceEngine = EngineOf(source);

            // A graph with multiple labels, a self-loop, shared endpoints, and one property of
            // EVERY allow-listed type on one vertex.
            var vtx = new CreateVerticesTransaction();
            vtx.AddVertex(100u, "person", AllTypedValues.ToDictionary(p => p.Key, p => p.Value));
            vtx.AddVertex(200u, "person", new Dictionary<string, object> { { "name", "Bob" } });
            vtx.AddVertex(300u, "company");
            vtx.AddVertex(400u, null); // unlabeled
            sourceEngine.EnqueueTransaction(vtx).WaitUntilFinished();
            var v = vtx.GetCreatedVertices();

            var etx = new CreateEdgesTransaction();
            etx.AddEdge(v[0].Id, "knows", v[1].Id, 500u, "friendship",
                new Dictionary<string, object> { { "since", new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc) }, { "weight", 2.5d } });
            etx.AddEdge(v[0].Id, "self", v[0].Id, 500u, "loop");           // self-loop
            etx.AddEdge(v[1].Id, "works_at", v[2].Id, 500u, null);          // unlabeled edge
            etx.AddEdge(v[3].Id, "knows", v[1].Id, 500u, "friendship");     // shared endpoint
            sourceEngine.EnqueueTransaction(etx).WaitUntilFinished();

            using var sourceClient = source.CreateClient();
            var exported = await sourceClient.GetStringAsync("/bulk/export");

            // Import into a FRESH instance.
            using var target = new BulkFactory();
            using var targetClient = target.CreateClient();
            using var importResponse = await Import(targetClient, exported);
            Assert.AreEqual(HttpStatusCode.OK, importResponse.StatusCode,
                await importResponse.Content.ReadAsStringAsync());

            var targetEngine = EngineOf(target);
            Assert.AreEqual(4, targetEngine.VertexCount);
            Assert.AreEqual(4, targetEngine.EdgeCount);

            // Structural equality: labels, creation dates, full property bags incl. CLR type.
            var importedRich = targetEngine.GetAllVertices("person")
                .First(x => x.GetPropertyCount() == AllTypedValues.Length);
            Assert.AreEqual(100u, importedRich.CreationDate);
            foreach (var (key, value) in AllTypedValues)
            {
                Assert.IsTrue(importedRich.TryGetProperty<object>(out var imported, key), key + " must exist");
                Assert.AreEqual(value.GetType(), imported.GetType(), key + " must keep its CLR type");
                Assert.AreEqual(value, imported, key + " must keep its exact value");
            }

            // Adjacency shape under the id map: the rich vertex has out-edges knows + self.
            Assert.IsTrue(importedRich.TryGetOutEdge(out var knows, "knows"));
            Assert.AreEqual("friendship", knows[0].Label);
            Assert.AreEqual(500u, knows[0].CreationDate, "edge creation dates survive import");
            Assert.IsTrue(knows[0].TryGetProperty<object>(out var since, "since"), "edge properties survive import");
            Assert.AreEqual(new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc), since);
            Assert.IsTrue(knows[0].TryGetProperty<object>(out var weight, "weight"));
            Assert.AreEqual(2.5d, weight);
            Assert.IsTrue(importedRich.TryGetOutEdge(out var self, "self"));
            Assert.AreEqual(importedRich.Id, self[0].TargetVertex.Id, "the self-loop points at its own image");

            // The unlabeled vertex kept its null label and its edge.
            var unlabeled = targetEngine.GetAllVertices().First(x => x.Label == null);
            Assert.IsTrue(unlabeled.TryGetOutEdge(out var sharedTarget, "knows"));
            Assert.AreEqual(1, sharedTarget.Count);
        }

        [TestMethod]
        public async Task Export_ShapeIsMetaThenVerticesThenEdges_WithExactCounts()
        {
            using var factory = new BulkFactory();
            SeedSmallGraph(EngineOf(factory));

            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/bulk/export");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);

            var lines = ParseNdjson(await response.Content.ReadAsStringAsync());
            Assert.AreEqual(4, lines.Count); // meta + 2 vertices + 1 edge

            Assert.AreEqual("meta", lines[0].GetProperty("type").GetString());
            Assert.AreEqual("fallen8-jsonl", lines[0].GetProperty("format").GetString());
            Assert.AreEqual(1, lines[0].GetProperty("version").GetInt32());
            Assert.AreEqual(2, lines[0].GetProperty("vertexCount").GetInt32());
            Assert.AreEqual(1, lines[0].GetProperty("edgeCount").GetInt32());

            Assert.AreEqual("vertex", lines[1].GetProperty("type").GetString());
            Assert.AreEqual("vertex", lines[2].GetProperty("type").GetString());
            Assert.AreEqual("edge", lines[3].GetProperty("type").GetString());
            Assert.AreEqual("knows", lines[3].GetProperty("edgePropertyId").GetString());
        }

        [TestMethod]
        public async Task Export_LabelFilters_AreEndpointConsistent_AndTheSubsetImports()
        {
            using var factory = new BulkFactory();
            var engine = EngineOf(factory);
            SeedSmallGraph(engine); // person -> robot edge

            using var client = factory.CreateClient();

            // vertexLabel=person excludes the robot; the edge to it must be omitted (its target
            // is outside the exported vertex set), keeping the file importable by construction.
            var subset = await client.GetStringAsync("/bulk/export?vertexLabel=person");
            var lines = ParseNdjson(subset);
            Assert.AreEqual(2, lines.Count); // meta + 1 person vertex, edge omitted
            Assert.AreEqual(1, lines[0].GetProperty("vertexCount").GetInt32());
            Assert.AreEqual(0, lines[0].GetProperty("edgeCount").GetInt32());

            using var target = new BulkFactory();
            using var targetClient = target.CreateClient();
            using var import = await Import(targetClient, subset);
            Assert.AreEqual(HttpStatusCode.OK, import.StatusCode);
            Assert.AreEqual(1, EngineOf(target).VertexCount);
        }

        [TestMethod]
        public async Task Export_EdgeLabelFilter_MapsToTheEngineScan()
        {
            using var factory = new BulkFactory();
            var engine = EngineOf(factory);
            var (a, b, _) = SeedSmallGraph(engine); // friendship edge
            var extra = new CreateEdgesTransaction();
            extra.AddEdge(a, "trusts", b, 1u, "trust");
            engine.EnqueueTransaction(extra).WaitUntilFinished();

            using var client = factory.CreateClient();
            var exported = await client.GetStringAsync("/bulk/export?edgeLabel=trust");

            var lines = ParseNdjson(exported);
            Assert.AreEqual(1, lines[0].GetProperty("edgeCount").GetInt32(), "only the trust edge matches");
            var edgeLine = lines.Single(l => l.GetProperty("type").GetString() == "edge");
            Assert.AreEqual("trust", edgeLine.GetProperty("label").GetString());
            Assert.AreEqual("trusts", edgeLine.GetProperty("edgePropertyId").GetString());

            // ...and the filtered file imports.
            using var target = new BulkFactory();
            using var targetClient = target.CreateClient();
            using var import = await Import(targetClient, exported);
            Assert.AreEqual(HttpStatusCode.OK, import.StatusCode);
            Assert.AreEqual(1, EngineOf(target).EdgeCount);
        }

        [TestMethod]
        public async Task Export_EmbeddedGraph_StampsVersion2_AndTheVectorRoundTrips()
        {
            using var source = new BulkFactory();
            var engine = EngineOf(source);
            var vector = new[] { 0.25f, -0.5f, 0.125f };
            var vtx = new CreateVerticesTransaction();
            vtx.AddVertex(1u, "doc", new Dictionary<string, object>
            {
                { "name", "embedded" },
                { "$embedding:default", vector },
                { "$embeddingModel:default", "test-model#3#Cosine" }
            });
            vtx.AddVertex(1u, "doc", new Dictionary<string, object> { { "name", "plain" } });
            engine.EnqueueTransaction(vtx).WaitUntilFinished();

            using var client = source.CreateClient();
            using var response = await client.GetAsync("/bulk/export");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                "an embedded graph must be exportable (the pre-version-2 422 gap)");

            var exported = await response.Content.ReadAsStringAsync();
            var lines = ParseNdjson(exported);
            Assert.AreEqual(2, lines[0].GetProperty("version").GetInt32(), "arrays present -> version 2");

            var embeddedLine = lines.Single(l =>
                l.GetProperty("type").GetString() == "vertex" && l.TryGetProperty("properties", out var p) &&
                p.TryGetProperty("$embedding:default", out _));
            var pair = embeddedLine.GetProperty("properties").GetProperty("$embedding:default");
            Assert.AreEqual("System.Single[]", pair.GetProperty("type").GetString());
            Assert.AreEqual("0.25,-0.5,0.125", pair.GetProperty("value").GetString());

            // The file imports into a fresh instance; the reserved property IS the embedding
            // there (element-embeddings v1 layout), and the model stamp survives next to it.
            using var target = new BulkFactory();
            using var targetClient = target.CreateClient();
            using var import = await Import(targetClient, exported);
            Assert.AreEqual(HttpStatusCode.OK, import.StatusCode, await import.Content.ReadAsStringAsync());

            var imported = EngineOf(target).GetAllVertices("doc").Single(v => v.GetPropertyCount() == 3);
            Assert.IsTrue(imported.TryGetProperty<object>(out var importedVector, "$embedding:default"));
            CollectionAssert.AreEqual(vector, (float[])importedVector, "the vector survives value-exactly");
            Assert.IsTrue(imported.TryGetProperty<object>(out var stamp, "$embeddingModel:default"));
            Assert.AreEqual("test-model#3#Cosine", stamp);
        }

        [TestMethod]
        public async Task Export_WithoutArrays_KeepsStampingVersion1()
        {
            // Pinned separately from the shape test: embedding-free exports must remain readable
            // by pre-version-2 builds - the stamp only escalates when the file needs it.
            using var factory = new BulkFactory();
            SeedSmallGraph(EngineOf(factory));

            using var client = factory.CreateClient();
            var lines = ParseNdjson(await client.GetStringAsync("/bulk/export"));
            Assert.AreEqual(1, lines[0].GetProperty("version").GetInt32());
        }

        [TestMethod]
        public async Task Import_Version1StampedFileWithAnArray_Is400_NamingTheLine()
        {
            const string file =
                "{\"type\":\"meta\",\"format\":\"fallen8-jsonl\",\"version\":1}\n" +
                "{\"type\":\"vertex\",\"id\":1,\"creationDate\":1,\"properties\":{\"$embedding:default\":{\"type\":\"System.Single[]\",\"value\":\"1,2\"}}}\n";

            using var factory = new BulkFactory();
            using var client = factory.CreateClient();
            using var response = await Import(client, file);

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.AreEqual(2, problem.GetProperty("lineNumber").GetInt32());
            StringAssert.Contains(problem.GetProperty("detail").GetString(), "requires format version 2");
        }

        [TestMethod]
        public async Task Import_MetaLessFileWithAnArray_IsAccepted()
        {
            // Grep-filtered subsets drop the meta line; without a version stamp the reader offers
            // the current build's full capability.
            const string file =
                "{\"type\":\"vertex\",\"id\":1,\"label\":\"doc\",\"creationDate\":1,\"properties\":{\"$embedding:default\":{\"type\":\"System.Single[]\",\"value\":\"0.5,1.5\"}}}\n";

            using var factory = new BulkFactory();
            using var client = factory.CreateClient();
            using var response = await Import(client, file);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());

            var imported = EngineOf(factory).GetAllVertices("doc").Single();
            Assert.IsTrue(imported.TryGetProperty<object>(out var vector, "$embedding:default"));
            CollectionAssert.AreEqual(new[] { 0.5f, 1.5f }, (float[])vector);
        }

        #endregion

        #region id remapping

        [TestMethod]
        public async Task Import_RemapsGappyOutOfOrderFileIds_AndWiresEdgesCorrectly()
        {
            const string file =
                "{\"type\":\"vertex\",\"id\":9000,\"label\":\"a\",\"creationDate\":1}\n" +
                "{\"type\":\"vertex\",\"id\":7,\"label\":\"b\",\"creationDate\":1}\n" +
                "{\"type\":\"edge\",\"id\":123456,\"edgePropertyId\":\"knows\",\"source\":9000,\"target\":7,\"creationDate\":1}\n";

            using var factory = new BulkFactory();
            using var client = factory.CreateClient();
            using var response = await Import(client, file);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());

            var engine = EngineOf(factory);
            var a = engine.GetAllVertices("a").Single();
            var b = engine.GetAllVertices("b").Single();
            Assert.IsTrue(a.Id == 0 || a.Id == 1, "engine ids start at 0 regardless of file ids");
            Assert.IsTrue(a.TryGetOutEdge(out var edges, "knows"));
            Assert.AreEqual(b.Id, edges[0].TargetVertex.Id, "the edge wires the IMAGES of its file endpoints");
        }

        [TestMethod]
        public async Task Import_DuplicateFileId_Is400_NamingTheLine()
        {
            const string file =
                "{\"type\":\"vertex\",\"id\":1,\"creationDate\":1}\n" +
                "{\"type\":\"vertex\",\"id\":1,\"creationDate\":1}\n";

            using var factory = new BulkFactory();
            using var client = factory.CreateClient();
            using var response = await Import(client, file);

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.AreEqual(2, problem.GetProperty("lineNumber").GetInt32());
            StringAssert.Contains(problem.GetProperty("detail").GetString(), "duplicate file id 1");
        }

        [TestMethod]
        public async Task Import_InterleavedVertexEdgeFile_Works()
        {
            const string file =
                "{\"type\":\"vertex\",\"id\":1,\"creationDate\":1}\n" +
                "{\"type\":\"vertex\",\"id\":2,\"creationDate\":1}\n" +
                "{\"type\":\"edge\",\"id\":10,\"edgePropertyId\":\"knows\",\"source\":1,\"target\":2,\"creationDate\":1}\n" +
                "{\"type\":\"vertex\",\"id\":3,\"creationDate\":1}\n" +
                "{\"type\":\"edge\",\"id\":11,\"edgePropertyId\":\"knows\",\"source\":2,\"target\":3,\"creationDate\":1}\n";

            using var factory = new BulkFactory();
            using var client = factory.CreateClient();
            using var response = await Import(client, file);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());

            var engine = EngineOf(factory);
            Assert.AreEqual(3, engine.VertexCount);
            Assert.AreEqual(2, engine.EdgeCount);
        }

        #endregion

        #region error contract

        [TestMethod]
        public async Task Import_EdgeToUnknownId_Is400_AndCommittedBatchesRemain()
        {
            // Batch size 2: the first two vertices commit as a batch BEFORE the bad edge line.
            using var factory = new BulkFactory(new Dictionary<string, string>
            {
                ["Fallen8:BulkIO:ImportBatchSize"] = "2"
            });
            const string file =
                "{\"type\":\"vertex\",\"id\":1,\"creationDate\":1}\n" +
                "{\"type\":\"vertex\",\"id\":2,\"creationDate\":1}\n" +
                "{\"type\":\"edge\",\"id\":10,\"edgePropertyId\":\"knows\",\"source\":1,\"target\":999,\"creationDate\":1}\n";

            using var client = factory.CreateClient();
            using var response = await Import(client, file);

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.AreEqual(3, problem.GetProperty("lineNumber").GetInt32());
            StringAssert.Contains(problem.GetProperty("detail").GetString(), "unknown target id 999");
            Assert.AreEqual(2, problem.GetProperty("verticesCommitted").GetInt32(),
                "the committed batch stays committed and the body says so");

            Assert.AreEqual(2, EngineOf(factory).VertexCount);
            Assert.AreEqual(0, EngineOf(factory).EdgeCount);
        }

        [TestMethod]
        public async Task Import_MetaCountMismatch_IsTheTruncationGuard()
        {
            const string file =
                "{\"type\":\"meta\",\"format\":\"fallen8-jsonl\",\"version\":1,\"vertexCount\":3,\"edgeCount\":0}\n" +
                "{\"type\":\"vertex\",\"id\":1,\"creationDate\":1}\n";

            using var factory = new BulkFactory();
            using var client = factory.CreateClient();
            using var response = await Import(client, file);

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            StringAssert.Contains(problem.GetProperty("detail").GetString(), "meta declared 3 vertices but the file produced 1");
        }

        [TestMethod]
        public async Task Import_MetaLineAfterLineOne_Is400()
        {
            const string file =
                "{\"type\":\"vertex\",\"id\":1,\"creationDate\":1}\n" +
                "{\"type\":\"meta\",\"format\":\"fallen8-jsonl\",\"version\":1}\n";

            using var factory = new BulkFactory();
            using var client = factory.CreateClient();
            using var response = await Import(client, file);

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            StringAssert.Contains(problem.GetProperty("detail").GetString(), "only valid as line 1");
        }

        [TestMethod]
        public async Task Import_OverlongLine_Is400_WithItsLineNumber()
        {
            using var factory = new BulkFactory(new Dictionary<string, string>
            {
                ["Fallen8:BulkIO:MaxLineBytes"] = "128"
            });
            var longLine = "{\"type\":\"vertex\",\"id\":1,\"creationDate\":1,\"label\":\"" + new string('x', 500) + "\"}";
            var file = "{\"type\":\"vertex\",\"id\":0,\"creationDate\":1}\n" + longLine + "\n";

            using var client = factory.CreateClient();
            using var response = await Import(client, file);

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.AreEqual(2, problem.GetProperty("lineNumber").GetInt32());
            StringAssert.Contains(problem.GetProperty("title").GetString(), "too long");
        }

        [TestMethod]
        public async Task Import_MalformedJsonLine_Is400ProblemJson_ThroughTheFullPipeline()
        {
            using var factory = new BulkFactory();
            using var client = factory.CreateClient();
            using var response = await Import(client,
                "{\"type\":\"vertex\",\"id\":1,\"creationDate\":1}\nnot json at all\n");

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.AreEqual(2, problem.GetProperty("lineNumber").GetInt32());
            StringAssert.Contains(problem.GetProperty("detail").GetString(), "malformed JSON");
            Assert.IsTrue(problem.TryGetProperty("verticesCommitted", out _), "committed counts are always reported");
        }

        [TestMethod]
        public async Task Import_WrongContentType_Is415()
        {
            using var factory = new BulkFactory();
            using var client = factory.CreateClient();
            using var response = await client.PostAsync("/bulk/import",
                new StringContent("{\"type\":\"vertex\",\"id\":1,\"creationDate\":1}", Encoding.UTF8, "application/json"));

            Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        }

        [TestMethod]
        public void ImportBatchRollback_MapsEveryFailureReasonToItsStatus()
        {
            // An engine-rolled-back batch is UNREACHABLE through the import path by construction
            // (the id map pre-resolves edge endpoints before the engine sees them, and parsed
            // vertex lines cannot produce an invalid batch), and TestServer buffers request
            // bodies, so a mid-request race cannot be staged. The mapping itself - the piece
            // that would rot - is pinned here via the private factory (reflection: no
            // InternalsVisibleTo).
            var importError = typeof(NoSQL.GraphDB.App.Controllers.BulkController)
                .GetNestedType("ImportError", System.Reflection.BindingFlags.NonPublic);
            var batch = importError.GetMethod("Batch",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var statusField = importError.GetField("Status", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var detailField = importError.GetField("Detail", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            (int Status, string Detail) Map(TransactionFailureReason reason)
            {
                var error = batch.Invoke(null, new object[] { 42L, reason, "edge" });
                return ((int)statusField.GetValue(error), (string)detailField.GetValue(error));
            }

            Assert.AreEqual(400, Map(TransactionFailureReason.InvalidInput).Status);
            Assert.AreEqual(400, Map(TransactionFailureReason.NotFound).Status);
            Assert.AreEqual(409, Map(TransactionFailureReason.QuotaExceeded).Status);
            Assert.AreEqual(409, Map(TransactionFailureReason.Conflict).Status);
            Assert.AreEqual(500, Map(TransactionFailureReason.InternalError).Status);

            var detail = Map(TransactionFailureReason.NotFound).Detail;
            StringAssert.Contains(detail, "line 42");
            StringAssert.Contains(detail, "NotFound");
            StringAssert.Contains(detail, "remain committed");
        }

        [TestMethod]
        public async Task Import_NonEmptyGraph_Is409_WithNothingMutated()
        {
            using var factory = new BulkFactory();
            SeedSmallGraph(EngineOf(factory));
            var before = EngineOf(factory).VertexCount;

            using var client = factory.CreateClient();
            using var response = await Import(client, "{\"type\":\"vertex\",\"id\":1,\"creationDate\":1}\n");

            Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
            Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.AreEqual(before, EngineOf(factory).VertexCount, "nothing was parsed or mutated");
        }

        #endregion

        #region batching + durability

        [TestMethod]
        public async Task Import_LargerThanTwoBatches_CommitsInMultipleTransactions()
        {
            using var factory = new BulkFactory(new Dictionary<string, string>
            {
                ["Fallen8:BulkIO:ImportBatchSize"] = "3"
            });

            var file = new StringBuilder();
            for (var i = 0; i < 8; i++)
            {
                file.Append("{\"type\":\"vertex\",\"id\":").Append(i).Append(",\"creationDate\":1}\n");
            }

            using var client = factory.CreateClient();
            using var response = await Import(client, file.ToString());
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

            var summary = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.AreEqual(8, summary.GetProperty("verticesCreated").GetInt32());
            Assert.AreEqual(8, EngineOf(factory).VertexCount);
        }

        [TestMethod]
        public async Task Import_MidFileFailureUnderWal_LeavesExactlyTheCommittedBatches_AndReplayAgrees()
        {
            var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "f8_bulkwalfail_" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(tempDir);
            var walPath = System.IO.Path.Combine(tempDir, "bulkfail.wal");
            try
            {
                using (var factory = new BulkFactory(new Dictionary<string, string>
                {
                    ["Fallen8:Durability:Volatile"] = "false",
                    ["Fallen8:Durability:StorageDirectory"] = tempDir,
                    ["Fallen8:Durability:WalPath"] = walPath,
                    ["Fallen8:Durability:SaveOnShutdown"] = "false",
                    ["Fallen8:Metadata:Directory"] = System.IO.Path.Combine(tempDir, "metadata"),
                    ["Fallen8:BulkIO:ImportBatchSize"] = "2"
                }))
                {
                    // 5 vertices at batch size 2: batches (v1,v2) and (v3,v4) commit; v5 is
                    // pending and never flushed when line 6 fails - proving batch-wise commits
                    // AND the honest partial-import contract in one scenario.
                    const string file =
                        "{\"type\":\"vertex\",\"id\":1,\"creationDate\":1}\n" +
                        "{\"type\":\"vertex\",\"id\":2,\"creationDate\":1}\n" +
                        "{\"type\":\"vertex\",\"id\":3,\"creationDate\":1}\n" +
                        "{\"type\":\"vertex\",\"id\":4,\"creationDate\":1}\n" +
                        "{\"type\":\"vertex\",\"id\":5,\"creationDate\":1}\n" +
                        "this is not json\n";

                    using var client = factory.CreateClient();
                    using var response = await Import(client, file);
                    Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
                    var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
                    Assert.AreEqual(6, problem.GetProperty("lineNumber").GetInt32());
                    Assert.AreEqual(4, problem.GetProperty("verticesCommitted").GetInt32(),
                        "two full batches committed; the pending fifth vertex was never flushed");
                    Assert.AreEqual(4, EngineOf(factory).VertexCount, "live state agrees with the report");
                } // crash: no shutdown save - the WAL alone carries the committed batches

                using var recovered = new Fallen8(TestLoggerFactory.Create(), new WriteAheadLogOptions(walPath));
                Assert.AreEqual(4, recovered.VertexCount,
                    "WAL replay reproduces exactly the committed batches - state and replay agree");
                Assert.AreEqual(0, recovered.EdgeCount);
            }
            finally
            {
                try { System.IO.Directory.Delete(tempDir, true); } catch { /* best effort */ }
            }
        }

        [TestMethod]
        public async Task Import_WithWalEnabled_ReplaysToTheSameGraph_PerCommittedBatch()
        {
            var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "f8_bulkwal_" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(tempDir);
            var walPath = System.IO.Path.Combine(tempDir, "bulk.wal");
            try
            {
                using (var factory = new BulkFactory(new Dictionary<string, string>
                {
                    ["Fallen8:Durability:Volatile"] = "false",
                    ["Fallen8:Durability:StorageDirectory"] = tempDir,
                    ["Fallen8:Durability:WalPath"] = walPath,
                    ["Fallen8:Durability:SaveOnShutdown"] = "false",
                    ["Fallen8:Metadata:Directory"] = System.IO.Path.Combine(tempDir, "metadata"),
                    ["Fallen8:BulkIO:ImportBatchSize"] = "2"
                }))
                {
                    const string file =
                        "{\"type\":\"vertex\",\"id\":1,\"label\":\"a\",\"creationDate\":1}\n" +
                        "{\"type\":\"vertex\",\"id\":2,\"label\":\"b\",\"creationDate\":1}\n" +
                        "{\"type\":\"vertex\",\"id\":3,\"label\":\"c\",\"creationDate\":1}\n" +
                        "{\"type\":\"edge\",\"id\":10,\"edgePropertyId\":\"knows\",\"source\":1,\"target\":3,\"creationDate\":1}\n";

                    using var client = factory.CreateClient();
                    using var response = await Import(client, file);
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
                } // "crash": no shutdown save; the WAL alone carries the import

                using var recovered = new Fallen8(TestLoggerFactory.Create(), new WriteAheadLogOptions(walPath));
                Assert.AreEqual(3, recovered.VertexCount, "each import batch is an ordinary logged transaction");
                Assert.AreEqual(1, recovered.EdgeCount);
                var a = recovered.GetAllVertices("a").Single();
                Assert.IsTrue(a.TryGetOutEdge(out var edges, "knows"));
                Assert.AreEqual("c", edges[0].TargetVertex.Label);
            }
            finally
            {
                try { System.IO.Directory.Delete(tempDir, true); } catch { /* best effort */ }
            }
        }

        #endregion

        #region export consistency under concurrent writes

        [TestMethod]
        public async Task Export_UnderConcurrentWrites_StaysInternallyConsistent_AndImports()
        {
            using var factory = new BulkFactory();
            var engine = EngineOf(factory);

            // Seed a base graph.
            var seed = new CreateVerticesTransaction();
            for (var i = 0; i < 2000; i++)
            {
                seed.AddVertex(1u, "person");
            }
            engine.EnqueueTransaction(seed).WaitUntilFinished();
            var seeded = seed.GetCreatedVertices();
            var seedEdges = new CreateEdgesTransaction();
            for (var i = 0; i < 1999; i++)
            {
                seedEdges.AddEdge(seeded[i].Id, "knows", seeded[i + 1].Id, 1u, "knows");
            }
            engine.EnqueueTransaction(seedEdges).WaitUntilFinished();

            // A writer keeps committing vertices+edges while the export streams.
            var stop = false;
            var writer = Task.Run(() =>
            {
                while (!Volatile.Read(ref stop))
                {
                    var vtx = new CreateVerticesTransaction();
                    vtx.AddVertex(1u, "late");
                    vtx.AddVertex(1u, "late");
                    engine.EnqueueTransaction(vtx).WaitUntilFinished();
                    var v = vtx.GetCreatedVertices();
                    var etx = new CreateEdgesTransaction();
                    etx.AddEdge(v[0].Id, "knows", v[1].Id, 1u, "knows");
                    engine.EnqueueTransaction(etx).WaitUntilFinished();
                }
            });

            using var client = factory.CreateClient();
            string exported;
            try
            {
                exported = await client.GetStringAsync("/bulk/export");
            }
            finally
            {
                Volatile.Write(ref stop, true);
                await writer;
            }

            // Internal consistency: every edge line's endpoints resolve to vertex lines in the
            // SAME file, and the meta counts equal the actual line counts.
            var lines = ParseNdjson(exported);
            var meta = lines[0];
            var vertexIds = new HashSet<int>();
            var edgeLines = new List<JsonElement>();
            foreach (var line in lines.Skip(1))
            {
                if (line.GetProperty("type").GetString() == "vertex")
                {
                    vertexIds.Add(line.GetProperty("id").GetInt32());
                }
                else
                {
                    edgeLines.Add(line);
                }
            }

            Assert.AreEqual(meta.GetProperty("vertexCount").GetInt32(), vertexIds.Count);
            Assert.AreEqual(meta.GetProperty("edgeCount").GetInt32(), edgeLines.Count);
            foreach (var edge in edgeLines)
            {
                Assert.IsTrue(vertexIds.Contains(edge.GetProperty("source").GetInt32()), "source resolves in-file");
                Assert.IsTrue(vertexIds.Contains(edge.GetProperty("target").GetInt32()), "target resolves in-file");
            }

            // ...and the file imports cleanly into a fresh instance.
            using var target = new BulkFactory();
            using var targetClient = target.CreateClient();
            using var import = await Import(targetClient, exported);
            Assert.AreEqual(HttpStatusCode.OK, import.StatusCode, await import.Content.ReadAsStringAsync());
            Assert.AreEqual(vertexIds.Count, EngineOf(target).VertexCount);
        }

        #endregion

        #region fidelity refusal + posture

        [TestMethod]
        public async Task Export_NonAllowListedEngineProperty_Is422_BeforeAnyStreaming()
        {
            using var factory = new BulkFactory();
            var engine = EngineOf(factory);
            var vtx = new CreateVerticesTransaction();
            vtx.AddVertex(1u, "person", new Dictionary<string, object> { { "blob", new int[] { 1, 2, 3 } } });
            engine.EnqueueTransaction(vtx).WaitUntilFinished();

            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/bulk/export");

            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType,
                "the failure is a problem body, never a half-written NDJSON file");
            var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.AreEqual("blob", problem.GetProperty("propertyKey").GetString());
        }

        [TestMethod]
        public async Task Posture_ApiKeyGatesBothEndpoints_AndTheBodyCapReturns413()
        {
            using (var secured = new BulkFactory(new Dictionary<string, string>
            {
                ["Fallen8:Security:ApiKey"] = "bulk-key"
            }))
            using (var anonymous = secured.CreateClient())
            {
                using var export = await anonymous.GetAsync("/bulk/export");
                Assert.AreEqual(HttpStatusCode.Unauthorized, export.StatusCode);

                using var import = await Import(anonymous, "{}");
                Assert.AreEqual(HttpStatusCode.Unauthorized, import.StatusCode);
            }

            using var capped = new BulkFactory(new Dictionary<string, string>
            {
                ["Fallen8:BulkIO:MaxImportRequestBytes"] = "64"
            });
            using var client = capped.CreateClient();
            var oversize = new string('x', 200);
            using var response = await Import(client,
                "{\"type\":\"vertex\",\"id\":1,\"creationDate\":1,\"label\":\"" + oversize + "\"}\n");
            Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }

        #endregion
    }
}
