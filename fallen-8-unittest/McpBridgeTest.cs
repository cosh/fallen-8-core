// MIT License
//
// McpBridgeTest.cs
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
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Mcp.Bridge;
using NoSQL.GraphDB.Mcp.Configuration;
using NoSQL.GraphDB.Mcp.Tools;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The REST bridge (feature mcp-server, Phase 0/1): the three-rule error mapping
    ///   (problem+json → title/detail; other 4xx/5xx string → detail; 204/200-null →
    ///   soft-not-found) proven against a stub, and a genuine walking-skeleton round-trip —
    ///   f8_overview through the ToolCatalog into a real hosted apiApp and back (spec §3.11).
    /// </summary>
    [TestClass]
    public class McpBridgeTest
    {
        private static readonly IReadOnlyDictionary<String, JsonElement> NoArgs =
            new Dictionary<String, JsonElement>();

        private static HttpResponseMessage Response(HttpStatusCode status, String body, String mediaType)
        {
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType),
            };
        }

        [TestMethod]
        public async Task ErrorMapping_ProblemJson_UsesTitleAndDetail()
        {
            var bridge = McpTestSupport.Bridge(new McpTestSupport.LambdaHandler(_ =>
                Response(HttpStatusCode.BadRequest, "{\"title\":\"Bad Request\",\"detail\":\"the name is invalid\"}", "application/problem+json")));

            var error = await Assert.ThrowsExceptionAsync<BridgeError>(
                () => bridge.GetStatusAsync("default", CancellationToken.None));

            Assert.AreEqual(400, error.Status);
            Assert.AreEqual("Bad Request", error.Title);
            Assert.AreEqual("the name is invalid", error.Detail);
        }

        [TestMethod]
        public async Task ErrorMapping_PlainStringBody_BecomesDetail()
        {
            var bridge = McpTestSupport.Bridge(new McpTestSupport.LambdaHandler(_ =>
                Response(HttpStatusCode.NotFound, "Could not find vertex with id 5.", "text/plain")));

            var error = await Assert.ThrowsExceptionAsync<BridgeError>(
                () => bridge.GetStatusAsync("default", CancellationToken.None));

            Assert.AreEqual(404, error.Status);
            Assert.AreEqual("Could not find vertex with id 5.", error.Detail);
        }

        [TestMethod]
        public async Task ErrorMapping_TooManyRequests_IsRetryable()
        {
            var bridge = McpTestSupport.Bridge(new McpTestSupport.LambdaHandler(_ =>
                Response(HttpStatusCode.TooManyRequests, "slow down", "text/plain")));

            var error = await Assert.ThrowsExceptionAsync<BridgeError>(
                () => bridge.GetStatusAsync("default", CancellationToken.None));

            Assert.AreEqual(429, error.Status);
            Assert.IsTrue(error.Retryable, "a 429 is surfaced as a retryable tool error");
        }

        [TestMethod]
        public async Task ErrorMapping_NoContent_IsSoftNotFound_NotAnError()
        {
            var bridge = McpTestSupport.Bridge(new McpTestSupport.LambdaHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.NoContent)));

            var status = await bridge.GetStatusAsync("default", CancellationToken.None);

            Assert.IsNull(status, "a 204 maps to a soft-not-found (null), not a thrown error");
        }

        // --- Walking-skeleton round-trip: MCP tool → bridge → real apiApp → back --------------

        private sealed class ApiAppFactory : WebApplicationFactory<NoSQL.GraphDB.App.Program>
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseEnvironment("Development");
                // Volatile durability: hosting writes no checkpoint/WAL into the test bin.
                builder.UseSetting("Fallen8:Durability:Volatile", "true");
            }
        }

        private static JsonElement Str(String value) => JsonSerializer.SerializeToElement(value);

        [TestMethod]
        public async Task Overview_NoNamespace_ListsNamespacesFromRealApiApp()
        {
            using var api = new ApiAppFactory();
            var bridge = McpTestSupport.Bridge(api.Server.CreateHandler());
            var catalog = McpTestSupport.Catalog(new McpToolsOptions(), new IMcpTool[] { new OverviewTool(bridge) });

            var result = await catalog.CallAsync("f8_overview", NoArgs, CancellationToken.None);

            Assert.IsFalse(result.IsError, "f8_overview must succeed against a live apiApp");
            Assert.IsNotNull(result.StructuredContent);
            var namespaces = result.StructuredContent!.Value.GetProperty("namespaces");
            Assert.IsTrue(
                namespaces.EnumerateArray().Any(n => n.GetProperty("name").GetString() == "default"),
                "the reserved default namespace is always present");
        }

        [TestMethod]
        public async Task Overview_WithNamespace_ReturnsThatGraphsStatus()
        {
            using var api = new ApiAppFactory();
            var bridge = McpTestSupport.Bridge(api.Server.CreateHandler());
            var catalog = McpTestSupport.Catalog(new McpToolsOptions(), new IMcpTool[] { new OverviewTool(bridge) });

            var args = new Dictionary<String, JsonElement> { ["namespace"] = Str("default") };
            var result = await catalog.CallAsync("f8_overview", args, CancellationToken.None);

            Assert.IsFalse(result.IsError);
            var status = result.StructuredContent!.Value;
            Assert.AreEqual("default", status.GetProperty("namespace").GetString());
            Assert.IsTrue(status.TryGetProperty("vertexCount", out _), "status carries the vertex count");
            Assert.IsTrue(status.TryGetProperty("availableAnalyticsAlgorithms", out _),
                "overview surfaces the available analytics algorithms for agent discovery");
        }

        /// <summary>
        ///   A not-loaded namespace (feature namespace-startup-load) answers /status with a residency
        ///   marker and NULL counts. Two things must hold, and both used to be broken by construction:
        ///   the bridge must be able to READ that body at all (non-nullable Int32 members threw on the
        ///   JSON null, failing the whole tool call), and the tool must report the residency instead of
        ///   an absent-count-as-zero, which an agent would act on.
        /// </summary>
        [TestMethod]
        public async Task Overview_OfANotLoadedNamespace_ReportsResidency_AndNoCounts()
        {
            var bridge = McpTestSupport.Bridge(new McpTestSupport.LambdaHandler(_ => Response(HttpStatusCode.OK,
                "{\"namespaceState\":\"notLoaded\",\"vertexCount\":null,\"edgeCount\":null,\"indices\":null," +
                "\"availableIndexPlugins\":null,\"availablePathPlugins\":null,\"availableAnalyticsPlugins\":null," +
                "\"usedMemory\":123,\"apiKeyRequired\":false,\"authenticated\":false}", "application/json")));
            var catalog = McpTestSupport.Catalog(new McpToolsOptions(), new IMcpTool[] { new OverviewTool(bridge) });

            var args = new Dictionary<String, JsonElement> { ["namespace"] = Str("archived") };
            var result = await catalog.CallAsync("f8_overview", args, CancellationToken.None);

            Assert.IsFalse(result.IsError, "an absent count must not fail the tool call");
            var status = result.StructuredContent!.Value;
            Assert.AreEqual("notLoaded", status.GetProperty("namespaceState").GetString());
            Assert.AreEqual(JsonValueKind.Null, status.GetProperty("vertexCount").ValueKind);
            Assert.AreEqual(JsonValueKind.Null, status.GetProperty("indexCount").ValueKind,
                "an index count of 0 would read as \"this namespace has no indices\"");
            Assert.AreEqual(JsonValueKind.Null, status.GetProperty("availablePathAlgorithms").ValueKind,
                "an empty list would read as \"this namespace can run nothing\"");
            StringAssert.Contains(
                String.Join(" ", result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text)),
                "not loaded", "the summary line must say why there are no counts");
        }

        /// <summary>
        ///   detail:"statistics" over a not-loaded namespace must NOT follow up into a
        ///   namespace-scoped route: /status is the only one that answers in that state, every other
        ///   one refuses with 503, and the bridge maps any non-2xx to a thrown BridgeError - which
        ///   would cost the agent the residency answer it was already holding. The loaded arm is
        ///   asserted in the same test so the skip cannot degrade into "never fetch statistics".
        /// </summary>
        [TestMethod]
        public async Task Overview_WithStatistics_OnANotLoadedNamespace_KeepsTheResidencyAnswer()
        {
            const String NotLoadedStatus =
                "{\"namespaceState\":\"notLoaded\",\"vertexCount\":null,\"edgeCount\":null,\"indices\":null," +
                "\"usedMemory\":123,\"apiKeyRequired\":false,\"authenticated\":false}";
            const String Problem503 =
                "{\"status\":503,\"title\":\"Namespace not loaded\",\"detail\":\"not loaded\"," +
                "\"namespace\":\"archived\",\"namespaceState\":\"notLoaded\"}";

            var statisticsCalls = 0;
            HttpResponseMessage Respond(HttpRequestMessage request, String statusBody, String statisticsBody)
            {
                if (request.RequestUri.AbsolutePath.EndsWith("/statistics", StringComparison.Ordinal))
                {
                    statisticsCalls++;
                    return statisticsBody == Problem503
                        ? Response(HttpStatusCode.ServiceUnavailable, Problem503, "application/problem+json")
                        : Response(HttpStatusCode.OK, statisticsBody, "application/json");
                }

                return Response(HttpStatusCode.OK, statusBody, "application/json");
            }

            var args = new Dictionary<String, JsonElement>
            {
                ["namespace"] = Str("archived"),
                ["detail"] = Str("statistics"),
            };

            // (i) Not loaded: the second response would be the 503, and it must never be requested.
            var refusing = McpTestSupport.Bridge(new McpTestSupport.LambdaHandler(
                r => Respond(r, NotLoadedStatus, Problem503)));
            var result = await McpTestSupport
                .Catalog(new McpToolsOptions(), new IMcpTool[] { new OverviewTool(refusing) })
                .CallAsync("f8_overview", args, CancellationToken.None);

            Assert.IsFalse(result.IsError, "a 503 from the follow-up must not replace the answer we already had");
            Assert.AreEqual(0, statisticsCalls, "the follow-up is skipped, not attempted and swallowed");
            var status = result.StructuredContent!.Value;
            Assert.AreEqual("notLoaded", status.GetProperty("namespaceState").GetString());
            Assert.IsFalse(status.TryGetProperty("statistics", out _));
            StringAssert.Contains(status.GetProperty("statisticsUnavailable").GetString(), "not loaded",
                "a silently absent block reads as \"nothing to report\", which is the wrong conclusion");

            // (ii) Loaded: the follow-up still happens, so the skip is conditional on residency.
            var serving = McpTestSupport.Bridge(new McpTestSupport.LambdaHandler(r => Respond(r,
                "{\"namespaceState\":\"ready\",\"vertexCount\":2,\"edgeCount\":1,\"usedMemory\":123}",
                "{\"vertexCount\":2}")));
            var loaded = await McpTestSupport
                .Catalog(new McpToolsOptions(), new IMcpTool[] { new OverviewTool(serving) })
                .CallAsync("f8_overview", args, CancellationToken.None);

            Assert.IsFalse(loaded.IsError);
            Assert.AreEqual(1, statisticsCalls, "a loaded namespace still gets its statistics block");
            Assert.IsTrue(loaded.StructuredContent!.Value.TryGetProperty("statistics", out _));
        }

        /// <summary>
        ///   The namespace directory carries the third state and absent counts too, so an agent
        ///   enumerating namespaces sees which ones cannot serve requests.
        /// </summary>
        [TestMethod]
        public async Task Overview_NamespaceDirectory_CarriesStateAndAbsentCounts()
        {
            var bridge = McpTestSupport.Bridge(new McpTestSupport.LambdaHandler(_ => Response(HttpStatusCode.OK,
                "{\"namespaces\":[{\"name\":\"archived\",\"state\":\"notLoaded\",\"vertexCount\":null," +
                "\"edgeCount\":null},{\"name\":\"default\",\"state\":\"ready\",\"vertexCount\":2,\"edgeCount\":1}]," +
                "\"maxNamespaces\":10000}", "application/json")));
            var catalog = McpTestSupport.Catalog(new McpToolsOptions(), new IMcpTool[] { new OverviewTool(bridge) });

            var result = await catalog.CallAsync("f8_overview", NoArgs, CancellationToken.None);

            Assert.IsFalse(result.IsError, "a null count in the list must not fail the listing");
            var namespaces = result.StructuredContent!.Value.GetProperty("namespaces").EnumerateArray().ToList();
            var archived = namespaces.Single(n => n.GetProperty("name").GetString() == "archived");
            Assert.AreEqual("notLoaded", archived.GetProperty("state").GetString());
            Assert.AreEqual(JsonValueKind.Null, archived.GetProperty("vertexCount").ValueKind);
            var byDefault = namespaces.Single(n => n.GetProperty("name").GetString() == "default");
            Assert.AreEqual(2, byDefault.GetProperty("vertexCount").GetInt32());
        }
    }
}
