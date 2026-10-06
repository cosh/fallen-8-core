// MIT License
//
// McpWriteToolsTest.cs
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
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
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

            foreach (var read in new[] { "f8_overview", "f8_get", "f8_search", "f8_paths", "f8_analytics", "f8_plugins", "f8_storedquery", "f8_documents" })
            {
                Assert.IsTrue(names.Contains(read), read + " is a default read tool");
            }
            foreach (var gated in new[] { "f8_mutate", "f8_index", "f8_subgraph", "f8_namespace", "f8_admin" })
            {
                Assert.IsFalse(names.Contains(gated), gated + " is absent when its tier is off");
            }
        }

        [TestMethod]
        public void WriteEnabled_AddsWriteTools_ButNotAdmin()
        {
            var names = DummyCatalog(new McpToolsOptions { EnableWrite = true }).ListTools().Select(t => t.Name).ToHashSet();

            foreach (var w in new[] { "f8_mutate", "f8_index", "f8_subgraph", "f8_namespace" })
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

        /// <summary>The shared volatile apiApp host, in Development so the dev-only routes are mapped.</summary>
        private sealed class ApiAppFactory : VolatileAppFactory
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                base.ConfigureWebHost(builder);
                builder.UseEnvironment("Development");
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

        /// <summary>
        ///   The batch argument is <c>updates</c> (feature mcp-plugin-gaps, spec section 3); the
        ///   array-valued <c>properties</c> the schema once advertised keeps working at runtime so
        ///   a caller written against that schema is not broken by the rename.
        /// </summary>
        [TestMethod]
        public async Task Mutate_SetProperties_ViaUpdates_AndViaTheLegacyArray_BothApplyInOneTransaction()
        {
            using var api = new ApiAppFactory();
            var catalog = WriteCatalog(api);

            await catalog.CallAsync("f8_mutate",
                McpTestSupport.Args("{\"op\":\"create_vertex\",\"label\":\"person\",\"properties\":{\"name\":\"Lin\"}}"),
                CancellationToken.None);
            var id = (await FindByName(catalog, "Lin")) ?? throw new AssertFailedException("seeded vertex not found");

            var viaUpdates = await catalog.CallAsync("f8_mutate", McpTestSupport.Args(
                $"{{\"op\":\"set_properties\",\"updates\":[{{\"id\":{id},\"key\":\"city\",\"value\":\"Berlin\"}},{{\"id\":{id},\"key\":\"age\",\"value\":41}}]}}"),
                CancellationToken.None);
            Assert.IsTrue(McpTestSupport.Structured(viaUpdates).GetProperty("applied").GetBoolean());

            var afterUpdates = McpTestSupport.Structured(await catalog.CallAsync("f8_get",
                McpTestSupport.Args($"{{\"kind\":\"vertex\",\"id\":{id}}}"), CancellationToken.None)).GetProperty("properties");
            Assert.AreEqual("Berlin", afterUpdates.GetProperty("city").GetString());
            Assert.AreEqual(41, afterUpdates.GetProperty("age").GetInt32());

            var viaLegacy = await catalog.CallAsync("f8_mutate", McpTestSupport.Args(
                $"{{\"op\":\"set_properties\",\"properties\":[{{\"id\":{id},\"key\":\"city\",\"value\":\"Paris\"}},{{\"id\":{id},\"key\":\"age\",\"remove\":true}}]}}"),
                CancellationToken.None);
            Assert.IsTrue(McpTestSupport.Structured(viaLegacy).GetProperty("applied").GetBoolean(),
                "the legacy array-valued 'properties' is still read when 'updates' is absent");

            var afterLegacy = McpTestSupport.Structured(await catalog.CallAsync("f8_get",
                McpTestSupport.Args($"{{\"kind\":\"vertex\",\"id\":{id}}}"), CancellationToken.None)).GetProperty("properties");
            Assert.AreEqual("Paris", afterLegacy.GetProperty("city").GetString());
            Assert.IsFalse(afterLegacy.TryGetProperty("age", out _), "the batch's remove entry removed the key");

            var neither = await catalog.CallAsync("f8_mutate",
                McpTestSupport.Args("{\"op\":\"set_properties\"}"), CancellationToken.None);
            Assert.IsTrue(neither.IsError, "no batch at all is a 400");
            StringAssert.Contains(((ModelContextProtocol.Protocol.TextContentBlock)neither.Content[0]).Text, "updates",
                "the error names the advertised argument, not the legacy one");
        }

        [TestMethod]
        public async Task Mutate_RemoveElements_RejectsANonIntegerId_AndRemovesTheBatchInOneTransaction()
        {
            using var api = new ApiAppFactory();
            var catalog = WriteCatalog(api);

            var created = await catalog.CallAsync("f8_mutate", McpTestSupport.Args(
                "{\"op\":\"create_vertices\",\"vertices\":[{\"label\":\"tmp\"},{\"label\":\"tmp\"}]}"), CancellationToken.None);
            var ids = McpTestSupport.Structured(created).GetProperty("ids").EnumerateArray().Select(e => e.GetInt32()).ToList();
            Assert.AreEqual(2, ids.Count);

            var rejected = await catalog.CallAsync("f8_mutate",
                McpTestSupport.Args($"{{\"op\":\"remove_elements\",\"ids\":[{ids[0]},\"{ids[1]}\"]}}"), CancellationToken.None);
            Assert.IsTrue(rejected.IsError, "a non-integer id is refused before anything is sent");
            StringAssert.Contains(((ModelContextProtocol.Protocol.TextContentBlock)rejected.Content[0]).Text, "integer");

            var stillThere = await catalog.CallAsync("f8_get",
                McpTestSupport.Args($"{{\"kind\":\"vertex\",\"id\":{ids[0]}}}"), CancellationToken.None);
            Assert.IsTrue(McpTestSupport.Structured(stillThere).GetProperty("found").GetBoolean(), "the refused batch removed nothing");

            var removed = await catalog.CallAsync("f8_mutate",
                McpTestSupport.Args($"{{\"op\":\"remove_elements\",\"ids\":[{ids[0]},{ids[1]}]}}"), CancellationToken.None);
            Assert.IsTrue(McpTestSupport.Structured(removed).GetProperty("applied").GetBoolean());

            foreach (var id in ids)
            {
                // f8_get reports a missing element as found:false, not as an error (spec section 3.7).
                var gone = await catalog.CallAsync("f8_get",
                    McpTestSupport.Args($"{{\"kind\":\"vertex\",\"id\":{id}}}"), CancellationToken.None);
                Assert.IsFalse(McpTestSupport.Structured(gone).GetProperty("found").GetBoolean(), $"vertex {id} was removed by the batch");
            }
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

        // --- activation (engine -> REST -> MCP, feature namespace-startup-load §4.8) ----------

        /// <summary>
        ///   Activation is the ONE admin operation an agent needs to recover from the 503 every other
        ///   tool gets for a not-loaded namespace, so it is bridged rather than deferred. Pinned here:
        ///   it is discoverable in the tool schema, it is admin-gated, it hits the Fallen-8-level
        ///   route with the name as ONE percent-encoded path segment, and an omitted namespace is
        ///   named as a mistake instead of silently activating "default" (always a no-op).
        /// </summary>
        [TestMethod]
        public async Task Admin_Activate_IsAdminGated_AndPostsToTheEncodedActivationRoute()
        {
            var schema = DummyCatalog(new McpToolsOptions { EnableAdmin = true }).ListTools()
                .Single(t => t.Name == "f8_admin").InputSchema.GetRawText();
            StringAssert.Contains(schema, "activate", "an agent can only use what the schema advertises");

            var offTier = await DummyCatalog(new McpToolsOptions())
                .CallAsync("f8_admin", McpTestSupport.Args("{\"op\":\"activate\",\"namespace\":\"archived\"}"),
                    CancellationToken.None);
            Assert.IsTrue(offTier.IsError, "activation is admin-tier: it must be rejected when that tier is off");

            var requests = new List<HttpRequestMessage>();
            var bridge = McpTestSupport.Bridge(new McpTestSupport.LambdaHandler(request =>
            {
                requests.Add(request);
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent(
                        "{\"activated\":true,\"detail\":\"Restored from save game \\\"sg-1\\\".\"," +
                        "\"namespace\":{\"name\":\"my graph\",\"state\":\"ready\",\"vertexCount\":5}}",
                        System.Text.Encoding.UTF8, "application/json"),
                };
            }));
            var catalog = McpTestSupport.Catalog(new McpToolsOptions { EnableAdmin = true }, new IMcpTool[] { new AdminTool(bridge) });

            var missing = await catalog.CallAsync("f8_admin", McpTestSupport.Args("{\"op\":\"activate\"}"), CancellationToken.None);
            Assert.IsTrue(missing.IsError, "activate without a namespace is a mistake, not a default-namespace no-op");
            Assert.AreEqual(0, requests.Count, "and it never reaches the wire");

            var result = await catalog.CallAsync("f8_admin",
                McpTestSupport.Args("{\"op\":\"activate\",\"namespace\":\"my graph\"}"), CancellationToken.None);

            var structured = McpTestSupport.Structured(result);
            Assert.IsTrue(structured.GetProperty("activated").GetBoolean(), "the REST answer is passed through");
            Assert.AreEqual(1, requests.Count);
            Assert.AreEqual(HttpMethod.Post, requests[0].Method);
            Assert.AreEqual("/ns/my%20graph/activate", requests[0].RequestUri.AbsolutePath,
                "Fallen-8-level route, name percent-encoded into exactly one segment (never a scoping prefix)");
        }

        // --- f8_storedquery (feature mcp-plugin-gaps, spec section 8) ---------------------------------

        [TestMethod]
        public async Task StoredQuery_RegisterListGetUseDelete_RoundTrip_WithTheGatesInBetween()
        {
            using var api = new ApiAppFactory();
            var readOnly = McpTestSupport.Catalog(new McpToolsOptions(), McpTestSupport.AllTools(McpTestSupport.Bridge(api.Server.CreateHandler())));
            var write = WriteCatalog(api);
            var code = CodeCatalog(api);
            var ids = await SeedPair(write);
            const String registration =
                "{\"op\":\"register\",\"name\":\"people-only\",\"kind\":\"Path\",\"description\":\"persons\"," +
                "\"path\":{\"filter\":{\"vertexFilter\":\"return (v) => v.Label == \\\"person\\\";\"}}}";

            var refused = await write.CallAsync("f8_storedquery", McpTestSupport.Args(registration), CancellationToken.None);
            Assert.IsTrue(refused.IsError, "register is C# and needs the code capability");
            StringAssert.Contains(Text(refused), "Mcp:Tools:EnableCode");

            var registered = McpTestSupport.Structured(await code.CallAsync("f8_storedquery", McpTestSupport.Args(registration), CancellationToken.None));
            Assert.AreEqual("Compiled", registered.GetProperty("compileState").GetString(), "the summary carries the compile state");

            var again = await code.CallAsync("f8_storedquery", McpTestSupport.Args(registration), CancellationToken.None);
            Assert.IsTrue(again.IsError, "a taken name is the server's 409");
            StringAssert.Contains(Text(again), "409");

            var broken = await code.CallAsync("f8_storedquery", McpTestSupport.Args(
                "{\"op\":\"register\",\"name\":\"broken\",\"kind\":\"Path\",\"path\":{\"filter\":{\"vertexFilter\":\"return (v) => v.NoSuchMember;\"}}}"),
                CancellationToken.None);
            Assert.IsTrue(broken.IsError, "a fragment that does not compile is the server's 400");
            StringAssert.Contains(Text(broken), "400");
            StringAssert.Contains(Text(broken), "NoSuchMember", "the compiler's own message reaches the agent");

            var listed = McpTestSupport.Structured(await readOnly.CallAsync("f8_storedquery", McpTestSupport.Args("{\"op\":\"list\"}"), CancellationToken.None));
            var names = listed.GetProperty("storedQueries").EnumerateArray().Select(q => q.GetProperty("name").GetString()).ToList();
            CollectionAssert.Contains(names, "people-only");
            CollectionAssert.DoesNotContain(names, "broken", "a refused registration leaves nothing behind");

            var detail = McpTestSupport.Structured(await readOnly.CallAsync("f8_storedquery", McpTestSupport.Args("{\"op\":\"get\",\"name\":\"people-only\"}"), CancellationToken.None));
            Assert.AreEqual("Path", detail.GetProperty("kind").GetString());
            Assert.AreEqual(JsonValueKind.Object, detail.GetProperty("specification").ValueKind, "the stored specification is an object, not JSON text");
            StringAssert.Contains(detail.GetProperty("specification").GetRawText(), "person");
            Assert.IsFalse(detail.TryGetProperty("specificationJson", out _), "the text form is replaced, not duplicated");
            Assert.AreEqual(JsonValueKind.Null, detail.GetProperty("compileDiagnostics").ValueKind, "no diagnostics on a compiled query");

            var used = McpTestSupport.Structured(await readOnly.CallAsync("f8_paths",
                McpTestSupport.Args($"{{\"from\":{ids[0]},\"to\":{ids[1]},\"storedQuery\":\"people-only\"}}"), CancellationToken.None));
            Assert.IsTrue(used.GetProperty("count").GetInt32() >= 1, "f8_paths runs the query the tool registered");

            var deleteRefused = await readOnly.CallAsync("f8_storedquery", McpTestSupport.Args("{\"op\":\"delete\",\"name\":\"people-only\"}"), CancellationToken.None);
            Assert.IsTrue(deleteRefused.IsError, "delete needs the write capability");
            StringAssert.Contains(Text(deleteRefused), "Mcp:Tools:EnableWrite");

            var deleted = McpTestSupport.Structured(await write.CallAsync("f8_storedquery", McpTestSupport.Args("{\"op\":\"delete\",\"name\":\"people-only\"}"), CancellationToken.None));
            Assert.IsTrue(deleted.GetProperty("deleted").GetBoolean());

            var gone = await readOnly.CallAsync("f8_storedquery", McpTestSupport.Args("{\"op\":\"get\",\"name\":\"people-only\"}"), CancellationToken.None);
            Assert.IsTrue(gone.IsError, "the deleted query is not found");
            StringAssert.Contains(Text(gone), "404");
        }

        // --- f8_subgraph: semantic and patterns (feature mcp-plugin-gaps, spec section 7) -----------

        private static ToolCatalog CodeCatalog(ApiAppFactory api)
        {
            var bridge = McpTestSupport.Bridge(api.Server.CreateHandler());
            return McpTestSupport.Catalog(new McpToolsOptions { EnableWrite = true, EnableCode = true }, McpTestSupport.AllTools(bridge));
        }

        /// <summary>Two linked vertices with an embedding on the source, so a pattern and a semantic
        /// block each have something to match.</summary>
        private static async Task<List<Int32>> SeedPair(ToolCatalog catalog)
        {
            var created = await catalog.CallAsync("f8_mutate", McpTestSupport.Args(
                "{\"op\":\"create_vertices\",\"vertices\":[{\"label\":\"person\",\"properties\":{\"name\":\"Pat\"}},{\"label\":\"person\",\"properties\":{\"name\":\"Quinn\"}}]}"),
                CancellationToken.None);
            var ids = McpTestSupport.Structured(created).GetProperty("ids").EnumerateArray().Select(e => e.GetInt32()).ToList();
            McpTestSupport.Structured(await catalog.CallAsync("f8_mutate", McpTestSupport.Args(
                $"{{\"op\":\"create_edges\",\"edges\":[{{\"source\":{ids[0]},\"target\":{ids[1]},\"edgePropertyId\":\"knows\"}}]}}"), CancellationToken.None));
            McpTestSupport.Structured(await catalog.CallAsync("f8_mutate", McpTestSupport.Args(
                $"{{\"op\":\"set_embedding\",\"id\":{ids[0]},\"name\":\"default\",\"vector\":[1,0,0]}}"), CancellationToken.None));
            return ids;
        }

        [TestMethod]
        public async Task Subgraph_CodeFreePatterns_AndSemantic_AreForwardedWithoutTheCodeCapability()
        {
            using var api = new ApiAppFactory();
            var catalog = WriteCatalog(api);
            var ids = await SeedPair(catalog);

            var byPattern = await catalog.CallAsync("f8_subgraph", McpTestSupport.Args(
                "{\"name\":\"linked\",\"patterns\":[{\"type\":\"Vertex\"},{\"type\":\"Edge\",\"direction\":\"OutgoingEdge\"},{\"type\":\"Vertex\"}]}"),
                CancellationToken.None);
            var patternResult = McpTestSupport.Structured(byPattern);
            Assert.IsTrue(patternResult.GetProperty("vertexCount").GetInt32() >= 2,
                "a code-free Vertex-Edge-Vertex pattern reached the server and matched the seeded pair");

            var bySemantic = await catalog.CallAsync("f8_subgraph", McpTestSupport.Args(
                "{\"name\":\"similar\",\"semantic\":{\"queryVector\":[1,0,0],\"embeddingName\":\"default\",\"minScore\":0.9}}"),
                CancellationToken.None);
            var semanticResult = McpTestSupport.Structured(bySemantic);
            Assert.AreEqual(1, semanticResult.GetProperty("vertexCount").GetInt32(),
                $"semantic.minScore admitted only the embedded vertex {ids[0]}; the block reached the server");

            var nothing = await catalog.CallAsync("f8_subgraph", McpTestSupport.Args("{\"name\":\"empty\"}"), CancellationToken.None);
            Assert.IsTrue(nothing.IsError, "no storedQuery, semantic or patterns is still a 400");
            StringAssert.Contains(Text(nothing), "code-free patterns");
        }

        [TestMethod]
        public async Task Subgraph_PatternWithAFragment_NeedsTheCodeCapability()
        {
            using var api = new ApiAppFactory();
            await SeedPair(WriteCatalog(api));
            var withFragment =
                "{\"name\":\"coded\",\"patterns\":[{\"type\":\"Vertex\",\"vertexFilter\":\"return (v) => v.Label == \\\"person\\\";\"},{\"type\":\"Edge\"},{\"type\":\"Vertex\"}]}";

            var refused = await WriteCatalog(api).CallAsync("f8_subgraph", McpTestSupport.Args(withFragment), CancellationToken.None);
            Assert.IsTrue(refused.IsError, "a fragment inside a pattern is code and is refused without the capability");
            StringAssert.Contains(Text(refused), "403");
            StringAssert.Contains(Text(refused), "Mcp:Tools:EnableCode");

            var accepted = await CodeCatalog(api).CallAsync("f8_subgraph", McpTestSupport.Args(withFragment), CancellationToken.None);
            Assert.IsTrue(McpTestSupport.Structured(accepted).GetProperty("vertexCount").GetInt32() >= 2,
                "with the capability the same pattern is forwarded and the server compiles it");

            var blank = await WriteCatalog(api).CallAsync("f8_subgraph", McpTestSupport.Args(
                "{\"name\":\"blank\",\"patterns\":[{\"type\":\"Vertex\",\"vertexFilter\":\"  \"},{\"type\":\"Edge\"},{\"type\":\"Vertex\"}]}"),
                CancellationToken.None);
            Assert.IsFalse(blank.IsError, "a blank fragment is not code (the server reads it as match-everything)");
        }

        // --- f8_index (feature mcp-plugin-gaps, spec section 6) ----------------------------------

        private static String Text(ModelContextProtocol.Protocol.CallToolResult result)
        {
            return result.Content.Count > 0 && result.Content[0] is ModelContextProtocol.Protocol.TextContentBlock text ? text.Text : String.Empty;
        }

        private static async Task<List<Int32>> IndexHits(ToolCatalog catalog, String indexId, String value)
        {
            var search = await catalog.CallAsync("f8_search",
                McpTestSupport.Args($"{{\"mode\":\"index\",\"indexId\":\"{indexId}\",\"value\":\"{value}\"}}"), CancellationToken.None);
            return McpTestSupport.Structured(search).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToList();
        }

        /// <summary>
        ///   The whole dictionary-index lifecycle through the tool, verified by what f8_search then
        ///   finds: create, the two bare-false refusals (taken id, unknown type) as 409 errors that
        ///   say the status is the bridge's reading, add and add_many, remove_key and remove_element,
        ///   backfill with its counts, delete, and the bare false of a second delete as a 404.
        /// </summary>
        [TestMethod]
        public async Task Index_DictionaryLifecycle_CreateAddSearchRemoveBackfillDelete()
        {
            using var api = new ApiAppFactory();
            var catalog = WriteCatalog(api);

            var created = await catalog.CallAsync("f8_index",
                McpTestSupport.Args("{\"op\":\"create\",\"indexId\":\"names\",\"pluginType\":\"DictionaryIndex\"}"), CancellationToken.None);
            Assert.IsTrue(McpTestSupport.Structured(created).GetProperty("applied").GetBoolean());

            var taken = await catalog.CallAsync("f8_index",
                McpTestSupport.Args("{\"op\":\"create\",\"indexId\":\"names\",\"pluginType\":\"DictionaryIndex\"}"), CancellationToken.None);
            Assert.IsTrue(taken.IsError, "a taken id is refused");
            StringAssert.Contains(Text(taken), "409");
            StringAssert.Contains(Text(taken), "already taken or the plugin type is unknown", "the message names both causes");
            StringAssert.Contains(Text(taken), "bridge's reading", "the status is declared as an interpretation");

            var unknownType = await catalog.CallAsync("f8_index",
                McpTestSupport.Args("{\"op\":\"create\",\"indexId\":\"other\",\"pluginType\":\"NoSuchIndex\"}"), CancellationToken.None);
            Assert.IsTrue(unknownType.IsError, "an unknown plugin type is refused the same way, the server cannot tell them apart");

            var vertices = await catalog.CallAsync("f8_mutate", McpTestSupport.Args(
                "{\"op\":\"create_vertices\",\"vertices\":[" +
                "{\"label\":\"person\",\"properties\":{\"name\":\"Ada\"}}," +
                "{\"label\":\"person\",\"properties\":{\"name\":\"Grace\"}}]}"), CancellationToken.None);
            var ids = McpTestSupport.Structured(vertices).GetProperty("ids").EnumerateArray().Select(e => e.GetInt32()).ToList();

            var added = await catalog.CallAsync("f8_index",
                McpTestSupport.Args($"{{\"op\":\"add\",\"indexId\":\"names\",\"id\":{ids[0]},\"key\":\"Ada\"}}"), CancellationToken.None);
            Assert.IsTrue(McpTestSupport.Structured(added).GetProperty("applied").GetBoolean());

            var addedMany = await catalog.CallAsync("f8_index",
                McpTestSupport.Args($"{{\"op\":\"add_many\",\"indexId\":\"names\",\"entries\":[{{\"id\":{ids[1]},\"key\":\"Grace\"}}]}}"), CancellationToken.None);
            Assert.AreEqual(1, McpTestSupport.Structured(addedMany).GetProperty("accepted").GetInt32(), "the batch route's count passes through");

            CollectionAssert.AreEqual(new List<Int32> { ids[0] }, await IndexHits(catalog, "names", "Ada"));
            CollectionAssert.AreEqual(new List<Int32> { ids[1] }, await IndexHits(catalog, "names", "Grace"));

            var keyRemoved = await catalog.CallAsync("f8_index",
                McpTestSupport.Args("{\"op\":\"remove_key\",\"indexId\":\"names\",\"key\":\"Ada\"}"), CancellationToken.None);
            Assert.IsTrue(McpTestSupport.Structured(keyRemoved).GetProperty("applied").GetBoolean());
            Assert.AreEqual(0, (await IndexHits(catalog, "names", "Ada")).Count, "the key is gone from the index");

            var elementRemoved = await catalog.CallAsync("f8_index",
                McpTestSupport.Args($"{{\"op\":\"remove_element\",\"indexId\":\"names\",\"id\":{ids[1]}}}"), CancellationToken.None);
            Assert.IsTrue(McpTestSupport.Structured(elementRemoved).GetProperty("applied").GetBoolean());
            Assert.AreEqual(0, (await IndexHits(catalog, "names", "Grace")).Count, "the element is gone from the index");

            var backfilled = await catalog.CallAsync("f8_index",
                McpTestSupport.Args("{\"op\":\"backfill\",\"indexId\":\"names\",\"propertyId\":\"name\"}"), CancellationToken.None);
            var counts = McpTestSupport.Structured(backfilled);
            Assert.AreEqual(2, counts.GetProperty("indexedElements").GetInt32(), "backfill indexed both named vertices");
            CollectionAssert.AreEqual(new List<Int32> { ids[0] }, await IndexHits(catalog, "names", "Ada"));

            var deleted = await catalog.CallAsync("f8_index",
                McpTestSupport.Args("{\"op\":\"delete\",\"indexId\":\"names\"}"), CancellationToken.None);
            Assert.IsTrue(McpTestSupport.Structured(deleted).GetProperty("applied").GetBoolean());

            var deletedAgain = await catalog.CallAsync("f8_index",
                McpTestSupport.Args("{\"op\":\"delete\",\"indexId\":\"names\"}"), CancellationToken.None);
            Assert.IsTrue(deletedAgain.IsError, "deleting a missing index is a bare false, surfaced as an error");
            StringAssert.Contains(Text(deletedAgain), "404");
            StringAssert.Contains(Text(deletedAgain), "no index 'names'");

            var noIndex = await catalog.CallAsync("f8_index",
                McpTestSupport.Args("{\"op\":\"add\",\"id\":1,\"key\":\"x\"}"), CancellationToken.None);
            Assert.IsTrue(noIndex.IsError, "indexId is required before anything is sent");
        }

        /// <summary>
        ///   Two vector indices created by the tool with JSON-native options. An UNBOUND one is
        ///   filled through add_vector and searched with f8_search mode:vector; one BOUND to an
        ///   embedding name fills itself from set_embedding and refuses add_vector, and that refusal
        ///   (the server's sentence) passes through. The exactly-one rule of add_vector is checked
        ///   client-side so an agent sees one sentence for the mistake.
        /// </summary>
        [TestMethod]
        public async Task Index_VectorLifecycle_CreateWithOptions_AddVector_SearchByVector()
        {
            using var api = new ApiAppFactory();
            var catalog = WriteCatalog(api);

            var created = await catalog.CallAsync("f8_index", McpTestSupport.Args(
                "{\"op\":\"create\",\"indexId\":\"vec\",\"pluginType\":\"VectorIndex\"," +
                "\"options\":{\"dimension\":3,\"metric\":\"Cosine\"}}"), CancellationToken.None);
            Assert.IsTrue(McpTestSupport.Structured(created).GetProperty("applied").GetBoolean(),
                "the typed options reached the plugin (a VectorIndex refuses creation without a dimension)");

            var vertices = await catalog.CallAsync("f8_mutate", McpTestSupport.Args(
                "{\"op\":\"create_vertices\",\"vertices\":[{\"label\":\"doc\"},{\"label\":\"doc\"}]}"), CancellationToken.None);
            var ids = McpTestSupport.Structured(vertices).GetProperty("ids").EnumerateArray().Select(e => e.GetInt32()).ToList();

            var addedVector = await catalog.CallAsync("f8_index",
                McpTestSupport.Args($"{{\"op\":\"add_vector\",\"indexId\":\"vec\",\"id\":{ids[1]},\"vector\":[0,1,0]}}"), CancellationToken.None);
            Assert.IsTrue(McpTestSupport.Structured(addedVector).GetProperty("applied").GetBoolean());
            await catalog.CallAsync("f8_index",
                McpTestSupport.Args($"{{\"op\":\"add_vector\",\"indexId\":\"vec\",\"id\":{ids[0]},\"vector\":[1,0,0]}}"), CancellationToken.None);

            var search = await catalog.CallAsync("f8_search",
                McpTestSupport.Args("{\"mode\":\"vector\",\"indexId\":\"vec\",\"vector\":[0,1,0],\"limit\":1}"), CancellationToken.None);
            var top = McpTestSupport.Structured(search).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToList();
            CollectionAssert.AreEqual(new List<Int32> { ids[1] }, top, "the vector written by add_vector ranks first for its own direction");

            var bound = await catalog.CallAsync("f8_index", McpTestSupport.Args(
                "{\"op\":\"create\",\"indexId\":\"bound\",\"pluginType\":\"VectorIndex\"," +
                "\"options\":{\"dimension\":3,\"metric\":\"Cosine\",\"embeddingName\":\"text\"}}"), CancellationToken.None);
            Assert.IsTrue(McpTestSupport.Structured(bound).GetProperty("applied").GetBoolean());
            var embedded = await catalog.CallAsync("f8_mutate",
                McpTestSupport.Args($"{{\"op\":\"set_embedding\",\"id\":{ids[0]},\"name\":\"text\",\"vector\":[1,0,0]}}"), CancellationToken.None);
            Assert.IsTrue(McpTestSupport.Structured(embedded).GetProperty("applied").GetBoolean());
            var boundSearch = await catalog.CallAsync("f8_search",
                McpTestSupport.Args("{\"mode\":\"vector\",\"indexId\":\"bound\",\"vector\":[1,0,0],\"limit\":1}"), CancellationToken.None);
            CollectionAssert.AreEqual(new List<Int32> { ids[0] },
                McpTestSupport.Structured(boundSearch).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToList(),
                "the bound index filled itself from set_embedding (the embeddingName option reached the plugin)");
            var refused = await catalog.CallAsync("f8_index",
                McpTestSupport.Args($"{{\"op\":\"add_vector\",\"indexId\":\"bound\",\"id\":{ids[1]},\"vector\":[0,1,0]}}"), CancellationToken.None);
            Assert.IsTrue(refused.IsError, "a bound index refuses direct vector writes");
            StringAssert.Contains(Text(refused), "maintains itself", "the server's own sentence passes through");

            var wrongDimension = await catalog.CallAsync("f8_index",
                McpTestSupport.Args($"{{\"op\":\"add_vector\",\"indexId\":\"vec\",\"id\":{ids[1]},\"vector\":[0,1]}}"), CancellationToken.None);
            Assert.IsTrue(wrongDimension.IsError, "the server's dimension check passes through as an error");
            StringAssert.Contains(Text(wrongDimension), "dimension");

            var neither = await catalog.CallAsync("f8_index",
                McpTestSupport.Args($"{{\"op\":\"add_vector\",\"indexId\":\"vec\",\"id\":{ids[1]}}}"), CancellationToken.None);
            Assert.IsTrue(neither.IsError);
            StringAssert.Contains(Text(neither), "exactly one of 'vector' or 'propertyId'");

            var both = await catalog.CallAsync("f8_index",
                McpTestSupport.Args($"{{\"op\":\"add_vector\",\"indexId\":\"vec\",\"id\":{ids[1]},\"vector\":[0,1,0],\"propertyId\":\"p\"}}"), CancellationToken.None);
            Assert.IsTrue(both.IsError);
            StringAssert.Contains(Text(both), "exactly one of 'vector' or 'propertyId'");
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
