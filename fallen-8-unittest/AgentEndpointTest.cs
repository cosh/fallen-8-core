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
using NoSQL.GraphDB.Agents.Runtime;
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

            // WHEN the probe ran, beside what it saw. Nothing refreshes that word, so a host that
            // started before the instance answered reports unreachable for the life of the
            // container while every agent runs fine: without the timestamp a route documented as
            // the first thing to read when an agent fails was permanently wrong in the case its
            // own doc calls ordinary, with no way for a reader to tell.
            Assert.AreEqual(JsonValueKind.String, chat.GetProperty("probedAt").ValueKind,
                "the reachability word has to carry the moment it was measured");

            // ABSENT before a step, not null: this host holds no model configuration and does not
            // invent one, and the host's serializer omits what has no value rather than writing a
            // null a reader has to interpret. Which model served a step is the instance's answer,
            // per step.
            //
            // ONE object, which is what spec 3.2 and 3.3 both specify. It shipped as two sibling
            // scalars, lastSeenBackend and lastSeenModel, so a consumer written from either read
            // chat.lastSeen.backend and got nothing on a host that had served many steps. Nothing
            // compared the spec's shape to the emitted one, which is why it survived two reviews.
            Assert.IsFalse(chat.TryGetProperty("lastSeen", out _),
                "nothing has served a step, so the object is absent rather than one of nulls");
            Assert.IsFalse(chat.TryGetProperty("lastSeenModel", out _),
                "the flat shape is gone: it was never the documented one");
            Assert.IsFalse(chat.TryGetProperty("lastSeenBackend", out _));

            var roles = body.GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("name").GetString()).ToList();
            CollectionAssert.AreEquivalent(new[] { "assistant", "orchestrator", "worker" }, roles);

            var limits = body.GetProperty("limits");
            Assert.AreEqual(24, limits.GetProperty("maxStepsPerRun").GetInt32());
            Assert.AreEqual(100000, limits.GetProperty("defaultTokenBudget").GetInt32());

            // The CEILING, which this route documents itself as reporting and did not: it is the
            // one cap that silently rewrites a caller's own tokenBudget, because it clamps rather
            // than refuses, so a client pre-validating a spawn form had no way to learn it and the
            // user saw a budget the run never got.
            Assert.AreEqual(400000, limits.GetProperty("maxTokenBudget").GetInt32(),
                "the cap that clamps a caller's request was the one cap the posture route hid");

            Assert.IsFalse(String.IsNullOrWhiteSpace(body.GetProperty("hostInstanceId").GetString()));
        }

        [TestMethod]
        public void TheChatPostureReportsLastSeenAsOneObjectWithTheShapeTheSpecNames()
        {
            // Serialized directly, because the HTTP test above can only reach the ABSENT case: no
            // model is reachable from these tests, so nothing ever serves a step and the field is
            // omitted whatever it is called. Renaming it back to the two sibling scalars it shipped
            // as therefore leaves that test green, which is exactly how the wrong shape survived
            // two reviews. Here the value is present, so the shape is the assertion.
            var payload = JsonSerializer.Serialize(new NoSQL.GraphDB.Agents.Hosting.ChatStatus
            {
                BaseUrl = "http://127.0.0.1:1/",
                Reachability = "reachable",
                TimeoutSeconds = 120,
                ProbedAt = DateTimeOffset.Parse("2026-09-15T09:00:00Z"),
                LastSeen = new NoSQL.GraphDB.Agents.Hosting.LastSeenStatus
                {
                    Backend = "Nahil",
                    Model = "an-agent-model",
                },
            }, NoSQL.GraphDB.Agents.Hosting.AgentsHost.Json);

            var chat = JsonSerializer.Deserialize<JsonElement>(payload);
            var lastSeen = chat.GetProperty("lastSeen");

            Assert.AreEqual(JsonValueKind.Object, lastSeen.ValueKind,
                "spec 3.2 and 3.3 both name lastSeen { backend, model }, and this shipped as two "
                + "sibling scalars, so a consumer read chat.lastSeen.backend and got nothing: "
                + payload);
            Assert.AreEqual("Nahil", lastSeen.GetProperty("backend").GetString());
            Assert.AreEqual("an-agent-model", lastSeen.GetProperty("model").GetString());

            Assert.IsFalse(chat.TryGetProperty("lastSeenBackend", out _),
                "the flat shape must not come back alongside the nested one");
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

        [TestMethod]
        public async Task TheTraceRouteCarriesTheStepsARunProducedAndSaysWhetherItIsWhole()
        {
            using var factory = new AgentHostFactory();
            using var client = factory.CreateClient();

            String id;
            using (var spawned = await client.PostAsync("/agent", Json("{\"task\":\"count\"}")))
            {
                id = (await Read(spawned)).GetProperty("id").GetString();
            }

            using var response = await client.GetAsync("/agent/" + id + "/trace");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await Text(response));
            var body = await Read(response);

            Assert.AreEqual(id, body.GetProperty("agentId").GetString());
            Assert.IsFalse(String.IsNullOrWhiteSpace(body.GetProperty("hostInstanceId").GetString()),
                "nothing here survives a restart, so a trace has to name the run that produced it");
            Assert.AreEqual(0, body.GetProperty("dropped").GetInt64(),
                "a short run reported dropped steps, so a reader cannot trust the number");
            Assert.IsTrue(body.GetProperty("recorded").GetInt64() > 0);

            // The spawn is the first step of every trace, so it is there whatever the model did.
            var steps = body.GetProperty("steps").EnumerateArray().ToList();
            Assert.IsTrue(steps.Any(s => s.GetProperty("kind").GetString() == "spawn"),
                "the spawn is not in the trace: " + await Text(response));
            Assert.AreEqual(1, steps[0].GetProperty("seq").GetInt64());
        }

        [TestMethod]
        public async Task AnUnknownAgentsTraceIsThe404WithTheSameMessageTheDetailRouteGives()
        {
            using var factory = new AgentHostFactory();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/agent/a1-999/trace");

            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
            StringAssert.Contains(await Text(response), "RetainFinishedMinutes");
        }

        [TestMethod]
        public async Task TheDetailRouteCarriesATailOfTheTraceAndHowMuchThereIs()
        {
            using var factory = new AgentHostFactory();
            using var client = factory.CreateClient();

            String id;
            using (var spawned = await client.PostAsync("/agent", Json("{\"task\":\"count\"}")))
            {
                id = (await Read(spawned)).GetProperty("id").GetString();
            }

            using var response = await client.GetAsync("/agent/" + id);
            var body = await Read(response);

            Assert.IsTrue(body.GetProperty("trace").GetArrayLength() > 0);
            Assert.IsTrue(body.GetProperty("traceRecorded").GetInt64() > 0,
                "without a total a reader cannot tell a tail from a whole run");
        }

        [TestMethod]
        public async Task TheDetailRoutesTraceIsATailAndItsTotalSaysHowMuchIsMissing()
        {
            // The test above is named for a tail and a total and asserted only that both are
            // non-empty: hand back the WHOLE trace instead of the tail and it stays green, which
            // unbounds a response body that the constant exists to bound. What is pinned here is
            // the distinction itself, on a trace short enough to truncate: rows fewer than
            // recorded, the newest kept, and the total reported beside them.
            //
            // The constant 20 is NOT pinned here, deliberately: a run in these tests produces about
            // four steps, because no model is reachable, so no HTTP test can reach the cap.
            // AgentTraceTest.TheTailIsTheNewestStepsAndNeverMoreThanExist covers Tail(n) itself.
            using var factory = new AgentHostFactory(maxTraceSteps: 2);
            using var client = factory.CreateClient();

            String id;
            using (var spawned = await client.PostAsync("/agent", Json("{\"task\":\"count\"}")))
            {
                id = (await Read(spawned)).GetProperty("id").GetString();
            }

            // The run ends on its own, because its chat target is a closed port, which is what
            // takes the trace past a bound of two rows.
            Assert.IsTrue(await Settles(client, id), "the run never reached an ending");

            using var response = await client.GetAsync("/agent/" + id);
            var body = await Read(response);

            var rows = body.GetProperty("trace").EnumerateArray().ToList();
            var recorded = body.GetProperty("traceRecorded").GetInt64();

            Assert.AreEqual(2, rows.Count, "the trace bound applies to what the detail route hands back");
            Assert.IsTrue(recorded > rows.Count,
                "recorded (" + recorded + ") has to exceed the rows shown, or there is nothing for "
                + "a reader to notice is missing");
            Assert.AreEqual("dropped", rows[0].GetProperty("kind").GetString(),
                "the marker has to survive serialization, or the response reads as a complete run");
            Assert.IsTrue(rows[0].GetProperty("droppedSteps").GetInt64() > 0);

            // The NEWEST row, which is what makes it a tail rather than the front of the buffer.
            Assert.AreEqual(recorded, rows[^1].GetProperty("seq").GetInt64(),
                "the last row is not the newest step, so this is not a tail");
        }

        [TestMethod]
        public async Task TheTraceRouteSaysSoWhenTheTraceIsNOTWholeAndTheMarkerSurvivesTheWire()
        {
            // The route above is named for saying whether the trace is whole and only ever asserted
            // the whole case, because nothing set the trace bound: dropped > 0 was produced by no
            // HTTP test, so the drop marker's serialization over this route was covered nowhere.
            // A trace that quietly lost its middle is the one failure the bound's design exists to
            // prevent, and it is the reader of this route who would be misled.
            using var factory = new AgentHostFactory(maxTraceSteps: 2);
            using var client = factory.CreateClient();

            String id;
            using (var spawned = await client.PostAsync("/agent", Json("{\"task\":\"count\"}")))
            {
                id = (await Read(spawned)).GetProperty("id").GetString();
            }

            Assert.IsTrue(await Settles(client, id), "the run never reached an ending");

            using var response = await client.GetAsync("/agent/" + id + "/trace");
            var body = await Read(response);

            var dropped = body.GetProperty("dropped").GetInt64();
            var recorded = body.GetProperty("recorded").GetInt64();
            var steps = body.GetProperty("steps").EnumerateArray().ToList();

            Assert.IsTrue(dropped > 0, "the trace was truncated and the route reported no loss");
            Assert.AreEqual(recorded - dropped, steps.Count(s => s.GetProperty("kind").GetString() != "dropped"),
                "recorded minus dropped has to equal the real steps handed back, or the numbers "
                + "beside the rows describe a different trace");

            var marker = steps.Single(s => s.GetProperty("kind").GetString() == "dropped");
            Assert.AreEqual(dropped, marker.GetProperty("droppedSteps").GetInt64(),
                "the marker carries the running total, which is the whole answer to how much went");
            Assert.AreEqual(steps[0].GetProperty("seq").GetInt64() + 1,
                steps[1].GetProperty("seq").GetInt64(),
                "the marker and the step after it read as consecutive on the wire too");
        }

        /// <summary>
        ///   Waits until an agent has reached an ending, bounded. Its chat target is a closed port
        ///   in these tests, so every run ends on its own; the wait is for the run to get there
        ///   rather than for anything to be retried.
        /// </summary>
        private static async Task<Boolean> Settles(HttpClient client, String id)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!budget.IsCancellationRequested)
            {
                using var response = await client.GetAsync("/agent/" + id, budget.Token);
                var state = (await Read(response)).GetProperty("agent").GetProperty("state").GetString();
                if (state is "completed" or "failed" or "cancelled" or "budgetExceeded")
                {
                    return true;
                }

                await Task.Delay(25, budget.Token);
            }

            return false;
        }

        [TestMethod]
        public async Task TheFeedStreamsFramesInTheSameShapeAsTheChangeFeed()
        {
            // The frame contract, asserted on the bytes: a client that can read this instance's
            // change feed has to be able to read this one, which is the whole reason it copies the
            // dialect rather than inventing one.
            using var factory = new AgentHostFactory();
            using var client = factory.CreateClient();
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            // Read first, because the id's prefix is asserted to BE this host's instance id and the
            // status route is what a client reads it from.
            String hostInstanceId;
            using (var status = await client.GetAsync("/agent/status"))
            {
                hostInstanceId = (await Read(status)).GetProperty("hostInstanceId").GetString();
            }

            using var stream = await client.GetAsync("/agent/feed",
                HttpCompletionOption.ResponseHeadersRead);

            Assert.AreEqual(HttpStatusCode.OK, stream.StatusCode);
            Assert.AreEqual("text/event-stream", stream.Content.Headers.ContentType.MediaType);

            using var reader = new System.IO.StreamReader(await stream.Content.ReadAsStreamAsync());

            // Spawned AFTER subscribing, because there is no catch-up: that is the contract, and a
            // test that spawned first would be asserting a replay this host does not do.
            using (var spawned = await client.PostAsync("/agent", Json("{\"task\":\"count\"}")))
            {
                Assert.AreEqual(HttpStatusCode.Accepted, spawned.StatusCode);
            }

            var frame = await ReadFrame(reader, budget.Token);

            Assert.AreEqual("agentSpawned", frame.Event);

            // id: <hostInstanceId>:<seq>, split on the LAST colon as the change feed's own test
            // splits its epoch id. Asserting that the frame merely CONTAINS "id: " asserted
            // nothing: measured, stripping the host instance and its separator from the writer
            // left this test green, so the half of the id that makes it useful was unpinned. A
            // reconnecting client compares that prefix to decide whether its own last id still
            // means anything here, and a bare sequence would read a restart as a gap.
            var separator = frame.Id.LastIndexOf(':');
            Assert.IsTrue(separator > 0, "the id carries <hostInstanceId>:<seq>, and was: " + frame.Id);
            Assert.AreEqual(hostInstanceId, frame.Id.Substring(0, separator),
                "the id's prefix is not this host's instance id, so a reconnecting client cannot "
                + "tell a restart from a gap");
            Assert.IsTrue(Int64.TryParse(frame.Id.Substring(separator + 1), out var streamedSeq)
                && streamedSeq == 1L,
                "the id's suffix is the event sequence, and was: " + frame.Id);

            var data = JsonSerializer.Deserialize<JsonElement>(frame.Data);
            Assert.AreEqual("agentSpawned", data.GetProperty("kind").GetString());
            Assert.IsFalse(String.IsNullOrWhiteSpace(data.GetProperty("agentId").GetString()));
            Assert.AreEqual(1L, data.GetProperty("seq").GetInt64());
            Assert.AreEqual(streamedSeq, data.GetProperty("seq").GetInt64(),
                "the id's sequence and the payload's have to be the same number");

            // The counters are on EVERY event, which is what lets a subscriber render live cost
            // without polling. Their absence would make the feed a notification and not a monitor.
            var tokens = data.GetProperty("tokens");
            Assert.AreEqual(0L, tokens.GetProperty("total").GetInt64());
            Assert.IsTrue(tokens.TryGetProperty("steps", out _));
            Assert.IsTrue(tokens.TryGetProperty("toolCalls", out _));
        }

        [TestMethod]
        public async Task TheFeedRefusesAnUnknownKindRatherThanStreamingSilence()
        {
            using var factory = new AgentHostFactory();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/agent/feed?kinds=agentExploded");

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var detail = await Text(response);
            StringAssert.Contains(detail, "agentExploded");
            StringAssert.Contains(detail, "agentSpawned",
                "a refusal has to name the accepted set, or a caller is left guessing");
        }

        [TestMethod]
        public async Task AKindFilterDeliversOnlyThatKind()
        {
            using var factory = new AgentHostFactory();
            using var client = factory.CreateClient();

            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            using var stream = await client.GetAsync("/agent/feed?kinds=agentFailed",
                HttpCompletionOption.ResponseHeadersRead);
            using var reader = new System.IO.StreamReader(await stream.Content.ReadAsStreamAsync());

            String id;
            using (var spawned = await client.PostAsync("/agent", Json("{\"task\":\"count\"}")))
            {
                id = (await Read(spawned)).GetProperty("id").GetString();
            }

            // The spawn and the state change are filtered out; the agent then fails, because its
            // chat target is a closed port, and THAT is the event this subscriber asked for.
            var frame = await ReadFrame(reader, budget.Token);

            Assert.AreEqual("agentFailed", frame.Event);
            StringAssert.Contains(frame.Data, id);
        }

        [TestMethod]
        public async Task AnAgentFilterOnTheRouteDeliversOnlyThatAgentsEvents()
        {
            // The agents= parameter had no coverage that ran the ROUTE. kinds= is covered end to
            // end; for agents= the only two touches were the proxy forwarding the literal string
            // and AgentFeedFilter.Admits called directly, so nothing connected the query parameter
            // to the filter applied at publish. Bind it to the wrong key and both of those stay
            // green while a Studio subscriber asking for one agent receives the whole host's
            // traffic, which is also how that subscriber then gets dropped for lagging.
            using var factory = new AgentHostFactory();
            using var client = factory.CreateClient();
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            // One agent spawned FIRST, so its id is the one to filter on, and its own events are
            // already past: there is no catch-up, which is what makes the next spawn the test.
            String mine;
            using (var spawned = await client.PostAsync("/agent", Json("{\"task\":\"mine\"}")))
            {
                mine = (await Read(spawned)).GetProperty("id").GetString();
            }

            using var stream = await client.GetAsync("/agent/feed?agents=" + mine,
                HttpCompletionOption.ResponseHeadersRead);
            Assert.AreEqual(HttpStatusCode.OK, stream.StatusCode);
            using var reader = new System.IO.StreamReader(await stream.Content.ReadAsStreamAsync());

            // A DIFFERENT agent is spawned and runs to failure (its chat target is a closed port),
            // so the host publishes a spawn, a state change and an ending for it. None may arrive.
            String other;
            using (var spawned = await client.PostAsync("/agent", Json("{\"task\":\"not mine\"}")))
            {
                other = (await Read(spawned)).GetProperty("id").GetString();
            }

            Assert.AreNotEqual(mine, other);

            // The filtered agent then gets an event of its own, by being cancelled. The FIRST frame
            // to arrive has to be that one: anything earlier is traffic the filter should have kept
            // out, and this read is bounded, so a filter that admits nothing fails rather than
            // hanging.
            using (var cancelled = await client.DeleteAsync("/agent/" + mine))
            {
                Assert.AreEqual(HttpStatusCode.Accepted, cancelled.StatusCode, await Text(cancelled));
            }

            var frame = await ReadFrame(reader, budget.Token);
            var data = JsonSerializer.Deserialize<JsonElement>(frame.Data);

            Assert.AreEqual(mine, data.GetProperty("agentId").GetString(),
                "the first frame was " + frame.Event + " for another agent, so agents= is not "
                + "reaching the filter that is applied at publish");
        }

        [TestMethod]
        public async Task AStreamEndsWhenItsSubscriptionDoesRatherThanSpinning()
        {
            // The writer's reaction to a subscription that has ENDED, over HTTP, which no test
            // reached: the unit tests assert the subscription returns null and stop there, so the
            // writer's own behaviour on that null ran nowhere. Turn its break into a continue and
            // the suite stays green while, on exactly the shutdown path the design exists to
            // handle, ReadAsync returns null on every iteration and the writer spins.
            //
            // The dispatcher is DISPOSED to produce the null, rather than overrunning a queue.
            // Overrunning it looked simpler and was wrong: the writer drains the channel into the
            // response as fast as it is filled, so whether the queue ever overflows is a race, and
            // a first version of this test passed on timing and then failed under an unrelated
            // mutation. Disposal is the same null by a deterministic route.
            using var factory = new AgentHostFactory();
            using var client = factory.CreateClient();

            using var stream = await client.GetAsync("/agent/feed",
                HttpCompletionOption.ResponseHeadersRead);
            Assert.AreEqual(HttpStatusCode.OK, stream.StatusCode);
            using var reader = new System.IO.StreamReader(await stream.Content.ReadAsStreamAsync());

            // One event first, so the stream is established and the writer is inside its loop
            // rather than still starting up.
            using (var spawned = await client.PostAsync("/agent", Json("{\"task\":\"count\"}")))
            {
                Assert.AreEqual(HttpStatusCode.Accepted, spawned.StatusCode);
            }

            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var first = await ReadFrame(reader, budget.Token);
            Assert.AreEqual("agentSpawned", first.Event);

            // Now the host stops feeding. Every open subscription ends, which is the null the
            // writer has to break on.
            factory.Services.GetRequiredService<AgentFeedDispatcher>().Dispose();

            var lines = 0;
            try
            {
                while (await reader.ReadLineAsync(budget.Token) != null)
                {
                    lines++;
                    Assert.IsTrue(lines < 10_000,
                        "the writer is still producing after its subscription ended");
                }
            }
            catch (OperationCanceledException)
            {
                Assert.Fail("the response never ended after the subscription did, having sent "
                    + lines + " further line(s): the writer did not stop when its feed stopped");
            }
        }

        [TestMethod]
        public async Task AnIdleFeedSendsKeepAlivesRatherThanGoingSilent()
        {
            // An idle feed that sent nothing is indistinguishable from a dead connection, and a
            // proxy in between would close it. So idle is never silent.
            using var factory = new AgentHostFactory(keepAliveSeconds: 1);
            using var client = factory.CreateClient();

            using var stream = await client.GetAsync("/agent/feed",
                HttpCompletionOption.ResponseHeadersRead);
            using var reader = new System.IO.StreamReader(await stream.Content.ReadAsStreamAsync());

            // Bounded, and this is the read where it matters most: this test is the ONLY gate on
            // the keep-alive write, and it had no bound at all. Removing that write left the read
            // waiting on a stream that by design sends nothing, which wedged the whole suite for
            // more than 450 seconds instead of failing this test. Generous against a 1 second
            // keep-alive so a loaded machine cannot flake it, and still an outcome rather than a
            // hang.
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            String line;
            try
            {
                line = await reader.ReadLineAsync(budget.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail("an idle feed sent nothing for 30 seconds against a keep-alive of 1, "
                    + "so a proxy in between would have closed the connection");
                throw;
            }

            StringAssert.StartsWith(line, ":", "an idle feed sent something that was not a comment: " + line);
        }

        [TestMethod]
        public async Task TheFeedRefusesASubscriberPastItsBoundWithTheLimitNamed()
        {
            using var factory = new AgentHostFactory(maxSubscribers: 1);
            using var client = factory.CreateClient();

            using var first = await client.GetAsync("/agent/feed",
                HttpCompletionOption.ResponseHeadersRead);
            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);

            using var second = await client.GetAsync("/agent/feed");

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, second.StatusCode);
            StringAssert.Contains(await Text(second), "MaxSubscribers");
        }

        [TestMethod]
        public async Task TheStatusRouteReportsTheFeedsOwnPosture()
        {
            using var factory = new AgentHostFactory();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/agent/status");
            var feed = (await Read(response)).GetProperty("feed");

            Assert.AreEqual(0, feed.GetProperty("subscribers").GetInt32());
            Assert.AreEqual(15, feed.GetProperty("keepAliveSeconds").GetInt32());
            Assert.IsTrue(feed.TryGetProperty("published", out _));
        }

        /// <summary>
        ///   Reads one SSE frame, skipping keep-alive comments, and returns its PARTS so a caller
        ///   asserts on the id and the event name rather than on a substring of the whole frame.
        ///
        ///   <para>
        ///     <b>Bounded by the caller's token, which is the only thing that bounds it.</b> This
        ///     compared <c>DateTimeOffset.UtcNow</c> to a deadline around a tokenless
        ///     <c>ReadLineAsync</c>, and such a loop cannot reach its own check, because the read
        ///     does not return. Nor does <c>HttpClient.Timeout</c> cover it: measured with the
        ///     timeout at five seconds and <c>ResponseHeadersRead</c>, a content read on a stream
        ///     sending nothing had not returned after thirty, under TestHost and under a real
        ///     Kestrel alike, because that timeout covers the headers phase only. So the bound was
        ///     decorative and a missing event WEDGED the suite rather than failing it, which this
        ///     repository's flake rule calls the one outcome worse than no test: a hung run cannot
        ///     even say which test hung.
        ///   </para>
        /// </summary>
        private static async Task<(String Id, String Event, String Data)> ReadFrame(
            System.IO.StreamReader reader, CancellationToken cancellation)
        {
            String id = null, name = null;

            while (true)
            {
                String line;
                try
                {
                    line = await reader.ReadLineAsync(cancellation);
                }
                catch (OperationCanceledException)
                {
                    Assert.Fail("no SSE frame arrived within the test's budget; saw id="
                        + (id ?? "<none>") + " event=" + (name ?? "<none>"));
                    throw;
                }

                if (line == null)
                {
                    Assert.Fail("the SSE stream ended before a frame arrived; saw id="
                        + (id ?? "<none>") + " event=" + (name ?? "<none>"));
                }

                if (line.StartsWith("id: ", StringComparison.Ordinal))
                {
                    id = line.Substring(4);
                }
                else if (line.StartsWith("event: ", StringComparison.Ordinal))
                {
                    name = line.Substring(7);
                }
                else if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    return (id, name, line.Substring(6));
                }

                // Keep-alive comments (": keepalive") and the blank separator fall through.
            }
        }

        #endregion

        #region the apiApp's proxy over it

        [TestMethod]
        public async Task WithAgentsOffTheProxyForbidsAKeyedCallerAndChallengesAKeylessOne()
        {
            // "Agents are off" arrives as 403 on a keyed instance and as 401 on a bare dotnet run.
            // Both are pinned, because a client that reads only 403 as "absent" shows a broken
            // screen on exactly the second instance. WHY the keyless one is a challenge rather than
            // a forbid is on AgentsController, which is its one home; this comment used to give a
            // different and wrong reason for it.
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
        public async Task TheProxyForwardsTheFeedAsAStreamRatherThanBufferingIt()
        {
            // The one new arm on the shared proxy client base, and the property that matters is not
            // that the bytes arrive but that they arrive AS THEY COME. A buffered forward would pass
            // every assertion about content and still make a live feed useless.
            var recorder = new RecordingAgentsClient(200, String.Empty, "text/event-stream");
            recorder.StreamChunks.Add("id: 1\nevent: agentSpawned\ndata: {\"seq\":1}\n\n");
            recorder.StreamChunks.Add("id: 2\nevent: toolCalled\ndata: {\"seq\":2}\n\n");

            using var factory = new AgentProxyFactory(enabled: "true", client: recorder);
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/agents/feed",
                HttpCompletionOption.ResponseHeadersRead);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("text/event-stream", response.Content.Headers.ContentType.MediaType,
                "the host's own content type has to survive the hop, or a browser will not treat it "
                + "as a stream");

            var body = await response.Content.ReadAsStringAsync();
            StringAssert.Contains(body, "event: agentSpawned");
            StringAssert.Contains(body, "event: toolCalled");

            Assert.AreEqual(1, recorder.Calls.Count);
            Assert.AreEqual("STREAM agent/feed", recorder.Calls[0],
                "the feed must be forwarded through the streaming arm, not the buffered one");
        }

        [TestMethod]
        public async Task TheProxyForwardsTheFeedFiltersItDeclaredAndNothingElse()
        {
            // Rebuilt from this action's own bound parameters rather than forwarded verbatim, so a
            // caller cannot append a query the proxy never declared.
            var recorder = new RecordingAgentsClient(200, String.Empty, "text/event-stream");
            using var factory = new AgentProxyFactory(enabled: "true", client: recorder);
            using var client = factory.CreateClient();

            using (await client.GetAsync("/agents/feed?kinds=toolCalled&agents=a1-1&smuggled=x",
                HttpCompletionOption.ResponseHeadersRead))
            {
            }

            Assert.AreEqual(1, recorder.Calls.Count);
            var forwarded = recorder.Calls[0];
            StringAssert.Contains(forwarded, "kinds=toolCalled");
            StringAssert.Contains(forwarded, "agents=a1-1");
            Assert.IsFalse(forwarded.Contains("smuggled", StringComparison.Ordinal),
                "the proxy forwarded a query parameter it does not declare: " + forwarded);
        }

        [TestMethod]
        public async Task AFeedRefusalReachesTheCallerAsARefusalRatherThanAnEmptyStream()
        {
            // The reason onHeaders runs before the body flows: a 400 naming a bad filter must not
            // arrive as a 200 with an error somewhere in the stream, which is what a forward that
            // assumed success would produce.
            var recorder = new RecordingAgentsClient(400,
                "{\"detail\":\"Unknown feed event kind 'agentExploded'.\"}", "application/problem+json");

            using var factory = new AgentProxyFactory(enabled: "true", client: recorder);
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/agents/feed?kinds=agentExploded");

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType.MediaType);
            StringAssert.Contains(await Text(response), "agentExploded");
        }

        [TestMethod]
        public async Task AnUnreachableHostIsA503OnTheFeedTooRatherThanAnEmptyStream()
        {
            using var factory = new AgentProxyFactory(enabled: "true", endpoint: String.Empty);
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/agents/feed");

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            StringAssert.Contains(await Text(response), "Fallen8:Agents:Endpoint");
        }

        [TestMethod]
        public async Task TheTraceIsForwardedThroughTheBufferedArmBecauseItIsNotAStream()
        {
            var recorder = new RecordingAgentsClient(200, "{\"agentId\":\"a1-1\",\"steps\":[]}");
            using var factory = new AgentProxyFactory(enabled: "true", client: recorder);
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/agents/a1-1/trace");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("GET agent/a1-1/trace", recorder.Calls[0]);
        }

        [TestMethod]
        public async Task TheFeedAndTraceRoutesAreGatedLikeEveryOtherAgentsRoute()
        {
            // A stream that ignored the capability gate would be the one way into a host an operator
            // switched off, so both new routes are checked rather than assumed.
            using (var factory = new AgentProxyFactory(enabled: "false", withApiKey: true))
            using (var client = factory.CreateAuthenticatedClient())
            {
                foreach (var route in new[] { "/agents/feed", "/agents/a1-1/trace" })
                {
                    using var response = await client.GetAsync(route);
                    Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, route);
                }
            }

            using (var factory = new AgentProxyFactory(enabled: "false"))
            using (var client = factory.CreateClient())
            {
                foreach (var route in new[] { "/agents/feed", "/agents/a1-1/trace" })
                {
                    using var response = await client.GetAsync(route);
                    Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, route);
                }
            }
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
            private readonly Int32 _keepAliveSeconds;
            private readonly Int32 _maxSubscribers;
            private readonly Int32 _maxQueuedEvents;
            private readonly Int32 _maxTraceSteps;

            // maxQueuedEvents and maxTraceSteps are settable because the two bounds they control
            // had no HTTP coverage at all: nothing overran a subscriber's queue while a real stream
            // was open, and nothing produced a truncated trace over the trace route, so the drop
            // marker's serialization and the writer's reaction to a dropped subscriber were both
            // reachable only through the unit-level classes.
            public AgentHostFactory(Int32 maxConcurrent = 4, String baseUrl = "http://127.0.0.1:1/",
                Int32 maxTokenBudget = 400_000, Int32 keepAliveSeconds = 15,
                Int32 maxSubscribers = 16, Int32 maxQueuedEvents = 512, Int32 maxTraceSteps = 1000)
            {
                _maxConcurrent = maxConcurrent;
                _baseUrl = baseUrl;
                _maxTokenBudget = maxTokenBudget;
                _keepAliveSeconds = keepAliveSeconds;
                _maxSubscribers = maxSubscribers;
                _maxQueuedEvents = maxQueuedEvents;
                _maxTraceSteps = maxTraceSteps;
            }

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseSetting("Agents:Mcp:Endpoint", "http://127.0.0.1:1");
                builder.UseSetting("Agents:Mcp:ConnectTimeoutSeconds", "1");
                builder.UseSetting("Agents:Limits:MaxConcurrentAgents",
                    _maxConcurrent.ToString(System.Globalization.CultureInfo.InvariantCulture));
                builder.UseSetting("Agents:Limits:MaxTokenBudget",
                    _maxTokenBudget.ToString(System.Globalization.CultureInfo.InvariantCulture));
                builder.UseSetting("Agents:Feed:KeepAliveSeconds",
                    _keepAliveSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                builder.UseSetting("Agents:Feed:MaxSubscribers",
                    _maxSubscribers.ToString(System.Globalization.CultureInfo.InvariantCulture));
                builder.UseSetting("Agents:Feed:MaxQueuedEvents",
                    _maxQueuedEvents.ToString(System.Globalization.CultureInfo.InvariantCulture));
                builder.UseSetting("Agents:Trace:MaxSteps",
                    _maxTraceSteps.ToString(System.Globalization.CultureInfo.InvariantCulture));
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

            /// <summary>Written to the caller a chunk at a time when the streaming arm is used, so a
            /// test can assert that frames arrive rather than that a body was buffered.</summary>
            public List<String> StreamChunks { get; } = new List<String>();

            public Boolean Configured => true;

            public Task<SidecarResponse> ForwardAsync(HttpMethod method, String path, String jsonBody,
                CancellationToken cancellationToken)
            {
                Calls.Add(method.Method + " " + path);
                return Task.FromResult(new SidecarResponse(_status, _body, _contentType));
            }

            public async Task StreamAsync(String path, Func<Int32, String, Task> onHeaders,
                System.IO.Stream destination, CancellationToken cancellationToken)
            {
                Calls.Add("STREAM " + path);
                await onHeaders(_status, _contentType);

                // The body first, then any chunks, which is what the shipped client does: it copies
                // the response through whatever the status was, so a refusal's body reaches the
                // caller rather than being swallowed by a forward that assumed success.
                var written = String.IsNullOrEmpty(_body)
                    ? StreamChunks
                    : new List<String>(StreamChunks) { _body };

                foreach (var chunk in written)
                {
                    var bytes = Encoding.UTF8.GetBytes(chunk);
                    await destination.WriteAsync(bytes, cancellationToken);
                    await destination.FlushAsync(cancellationToken);
                }
            }

            public Task<Boolean> IsReachableAsync(CancellationToken cancellationToken)
                => Task.FromResult(true);
        }

        #endregion
    }
}
