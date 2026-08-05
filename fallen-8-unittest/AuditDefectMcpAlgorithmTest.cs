// MIT License
//
// AuditDefectMcpAlgorithmTest.cs
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
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Mcp.Configuration;
using NoSQL.GraphDB.Mcp.Tools;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Audit defect B49 (engine -> REST -> MCP propagation, field level): the algorithm selector
    ///   of <c>f8_paths</c> and <c>f8_subgraph</c>. REST resolves a runtime-registered Path plugin
    ///   by name and <c>PUT /subgraph</c> takes an optional <c>algorithm</c>, so neither tool may
    ///   advertise a closed enum (f8_paths did) or hide the knob entirely (f8_subgraph did) - an
    ///   MCP client that validates arguments against the advertised schema would then have strictly
    ///   less reach than Studio or raw REST. The route/method guards (<c>McpContractTest</c>,
    ///   <c>McpRestCoverageTest</c>) are endpoint-level and cannot see this, hence these
    ///   field-level tests: they pin the schema shape AND what actually reaches the wire.
    /// </summary>
    [TestClass]
    public class AuditDefectMcpAlgorithmTest
    {
        /// <summary>Captures the bridged HTTP request (method, path, body) and answers a canned
        /// 200, so a test can assert what the tool really sent downstream.</summary>
        private sealed class CapturingHandler : HttpMessageHandler
        {
            private readonly String _responseBody;

            public CapturingHandler(String responseBody)
            {
                _responseBody = responseBody;
            }

            public Int32 Calls
            {
                get; private set;
            }

            public HttpMethod Method
            {
                get; private set;
            }

            public String RequestPath
            {
                get; private set;
            }

            public String Body
            {
                get; private set;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls++;
                Method = request.Method;
                RequestPath = request.RequestUri == null ? null : request.RequestUri.AbsolutePath;
                Body = request.Content == null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
                };
            }
        }

        private static JsonElement Property(JsonElement schema, String name)
        {
            return schema.GetProperty("properties").GetProperty(name);
        }

        private static JsonElement SentBody(CapturingHandler handler)
        {
            Assert.AreEqual(1, handler.Calls, "the tool issued exactly one downstream request");
            Assert.IsNotNull(handler.Body, "the downstream request carried a JSON body");
            return JsonDocument.Parse(handler.Body).RootElement;
        }

        // --- f8_paths -----------------------------------------------------------------------

        [TestMethod]
        public void Paths_AlgorithmArgument_IsAFreeFormString_NotAClosedEnum()
        {
            var tool = new PathsTool(McpTestSupport.Bridge(new CapturingHandler("[]")));

            var algorithm = Property(tool.Describe(new McpToolsOptions()).InputSchema, "algorithm");

            Assert.AreEqual("string", algorithm.GetProperty("type").GetString());
            Assert.IsFalse(algorithm.TryGetProperty("enum", out _),
                "a closed enum would forbid the registered Path plugin names REST resolves (B49)");
            var description = algorithm.GetProperty("description").GetString();
            StringAssert.Contains(description, "BLS", "the built-ins stay discoverable from the description");
            StringAssert.Contains(description, "DIJKSTRA");
            StringAssert.Contains(description, "availablePathAlgorithms",
                "the description points at the discovery route for registered plugins");
        }

        [TestMethod]
        public async Task Paths_RegisteredPluginName_ReachesRestVerbatim()
        {
            var handler = new CapturingHandler("[]");
            var tool = new PathsTool(McpTestSupport.Bridge(handler));

            var result = await tool.InvokeAsync(
                McpTestSupport.Args("{\"from\":1,\"to\":2,\"algorithm\":\"My Registered Path Plugin\"}"),
                new McpToolsOptions(),
                CancellationToken.None);

            Assert.IsFalse(result.IsError);
            Assert.AreEqual(HttpMethod.Post, handler.Method);
            Assert.AreEqual("/path/1/to/2", handler.RequestPath);
            Assert.AreEqual("My Registered Path Plugin", SentBody(handler).GetProperty("pathAlgorithmName").GetString(),
                "the handler forwards the name unchanged - no client-side whitelist");
        }

        [TestMethod]
        public async Task Paths_OmittedAlgorithm_StillDefaultsToBls()
        {
            var handler = new CapturingHandler("[]");
            var tool = new PathsTool(McpTestSupport.Bridge(handler));

            await tool.InvokeAsync(
                McpTestSupport.Args("{\"from\":3,\"to\":4}"),
                new McpToolsOptions(),
                CancellationToken.None);

            Assert.AreEqual("BLS", SentBody(handler).GetProperty("pathAlgorithmName").GetString(),
                "widening the schema must not move the default an existing agent call relies on");
        }

        // --- f8_subgraph --------------------------------------------------------------------

        [TestMethod]
        public void Subgraph_AdvertisesTheAlgorithmSelector_AsAFreeFormString()
        {
            var tool = new SubgraphTool(McpTestSupport.Bridge(new CapturingHandler("{}")));

            var algorithm = Property(tool.Describe(new McpToolsOptions()).InputSchema, "algorithm");

            Assert.AreEqual("string", algorithm.GetProperty("type").GetString());
            Assert.IsFalse(algorithm.TryGetProperty("enum", out _),
                "the REST selector accepts any available SubGraph plugin name, built-in or registered");
            StringAssert.Contains(algorithm.GetProperty("description").GetString(), "breadth-first",
                "the description names the default the omitted field selects");
        }

        [TestMethod]
        public async Task Subgraph_Algorithm_IsForwardedInThePutBody()
        {
            var handler = new CapturingHandler("{\"name\":\"net\",\"vertexCount\":3}");
            var tool = new SubgraphTool(McpTestSupport.Bridge(handler));

            var result = await tool.InvokeAsync(
                McpTestSupport.Args("{\"name\":\"net\",\"storedQuery\":\"person-net\",\"algorithm\":\"My Registered SubGraph Plugin\"}"),
                new McpToolsOptions(),
                CancellationToken.None);

            Assert.IsFalse(result.IsError);
            Assert.AreEqual(HttpMethod.Put, handler.Method);
            Assert.AreEqual("/subgraph", handler.RequestPath);
            var body = SentBody(handler);
            Assert.AreEqual("net", body.GetProperty("name").GetString());
            Assert.AreEqual("person-net", body.GetProperty("storedQuery").GetString());
            Assert.AreEqual("My Registered SubGraph Plugin", body.GetProperty("algorithm").GetString(),
                "the wire field is 'algorithm', exactly as SubGraphSpecification declares it");
        }

        [TestMethod]
        public async Task Subgraph_WithoutAlgorithm_OmitsTheField_SoRestPicksTheBuiltIn()
        {
            var handler = new CapturingHandler("{\"name\":\"net\",\"vertexCount\":0}");
            var tool = new SubgraphTool(McpTestSupport.Bridge(handler));

            await tool.InvokeAsync(
                McpTestSupport.Args("{\"name\":\"net\",\"storedQuery\":\"person-net\"}"),
                new McpToolsOptions(),
                CancellationToken.None);

            var body = SentBody(handler);
            Assert.IsFalse(body.TryGetProperty("algorithm", out _),
                "an absent selector must not be sent as null/empty - the built-in BFS is the server-side default");
            Assert.IsFalse(body.TryGetProperty("vertexFilter", out _), "no inline fragment is invented");
        }

        [TestMethod]
        public async Task Subgraph_EmptyAlgorithm_IsTreatedAsAbsent()
        {
            var handler = new CapturingHandler("{\"name\":\"net\"}");
            var tool = new SubgraphTool(McpTestSupport.Bridge(handler));

            await tool.InvokeAsync(
                McpTestSupport.Args("{\"name\":\"net\",\"storedQuery\":\"person-net\",\"algorithm\":\"\"}"),
                new McpToolsOptions(),
                CancellationToken.None);

            Assert.IsFalse(SentBody(handler).TryGetProperty("algorithm", out _),
                "an empty selector would be REST's 'absent' anyway; do not spend a wire field on it");
        }

        [TestMethod]
        public async Task Subgraph_AlgorithmAlone_IsStillRejectedBeforeAnyRequest()
        {
            var handler = new CapturingHandler("{}");
            var tool = new SubgraphTool(McpTestSupport.Bridge(handler));

            var result = await tool.InvokeAsync(
                McpTestSupport.Args("{\"name\":\"net\",\"algorithm\":\"My Registered SubGraph Plugin\"}"),
                new McpToolsOptions(),
                CancellationToken.None);

            Assert.IsTrue(result.IsError,
                "an algorithm alone selects nothing: the storedQuery/inline-filter pre-check is unchanged");
            Assert.AreEqual(0, handler.Calls, "the pre-check fails locally, without touching the target");
        }

        [TestMethod]
        public async Task Subgraph_CodeCapabilityOff_KeepsInlineFragmentsOutOfTheBody()
        {
            var handler = new CapturingHandler("{\"name\":\"net\"}");
            var tool = new SubgraphTool(McpTestSupport.Bridge(handler));

            await tool.InvokeAsync(
                McpTestSupport.Args(
                    "{\"name\":\"net\",\"storedQuery\":\"person-net\",\"algorithm\":\"My Registered SubGraph Plugin\","
                    + "\"vertexFilter\":\"return (v) => true;\",\"edgeFilter\":\"return (e) => true;\"}"),
                new McpToolsOptions(),
                CancellationToken.None);

            var body = SentBody(handler);
            Assert.IsFalse(body.TryGetProperty("vertexFilter", out _),
                "fragments are dropped without the code capability (defence beyond the schema)");
            Assert.IsFalse(body.TryGetProperty("edgeFilter", out _));
            Assert.AreEqual("My Registered SubGraph Plugin", body.GetProperty("algorithm").GetString(),
                "the algorithm selector is code-free and survives the code gate");
        }
    }
}
