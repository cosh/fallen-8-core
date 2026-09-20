// MIT License
//
// AgentJournal.cs
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
using System.Threading;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.Agents.Configuration;

namespace NoSQL.GraphDB.Agents.Runtime
{
    /// <summary>
    ///   Records what an agent did, in both places it has to appear.
    ///
    ///   <para>
    ///     <b>One method per fact, and it writes the trace step and publishes the feed event
    ///     together.</b> The two are different views of the same thing - a step is the record a
    ///     reviewer reads afterwards, an event is the notification a watcher gets now - and letting
    ///     a caller write one without the other is how they drift. So no caller decides: the runner
    ///     says "a tool was called" and this decides that it becomes both, while a model call
    ///     becomes a step only, because a subscriber watching a swarm does not want one event per
    ///     step.
    ///   </para>
    ///   <para>
    ///     <b>The byte capping happens here, once.</b> A capture is capped on the way into the trace
    ///     step and the event carries the SAME capped string, so the two can never disagree about
    ///     what was seen and there is one place that knows what the caps are.
    ///   </para>
    ///   <para>
    ///     A capped capture carries its own "truncated, N bytes total" suffix, and the suffix is
    ///     inside the configured cap rather than added past it. <see cref="TraceStepKinds.CapWithMarker" />
    ///     owns that rule and its one exception.
    ///   </para>
    /// </summary>
    public sealed class AgentJournal
    {
        private readonly AgentFeedDispatcher _feed;
        private readonly IOptions<AgentsOptions> _options;
        private readonly TimeProvider _clock;
        private readonly Diagnostics.AgentsMetrics? _metrics;

        /// <param name="feed">The broadcast every fact is published to.</param>
        /// <param name="options">The trace caps a capture is held to.</param>
        /// <param name="clock">The clock every step and event is stamped from.</param>
        /// <param name="metrics">
        ///   The host's meter, or null for a caller that is not measuring. It belongs HERE rather
        ///   than at each fact's origin because this type already exists to be the one call site
        ///   per fact: a meter wired anywhere else would be a second place to remember, and the
        ///   thing most likely to be forgotten is the one nothing fails without.
        /// </param>
        public AgentJournal(AgentFeedDispatcher feed, IOptions<AgentsOptions> options,
            TimeProvider? clock = null, Diagnostics.AgentsMetrics? metrics = null)
        {
            _feed = feed ?? throw new ArgumentNullException(nameof(feed));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _clock = clock ?? TimeProvider.System;
            _metrics = metrics;
        }

        /// <summary>The trace bound every new agent's buffer is built with.</summary>
        public Int32 MaxSteps => _options.Value.Trace.MaxSteps;

        /// <summary>
        ///   An agent was admitted. The first step of every trace, and it carries the host instance,
        ///   because nothing here survives a restart and a reader comparing two traces has to be
        ///   able to tell.
        /// </summary>
        /// <param name="agent">The agent that was admitted.</param>
        /// <param name="state">
        ///   The state the caller SET, passed in rather than read off the record here. The record is
        ///   shared, so re-reading it reports whatever it holds at this instant instead of the
        ///   transition being journaled: a cancel landing in that window made a spawn step carry
        ///   <c>cancelled</c>, which reads as an agent that was never alive.
        /// </param>
        public void Spawned(AgentRecord agent, AgentState state)
        {
            Guard(agent);
            var at = _clock.GetUtcNow();

            agent.Trace.Record(new TraceStep
            {
                Kind = TraceStepKinds.Wire(TraceStepKind.Spawn),
                State = AgentStates.Wire(state),
                HostInstanceId = agent.HostInstanceId,
            }, at);

            _feed.Publish(Basic(agent, AgentEventKind.AgentSpawned, state), at);

            // On the PARENT's trace as well, because "this agent spawned that one" is a fact about
            // the parent's run and is what makes a swarm readable from the orchestrator's trace.
            agent.Parent?.Trace.Record(new TraceStep
            {
                Kind = TraceStepKinds.Wire(TraceStepKind.Spawn),
                ChildId = agent.Id,
            }, at);
        }

        /// <summary>The agent moved to a live state.</summary>
        /// <param name="agent">The agent that moved.</param>
        /// <param name="state">
        ///   The state the caller SET. See <see cref="Spawned" /> for why this is a parameter rather
        ///   than a read of <see cref="AgentRecord.State" />; here the same window made a live
        ///   transition record the TERMINAL state instead, so a completed run's trace carried a
        ///   second "changed to completed" step that no transition produced.
        /// </param>
        public void StateChanged(AgentRecord agent, AgentState state)
        {
            Guard(agent);
            var at = _clock.GetUtcNow();
            var budget = state == AgentState.BudgetExceeded ? AgentStates.Wire(agent.Budget) : null;

            agent.Trace.Record(new TraceStep
            {
                Kind = TraceStepKinds.Wire(TraceStepKind.StateChanged),
                State = AgentStates.Wire(state),
                Budget = budget,
            }, at);

            var moved = Basic(agent, AgentEventKind.AgentStateChanged, state);
            moved.Budget = budget;

            // Wall clock SO FAR, which is the fourth counter and the one a state change was
            // missing: the other three ride on every event, so a subscriber rendering live cost
            // had to poll the listing for duration alone, which is the one thing the counters on
            // the feed exist to avoid. Measured from admission, as the ending's duration is, so
            // the numbers a subscriber sees over a run are one series rather than two.
            moved.DurationMs = (Int64)(at - agent.CreatedUtc).TotalMilliseconds;
            _feed.Publish(moved, at);
        }

        /// <summary>
        ///   The agent reached an ending. One state change, then the ending's own event, because a
        ///   subscriber filtering on <c>agentCompleted</c> should not have to also watch state
        ///   changes to learn a run finished.
        ///
        ///   <para>
        ///     The citation check is recorded BEFORE the ending step, which is the order it
        ///     happened in: it is a statement about the final text, and the final text exists
        ///     before the run is marked ended. So the ending is the last step of a completed run,
        ///     and a test pins that.
        ///   </para>
        ///   <para>
        ///     A deliberate ordering decision, NOT the repair of a false claim, which is what this
        ///     said. Recording it afterwards was defensible: no wire contract says the ending is
        ///     last, the spec's one sentence about a last step is about cancellation (where there
        ///     is no check and the ending IS last), and the only reader scans the tail for citation
        ///     fields and is position-agnostic. A sceptic refuted that half of the finding and was
        ///     right; the reorder is kept because this order is the truer one and because it makes
        ///     the tail a pinned property instead of an accident.
        ///   </para>
        /// </summary>
        public void Finished(AgentRecord agent, CitationCounts? citations)
        {
            Guard(agent);
            var at = _clock.GetUtcNow();
            var caps = _options.Value.Trace;

            if (citations != null)
            {
                agent.Trace.Record(new TraceStep
                {
                    Kind = TraceStepKinds.Wire(TraceStepKind.CitationCheck),
                    ValidCitations = citations.Valid,
                    DanglingCitations = citations.Dangling,
                }, at);
            }

            // The ending LAST, so a reader taking the tail of a trace sees how the run ended there.
            agent.Trace.Record(new TraceStep
            {
                Kind = TraceStepKinds.Wire(TraceStepKind.StateChanged),
                State = AgentStates.Wire(agent.State),
                Budget = agent.State == AgentState.BudgetExceeded ? AgentStates.Wire(agent.Budget) : null,
                DurationMs = (Int64)((agent.FinishedUtc ?? at) - agent.CreatedUtc).TotalMilliseconds,
            }, at);

            var ended = Basic(agent,
                agent.State == AgentState.Completed ? AgentEventKind.AgentCompleted : AgentEventKind.AgentFailed,
                agent.State);
            ended.Budget = agent.State == AgentState.BudgetExceeded ? AgentStates.Wire(agent.Budget) : null;
            ended.Failure = agent.Failure;
            ended.Citations = citations;
            ended.DurationMs = (Int64)((agent.FinishedUtc ?? at) - agent.CreatedUtc).TotalMilliseconds;

            // The result travels on the event, capped like any other capture: a subscriber watching
            // a swarm should not have to fetch a detail route to see what an agent concluded, and a
            // model can produce an arbitrarily long answer.
            ended.ResultText = TraceStepKinds.CapWithMarker(agent.ResultText, caps.ResultBytes,
                out _, out var truncated);
            if (truncated)
            {
                ended.Truncated = true;
            }

            _feed.Publish(ended, at);

            // The outcome carries the BUDGET's name for a budget ending, because budgetExceeded
            // alone cannot tell an operator whether to raise a cap or fix a loop.
            _metrics?.Finished(agent.State, agent.Budget, agent.Role);
        }

        /// <summary>One model call, as a trace step only. The provenance it records is per STEP;
        /// <c>TraceStep.Backend</c> is the one home for why.</summary>
        public void ModelCall(AgentRecord agent, String? backend, String? model, Int64 durationMs,
            Int64 inputTokens, Int64 outputTokens, Boolean usageReported)
        {
            Guard(agent);

            agent.Trace.Record(new TraceStep
            {
                Kind = TraceStepKinds.Wire(TraceStepKind.ModelCall),
                Backend = backend,
                Model = model,
                DurationMs = durationMs,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                UnreportedUsage = usageReported ? null : true,
            }, _clock.GetUtcNow());

            // The backend as the instance named it, which is a closed set and therefore safe as a
            // tag; the role likewise. Nothing a caller typed goes near this.
            _metrics?.ModelCall(backend, agent.Role, inputTokens, outputTokens, durationMs);
        }

        /// <summary>
        ///   One tool invocation, capped once and recorded in both places. Both the arguments and
        ///   the result are the model's or the graph's, so neither is trusted to be small.
        /// </summary>
        public void ToolCall(AgentRecord agent, String? toolCallId, String tool, String? arguments,
            String? result, Boolean success, String? error, Int64 durationMs)
        {
            Guard(agent);
            var at = _clock.GetUtcNow();
            var caps = _options.Value.Trace;

            var cappedArgs = TraceStepKinds.CapWithMarker(arguments, caps.ArgsBytes,
                out var argBytes, out var argCut);
            var cappedResult = TraceStepKinds.CapWithMarker(result, caps.ResultBytes,
                out var resultBytes, out var resultCut);
            var truncated = argCut || resultCut;

            agent.Trace.Record(new TraceStep
            {
                Kind = TraceStepKinds.Wire(TraceStepKind.ToolCall),
                ToolCallId = toolCallId,
                Tool = tool,
                Arguments = cappedArgs,
                Result = cappedResult,
                Truncated = truncated ? true : null,
                ArgumentsBytes = argBytes,
                ResultBytes = resultBytes,
                Success = success,
                Error = error,
                DurationMs = durationMs,
            }, at);

            var called = Basic(agent, AgentEventKind.ToolCalled, agent.State);
            called.ToolCallId = toolCallId;
            called.Tool = tool;
            called.Arguments = cappedArgs;
            called.Result = cappedResult;
            called.Truncated = truncated ? true : null;
            called.ArgumentsBytes = argBytes;
            called.ResultBytes = resultBytes;
            called.Success = success;
            // The reason travels with the event, on the field an ending already uses for one. The
            // feed is the channel an operator watches, and without this it could say a call failed
            // without saying which cap or which status said so: the reason was on the trace route
            // alone, which that operator has no reason to be reading.
            called.Failure = error;
            called.DurationMs = durationMs;
            _feed.Publish(called, at);

            // The tool name as the MCP server advertises it, a closed set. Neither capture goes
            // near a tag: arguments and results are the model's and the graph's.
            _metrics?.ToolCall(tool, success);
        }

        /// <summary>A message to or from the agent.</summary>
        public void Message(AgentRecord agent, String direction, String messageId, String? inReplyTo,
            String? text)
        {
            Guard(agent);
            var at = _clock.GetUtcNow();
            var caps = _options.Value.Trace;

            var capped = TraceStepKinds.CapWithMarker(text, caps.ResultBytes, out var bytes, out var cut);

            agent.Trace.Record(new TraceStep
            {
                Kind = TraceStepKinds.Wire(TraceStepKind.Message),
                Direction = direction,
                MessageId = messageId,
                InReplyTo = inReplyTo,
                Text = capped,
                Truncated = cut ? true : null,
            }, at);

            var message = Basic(agent, AgentEventKind.AgentMessage, agent.State);
            message.Direction = direction;
            message.MessageId = messageId;
            message.InReplyTo = inReplyTo;
            message.Text = capped;
            message.Truncated = cut ? true : null;
            _feed.Publish(message, at);
        }

        /// <summary>
        ///   The fields every event carries, including the counters. They are on EVERY event rather
        ///   than only the ending, so a subscriber can render live cost without polling, which is
        ///   the reason the feed exists at all rather than a listing being enough.
        /// </summary>
        /// <param name="agent">The agent the event is about.</param>
        /// <param name="kind">The event kind, which decides which further fields a caller sets.</param>
        /// <param name="state">
        ///   The state to stamp. A parameter rather than a read of the record, because for a
        ///   transition the event is ABOUT the state being set, and the record may already hold a
        ///   later one. A caller reporting something that merely happened while the agent was in
        ///   some state passes <c>agent.State</c>, which is the right answer for it.
        /// </param>
        private static AgentEvent Basic(AgentRecord agent, AgentEventKind kind, AgentState state)
        {
            var input = Interlocked.Read(ref agent.InputTokens);
            var output = Interlocked.Read(ref agent.OutputTokens);

            return new AgentEvent
            {
                Kind = AgentEventKinds.Wire(kind),
                AgentId = agent.Id,
                ParentId = agent.ParentId,
                Role = agent.Role,
                Name = agent.Name,
                State = AgentStates.Wire(state),
                Tokens = new EventCounters
                {
                    Input = input,
                    Output = output,
                    Total = input + output,
                    Steps = Interlocked.Read(ref agent.Steps),
                    ToolCalls = Interlocked.Read(ref agent.ToolCalls),
                    UnreportedUsage = agent.UnreportedUsage,
                },
            };
        }

        private static void Guard(AgentRecord agent)
        {
            if (agent == null)
            {
                throw new ArgumentNullException(nameof(agent));
            }
        }
    }
}
