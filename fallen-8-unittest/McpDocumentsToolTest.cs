// MIT License
//
// McpDocumentsToolTest.cs
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
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Mcp.Configuration;
using NoSQL.GraphDB.Mcp.Tools;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Feature unstructured-ingestion FR-10: the <c>f8_documents</c> tool end to end against
    ///   a real hosted apiApp (ingest, fused search, list, get, delete), the per-op write-tier
    ///   gating, and the schema honesty (write ops absent while the tier is off).
    /// </summary>
    [TestClass]
    public class McpDocumentsToolTest
    {
        /// <summary>Binds the semantic layer on the target apiApp (FR-7) so the tool's ingest is
        /// accepted. The MCP binding op is covered separately; here it is plain setup.</summary>
        private static async Task SeedBinding(IngestionFactory api)
        {
            using var client = api.CreateClient();
            using var response = await client.PostAsync("/document/binding/ensure", null);
            Assert.IsTrue(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        }

        private static JsonElement Str(String value) => JsonSerializer.SerializeToElement(value);

        private static JsonElement Num(Int32 value) => JsonSerializer.SerializeToElement(value);

        private static Dictionary<String, JsonElement> Args(params (String Key, JsonElement Value)[] entries)
        {
            var args = new Dictionary<String, JsonElement>();
            foreach (var (key, value) in entries)
            {
                args[key] = value;
            }

            return args;
        }

        [TestMethod]
        public async Task RoundTrip_IngestSearchGetDelete_AgainstALiveApiApp()
        {
            using var api = new IngestionFactory();
            var bridge = McpTestSupport.Bridge(api.Server.CreateHandler());
            var tools = new McpToolsOptions { EnableWrite = true };
            var catalog = McpTestSupport.Catalog(tools, new IMcpTool[] { new DocumentsTool(bridge) });

            // bind: the semantic layer creates no indices implicitly - bind through the tool first.
            var bind = await catalog.CallAsync("f8_documents", Args(("op", Str("bind"))), CancellationToken.None);
            Assert.IsFalse(bind.IsError, bind.Content?.OfType<ModelContextProtocol.Protocol.TextContentBlock>().FirstOrDefault()?.Text);
            Assert.IsTrue(bind.StructuredContent!.Value.GetProperty("ready").GetBoolean());

            // ingest_text
            var ingest = await catalog.CallAsync("f8_documents", Args(
                ("op", Str("ingest_text")),
                ("name", Str("edge-notes.md")),
                ("text", Str("# Edge\n\nThe EDGE_TLS_01 box terminates tls for the shop."))),
                CancellationToken.None);
            Assert.IsFalse(ingest.IsError, ingest.Content?.OfType<ModelContextProtocol.Protocol.TextContentBlock>().FirstOrDefault()?.Text);
            var documentId = ingest.StructuredContent!.Value.GetProperty("documentId").GetInt32();
            // Ingestion is async: the stub is accepted as `processing`; poll get until it indexes.
            Assert.AreEqual("processing", ingest.StructuredContent!.Value.GetProperty("status").GetString());

            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                var poll = await catalog.CallAsync("f8_documents", Args(
                    ("op", Str("get")), ("documentId", Num(documentId))), CancellationToken.None);
                if (poll.IsError != true &&
                    poll.StructuredContent!.Value.GetProperty("summary").GetProperty("status").GetString() == "indexed")
                {
                    break;
                }

                await Task.Delay(50);
            }

            // search (fused): the identifier query lands on the ingested chunk.
            var search = await catalog.CallAsync("f8_documents", Args(
                ("op", Str("search")),
                ("query", Str("EDGE_TLS_01")),
                ("k", Num(3))),
                CancellationToken.None);
            Assert.IsFalse(search.IsError);
            var hits = search.StructuredContent!.Value.GetProperty("hits");
            Assert.IsTrue(hits.GetArrayLength() >= 1);
            StringAssert.Contains(hits[0].GetProperty("text").GetString(), "EDGE_TLS_01");

            // list
            var list = await catalog.CallAsync("f8_documents", Args(("op", Str("list"))), CancellationToken.None);
            Assert.IsFalse(list.IsError);
            Assert.AreEqual(1, list.StructuredContent!.Value.GetProperty("documents").GetArrayLength());

            // get
            var get = await catalog.CallAsync("f8_documents", Args(
                ("op", Str("get")), ("documentId", Num(documentId))), CancellationToken.None);
            Assert.IsFalse(get.IsError);
            Assert.AreEqual("edge-notes.md",
                get.StructuredContent!.Value.GetProperty("summary").GetProperty("name").GetString());

            // delete
            var delete = await catalog.CallAsync("f8_documents", Args(
                ("op", Str("delete")), ("documentId", Num(documentId))), CancellationToken.None);
            Assert.IsFalse(delete.IsError);

            var afterDelete = await catalog.CallAsync("f8_documents", Args(("op", Str("list"))), CancellationToken.None);
            Assert.AreEqual(0, afterDelete.StructuredContent!.Value.GetProperty("documents").GetArrayLength());
        }

        [TestMethod]
        public async Task Binding_ReadIsOpen_BindNeedsWriteTier()
        {
            using var api = new IngestionFactory();
            var bridge = McpTestSupport.Bridge(api.Server.CreateHandler());

            // Read tier: the binding state is visible, and reports NOT ready before any bind.
            var readCatalog = McpTestSupport.Catalog(new McpToolsOptions(), new IMcpTool[] { new DocumentsTool(bridge) });
            var state = await readCatalog.CallAsync("f8_documents", Args(("op", Str("binding"))), CancellationToken.None);
            Assert.IsFalse(state.IsError);
            Assert.IsFalse(state.StructuredContent!.Value.GetProperty("ready").GetBoolean());

            // bind is a write op: hidden from the advertised op set and refused without the tier.
            var tool = new DocumentsTool(bridge);
            var readOnly = new McpToolsOptions();
            var schema = JsonSerializer.SerializeToElement(tool.Describe(readOnly).InputSchema);
            var ops = schema.GetProperty("properties").GetProperty("op").GetProperty("enum")
                .EnumerateArray().Select(e => e.GetString()).ToList();
            CollectionAssert.Contains(ops, "binding");
            CollectionAssert.DoesNotContain(ops, "bind");

            var refused = await readCatalog.CallAsync("f8_documents", Args(("op", Str("bind"))), CancellationToken.None);
            Assert.IsTrue(refused.IsError, "bind without the write tier is rejected");

            // With the write tier, bind makes the layer ready.
            var writeCatalog = McpTestSupport.Catalog(new McpToolsOptions { EnableWrite = true },
                new IMcpTool[] { new DocumentsTool(bridge) });
            var bound = await writeCatalog.CallAsync("f8_documents", Args(("op", Str("bind"))), CancellationToken.None);
            Assert.IsFalse(bound.IsError);
            Assert.IsTrue(bound.StructuredContent!.Value.GetProperty("ready").GetBoolean());
        }

        [TestMethod]
        public async Task WriteOps_AreGatedAndHidden_WithoutTheWriteTier()
        {
            using var api = new IngestionFactory();
            await SeedBinding(api);
            var bridge = McpTestSupport.Bridge(api.Server.CreateHandler());

            // First ingest a REAL document WITH the write tier, so the gate test below deletes
            // an id that would otherwise succeed - not a missing id that 404s regardless (the
            // vacuity the council flagged).
            var writeCatalog = McpTestSupport.Catalog(new McpToolsOptions { EnableWrite = true },
                new IMcpTool[] { new DocumentsTool(bridge) });
            var ingested = await writeCatalog.CallAsync("f8_documents", Args(
                ("op", Str("ingest_text")), ("name", Str("gated.md")), ("text", Str("# H\n\nbody"))),
                CancellationToken.None);
            Assert.IsFalse(ingested.IsError);
            var documentId = ingested.StructuredContent!.Value.GetProperty("documentId").GetInt32();

            var tools = new McpToolsOptions();  // write off
            var tool = new DocumentsTool(bridge);
            var catalog = McpTestSupport.Catalog(tools, new IMcpTool[] { tool });

            // The advertised op set matches what the caller may actually do.
            var schema = JsonSerializer.SerializeToElement(tool.Describe(tools).InputSchema);
            var ops = schema.GetProperty("properties").GetProperty("op").GetProperty("enum")
                .EnumerateArray().Select(e => e.GetString()).ToList();
            CollectionAssert.AreEquivalent(new List<String> { "list", "get", "search", "binding" }, ops);

            var ingest = await catalog.CallAsync("f8_documents", Args(
                ("op", Str("ingest_text")), ("name", Str("n")), ("text", Str("t"))), CancellationToken.None);
            Assert.IsTrue(ingest.IsError, "ingest_text without the write tier is rejected");
            AssertMentionsWriteCapability(ingest);

            // Delete the REAL id: rejected by the gate, and the document must survive (proving
            // the gate blocked a delete that would otherwise have succeeded).
            var delete = await catalog.CallAsync("f8_documents", Args(
                ("op", Str("delete")), ("documentId", Num(documentId))), CancellationToken.None);
            Assert.IsTrue(delete.IsError, "delete without the write tier is rejected");
            AssertMentionsWriteCapability(delete);

            var stillThere = await writeCatalog.CallAsync("f8_documents", Args(
                ("op", Str("get")), ("documentId", Num(documentId))), CancellationToken.None);
            Assert.IsFalse(stillThere.IsError, "the gated delete must not have removed the document");
        }

        private static void AssertMentionsWriteCapability(ModelContextProtocol.Protocol.CallToolResult result)
        {
            var text = String.Join(" ",
                result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(b => b.Text));
            StringAssert.Contains(text, "write capability",
                "the rejection must be the tier gate (403), not an incidental error");
        }

        [TestMethod]
        public async Task InvalidArguments_AreToolErrors_NotExceptions()
        {
            using var api = new IngestionFactory();
            var bridge = McpTestSupport.Bridge(api.Server.CreateHandler());
            var catalog = McpTestSupport.Catalog(new McpToolsOptions { EnableWrite = true },
                new IMcpTool[] { new DocumentsTool(bridge) });

            var noQuery = await catalog.CallAsync("f8_documents", Args(("op", Str("search"))), CancellationToken.None);
            Assert.IsTrue(noQuery.IsError);

            var noId = await catalog.CallAsync("f8_documents", Args(("op", Str("get"))), CancellationToken.None);
            Assert.IsTrue(noId.IsError);

            var badOp = await catalog.CallAsync("f8_documents", Args(("op", Str("upload"))), CancellationToken.None);
            Assert.IsTrue(badOp.IsError);
        }

        [TestMethod]
        public async Task TargetCapabilityOff_SurfacesTheTargets403()
        {
            using var api = new IngestionFactory(new Dictionary<String, String>
            {
                { "Fallen8:Ingestion:Enabled", "false" }
            });
            var bridge = McpTestSupport.Bridge(api.Server.CreateHandler());
            var catalog = McpTestSupport.Catalog(new McpToolsOptions(), new IMcpTool[] { new DocumentsTool(bridge) });

            var list = await catalog.CallAsync("f8_documents", Args(("op", Str("list"))), CancellationToken.None);
            Assert.IsTrue(list.IsError, "the target's 403 (ingestion disabled) surfaces as a tool error");
        }
    }
}
