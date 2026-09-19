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

        /// <summary>
        ///   The reconnect a run asks for: with no session, it makes one. The handshake used to be
        ///   made once at startup, so a host that came up before the MCP server stayed toolless for
        ///   the life of the process.
        /// </summary>
        [TestMethod]
        public async Task ReconnectingMakesTheSessionAStartupRaceLost()
        {
            using var server = new McpServerFactory();
            await using var toolset = Toolset(server);

            Assert.IsFalse(toolset.Connected, "nothing has connected yet, which is the arrangement");

            await toolset.EnsureConnectedAsync(CancellationToken.None);

            Assert.IsTrue(toolset.Connected, toolset.Failure);
            Assert.IsTrue(toolset.Tools.Count > 0,
                "a session with no tools is not a recovered host");
        }

        /// <summary>
        ///   And it does not hammer. A server that is down costs a run ONE handshake per connect
        ///   timeout rather than one per run, and a session that works is never torn down to make
        ///   an identical one. Counted at the transport, so the assertion is on attempts rather
        ///   than on how long something took.
        /// </summary>
        [TestMethod]
        public async Task ReconnectingIsBoundedByTheConnectTimeoutAndSkippedWhileConnected()
        {
            var handler = new RecordingHandler(HttpStatusCode.InternalServerError, "no");
            await using var toolset = new McpToolset(
                Options.Create(Configured("http://mcp.localhost", connectSeconds: 15)),
                TestLoggerFactory.Create(), handler);

            await toolset.EnsureConnectedAsync(CancellationToken.None);
            Assert.IsFalse(toolset.Connected, "the handler answers 500, so there is no session");
            var afterFirst = handler.Requests.Count;
            Assert.IsTrue(afterFirst > 0, "the first reconnect never reached the transport");

            await toolset.EnsureConnectedAsync(CancellationToken.None);
            Assert.AreEqual(afterFirst, handler.Requests.Count,
                "a second run inside the connect timeout tried again, so a server that is down "
                + "costs every run a handshake");

            // The other half: a toolset that IS connected asks nothing at all.
            using var server = new McpServerFactory();
            await using var live = Toolset(server);
            Assert.IsTrue(await live.ConnectAsync(CancellationToken.None), live.Failure);
            var tools = live.Tools;

            await live.EnsureConnectedAsync(CancellationToken.None);
            Assert.IsTrue(live.Connected);
            Assert.AreSame(tools, live.Tools,
                "a working session was torn down and replaced by an identical one");
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

        #region the startup posture line

        [TestMethod]
        public void ThePostureLineNamesTheCeilingThatBoundsARunRatherThanTheDefault()
        {
            // The line's own stated job is "what bounds a run", and it printed
            // DefaultTokenBudget, which bounds nothing: TryAdmit honours a caller's own
            // tokenBudget up to MaxTokenBudget, so the ceiling is the only enforced bound. An
            // operator reading the shipped line recorded 100,000 tokens as the most a run could
            // cost on a host whose ceiling is four times that. Nothing asserted any of these
            // lines, which is why it survived.
            using var sink = new TestLogSink();
            var options = new AgentsOptions();
            options.Limits.DefaultTokenBudget = 100000;
            options.Limits.MaxTokenBudget = 400000;

            Posture(sink, options);

            Assert.IsTrue(sink.Contains(LogLevel.Information, "400000", "bounded at"),
                "the enforced ceiling is missing from the one line that claims to state the bounds");
            Assert.IsTrue(sink.Contains(LogLevel.Information, "100000", "unless a caller asks"),
                "the default is still worth naming, as what a caller who asks for nothing gets");
        }

        [TestMethod]
        public void ADisabledCapReadsAsUnboundedRatherThanAsABoundOfZero()
        {
            // EVERY limit these lines print treats a non-positive value as OFF. Printing the raw
            // number told an operator who had deliberately disabled one that this host was the
            // strictest possible: "bounded at 0 model calls" for a host with no step cap at all,
            // which inverts the meaning of the whole line.
            //
            // The count in this comment went stale twice, so it is gone: it said seven while
            // DefaultTokenBudget was still printing a raw 0 (a spawn naming no budget takes the
            // default, and the meter enforces only a budget above zero), then eight while the two
            // swarm caps were printed nowhere at all. Every limit the lines print is set to 0 here,
            // which is the only version of this test that can catch the next one.
            using var sink = new TestLogSink();
            var options = new AgentsOptions();
            options.Limits.MaxStepsPerRun = 0;
            options.Limits.MaxToolCallsPerRun = 0;
            options.Limits.MaxRunSeconds = 0;
            options.Limits.MaxConcurrentAgents = 0;
            options.Limits.MaxTokenBudget = 0;
            options.Limits.DefaultTokenBudget = 0;
            options.Limits.RetainFinishedMinutes = 0;
            options.Limits.MaxRetainedAgents = 0;
            options.Limits.MaxSwarmDepth = 0;
            options.Limits.MaxWorkersPerOrchestrator = 0;

            Posture(sink, options);

            var bounds = sink.Entries.Single(e => e.Message.Contains("A run is bounded at",
                StringComparison.Ordinal));
            Assert.IsFalse(bounds.Message.Contains("0", StringComparison.Ordinal),
                "a cap that is switched off printed as a number: " + bounds.Message);
            Assert.AreEqual(6, bounds.Message.Split("unlimited").Length - 1,
                "the line prints six bounds and all six are off, so six read as unlimited: "
                + bounds.Message);

            var retention = sink.Entries.Single(e => e.Message.Contains("Nothing here is durable",
                StringComparison.Ordinal));
            Assert.AreEqual(2, retention.Message.Split("unlimited").Length - 1,
                "retention and its ceiling are both off: " + retention.Message);

            var swarm = sink.Entries.Single(e => e.Message.Contains("A swarm may nest",
                StringComparison.Ordinal));
            Assert.AreEqual(2, swarm.Message.Split("unlimited").Length - 1,
                "the depth and the per-orchestrator count are both off: " + swarm.Message);
        }

        /// <summary>
        ///   The posture line states the bind it can observe and makes no claim about port
        ///   publication, which is a property of a compose file that no process can see: the old
        ///   line printed "this port is not published" unchanged under a bare <c>dotnet run</c>
        ///   bound to every interface.
        /// </summary>
        [TestMethod]
        public void TheStartupLineStatesItsBindRatherThanAPortPublicationItCannotObserve()
        {
            using var sink = new TestLogSink();

            Posture(sink, new AgentsOptions());

            var listening = sink.Entries.Single(e => e.Message.Contains("Agent host listening on",
                StringComparison.Ordinal));
            Assert.AreEqual(LogLevel.Information, listening.Level);
            StringAssert.Contains(listening.Message, "127.0.0.1");
            StringAssert.Contains(listening.Message, "8120");
            StringAssert.Contains(listening.Message, "authenticates no caller");
            Assert.IsFalse(
                sink.Entries.Any(e => e.Message.Contains("published", StringComparison.Ordinal)),
                "nothing this process can observe justifies the word: " + listening.Message);
        }

        /// <summary>
        ///   The bind the image actually sets is WARNED about and not refused: the shipped container
        ///   binds every interface deliberately, and unlike the MCP server this host has no auth
        ///   mode to fall back to, so a refusal would leave no way to run it.
        /// </summary>
        [TestMethod]
        public void ANonLoopbackBindIsWarnedAboutRatherThanRefused()
        {
            using var sink = new TestLogSink();

            Posture(sink, new AgentsOptions { BindAddress = "0.0.0.0" });

            var warning = sink.Entries.Single(e => e.Level == LogLevel.Warning
                && e.Message.Contains("not loopback", StringComparison.Ordinal));
            StringAssert.Contains(warning.Message, "0.0.0.0");
            StringAssert.Contains(warning.Message, "8120");
            Assert.IsTrue(
                sink.Entries.Any(e => e.Message.Contains("Agent host listening on",
                    StringComparison.Ordinal)),
                "a warning instead of the posture line would hide what the host is doing");
        }

        /// <summary>
        ///   The control arm, which is what makes the warning a signal rather than noise on every
        ///   start: every loopback spelling is silent, and the wildcard forms Kestrel accepts are
        ///   not loopback and do warn.
        /// </summary>
        [TestMethod]
        public void ALoopbackBindDrawsNoWarningSoTheWarningMeansSomething()
        {
            foreach (var address in new[] { "127.0.0.1", "localhost", "::1", "127.0.0.5" })
            {
                using var sink = new TestLogSink();
                Posture(sink, new AgentsOptions { BindAddress = address });
                Assert.IsFalse(
                    sink.Entries.Any(e => e.Message.Contains("not loopback", StringComparison.Ordinal)),
                    address + " was warned about, so the warning is printed on every start and "
                    + "reads as noise");
            }

            using var wildcard = new TestLogSink();
            Posture(wildcard, new AgentsOptions { BindAddress = "*" });
            Assert.IsTrue(wildcard.Entries.Any(e => e.Level == LogLevel.Warning
                    && e.Message.Contains("not loopback", StringComparison.Ordinal)),
                "a wildcard bind is not loopback, which is the side that has to warn");
        }

        /// <summary>
        ///   The startup line prints the deadline a call GETS, not the number an operator wrote:
        ///   <c>Fallen8Target:TimeoutSeconds</c> is floored at 1 rather than switched off, and the
        ///   line said "deadline 0s" for a host that fails every model call after a second.
        /// </summary>
        [TestMethod]
        public void TheStartupLinePrintsTheDeadlineInForceRatherThanTheConfiguredZero()
        {
            using var sink = new TestLogSink();

            Posture(sink, new AgentsOptions(), new Fallen8TargetOptions { TimeoutSeconds = 0 });

            var line = sink.Entries.Single(e => e.Message.Contains("Completions come from",
                StringComparison.Ordinal));
            StringAssert.Contains(line.Message, "deadline 1s",
                "the line reports the number an operator wrote rather than the deadline a call "
                + "gets: " + line.Message);
        }


        [TestMethod]
        public void ACapThatISSetStillPrintsItsNumberBesideTheOnesThatAreOff()
        {
            // The control arm the all-zero case cannot give: a host with a real step cap and no
            // token ceiling has to read as both, or "unlimited" would be the answer to every
            // question and the line would carry no information at all.
            using var sink = new TestLogSink();
            var options = new AgentsOptions();
            options.Limits.MaxStepsPerRun = 24;
            options.Limits.MaxTokenBudget = 0;
            options.Limits.DefaultTokenBudget = 100000;

            Posture(sink, options);

            var bounds = sink.Entries.Single(e => e.Message.Contains("A run is bounded at",
                StringComparison.Ordinal));

            StringAssert.Contains(bounds.Message, "24 model calls");
            StringAssert.Contains(bounds.Message, "unlimited tokens",
                "the ceiling is off, so it is unlimited whatever the default says: " + bounds.Message);
            StringAssert.Contains(bounds.Message, "100000",
                "the default is still what a caller who asks for nothing gets, so it is still "
                + "worth printing when the ceiling is off");
        }

        /// <summary>The posture line as the host writes it, with no MCP server and no gateway: this
        /// is about the caps it prints, and neither probe contributes one.</summary>
        private static void Posture(TestLogSink sink, AgentsOptions options,
            Fallen8TargetOptions target = null)
        {
            AgentsHost.LogStartupPosture(sink.CreateFactory().CreateLogger("posture"), options,
                target ?? new Fallen8TargetOptions(), RoleCatalog.Load(options), new EmptyToolSource());
        }

        private sealed class EmptyToolSource : IAgentToolSource
        {
            public IReadOnlyList<Microsoft.Extensions.AI.AITool> Tools
                => Array.Empty<Microsoft.Extensions.AI.AITool>();

            public Boolean Connected => false;

            public String Failure => "no server configured for this test";

            public System.Threading.Tasks.Task EnsureConnectedAsync(
                System.Threading.CancellationToken cancellationToken = default)
                => System.Threading.Tasks.Task.CompletedTask;
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
