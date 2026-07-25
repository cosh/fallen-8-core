// MIT License
//
// McpWriteToolsTest.cs
//
// Copyright (c) 2026 Henning Rauch
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
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Mcp.Configuration;
using NoSQL.GraphDB.Mcp.Tools;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The Phase 2 write/admin tiers and the code capability (feature mcp-server §3.2/§3.6):
    ///   the tier-gating matrix (a disabled tier's tools are absent from tools/list AND rejected
    ///   on call; code widens params, not tools) and write round-trips through the ToolCatalog
    ///   into a real hosted apiApp.
    /// </summary>
    [TestClass]
    public class McpWriteToolsTest
    {
        private static readonly IReadOnlyDictionary<String, JsonElement> NoArgs = new Dictionary<String, JsonElement>();

        private static ToolCatalog DummyCatalog(McpToolsOptions tools)
        {
            var bridge = McpTestSupport.Bridge(new McpTestSupport.LambdaHandler(
                _ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)));
            return McpTestSupport.Catalog(tools, McpTestSupport.AllTools(bridge));
        }

        // --- tier gating matrix -------------------------------------------------------------

        [TestMethod]
        public void DefaultTiers_ListsReadToolsOnly()
        {
            var names = DummyCatalog(new McpToolsOptions()).ListTools().Select(t => t.Name).ToHashSet();

            foreach (var read in new[] { "f8_overview", "f8_get", "f8_search", "f8_paths", "f8_analytics" })
            {
                Assert.IsTrue(names.Contains(read), read + " is a default read tool");
            }
            foreach (var gated in new[] { "f8_mutate", "f8_subgraph", "f8_namespace", "f8_admin" })
            {
                Assert.IsFalse(names.Contains(gated), gated + " is absent when its tier is off");
            }
        }

        [TestMethod]
        public void WriteEnabled_AddsWriteTools_ButNotAdmin()
        {
            var names = DummyCatalog(new McpToolsOptions { EnableWrite = true }).ListTools().Select(t => t.Name).ToHashSet();

            foreach (var w in new[] { "f8_mutate", "f8_subgraph", "f8_namespace" })
            {
                Assert.IsTrue(names.Contains(w), w + " appears with the write tier on");
            }
            Assert.IsFalse(names.Contains("f8_admin"), "admin stays gated behind EnableAdmin");
        }

        [TestMethod]
        public void AdminEnabled_AddsAdminTool()
        {
            var names = DummyCatalog(new McpToolsOptions { EnableAdmin = true }).ListTools().Select(t => t.Name).ToHashSet();
            Assert.IsTrue(names.Contains("f8_admin"));
        }

        [TestMethod]
        public async Task CallMutate_WriteDisabled_IsRejected()
        {
            var result = await DummyCatalog(new McpToolsOptions())
                .CallAsync("f8_mutate", McpTestSupport.Args("{\"op\":\"create_vertex\"}"), CancellationToken.None);
            Assert.IsTrue(result.IsError, "f8_mutate must be rejected when the write tier is off");
        }

        [TestMethod]
        public void CodeCapability_WidensPathParams_OnlyWhenEnabled()
        {
            var withoutCode = DummyCatalog(new McpToolsOptions()).ListTools().Single(t => t.Name == "f8_paths");
            Assert.IsFalse(withoutCode.InputSchema.GetRawText().Contains("vertexFilter", StringComparison.Ordinal),
                "the code fragment params are absent (cost no tokens) when the capability is off");

            var withCode = DummyCatalog(new McpToolsOptions { EnableCode = true }).ListTools().Single(t => t.Name == "f8_paths");
            StringAssert.Contains(withCode.InputSchema.GetRawText(), "vertexFilter",
                "the code capability widens f8_paths with inline fragment params");
        }

        [TestMethod]
        public void NamespaceAndAdmin_CarryDestructiveHint()
        {
            var caps = new McpToolsOptions { EnableWrite = true, EnableAdmin = true };
            var tools = DummyCatalog(caps).ListTools().ToDictionary(t => t.Name);

            Assert.AreEqual(true, tools["f8_namespace"].Annotations!.DestructiveHint, "f8_namespace can drop → destructive");
            Assert.AreEqual(true, tools["f8_admin"].Annotations!.DestructiveHint, "f8_admin can load/trim/tabula_rasa → destructive");
            Assert.AreNotEqual(true, tools["f8_mutate"].Annotations!.DestructiveHint, "f8_mutate is not blanket-destructive");
        }

        // --- write round-trips (through the catalog into a real hosted apiApp) --------------

        private sealed class ApiAppFactory : WebApplicationFactory<NoSQL.GraphDB.App.Program>
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseEnvironment("Development");
                builder.UseSetting("Fallen8:Durability:Volatile", "true");
            }
        }

        private static ToolCatalog WriteCatalog(ApiAppFactory api)
        {
            var bridge = McpTestSupport.Bridge(api.Server.CreateHandler());
            return McpTestSupport.Catalog(new McpToolsOptions { EnableWrite = true, EnableAdmin = true }, McpTestSupport.AllTools(bridge));
        }

        [TestMethod]
        public async Task Mutate_CreateVertex_ThenFindByPropertyScan()
        {
            using var api = new ApiAppFactory();
            var catalog = WriteCatalog(api);

            var create = await catalog.CallAsync("f8_mutate",
                McpTestSupport.Args("{\"op\":\"create_vertex\",\"label\":\"person\",\"properties\":{\"name\":\"Zoe\",\"age\":29}}"),
                CancellationToken.None);
            Assert.IsTrue(McpTestSupport.Structured(create).GetProperty("applied").GetBoolean());

            // Create returns no id (REST 202) — the honest recipe is to find it by search.
            var search = await catalog.CallAsync("f8_search",
                McpTestSupport.Args("{\"mode\":\"property\",\"key\":\"name\",\"value\":\"Zoe\",\"kind\":\"vertex\"}"),
                CancellationToken.None);
            var items = McpTestSupport.Structured(search).GetProperty("items");
            Assert.AreEqual(1, items.GetArrayLength(), "the created vertex is found by an un-indexed property scan");
        }

        [TestMethod]
        public async Task Mutate_SetProperty_ThenReadBack()
        {
            using var api = new ApiAppFactory();
            var catalog = WriteCatalog(api);

            await catalog.CallAsync("f8_mutate",
                McpTestSupport.Args("{\"op\":\"create_vertex\",\"label\":\"person\",\"properties\":{\"name\":\"Ivy\"}}"),
                CancellationToken.None);
            var id = (await FindByName(catalog, "Ivy")) ?? throw new AssertFailedException("seeded vertex not found");

            var set = await catalog.CallAsync("f8_mutate",
                McpTestSupport.Args($"{{\"op\":\"set_property\",\"id\":{id},\"key\":\"city\",\"value\":\"Berlin\"}}"),
                CancellationToken.None);
            Assert.IsTrue(McpTestSupport.Structured(set).GetProperty("applied").GetBoolean());

            var get = await catalog.CallAsync("f8_get", McpTestSupport.Args($"{{\"kind\":\"vertex\",\"id\":{id}}}"), CancellationToken.None);
            Assert.AreEqual("Berlin", McpTestSupport.Structured(get).GetProperty("properties").GetProperty("city").GetString());
        }

        [TestMethod]
        public async Task Mutate_RemoveElement_HonestSemantics_NoOpVsOutOfRange()
        {
            using var api = new ApiAppFactory();
            var catalog = WriteCatalog(api);

            await catalog.CallAsync("f8_mutate",
                McpTestSupport.Args("{\"op\":\"create_vertex\",\"label\":\"person\",\"properties\":{\"name\":\"Temp\"}}"),
                CancellationToken.None);
            var id = (await FindByName(catalog, "Temp")) ?? throw new AssertFailedException("seeded vertex not found");

            // First removal applies.
            var first = await catalog.CallAsync("f8_mutate",
                McpTestSupport.Args($"{{\"op\":\"remove_element\",\"id\":{id}}}"), CancellationToken.None);
            Assert.IsTrue(McpTestSupport.Structured(first).GetProperty("applied").GetBoolean());

            // §3.7: removing the now-absent-but-IN-RANGE id again is a committed no-op → applied, not an error.
            var again = await catalog.CallAsync("f8_mutate",
                McpTestSupport.Args($"{{\"op\":\"remove_element\",\"id\":{id}}}"), CancellationToken.None);
            Assert.IsTrue(McpTestSupport.Structured(again).GetProperty("applied").GetBoolean(),
                "an absent-but-in-range id is a no-op success, not a not-found");

            // §3.7: an OUT-OF-RANGE id rolls back → surfaced as a tool error (not a fake success).
            var outOfRange = await catalog.CallAsync("f8_mutate",
                McpTestSupport.Args("{\"op\":\"remove_element\",\"id\":999999}"), CancellationToken.None);
            Assert.IsTrue(outOfRange.IsError, "an out-of-range id rolls back and surfaces as a tool error");
        }

        [TestMethod]
        public async Task Namespace_CreateListDrop_RoundTrip()
        {
            using var api = new ApiAppFactory();
            var catalog = WriteCatalog(api);

            var created = await catalog.CallAsync("f8_namespace",
                McpTestSupport.Args("{\"op\":\"create\",\"name\":\"scratch\"}"), CancellationToken.None);
            Assert.IsFalse(created.IsError, "namespace create succeeds");

            var overview = await catalog.CallAsync("f8_overview", NoArgs, CancellationToken.None);
            var names = McpTestSupport.Structured(overview).GetProperty("namespaces").EnumerateArray()
                .Select(n => n.GetProperty("name").GetString()).ToHashSet();
            Assert.IsTrue(names.Contains("scratch"), "the new namespace shows in the directory");

            var dropped = await catalog.CallAsync("f8_namespace",
                McpTestSupport.Args("{\"op\":\"drop\",\"name\":\"scratch\"}"), CancellationToken.None);
            Assert.IsTrue(McpTestSupport.Structured(dropped).GetProperty("dropped").GetBoolean());
        }

        [TestMethod]
        public async Task Namespace_ScopedMutation_LandsInThatNamespace()
        {
            using var api = new ApiAppFactory();
            var catalog = WriteCatalog(api);

            await catalog.CallAsync("f8_namespace", McpTestSupport.Args("{\"op\":\"create\",\"name\":\"tenantA\"}"), CancellationToken.None);
            await catalog.CallAsync("f8_mutate",
                McpTestSupport.Args("{\"namespace\":\"tenantA\",\"op\":\"create_vertex\",\"label\":\"person\",\"properties\":{\"name\":\"Ada\"}}"),
                CancellationToken.None);

            // The vertex is in tenantA...
            var inTenant = await catalog.CallAsync("f8_search",
                McpTestSupport.Args("{\"namespace\":\"tenantA\",\"mode\":\"property\",\"key\":\"name\",\"value\":\"Ada\"}"),
                CancellationToken.None);
            Assert.AreEqual(1, McpTestSupport.Structured(inTenant).GetProperty("items").GetArrayLength());

            // ...and NOT in the default namespace (isolation).
            var inDefault = await catalog.CallAsync("f8_search",
                McpTestSupport.Args("{\"mode\":\"property\",\"key\":\"name\",\"value\":\"Ada\"}"), CancellationToken.None);
            Assert.AreEqual(0, McpTestSupport.Structured(inDefault).GetProperty("items").GetArrayLength());
        }

        [TestMethod]
        public async Task Admin_Trim_IsEnqueuedNotApplied()
        {
            using var api = new ApiAppFactory();
            var catalog = WriteCatalog(api);

            var result = await catalog.CallAsync("f8_admin", McpTestSupport.Args("{\"op\":\"trim\"}"), CancellationToken.None);
            var structured = McpTestSupport.Structured(result);
            Assert.IsTrue(structured.GetProperty("enqueued").GetBoolean(), "trim is fire-and-forget → enqueued, never 'applied'");
        }

        [TestMethod]
        public async Task Admin_SaveThenListSavegames()
        {
            using var api = new ApiAppFactory();
            var catalog = WriteCatalog(api);

            var save = await catalog.CallAsync("f8_admin", McpTestSupport.Args("{\"op\":\"save\"}"), CancellationToken.None);
            Assert.IsFalse(save.IsError, "save succeeds");

            var list = await catalog.CallAsync("f8_admin", McpTestSupport.Args("{\"op\":\"list_savegames\"}"), CancellationToken.None);
            var saveGames = McpTestSupport.Structured(list).GetProperty("saveGames");
            Assert.IsTrue(saveGames.GetArrayLength() >= 1, "the saved game appears in the registry");
        }

        [TestMethod]
        public async Task Mutate_BatchCreate_ReturnsIds_ThenLinksThem()
        {
            using var api = new ApiAppFactory();
            var catalog = WriteCatalog(api);

            var created = await catalog.CallAsync("f8_mutate", McpTestSupport.Args(
                "{\"op\":\"create_vertices\",\"vertices\":[" +
                "{\"label\":\"person\",\"properties\":{\"name\":\"Ada\"}}," +
                "{\"label\":\"person\",\"properties\":{\"name\":\"Grace\"}}]}"), CancellationToken.None);
            var vids = McpTestSupport.Structured(created).GetProperty("ids").EnumerateArray().Select(e => e.GetInt32()).ToList();
            Assert.AreEqual(2, vids.Count, "create_vertices returns one id per vertex (the single-create gap fixed)");

            var linked = await catalog.CallAsync("f8_mutate", McpTestSupport.Args(
                $"{{\"op\":\"create_edges\",\"edges\":[{{\"source\":{vids[0]},\"target\":{vids[1]},\"edgePropertyId\":\"knows\"}}]}}"),
                CancellationToken.None);
            var eids = McpTestSupport.Structured(linked).GetProperty("ids").EnumerateArray().ToList();
            Assert.AreEqual(1, eids.Count, "create_edges returns the assigned edge id");

            var get = await catalog.CallAsync("f8_get",
                McpTestSupport.Args($"{{\"kind\":\"vertex\",\"id\":{vids[0]},\"include\":[\"degree\"]}}"), CancellationToken.None);
            Assert.IsTrue(McpTestSupport.Structured(get).GetProperty("degree").GetInt32() >= 1,
                "the batch-created vertices are linked by the batch-created edge");
        }

        private static async Task<Int32?> FindByName(ToolCatalog catalog, String name)
        {
            var search = await catalog.CallAsync("f8_search",
                McpTestSupport.Args($"{{\"mode\":\"property\",\"key\":\"name\",\"value\":\"{name}\"}}"), CancellationToken.None);
            var items = McpTestSupport.Structured(search).GetProperty("items");
            return items.GetArrayLength() > 0 ? items[0].GetProperty("id").GetInt32() : null;
        }
    }
}
