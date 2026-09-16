// MIT License
//
// AgentRegistry.cs
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.Agents.Configuration;

namespace NoSQL.GraphDB.Agents.Runtime
{
    /// <summary>
    ///   This process's memory of its agents: what is running, what ran, and what each one spent.
    ///
    ///   <para>
    ///     <b>In memory and deliberately not durable.</b> A restart ends everything, and the first
    ///     thing every listing reports is which host instance produced it, so a reader cannot
    ///     mistake an empty list for "nothing ever ran". An agent's value is in reviewing it in the
    ///     minutes after it finished, which is what <c>RetainFinishedMinutes</c> buys; a durable
    ///     run history is a different feature and would need a different store.
    ///   </para>
    ///   <para>
    ///     <b>It owns the concurrency cap and the cancellation tokens, not the runner.</b> A slot is
    ///     taken when an agent is admitted and released when it reaches an ending, so the cap holds
    ///     across a cancel racing a completion. The registry is the only thing that moves a state,
    ///     which is what makes "an ending is final" true rather than merely intended.
    ///   </para>
    /// </summary>
    public sealed class AgentRegistry : IDisposable
    {
        private readonly Object _gate = new Object();
        private readonly Dictionary<String, AgentRecord> _agents = new Dictionary<String, AgentRecord>(StringComparer.Ordinal);
        private readonly IOptions<AgentsOptions> _options;
        private readonly ILogger<AgentRegistry> _logger;
        private readonly AgentJournal _journal;
        private readonly TimeProvider _clock;
        private Int64 _sequence;
        private Boolean _disposed;

        public AgentRegistry(IOptions<AgentsOptions> options, ILogger<AgentRegistry> logger,
            AgentJournal journal, TimeProvider? clock = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _journal = journal ?? throw new ArgumentNullException(nameof(journal));
            _clock = clock ?? TimeProvider.System;

            // Stamped once and carried by every agent this process runs. It is the answer to "is
            // this the same host that ran the agent I am looking at", which matters because nothing
            // here survives a restart.
            HostInstanceId = Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        /// <summary>Identifies this process's run. See the constructor's remark.</summary>
        public String HostInstanceId
        {
            get;
        }

        /// <summary>How many agents are live right now.</summary>
        public Int32 ActiveCount
        {
            get
            {
                lock (_gate)
                {
                    return _agents.Values.Count(a => AgentStates.IsLive(a.State));
                }
            }
        }

        /// <summary>How many finished agents are still readable.</summary>
        public Int32 RetainedCount
        {
            get
            {
                lock (_gate)
                {
                    Evict();
                    return _agents.Values.Count(a => AgentStates.IsTerminal(a.State));
                }
            }
        }

        /// <summary>
        ///   Admits an agent, or refuses it because the host is already running as many as it may.
        ///   The refusal is a first-class outcome rather than an exception: a caller hitting the cap
        ///   has something to do about it (wait, or raise the cap), and the message says which.
        ///   <para>
        ///     <b>Every journal call in this class happens OUTSIDE <c>_gate</c></b>, and the reason
        ///     is here because three sites share it.
        ///   </para>
        ///   <para>
        ///     Not the reason this comment used to give. It said publishing releases a reader's
        ///     continuations onto the publishing thread, and that was never true: the dispatcher
        ///     creates every subscriber channel with <c>AllowSynchronousContinuations</c> false and
        ///     says it relies on that, so a reader's continuation is scheduled rather than run
        ///     inline. Measured against the runtime, not reasoned from the name.
        ///   </para>
        ///   <para>
        ///     The real reasons are three, and they survive that correction. Journaling under this
        ///     lock would nest registry, trace and feed locks on one path. It would hold the
        ///     registry across every subscriber write, so one publish delays every read and every
        ///     transition. And <see cref="AgentFeedDispatcher.Publish" /> calls a LOGGER on the
        ///     publishing thread when it drops a subscriber that fell behind; a log provider is
        ///     somebody else's code and may block, which is exactly why the dispatcher keeps that
        ///     call outside its OWN lock, and holding the registry across it would undo that.
        ///   </para>
        ///   <para>
        ///     What it costs, stated rather than hidden: between the release and the journal call
        ///     another thread can record an ending, so a live transition's step can land after it,
        ///     and an admission racing a shutdown can be journaled after the ending it precedes.
        ///     The state each step REPORTS is not affected, because the state is passed to the
        ///     journal rather than read back off the shared record. It is the same window
        ///     <see cref="Finish" /> documents, and closing it would mean paying the three costs
        ///     above on every transition.
        ///   </para>
        ///   <para>
        ///     <b>How far past an ending a step can land is bounded by the writers in flight, not
        ///     by one.</b> These docs said "one step", which undercounts: a cancel arriving during
        ///     a model call lets that call journal its own step when it returns, and if the
        ///     response asked for a tool the invocation already dispatched journals a second, since
        ///     a tool call already sent to the graph is deliberately not undone. So a reader should
        ///     expect a short tail past an ending rather than exactly one row.
        ///   </para>
        /// </summary>
        public Boolean TryAdmit(AgentSpawn spawn, out AgentRecord agent, out String problem)
        {
            if (spawn == null)
            {
                throw new ArgumentNullException(nameof(spawn));
            }

            agent = null!;
            problem = String.Empty;

            var limits = _options.Value.Limits;
            AgentState admitted;

            // Captured under the lock, reported after it. See the clamp below: this method's own
            // doc names a blocking log provider as the decisive reason the journal calls stay
            // outside this lock, and the clamp was logging inside it.
            Int32? clampedFrom = null;

            lock (_gate)
            {
                Evict();

                var live = _agents.Values.Count(a => AgentStates.IsLive(a.State));
                if (limits.MaxConcurrentAgents > 0 && live >= limits.MaxConcurrentAgents)
                {
                    problem = String.Format(CultureInfo.InvariantCulture,
                        "This host is already running {0} agents, which is its limit "
                        + "(Agents:Limits:MaxConcurrentAgents). Cancel one or wait for one to finish.",
                        limits.MaxConcurrentAgents);
                    return false;
                }

                AgentRecord? parent = null;
                if (spawn.ParentId != null && !_agents.TryGetValue(spawn.ParentId, out parent))
                {
                    problem = String.Format("No agent '{0}' to be the parent of this one.", spawn.ParentId);
                    return false;
                }

                // A caller's agent is depth 0; a worker is one deeper than whatever spawned it.
                // Read off the PARENT's record rather than walked up the tree, so an evicted
                // ancestor cannot make a deep agent look shallow.
                var depth = parent == null ? 0 : parent.Depth + 1;
                if (parent != null && limits.MaxSwarmDepth > 0 && depth >= limits.MaxSwarmDepth)
                {
                    problem = String.Format(CultureInfo.InvariantCulture,
                        "Agent '{0}' is at depth {1} and this host allows {2} "
                        + "(Agents:Limits:MaxSwarmDepth), so it may not spawn a worker of its own. "
                        + "A deeper tree multiplies cost that is configured per agent.",
                        parent.Id, parent.Depth, limits.MaxSwarmDepth);
                    return false;
                }

                // Over the orchestrator's whole LIFE, not at once: a live-only count would let it
                // spawn its allowance, await, and spawn again without bound, because its token
                // budget bounds its own calls and not its workers'.
                if (parent != null && limits.MaxWorkersPerOrchestrator > 0
                    && parent.WorkersSpawned >= limits.MaxWorkersPerOrchestrator)
                {
                    problem = String.Format(CultureInfo.InvariantCulture,
                        "Agent '{0}' has already spawned {1} workers, which is its limit "
                        + "(Agents:Limits:MaxWorkersPerOrchestrator). Await the ones it has and "
                        + "compose what they found.",
                        parent.Id, parent.WorkersSpawned);
                    return false;
                }

                var now = _clock.GetUtcNow();
                var id = NextId(now);

                // A caller's own figure is honoured up to the operator's ceiling and clamped past
                // it, rather than refused: the request is answerable, just not at that price, and a
                // 400 would make a caller guess at a number it cannot read. What it actually got is
                // on the record it gets back.
                var budget = spawn.TokenBudget is > 0 ? spawn.TokenBudget.Value : limits.DefaultTokenBudget;
                if (limits.MaxTokenBudget > 0 && budget > limits.MaxTokenBudget)
                {
                    // Remembered rather than logged here. Logging under this lock contradicted the
                    // third reason on this method's own doc, 55 lines above, in the one method that
                    // states it: a log provider is somebody else's code and may block, and holding
                    // the registry across it stalls every read and every transition on the host.
                    clampedFrom = budget;
                    budget = limits.MaxTokenBudget;
                }

                agent = new AgentRecord(id, spawn.Role, spawn.Task, _journal.MaxSteps)
                {
                    Name = String.IsNullOrWhiteSpace(spawn.Name) ? id : spawn.Name.Trim(),
                    ParentId = spawn.ParentId,
                    Parent = spawn.ParentId == null ? null : _agents[spawn.ParentId],
                    TokenBudget = budget,
                    CreatedUtc = now,
                    LastActivityUtc = now,
                    HostInstanceId = HostInstanceId,
                    Depth = depth,
                };

                // Counted on the parent BEFORE the child is reachable, and never decremented: the
                // number this bounds is how many an orchestrator has spawned, which does not go
                // down when a worker finishes or is evicted.
                if (parent != null)
                {
                    parent.WorkersSpawned++;
                }

                _agents[id] = agent;
                admitted = agent.State;
            }

            if (clampedFrom != null)
            {
                _logger.LogInformation(
                    "A spawn asked for a {Asked} token budget; this host allows {Allowed} "
                    + "(Agents:Limits:MaxTokenBudget).", clampedFrom.Value, limits.MaxTokenBudget);
            }

            // Outside the lock, for the three reasons on this method's own doc. The state is
            // passed rather than read back off the record, which is what bounds the cost of
            // the placement to ordering alone.
            _journal.Spawned(agent, admitted);
            return true;
        }

        /// <summary>The agent, live or retained.</summary>
        public Boolean TryGet(String? id, out AgentRecord agent)
        {
            agent = null!;
            if (String.IsNullOrEmpty(id))
            {
                return false;
            }

            lock (_gate)
            {
                Evict();
                return _agents.TryGetValue(id, out agent!);
            }
        }

        /// <summary>
        ///   One agent as a reader sees it, built UNDER the lock. That is the difference between this
        ///   and calling <see cref="AgentRecord.Summarize" /> on a record from
        ///   <see cref="TryGet" />: <see cref="Finish" /> writes the state, the budget, the result,
        ///   the failure and two timestamps one after another, so a summary taken outside can show
        ///   an ending that is half recorded - a state of <c>completed</c> with no result, or a
        ///   <c>budgetExceeded</c> naming no budget. Every route that reports one agent uses this.
        ///   <para>
        ///     The COUNTERS are still read with interlocked operations rather than frozen, for the
        ///     reason <see cref="AgentRecord" /> states: they are written on the run's own thread
        ///     between model calls, and blocking a run to make a listing atomic is the wrong trade.
        ///     So a summary is coherent about how an agent ENDED and up to one step stale about what
        ///     it spent.
        ///   </para>
        /// </summary>
        public Boolean TrySummarize(String? id, out AgentSummary summary)
        {
            summary = null!;
            if (String.IsNullOrEmpty(id))
            {
                return false;
            }

            lock (_gate)
            {
                Evict();
                if (!_agents.TryGetValue(id, out var agent))
                {
                    return false;
                }

                summary = agent.Summarize(agent.FinishedUtc ?? _clock.GetUtcNow());
                return true;
            }
        }

        /// <summary>Everything this host knows about, newest first, which is the order a reviewer
        /// wants.</summary>
        public IReadOnlyList<AgentSummary> All()
        {
            lock (_gate)
            {
                Evict();
                return _agents.Values
                    .OrderByDescending(a => a.CreatedUtc)
                    .Select(a => a.Summarize(_clock.GetUtcNow()))
                    .ToList();
            }
        }

        /// <summary>The live children of one agent, for the cascade a cancel performs.</summary>
        /// <summary>
        ///   Every child of this agent that the registry still holds, live or finished, oldest
        ///   first.
        ///   <para>
        ///     What an orchestrator awaits over. Live-only would be wrong in both directions: a
        ///     worker that finished before the await was called would be dropped from the results,
        ///     and one that finished during the await would vanish from them. Retention bounds this
        ///     list, so a worker evicted before its orchestrator collected it is simply gone, which
        ///     is the same answer the listing gives.
        ///   </para>
        /// </summary>
        public IReadOnlyList<AgentRecord> Children(String parentId)
        {
            lock (_gate)
            {
                return _agents.Values
                    .Where(a => String.Equals(a.ParentId, parentId, StringComparison.Ordinal))
                    .OrderBy(a => a.CreatedUtc)
                    .ToList();
            }
        }

        public IReadOnlyList<AgentRecord> LiveChildren(String parentId)
        {
            lock (_gate)
            {
                return _agents.Values
                    .Where(a => String.Equals(a.ParentId, parentId, StringComparison.Ordinal)
                        && AgentStates.IsLive(a.State))
                    .ToList();
            }
        }

        /// <summary>Moves an agent to a live state. A terminal agent is not moved, and false says
        /// so.</summary>
        public Boolean Advance(String id, AgentState state)
        {
            if (AgentStates.IsTerminal(state))
            {
                throw new ArgumentException(
                    "An ending is recorded with Finish, which releases the agent's slot.", nameof(state));
            }

            AgentRecord? moved;
            lock (_gate)
            {
                if (!_agents.TryGetValue(id, out var agent) || AgentStates.IsTerminal(agent.State))
                {
                    return false;
                }

                if (agent.State == state)
                {
                    // Not a transition. Journaling it would put a step and an event in front of a
                    // reviewer for something that did not happen.
                    return true;
                }

                agent.State = state;
                agent.LastActivityUtc = _clock.GetUtcNow();
                moved = agent;
            }

            // Outside the lock, for the reasons on TryAdmit. The state that was SET is passed
            // rather than read back off the record: an ending landing in this window used to make
            // this step report the TERMINAL state, so a completed run's trace said it changed to
            // completed twice. The ordering window remains and is documented; the false state does
            // not, and that was the half that lied.
            _journal.StateChanged(moved, state);
            return true;
        }

        /// <summary>
        ///   Records how an agent ended and releases its slot. The FIRST ending wins: a cancel that
        ///   arrives while a completion is being recorded finds an agent that already said how it
        ///   ended, and leaves it alone. False means exactly that, and it is not an error.
        ///   <para>
        ///     <c>citations</c> is the mechanical citation count, and only the runner can supply
        ///     one: it is the only caller that has both the final text and the trace. A cancel
        ///     passes none, and none is NOT zero - an ending with no citation check carries no
        ///     citation counts at all, rather than reporting that an answer cited nothing.
        ///   </para>
        /// </summary>
        public Boolean Finish(String id, AgentState ending, String? resultText = null,
            String? failure = null, BudgetKind budget = BudgetKind.None,
            CitationCounts? citations = null)
        {
            if (!AgentStates.IsTerminal(ending))
            {
                throw new ArgumentException("Finish records an ending; use Advance for a live state.",
                    nameof(ending));
            }

            AgentRecord? finished = null;
            lock (_gate)
            {
                if (!_agents.TryGetValue(id, out var agent) || AgentStates.IsTerminal(agent.State))
                {
                    return false;
                }

                var now = _clock.GetUtcNow();
                agent.State = ending;
                agent.Budget = budget;
                agent.ResultText = resultText;
                agent.Failure = failure;
                agent.FinishedUtc = now;
                agent.LastActivityUtc = now;
                finished = agent;
            }

            // Journaled BEFORE the token is cancelled, so the ending is on the record before the
            // cancellation releases anything waiting on it, and outside the lock for the reasons
            // on TryAdmit.
            //
            // NOT a guarantee that the ending is the last step, and the honest version is worth
            // stating: a model call already in flight when a cancel arrives records its own step
            // when it returns, which lands after the ending, and the tool call that response asked
            // for can add another, because a call already sent to the graph is not undone.
            // Preventing that would mean holding this lock across an inference call. So a trace can
            // carry a short tail past its ending rather than exactly one step, and a reader
            // comparing the last step's kind against the state should expect it. See TryAdmit for
            // the bound.
            _journal.Finished(finished, citations);

            // Also outside: cancelling the token runs continuations, and one of those is the
            // runner's own finally, which calls back in here.
            finished.SignalCancellation();

            // AFTER the ending is journaled and the token is cancelled, so an orchestrator woken
            // by this reads a record that is already complete rather than one mid-transition.
            finished.SignalFinished();
            return true;
        }

        /// <summary>
        ///   Cancels an agent and its live descendants. Cooperative: the token is observed between
        ///   steps and is passed into the chat call and the tool call in flight, so a tool call
        ///   already sent to the graph is not undone. The count is how many agents were signalled,
        ///   which for an orchestrator includes its workers.
        /// </summary>
        public Boolean TryCancel(String id, out Int32 signalled)
        {
            signalled = 0;
            if (!TryGet(id, out var agent))
            {
                return false;
            }

            if (AgentStates.IsTerminal(agent.State))
            {
                // Not a failure. A caller cancelling something that just finished asked for a state
                // it already has, and reporting a conflict would make a race look like a mistake.
                return true;
            }

            foreach (var child in Descendants(id))
            {
                if (Finish(child.Id, AgentState.Cancelled, failure: "Its orchestrator was cancelled."))
                {
                    signalled++;
                }
            }

            if (Finish(id, AgentState.Cancelled, failure: "Cancelled by request."))
            {
                signalled++;
            }

            _logger.LogInformation("Agent {AgentId} cancelled, {Signalled} agents signalled.", id, signalled);
            return true;
        }

        /// <summary>Cancels everything, for a host that is shutting down.</summary>
        public void CancelAll(String reason)
        {
            foreach (var id in Ids())
            {
                Finish(id, AgentState.Cancelled, failure: reason);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CancelAll("The agent host is shutting down.");

            // The records are dropped but NOT disposed. A run that is still unwinding reads its own
            // token in a finally, and disposing the source under it turns an orderly shutdown into
            // an ObjectDisposedException from a getter. They are unreachable from here on and the
            // process is going away, so the sources go with it.
            lock (_gate)
            {
                _agents.Clear();
            }
        }

        private IReadOnlyList<String> Ids()
        {
            lock (_gate)
            {
                return _agents.Keys.ToList();
            }
        }

        /// <summary>
        ///   Every live agent below this one, deepest first, so a worker is cancelled before the
        ///   orchestrator that would otherwise be told its worker vanished. Bounded by the agent
        ///   count rather than by depth, because the tree is built by spawns this registry admitted
        ///   and a cycle is therefore impossible; the visited set is belt and braces.
        /// </summary>
        private IReadOnlyList<AgentRecord> Descendants(String rootId)
        {
            var found = new List<AgentRecord>();
            var seen = new HashSet<String>(StringComparer.Ordinal) { rootId };
            var frontier = new Queue<String>();
            frontier.Enqueue(rootId);

            while (frontier.Count > 0)
            {
                foreach (var child in LiveChildren(frontier.Dequeue()))
                {
                    if (seen.Add(child.Id))
                    {
                        found.Add(child);
                        frontier.Enqueue(child.Id);
                    }
                }
            }

            found.Reverse();
            return found;
        }

        /// <summary>
        ///   Drops finished agents that are past their retention, then trims the oldest finished
        ///   ones down to the ceiling. Called on every read rather than on a timer: a host nobody is
        ///   asking has nothing to evict, and a timer would be one more thing to shut down.
        /// </summary>
        private void Evict()
        {
            var limits = _options.Value.Limits;
            var now = _clock.GetUtcNow();

            var finished = _agents.Values
                .Where(a => AgentStates.IsTerminal(a.State) && a.FinishedUtc != null)
                .OrderBy(a => a.FinishedUtc)
                .ToList();

            var doomed = new List<AgentRecord>();
            if (limits.RetainFinishedMinutes > 0)
            {
                var cutoff = now.AddMinutes(-limits.RetainFinishedMinutes);
                doomed.AddRange(finished.Where(a => a.FinishedUtc < cutoff));
            }

            var surviving = finished.Count - doomed.Count;
            if (limits.MaxRetainedAgents > 0 && surviving > limits.MaxRetainedAgents)
            {
                doomed.AddRange(finished.Except(doomed).Take(surviving - limits.MaxRetainedAgents));
            }

            if (doomed.Count == 0)
            {
                return;
            }

            var gone = new HashSet<String>(StringComparer.Ordinal);
            foreach (var agent in doomed)
            {
                // Removed, not disposed: a run recording its own ending still holds this record and
                // reads its token. The token source is small and an ObjectDisposedException out of
                // a property getter is the worse trade, so the collector takes it.
                _agents.Remove(agent.Id);
                gone.Add(agent.Id);

                // Its own parent LINK goes, which is what makes "the collector takes it" true. A
                // chain of records held each other by reference, so evicting a worker while its
                // orchestrator was still reachable kept the orchestrator's whole trace alive
                // through it, and the grandparent's through that: megabytes of bounded buffers and
                // an undisposed token source per link, retained by a record already removed from
                // the listing. The comment above claimed the opposite.
                agent.Parent = null;
            }

            foreach (var survivor in _agents.Values)
            {
                if (survivor.Parent != null && gone.Contains(survivor.Parent.Id))
                {
                    // The other direction: a LIVE child outliving its orchestrator. Nothing is
                    // lost by dropping the link, because the spawn step it exists to write goes on
                    // a trace no route can reach once the parent is off the listing, so writing it
                    // retained the parent for a reader that gets a 404. ParentId stays, so the
                    // lineage a summary reports is unchanged.
                    survivor.Parent = null;
                }
            }
        }

        /// <summary>
        ///   Sortable, short and unique within this process: a UTC stamp plus a counter. Not a Guid,
        ///   because an id a person retypes into a cancel is worth keeping readable, and not a bare
        ///   counter, because two hosts' logs side by side would collide on "agent 3".
        /// </summary>
        private String NextId(DateTimeOffset now)
        {
            var next = ++_sequence;
            return String.Format(CultureInfo.InvariantCulture, "a{0}-{1}",
                now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), next);
        }
    }

    /// <summary>What a caller asked for. Notably NOT a model: the instance owns that.</summary>
    public sealed class AgentSpawn
    {
        public AgentSpawn(String role, String task)
        {
            Role = role;
            Task = task;
        }

        public String Role
        {
            get;
        }

        public String Task
        {
            get;
        }

        public String? Name
        {
            get; set;
        }

        /// <summary>Null takes <c>Agents:Limits:DefaultTokenBudget</c>.</summary>
        public Int32? TokenBudget
        {
            get; set;
        }

        /// <summary>Set only for a worker an orchestrator spawned.</summary>
        public String? ParentId
        {
            get; set;
        }

        /// <summary>Appended to the role prompt. It cannot replace it: the role prompt is what makes
        /// the agent honest about what it did and did not call; see RoleCatalog for what a prompt
        /// may and may not be credited with.</summary>
        public String? SystemPromptAppendix
        {
            get; set;
        }
    }

    /// <summary>
    ///   One agent's mutable state, with two writers and two disciplines.
    ///
    ///   <para>
    ///     The STATE fields (state, budget, result, failure, the timestamps) are written only by
    ///     <see cref="AgentRegistry" /> and only under its lock, which is what makes an ending
    ///     final.
    ///   </para>
    ///   <para>
    ///     The COUNTERS are written directly by the per-agent meter, with interlocked operations and
    ///     no lock at all, because they are updated between every model call on the run's own thread
    ///     while a listing reads them. A reader seeing a step-old number is fine; blocking the run
    ///     to avoid that is not. The consequence is stated rather than hidden: a summary is not an
    ///     atomic snapshot, so an agent's counters and its state can be one step out of step with
    ///     each other.
    ///   </para>
    /// </summary>
    public sealed class AgentRecord : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();

        private readonly TaskCompletionSource<Boolean> _finished =
            new TaskCompletionSource<Boolean>(TaskCreationOptions.RunContinuationsAsynchronously);

        internal AgentRecord(String id, String role, String task, Int32 maxTraceSteps)
        {
            Id = id;
            Role = role;
            Task = task;
            Name = id;
            Trace = new AgentTrace(maxTraceSteps);
        }

        /// <summary>
        ///   What this agent did, bounded. Lives here rather than in a store of its own so it is
        ///   evicted exactly when the agent is: a trace whose agent has been forgotten is a leak
        ///   nobody would notice, since this process keeps nothing durable.
        /// </summary>
        public AgentTrace Trace
        {
            get;
        }

        /// <summary>
        ///   How deep in a swarm this agent sits: 0 for one a caller spawned, one more than its
        ///   parent for a worker. Stamped at admission from the parent's own depth rather than
        ///   computed by walking up, because an ancestor can be evicted while this agent still
        ///   runs and a walk would then report a depth that flatters the tree.
        /// </summary>
        public Int32 Depth
        {
            get; internal set;
        }

        /// <summary>
        ///   How many workers this agent has spawned over its whole life, which is what
        ///   <c>Agents:Limits:MaxWorkersPerOrchestrator</c> bounds. Never decremented, so a
        ///   finished or evicted worker still counts against the orchestrator that created it;
        ///   mutated only under the registry's lock.
        /// </summary>
        public Int32 WorkersSpawned
        {
            get; internal set;
        }

        /// <summary>
        ///   The agent that spawned this one, for the spawn step that belongs on ITS trace. Null for
        ///   anything a caller spawned, and null again once the parent is evicted.
        ///
        ///   <para>
        ///     A reference rather than an id looked up on demand, because the spawn step is written
        ///     once and the lookup would be a second chance to get the lifetime wrong. The
        ///     reference is CLEARED on eviction, in both directions, because nothing cleared it: a
        ///     record held its parent, which held its own, so a retained record would keep a whole
        ///     ancestry of bounded traces and undisposed token sources alive after the listing had
        ///     forgotten them, while <c>Evict</c> claimed the collector took them.
        ///   </para>
        ///   <para>
        ///     <b>Latent rather than live, and the difference is worth stating.</b> No shipped path
        ///     supplies a parent today: the spawn route refuses a caller-supplied <c>parentId</c>
        ///     with a 400, and the orchestrator's swarm tool that will supply one is Phase 4. So
        ///     <see cref="Parent" /> is null on every record a deployment currently holds and the
        ///     retention cannot occur; only the registry's own tests build a chain. It is fixed
        ///     ahead of the phase that makes it reachable rather than after, because an eviction
        ///     contract that depends on an unrelated route's validation to be true is one route
        ///     change away from being false. The reason previously given for keeping the link, that
        ///     a spawn step is worth writing to an evicted parent anyway, was wrong on its own
        ///     terms: no route can read that parent's trace.
        ///     <see cref="AgentRecord.ParentId" /> is what survives, and it is what a summary
        ///     reports.
        ///   </para>
        /// </summary>
        public AgentRecord? Parent
        {
            get; internal set;
        }

        public String Id
        {
            get;
        }

        public String Role
        {
            get;
        }

        public String Task
        {
            get;
        }

        public String Name
        {
            get; internal set;
        }

        public String? ParentId
        {
            get; internal set;
        }

        public AgentState State
        {
            get; internal set;
        }

        public BudgetKind Budget
        {
            get; internal set;
        }

        public Int32 TokenBudget
        {
            get; internal set;
        }

        public String? ResultText
        {
            get; internal set;
        }

        public String? Failure
        {
            get; internal set;
        }

        public DateTimeOffset CreatedUtc
        {
            get; internal set;
        }

        public DateTimeOffset LastActivityUtc
        {
            get; internal set;
        }

        public DateTimeOffset? FinishedUtc
        {
            get; internal set;
        }

        public String HostInstanceId
        {
            get; internal set;
        } = String.Empty;

        /// <summary>
        ///   Cancelled when this agent ends, for whatever reason. The runner passes it into the chat
        ///   call and the tool call.
        ///   <para>
        ///     Answers an already-cancelled token rather than throwing if this record's source has
        ///     been disposed. A disposed record is one that ended, and "this agent is over" is the
        ///     truthful answer to the question; an <see cref="ObjectDisposedException" /> out of a
        ///     property getter would surface as a failed run whose reason is about object lifetimes.
        ///   </para>
        /// </summary>
        public CancellationToken Cancellation
        {
            get
            {
                try
                {
                    return _cancellation.Token;
                }
                catch (ObjectDisposedException)
                {
                    return new CancellationToken(canceled: true);
                }
            }
        }

        /// <summary>
        ///   What this agent has spent. Interlocked rather than lock-protected because the runner
        ///   adds to these on its own thread between steps while a listing reads them, and a reader
        ///   seeing a step-old number is fine where blocking the runner is not.
        /// </summary>
        public Int64 InputTokens;

        public Int64 OutputTokens;

        public Int64 Steps;

        public Int64 ToolCalls;

        /// <summary>True once a backend answered a step without reporting usage. Carried rather than
        /// estimated: a token count nobody measured is worse than a missing one, because a budget
        /// enforced on a guess stops a run for a reason that never happened.</summary>
        public Boolean UnreportedUsage
        {
            get; internal set;
        }

        /// <summary>
        ///   Adds one step's measurements and returns the running token total.
        ///   <para>
        ///     The return value is a convenience, and NOT what any budget is compared against: both
        ///     call sites discard it, and the comparison happens in
        ///     <see cref="AgentBudgetChatClient" /> from its own fresh reads BEFORE the next call.
        ///     This said the runner compares it, which named the wrong component, the wrong moment
        ///     and a consumer that does not exist; the meter's own class doc is the one home for how
        ///     a cap is enforced.
        ///   </para>
        /// </summary>
        public Int64 CountStep(Int64 inputTokens, Int64 outputTokens, Boolean usageReported)
        {
            Interlocked.Increment(ref Steps);
            var input = Interlocked.Add(ref InputTokens, inputTokens);
            var output = Interlocked.Add(ref OutputTokens, outputTokens);
            if (!usageReported)
            {
                UnreportedUsage = true;
            }

            return input + output;
        }

        public Int64 CountToolCall()
        {
            return Interlocked.Increment(ref ToolCalls);
        }

        public AgentSummary Summarize(DateTimeOffset now)
        {
            var input = Interlocked.Read(ref InputTokens);
            var output = Interlocked.Read(ref OutputTokens);

            return new AgentSummary
            {
                Id = Id,
                Name = Name,
                Role = Role,
                Task = Task,
                ParentId = ParentId,
                State = AgentStates.Wire(State),
                Budget = State == AgentState.BudgetExceeded ? AgentStates.Wire(Budget) : null,
                TokenBudget = TokenBudget,
                InputTokens = input,
                OutputTokens = output,
                TotalTokens = input + output,
                UnreportedUsage = UnreportedUsage,
                Steps = Interlocked.Read(ref Steps),
                ToolCalls = Interlocked.Read(ref ToolCalls),
                DurationMs = (Int64)((FinishedUtc ?? now) - CreatedUtc).TotalMilliseconds,
                CreatedAt = CreatedUtc,
                LastActivityAt = LastActivityUtc,
                FinishedAt = FinishedUtc,
                Result = ResultText,
                Failure = Failure,
                HostInstanceId = HostInstanceId,
            };
        }

        /// <summary>
        ///   Completes when this agent reaches an ending, whatever the ending is. What an
        ///   orchestrator awaits, so <c>await_workers</c> needs no polling and no scheduler of its
        ///   own: the framework still runs every agent's loop and this is only the signal that one
        ///   has stopped.
        ///   <para>
        ///     <c>RunContinuationsAsynchronously</c>, deliberately. Without it the thread that
        ///     finishes a worker would run the awaiting orchestrator's continuation inline, which
        ///     on the cancel path is a thread already inside the registry's cascade cancel, walking a
        ///     list of descendants. The registry's own rule about not running other people's work
        ///     on its threads applies to this just as it does to the journal.
        ///   </para>
        /// </summary>
        public Task Finished => _finished.Task;

        internal void SignalCancellation()
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The agent was evicted while it was ending. Nothing to signal.
            }
        }

        /// <summary>Releases whoever is awaiting this agent. Called once, by the registry, after
        /// the ending is on the record.</summary>
        internal void SignalFinished()
        {
            _finished.TrySetResult(true);
        }

        public void Dispose()
        {
            // Released before the source goes, so an orchestrator awaiting a worker that is being
            // evicted mid-await is woken rather than left on a task nothing will ever complete.
            SignalFinished();
            _cancellation.Dispose();
        }
    }

    /// <summary>One agent as a listing shows it. A flat record on purpose: this is what a reviewer
    /// scans, and a nested cost object would put the number they came for one level down.</summary>
    public sealed class AgentSummary
    {
        public String Id { get; set; } = String.Empty;

        public String Name { get; set; } = String.Empty;

        public String Role { get; set; } = String.Empty;

        public String Task { get; set; } = String.Empty;

        public String? ParentId
        {
            get; set;
        }

        public String State { get; set; } = String.Empty;

        /// <summary>Which budget ended it, present only when the state is <c>budgetExceeded</c>.</summary>
        public String? Budget
        {
            get; set;
        }

        public Int32 TokenBudget
        {
            get; set;
        }

        public Int64 InputTokens
        {
            get; set;
        }

        public Int64 OutputTokens
        {
            get; set;
        }

        public Int64 TotalTokens
        {
            get; set;
        }

        /// <summary>True when at least one step's usage was not reported, so the totals above are a
        /// floor rather than a measurement.</summary>
        public Boolean UnreportedUsage
        {
            get; set;
        }

        public Int64 Steps
        {
            get; set;
        }

        public Int64 ToolCalls
        {
            get; set;
        }

        public Int64 DurationMs
        {
            get; set;
        }

        public DateTimeOffset CreatedAt
        {
            get; set;
        }

        public DateTimeOffset LastActivityAt
        {
            get; set;
        }

        public DateTimeOffset? FinishedAt
        {
            get; set;
        }

        public String? Result
        {
            get; set;
        }

        public String? Failure
        {
            get; set;
        }

        /// <summary>Which run of the host produced this. Nothing here survives a restart, so a
        /// reader comparing two listings needs it.</summary>
        public String HostInstanceId { get; set; } = String.Empty;
    }
}
