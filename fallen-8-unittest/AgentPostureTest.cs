// MIT License
//
// AgentPostureTest.cs
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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Agents.Configuration;
using NoSQL.GraphDB.Agents.Hosting;
using NoSQL.GraphDB.Agents.Runtime;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The agent host's two startup probes (feature agent-host): the chat gateway's reachability
    ///   and the MCP server's tool list.
    ///
    ///   <para>
    ///     Both are best-effort and neither gates startup, which makes their OUTCOMES the whole of
    ///     what an operator has to diagnose from. So every outcome is pinned here, including the two
    ///     an operator actually has to act on: a capability that is off, and a key that is not
    ///     accepted.
    ///   </para>
    ///   <para>
    ///     The MCP half runs the REAL protocol against the REAL <c>fallen-8-mcp</c> server, hosted
    ///     in process. Before this, only the toolset's catch block ran anywhere in the suite: a
    ///     successful connect had no coverage at all, so a change that broke the transport, the
    ///     handshake or the tool mapping would have been found by an operator rather than by CI.
    ///   </para>
    /// </summary>
    [TestClass]
    public class AgentPostureTest
    {
        #region the chat gateway probe

        [TestMethod]
        public async Task AReachableGatewayIsReportedAsReachable()
        {
            using var http = Answering(HttpStatusCode.OK, "{\"backend\":\"Nahil\",\"models\":[]}");

            Assert.AreEqual("reachable",
                await AgentsHost.ProbeChatAsync(http, Logger(), CancellationToken.None));
        }

        [TestMethod]
        public async Task TheTwoStatusesAnOperatorHasToActOnAreNamedRatherThanLumpedIn()
        {
            // 401 and 403 mean two fixable things: the Chat capability is off on that instance, or
            // this host's key is not accepted. Both are reported distinctly, because an agent
            // spawned in either state fails on its first model call with the same answer and the
            // status route is where somebody looks first.
            using (var http = Answering(HttpStatusCode.Unauthorized, "{}"))
            {
                Assert.AreEqual("refused:401",
                    await AgentsHost.ProbeChatAsync(http, Logger(), CancellationToken.None));
            }

            using (var http = Answering(HttpStatusCode.Forbidden, "{}"))
            {
                Assert.AreEqual("refused:403",
                    await AgentsHost.ProbeChatAsync(http, Logger(), CancellationToken.None));
            }
        }

        [TestMethod]
        public async Task AnyOtherStatusIsCarriedAsItselfRatherThanFlattened()
        {
            using var http = Answering(HttpStatusCode.BadGateway, "{}");

            Assert.AreEqual("status:502",
                await AgentsHost.ProbeChatAsync(http, Logger(), CancellationToken.None));
        }

        [TestMethod]
        public async Task AnUnreachableGatewayIsReportedRatherThanThrown()
        {
            // The probe must never throw: it runs in a hosted service, and a throw there is a host
            // that will not start, which the proxy reports as a runtime that did not answer.
            using var http = Failing(new HttpRequestException("no route to host"));

            Assert.AreEqual("unreachable",
                await AgentsHost.ProbeChatAsync(http, Logger(), CancellationToken.None));
        }

        [TestMethod]
        public async Task TheCallersOwnCancellationPropagatesRatherThanBecomingUnreachable()
        {
            // A host shutting down while the probe is in flight is not an unreachable gateway, and
            // reporting it as one would put a false line in the log of every restart.
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            using var http = Failing(new OperationCanceledException());

            // Caught rather than ThrowsExceptionAsync<T>, which demands the exact type: HttpClient
            // rewraps a cancellation as TaskCanceledException, and what matters is that it is a
            // cancellation at all rather than which of the two the transport chose.
            try
            {
                await AgentsHost.ProbeChatAsync(http, Logger(), cancelled.Token);
                Assert.Fail("the caller's cancellation was swallowed and reported as a posture");
            }
            catch (OperationCanceledException)
            {
            }
        }

        [TestMethod]
        public async Task TheProbeAsksForTheModelCatalogueAndNothingElse()
        {
            // The route matters: this host may call the chat gateway and no other REST family, which
            // CodeQualityTest pins statically. This pins what it actually requests at run time.
            var handler = new RecordingHandler(HttpStatusCode.OK, "{}");
            using var http = new HttpClient(handler) { BaseAddress = new Uri("http://instance.invalid/") };

            await AgentsHost.ProbeChatAsync(http, Logger(), CancellationToken.None);

            Assert.AreEqual(1, handler.Requests.Count);
            Assert.AreEqual(HttpMethod.Get, handler.Requests[0].Method);
            Assert.AreEqual("/chat/models", handler.Requests[0].Path);
        }

        #endregion

        #region the MCP toolset, against the real server

        [TestMethod]
        public async Task TheToolsetConnectsToARealMcpServerAndReadsItsAdvertisedTools()
        {
            // The success path, which nothing else in the suite reaches. The server is the shipped
            // fallen-8-mcp, hosted in process and speaking its real protocol over its real
            // transport, so the handshake and the tool mapping are what is under test rather than a
            // stub's idea of them.
            using var server = new McpServerFactory();
            await using var toolset = Toolset(server);

            Assert.IsTrue(await toolset.ConnectAsync(CancellationToken.None),
                "the toolset could not connect to a hosted fallen-8-mcp: " + toolset.Failure);

            Assert.IsTrue(toolset.Connected);
            Assert.IsNull(toolset.Failure);
            Assert.IsTrue(toolset.Tools.Count > 0,
                "a connected server advertising no tools would leave every agent unable to reach a graph");

            // Every advertised tool has to arrive as something the framework can offer a model.
            foreach (var tool in toolset.Tools)
            {
                Assert.IsFalse(String.IsNullOrWhiteSpace(tool.Name));
                Assert.IsInstanceOfType<Microsoft.Extensions.AI.AIFunction>(tool,
                    "a tool the framework cannot invoke is worse than one that is absent");
            }

            // And a role's allowlist narrows THAT list, which is the only place the two meet.
            var options = new AgentsOptions();
            options.Roles["assistant"] = new AgentsOptions.RoleOptions
            {
                Tools = new List<String> { toolset.Tools[0].Name },
            };
            Assert.IsTrue(RoleCatalog.Load(options).TryGet("assistant", out var role, out _));
            Assert.AreEqual(1, role.Filter(toolset.Tools).Count);
        }

        [TestMethod]
        public async Task ASecondConnectReplacesTheSessionRatherThanLeakingIt()
        {
            using var server = new McpServerFactory();
            await using var toolset = Toolset(server);

            Assert.IsTrue(await toolset.ConnectAsync(CancellationToken.None), toolset.Failure);
            var first = toolset.Tools.Count;

            Assert.IsTrue(await toolset.ConnectAsync(CancellationToken.None), toolset.Failure);
            Assert.AreEqual(first, toolset.Tools.Count, "the second connect saw a different server");
            Assert.IsTrue(toolset.Connected);
        }

        [TestMethod]
        public async Task AnUnreachableServerLeavesTheToolsetEmptyWithAReasonRatherThanThrowing()
        {
            // Empty AND a reason, because empty alone cannot be told from a server that advertises
            // nothing, and an operator has to know which of the two they have.
            await using var toolset = new McpToolset(
                Options.Create(Configured("http://127.0.0.1:1", connectSeconds: 1)),
                TestLoggerFactory.Create());

            Assert.IsFalse(await toolset.ConnectAsync(CancellationToken.None));
            Assert.IsFalse(toolset.Connected);
            Assert.AreEqual(0, toolset.Tools.Count);
            Assert.IsFalse(String.IsNullOrWhiteSpace(toolset.Failure));
        }

        [TestMethod]
        public async Task DisposingTwiceIsANoOpBecauseAContainerDoesExactlyThat()
        {
            // Not hypothetical: the host registers this as a singleton and also resolves it, so the
            // second dispose is the ordinary case. It threw ObjectDisposedException out of shutdown
            // until it was made idempotent.
            using var server = new McpServerFactory();
            var toolset = Toolset(server);

            Assert.IsTrue(await toolset.ConnectAsync(CancellationToken.None), toolset.Failure);

            await toolset.DisposeAsync();
            await toolset.DisposeAsync();
        }

        #endregion

        #region harness

        private static ILogger Logger()
        {
            return TestLoggerFactory.Create().CreateLogger("posture");
        }

        private static AgentsOptions Configured(String endpoint, Int32 connectSeconds = 15)
        {
            var options = new AgentsOptions();
            options.Mcp.Endpoint = endpoint;
            options.Mcp.ConnectTimeoutSeconds = connectSeconds;
            return options;
        }

        private static McpToolset Toolset(McpServerFactory server)
        {
            // The endpoint is nominal: the handler below reaches the in-process server whatever the
            // authority says. It still has to be a legal absolute URI, which is itself worth having
            // covered.
            return new McpToolset(Options.Create(Configured("http://mcp.localhost")),
                TestLoggerFactory.Create(), server.Server.CreateHandler());
        }

        /// <summary>A client whose every request gets one canned answer.</summary>
        private static HttpClient Answering(HttpStatusCode status, String body)
        {
            return new HttpClient(new RecordingHandler(status, body))
            {
                BaseAddress = new Uri("http://instance.invalid/"),
            };
        }

        /// <summary>A client whose every request fails the way a real one would.</summary>
        private static HttpClient Failing(Exception failure)
        {
            return new HttpClient(new ThrowingHandler(failure))
            {
                BaseAddress = new Uri("http://instance.invalid/"),
            };
        }

        private sealed class RecordingHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _status;
            private readonly String _body;

            public RecordingHandler(HttpStatusCode status, String body)
            {
                _status = status;
                _body = body;
            }

            public List<(HttpMethod Method, String Path)> Requests { get; }
                = new List<(HttpMethod, String)>();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Requests.Add((request.Method, request.RequestUri!.AbsolutePath));
                return Task.FromResult(new HttpResponseMessage(_status)
                {
                    Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json"),
                });
            }
        }

        private sealed class ThrowingHandler : HttpMessageHandler
        {
            private readonly Exception _failure;

            public ThrowingHandler(Exception failure)
            {
                _failure = failure;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                throw _failure;
            }
        }

        /// <summary>
        ///   The shipped MCP server, hosted. Its auth mode is left at its default and its graph
        ///   target points nowhere reachable on purpose: this test is about the PROTOCOL handshake
        ///   and the advertised tool list, neither of which touches a graph.
        /// </summary>
        private sealed class McpServerFactory : WebApplicationFactory<NoSQL.GraphDB.Mcp.Program>
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseSetting("Fallen8Target:BaseUrl", "http://graph.invalid:19999/");
            }
        }

        #endregion
    }
}
