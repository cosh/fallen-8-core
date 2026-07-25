// MIT License
//
// McpBridgeTest.cs
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
    }
}
