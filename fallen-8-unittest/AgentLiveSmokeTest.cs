// MIT License
//
// AgentLiveSmokeTest.cs
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
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Agents.Configuration;
using NoSQL.GraphDB.Agents.Model;
using NoSQL.GraphDB.Agents.Runtime;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   One real agent, end to end, against a real Fallen-8 instance and whatever model that
    ///   instance's <c>purpose: agent</c> resolves to. Phase 0's throwaway harness, graduated.
    ///
    ///   <para>
    ///     <b>Gated and never run in CI</b>, because it needs an instance with the Chat capability
    ///     on and a model behind it, and because a metered provider charges for it. It is here for
    ///     the thing no stub can do: a stub accepts whatever it is handed, so it proves nothing
    ///     about a real provider's parsing. That is not a theoretical worry - the one bug Phase 1a
    ///     shipped past every unit test was a null <c>content</c> on an assistant turn that only
    ///     called a tool, and only the live service refused it.
    ///   </para>
    ///   <para>
    ///     To run: set <c>F8_TEST_AGENT_BASEURL</c> (and <c>F8_TEST_AGENT_APIKEY</c> if that
    ///     instance has one), remove the <c>[Ignore]</c>, and run the one test. What it proves is
    ///     the whole route B claim in one call: a tool offered by this host reaches a real model
    ///     through the instance's gateway, the model's call comes back parsed, the framework invokes
    ///     the local function, and the model's next turn uses the result.
    ///   </para>
    /// </summary>
    [TestClass]
    public class AgentLiveSmokeTest
    {
        [TestMethod]
        [Ignore("Live-model smoke: set F8_TEST_AGENT_BASEURL and remove [Ignore] to run.")]
        [TestCategory("LiveModel")]
        public async Task OneRealAgentCallsOneRealToolAndUsesItsResult()
        {
            var baseUrl = Env("F8_TEST_AGENT_BASEURL");
            if (String.IsNullOrEmpty(baseUrl))
            {
                Assert.Inconclusive("F8_TEST_AGENT_BASEURL not set.");
            }

            using var http = new HttpClient
            {
                BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/"),
                // The transport owns the deadline, as it does in the host (AgentsHost, which
                // states why).
                Timeout = TimeSpan.FromSeconds(600),
            };
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var apiKey = Env("F8_TEST_AGENT_APIKEY");
            if (!String.IsNullOrEmpty(apiKey))
            {
                http.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", apiKey);
            }

            // A local function standing in for an MCP tool. Its shape is what matters: one required
            // string argument, so a model that ignores the schema produces a call this host still
            // has to survive.
            var calls = new List<String>();
            var tool = AIFunctionFactory.Create(
                (String graphNamespace) =>
                {
                    calls.Add(graphNamespace ?? "(none)");
                    return "8";
                },
                new AIFunctionFactoryOptions
                {
                    Name = "count_vertices",
                    Description = "Counts the vertices in one Fallen-8 namespace. "
                        + "The namespace is usually 'default'.",
                });

            var options = new AgentsOptions();
            options.Limits.MaxStepsPerRun = 6;
            options.Limits.MaxRunSeconds = 300;
            options.Limits.MaxConcurrentAgents = 1;

            var wrapped = Options.Create(options);
            var chat = new Fallen8ChatClient(http);
            var roles = RoleCatalog.Load(options);
            using var feed = new AgentFeedDispatcher(wrapped,
                TestLoggerFactory.Create().CreateLogger<AgentFeedDispatcher>());
            var journal = new AgentJournal(feed, wrapped);
            using var registry = new AgentRegistry(wrapped,
                TestLoggerFactory.Create().CreateLogger<AgentRegistry>(), journal);
            var runner = new AgentRunner(registry, roles, new SingleTool(tool), chat, journal, wrapped,
                TestLoggerFactory.Create());

            Assert.IsTrue(registry.TryAdmit(
                new AgentSpawn("assistant", "How many vertices are in the default namespace?"),
                out var agent, out var problem), problem);
            Assert.IsTrue(roles.TryGet("assistant", out var role, out _));

            await runner.RunAsync(agent, role, null);

            Assert.AreEqual(AgentState.Completed, agent.State,
                "the run did not complete: " + (agent.Failure ?? "(no reason recorded)"));

            Assert.AreEqual(1, calls.Count,
                "the model did not call the tool, which is the failure mode the role prompt exists "
                + "to prevent: a bare imperative made it fabricate a result instead");

            Assert.IsTrue(Interlocked.Read(ref agent.Steps) >= 2,
                "a tool call and the answer after it are two steps");
            Assert.AreEqual(1L, Interlocked.Read(ref agent.ToolCalls));
            StringAssert.Contains(agent.ResultText, "8",
                "the model did not use the tool's result: " + agent.ResultText);

            // Provenance, which is the whole point of asking the instance rather than a provider:
            // this host configured no model and learned one from the answer.
            Assert.IsNotNull(chat.LastSeen);
            Assert.IsFalse(String.IsNullOrWhiteSpace(chat.LastSeen.Backend));
            Assert.IsFalse(String.IsNullOrWhiteSpace(chat.LastSeen.Model));

            // Not asserted as non-zero: a measured provider reply carried a completion count of
            // zero for a real tool call, so the flag is the honest reading and the counters are a
            // floor.
            Assert.IsTrue(Interlocked.Read(ref agent.InputTokens) >= 0);

            // The trace is what a reviewer reads afterwards, so a live run has to have produced one
            // with the shape of what happened: a model call naming what served it, and the tool call
            // in between.
            var steps = agent.Trace.Steps();
            Assert.IsTrue(steps.Any(s => s.Kind == "modelCall" && s.Backend != null && s.Model != null),
                "no model call in the trace named the backend and model that served it");
            Assert.IsTrue(steps.Any(s => s.Kind == "toolCall" && s.Tool == "count_vertices"
                    && s.Success == true),
                "the tool call this run made is not in its trace as a success");
        }

        private static String Env(String name)
        {
            return Environment.GetEnvironmentVariable(name);
        }

        private sealed class SingleTool : IAgentToolSource
        {
            public SingleTool(AITool tool)
            {
                Tools = new[] { tool };
            }

            public IReadOnlyList<AITool> Tools
            {
                get;
            }

            public Boolean Connected => true;

            public String Failure => null;

            public System.Threading.Tasks.Task EnsureConnectedAsync(
                System.Threading.CancellationToken cancellationToken = default)
                => System.Threading.Tasks.Task.CompletedTask;
        }
    }
}
