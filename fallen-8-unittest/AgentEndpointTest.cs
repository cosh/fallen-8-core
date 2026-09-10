// MIT License
//
// AgentEndpointTest.cs
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
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Agents;
using NoSQL.GraphDB.App.Integrations;
// The capturing log sink, reused rather than written twice. It is a general test utility that
// happens to live in the integrations project because its conformance suite ships as product code;
// the no-leak rule it serves is the same rule here.
using NoSQL.GraphDB.Integrations.Conformance;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Pins the HTTP surface of the agent-host feature through both hosted pipelines: the
    ///   fallen-8-agents host's own control plane, and the apiApp's authenticated proxy over it.
    ///   Both halves run in process, the host under <c>WebApplicationFactory</c> over its
    ///   deliberately namespaced entry point.
    ///
    ///   <para>No model is reachable from either, and none needs to be: nothing on this control
    ///   plane blocks on inference, so every route answers whether or not a model exists.</para>
    /// </summary>
    [TestClass]
    public class AgentEndpointTest
    {
        #region the host's own control plane

        [TestMethod]
        public async Task ASpawnAnswers202WithTheAgentsIdAndItsInitialState()
        {
            using var factory = new AgentHostFactory();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync("/agent", Json("{\"task\":\"count the vertices\"}"));

            Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, await Text(response));
            var body = await Read(response);

            Assert.IsFalse(String.IsNullOrWhiteSpace(body.GetProperty("id").GetString()),
                "a caller with no id cannot cancel or review what it started");
            Assert.AreEqual("assistant", body.GetProperty("role").GetString(),
                "an omitted role is the assistant");
            Assert.AreEqual("count the vertices", body.GetProperty("task").GetString());
            Assert.IsFalse(String.IsNullOrWhiteSpace(body.GetProperty("hostInstanceId").GetString()));

            // There is deliberately no model field anywhere in the answer: this host configures
            // none, so reporting one would be reporting a value that does not exist.
            Assert.IsFalse(body.TryGetProperty("model", out _));
        }

        [TestMethod]
        public async Task ASpawnWithNoTaskAndOneWithAnUnknownRoleAreBothRefusedAtTheEdge()
        {
            using var factory = new AgentHostFactory();
            using var client = factory.CreateClient();

            using (var response = await client.PostAsync("/agent", Json("{}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
                StringAssert.Contains(await Text(response), "A task is required.");
            }

            using (var response = await client.PostAsync("/agent",
                Json("{\"task\":\"do it\",\"role\":\"architect\"}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
                var detail = await Text(response);
                StringAssert.Contains(detail, "architect");
                StringAssert.Contains(detail, "assistant",
                    "an unknown role names the accepted set rather than just refusing");
            }

            // Blank rather than absent, which a required-field check that only tests for null misses.
            using (var response = await client.PostAsync("/agent", Json("{\"task\":\"   \"}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            }
        }

        [TestMethod]
        public async Task ACallerNamingAParentIsRefusedBecauseOnlyAnOrchestratorSpawnsAWorker()
        {
            using var factory = new AgentHostFactory();
            using var client = factory.CreateClient();

            // Any role: parentId is refused because of what it MEANS, not because of the role it
            // was sent with, so the assistant case is the one that pins the rule.
            using var response = await client.PostAsync("/agent",
                Json("{\"task\":\"part one\",\"parentId\":\"a1-1\"}"));

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            StringAssert.Contains(await Text(response), "parentId is set by an orchestrator");
        }

        [TestMethod]
        public async Task TheTwoSwarmRolesAreRefusedWithTheReasonRatherThanSpawnedWithoutTheirTools()
        {
            // Both roles exist, have prompts and have allowlists, and the runner serves them. What
            // is missing is the swarm, so spawning either would produce an agent commanded to use
            // tools it does not have, or one told to report to an orchestrator that does not exist.
            // Refused with that reason, which is more use than a role the catalogue denies having.
            using var factory = new AgentHostFactory();
            using var client = factory.CreateClient();

            using (var response = await client.PostAsync("/agent",
                Json("{\"task\":\"plan it\",\"role\":\"orchestrator\"}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
                var detail = await Text(response);
                StringAssert.Contains(detail, "delegates with are not available");
                StringAssert.Contains(detail, "assistant", "the refusal has to say what to do instead");
            }

            using (var response = await client.PostAsync("/agent",
                Json("{\"task\":\"part one\",\"role\":\"worker\"}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
                StringAssert.Contains(await Text(response), "spawned by an orchestrator");
            }

            // And nothing was admitted by either attempt.
            using (var listed = await client.GetAsync("/agent"))
            {
                Assert.AreEqual(0, (await Read(listed)).GetArrayLength());
            }
        }

        [TestMethod]
        public async Task ASpawnAskingForMoreTokensThanTheHostAllowsIsClampedRatherThanRefused()
        {
            // The operator's ceiling is not advice: a caller could otherwise name its own budget and
            // spend the provider's shared hourly quota. Clamped rather than refused, because the
            // request is answerable and the record it gets back says at what price.
            using var factory = new AgentHostFactory(maxTokenBudget: 5000);
            using var client = factory.CreateClient();

            using var response = await client.PostAsync("/agent",
                Json("{\"task\":\"count\",\"tokenBudget\":999999}"));

            Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, await Text(response));
            Assert.AreEqual(5000, (await Read(response)).GetProperty("tokenBudget").GetInt32());
        }

        [TestMethod]
        public async Task TheListingAndTheDetailCarryTheSameAgentAndTheDetailNamesItsChildren()
        {
            using var factory = new AgentHostFactory();
            using var client = factory.CreateClient();

            String id;
            using (var spawned = await client.PostAsync("/agent", Json("{\"task\":\"count\",\"name\":\"counter\"}")))
            {
                id = (await Read(spawned)).GetProperty("id").GetString();
            }

            using (var listed = await client.GetAsync("/agent"))
            {
                Assert.AreEqual(HttpStatusCode.OK, listed.StatusCode);
                var agents = (await Read(listed)).EnumerateArray().ToList();
                Assert.AreEqual(1, agents.Count);
                Assert.AreEqual(id, agents[0].GetProperty("id").GetString());
                Assert.AreEqual("counter", agents[0].GetProperty("name").GetString());
                Assert.IsTrue(agents[0].TryGetProperty("totalTokens", out _),
                    "the listing carries the counters, so a reviewer sees cost without a second call");
                Assert.IsTrue(agents[0].TryGetProperty("durationMs", out _));
            }

            using (var detail = await client.GetAsync("/agent/" + id))
            {
                Assert.AreEqual(HttpStatusCode.OK, detail.StatusCode);
                var body = await Read(detail);
                Assert.AreEqual(id, body.GetProperty("agent").GetProperty("id").GetString());
                Assert.AreEqual(0, body.GetProperty("children").GetArrayLength());
            }
        }

        [TestMethod]
        public async Task AnUnknownAgentIsA404WhoseMessageSaysRetentionIsBounded()
        {
            using var factory = new AgentHostFactory();
            using var client = factory.CreateClient();

            using (var response = await client.GetAsync("/agent/a1-999"))
            {
                Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
                var detail = await Text(response);
                StringAssert.Contains(detail, "a1-999");
                StringAssert.Contains(detail, "RetainFinishedMinutes",
                    "the message has to distinguish 'never existed' from 'finished and evicted'");
            }

            using (var response = await client.DeleteAsync("/agent/a1-999"))
            {
                Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
            }
        }

        [TestMethod]
        public async Task ACancelAnswers202SayingHowManyAgentsWereSignalled()
        {
            // Against a target that ACCEPTS and never answers, so the agent is reliably mid-call
            // when the cancel lands. Pointing at a closed port instead made this depend on how long
            // the host platform takes to refuse a loopback connection: about two seconds on Windows,
            // effectively instant on Linux, where the agent would already have failed and the
            // signalled count would be zero.
            using var blackhole = new Blackhole();
            using var factory = new AgentHostFactory(baseUrl: blackhole.BaseUrl);
            using var client = factory.CreateClient();

            String id;
            using (var spawned = await client.PostAsync("/agent", Json("{\"task\":\"long one\"}")))
            {
                id = (await Read(spawned)).GetProperty("id").GetString();
            }

            using var response = await client.DeleteAsync("/agent/" + id);

            // Accepted rather than OK, honestly: the token is observed between steps, so a tool call
            // already sent to the graph is not undone.
            Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, await Text(response));
            var body = await Read(response);
            Assert.AreEqual(1, body.GetProperty("signalled").GetInt32());
            Assert.AreEqual("cancelled", body.GetProperty("agent").GetProperty("state").GetString());
        }

        [TestMethod]
        public async Task TheConcurrencyCapAnswers429RatherThan503BecauseTheHostIsHealthy()
        {
            // Same reason as the cancel test: the first agent has to still hold its slot when the
            // second spawn arrives, and a target that accepts and never answers is what makes that
            // true on every platform rather than only on a slow one.
            using var blackhole = new Blackhole();
            using var factory = new AgentHostFactory(maxConcurrent: 1, baseUrl: blackhole.BaseUrl);
            using var client = factory.CreateClient();

            using (var first = await client.PostAsync("/agent", Json("{\"task\":\"one\"}")))
            {
                Assert.AreEqual(HttpStatusCode.Accepted, first.StatusCode);
            }

            using var second = await client.PostAsync("/agent", Json("{\"task\":\"two\"}"));

            Assert.AreEqual(HttpStatusCode.TooManyRequests, second.StatusCode,
                "a 503 would read as 'this sidecar is broken', which is what the proxy's own 503 means");
            StringAssert.Contains(await Text(second), "MaxConcurrentAgents");
        }

        [TestMethod]
        public async Task TheStatusRouteReportsThePostureIncludingWhyTheToolsetIsEmpty()
        {
            using var factory = new AgentHostFactory();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/agent/status");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var body = await Read(response);

            var mcp = body.GetProperty("mcp");
            Assert.IsFalse(mcp.GetProperty("connected").GetBoolean());
            Assert.AreEqual(0, mcp.GetProperty("toolCount").GetInt32());
            Assert.AreEqual(JsonValueKind.String, mcp.GetProperty("failure").ValueKind,
                "an empty toolset must be distinguishable from a server that did not answer");

            var chat = body.GetProperty("chat");
            Assert.IsFalse(String.IsNullOrWhiteSpace(chat.GetProperty("reachability").GetString()));

            // Absent before a step, because this host holds no model configuration and does not
            // invent one. Which model served a step is the instance's answer, per step.
            Assert.AreEqual(JsonValueKind.Null, chat.GetProperty("lastSeenModel").ValueKind);
            Assert.AreEqual(JsonValueKind.Null, chat.GetProperty("lastSeenBackend").ValueKind);

            var roles = body.GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("name").GetString()).ToList();
            CollectionAssert.AreEquivalent(new[] { "assistant", "orchestrator", "worker" }, roles);

            var limits = body.GetProperty("limits");
            Assert.AreEqual(24, limits.GetProperty("maxStepsPerRun").GetInt32());
            Assert.AreEqual(100000, limits.GetProperty("defaultTokenBudget").GetInt32());

            Assert.IsFalse(String.IsNullOrWhiteSpace(body.GetProperty("hostInstanceId").GetString()));
        }

        [TestMethod]
        public async Task TheHostServesAHealthProbeSoTheProxyCanTellItApart()
        {
            using var factory = new AgentHostFactory();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/health");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("ok", (await Read(response)).GetProperty("status").GetString());
        }

        [TestMethod]
        public async Task TheHostRefusesToStartWhenTheChatTargetIsUnreachableIsNotTrue()
        {
            // The stated posture, asserted rather than assumed: a target that does not answer is a
            // host that starts and SAYS SO. The alternative is a crash loop the proxy reports as a
            // runtime that did not answer, sending an operator to the wrong container.
            using var factory = new AgentHostFactory(baseUrl: "http://127.0.0.1:1/");
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/agent/status");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("unreachable",
                (await Read(response)).GetProperty("chat").GetProperty("reachability").GetString());
        }

        #endregion

        #region the apiApp's proxy over it

        [TestMethod]
        public async Task WithAgentsOffTheProxyForbidsAKeyedCallerAndChallengesAKeylessOne()
        {
            // The shared capability policy pairs RequireAuthenticatedUser with the capability
            // requirement, so an ANONYMOUS caller is challenged before the capability is even read.
            // "Agents are off" therefore arrives as 403 on a keyed instance and as 401 on a bare
            // dotnet run. Both are pinned, because a client that reads only 403 as "absent" shows a
            // broken screen on exactly the second instance.
            using (var factory = new AgentProxyFactory(enabled: "false", withApiKey: true))
            using (var client = factory.CreateAuthenticatedClient())
            {
                using var response = await client.GetAsync("/agents");
                Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode,
                    "that 403 IS the opt-out, and it is what a client gates the feature on");
            }

            using (var factory = new AgentProxyFactory(enabled: "false"))
            using (var client = factory.CreateClient())
            {
                using var response = await client.GetAsync("/agents");
                Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode,
                    "an unsecured instance challenges first, so 'off' is a 401 there");
            }

            using (var factory = new AgentProxyFactory(enabled: "true", client: Canned(200, "[]")))
            using (var client = factory.CreateClient())
            {
                using var response = await client.GetAsync("/agents");
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }
        }

        [TestMethod]
        public async Task AKeyedInstanceAnswers401WithoutTheKeyAnd200WithIt()
        {
            using var factory = new AgentProxyFactory(enabled: "true", withApiKey: true,
                client: Canned(200, "[]"));

            using (var anonymous = factory.CreateClient())
            using (var response = await anonymous.GetAsync("/agents"))
            {
                Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode,
                    "a keyed instance refuses an unauthenticated caller before the capability is read");
            }

            using (var authenticated = factory.CreateAuthenticatedClient())
            using (var response = await authenticated.GetAsync("/agents"))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }
        }

        [TestMethod]
        public async Task AnUnconfiguredHostIsTheOneStatusThisProxyInvents()
        {
            using var factory = new AgentProxyFactory(enabled: "true", endpoint: String.Empty);
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/agents");

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode,
                "an unconfigured host answers rather than timing out");
            StringAssert.Contains(await Text(response), "Fallen8:Agents:Endpoint");
        }

        [TestMethod]
        public async Task EveryStatusTheHostChoseIsPassedThroughUntouched()
        {
            // The rule this pins: a 400 naming an unknown role, a 404 explaining bounded retention
            // and a 429 naming the concurrency cap are answers a caller has to READ, so none of
            // them becomes a 502.
            foreach (var status in new[] { 400, 404, 429, 500 })
            {
                using var factory = new AgentProxyFactory(enabled: "true",
                    client: Canned(status, "{\"detail\":\"the host said so\"}", "application/problem+json"));
                using var client = factory.CreateClient();

                using var response = await client.PostAsync("/agents", Json("{\"task\":\"do it\"}"));

                Assert.AreEqual(status, (Int32)response.StatusCode, "status " + status + " was remapped");
                StringAssert.Contains(await Text(response), "the host said so",
                    "the host's own sentence is more use than a proxy-shaped one");
                Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType.MediaType);
            }
        }

        [TestMethod]
        public async Task TheProxyForwardsTheHostsOwnRoutesAndTheStatusRouteIsNotAnAgentId()
        {
            var recorder = new RecordingAgentsClient(200, "{}");
            using var factory = new AgentProxyFactory(enabled: "true", client: recorder);
            using var client = factory.CreateClient();

            await (await client.PostAsync("/agents", Json("{\"task\":\"x\"}"))).Content.ReadAsStringAsync();
            await client.GetAsync("/agents");
            await client.GetAsync("/agents/status");
            await client.GetAsync("/agents/a1-7");
            await client.DeleteAsync("/agents/a1-7");

            CollectionAssert.AreEqual(new[]
            {
                "POST agent",
                "GET agent",
                "GET agent/status",
                "GET agent/a1-7",
                "DELETE agent/a1-7",
            }, recorder.Calls.ToArray(),
                "the status route must not be forwarded as an agent whose id is 'status'");
        }

        [TestMethod]
        public async Task AnAgentIdWithAPathSeparatorInItCannotReachAnotherRoute()
        {
            var recorder = new RecordingAgentsClient(200, "{}");
            using var factory = new AgentProxyFactory(enabled: "true", client: recorder);
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/agents/a1-7%2Fstatus");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(1, recorder.Calls.Count);

            // The property that matters is that the forwarded path stays ONE segment under agent/,
            // not the exact spelling of the escape: routing hands the value on with its percent
            // sequence intact and the proxy escapes the percent again, so the host receives a
            // nonsense id and answers 404 rather than being addressed at a sibling route.
            var forwarded = recorder.Calls[0].Substring("GET agent/".Length);
            Assert.IsFalse(forwarded.Contains('/'),
                "an id must not be able to address a sibling route: " + recorder.Calls[0]);
            StringAssert.StartsWith(forwarded, "a1-7");
        }

        [TestMethod]
        public async Task TheSpawnBodyIsNeverLogged()
        {
            // A task sentence is the caller's own text and may name anything. The integrations proxy
            // pins the same rule for a job body; the reason is the same and the leak would be too.
            //
            // Deliberately runs the SHIPPED AgentsClient rather than a canned one, against an
            // endpoint that does not answer: the client's own failure path is where a body would
            // most plausibly be logged, and a test that replaces the client cannot see it. The
            // sidecar base logs at DEBUG, which is why the factory lowers the level.
            const String Secret = "must-not-be-logged-7f3a";

            using var factory = new AgentProxyFactory(enabled: "true",
                endpoint: "http://127.0.0.1:1");
            using var client = factory.CreateClient();

            using (var response = await client.PostAsync("/agents",
                Json("{\"task\":\"summarise " + Secret + "\"}")))
            {
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode,
                    "this test wants the shipped client's FAILURE path: " + await Text(response));
            }

            // The guard that makes the assertion below mean something: a sink that received nothing
            // would pass it for the wrong reason.
            Assert.IsTrue(factory.Sink.Lines.Length > 0,
                "the capturing sink was never wired, so the no-leak check proves nothing");

            var leaked = factory.Sink.Lines.Where(m => m.Contains(Secret, StringComparison.Ordinal)).ToList();
            Assert.AreEqual(0, leaked.Count,
                "the request body reached a log line: " + String.Join(" | ", leaked));
        }

        [TestMethod]
        public async Task TheShippedClientReportsAnUnreachableHostAsUnavailableAndNotAsSomethingElse()
        {
            // The shipped AgentsClient's own arms, which no test reached: an endpoint that is
            // configured but does not answer has to become the ONE status this proxy invents, and
            // the message has to say which host did not answer rather than blaming the caller.
            using var blackhole = new Blackhole();
            using var factory = new AgentProxyFactory(enabled: "true", endpoint: blackhole.BaseUrl);
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/agents");

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            StringAssert.Contains(await Text(response), "did not answer",
                "a host that accepted the connection and went quiet is still a host that did not answer");
        }

        [TestMethod]
        public async Task ACallerWhoGoesAwayIsNotReportedAsAnUnavailableHost()
        {
            // The distinction the client's catch ordering exists for. A caller that disconnects is
            // not a broken sidecar, and reporting it as one would put a false 503 in the log every
            // time somebody closed a tab.
            using var blackhole = new Blackhole();
            using var factory = new AgentProxyFactory(enabled: "true", endpoint: blackhole.BaseUrl);

            var target = factory.Services.GetRequiredService<IAgentsClient>();
            Assert.IsInstanceOfType<AgentsClient>(target, "this test wants the shipped client");

            using var caller = new CancellationTokenSource();
            var pending = target.ForwardAsync(HttpMethod.Get, "agent", null, caller.Token);
            caller.CancelAfter(TimeSpan.FromMilliseconds(50));

            try
            {
                await pending;
                Assert.Fail("the caller's cancellation was swallowed");
            }
            catch (AgentsUnavailableException)
            {
                Assert.Fail("a caller who went away was reported as an unavailable host");
            }
            catch (OperationCanceledException)
            {
            }
        }

        [TestMethod]
        public async Task TheAgentsRoutesHaveNoNamespaceTwinBecauseOneHostServesTheInstance()
        {
            var recorder = new RecordingAgentsClient(200, "[]");
            using var factory = new AgentProxyFactory(enabled: "true", client: recorder);
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/ns/other/agents");

            // Asserted on what was FORWARDED rather than on a 404, because this app serves a
            // single-page app and an unmatched GET falls through to its index with a 200. A status
            // assertion here would pass for the wrong reason the day the twin appeared.
            Assert.AreEqual(0, recorder.Calls.Count,
                "a twin would offer a second way to say the same thing and let the two disagree");
            Assert.AreNotEqual("application/json", response.Content.Headers.ContentType?.MediaType,
                "the proxy answered under a namespace prefix");
        }

        #endregion

        #region harness

        private static StringContent Json(String body)
        {
            return new StringContent(body, Encoding.UTF8, "application/json");
        }

        private static async Task<JsonElement> Read(HttpResponseMessage response)
        {
            return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        }

        private static Task<String> Text(HttpResponseMessage response)
        {
            return response.Content.ReadAsStringAsync();
        }

        private static IAgentsClient Canned(Int32 status, String body, String contentType = "application/json")
        {
            return new RecordingAgentsClient(status, body, contentType);
        }

        /// <summary>
        ///   The agent host itself, hosted. The MCP endpoint and the chat target both point at a
        ///   closed port on purpose: a host must start and REPORT that, and every control-plane
        ///   route must answer regardless, because none of them blocks on inference.
        /// </summary>
        private sealed class AgentHostFactory : WebApplicationFactory<NoSQL.GraphDB.Agents.Program>
        {
            private readonly Int32 _maxConcurrent;
            private readonly String _baseUrl;
            private readonly Int32 _maxTokenBudget;

            public AgentHostFactory(Int32 maxConcurrent = 4, String baseUrl = "http://127.0.0.1:1/",
                Int32 maxTokenBudget = 400_000)
            {
                _maxConcurrent = maxConcurrent;
                _baseUrl = baseUrl;
                _maxTokenBudget = maxTokenBudget;
            }

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseSetting("Agents:Mcp:Endpoint", "http://127.0.0.1:1");
                builder.UseSetting("Agents:Mcp:ConnectTimeoutSeconds", "1");
                builder.UseSetting("Agents:Limits:MaxConcurrentAgents",
                    _maxConcurrent.ToString(System.Globalization.CultureInfo.InvariantCulture));
                builder.UseSetting("Agents:Limits:MaxTokenBudget",
                    _maxTokenBudget.ToString(System.Globalization.CultureInfo.InvariantCulture));
                builder.UseSetting("Fallen8Target:BaseUrl", _baseUrl);
            }
        }

        /// <summary>An apiApp with the proxy client replaced, so the proxy's own behaviour is what is
        /// under test rather than a sidecar's.</summary>
        private sealed class AgentProxyFactory : VolatileAppFactory
        {
            internal const String ApiKey = "agents-proxy-test-key";

            private readonly String _enabled;
            private readonly String _endpoint;
            private readonly Boolean _withApiKey;
            private readonly IAgentsClient _client;

            public AgentProxyFactory(String enabled = null, String endpoint = null,
                Boolean withApiKey = false, IAgentsClient client = null)
            {
                _enabled = enabled;
                _endpoint = endpoint;
                _withApiKey = withApiKey;
                _client = client;
            }

            /// <summary>Everything this apiApp logged, for the one test that asserts what it did NOT
            /// log.</summary>
            internal CapturingLoggerProvider Sink { get; } = new CapturingLoggerProvider();

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                base.ConfigureWebHost(builder);

                // Trace, because the sidecar client base logs its failures at DEBUG: at the app's
                // configured Information level a body logged there would never reach this sink and
                // the no-leak check would pass over exactly the line it exists to catch.
                builder.ConfigureLogging(logging =>
                {
                    logging.SetMinimumLevel(LogLevel.Trace);
                    logging.AddProvider(Sink);
                });
                builder.UseEnvironment("Development");

                if (_enabled != null)
                {
                    builder.UseSetting("Fallen8:Agents:Enabled", _enabled);
                }

                if (_endpoint != null)
                {
                    builder.UseSetting("Fallen8:Agents:Endpoint", _endpoint);
                }

                if (_withApiKey)
                {
                    builder.UseSetting("Fallen8:Security:ApiKey", ApiKey);
                }

                builder.UseSetting("Fallen8:Agents:TimeoutSeconds", "1");

                if (_client != null)
                {
                    builder.ConfigureTestServices(services => services.AddSingleton(_client));
                }
            }

            internal HttpClient CreateAuthenticatedClient()
            {
                var client = CreateClient();
                if (_withApiKey)
                {
                    client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
                }

                return client;
            }
        }

        /// <summary>A canned host that also records the method and path it was handed, which is how
        /// the route mapping is asserted without a second process.</summary>
        /// <summary>
        ///   A socket that accepts a connection and then says nothing, ever. It exists because
        ///   "an agent is still working" and "a sidecar has not answered" are states several tests
        ///   need to be IN, and getting there by pointing at a closed port makes the test depend on
        ///   how fast the platform refuses a connection. It holds its accepted sockets so the peer
        ///   sees an open connection rather than a reset.
        /// </summary>
        private sealed class Blackhole : IDisposable
        {
            private readonly System.Net.Sockets.TcpListener _listener;
            private readonly List<System.Net.Sockets.TcpClient> _accepted =
                new List<System.Net.Sockets.TcpClient>();

            private readonly CancellationTokenSource _stopping = new CancellationTokenSource();

            public Blackhole()
            {
                _listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                _listener.Start();
                BaseUrl = "http://127.0.0.1:"
                    + ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port
                        .ToString(System.Globalization.CultureInfo.InvariantCulture) + "/";

                _ = Task.Run(async () =>
                {
                    while (!_stopping.IsCancellationRequested)
                    {
                        try
                        {
                            var socket = await _listener.AcceptTcpClientAsync(_stopping.Token);
                            lock (_accepted)
                            {
                                _accepted.Add(socket);
                            }
                        }
                        catch (Exception)
                        {
                            return;
                        }
                    }
                });
            }

            public String BaseUrl
            {
                get;
            }

            public void Dispose()
            {
                _stopping.Cancel();
                _listener.Stop();

                lock (_accepted)
                {
                    foreach (var socket in _accepted)
                    {
                        socket.Dispose();
                    }

                    _accepted.Clear();
                }

                _stopping.Dispose();
            }
        }

        private sealed class RecordingAgentsClient : IAgentsClient
        {
            private readonly Int32 _status;
            private readonly String _body;
            private readonly String _contentType;

            public RecordingAgentsClient(Int32 status, String body, String contentType = "application/json")
            {
                _status = status;
                _body = body;
                _contentType = contentType;
            }

            public List<String> Calls { get; } = new List<String>();

            public Boolean Configured => true;

            public Task<SidecarResponse> ForwardAsync(HttpMethod method, String path, String jsonBody,
                CancellationToken cancellationToken)
            {
                Calls.Add(method.Method + " " + path);
                return Task.FromResult(new SidecarResponse(_status, _body, _contentType));
            }

            public Task<Boolean> IsReachableAsync(CancellationToken cancellationToken)
                => Task.FromResult(true);
        }

        #endregion
    }
}
