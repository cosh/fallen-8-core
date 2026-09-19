// MIT License
//
// AgentRuntimeTest.cs
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
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Agents.Configuration;
using NoSQL.GraphDB.Agents.Diagnostics;
using NoSQL.GraphDB.Agents.Runtime;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The agent host's runtime (feature agent-host): the registry's state machine, retention and
    ///   caps, the role catalogue's prompts and allowlists, and the runner driving a real Microsoft
    ///   Agent Framework tool loop.
    ///
    ///   <para>
    ///     <b>No model is involved anywhere in this file.</b> The framework is driven by a scripted
    ///     <see cref="IChatClient" />, which is the seam the spec requires exactly so lifecycle,
    ///     budgets, allowlists and cancellation are assertable in CI. The tools are real local
    ///     functions, so the loop that invokes them is the real one.
    ///   </para>
    /// </summary>
    [TestClass]
    public class AgentRuntimeTest
    {
        [TestMethod]
        public void EveryShippedRolePromptCarriesTheFourPropertiesThatKeepAnAgentHonest()
        {
            // Spec section 3.2a, and it is a behaviour test rather than style policing. Phase 0
            // measured the SAME model, tool schema and temperature producing parsed tool calls under
            // a prompt that named the tools and forbade invented results, and producing the literal
            // text of a tool-call marker followed by a FABRICATED result under a bare imperative.
            var catalog = RoleCatalog.Load(new AgentsOptions());

            foreach (var name in new[] { "assistant", "orchestrator", "worker" })
            {
                Assert.IsTrue(catalog.TryGet(name, out var role, out _), name + " is a shipped role");
                var prompt = role.Prompt.ToLowerInvariant();

                // Property 1 was pinned by prompt.Contains("tool") and Contains("call"), which
                // both hold from the citation paragraph alone: measured, deleting the ENTIRE
                // "You have tools" paragraph from all three prompts left this test green, so the
                // one property Phase 0 measured as deciding whether a tool call parses at all was
                // covered by nothing. What is asserted now is the paragraph's three claims, in the
                // words that carry them. A reword has to keep the claim, which is the intent.
                Assert.IsTrue(role.Prompt.Contains("the only way you", StringComparison.Ordinal),
                    name + ": the prompt must say tools are the ONLY way it learns anything, not "
                    + "merely that tools exist");
                Assert.IsTrue(role.Prompt.Contains("by CALLING a", StringComparison.Ordinal),
                    name + ": the prompt must say a question needing data is answered by CALLING "
                    + "one, in those terms: a bare imperative produced a fabricated result");
                Assert.IsTrue(role.Prompt.Contains("one tool at a time", StringComparison.Ordinal),
                    name + ": the prompt must say to call one tool and read its result before "
                    + "deciding the next, or a model fans out and reasons over nothing");
                Assert.IsTrue(prompt.Contains("never invent"),
                    name + ": the prompt must forbid inventing a tool's result");
                Assert.IsTrue(role.Prompt.Contains("[t:<name>]", StringComparison.Ordinal),
                    name + ": the prompt must require the citation the grounding check counts");
                Assert.IsFalse(role.Prompt.Contains("[t:<id>]", StringComparison.Ordinal),
                    name + ": a tool-call id is never shown to the model, so a prompt that asks it "
                    + "to cite one is asking it to invent one");
            }

            // The one-composer rule, which only the two swarm roles carry.
            Assert.IsTrue(catalog.TryGet("orchestrator", out var orchestrator, out _));
            Assert.IsTrue(orchestrator.Prompt.Contains("ONLY composer", StringComparison.Ordinal),
                "an orchestrator must be told its final text is the whole answer");

            Assert.IsTrue(catalog.TryGet("worker", out var worker, out _));
            Assert.IsTrue(worker.Prompt.Contains("NOT the composer", StringComparison.Ordinal),
                "a worker must be told it is writing for its orchestrator, not for the user");
        }

        [TestMethod]
        public void AnUnknownRoleIsRefusedWithTheAcceptedSetNamedAndAnOmittedOneIsTheAssistant()
        {
            var catalog = RoleCatalog.Load(new AgentsOptions());

            Assert.IsFalse(catalog.TryGet("architect", out _, out var problem));
            StringAssert.Contains(problem, "assistant");
            StringAssert.Contains(problem, "orchestrator");
            StringAssert.Contains(problem, "worker");

            Assert.IsTrue(catalog.TryGet(null, out var byDefault, out _));
            Assert.AreEqual("assistant", byDefault.Name);

            Assert.IsTrue(catalog.TryGet("ASSISTANT", out var shouted, out _),
                "a role name is matched case-insensitively; a caller's capitalisation is not a refusal");
            Assert.AreEqual("assistant", shouted.Name);
        }

        [TestMethod]
        public void AnAllowlistNarrowsTheAdvertisedToolsAndAnEmptyOnePassesThemThrough()
        {
            var advertised = new List<AITool> { Tool("f8_overview"), Tool("f8_read"), Tool("f8_write") };

            var shipped = RoleCatalog.Load(new AgentsOptions());
            Assert.IsTrue(shipped.TryGet("assistant", out var assistant, out _));
            Assert.AreEqual(3, assistant.Filter(advertised).Count,
                "the assistant's allowlist is empty, so it sees every advertised tool");

            Assert.IsTrue(shipped.TryGet("orchestrator", out var orchestrator, out _));
            var narrowed = orchestrator.Filter(advertised);
            Assert.AreEqual(1, narrowed.Count,
                "an orchestrator that can look but must delegate decomposes better");
            Assert.AreEqual("f8_overview", narrowed[0].Name);

            // A configured list REPLACES the shipped one, because the only reason to configure one
            // is to say "this role may use exactly these".
            var configured = new AgentsOptions();
            configured.Roles["assistant"] = Allow("f8_read");
            var custom = RoleCatalog.Load(configured);
            Assert.IsTrue(custom.TryGet("assistant", out var restricted, out _));
            var kept = restricted.Filter(advertised);
            Assert.AreEqual(1, kept.Count);
            Assert.AreEqual("f8_read", kept[0].Name);
        }

        [TestMethod]
        public void AnAllowlistNamingAToolTheServerDoesNotAdvertiseIsSilentlyNarrowerRatherThanAnError()
        {
            // The correct reading: the allowlist says what a role MAY use, not what must exist. The
            // status route reports the count a role actually got, which is where this shows.
            var configured = new AgentsOptions();
            configured.Roles["worker"] = Allow("f8_read", "f8_time_travel");
            var catalog = RoleCatalog.Load(configured);

            Assert.IsTrue(catalog.TryGet("worker", out var worker, out _));
            var kept = worker.Filter(new List<AITool> { Tool("f8_read") });

            Assert.AreEqual(1, kept.Count);
            Assert.AreEqual("f8_read", kept[0].Name);
            Assert.AreEqual(2, worker.AllowedTools.Count,
                "the configured list is reported as configured, so a name that matches nothing is visible");
        }

        [TestMethod]
        public async Task AnAgentRunsFromPendingThroughRunningToCompletedAndCarriesItsResult()
        {
            using var harness = new Harness(Script.Says("The graph has 8 vertices. [t:call-1]"));

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("assistant", "count them"),
                out var agent, out _));
            Assert.AreEqual(AgentState.Pending, agent.State, "an admitted agent has not started yet");

            await harness.Run(agent);

            Assert.AreEqual(AgentState.Completed, agent.State);
            Assert.AreEqual("The graph has 8 vertices. [t:call-1]", agent.ResultText);
            Assert.AreEqual(1L, Interlocked.Read(ref agent.Steps));
            Assert.AreEqual(0L, Interlocked.Read(ref agent.ToolCalls));
            Assert.IsNotNull(agent.FinishedUtc);
        }

        /// <summary>
        ///   A model naming a tool that does not exist ends the TURN rather than being handed an
        ///   error and asked again, and the run is recorded as having produced no answer.
        ///   <para>
        ///     This pins <c>TerminateOnUnknownCalls</c>, which nothing did: a name nothing serves
        ///     will not become a name something serves, so a retry only spends budget. Flipping it
        ///     fails this test.
        ///   </para>
        ///   <para>
        ///     It does NOT pin <c>UseProvidedChatClientAsIs</c>, and that was worth measuring
        ///     rather than assuming: setting that flag to false passes every test in this
        ///     repository, including this one, because the library's outer loop finds every call
        ///     already resolved by the inner one. The runner's comment there says so.
        ///   </para>
        /// </summary>
        [TestMethod]
        public async Task AToolNameNothingServesEndsTheTurnRatherThanBeingRetried()
        {
            // No tools at all, so the name the model asks for cannot resolve.
            using var harness = new Harness(
                Script.Calls("c1", "f8_a_tool_nothing_serves").Then("I answered anyway."));

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("assistant", "count them"),
                out var agent, out _));

            await harness.Run(agent);

            Assert.AreEqual(AgentState.Failed, agent.State,
                "the turn ended on the unknown call, so the run has no answer: " + agent.Failure);
            StringAssert.Contains(agent.Failure, "without producing an answer", agent.Failure);
            Assert.IsNull(agent.ResultText,
                "the second scripted turn must never be reached: a retry would spend the budget "
                + "of a host whose tool list cannot grow mid-run");
            Assert.AreEqual(0, agent.Trace.Steps().Count(s => s.Kind == "toolCall"),
                "nothing was invoked, so nothing belongs on the trace as a tool call");
        }

        /// <summary>
        ///   What Microsoft Agent Framework actually emits for a run, MEASURED, because four other
        ///   sites depend on two facts about it: which Meter the library publishes its GenAI
        ///   instruments on, and what it puts in the name of the one span it emits.
        ///   <para>
        ///     The WHOLE runner is driven rather than a rebuilt copy of its pipeline, because what
        ///     is under test is what the RUNNER hands the library: a test that rebuilt the pipeline
        ///     could not fail when the runner changed, which is the defect shape this feature keeps
        ///     producing. Both listeners are process-wide, hence DoNotParallelize.
        ///   </para>
        /// </summary>
        [TestMethod]
        [DoNotParallelize]
        public async Task TheFrameworksTelemetryLandsOnThisHostsMeterAndNamesTheRoleNotTheCaller()
        {
            const String CallerName = "NAME-MARKER-a-caller-chose-this";

            var published = new List<String>();
            var recorded = new List<String>();

            using var meters = new MeterListener();
            meters.InstrumentPublished = (instrument, listener) =>
            {
                // Start() replays every instrument alive in the process, so an unfiltered list
                // would carry whatever an earlier test left behind.
                if (!instrument.Name.StartsWith("gen_ai.", StringComparison.Ordinal))
                {
                    return;
                }

                published.Add(instrument.Meter.Name + "::" + instrument.Name);
                listener.EnableMeasurementEvents(instrument);
            };
            meters.SetMeasurementEventCallback<Int32>(
                (i, v, t, s) => recorded.Add(i.Meter.Name + "::" + i.Name));
            meters.SetMeasurementEventCallback<Int64>(
                (i, v, t, s) => recorded.Add(i.Meter.Name + "::" + i.Name));
            meters.SetMeasurementEventCallback<Double>(
                (i, v, t, s) => recorded.Add(i.Meter.Name + "::" + i.Name));
            meters.Start();

            var spans = new List<Activity>();
            using var activities = new ActivityListener
            {
                ShouldListenTo = source => source.Name == AgentsMetrics.SourceName,
                // AllDataAndRecorded, not a cheaper result: the library writes its gen_ai.* tags
                // only while Activity.IsAllDataRequested is true, so PropagationData would give a
                // span with a name and no tags.
                Sample = (ref ActivityCreationOptions<ActivityContext> options)
                    => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => spans.Add(activity),
            };
            ActivitySource.AddActivityListener(activities);

            using var harness = new Harness(Script.Says("eight"));
            Assert.IsTrue(harness.Registry.TryAdmit(
                new AgentSpawn("assistant", "count the vertices") { Name = CallerName },
                out var agent, out _));

            await harness.Run(agent);
            Assert.AreEqual(AgentState.Completed, agent.State,
                "a run that did not finish has no telemetry to inspect");

            // THE METER. The library names its own Meter after the source name the runner gives
            // it, so its instruments arrive on this host's meter and the exporter's single
            // AddMeter of that name carries them.
            CollectionAssert.Contains(published,
                AgentsMetrics.MeterName + "::gen_ai.client.token.usage",
                "the framework's token accounting has to arrive on the meter the exporter "
                + "registers; published: " + String.Join(", ", published));
            CollectionAssert.Contains(recorded,
                AgentsMetrics.MeterName + "::gen_ai.client.operation.duration",
                "and it has to be WRITTEN to that meter, not merely created on it; recorded: "
                + String.Join(", ", recorded));
            Assert.IsFalse(
                published.Any(p => p.StartsWith("Experimental.", StringComparison.Ordinal)),
                "nothing here publishes under a library default name, so registering one would "
                + "be a no-op; published: " + String.Join(", ", published));

            // THE SPAN, whose NAME is what a collector deriving metrics from spans keys a series
            // on. One per run: the runner sets UseProvidedChatClientAsIs, so the library wires no
            // second telemetry client below the tool loop.
            var invoked = spans
                .Where(s => s.DisplayName.StartsWith("invoke_agent", StringComparison.Ordinal))
                .ToList();
            Assert.AreEqual(1, invoked.Count,
                "one framework span per run; seen: "
                + String.Join(" | ", spans.Select(s => s.DisplayName)));

            StringAssert.Contains(invoked[0].DisplayName, "assistant",
                "the ROLE is what the runner hands the framework as the agent's name");
            Assert.AreEqual("assistant", invoked[0].GetTagItem("gen_ai.agent.name") as String,
                "the span's agent-name tag is the role too");
            Assert.IsFalse(invoked[0].DisplayName.Contains("MARKER", StringComparison.Ordinal),
                "no caller text may reach a span name: " + invoked[0].DisplayName);

            // And what remains, stated rather than hoped for: the id is in the name whatever the
            // name is, which is why the documented promise is about caller text and not about
            // identifiers, and why the shipped collector bounds this name before deriving metrics
            // from it.
            StringAssert.Contains(invoked[0].DisplayName, agent.Id,
                "the framework concatenates the agent id into the span name unconditionally");
            Assert.AreEqual(agent.Id, invoked[0].GetTagItem("gen_ai.agent.id") as String);
        }

        [TestMethod]
        public async Task TheFrameworkInvokesAToolAndTheHostCountsTheCallAndBothStepsAroundIt()
        {
            var invoked = 0;
            var tool = AIFunctionFactory.Create(() => { invoked++; return "8"; },
                new AIFunctionFactoryOptions { Name = "count_vertices", Description = "Counts vertices." });

            using var harness = new Harness(
                Script.Calls("call-1", "count_vertices").Then("The graph has 8 vertices. [t:call-1]"),
                tools: new List<AITool> { tool });

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("assistant", "count them"),
                out var agent, out _));
            await harness.Run(agent);

            Assert.AreEqual(AgentState.Completed, agent.State);
            Assert.AreEqual(1, invoked, "the framework's loop invoked the real function");
            Assert.AreEqual(2L, Interlocked.Read(ref agent.Steps),
                "the call and the answer after it are two model calls, and the meter sees both");
            Assert.AreEqual(1L, Interlocked.Read(ref agent.ToolCalls));
            StringAssert.Contains(agent.ResultText, "8 vertices");
        }

        [TestMethod]
        public async Task AToolOutsideTheRolesAllowlistIsNotEvenOfferedToTheModel()
        {
            // The allowlist is not a discouragement in prose: the model never sees the tool, so it
            // cannot name it. Asserted on what the chat client was OFFERED, which is the only place
            // the difference is observable.
            var configured = new AgentsOptions();
            configured.Roles["assistant"] = Allow("f8_overview");

            using var harness = new Harness(Script.Says("ok"), options: configured,
                tools: new List<AITool> { Tool("f8_overview"), Tool("f8_write") });

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("assistant", "look"), out var agent, out _));
            await harness.Run(agent);

            var offered = harness.Client.LastTools;
            Assert.AreEqual(1, offered.Count);
            Assert.AreEqual("f8_overview", offered[0]);
        }

        [TestMethod]
        public async Task TheRolePromptIsHandedToTheClientAndAnAppendixIsAddedRatherThanSubstituted()
        {
            using var harness = new Harness(Script.Says("ok"));

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("assistant", "hello"), out var agent, out _));
            Assert.IsTrue(RoleCatalog.Load(new AgentsOptions()).TryGet("assistant", out var role, out _));

            await harness.Runner.RunAsync(agent, role, "Answer in German.");

            // The framework carries an agent's system prompt on ChatOptions.Instructions rather than
            // as a message, so this is where the runner's half of the contract is observable. It is
            // only HALF: an adapter that reads only the message list drops this silently, which is
            // what happened and what AgentChatAdapterTest now pins on the wire itself. Asserting
            // here alone is what let a dropped prompt pass.
            var instructions = harness.Client.LastInstructions;
            Assert.IsNotNull(instructions);
            StringAssert.Contains(instructions, "Never invent",
                "a caller's appendix cannot displace the rule that keeps the agent honest");
            StringAssert.Contains(instructions, "Answer in German.");
            Assert.IsTrue(instructions.IndexOf("Never invent", StringComparison.Ordinal)
                < instructions.IndexOf("Answer in German.", StringComparison.Ordinal),
                "the role prompt comes first; the appendix is appended");

            Assert.IsFalse(harness.Client.LastMessages.Any(m => m.Role == ChatRole.System),
                "the framework does NOT put instructions in the message list, which is exactly why "
                + "an adapter has to read the property");
        }

        [TestMethod]
        public async Task TheStepCapEndsTheRunAsBudgetExceededNamingSteps()
        {
            // A model that keeps calling a tool is the loop this cap exists for. The scripted client
            // never stops asking, so only the cap can end it.
            var tool = AIFunctionFactory.Create(() => "8",
                new AIFunctionFactoryOptions { Name = "count_vertices", Description = "Counts vertices." });

            var options = new AgentsOptions();
            options.Limits.MaxStepsPerRun = 3;

            using var harness = new Harness(Script.AlwaysCalls("count_vertices"), options: options,
                tools: new List<AITool> { tool });

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("assistant", "loop"), out var agent, out _));
            await harness.Run(agent);

            Assert.AreEqual(AgentState.BudgetExceeded, agent.State);
            Assert.AreEqual(BudgetKind.Steps, agent.Budget);
            Assert.AreEqual(3L, Interlocked.Read(ref agent.Steps),
                "the cap is checked BEFORE the call, so the run stops with its budget intact");
            StringAssert.Contains(agent.Failure, "Agents:Limits:MaxStepsPerRun");
        }

        [TestMethod]
        public async Task TheToolCallCapEndsTheRunAsBudgetExceededNamingToolCalls()
        {
            // The fourth budget, and the one that had no test: deleting its enforcement left every
            // other agent test green. The step cap is set high on purpose so that the TOOL-CALL cap
            // is what fires, which a shared cap would hide.
            var tool = AIFunctionFactory.Create(() => "8",
                new AIFunctionFactoryOptions { Name = "count_vertices", Description = "Counts vertices." });

            var options = new AgentsOptions();
            options.Limits.MaxStepsPerRun = 50;
            options.Limits.MaxToolCallsPerRun = 3;

            using var harness = new Harness(Script.AlwaysCalls("count_vertices"), options: options,
                tools: new List<AITool> { tool });

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("assistant", "loop"), out var agent, out _));
            await harness.Run(agent);

            Assert.AreEqual(AgentState.BudgetExceeded, agent.State);
            Assert.AreEqual(BudgetKind.ToolCalls, agent.Budget);
            Assert.AreEqual(3L, Interlocked.Read(ref agent.ToolCalls));
            Assert.IsTrue(Interlocked.Read(ref agent.Steps) < 50,
                "the step cap must not be what ended this run");
            StringAssert.Contains(agent.Failure, "Agents:Limits:MaxToolCallsPerRun");
        }

        [TestMethod]
        public async Task ToolCallsAreCountedPerCallRatherThanPerResponseSoOneTurnCanOvershoot()
        {
            // The documented inexactness, pinned so it stays a known shape rather than a surprise:
            // the cap is sampled between MODEL calls, because this seam never sees an invocation.
            // A model that asks for four tools at once therefore spends four against a cap of two,
            // and the cap stops the next model call. Bounding it tighter would mean owning the loop.
            var tool = AIFunctionFactory.Create(() => "8",
                new AIFunctionFactoryOptions { Name = "count_vertices", Description = "Counts vertices." });

            var options = new AgentsOptions();
            options.Limits.MaxStepsPerRun = 50;
            options.Limits.MaxToolCallsPerRun = 2;

            using var harness = new Harness(Script.AlwaysCallsMany("count_vertices", 4),
                options: options, tools: new List<AITool> { tool });

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("assistant", "loop"), out var agent, out _));
            await harness.Run(agent);

            Assert.AreEqual(AgentState.BudgetExceeded, agent.State);
            Assert.AreEqual(BudgetKind.ToolCalls, agent.Budget);
            Assert.AreEqual(4L, Interlocked.Read(ref agent.ToolCalls),
                "all four calls in the one response were counted, which is the overshoot the doc names");
            Assert.AreEqual(1L, Interlocked.Read(ref agent.Steps),
                "and the cap stopped the very next model call");
        }

        [TestMethod]
        public async Task TheTokenBudgetEndsTheRunAsBudgetExceededNamingTokens()
        {
            var tool = AIFunctionFactory.Create(() => "8",
                new AIFunctionFactoryOptions { Name = "count_vertices", Description = "Counts vertices." });

            var options = new AgentsOptions();
            options.Limits.MaxStepsPerRun = 50;

            using var harness = new Harness(Script.AlwaysCalls("count_vertices").WithUsage(40, 10),
                options: options, tools: new List<AITool> { tool });

            Assert.IsTrue(harness.Registry.TryAdmit(
                new AgentSpawn("assistant", "loop") { TokenBudget = 120 }, out var agent, out _));
            await harness.Run(agent);

            Assert.AreEqual(AgentState.BudgetExceeded, agent.State);
            Assert.AreEqual(BudgetKind.Tokens, agent.Budget);

            // 150, not 120, and the overshoot is inherent rather than a bug: a step costs 50, the
            // check runs BEFORE a call, so the third step was admitted at 100 and carried the total
            // past the budget on the way back. Bounding it tighter would mean pricing a request
            // before making it. What is guaranteed is that no call is made once the budget is
            // reached, which is why the total is one step over and not two.
            var spent = Interlocked.Read(ref agent.InputTokens) + Interlocked.Read(ref agent.OutputTokens);
            Assert.AreEqual(150L, spent);
            Assert.IsTrue(spent >= 120L && spent < 120L + 50L,
                "a token budget is a ceiling a run stops AT, overshooting by at most one step");
            Assert.IsFalse(agent.UnreportedUsage, "this backend reported its usage");
        }

        [TestMethod]
        public async Task UsageABackendDidNotReportIsCountedAsZeroAndFlaggedRatherThanEstimated()
        {
            using var harness = new Harness(Script.Says("ok").WithoutUsage());

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("assistant", "hi"), out var agent, out _));
            await harness.Run(agent);

            Assert.AreEqual(AgentState.Completed, agent.State);
            Assert.AreEqual(0L, Interlocked.Read(ref agent.InputTokens));
            Assert.AreEqual(0L, Interlocked.Read(ref agent.OutputTokens));
            Assert.IsTrue(agent.UnreportedUsage,
                "a token count nobody measured is flagged, so the totals read as a floor");
            Assert.AreEqual(1L, Interlocked.Read(ref agent.Steps),
                "an unreported step is still a step");
        }

        [TestMethod]
        public async Task TheTimeCapEndsTheRunAsBudgetExceededNamingTimeAndNotAsACancellation()
        {
            // The distinction this test exists for: the deadline is its own source, so a run that
            // ran out of time does not report itself as something a caller cancelled.
            var options = new AgentsOptions();
            options.Limits.MaxRunSeconds = 1;

            using var harness = new Harness(Script.Waits(TimeSpan.FromMinutes(5)), options: options);

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("assistant", "slow"), out var agent, out _));
            await harness.Run(agent);

            Assert.AreEqual(AgentState.BudgetExceeded, agent.State);
            Assert.AreEqual(BudgetKind.Time, agent.Budget);
            StringAssert.Contains(agent.Failure, "Agents:Limits:MaxRunSeconds");
        }

        [TestMethod]
        public async Task ACancelStopsARunInFlightAndTheFirstEndingWins()
        {
            using var harness = new Harness(Script.Waits(TimeSpan.FromMinutes(5)));

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("assistant", "slow"), out var agent, out _));
            var running = harness.Run(agent);

            // The cancel arrives while the model call is in flight.
            await harness.Client.FirstCall;
            Assert.IsTrue(harness.Registry.TryCancel(agent.Id, out var signalled));
            Assert.AreEqual(1, signalled);

            await running;

            Assert.AreEqual(AgentState.Cancelled, agent.State);
            StringAssert.Contains(agent.Failure, "Cancelled by request.");

            // Finish is first-wins, so nothing that unwinds afterwards can rewrite how it ended.
            Assert.IsFalse(harness.Registry.Finish(agent.Id, AgentState.Completed, resultText: "too late"));
            Assert.AreEqual(AgentState.Cancelled, agent.State);
            Assert.IsNull(agent.ResultText);
        }

        [TestMethod]
        public void CancellingAnAgentThatAlreadyFinishedIsNotAnError()
        {
            using var harness = new Harness(Script.Says("ok"));
            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("assistant", "hi"), out var agent, out _));
            Assert.IsTrue(harness.Registry.Finish(agent.Id, AgentState.Completed, resultText: "done"));

            // It asked for a state the agent already has; reporting a conflict would make a race
            // look like a mistake.
            Assert.IsTrue(harness.Registry.TryCancel(agent.Id, out var signalled));
            Assert.AreEqual(0, signalled);
            Assert.AreEqual(AgentState.Completed, agent.State);
        }

        [TestMethod]
        public void CancellingAnOrchestratorStopsItsLiveWorkersAndLeavesTheFinishedOneAlone()
        {
            using var harness = new Harness(Script.Says("ok"));

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("orchestrator", "plan"),
                out var boss, out _));
            Assert.IsTrue(harness.Registry.TryAdmit(
                new AgentSpawn("worker", "part one") { ParentId = boss.Id }, out var one, out _));
            Assert.IsTrue(harness.Registry.TryAdmit(
                new AgentSpawn("worker", "part two") { ParentId = boss.Id }, out var two, out _));

            // A finished worker is not signalled again, so the count is of what actually moved.
            Assert.IsTrue(harness.Registry.Finish(two.Id, AgentState.Completed, resultText: "twelve"));

            Assert.IsTrue(harness.Registry.TryCancel(boss.Id, out var signalled));
            Assert.AreEqual(2, signalled, "the orchestrator and its ONE live worker");
            Assert.AreEqual(AgentState.Cancelled, boss.State);
            Assert.AreEqual(AgentState.Cancelled, one.State);
            StringAssert.Contains(one.Failure, "Its orchestrator ended (cancelled)",
                "the worker's row has to name the ending that stopped it, because \"cancelled\" on "
                + "its own reads as somebody having cancelled the worker: " + one.Failure);
            Assert.AreEqual(AgentState.Completed, two.State, "a finished worker keeps how it ended");
        }

        /// <summary>
        ///   EVERY ending stops the workers it leaves behind, not just a cancel, and the reason on
        ///   the worker's row names which ending stopped it.
        ///   <para>
        ///     Completed is the case in here on purpose, because it is the most debatable one and
        ///     the one that made this a defect rather than a rough edge: an orchestrator that
        ///     answers without awaiting is a real outcome, and the orchestrator prompt says nobody
        ///     but it reads a worker's result. So a worker still running then is spending a budget
        ///     and holding a slot for a reader the feature says does not exist. Three deep, because
        ///     a walk that stops at the first level leaves a grandchild running.
        ///   </para>
        /// </summary>
        [TestMethod]
        public void EveryEndingStopsTheWorkersItLeavesBehindAndTheReasonNamesIt()
        {
            var options = new AgentsOptions();
            options.Limits.MaxConcurrentAgents = 0;
            // Off, so the tree can be three deep and the walk has to pass the first level.
            options.Limits.MaxSwarmDepth = 0;
            using var harness = new Harness(Script.Says("ok"), options: options);

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("orchestrator", "plan"),
                out var boss, out _));
            Assert.IsTrue(harness.Registry.TryAdmit(
                new AgentSpawn("worker", "part one") { ParentId = boss.Id }, out var mid, out _));
            Assert.IsTrue(harness.Registry.TryAdmit(
                new AgentSpawn("worker", "part one, split") { ParentId = mid.Id }, out var leaf, out _));

            Assert.IsTrue(harness.Registry.Finish(boss.Id, AgentState.Completed,
                resultText: "answered"));

            Assert.AreEqual(AgentState.Completed, boss.State,
                "the ending that cascaded is itself unchanged");
            Assert.AreEqual(AgentState.Cancelled, mid.State,
                "an orchestrator that answered left this worker producing for nobody");
            Assert.AreEqual(AgentState.Cancelled, leaf.State,
                "the cascade stopped at the first level, so a grandchild ran on with nobody above it");
            StringAssert.Contains(mid.Failure, "Its orchestrator ended (completed)", mid.Failure);
            Assert.AreEqual(BudgetKind.None, mid.Budget, "nothing about a cascade is a budget breach");
            Assert.IsNull(mid.ResultText);

            // Signalled, not merely relabelled: the token is what stops a run that is in flight and
            // the completion signal is what wakes an orchestrator sitting in await_workers.
            Assert.IsTrue(mid.Cancellation.IsCancellationRequested);
            Assert.IsTrue(leaf.Cancellation.IsCancellationRequested);
            Assert.IsTrue(leaf.Finished.IsCompleted);

            Assert.AreEqual(0, harness.Registry.ActiveCount,
                "a cascaded ending releases the slot like any other");
        }

        /// <summary>
        ///   Presence in the registry is not liveness: a finished orchestrator is readable for
        ///   <c>RetainFinishedMinutes</c>, and a worker admitted under one would be a run no ending
        ///   reaches. The refusal names the state it is in, and does not spend the orchestrator's
        ///   worker allowance, which is what pins the gate as sitting before that counter.
        /// </summary>
        [TestMethod]
        public void AFinishedOrchestratorCannotTakeAWorkerWhileItIsStillRetained()
        {
            using var harness = new Harness(Script.Says("ok"));

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("orchestrator", "plan"),
                out var boss, out _));
            Assert.IsTrue(harness.Registry.Finish(boss.Id, AgentState.Completed,
                resultText: "answered"));

            // The premise, asserted rather than assumed: the parent is still there to be found.
            Assert.IsTrue(harness.Registry.TryGet(boss.Id, out _),
                "the parent is still retained, which is the difference this gate exists for");

            Assert.IsFalse(harness.Registry.TryAdmit(
                    new AgentSpawn("worker", "a late part") { ParentId = boss.Id },
                    out _, out var problem),
                "a worker under an orchestrator that has already ended is a run nothing will "
                + "collect and no cascade reaches");
            StringAssert.Contains(problem, "already ended", problem);
            StringAssert.Contains(problem, AgentStates.Wire(AgentState.Completed),
                "the refusal has to name the state the parent is in: " + problem);
            Assert.AreEqual(0, harness.Registry.Children(boss.Id).Count);
            Assert.AreEqual(0, boss.WorkersSpawned,
                "a refused spawn must not count against MaxWorkersPerOrchestrator");
            Assert.AreEqual(0, harness.Registry.ActiveCount);
        }

        /// <summary>
        ///   The invariant the gate and the downward walk exist to hold, over the race this class's
        ///   own doc describes: a tool call already dispatched when an ending lands still runs, so a
        ///   <c>spawn_worker</c> can arrive on either side of its orchestrator's ending.
        ///   <para>
        ///     Whichever order the lock serialises them in, only two outcomes are allowed: the spawn
        ///     is refused, or the worker it admitted is terminal once both calls have returned. This
        ///     cannot pass wrongly: the invariant holds in every interleaving, so a round that
        ///     misses the interesting window proves less rather than passing falsely, and no
        ///     assertion here waits on a timeout or spins.
        ///   </para>
        /// </summary>
        [TestMethod]
        public async Task ASpawnRacingItsOrchestratorsEndingNeverLeavesALiveWorkerBehind()
        {
            var options = new AgentsOptions();
            options.Limits.MaxConcurrentAgents = 0;
            using var harness = new Harness(Script.Says("ok"), options: options);

            for (var round = 0; round < 64; round++)
            {
                Assert.IsTrue(harness.Registry.TryAdmit(
                    new AgentSpawn("orchestrator", "plan " + round), out var boss, out _));

                using var both = new Barrier(2);
                AgentRecord worker = null;
                var admitted = false;

                var spawning = Task.Run(() =>
                {
                    both.SignalAndWait();
                    admitted = harness.Registry.TryAdmit(
                        new AgentSpawn("worker", "part") { ParentId = boss.Id }, out worker, out _);
                });
                var ending = Task.Run(() =>
                {
                    both.SignalAndWait();
                    harness.Registry.Finish(boss.Id, AgentState.Completed, resultText: "answered");
                });

                await Task.WhenAll(spawning, ending);

                if (admitted)
                {
                    Assert.IsTrue(AgentStates.IsTerminal(worker.State),
                        "round " + round + ": a worker admitted around its orchestrator's ending is "
                        + "still live, and nothing will ever reach it");
                }
            }

            Assert.AreEqual(0, harness.Registry.ActiveCount, "no round left anything running");
        }

        [TestMethod]
        public void TheConcurrencyCapRefusesASpawnWithAReasonAndAnEndingFreesTheSlot()
        {
            var options = new AgentsOptions();
            options.Limits.MaxConcurrentAgents = 2;
            using var harness = new Harness(Script.Says("ok"), options: options);

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("assistant", "one"), out var one, out _));
            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("assistant", "two"), out _, out _));

            Assert.IsFalse(harness.Registry.TryAdmit(new AgentSpawn("assistant", "three"), out _, out var problem));
            StringAssert.Contains(problem, "MaxConcurrentAgents");
            Assert.AreEqual(2, harness.Registry.ActiveCount);

            // A slot is released by an ENDING, not by the run's task finishing, so the cap holds
            // across a cancel racing a completion.
            Assert.IsTrue(harness.Registry.Finish(one.Id, AgentState.Completed, resultText: "done"));
            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("assistant", "three"), out _, out _));
        }

        [TestMethod]
        public void AnAgentWaitingForUserStillHoldsItsSlotBecauseItIsLive()
        {
            var options = new AgentsOptions();
            options.Limits.MaxConcurrentAgents = 1;
            using var harness = new Harness(Script.Says("ok"), options: options);

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("assistant", "chat"), out var agent, out _));
            Assert.IsTrue(harness.Registry.Advance(agent.Id, AgentState.WaitingForUser));

            Assert.IsFalse(harness.Registry.TryAdmit(new AgentSpawn("assistant", "another"), out _, out _),
                "an agent holding a session for the next message is still costing a slot");
        }

        [TestMethod]
        public void AFinishedAgentIsEvictedOnceItsRetentionHasPassed()
        {
            var clock = new StepClock(DateTimeOffset.Parse("2026-09-10T06:00:00Z"));
            var options = new AgentsOptions();
            options.Limits.RetainFinishedMinutes = 30;

            using var harness = new Harness(Script.Says("ok"), options: options, clock: clock);

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("assistant", "hi"), out var agent, out _));
            Assert.IsTrue(harness.Registry.Finish(agent.Id, AgentState.Completed, resultText: "done"));
            Assert.AreEqual(1, harness.Registry.All().Count);

            clock.Advance(TimeSpan.FromMinutes(29));
            Assert.AreEqual(1, harness.Registry.All().Count, "still inside its retention");

            clock.Advance(TimeSpan.FromMinutes(2));
            Assert.AreEqual(0, harness.Registry.All().Count);
            Assert.IsFalse(harness.Registry.TryGet(agent.Id, out _),
                "an evicted agent is a 404 whose message says retention is bounded");
        }

        [TestMethod]
        public void TheRetentionCeilingEvictsTheOldestFinishedAgentsFirstAndSparesTheLiveOnes()
        {
            var clock = new StepClock(DateTimeOffset.Parse("2026-09-10T06:00:00Z"));
            var options = new AgentsOptions();
            options.Limits.MaxRetainedAgents = 2;
            options.Limits.MaxConcurrentAgents = 0; // no cap, so four can be admitted at once
            options.Limits.RetainFinishedMinutes = 0; // no time-based eviction; the ceiling alone

            using var harness = new Harness(Script.Says("ok"), options: options, clock: clock);

            var finished = new List<AgentRecord>();
            for (var i = 0; i < 3; i++)
            {
                Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("assistant", "task " + i),
                    out var agent, out _));
                finished.Add(agent);
                clock.Advance(TimeSpan.FromSeconds(1));
                Assert.IsTrue(harness.Registry.Finish(agent.Id, AgentState.Completed, resultText: "done"));
            }

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("assistant", "live"), out var live, out _));

            var remaining = harness.Registry.All().Select(a => a.Id).ToList();
            Assert.AreEqual(3, remaining.Count, "two retained finished agents plus the live one");
            Assert.IsTrue(remaining.Contains(live.Id), "a live agent is never evicted by the ceiling");
            Assert.IsFalse(remaining.Contains(finished[0].Id), "the oldest finished one goes first");
            Assert.IsTrue(remaining.Contains(finished[2].Id));
        }

        [TestMethod]
        public void TheSwarmDepthCapStopsAWorkerOrchestratingInTurn()
        {
            // The cost of a tree is multiplicative while the thing an operator configures is per
            // agent: three levels of four is 21 agents from one request, each with its own token
            // budget, against a provider quota they all share. Depth 2 is "delegate once".
            var options = new AgentsOptions();
            options.Limits.MaxConcurrentAgents = 0;
            options.Limits.MaxSwarmDepth = 2;

            using var harness = new Harness(Script.Says("ok"), options: options);

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("orchestrator", "plan"),
                out var boss, out _));
            Assert.AreEqual(0, boss.Depth, "a caller's agent is the root");

            Assert.IsTrue(harness.Registry.TryAdmit(
                new AgentSpawn("worker", "part one") { ParentId = boss.Id }, out var worker, out _));
            Assert.AreEqual(1, worker.Depth);

            Assert.IsFalse(harness.Registry.TryAdmit(
                new AgentSpawn("worker", "deeper") { ParentId = worker.Id }, out _, out var problem),
                "a worker at the depth limit spawned one of its own");
            StringAssert.Contains(problem, "MaxSwarmDepth");
            StringAssert.Contains(problem, worker.Id, "the refusal names WHICH agent is too deep");
        }

        [TestMethod]
        public void TheWorkerCapCountsOverTheOrchestratorsLifeRatherThanWhatIsLive()
        {
            // A live-only count would let an orchestrator spawn its allowance, await them all, and
            // spawn again without bound, because its token budget bounds its OWN calls and not its
            // workers'. So finishing a worker does not buy another.
            var options = new AgentsOptions();
            options.Limits.MaxConcurrentAgents = 0;
            options.Limits.MaxWorkersPerOrchestrator = 2;

            using var harness = new Harness(Script.Says("ok"), options: options);

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("orchestrator", "plan"),
                out var boss, out _));

            Assert.IsTrue(harness.Registry.TryAdmit(
                new AgentSpawn("worker", "one") { ParentId = boss.Id }, out var first, out _));
            Assert.IsTrue(harness.Registry.TryAdmit(
                new AgentSpawn("worker", "two") { ParentId = boss.Id }, out var second, out _));

            // Both finish, which a live-only count would treat as room for two more.
            Assert.IsTrue(harness.Registry.Finish(first.Id, AgentState.Completed, resultText: "a"));
            Assert.IsTrue(harness.Registry.Finish(second.Id, AgentState.Completed, resultText: "b"));

            Assert.IsFalse(harness.Registry.TryAdmit(
                new AgentSpawn("worker", "three") { ParentId = boss.Id }, out _, out var problem),
                "finishing a worker bought the orchestrator another, so the cap bounds nothing");
            StringAssert.Contains(problem, "MaxWorkersPerOrchestrator");
            Assert.AreEqual(2, boss.WorkersSpawned, "the count is of what it spawned, ever");
        }

        [TestMethod]
        public void EitherSwarmCapIsOffAtANonPositiveValueLikeEveryOtherLimit()
        {
            // The convention every other limit here follows, and the one a startup line reports as
            // "unlimited": a non-positive value means off rather than a bound of zero, which for
            // these two would otherwise refuse every worker a swarm ever spawns.
            var options = new AgentsOptions();
            options.Limits.MaxConcurrentAgents = 0;
            options.Limits.MaxSwarmDepth = 0;
            options.Limits.MaxWorkersPerOrchestrator = 0;

            using var harness = new Harness(Script.Says("ok"), options: options);

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("orchestrator", "plan"),
                out var boss, out _));

            var parent = boss;
            for (var depth = 1; depth <= 5; depth++)
            {
                Assert.IsTrue(harness.Registry.TryAdmit(
                    new AgentSpawn("worker", "level " + depth) { ParentId = parent.Id },
                    out var child, out var problem), "depth " + depth + " was refused: " + problem);
                Assert.AreEqual(depth, child.Depth, "depth is still recorded when it is not capped");
                parent = child;
            }
        }

        [TestMethod]
        public void DepthIsStampedFromTheParentsRecordAndIsWhatTheSwarmCapCounts()
        {
            // Depth is stamped at admission from the parent's own depth rather than walked up the
            // tree, and it is what MaxSwarmDepth compares against, so a tree cannot gain a level.
            //
            // This test used to arrange an EVICTED ancestor, on the grounds that a walk would then
            // flatter the tree. That state is unreachable since every ending cascades to its live
            // workers and admission refuses a parent that has ended: a live agent's ancestor cannot
            // be evicted, because it would have had to end first and that would have ended this
            // agent too. An assertion about it would have been a test of nothing.
            var options = new AgentsOptions();
            options.Limits.MaxConcurrentAgents = 0;
            options.Limits.MaxSwarmDepth = 3;

            using var harness = new Harness(Script.Says("ok"), options: options);

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("orchestrator", "plan"),
                out var boss, out _));
            Assert.AreEqual(0, boss.Depth, "an agent a caller spawned is the root of its own tree");

            Assert.IsTrue(harness.Registry.TryAdmit(
                new AgentSpawn("worker", "middle") { ParentId = boss.Id }, out var middle, out _));
            Assert.AreEqual(1, middle.Depth);

            Assert.IsTrue(harness.Registry.TryAdmit(
                new AgentSpawn("worker", "leaf") { ParentId = middle.Id }, out var leaf, out _));
            Assert.AreEqual(2, leaf.Depth, "one deeper than the record that spawned it");

            // And the cap counts THAT number: a fourth level is refused, naming the depth it is at.
            Assert.IsFalse(harness.Registry.TryAdmit(
                    new AgentSpawn("worker", "too deep") { ParentId = leaf.Id }, out _, out var problem),
                "a cap of 3 admitted a fourth level of agent");
            StringAssert.Contains(problem, "MaxSwarmDepth", problem);
        }

        [TestMethod]
        public void EvictionBreaksTheParentLinkSoAnEvictedAncestorIsNotRetainedByItsDescendants()
        {
            // A record held its parent by reference and nothing ever cleared it, so eviction
            // removed a record from the listing while a descendant kept it, its bounded trace and
            // its undisposed token source alive, transitively up the whole ancestry, while Evict's
            // own comment claimed the collector took them.
            //
            // LATENT, not live, and the test says so because the claim was first written as though
            // it were live: no shipped path supplies a parent today, since the spawn route refuses
            // a caller-supplied parentId and the orchestrator's swarm tool is Phase 4. This test
            // builds the chain itself, which is the point: it pins the registry's eviction contract
            // rather than relying on another route's validation to keep it true.
            var clock = new StepClock(DateTimeOffset.Parse("2026-09-10T06:00:00Z"));
            var options = new AgentsOptions();
            options.Limits.MaxConcurrentAgents = 0;
            options.Limits.RetainFinishedMinutes = 10;
            options.Limits.MaxRetainedAgents = 0;

            // The depth cap is off because this test needs a three-generation chain and the
            // shipped cap of 2 exists precisely to refuse one. What is under test here is
            // eviction's effect on the parent link, not how deep a swarm may go.
            options.Limits.MaxSwarmDepth = 0;

            using var harness = new Harness(Script.Says("ok"), options: options, clock: clock);

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("assistant", "orchestrate"),
                out var grand, out _));
            Assert.IsTrue(harness.Registry.TryAdmit(
                new AgentSpawn("assistant", "middle") { ParentId = grand.Id }, out var middle, out _));
            Assert.IsTrue(harness.Registry.TryAdmit(
                new AgentSpawn("assistant", "leaf") { ParentId = middle.Id }, out var leaf, out _));

            Assert.AreSame(grand, middle.Parent, "this test needs the chain it is about");
            Assert.AreSame(middle, leaf.Parent);

            // The DESCENDANTS end and are evicted while their parent is still live, which is the
            // direction that is reachable: a parent's ending cancels its live workers, so an
            // evicted parent no longer has a surviving child, and this is the case the eviction
            // leak was about anyway. A record that held its parent kept that parent's whole trace
            // and token source alive after the listing had forgotten the holder.
            Assert.IsTrue(harness.Registry.Finish(leaf.Id, AgentState.Completed, resultText: "done"));
            Assert.IsTrue(harness.Registry.Finish(middle.Id, AgentState.Completed, resultText: "done"));

            clock.Advance(TimeSpan.FromMinutes(11));

            // Any read evicts, which is this registry's contract rather than a timer.
            Assert.AreEqual(1, harness.Registry.All().Count,
                "both finished descendants are past retention");
            Assert.IsFalse(harness.Registry.TryGet(leaf.Id, out _));
            Assert.IsFalse(harness.Registry.TryGet(middle.Id, out _));
            Assert.IsTrue(harness.Registry.TryGet(grand.Id, out _), "the live parent stays");

            Assert.IsNull(leaf.Parent,
                "an evicted record still holds ITS parent, so one reference kept the whole "
                + "ancestry alive through the chain");
            Assert.IsNull(middle.Parent,
                "the same one level up, which is what made it a chain rather than one link");

            // The lineage a client reads is unchanged: only the object reference goes.
            Assert.AreEqual(middle.Id, leaf.ParentId);
            Assert.AreEqual(grand.Id, middle.ParentId);
            Assert.AreEqual(AgentState.Pending, grand.State, "the live agent was not disturbed");
        }

        [TestMethod]
        public void EveryAgentCarriesTheHostInstanceThatRanItSoAnEmptyListIsNotMistakenForNothingEverRan()
        {
            using var harness = new Harness(Script.Says("ok"));
            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("assistant", "hi"), out var agent, out _));

            Assert.AreEqual(harness.Registry.HostInstanceId, agent.HostInstanceId);
            Assert.IsFalse(String.IsNullOrEmpty(agent.HostInstanceId));
            Assert.AreEqual(harness.Registry.HostInstanceId, agent.Summarize(agent.CreatedUtc).HostInstanceId);
        }

        [TestMethod]
        public void AListingIsNewestFirstAndAnUnnamedAgentTakesItsId()
        {
            var clock = new StepClock(DateTimeOffset.Parse("2026-09-10T06:00:00Z"));
            var options = new AgentsOptions();
            options.Limits.MaxConcurrentAgents = 0;
            using var harness = new Harness(Script.Says("ok"), options: options, clock: clock);

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("assistant", "first"), out var first, out _));
            clock.Advance(TimeSpan.FromSeconds(5));
            Assert.IsTrue(harness.Registry.TryAdmit(
                new AgentSpawn("assistant", "second") { Name = "counter" }, out var second, out _));

            var listing = harness.Registry.All();
            Assert.AreEqual(second.Id, listing[0].Id, "newest first is the order a reviewer wants");
            Assert.AreEqual("counter", listing[0].Name);
            Assert.AreEqual(first.Id, listing[1].Name, "an unnamed agent takes its id as its label");
        }

        [TestMethod]
        public void ABudgetIsOnlyReportedWhenABudgetIsWhatEndedTheRun()
        {
            using var harness = new Harness(Script.Says("ok"));

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("assistant", "hi"), out var agent, out _));
            Assert.IsTrue(harness.Registry.Finish(agent.Id, AgentState.Completed, resultText: "done"));
            Assert.IsNull(agent.Summarize(agent.FinishedUtc.Value).Budget);

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("assistant", "loop"), out var capped, out _));
            Assert.IsTrue(harness.Registry.Finish(capped.Id, AgentState.BudgetExceeded,
                failure: "out of tokens", budget: BudgetKind.Tokens));
            Assert.AreEqual("tokens", capped.Summarize(capped.FinishedUtc.Value).Budget);
        }

        [TestMethod]
        public void AnEndingIsRecordedWithFinishAndALiveStateWithAdvance()
        {
            // The invariant that makes "an ending is final" true rather than merely intended: the
            // two paths are not interchangeable, and mixing them throws rather than corrupting a
            // slot's meaning.
            using var harness = new Harness(Script.Says("ok"));
            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("assistant", "hi"), out var agent, out _));

            Assert.ThrowsException<ArgumentException>(
                () => harness.Registry.Advance(agent.Id, AgentState.Completed));
            Assert.ThrowsException<ArgumentException>(
                () => harness.Registry.Finish(agent.Id, AgentState.Running));

            Assert.IsFalse(harness.Registry.Advance("no-such-agent", AgentState.Running));
            Assert.IsFalse(harness.Registry.Finish("no-such-agent", AgentState.Completed));
        }

        [TestMethod]
        public async Task AFailingModelCallEndsTheRunAsFailedCarryingTheReason()
        {
            using var harness = new Harness(Script.Throws("The Fallen-8 chat gateway answered 503."));

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("assistant", "hi"), out var agent, out _));
            await harness.Run(agent);

            Assert.AreEqual(AgentState.Failed, agent.State);
            StringAssert.Contains(agent.Failure, "503");
            Assert.IsNull(agent.ResultText);
        }

        [TestMethod]
        public void AParentThatDoesNotExistIsRefusedRatherThanBuildingATreeNobodyOrchestrates()
        {
            using var harness = new Harness(Script.Says("ok"));

            Assert.IsFalse(harness.Registry.TryAdmit(
                new AgentSpawn("worker", "orphan") { ParentId = "a1-99" }, out _, out var problem));
            StringAssert.Contains(problem, "a1-99");
        }

        [TestMethod]
        public void AnOmittedTokenBudgetTakesTheConfiguredDefault()
        {
            var options = new AgentsOptions();
            options.Limits.DefaultTokenBudget = 4321;
            using var harness = new Harness(Script.Says("ok"), options: options);

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("assistant", "hi"), out var agent, out _));
            Assert.AreEqual(4321, agent.TokenBudget);

            Assert.IsTrue(harness.Registry.TryAdmit(
                new AgentSpawn("assistant", "hi") { TokenBudget = 99 }, out var thrifty, out _));
            Assert.AreEqual(99, thrifty.TokenBudget);
        }

        [TestMethod]
        public void TheDocumentedAllowlistKeyActuallyBinds()
        {
            // The one test that would have caught a real defect: the options shape was
            // Dictionary<String, List<String>>, which binds Agents:Roles:<role>:0, while every
            // document for this feature spells the key Agents:Roles:<role>:Tools. So an operator's
            // allowlist bound to NOTHING, no error was raised anywhere, and the role silently kept
            // its shipped default. Constructing the options object by hand cannot see that; only
            // binding from configuration can, which is why this goes through the real binder.
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<String, String>
                {
                    ["Agents:Roles:assistant:Tools:0"] = "f8_read",
                    ["Agents:Roles:assistant:Tools:1"] = "f8_overview",
                    ["Agents:Limits:MaxStepsPerRun"] = "7",
                })
                .Build();

            var options = configuration.GetSection(AgentsOptions.SectionName).Get<AgentsOptions>();

            Assert.IsNotNull(options);
            Assert.AreEqual(7, options.Limits.MaxStepsPerRun, "the section itself must bind");
            Assert.IsTrue(options.Roles.ContainsKey("assistant"),
                "Agents:Roles:<role> did not bind at all");
            CollectionAssert.AreEqual(new[] { "f8_read", "f8_overview" },
                options.Roles["assistant"].Tools.ToArray(),
                "Agents:Roles:<role>:Tools is the documented key and must be what binds");

            var catalog = RoleCatalog.Load(options);
            Assert.IsTrue(catalog.TryGet("assistant", out var role, out _));
            var kept = role.Filter(new List<AITool> { Tool("f8_read"), Tool("f8_write") });
            Assert.AreEqual(1, kept.Count, "the bound allowlist must actually narrow the tool list");
            Assert.AreEqual("f8_read", kept[0].Name);
        }

        [TestMethod]
        public void AnEmptyConfiguredAllowlistMeansEveryToolRatherThanNone()
        {
            // The distinction a role's own config has to preserve: a role entry with no Tools list
            // is "this role is mentioned", not "this role may use nothing". Reading it the other way
            // would silently disarm an agent whose configuration merely existed.
            //
            // The worker, whose SHIPPED allowlist is empty, so "keeps what it ships with" and
            // "sees everything" are the same answer here. They are not for the orchestrator, which
            // is the arm AnEmptyOrAllBlankAllowlistKeepsTheShippedOneAndAStarEntryWidensIt covers.
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<String, String>
                {
                    ["Agents:Roles:worker:Tools:0"] = "   ",
                })
                .Build();

            var options = configuration.GetSection(AgentsOptions.SectionName).Get<AgentsOptions>();
            var catalog = RoleCatalog.Load(options);

            Assert.IsTrue(catalog.TryGet("worker", out var worker, out _));
            Assert.AreEqual(2, worker.Filter(new List<AITool> { Tool("a"), Tool("b") }).Count,
                "a blank entry is not a tool name, so the list is empty and empty means all");
        }

        [TestMethod]
        public void TheParentsTraceRecordsTheChildItSpawned()
        {
            // The spawn step on the PARENT's trace, whose stated purpose is that a swarm is
            // readable from the orchestrator's own trace. Nothing gated it: childId appeared
            // nowhere in this project, and measured, deleting the block left every agent test
            // green. The phase that makes it load-bearing is the swarm, so it is pinned before then
            // rather than after.
            using var harness = new Harness(Script.Says("ok"));

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("assistant", "orchestrate"),
                out var boss, out _));
            Assert.IsTrue(harness.Registry.TryAdmit(
                new AgentSpawn("worker", "part one") { ParentId = boss.Id }, out var worker, out _));

            var onTheParent = boss.Trace.Steps().Where(s => s.Kind == "spawn").ToList();
            Assert.AreEqual(2, onTheParent.Count,
                "the parent's own spawn step, then one for the child it spawned");
            Assert.AreEqual(worker.Id, onTheParent[^1].ChildId,
                "the parent's trace has to name WHICH child, or a swarm reads as an orchestrator "
                + "that spawned something unidentified");

            // And the child's own first step is its own spawn, carrying no childId: the two uses of
            // the kind are distinguished by that field and by nothing else.
            var onTheChild = worker.Trace.Steps().Single(s => s.Kind == "spawn");
            Assert.IsNull(onTheChild.ChildId);
            Assert.AreEqual("pending", onTheChild.State);
        }

        [TestMethod]
        public async Task AnOutOfRangeRunCapIsClampedRatherThanThrowingPastEveryCatch()
        {
            // A recorded incident, not a hypothesis: CancelAfter refuses a delay past a timer's
            // maximum (about 49 days), so a MaxRunSeconds an operator meant as "no cap" threw where
            // no catch could turn it into an ending, and the agent sat at pending holding a
            // concurrency slot for the life of the process with nothing in the log. The clamp and
            // its warning were added for it and neither had a test: across the whole suite
            // MaxRunSeconds was only ever 1 or 300, both far below the armable maximum, so this
            // branch ran nowhere.
            using var sink = new TestLogSink();
            var options = new AgentsOptions();
            options.Limits.MaxRunSeconds = Int32.MaxValue;

            using var harness = new Harness(Script.Says("done"), options: options, sink: sink);

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("assistant", "count"),
                out var agent, out _));
            await harness.Run(agent);

            Assert.AreEqual(AgentState.Completed, agent.State,
                "the run must reach an ending rather than escaping as an unhandled argument error");
            Assert.IsTrue(sink.Contains(LogLevel.Warning, "MaxRunSeconds", "bounded at"),
                "silently ignoring a configured number is its own defect, so the clamp says what "
                + "it did");
        }

        [TestMethod]
        public void AFailureBeforeTheRunsOwnTryIsStillRecordedAsAnEnding()
        {
            // Rescue, the last resort, which had zero references anywhere. Its own doc says it
            // should never fire and that this is exactly why it exists: the alternative to a
            // recorded failure is a silent one, and a silent one costs a concurrency slot until the
            // process restarts. RunAsync catches everything inside its try, so what is pinned here
            // is the stretch BEFORE it, which is where the original incident threw.
            using var sink = new TestLogSink();
            var real = Options.Create(new AgentsOptions());

            using var feed = new AgentFeedDispatcher(real,
                TestLoggerFactory.Create().CreateLogger<AgentFeedDispatcher>());
            var journal = new AgentJournal(feed, real);
            using var registry = new AgentRegistry(real,
                TestLoggerFactory.Create().CreateLogger<AgentRegistry>(), journal);
            var roles = RoleCatalog.Load(real.Value);
            using var chat = new ScriptedChatClient(Script.Says("never reached"));

            // Only the RUNNER gets unreadable options, so the registry still admits normally and
            // the failure lands exactly where Rescue is documented to cover: before the try.
            var runner = new AgentRunner(registry, roles, new FixedToolSource(null), chat, journal,
                new UnreadableOptions(), sink.CreateFactory());

            Assert.IsTrue(registry.TryAdmit(new AgentSpawn("assistant", "count"), out var agent, out _));
            Assert.IsTrue(roles.TryGet(agent.Role, out var role, out _));

            runner.Start(agent, role, null);

            Assert.IsTrue(SpinWait.SpinUntil(() => AgentStates.IsTerminal(agent.State), 10_000),
                "the agent stayed at " + AgentStates.Wire(agent.State) + ", holding its slot with "
                + "nothing recorded, which is the incident this exists to prevent");
            Assert.AreEqual(AgentState.Failed, agent.State);
            StringAssert.Contains(agent.Failure, "the options could not be read",
                "the ending has to carry WHY, or an operator sees a failure with no cause");
            Assert.IsTrue(sink.Contains(LogLevel.Error, agent.Id, "before it could record an ending"));
        }

        [TestMethod]
        public async Task TheCitationCountReachesTheTraceAndTheEndingEvent()
        {
            // Phase 2's headline, and it was delivered by code that no test executed: every
            // citation test called GroundingCheck.Count directly, so the counts reaching a trace
            // step and an ending event ran nowhere. Deleting the journal's whole citation block
            // left the suite green.
            var tool = AIFunctionFactory.Create(() => "8",
                new AIFunctionFactoryOptions { Name = "count_vertices", Description = "Counts vertices." });

            using var harness = new Harness(
                Script.Calls("c1", "count_vertices")
                    .Then("There are 8 [t:count_vertices], and none deleted [t:delete_all]."),
                tools: new List<AITool> { tool });

            Assert.IsTrue(harness.Feed.TrySubscribe(AgentFeedFilter.All, out var subscription, out _));
            using (subscription)
            {
                Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("assistant", "count"),
                    out var agent, out _));
                await harness.Run(agent);

                Assert.AreEqual(AgentState.Completed, agent.State);

                var steps = agent.Trace.Steps();
                var check = steps.SingleOrDefault(s => s.Kind == "citationCheck");
                Assert.IsNotNull(check, "the citation check never reached the trace");
                Assert.AreEqual(1, check.ValidCitations,
                    "count_vertices was called, so citing it is grounded");
                Assert.AreEqual(1, check.DanglingCitations,
                    "delete_all was never called, so citing it dangles");

                // The ENDING is last, and the check is the step before it. Recording the check
                // afterwards made the tail of every checked run [stateChanged, citationCheck], so
                // the registry's claim that the ending is ordinarily the last step was false in
                // the ordinary case rather than the exceptional one.
                Assert.AreEqual("stateChanged", steps[^1].Kind,
                    "the last step of a completed run is how it ended");
                Assert.AreEqual("completed", steps[^1].State);
                Assert.AreEqual("citationCheck", steps[^2].Kind,
                    "the check is a statement about the final text, so it belongs before the ending");

                // And on the wire, where a subscriber reads it without fetching the trace.
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                AgentEvent ended = null;
                while (ended == null)
                {
                    var next = await subscription.ReadAsync(budget.Token);
                    Assert.IsNotNull(next, "the feed ended before the completion arrived");
                    if (next.Kind == "agentCompleted")
                    {
                        ended = next;
                    }
                }

                Assert.IsNotNull(ended.Citations, "the ending event carried no citation counts");
                Assert.AreEqual(1, ended.Citations.Valid);
                Assert.AreEqual(1, ended.Citations.Dangling);
            }
        }

        [TestMethod]
        public async Task ACancelledRunCarriesNoCitationCountsRatherThanZeroOfEach()
        {
            // The distinction the registry's doc promises and nothing checked: none is NOT zero.
            // Reporting zero valid and zero dangling on a run that was never checked is the same
            // shape as an answer that cited nothing, which is the fabrication shape.
            using var harness = new Harness(Script.Waits(TimeSpan.FromSeconds(30)));

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("assistant", "wait"),
                out var agent, out _));

            var running = harness.Run(agent);
            Assert.IsTrue(harness.Registry.TryCancel(agent.Id, out _));
            await running;

            Assert.AreEqual(AgentState.Cancelled, agent.State);
            Assert.IsFalse(agent.Trace.Steps().Any(s => s.Kind == "citationCheck"),
                "a cancelled run was given a citation check it never had");
        }

        [TestMethod]
        public void AJournaledTransitionCarriesTheStateItWasGivenNotWhateverTheRecordHolds()
        {
            // The record is shared, and the journal used to read the state back off it. So an
            // ending landing between setting a state and journaling it made the step report the
            // TERMINAL state: a live transition that said "changed to completed", and a spawn step
            // that said "cancelled" on an agent that had been alive. What is pinned here is that
            // the journal records the state it was TOLD, so the two cannot drift apart again.
            //
            // The ordering window itself is deliberately still open, and AgentRegistry.TryAdmit is
            // the one home for that and for the three reasons the journal calls stay outside its
            // lock. This comment claimed the window was closed by a lock, which was true for
            // exactly one commit before the move was reverted, and it was the last artifact in the
            // tree still saying so.
            using var harness = new Harness(Script.Says("done"));

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("assistant", "count"),
                out var agent, out _));

            // The record is put in its TERMINAL state first, standing in for the ending the racing
            // thread records. The journal is then told about a live transition, which is what a
            // thread already past the registry's lock would do.
            Assert.IsTrue(harness.Registry.Finish(agent.Id, AgentState.Completed, resultText: "done"));
            Assert.AreEqual(AgentState.Completed, agent.State);

            harness.Journal.StateChanged(agent, AgentState.Running);
            harness.Journal.Spawned(agent, AgentState.Pending);

            var steps = agent.Trace.Steps();
            var moved = steps.Last(s => s.Kind == "stateChanged");
            var spawn = steps.Last(s => s.Kind == "spawn");

            Assert.AreEqual("running", moved.State,
                "the step reported the record's state rather than the transition being journaled");
            Assert.AreEqual("pending", spawn.State,
                "the spawn step reported the record's state, so an agent that had been alive read "
                + "as one that never was");
        }

        #region swarm mode

        [TestMethod]
        public async Task AnOrchestratorGetsTheSwarmToolsAndNobodyElseDoes()
        {
            // The swarm tools are appended after the role's allowlist, because they are not MCP
            // tools. Only an orchestrator gets them: a worker with spawn_worker is how a swarm
            // becomes a tree nobody bounded, and an assistant with it would be an orchestrator
            // that was never told the one-composer rule.
            var graphTool = Tool("f8_overview");
            var options = new AgentsOptions();
            options.Limits.MaxConcurrentAgents = 0;

            using var harness = new Harness(Script.Says("done"), options: options,
                tools: new List<AITool> { graphTool, Tool("f8_write") });

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("orchestrator", "plan"),
                out var boss, out _));
            await harness.Run(boss);

            var offered = harness.Client.LastTools;
            CollectionAssert.Contains(offered.ToArray(), SwarmTools.SpawnWorker);
            CollectionAssert.Contains(offered.ToArray(), SwarmTools.AwaitWorkers);
            CollectionAssert.Contains(offered.ToArray(), "f8_overview");
            CollectionAssert.DoesNotContain(offered.ToArray(), "f8_write",
                "the orchestrator's allowlist still narrows the MCP tools; the swarm tools are "
                + "appended, not a way around it");

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("assistant", "count"),
                out var solo, out _));
            await harness.Run(solo);

            CollectionAssert.DoesNotContain(harness.Client.LastTools.ToArray(), SwarmTools.SpawnWorker);
            CollectionAssert.DoesNotContain(harness.Client.LastTools.ToArray(), SwarmTools.AwaitWorkers);
        }

        [TestMethod]
        public async Task AnOrchestratorSpawnsARealWorkerAndCollectsATypedResult()
        {
            // The whole phase in one test: the orchestrator's model calls spawn_worker, a real
            // worker agent is admitted and run, and await_workers returns its result as a TYPED
            // value rather than prose. Prose would have to be parsed by a model, which is where a
            // swarm starts inventing.
            var options = new AgentsOptions();
            options.Limits.MaxConcurrentAgents = 0;

            using var harness = new Harness(
                Script.CallsWith("c1", SwarmTools.SpawnWorker, new Dictionary<String, Object>
                    {
                        ["task"] = "count the vertices",
                        ["name"] = "counter",
                    })
                    .ThenCalls("c2", SwarmTools.AwaitWorkers)
                    .Then("Eight, per the worker. [t:await_workers]"),
                options: options,
                // SLOW on purpose. With an instant worker, an await that waited for nothing would
                // still find it finished, so the assertion below would pass on a broken await:
                // measured, removing the wait left this test green until the worker took time.
                workerScript: Script.SaysAfter("eight", TimeSpan.FromMilliseconds(750)));

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("orchestrator", "how many?"),
                out var boss, out _));
            await harness.Run(boss);

            Assert.AreEqual(AgentState.Completed, boss.State, boss.Failure);

            // The worker is a first-class agent: listed, with the orchestrator as its parent.
            var workers = harness.Registry.Children(boss.Id);
            Assert.AreEqual(1, workers.Count, "spawn_worker did not admit a worker");
            Assert.AreEqual("worker", workers[0].Role);
            Assert.AreEqual(boss.Id, workers[0].ParentId);
            Assert.AreEqual("count the vertices", workers[0].Task,
                "the task the model chose has to reach the worker, or delegation means nothing");
            Assert.AreEqual("counter", workers[0].Name);
            Assert.AreEqual(1, workers[0].Depth);

            // It ran on its own and reached an ending, which is what await_workers waited for.
            Assert.IsTrue(AgentStates.IsTerminal(workers[0].State),
                "await_workers returned while its worker was still " + workers[0].State);

            // And the orchestrator's own trace records both calls, so a reviewer sees the
            // delegation rather than inferring it from a listing.
            var calls = boss.Trace.Steps().Where(s => s.Kind == "toolCall").ToList();
            CollectionAssert.AreEquivalent(
                new[] { SwarmTools.SpawnWorker, SwarmTools.AwaitWorkers },
                calls.Select(c => c.Tool).ToArray());
            Assert.IsTrue(calls.All(c => c.Success == true), "a swarm call failed: "
                + String.Join("; ", calls.Select(c => c.Tool + ": " + c.Error)));
        }

        [TestMethod]
        public async Task AwaitWorkersReturnsTheResultShapeAnOrchestratorCanComposeFrom()
        {
            // The typed result is the contract: state, answer, citation counts and cost. The
            // counts come off the worker's own trace through AgentTrace.Citations, the one home,
            // so an orchestrator and the detail route cannot disagree about the same run.
            var options = new AgentsOptions();
            options.Limits.MaxConcurrentAgents = 0;

            using var harness = new Harness(
                Script.CallsWith("c1", SwarmTools.SpawnWorker, new Dictionary<String, Object>
                    { ["task"] = "count" })
                    .ThenCalls("c2", SwarmTools.AwaitWorkers)
                    .Then("done"),
                options: options,
                workerScript: Script.SaysAfter("eight", TimeSpan.FromMilliseconds(750)));

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("orchestrator", "how many?"),
                out var boss, out _));
            await harness.Run(boss);

            // The await's own tool-call step carries what came back, capped like any capture.
            var awaited = boss.Trace.Steps()
                .Single(s => s.Kind == "toolCall" && s.Tool == SwarmTools.AwaitWorkers);
            Assert.IsNotNull(awaited.Result, "the await returned nothing for the trace to record");

            var worker = harness.Registry.Children(boss.Id).Single();
            StringAssert.Contains(awaited.Result, worker.Id,
                "a result an orchestrator cannot attribute to a worker is prose: " + awaited.Result);
            StringAssert.Contains(awaited.Result, "state",
                "the shape is typed, so the state is a field: " + awaited.Result);
        }

        [TestMethod]
        public async Task ABreachedCapReachesTheModelAsAToolErrorRatherThanEndingTheRun()
        {
            // Spec 3.5: a breached cap is a tool error the orchestrator sees and a
            // toolCalled(success=false) event the user sees. Thrown instead, it would end the
            // orchestrator's turn, and the one reader that could act on it (delegate less, await
            // what it has) would never be told.
            var options = new AgentsOptions();
            options.Limits.MaxConcurrentAgents = 0;
            options.Limits.MaxWorkersPerOrchestrator = 1;

            using var harness = new Harness(
                Script.CallsWith("c1", SwarmTools.SpawnWorker, new Dictionary<String, Object>
                    { ["task"] = "one" })
                    .ThenCallsWith("c2", SwarmTools.SpawnWorker, new Dictionary<String, Object>
                        { ["task"] = "two" })
                    .Then("I could only delegate one part."),
                options: options,
                workerScript: Script.Says("one"));

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("orchestrator", "two parts"),
                out var boss, out _));
            await harness.Run(boss);

            Assert.AreEqual(AgentState.Completed, boss.State,
                "a breached cap ended the run instead of being handed to the model: " + boss.Failure);
            Assert.AreEqual(1, harness.Registry.Children(boss.Id).Count,
                "the cap admitted a second worker");

            // The refusal is IN the trace as a FAILED call: the second spawn did not happen, so
            // its step names the cap in error and carries no result. Journaled as a success, the
            // step, the toolCalled event and f8a.agents.tool.calls would all report a breached cap
            // as work that was done.
            var spawns = boss.Trace.Steps()
                .Where(s => s.Kind == "toolCall" && s.Tool == SwarmTools.SpawnWorker)
                .ToList();
            Assert.AreEqual(2, spawns.Count, "both spawn calls belong on the trace");
            Assert.IsTrue(spawns[0].Success == true,
                "the spawn that admitted a worker was not recorded as a success");
            Assert.IsTrue(spawns[1].Success == false,
                "the refused spawn was recorded as a call that worked");
            StringAssert.Contains(spawns[1].Error, "MaxWorkersPerOrchestrator",
                "the model was not told WHICH cap it hit: " + spawns[1].Error);
            Assert.IsNull(spawns[1].Result,
                "a refused call produced no result, so the reason belongs in error alone");
        }

        [TestMethod]
        public async Task CancellingAnOrchestratorCancelsTheWorkersItIsWaitingFor()
        {
            // Cascade cancel, through the swarm rather than through the registry's own API: the
            // orchestrator is waiting inside await_workers when the cancel arrives, so this also
            // pins that the await ends rather than holding a task nothing will complete.
            var options = new AgentsOptions();
            options.Limits.MaxConcurrentAgents = 0;

            using var harness = new Harness(
                Script.CallsWith("c1", SwarmTools.SpawnWorker, new Dictionary<String, Object>
                    { ["task"] = "slow part" })
                    .ThenCalls("c2", SwarmTools.AwaitWorkers)
                    .Then("never reached"),
                options: options,
                // The worker hangs, which is what leaves the orchestrator genuinely waiting inside
                // await_workers when the cancel arrives.
                workerScript: Script.Waits(TimeSpan.FromSeconds(30)));

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("orchestrator", "plan"),
                out var boss, out _));
            var running = harness.Run(boss);

            Assert.IsTrue(SpinWait.SpinUntil(() => harness.Registry.Children(boss.Id).Count == 1, 10_000),
                "the worker was never spawned");
            var worker = harness.Registry.Children(boss.Id).Single();

            Assert.IsTrue(harness.Registry.TryCancel(boss.Id, out var signalled));
            Assert.IsTrue(signalled >= 2,
                "a cancel has to reach the workers too; it signalled " + signalled);

            await running;

            Assert.AreEqual(AgentState.Cancelled, boss.State);
            Assert.IsTrue(AgentStates.IsTerminal(worker.State),
                "the worker outlived the orchestrator that was waiting for it, holding a "
                + "concurrency slot for a swarm nobody is composing");
        }

        /// <summary>
        ///   The scenario F01 says happens EVERY time, because a worker is admitted after its
        ///   orchestrator and therefore always has the later deadline: an orchestrator awaiting a
        ///   slow worker runs out of time first. Its ending has to stop the worker rather than leave
        ///   it running to its own deadline, and the failure text is what says which of the two
        ///   happened.
        ///   <para>
        ///     The only test in this file that puts a wall-clock cap on a swarm, so it also covers
        ///     the half of <c>await_workers</c> that no test reached: that the orchestrator's own
        ///     deadline ends the wait. The margins are ARRANGED rather than hoped for, and they are
        ///     load-bearing: a one second tool read inside a three second cap puts the spawn about a
        ///     second in, so the worker's own deadline lands a second AFTER the orchestrator's. That
        ///     gap is what makes "the cascade stopped it" distinguishable from "it ran out of time
        ///     by itself", which are different states carrying different text.
        ///   </para>
        /// </summary>
        [TestMethod]
        public async Task AnOrchestratorThatRunsOutOfTimeInsideAnAwaitStopsTheWorkerItWasWaitingFor()
        {
            var options = new AgentsOptions();
            options.Limits.MaxConcurrentAgents = 0;
            options.Limits.MaxRunSeconds = 3;

            // f8_overview because that is the orchestrator's whole shipped allowlist, and slow
            // because the delay before the spawn is what separates the two deadlines.
            var slowRead = AIFunctionFactory.Create(
                () =>
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(1000));
                    return "the graph has eight vertices";
                },
                new AIFunctionFactoryOptions
                {
                    Name = "f8_overview",
                    Description = "Describes the graph, slowly.",
                });

            using var harness = new Harness(
                Script.Calls("c1", "f8_overview")
                    .ThenCallsWith("c2", SwarmTools.SpawnWorker,
                        new Dictionary<String, Object> { ["task"] = "the slow part" })
                    .ThenCalls("c3", SwarmTools.AwaitWorkers)
                    .Then("never reached"),
                options: options,
                tools: new List<AITool> { slowRead },
                workerScript: Script.SaysAfter("eight", TimeSpan.FromSeconds(30)));

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("orchestrator", "how many?"),
                out var boss, out _));

            await harness.Run(boss);

            var workers = harness.Registry.Children(boss.Id);
            Assert.AreEqual(1, workers.Count,
                "the orchestrator never got to spawn its worker: " + boss.Failure);
            var worker = workers[0];

            Assert.AreEqual(AgentState.BudgetExceeded, boss.State, boss.Failure);
            Assert.AreEqual(BudgetKind.Time, boss.Budget,
                "the orchestrator's deadline has to apply inside await_workers");
            Assert.AreEqual(AgentState.Cancelled, worker.State,
                "the worker outlived the orchestrator that was awaiting it, holding a slot and "
                + "spending its budget for an answer nobody will compose");
            // This text is what proves the CASCADE recorded the ending: the worker unwinding on its
            // own would have written its own reason instead.
            StringAssert.Contains(worker.Failure, "Its orchestrator ended (budgetExceeded)",
                worker.Failure);
            Assert.IsNull(worker.ResultText,
                "the worker had not answered yet, which is what makes this the orphan case");
            Assert.AreEqual(0, harness.Registry.ActiveCount);
        }

        [TestMethod]
        public async Task AnOrchestratorsTokenBudgetBoundsItsOwnCallsAndNotItsWorkers()
        {
            // Spec 3.6: "Orchestrator budgets bound only their own calls; the global token cap is
            // MaxConcurrentAgents x DefaultTokenBudget by construction." So a worker gets its own
            // budget rather than drawing on its orchestrator's, which is also why
            // MaxWorkersPerOrchestrator has to count over a life: it is the only thing bounding
            // how much a swarm can spend in total.
            var options = new AgentsOptions();
            options.Limits.MaxConcurrentAgents = 0;
            options.Limits.DefaultTokenBudget = 5000;

            using var harness = new Harness(
                Script.CallsWith("c1", SwarmTools.SpawnWorker, new Dictionary<String, Object>
                    { ["task"] = "count" })
                    .ThenCalls("c2", SwarmTools.AwaitWorkers)
                    .Then("done"),
                options: options,
                workerScript: Script.Says("eight"));

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("orchestrator", "how many?"),
                out var boss, out _));
            await harness.Run(boss);

            var worker = harness.Registry.Children(boss.Id).Single();

            Assert.AreEqual(5000, worker.TokenBudget,
                "a worker has to get its own budget, or a swarm's cost would be capped by whatever "
                + "its orchestrator had left and a long orchestration would starve its own workers");
            Assert.AreEqual(5000, boss.TokenBudget);

            // And the two meters are separate: what the worker spent is not charged to the
            // orchestrator, so neither can exhaust the other.
            Assert.IsTrue(Interlocked.Read(ref worker.InputTokens) > 0,
                "the worker made a model call, so it spent something");
            Assert.AreNotSame(boss, worker);
            Assert.IsTrue(
                Interlocked.Read(ref boss.InputTokens) + Interlocked.Read(ref boss.OutputTokens)
                    < boss.TokenBudget,
                "the orchestrator was charged for its worker's calls");
        }

        [TestMethod]
        public async Task TheConcurrencyCapIsSeenByAnOrchestratorAsAToolErrorToo()
        {
            // The host-wide cap, reached through a tool call rather than the spawn route. It is the
            // same refusal either way, which is the point of enforcing it in the registry: the
            // swarm has no second opinion about how many agents may run.
            var options = new AgentsOptions();
            options.Limits.MaxConcurrentAgents = 1;

            using var harness = new Harness(
                Script.CallsWith("c1", SwarmTools.SpawnWorker, new Dictionary<String, Object>
                    { ["task"] = "one" })
                    .Then("I could not delegate."),
                options: options,
                workerScript: Script.Says("never started"));

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("orchestrator", "plan"),
                out var boss, out _));
            await harness.Run(boss);

            Assert.AreEqual(0, harness.Registry.Children(boss.Id).Count,
                "the orchestrator itself is the one live agent, so no worker had room");

            var call = boss.Trace.Steps()
                .Single(s => s.Kind == "toolCall" && s.Tool == SwarmTools.SpawnWorker);
            Assert.IsTrue(call.Success == false,
                "the refused spawn was recorded as a call that worked");
            StringAssert.Contains(call.Error, "MaxConcurrentAgents",
                "the model has to be told which cap it hit to act on it: " + call.Error);
            Assert.IsNull(call.Result, "a refused call produced no result");
            Assert.AreEqual(AgentState.Completed, boss.State,
                "a full host ended the orchestrator instead of letting it say so");

            // And the framework saw a RESULT rather than an error, which is what keeps the turn
            // going. The ending above cannot tell the two apart on its own, because one thrown call
            // is still inside the framework's consecutive-error cap. Asserted on this test rather
            // than the per-orchestrator one because no worker is ever admitted here, so the
            // orchestrator's are the only calls the scripted client sees.
            var handed = harness.Client.LastMessages
                .SelectMany(m => m.Contents)
                .OfType<FunctionResultContent>()
                .Single(r => r.CallId == "c1");
            Assert.IsNull(handed.Exception,
                "the refusal reached the framework as an error, which is what ends a turn");
            StringAssert.Contains(handed.Result?.ToString(), "MaxConcurrentAgents",
                "the refusal never reached the model, so it cannot act on the cap");
        }

        /// <summary>
        ///   Four refusals in a row, one more than the framework's
        ///   <c>MaximumConsecutiveErrorsPerRequest</c> allows and the default this host leaves it
        ///   at. A refusal that arrived as a thrown exception would be rethrown past the tool loop
        ///   on the fourth and end the run as failed; it arrives as a value instead, so none of
        ///   them is a framework error and the orchestrator still gets to say it could not
        ///   delegate. This is the property the contract choice turns on, and no test reached more
        ///   than one breach.
        /// </summary>
        [TestMethod]
        public async Task RefusingEveryDelegationInARowStillLetsTheOrchestratorAnswer()
        {
            var options = new AgentsOptions();
            options.Limits.MaxConcurrentAgents = 1;

            var arguments = new Dictionary<String, Object> { ["task"] = "one part" };
            using var harness = new Harness(
                Script.CallsWith("c1", SwarmTools.SpawnWorker, arguments)
                    .ThenCallsWith("c2", SwarmTools.SpawnWorker, arguments)
                    .ThenCallsWith("c3", SwarmTools.SpawnWorker, arguments)
                    .ThenCallsWith("c4", SwarmTools.SpawnWorker, arguments)
                    .Then("This host is full, so I did all of it myself."),
                options: options);

            Assert.IsTrue(harness.Registry.TryAdmit(new AgentSpawn("orchestrator", "four parts"),
                out var boss, out _));
            await harness.Run(boss);

            Assert.AreEqual(AgentState.Completed, boss.State,
                "consecutive cap breaches ended the run: " + boss.Failure);
            Assert.AreEqual(0, harness.Registry.Children(boss.Id).Count,
                "the orchestrator itself is the one live agent, so no worker had room");

            var spawns = boss.Trace.Steps()
                .Where(s => s.Kind == "toolCall" && s.Tool == SwarmTools.SpawnWorker)
                .ToList();
            Assert.AreEqual(4, spawns.Count, "every refused call belongs on the trace");
            Assert.IsTrue(spawns.All(s => s.Success == false),
                "a refused spawn was recorded as a call that worked");
        }

        #endregion

        /// <summary>
        ///   The three arms of <c>Agents:Roles:&lt;role&gt;:Tools</c>, on the role where they
        ///   differ: an empty list keeps the SHIPPED allowlist (not everything, which three sites
        ///   claimed), a list of blanks keeps it too rather than silently lifting it, and a lone
        ///   star widens to everything the server advertises.
        /// </summary>
        [TestMethod]
        public void AnEmptyOrAllBlankAllowlistKeepsTheShippedOneAndAStarEntryWidensIt()
        {
            var advertised = new List<AITool> { Tool("f8_overview"), Tool("f8_read"), Tool("f8_write") };

            var empty = new AgentsOptions();
            empty.Roles["orchestrator"] = Allow();
            Assert.IsTrue(RoleCatalog.Load(empty).TryGet("orchestrator", out var kept, out _));
            CollectionAssert.AreEqual(new[] { "f8_overview" }, kept.AllowedTools.ToArray(),
                "an empty configured list keeps what the role ships with");
            Assert.AreEqual(1, kept.Filter(advertised).Count);

            var blank = new AgentsOptions();
            blank.Roles["orchestrator"] = Allow(" ", "");
            Assert.IsTrue(RoleCatalog.Load(blank).TryGet("orchestrator", out var stillKept, out _));
            CollectionAssert.AreEqual(new[] { "f8_overview" }, stillKept.AllowedTools.ToArray(),
                "a list of blanks is a typo, not an instruction to lift the allowlist");
            Assert.AreEqual(1, stillKept.Filter(advertised).Count);

            var star = new AgentsOptions();
            star.Roles["orchestrator"] = Allow(RoleCatalog.EveryTool);
            Assert.IsTrue(RoleCatalog.Load(star).TryGet("orchestrator", out var widened, out _));
            Assert.AreEqual(0, widened.AllowedTools.Count,
                "no narrowing left, which is how every-tool is represented downstream");
            Assert.AreEqual(3, widened.Filter(advertised).Count,
                "a star means every tool the server advertises, whatever the role ships with");
        }

        /// <summary>
        ///   A star beside a tool name is refused at load, naming the key: the two are opposite
        ///   instructions, and guessing which one an operator meant would either silently widen an
        ///   allowlist or silently discard what they wrote.
        /// </summary>
        [TestMethod]
        public void AStarBesideAToolNameIsRefusedBecauseTheTwoAreOppositeInstructions()
        {
            var options = new AgentsOptions();
            options.Roles["worker"] = Allow(RoleCatalog.EveryTool, "f8_read");

            var thrown = Assert.ThrowsException<InvalidOperationException>(
                () => RoleCatalog.Load(options));
            StringAssert.Contains(thrown.Message, "Agents:Roles:worker:Tools",
                "the message has to name the key an operator edits: " + thrown.Message);
            StringAssert.Contains(thrown.Message, RoleCatalog.EveryTool);

            // The refusal is about the COMBINATION: the narrowing half alone loads.
            var narrowed = new AgentsOptions();
            narrowed.Roles["worker"] = Allow("f8_read");
            Assert.IsTrue(RoleCatalog.Load(narrowed).TryGet("worker", out var role, out _));
            CollectionAssert.AreEqual(new[] { "f8_read" }, role.AllowedTools.ToArray());
        }

        private static AgentsOptions.RoleOptions Allow(params String[] tools)
        {
            return new AgentsOptions.RoleOptions { Tools = tools.ToList() };
        }

        private static AITool Tool(String name)
        {
            return AIFunctionFactory.Create(() => "ok",
                new AIFunctionFactoryOptions { Name = name, Description = name + " does something." });
        }

        /// <summary>
        ///   A registry, a role catalogue and a runner over a scripted chat client. Everything a
        ///   runner test needs and no model.
        /// </summary>
        private sealed class Harness : IDisposable
        {
            private readonly RoleCatalog _roles;

            public Harness(Script script, AgentsOptions options = null, IReadOnlyList<AITool> tools = null,
                TimeProvider clock = null, TestLogSink sink = null, Script workerScript = null)
            {
                var resolved = options ?? new AgentsOptions();
                var wrapped = Options.Create(resolved);
                var loggers = sink == null ? TestLoggerFactory.Create() : sink.CreateFactory();

                Client = new ScriptedChatClient(script, workerScript);
                _roles = RoleCatalog.Load(resolved);

                // The real feed and the real journal, not fakes: the trace and the events are part
                // of what a run DOES, so a harness that stubbed them would leave every assertion
                // about them meaningless.
                Feed = new AgentFeedDispatcher(wrapped,
                    TestLoggerFactory.Create().CreateLogger<AgentFeedDispatcher>());
                Journal = new AgentJournal(Feed, wrapped, clock);

                Registry = new AgentRegistry(wrapped,
                    TestLoggerFactory.Create().CreateLogger<AgentRegistry>(), Journal, clock);
                Runner = new AgentRunner(Registry, _roles, new FixedToolSource(tools), Client, Journal,
                    wrapped, loggers);
            }

            public AgentFeedDispatcher Feed
            {
                get;
            }

            public AgentJournal Journal
            {
                get;
            }

            public AgentRegistry Registry
            {
                get;
            }

            public AgentRunner Runner
            {
                get;
            }

            public ScriptedChatClient Client
            {
                get;
            }

            public Task Run(AgentRecord agent)
            {
                Assert.IsTrue(_roles.TryGet(agent.Role, out var role, out _));
                return Runner.RunAsync(agent, role, null);
            }

            /// <summary>The usage/stat figures the scripted usage reports; the two scripts share
            /// them, because a swarm's cost is the whole tree's.</summary>
            public RoleCatalog Roles => _roles;

            public void Dispose()
            {
                Registry.Dispose();
                Feed.Dispose();
                Client.Dispose();
            }
        }

        /// <summary>
        ///   Options whose value cannot be read, standing in for any failure in the stretch of a run
        ///   BEFORE its own try block. The shipped code reads the limits there, which is where the
        ///   recorded incident threw.
        /// </summary>
        private sealed class UnreadableOptions : IOptions<AgentsOptions>
        {
            public AgentsOptions Value
                => throw new InvalidOperationException("the options could not be read");
        }

        private sealed class FixedToolSource : IAgentToolSource
        {
            public FixedToolSource(IReadOnlyList<AITool> tools)
            {
                Tools = tools ?? Array.Empty<AITool>();
            }

            public IReadOnlyList<AITool> Tools
            {
                get;
            }

            public Boolean Connected => true;

            public String Failure => null;
        }

        /// <summary>
        ///   What the scripted client answers. Built by the static factories so a test reads as one
        ///   line saying what the model does, rather than as a queue somebody has to decode.
        /// </summary>
        private sealed class Script
        {
            private Script()
            {
            }

            public List<Func<Int32, ScriptedTurn>> Turns { get; } = new List<Func<Int32, ScriptedTurn>>();

            public Func<Int32, ScriptedTurn> Repeating
            {
                get; private set;
            }

            public Int64? PromptTokens { get; private set; } = 10;

            public Int64? CompletionTokens { get; private set; } = 5;

            public static Script Says(String text)
            {
                var script = new Script();
                script.Turns.Add(_ => ScriptedTurn.Text(text));
                return script;
            }

            public static Script Calls(String id, String name)
            {
                var script = new Script();
                script.Turns.Add(_ => ScriptedTurn.Call(id, name));
                return script;
            }

            /// <summary>One call carrying arguments, for a tool that requires them.</summary>
            public static Script CallsWith(String id, String name,
                IReadOnlyDictionary<String, Object> arguments)
            {
                var script = new Script();
                script.Turns.Add(_ => ScriptedTurn.CallWith(id, name, arguments));
                return script;
            }

            /// <summary>Appends another call, carrying arguments.</summary>
            public Script ThenCallsWith(String id, String name,
                IReadOnlyDictionary<String, Object> arguments)
            {
                Turns.Add(_ => ScriptedTurn.CallWith(id, name, arguments));
                return this;
            }

            /// <summary>Appends a call with no arguments.</summary>
            public Script ThenCalls(String id, String name)
            {
                Turns.Add(_ => ScriptedTurn.Call(id, name));
                return this;
            }

            /// <summary>A model that never stops asking for a tool. Only a cap can end it, which is
            /// the whole point of the caps.</summary>
            public static Script AlwaysCalls(String name)
            {
                var script = new Script();
                script.Repeating = i => ScriptedTurn.Call("call-" + i, name);
                return script;
            }

            /// <summary>A model that asks for several tools in ONE response, which is what makes the
            /// tool-call cap inexact.</summary>
            public static Script AlwaysCallsMany(String name, Int32 howMany)
            {
                var script = new Script();
                script.Repeating = i => ScriptedTurn.Calls(
                    Enumerable.Range(0, howMany).Select(n => ("call-" + i + "-" + n, name)).ToList());
                return script;
            }

            /// <summary>One answer, delivered after a delay: a worker slow enough that awaiting it
            /// is observably different from not awaiting it.</summary>
            public static Script SaysAfter(String text, TimeSpan how)
            {
                var script = new Script();
                script.Turns.Add(_ => ScriptedTurn.SlowText(text, how));
                return script;
            }

            public static Script Waits(TimeSpan how)
            {
                var script = new Script();
                script.Repeating = _ => ScriptedTurn.Wait(how);
                return script;
            }

            public static Script Throws(String message)
            {
                var script = new Script();
                script.Repeating = _ => ScriptedTurn.Failure(message);
                return script;
            }

            public Script Then(String text)
            {
                Turns.Add(_ => ScriptedTurn.Text(text));
                return this;
            }

            public Script WithUsage(Int64 prompt, Int64 completion)
            {
                PromptTokens = prompt;
                CompletionTokens = completion;
                return this;
            }

            public Script WithoutUsage()
            {
                PromptTokens = null;
                CompletionTokens = null;
                return this;
            }
        }

        private sealed class ScriptedTurn
        {
            public String Content
            {
                get; private set;
            }

            public String CallId
            {
                get; private set;
            }

            public String CallName
            {
                get; private set;
            }

            /// <summary>What the scripted call passes, or null for none.</summary>
            public IReadOnlyDictionary<String, Object> CallArguments
            {
                get; private set;
            }

            /// <summary>Several calls in one response, for the overshoot the tool-call cap has.</summary>
            public IReadOnlyList<(String Id, String Name)> Batch
            {
                get; private set;
            }

            public TimeSpan? Delay
            {
                get; private set;
            }

            public String Error
            {
                get; private set;
            }

            public static ScriptedTurn Text(String text) => new ScriptedTurn { Content = text };

            /// <summary>
            ///   A call with ARGUMENTS, which a tool with a required parameter needs: the swarm's
            ///   spawn_worker takes a task, and a call with an empty argument bag would be refused
            ///   by the schema rather than reaching the tool.
            /// </summary>
            public static ScriptedTurn CallWith(String id, String name,
                IReadOnlyDictionary<String, Object> arguments)
            {
                var turn = Call(id, name);
                turn.CallArguments = arguments;
                return turn;
            }

            public static ScriptedTurn Call(String id, String name)
                => new ScriptedTurn { CallId = id, CallName = name };

            public static ScriptedTurn Calls(IReadOnlyList<(String Id, String Name)> calls)
                => new ScriptedTurn { Batch = calls };

            public static ScriptedTurn Wait(TimeSpan how) => new ScriptedTurn { Delay = how };

            /// <summary>An answer that takes time to arrive. Wait() answers with NOTHING, which the
            /// runner records as a run that did not answer, so it cannot stand in for a slow
            /// worker.</summary>
            public static ScriptedTurn SlowText(String text, TimeSpan how)
                => new ScriptedTurn { Content = text, Delay = how };

            public static ScriptedTurn Failure(String message) => new ScriptedTurn { Error = message };
        }

        /// <summary>
        ///   The seam the spec requires: a deterministic <see cref="IChatClient" /> so lifecycle,
        ///   budgets, allowlists and cancellation are all assertable without a model. It also records
        ///   what it was OFFERED, which is the only place a role allowlist is observable.
        /// </summary>
        private sealed class ScriptedChatClient : IChatClient
        {
            private readonly Script _script;
            private readonly TaskCompletionSource _firstCall =
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            private Int32 _calls;

            private readonly Script _workerScript;
            private Int32 _workerCalls;

            /// <param name="script">What every agent answers with, unless it is a worker and
            /// <paramref name="workerScript" /> is given.</param>
            /// <param name="workerScript">
            ///   What a WORKER answers with, on its own call counter.
            ///   <para>
            ///     A swarm needs this. One script shared by an orchestrator and its workers is
            ///     indexed by a single counter, so which turn a given agent gets depends on the
            ///     order the two happened to call in: the worker starts the moment it is spawned,
            ///     so "the orchestrator's second turn" and "the worker's first" race for index 1.
            ///     A first version of the swarm tests was written that way and was racy rather
            ///     than wrong-looking, which is worse.
            ///   </para>
            /// </param>
            public ScriptedChatClient(Script script, Script workerScript = null)
            {
                _script = script;
                _workerScript = workerScript;
            }

            /// <summary>
            ///   Whether this call is a worker's, decided from the role prompt the framework put on
            ///   ChatOptions.Instructions. The prompts are the host's own embedded resources, so
            ///   the marker is stable, and it is the only thing on a chat call that says which
            ///   agent made it: the adapter deliberately sends no agent id.
            /// </summary>
            private static Boolean IsWorker(ChatOptions options)
            {
                return options?.Instructions != null
                    && options.Instructions.Contains("Fallen-8 graph worker", StringComparison.Ordinal);
            }

            /// <summary>Completes when the first model call has been entered, so a test can cancel a
            /// run that is genuinely in flight rather than racing its start.</summary>
            public Task FirstCall => _firstCall.Task;

            public IReadOnlyList<String> LastTools { get; private set; } = Array.Empty<String>();

            /// <summary>The messages as the framework handed them over, so a test can assert what is
            /// NOT in them.</summary>
            public IReadOnlyList<ChatMessage> LastMessages { get; private set; } = Array.Empty<ChatMessage>();

            public String LastInstructions
            {
                get; private set;
            }

            public Int32 Calls => Volatile.Read(ref _calls);

            public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
                ChatOptions options = null, CancellationToken cancellationToken = default)
            {
                var worker = _workerScript != null && IsWorker(options);
                var script = worker ? _workerScript : _script;
                var index = worker
                    ? Interlocked.Increment(ref _workerCalls) - 1
                    : Interlocked.Increment(ref _calls) - 1;
                LastTools = options?.Tools?.Select(t => t.Name).ToList() ?? (IReadOnlyList<String>)Array.Empty<String>();
                LastMessages = messages.ToList();
                LastInstructions = options?.Instructions;
                _firstCall.TrySetResult();

                var turn = index < script.Turns.Count
                    ? script.Turns[index](index)
                    : script.Repeating?.Invoke(index) ?? ScriptedTurn.Text("done");

                if (turn.Error != null)
                {
                    throw new InvalidOperationException(turn.Error);
                }

                if (turn.Delay != null)
                {
                    await Task.Delay(turn.Delay.Value, cancellationToken).ConfigureAwait(false);
                }

                var contents = new List<AIContent>();
                if (turn.Batch != null)
                {
                    foreach (var call in turn.Batch)
                    {
                        contents.Add(new FunctionCallContent(call.Id, call.Name,
                            new Dictionary<String, Object>(StringComparer.Ordinal)));
                    }
                }
                else if (turn.CallName != null)
                {
                    contents.Add(new FunctionCallContent(turn.CallId, turn.CallName,
                        turn.CallArguments == null
                            ? new Dictionary<String, Object>(StringComparer.Ordinal)
                            : new Dictionary<String, Object>(turn.CallArguments, StringComparer.Ordinal)));
                }
                else
                {
                    contents.Add(new TextContent(turn.Content ?? String.Empty));
                }

                return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents))
                {
                    ModelId = "scripted-agent-model",
                    Usage = _script.PromptTokens == null && _script.CompletionTokens == null
                        ? null
                        : new UsageDetails
                        {
                            InputTokenCount = _script.PromptTokens,
                            OutputTokenCount = _script.CompletionTokens,
                        },
                };
            }

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
                foreach (var update in response.ToChatResponseUpdates())
                {
                    yield return update;
                }
            }

            public Object GetService(Type serviceType, Object serviceKey = null)
                => serviceType?.IsInstanceOfType(this) == true ? this : null;

            public void Dispose()
            {
            }
        }

        /// <summary>
        ///   A clock a test moves by hand. Retention is measured in minutes, and a test that slept
        ///   for them would be a test nobody runs.
        /// </summary>
        private sealed class StepClock : TimeProvider
        {
            private DateTimeOffset _now;

            public StepClock(DateTimeOffset start)
            {
                _now = start;
            }

            public override DateTimeOffset GetUtcNow() => _now;

            public void Advance(TimeSpan by)
            {
                _now = _now.Add(by);
            }
        }
    }
}
