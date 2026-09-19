// MIT License
//
// AgentRunner.cs
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
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.Agents.Diagnostics;
using NoSQL.GraphDB.Agents.Configuration;

namespace NoSQL.GraphDB.Agents.Runtime
{
    /// <summary>
    ///   Runs one admitted agent to an ending.
    ///
    ///   <para>
    ///     <b>There is no agentic loop here, on purpose.</b> Microsoft Agent Framework owns the
    ///     loop, the tool invocation and the multi-turn bookkeeping; this type builds the pipeline,
    ///     starts it, and translates however it stopped into one of the registry's endings. That is
    ///     the whole reason the framework is a dependency: a hand-rolled loop is the part of an
    ///     agent host that looks easy and then quietly gets tool-result ordering wrong.
    ///   </para>
    ///   <para>
    ///     <b>The pipeline is built per agent, and its order is load-bearing:</b>
    ///     the shared chat adapter at the bottom, this agent's meter above it, and the framework's
    ///     tool-invoking client on top. So the meter sees every model call the loop makes, including
    ///     the ones the loop makes on its own after a tool returns, which is exactly what a
    ///     per-run cap has to count. Putting the meter on top would count one call per user message
    ///     and miss the loop entirely.
    ///   </para>
    ///   <para>
    ///     <b>Nothing here names a model.</b> The adapter asks the instance for
    ///     <c>purpose: agent</c> and the instance decides; which model actually served a step is
    ///     read back off the response.
    ///   </para>
    /// </summary>
    public sealed class AgentRunner
    {
        private readonly AgentRegistry _registry;
        private readonly RoleCatalog _roles;
        private readonly IAgentToolSource _toolset;
        private readonly IChatClient _chat;
        private readonly AgentJournal _journal;
        private readonly IOptions<AgentsOptions> _options;
        private readonly ILoggerFactory _loggers;
        private readonly ILogger<AgentRunner> _logger;

        public AgentRunner(AgentRegistry registry, RoleCatalog roles, IAgentToolSource toolset,
            IChatClient chat, AgentJournal journal, IOptions<AgentsOptions> options,
            ILoggerFactory loggers)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _roles = roles ?? throw new ArgumentNullException(nameof(roles));
            _toolset = toolset ?? throw new ArgumentNullException(nameof(toolset));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _journal = journal ?? throw new ArgumentNullException(nameof(journal));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _loggers = loggers ?? throw new ArgumentNullException(nameof(loggers));
            _logger = loggers.CreateLogger<AgentRunner>();
        }

        /// <summary>
        ///   Starts the agent and returns immediately. The control plane never blocks on inference:
        ///   one step against a remote provider was measured between 0.3 and 41 seconds, so a spawn
        ///   that waited for the answer would be a request that looks hung.
        /// </summary>
        public void Start(AgentRecord agent, AgentRole role, String? systemPromptAppendix)
        {
            if (agent == null)
            {
                throw new ArgumentNullException(nameof(agent));
            }

            if (role == null)
            {
                throw new ArgumentNullException(nameof(role));
            }

            // Long-running rather than a pool thread: the run is dominated by awaits, but its first
            // synchronous stretch builds a pipeline and would otherwise sit behind whatever the pool
            // is doing at the moment a spawn arrives.
            //
            // Unwrap() then a continuation, because StartNew over an async lambda returns a
            // Task<Task> whose INNER task carries the failure. Discarding it swallowed the fault:
            // one out-of-range cap left the agent at pending with its slot held for the life of the
            // process and nothing in the log, which reads as an agent that is thinking. RunAsync
            // records its own endings, so this only has to catch what escaped even that.
            _ = Task.Factory.StartNew(() => RunAsync(agent, role, systemPromptAppendix),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default)
                .Unwrap()
                .ContinueWith(finished => Rescue(agent, finished.Exception),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
        }

        /// <summary>
        ///   The last resort: an ending for a run whose failure escaped <see cref="RunAsync" />
        ///   itself. It should never fire, and that is exactly why it exists rather than being
        ///   trusted: the alternative to a recorded failure here is a silent one, and a silent one
        ///   costs a concurrency slot until the process restarts.
        /// </summary>
        private void Rescue(AgentRecord agent, AggregateException? failure)
        {
            var reason = failure?.GetBaseException().Message ?? "The run ended without recording why.";
            if (_registry.Finish(agent.Id, AgentState.Failed, failure: reason))
            {
                _logger.LogError(failure,
                    "Agent {AgentId} failed before it could record an ending: {Reason}", agent.Id, reason);
            }
        }

        /// <summary>
        ///   The run, from pending to an ending. Awaitable so a test does not have to poll, and so
        ///   the gated live smoke test reads as one call.
        /// </summary>
        public async Task RunAsync(AgentRecord agent, AgentRole role, String? systemPromptAppendix)
        {
            if (agent == null)
            {
                throw new ArgumentNullException(nameof(agent));
            }

            if (role == null)
            {
                throw new ArgumentNullException(nameof(role));
            }

            var limits = _options.Value.Limits;

            // Wall clock is a SEPARATE source from the agent's own cancellation, and that is what
            // makes "it ran out of time" distinguishable from "somebody cancelled it". Measured
            // here rather than taken from the backend's reported durations, because those do not
            // cover a remote provider's routing and verification passes: one measured step reported
            // 45 ms for a call that took 41 seconds.
            //
            // Both sources are built INSIDE the try, and that placement is the fix for a real
            // defect rather than tidiness: CancelAfter refuses a delay past Timer.MaxSupportedTimeout
            // (about 49 days), so a MaxRunSeconds an operator meant as "no cap" threw here, before
            // any catch could turn it into an ending. Reading agent.Cancellation can throw too, on
            // a record evicted while its run was starting.
            using var deadline = new CancellationTokenSource();
            CancellationTokenSource? linked = null;

            try
            {
                if (limits.MaxRunSeconds > 0)
                {
                    // Clamped rather than refused, because the value is a CAP and a cap that is too
                    // large to arm is the same intent as no cap at all. The message says what was
                    // done, since silently ignoring a configured number is its own defect.
                    var seconds = TimeSpan.FromSeconds(limits.MaxRunSeconds);
                    if (seconds > MaxArmableDeadline)
                    {
                        _logger.LogWarning(
                            "Agents:Limits:MaxRunSeconds is {Configured}, which is longer than a timer "
                            + "can be armed for; this run is bounded at {Applied} seconds instead.",
                            limits.MaxRunSeconds, (Int64)MaxArmableDeadline.TotalSeconds);
                        seconds = MaxArmableDeadline;
                    }

                    deadline.CancelAfter(seconds);
                }

                linked = CancellationTokenSource.CreateLinkedTokenSource(
                    agent.Cancellation, deadline.Token);

                _registry.Advance(agent.Id, AgentState.Running);

                var tools = Tools(agent, role);
                var instructions = Instructions(role, systemPromptAppendix);

                var pipeline = new ChatClientBuilder(
                        new AgentBudgetChatClient(_chat, agent, limits, _journal))
                    .UseFunctionInvocation(_loggers, invoking =>
                    {
                        // The framework's own iteration cap, handed the host's step cap so the two
                        // cannot disagree. The meter below it is still the enforcer, because this
                        // one bounds a request and the budget bounds a RUN, which is every request
                        // the loop makes plus every user message after it.
                        if (limits.MaxStepsPerRun > 0)
                        {
                            invoking.MaximumIterationsPerRequest = limits.MaxStepsPerRun;
                        }

                        // A model that names a tool that does not EXIST ends the turn rather than
                        // being handed an error and asked again: a name nothing serves will not
                        // become a name something serves, so the retry only spends budget.
                        //
                        // It does not cover the failure this feature actually measured, which is a
                        // real tool name carrying arguments that do not match its schema. Those ARE
                        // retried, by design, because the model can correct them; the runner's
                        // no-answer ending is what stops that from looping forever.
                        invoking.TerminateOnUnknownCalls = true;

                        // The error text reaches the model, which is the only reader that can act
                        // on it: a schema complaint is what lets it correct the call. It also
                        // reaches the trace, on the failed tool-call step, so a reviewer sees the
                        // same complaint the model was given.
                        invoking.IncludeDetailedErrors = true;

                        // The tool-invocation seam, which is the ONLY place a tool call is
                        // observable: the meter below sees a model asking for one, and the framework
                        // does the invoking, so what actually went in and came back is visible
                        // nowhere else. Replacing the invoker means performing the call here, which
                        // is what the seam is for.
                        invoking.FunctionInvoker = (context, token) => Invoke(agent, context, token);
                    })
                    .Build();

                var framework = new ChatClientAgent(pipeline, new ChatClientAgentOptions
                {
                    Id = agent.Id,
                    Name = agent.Name,
                    Description = String.Format("A Fallen-8 {0} agent.", role.Name),
                    ChatOptions = new ChatOptions
                    {
                        Instructions = instructions,
                        Tools = tools.Count == 0 ? null : System.Linq.Enumerable.ToList(tools),
                        // Zero, because this host's job is to read a graph and report what is
                        // there. Phase 0 measured prompt shape, not temperature, deciding whether a
                        // tool call parsed at all, so there is nothing to buy here by sampling.
                        Temperature = 0,
                    },
                    // The pipeline above is complete, including the tool-invoking client, so the
                    // framework must not wrap another one around it: two tool loops over one
                    // request would each invoke every call.
                    UseProvidedChatClientAsIs = true,
                }, _loggers);

                // The framework's own GenAI telemetry: invoke_agent, the chat span below the tool
                // loop, and execute_tool per call, emitted on OUR source name so one registration
                // in the exporter covers all three (spec 3.6).
                //
                // EnableSensitiveData stays FALSE, and it is set rather than left to a default
                // because the whole tag-hygiene rule depends on it: with it on, the library writes
                // message content, tool arguments and tool results into telemetry, which is
                // exactly what this feature refuses to put in a monitoring backend. A task
                // sentence is a caller's text.
                // DISPOSED, because it owns an OpenTelemetry chat client of its own and this is
                // built per run: one undisposed meter per agent is a leak that grows with use,
                // which is worse than no telemetry. Disposal chains down through the pipeline and
                // stops at AgentBudgetChatClient, whose Dispose deliberately does not touch the
                // chat client every agent on this host shares.
                using var observed = new OpenTelemetryAgent(framework, AgentsMetrics.SourceName)
                {
                    EnableSensitiveData = false,
                };

                var session = await observed.CreateSessionAsync(cancellationToken: linked.Token)
                    .ConfigureAwait(false);

                var response = await observed.RunAsync(agent.Task, session, options: null, linked.Token)
                    .ConfigureAwait(false);

                // An agent that stopped without saying anything has not answered, so it has not
                // completed. Recording it as completed with empty text is the one outcome a
                // reviewer cannot tell from a successful run that happened to be terse, and it is
                // reachable: the framework ends a turn on a tool name it does not know, and a
                // model that only ever asks for tools runs the iteration cap out. Measured against
                // the live service, both produce exactly this.
                if (String.IsNullOrWhiteSpace(response.Text))
                {
                    _registry.Finish(agent.Id, AgentState.Failed,
                        failure: "The model stopped without producing an answer after "
                            + Interlocked.Read(ref agent.Steps).ToString(CultureInfo.InvariantCulture)
                            + " model calls. Its last turn carried no text.");
                    _logger.LogWarning(
                        "Agent {AgentId} ({Role}) stopped with no answer after {Steps} steps and "
                        + "{ToolCalls} tool calls.",
                        agent.Id, role.Name, Interlocked.Read(ref agent.Steps),
                        Interlocked.Read(ref agent.ToolCalls));
                    return;
                }

                // Counted against what this run actually called, which is why it happens here and
                // not in the registry: this is the one place that has both the final text and the
                // trace. A count, never a judgement - see GroundingCheck for what the numbers do
                // and do not mean.
                var citations = GroundingCheck.Count(response.Text, agent.Trace.ToolsCalled());

                _registry.Finish(agent.Id, AgentState.Completed, resultText: response.Text,
                    citations: citations);

                _logger.LogInformation(
                    "Agent {AgentId} ({Role}) completed in {DurationMs} ms over {Steps} steps and "
                    + "{ToolCalls} tool calls, {Tokens} tokens, citations {Valid} valid and "
                    + "{Dangling} dangling.",
                    agent.Id, role.Name,
                    (Int64)(DateTimeOffset.UtcNow - agent.CreatedUtc).TotalMilliseconds,
                    Interlocked.Read(ref agent.Steps), Interlocked.Read(ref agent.ToolCalls),
                    Interlocked.Read(ref agent.InputTokens) + Interlocked.Read(ref agent.OutputTokens),
                    citations.Valid, citations.Dangling);
            }
            catch (AgentBudgetExceededException budget)
            {
                _registry.Finish(agent.Id, AgentState.BudgetExceeded, failure: budget.Message,
                    budget: budget.Kind);
                _logger.LogInformation("Agent {AgentId} stopped on its {Budget} budget: {Reason}",
                    agent.Id, AgentStates.Wire(budget.Kind), budget.Message);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                // Checked BEFORE the agent's own token, because Finish cancels that token, so by the
                // time a caller's cancel unwinds to here both are set. The deadline source is only
                // ever set by the timer.
                var reason = String.Format(CultureInfo.InvariantCulture,
                    "This agent ran for {0} seconds, which is its limit "
                    + "(Agents:Limits:MaxRunSeconds).", limits.MaxRunSeconds);
                _registry.Finish(agent.Id, AgentState.BudgetExceeded, failure: reason, budget: BudgetKind.Time);
                _logger.LogInformation("Agent {AgentId} stopped on its time budget.", agent.Id);
            }
            catch (OperationCanceledException)
            {
                // A cancel already recorded the ending, and Finish is first-wins, so this is a
                // no-op in the ordinary case. It matters for the case it is not: a token cancelled
                // by host shutdown, where nothing has recorded anything yet. Logged either way, so
                // that this is not the one ending a run can reach silently.
                if (_registry.Finish(agent.Id, AgentState.Cancelled,
                        failure: "Cancelled while the host was stopping."))
                {
                    _logger.LogInformation(
                        "Agent {AgentId} ({Role}) was cancelled before it recorded an ending.",
                        agent.Id, role.Name);
                }
            }
            catch (Exception failure)
            {
                _registry.Finish(agent.Id, AgentState.Failed, failure: failure.Message);
                _logger.LogWarning(failure, "Agent {AgentId} ({Role}) failed.", agent.Id, role.Name);
            }
            finally
            {
                linked?.Dispose();
            }
        }

        /// <summary>
        ///   Performs one tool call and records what it did.
        ///
        ///   <para>
        ///     <b>This is the only place a tool call is observable.</b> The meter one layer down sees
        ///     a model ASK for a tool; the framework's loop does the invoking. So the arguments as
        ///     they arrived, the result as it came back, how long it took and whether it worked are
        ///     visible here and nowhere else, which is why the invoker is replaced rather than
        ///     wrapped.
        ///   </para>
        ///   <para>
        ///     <b>A failure is recorded and then RETHROWN.</b> The framework turns it into a tool
        ///     result the model reads, and that is what lets a model correct a malformed call, which
        ///     the measured behaviour of the shipped agent model needs. Swallowing it here would
        ///     leave the loop believing the call succeeded and returned nothing.
        ///   </para>
        ///   <para>
        ///     <b>A <see cref="ToolRefusal" /> is recorded as a failure and NOT rethrown.</b> The
        ///     framework has to see an ordinary result, or the refusal counts against its
        ///     consecutive-error cap and a model that keeps asking ends the run;
        ///     <see cref="ToolRefusal" /> is the one home for why.
        ///   </para>
        /// </summary>
        private async ValueTask<Object?> Invoke(AgentRecord agent, FunctionInvocationContext context,
            CancellationToken cancellationToken)
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            var call = context.CallContent;
            var tool = context.Function?.Name ?? call?.Name ?? "(unnamed)";

            // Serialized from the arguments the MODEL produced, not from the schema: the measured
            // failure mode on a small model is arguments that echo the schema instead of filling
            // it, and a reviewer needs to see exactly that.
            var arguments = Describe(call?.Arguments);

            try
            {
                var result = await context.Function!.InvokeAsync(context.Arguments, cancellationToken)
                    .ConfigureAwait(false);

                started.Stop();

                // A refusal is the failed call it is on the record, while the framework gets the
                // message as an ordinary result, so the model reads it and the turn continues.
                // ToolRefusal is the one home for why a refusal is a value rather than a throw.
                if (result is ToolRefusal refused)
                {
                    _journal.ToolCall(agent, call?.CallId, tool, arguments, result: null,
                        success: false, error: refused.Message,
                        durationMs: started.ElapsedMilliseconds);
                    return refused.Message;
                }

                _journal.ToolCall(agent, call?.CallId, tool, arguments, Describe(result),
                    success: true, error: null, durationMs: started.ElapsedMilliseconds);
                return result;
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                started.Stop();
                _journal.ToolCall(agent, call?.CallId, tool, arguments, result: null,
                    success: false, error: failure.Message, durationMs: started.ElapsedMilliseconds);
                throw;
            }
        }

        /// <summary>
        ///   A tool's arguments or result as text for the trace. JSON when it is structured, because
        ///   that is what it was; <c>ToString</c> otherwise. Never throws: a capture that failed to
        ///   serialize must not be able to fail the tool call it was describing.
        /// </summary>
        private static String? Describe(Object? value)
        {
            switch (value)
            {
                case null:
                    return null;
                case String text:
                    return text;
            }

            try
            {
                return System.Text.Json.JsonSerializer.Serialize(value, NoSQL.GraphDB.Rest.RestSeam.JsonOptions);
            }
            catch (Exception)
            {
                // Deliberately broad: this is a capture for a human to read, and there is no failure
                // here worth turning into the agent's failure.
                try
                {
                    return value.ToString();
                }
                catch (Exception)
                {
                    return "(a value that could not be described)";
                }
            }
        }

        /// <summary>
        ///   The longest deadline a timer can actually be armed for. A configured cap above this is
        ///   clamped to it with a warning rather than refused, because a number that large means
        ///   "effectively no cap" and refusing it would be a worse answer than honouring the intent.
        /// </summary>
        private static readonly TimeSpan MaxArmableDeadline =
            TimeSpan.FromMilliseconds(UInt32.MaxValue - 2);

        /// <summary>
        ///   What this agent may call: the MCP tools its role allows, plus the swarm tools if it is
        ///   an orchestrator.
        ///
        ///   <para>
        ///     APPENDED after the allowlist rather than listed in it, because the allowlist narrows
        ///     what the MCP server advertises and these are not MCP tools; <see cref="RoleCatalog" />
        ///     says so where the allowlist is declared. Built PER AGENT because each orchestrator
        ///     may only spawn and await its own workers, which is what makes an id from one
        ///     orchestrator useless to another.
        ///   </para>
        ///   <para>
        ///     Only an orchestrator gets them. A worker with <c>spawn_worker</c> is how a swarm
        ///     becomes a tree nobody bounded, and the depth cap exists because that cost is
        ///     multiplicative; an assistant with it would be an orchestrator that was never told
        ///     the one-composer rule.
        ///   </para>
        /// </summary>
        private IReadOnlyList<AITool> Tools(AgentRecord agent, AgentRole role)
        {
            var allowed = role.Filter(_toolset.Tools);
            if (!String.Equals(role.Name, "orchestrator", StringComparison.OrdinalIgnoreCase))
            {
                return allowed;
            }

            var swarm = new SwarmTools(_registry, _roles, this, agent).Tools();
            var all = new List<AITool>(allowed.Count + swarm.Count);
            all.AddRange(allowed);
            all.AddRange(swarm);
            return all;
        }

        /// <summary>
        ///   The role prompt, plus whatever the caller appended. The order is the point: the role
        ///   prompt comes FIRST and the appendix cannot replace it, because the role prompt carries
        ///   the honesty properties a reviewer depends on, and a caller who could overwrite it could
        ///   turn an agent into a confident liar with one request field. What a role prompt may and
        ///   may not be credited with is on <see cref="RoleCatalog" />, including the measurement
        ///   that narrowed it.
        /// </summary>
        private static String Instructions(AgentRole role, String? appendix)
        {
            return String.IsNullOrWhiteSpace(appendix)
                ? role.Prompt
                : role.Prompt + "\n\nAdditional instructions for this task:\n" + appendix.Trim();
        }
    }
}
