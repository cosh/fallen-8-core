// MIT License
//
// AgentsMetrics.cs
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
using System.Diagnostics.Metrics;
using NoSQL.GraphDB.Agents.Runtime;

namespace NoSQL.GraphDB.Agents.Diagnostics
{
    /// <summary>
    ///   The agent host's meter (feature agent-host, spec 3.6): one instrument per number an
    ///   operator watches, pushed to the same collector as the Fallen-8 this host runs against.
    ///
    ///   <para>
    ///     <b>TAG HYGIENE, which is the rule that shapes this whole type.</b> No user input reaches
    ///     a tag value, so no task text and no agent NAME: both arrive from a caller on every
    ///     spawn, and tagging by either lets a caller mint unbounded time series in somebody else's
    ///     monitoring backend. What is tagged is closed sets only: the role (three), the token
    ///     direction (two), the outcome (four endings plus the four budget names), the backend as
    ///     the instance named it, the tool name as the MCP server advertises it, and a success
    ///     flag. An agent ID is not tagged either, and that is deliberate rather than an oversight:
    ///     ids are unbounded over a host's lifetime, and per-agent detail is what the trace and the
    ///     feed are for.
    ///   </para>
    ///   <para>
    ///     <b>CONTAINMENT.</b> <c>Counter.Add</c> and <c>Histogram.Record</c> invoke listener
    ///     callbacks INLINE and the BCL does not swallow what they throw, so an exporter having a
    ///     bad day would otherwise fault the agent being measured. Every recording method is
    ///     therefore wrapped: observability must never fault the observed, which is the same rule
    ///     the apiApp and the MCP server state at their own meters.
    ///   </para>
    ///   <para>
    ///     Constructed even when no exporter is configured. An instrument nobody listens to costs a
    ///     few objects and nothing else, and a host that had to be configured before it could count
    ///     would have nothing to say about the run that made an operator look.
    ///   </para>
    /// </summary>
    public sealed class AgentsMetrics : IDisposable
    {
        /// <summary>The meter name the collector's view is keyed on.</summary>
        public const String MeterName = "NoSQL.GraphDB.Agents";

        /// <summary>
        ///   The activity source the framework's GenAI spans land on. The same string as the meter,
        ///   as the MCP server's diagnostics do it: the runner hands this name to the framework's
        ///   OpenTelemetry wrapper, so <c>invoke_agent</c>, the chat span and <c>execute_tool</c>
        ///   are emitted on a source this host names rather than on a library default that could
        ///   change under us.
        /// </summary>
        public const String SourceName = MeterName;

        /// <summary>
        ///   The framework's OWN meter, which is not ours and cannot be renamed by us: its GenAI
        ///   token and duration instruments are published here whatever source name it is given.
        ///   Registered beside <see cref="MeterName" /> so the library's accounting for a run is
        ///   exported alongside this host's, and pinned by a test that observes both, because a
        ///   name taken from a library's assembly is a name that can move in a package bump.
        /// </summary>
        public const String FrameworkTelemetryName = "Experimental.Microsoft.Extensions.AI";

        private readonly Meter _meter;
        private readonly Counter<Int64> _tokens;
        private readonly Counter<Int64> _completed;
        private readonly Histogram<Double> _stepDuration;
        private readonly Counter<Int64> _toolCalls;
        private Boolean _disposed;

        /// <summary>
        ///   Builds the meter and its instruments.
        /// </summary>
        /// <param name="activeAgents">
        ///   Reads the number of live agents for the <c>f8a.agents.active</c> gauge. A CALLBACK
        ///   rather than a value this type maintains, because the registry already knows and two
        ///   counts of one thing drift; it is invoked by the collector, so it must be cheap and must
        ///   not throw. The registry's own read is a lock and a count, and the callback is wrapped
        ///   here in any case.
        /// </param>
        public AgentsMetrics(Func<Int32> activeAgents)
        {
            if (activeAgents == null)
            {
                throw new ArgumentNullException(nameof(activeAgents));
            }

            _meter = new Meter(MeterName);

            _tokens = _meter.CreateCounter<Int64>("f8a.agents.tokens", "token",
                "Tokens an agent's run consumed, as the instance reported them.");
            _completed = _meter.CreateCounter<Int64>("f8a.agents.completed", "run",
                "Agent runs that reached an ending, by outcome.");
            _stepDuration = _meter.CreateHistogram<Double>("f8a.agents.step.duration", "ms",
                "Wall clock per chat call, as the HOST measured it.");
            _toolCalls = _meter.CreateCounter<Int64>("f8a.agents.tool.calls", "call",
                "Tool invocations an agent made.");

            // An observable gauge rather than an up-down counter: the registry is the one place
            // that knows how many agents are live, and it already evicts finished ones, so a
            // counter maintained here would be a second tally to keep in step with the first.
            _meter.CreateObservableGauge("f8a.agents.active", () => Observe(activeAgents), "agent",
                "Agents currently live on this host.");
        }

        /// <summary>
        ///   One finished chat call: its usage and how long the host measured it taking.
        /// </summary>
        /// <param name="backend">The backend the instance said served it, or null when it said
        /// nothing. A closed set, so it is safe as a tag; null becomes <c>unreported</c>, which is
        /// a real state rather than a gap (see <c>TraceStep.UnreportedUsage</c>).</param>
        /// <param name="role">The agent's role: one of three, so safe.</param>
        /// <param name="inputTokens">Prompt tokens, zero when the backend reported none.</param>
        /// <param name="outputTokens">Completion tokens, zero when the backend reported none.</param>
        /// <param name="durationMs">Wall clock as the host measured it, not as the backend reported
        /// it: a measured step reported 45 ms for a call that took 41 seconds.</param>
        public void ModelCall(String? backend, String role, Int64 inputTokens, Int64 outputTokens,
            Int64 durationMs)
        {
            try
            {
                var safeBackend = String.IsNullOrWhiteSpace(backend) ? "unreported" : backend!;
                var safeRole = String.IsNullOrWhiteSpace(role) ? "unknown" : role!;

                // Tokens carry a direction, so one instrument answers both "what did this cost"
                // and "was it prompt or completion", which are different questions on a host whose
                // prompts are long and whose answers are short.
                _tokens.Add(inputTokens,
                    new System.Collections.Generic.KeyValuePair<String, Object?>("direction", "input"),
                    new System.Collections.Generic.KeyValuePair<String, Object?>("role", safeRole));
                _tokens.Add(outputTokens,
                    new System.Collections.Generic.KeyValuePair<String, Object?>("direction", "output"),
                    new System.Collections.Generic.KeyValuePair<String, Object?>("role", safeRole));

                _stepDuration.Record(durationMs,
                    new System.Collections.Generic.KeyValuePair<String, Object?>("backend", safeBackend));
            }
            catch
            {
                // Contained: see the class doc. An exporter must not end a run.
            }
        }

        /// <summary>One tool invocation. The tool name is what the MCP server advertises, a closed
        /// set, so it is safe as a tag.</summary>
        public void ToolCall(String tool, Boolean success)
        {
            try
            {
                _toolCalls.Add(1,
                    new System.Collections.Generic.KeyValuePair<String, Object?>(
                        "tool", String.IsNullOrWhiteSpace(tool) ? "unknown" : tool),
                    new System.Collections.Generic.KeyValuePair<String, Object?>("success", success));
            }
            catch
            {
                // Contained: see the class doc.
            }
        }

        /// <summary>
        ///   One run that reached an ending, tagged with the outcome.
        /// </summary>
        /// <param name="state">The terminal state.</param>
        /// <param name="budget">Which budget ended it, for <c>budgetExceeded</c>; ignored
        /// otherwise.</param>
        /// <param name="role">The agent's role.</param>
        public void Finished(AgentState state, BudgetKind budget, String role)
        {
            try
            {
                _completed.Add(1,
                    new System.Collections.Generic.KeyValuePair<String, Object?>(
                        "outcome", Outcome(state, budget)),
                    new System.Collections.Generic.KeyValuePair<String, Object?>(
                        "role", String.IsNullOrWhiteSpace(role) ? "unknown" : role));
            }
            catch
            {
                // Contained: see the class doc.
            }
        }

        /// <summary>
        ///   The outcome tag: the ending, and for a budget the budget's own name, because
        ///   <c>budgetExceeded</c> alone cannot tell an operator whether to raise a cap or fix a
        ///   loop. A closed set of seven, which is what makes it safe to tag.
        /// </summary>
        public static String Outcome(AgentState state, BudgetKind budget)
        {
            if (state != AgentState.BudgetExceeded)
            {
                return AgentStates.Wire(state);
            }

            return budget == BudgetKind.None
                ? AgentStates.Wire(state)
                : AgentStates.Wire(state) + ":" + AgentStates.Wire(budget);
        }

        /// <summary>The gauge's callback, contained like every other recording path: it runs on the
        /// COLLECTOR's thread, so a throw there is somebody else's exception in somebody else's
        /// loop. A negative reading is never published; the collector sees nothing instead.</summary>
        private static Int32 Observe(Func<Int32> read)
        {
            try
            {
                return read();
            }
            catch
            {
                return 0;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _meter.Dispose();
        }
    }
}
