// MIT License
//
// AgentState.cs
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

namespace NoSQL.GraphDB.Agents.Runtime
{
    /// <summary>
    ///   Where an agent is. <c>Pending</c> and <c>Running</c> and <c>WaitingForUser</c> are live;
    ///   the last four are endings and are final, so an agent that reached one never moves again.
    ///   <para>
    ///     Serialized as the camel-case names the control-plane API documents
    ///     (<c>waitingForUser</c>, <c>budgetExceeded</c>), which is why the wire spelling is
    ///     <see cref="AgentStates" />'s job rather than the enum's <c>ToString</c>.
    ///   </para>
    /// </summary>
    public enum AgentState
    {
        /// <summary>Accepted, not yet started. The state a spawn's 202 reports.</summary>
        Pending = 0,

        /// <summary>Working: a model call or a tool call is in flight, or one is about to be.</summary>
        Running = 1,

        /// <summary>Finished its turn and is waiting for the next user message. Live, and still
        /// holding its session, so it counts against the concurrency cap.</summary>
        WaitingForUser = 2,

        /// <summary>Answered. Its result text is the deliverable.</summary>
        Completed = 3,

        /// <summary>Ended on an error it could not work around. The message says which.</summary>
        Failed = 4,

        /// <summary>Ended because it was cancelled, by a caller or by its orchestrator going
        /// away.</summary>
        Cancelled = 5,

        /// <summary>Ended because it hit one of the four caps. Which one is carried separately: an
        /// agent stopped for time and one stopped for tokens want different responses.</summary>
        BudgetExceeded = 6,
    }

    /// <summary>Which budget ended a run. Only meaningful on <see cref="AgentState.BudgetExceeded" />.</summary>
    public enum BudgetKind
    {
        None = 0,
        Tokens = 1,
        Steps = 2,
        ToolCalls = 3,
        Time = 4,
    }

    /// <summary>
    ///   The wire spellings and the one rule about them: an ending is final. Kept beside the enum
    ///   rather than in the registry because both the runner and the endpoints ask it.
    /// </summary>
    public static class AgentStates
    {
        /// <summary>The name this state travels under.</summary>
        public static String Wire(AgentState state)
        {
            return state switch
            {
                AgentState.Pending => "pending",
                AgentState.Running => "running",
                AgentState.WaitingForUser => "waitingForUser",
                AgentState.Completed => "completed",
                AgentState.Failed => "failed",
                AgentState.Cancelled => "cancelled",
                AgentState.BudgetExceeded => "budgetExceeded",
                _ => "unknown",
            };
        }

        public static String Wire(BudgetKind kind)
        {
            return kind switch
            {
                BudgetKind.Tokens => "tokens",
                BudgetKind.Steps => "steps",
                BudgetKind.ToolCalls => "toolCalls",
                BudgetKind.Time => "time",
                _ => "none",
            };
        }

        /// <summary>
        ///   True once an agent can never move again. Every ending is terminal, and the registry
        ///   relies on that: a cancel arriving after a completion is a no-op rather than a state
        ///   change, so a run that already said how it ended keeps saying it.
        /// </summary>
        public static Boolean IsTerminal(AgentState state)
        {
            return state is AgentState.Completed or AgentState.Failed or AgentState.Cancelled
                or AgentState.BudgetExceeded;
        }

        /// <summary>True while an agent still occupies a concurrency slot.</summary>
        public static Boolean IsLive(AgentState state)
        {
            return !IsTerminal(state);
        }
    }
}
