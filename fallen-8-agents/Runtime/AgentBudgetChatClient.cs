// MIT License
//
// AgentBudgetChatClient.cs
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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NoSQL.GraphDB.Agents.Configuration;

namespace NoSQL.GraphDB.Agents.Runtime
{
    /// <summary>
    ///   One agent's meter and its brake, sitting between the framework's tool loop and the shared
    ///   <see cref="Model.Fallen8ChatClient" />.
    ///
    ///   <para>
    ///     <b>This is where every per-run cap is enforced, and it is enforced in code rather than
    ///     asked of the model.</b> A model that is looping is exactly the model that will not honour
    ///     an instruction to stop, so the prompt is not the place for a budget. Every model call in
    ///     an agent's run passes through here, which makes it the one seam that sees steps, tokens
    ///     and tool calls without knowing anything about the loop above it.
    ///   </para>
    ///   <para>
    ///     <b>The cap is checked BEFORE the call, not after</b>, which saves exactly one model call
    ///     on a run that is already over: a run stopped afterwards would have made the call that
    ///     discovered the breach and then one more.
    ///   </para>
    ///   <para>
    ///     <b>Only the step cap is exact. The other two are ceilings a run stops AT.</b> Steps are
    ///     counted here and checked here, so the call that would be one too many is refused.
    ///   </para>
    ///   <para>
    ///     The TOKEN budget cannot be exact: a step's cost is unknown until it returns, so the last
    ///     admitted step can carry the total past the budget, and bounding it tighter would mean
    ///     asking a provider to price a request before making it. The overshoot is one step.
    ///   </para>
    ///   <para>
    ///     The TOOL-CALL cap is sampled between model calls, not between tool calls, because this
    ///     seam does not see tool invocations: the framework's loop invokes them and comes back
    ///     here. So every call in one model response runs, and the cap stops the NEXT model call.
    ///     A model that asks for eight tools at once therefore overshoots by up to seven. Moving
    ///     the check to where invocation happens would mean owning the loop, which is the one thing
    ///     this host deliberately does not do.
    ///   </para>
    ///   <para>
    ///     Wall clock is NOT enforced here: it is the runner's linked deadline, because a call that
    ///     never returns has no "before" to check at.
    ///   </para>
    /// </summary>
    internal sealed class AgentBudgetChatClient : IChatClient
    {
        private readonly IChatClient _inner;
        private readonly AgentRecord _agent;
        private readonly AgentsOptions.LimitsOptions _limits;

        internal AgentBudgetChatClient(IChatClient inner, AgentRecord agent,
            AgentsOptions.LimitsOptions limits)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _agent = agent ?? throw new ArgumentNullException(nameof(agent));
            _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        }

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Admit();

            var response = await _inner.GetResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false);

            Count(response);
            return response;
        }

        /// <summary>
        ///   Counted the same way, because a streamed step costs the same as a buffered one. The
        ///   adapter below reports no streaming and answers this in one update, so the aggregation
        ///   here is over a sequence of length one in practice; it is written for the general case so
        ///   a future streamed gateway needs no second meter.
        /// </summary>
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Admit();

            var updates = new List<ChatResponseUpdate>();
            await foreach (var update in _inner.GetStreamingResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false))
            {
                updates.Add(update);
                yield return update;
            }

            // After the sequence, because usage arrives on the last update on every provider that
            // reports it at all.
            Count(updates.ToChatResponse());
        }

        public Object? GetService(Type serviceType, Object? serviceKey = null)
        {
            if (serviceType == null)
            {
                throw new ArgumentNullException(nameof(serviceType));
            }

            // The meter answers for itself and delegates everything else, so a caller reaching for
            // the adapter underneath still finds it.
            return serviceType.IsInstanceOfType(this) ? this : _inner.GetService(serviceType, serviceKey);
        }

        public void Dispose()
        {
            // The inner client is shared by every agent on this host, so it is not this decorator's
            // to dispose. One agent finishing must not close the transport the others are using.
        }

        /// <summary>
        ///   Decides whether this agent may make another model call. Throws rather than returning a
        ///   refusal because it sits under the framework's loop, which has no vocabulary for "stop,
        ///   but not because of an error"; the runner turns it back into the ending it is.
        /// </summary>
        private void Admit()
        {
            if (_limits.MaxStepsPerRun > 0 && Interlocked.Read(ref _agent.Steps) >= _limits.MaxStepsPerRun)
            {
                throw new AgentBudgetExceededException(BudgetKind.Steps, String.Format(
                    CultureInfo.InvariantCulture,
                    "This agent has made {0} model calls, which is its limit "
                    + "(Agents:Limits:MaxStepsPerRun).", _limits.MaxStepsPerRun));
            }

            if (_limits.MaxToolCallsPerRun > 0
                && Interlocked.Read(ref _agent.ToolCalls) >= _limits.MaxToolCallsPerRun)
            {
                throw new AgentBudgetExceededException(BudgetKind.ToolCalls, String.Format(
                    CultureInfo.InvariantCulture,
                    "This agent has made {0} tool calls, which is its limit "
                    + "(Agents:Limits:MaxToolCallsPerRun).", _limits.MaxToolCallsPerRun));
            }

            var spent = Interlocked.Read(ref _agent.InputTokens) + Interlocked.Read(ref _agent.OutputTokens);
            if (_agent.TokenBudget > 0 && spent >= _agent.TokenBudget)
            {
                throw new AgentBudgetExceededException(BudgetKind.Tokens, String.Format(
                    CultureInfo.InvariantCulture,
                    "This agent has spent {0} of its {1} token budget.", spent, _agent.TokenBudget));
            }
        }

        /// <summary>
        ///   Records what the step cost. Usage the backend did not report counts as ZERO and sets the
        ///   agent's unreported flag; it is never estimated, because a budget enforced on a guess
        ///   stops a run for a reason that never happened. One measured provider reply carried a
        ///   completion count of zero for a real tool call, which is why the flag exists at all.
        /// </summary>
        private void Count(ChatResponse response)
        {
            var usage = response.Usage;
            var reported = usage != null
                && (usage.InputTokenCount.HasValue || usage.OutputTokenCount.HasValue);

            _agent.CountStep(usage?.InputTokenCount ?? 0, usage?.OutputTokenCount ?? 0, reported);

            // Counted where they are REQUESTED rather than where they are invoked, so a tool the
            // framework refuses to run still costs the model call that asked for it. The cap is on
            // what a run may ask the graph to do, and an unrunnable request is still an attempt.
            var calls = response.Messages
                .SelectMany(m => m.Contents)
                .Count(c => c is FunctionCallContent);

            for (var i = 0; i < calls; i++)
            {
                _agent.CountToolCall();
            }
        }
    }

    /// <summary>
    ///   A run reached one of its caps. Not a failure of the agent or the model: the host stopped
    ///   it on purpose, and <see cref="Kind" /> says which budget, because an agent stopped for time
    ///   and one stopped for tokens want different responses from whoever spawned it.
    /// </summary>
    public sealed class AgentBudgetExceededException : Exception
    {
        public AgentBudgetExceededException(BudgetKind kind, String message)
            : base(message)
        {
            Kind = kind;
        }

        public BudgetKind Kind
        {
            get;
        }
    }
}
