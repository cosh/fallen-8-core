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
    ///     inside the configured cap rather than added past it: <c>ArgsBytes</c> is what a stored
    ///     capture costs, not what it costs before the marker.
    ///   </para>
    /// </summary>
    public sealed class AgentJournal
    {
        private readonly AgentFeedDispatcher _feed;
        private readonly IOptions<AgentsOptions> _options;
        private readonly TimeProvider _clock;

        public AgentJournal(AgentFeedDispatcher feed, IOptions<AgentsOptions> options,
            TimeProvider? clock = null)
        {
            _feed = feed ?? throw new ArgumentNullException(nameof(feed));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _clock = clock ?? TimeProvider.System;
        }

        /// <summary>The trace bound every new agent's buffer is built with.</summary>
        public Int32 MaxSteps => _options.Value.Trace.MaxSteps;

        /// <summary>An agent was admitted. The first step of every trace, and it carries the host
        /// instance, because nothing here survives a restart and a reader comparing two traces has
        /// to be able to tell.</summary>
        public void Spawned(AgentRecord agent)
        {
            Guard(agent);
            var at = _clock.GetUtcNow();

            agent.Trace.Record(new TraceStep
            {
                Kind = TraceStepKinds.Wire(TraceStepKind.Spawn),
                State = AgentStates.Wire(agent.State),
                HostInstanceId = agent.HostInstanceId,
            }, at);

            _feed.Publish(Basic(agent, AgentEventKind.AgentSpawned), at);

            // On the PARENT's trace as well, because "this agent spawned that one" is a fact about
            // the parent's run and is what makes a swarm readable from the orchestrator's trace.
            agent.Parent?.Trace.Record(new TraceStep
            {
                Kind = TraceStepKinds.Wire(TraceStepKind.Spawn),
                ChildId = agent.Id,
            }, at);
        }

        /// <summary>The agent moved to a live state.</summary>
        public void StateChanged(AgentRecord agent)
        {
            Guard(agent);
            var at = _clock.GetUtcNow();

            agent.Trace.Record(new TraceStep
            {
                Kind = TraceStepKinds.Wire(TraceStepKind.StateChanged),
                State = AgentStates.Wire(agent.State),
                Budget = agent.State == AgentState.BudgetExceeded ? AgentStates.Wire(agent.Budget) : null,
            }, at);

            var moved = Basic(agent, AgentEventKind.AgentStateChanged);
            moved.Budget = agent.State == AgentState.BudgetExceeded ? AgentStates.Wire(agent.Budget) : null;
            _feed.Publish(moved, at);
        }

        /// <summary>
        ///   The agent reached an ending. One state change, then the ending's own event, because a
        ///   subscriber filtering on <c>agentCompleted</c> should not have to also watch state
        ///   changes to learn a run finished.
        /// </summary>
        public void Finished(AgentRecord agent, CitationCounts? citations)
        {
            Guard(agent);
            var at = _clock.GetUtcNow();
            var caps = _options.Value.Trace;

            agent.Trace.Record(new TraceStep
            {
                Kind = TraceStepKinds.Wire(TraceStepKind.StateChanged),
                State = AgentStates.Wire(agent.State),
                Budget = agent.State == AgentState.BudgetExceeded ? AgentStates.Wire(agent.Budget) : null,
                DurationMs = (Int64)((agent.FinishedUtc ?? at) - agent.CreatedUtc).TotalMilliseconds,
            }, at);

            if (citations != null)
            {
                agent.Trace.Record(new TraceStep
                {
                    Kind = TraceStepKinds.Wire(TraceStepKind.CitationCheck),
                    ValidCitations = citations.Valid,
                    DanglingCitations = citations.Dangling,
                }, at);
            }

            var ended = Basic(agent,
                agent.State == AgentState.Completed ? AgentEventKind.AgentCompleted : AgentEventKind.AgentFailed);
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
        }

        /// <summary>One model call, as a trace step only. Provenance is per STEP here rather than
        /// per host, which is the point: a deployment that switches backend mid-day shows it.</summary>
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

            var called = Basic(agent, AgentEventKind.ToolCalled);
            called.ToolCallId = toolCallId;
            called.Tool = tool;
            called.Arguments = cappedArgs;
            called.Result = cappedResult;
            called.Truncated = truncated ? true : null;
            called.ArgumentsBytes = argBytes;
            called.ResultBytes = resultBytes;
            called.Success = success;
            called.DurationMs = durationMs;
            _feed.Publish(called, at);
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

            var message = Basic(agent, AgentEventKind.AgentMessage);
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
        private static AgentEvent Basic(AgentRecord agent, AgentEventKind kind)
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
                State = AgentStates.Wire(agent.State),
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
