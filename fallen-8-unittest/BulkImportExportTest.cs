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
            StringAssert.Contains(Parse("{\"type\":\"meta\",\"format\":\"fallen8-jsonl\",\"version\":2}"), "unsupported format version");
            StringAssert.Contains(Parse("{\"type\":\"meta\",\"format\":\"other\",\"version\":1}"), "'format'");
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
            etx.AddEdge(v[0].Id, "knows", v[1].Id, 500u, "friendship");
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
