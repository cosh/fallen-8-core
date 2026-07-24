// MIT License
//
// McpToolSurfaceTest.cs
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
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Protocol;
using NoSQL.GraphDB.Mcp.Configuration;
using NoSQL.GraphDB.Mcp.Tools;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The token-frugal, hand-authored tool surface (feature mcp-server, Phase 0/§3.2): the
    ///   schema-shape proof (flat, enum-discriminated, NO oneOf/anyOf/$ref — the load-bearing
    ///   assumption the whole design rests on) and tier gating at both list and call.
    /// </summary>
    [TestClass]
    public class McpToolSurfaceTest
    {
        private static readonly IReadOnlyDictionary<String, JsonElement> NoArgs =
            new Dictionary<String, JsonElement>();

        /// <summary>A write-tier stand-in so tier gating can be proven before Phase 2 lands the
        /// real write tools.</summary>
        private sealed class StubWriteTool : IMcpTool
        {
            public String Name => "f8_stub_write";

            public ToolTier Tier => ToolTier.Write;

            public Tool Describe(McpToolsOptions tools) => new()
            {
                Name = Name,
                Description = "stub",
                InputSchema = SchemaBuilder.Empty(),
            };

            public Task<CallToolResult> InvokeAsync(
                IReadOnlyDictionary<String, JsonElement> arguments,
                McpToolsOptions tools,
                CancellationToken cancellationToken)
            {
                return Task.FromResult(ToolResults.Ok("stub ok"));
            }
        }

        private static ToolCatalog Build(McpToolsOptions tools)
        {
            var bridge = McpTestSupport.Bridge(
                new McpTestSupport.LambdaHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
            return McpTestSupport.Catalog(tools, new IMcpTool[] { new OverviewTool(bridge), new StubWriteTool() });
        }

        [TestMethod]
        public void Overview_Schema_IsFlatEnumDiscriminated_NoComposition()
        {
            var overview = Build(new McpToolsOptions()).ListTools().Single(t => t.Name == "f8_overview");
            var schema = overview.InputSchema.GetRawText();

            StringAssert.Contains(schema, "\"type\":\"object\"", "the input schema is a JSON-Schema object");
            StringAssert.Contains(schema, "namespace", "the overview exposes the optional namespace parameter");

            // The load-bearing token/accuracy decision (spec §3.2): flat, no composition keywords.
            Assert.IsFalse(schema.Contains("oneOf", StringComparison.Ordinal), "no oneOf");
            Assert.IsFalse(schema.Contains("anyOf", StringComparison.Ordinal), "no anyOf");
            Assert.IsFalse(schema.Contains("allOf", StringComparison.Ordinal), "no allOf");
            Assert.IsFalse(schema.Contains("$ref", StringComparison.Ordinal), "no $ref");
        }

        [TestMethod]
        public void Overview_CarriesReadOnlyAndClosedWorldAnnotations()
        {
            var overview = Build(new McpToolsOptions()).ListTools().Single(t => t.Name == "f8_overview");

            Assert.IsNotNull(overview.Annotations);
            Assert.AreEqual(true, overview.Annotations!.ReadOnlyHint);
            Assert.AreEqual(false, overview.Annotations.OpenWorldHint);
            Assert.AreEqual(true, overview.Annotations.IdempotentHint);
        }

        [TestMethod]
        public void ListTools_DefaultTiers_ExposesOnlyReadTools()
        {
            var names = Build(new McpToolsOptions()).ListTools().Select(t => t.Name).ToList();

            CollectionAssert.Contains(names, "f8_overview");
            CollectionAssert.DoesNotContain(names, "f8_stub_write",
                "a write-tier tool is absent from tools/list when the write tier is off");
        }

        [TestMethod]
        public void ListTools_WriteEnabled_ExposesWriteTools()
        {
            var names = Build(new McpToolsOptions { EnableWrite = true }).ListTools().Select(t => t.Name).ToList();

            CollectionAssert.Contains(names, "f8_stub_write");
        }

        [TestMethod]
        public async Task CallTool_DisabledTier_IsRejectedEvenWhenNameIsKnown()
        {
            // The name is real; only the tier is off. Defends against a client replaying a cached list.
            var result = await Build(new McpToolsOptions()).CallAsync("f8_stub_write", NoArgs, CancellationToken.None);

            Assert.IsTrue(result.IsError, "calling a disabled-tier tool must be an error");
        }

        [TestMethod]
        public async Task CallTool_EnabledTier_Invokes()
        {
            var result = await Build(new McpToolsOptions { EnableWrite = true })
                .CallAsync("f8_stub_write", NoArgs, CancellationToken.None);

            Assert.IsFalse(result.IsError);
        }

        [TestMethod]
        public async Task CallTool_UnknownName_IsError()
        {
            var result = await Build(new McpToolsOptions()).CallAsync("f8_nope", NoArgs, CancellationToken.None);

            Assert.IsTrue(result.IsError);
        }
    }
}
