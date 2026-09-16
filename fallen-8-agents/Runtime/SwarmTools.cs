// MIT License
//
// SwarmTools.cs
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
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace NoSQL.GraphDB.Agents.Runtime
{
    /// <summary>
    ///   The two tools an orchestrator delegates with (feature agent-host, spec 3.5), built per
    ///   orchestrator because each one may only spawn and await ITS OWN workers.
    ///
    ///   <para>
    ///     <b>A worker is a first-class agent, not a sub-call.</b> It is admitted to the same
    ///     registry, gets the <c>worker</c> role's prompt and allowlist, its own token budget, its
    ///     own trace and its own feed events, and it is listed and cancellable like anything else.
    ///     That is what makes a swarm reviewable: the orchestrator's answer is one row and every
    ///     worker's work is there beside it.
    ///   </para>
    ///   <para>
    ///     <b>Why these are tools rather than a framework workflow.</b> Spec 3.5 asked for the
    ///     framework's concurrent or handoff patterns and no bespoke scheduler. Measured against
    ///     <c>Microsoft.Agents.AI.Workflows</c> 1.20.0, those patterns fix their participants when
    ///     the workflow is BUILT: the concurrent builder broadcasts the same messages to every
    ///     participant, and the Magentic builder puts its own LLM manager in charge of a set given
    ///     up front. Neither expresses <c>spawn_worker(task)</c>, where the orchestrator's own
    ///     model decides mid-run how many workers there are and what each one's task is, which is
    ///     the contract the same section specifies and which the role prompts are written for. So
    ///     the tools are ours and no scheduler is: the framework still runs every agent's loop,
    ///     including each worker's, and awaiting is one <c>Task.WhenAll</c> over completion
    ///     signals the registry already has to raise.
    ///   </para>
    ///   <para>
    ///     Caps are NOT checked here. <see cref="AgentRegistry.TryAdmit" /> enforces
    ///     <c>MaxConcurrentAgents</c>, <c>MaxSwarmDepth</c> and
    ///     <c>MaxWorkersPerOrchestrator</c>, and a breach comes back as a refusal this class hands
    ///     to the model as a tool error. Checking them here as well would be a second opinion that
    ///     can disagree with the first.
    ///   </para>
    /// </summary>
    public sealed class SwarmTools
    {
        /// <summary>The tool an orchestrator spawns a worker with.</summary>
        public const String SpawnWorker = "spawn_worker";

        /// <summary>The tool an orchestrator collects its workers' results with.</summary>
        public const String AwaitWorkers = "await_workers";

        private readonly AgentRegistry _registry;
        private readonly RoleCatalog _roles;
        private readonly AgentRunner _runner;
        private readonly AgentRecord _orchestrator;

        public SwarmTools(AgentRegistry registry, RoleCatalog roles, AgentRunner runner,
            AgentRecord orchestrator)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _roles = roles ?? throw new ArgumentNullException(nameof(roles));
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
            _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        }

        /// <summary>
        ///   The two tools, ready to be appended to the orchestrator's filtered tool list. Appended
        ///   rather than allowlisted, because the allowlist narrows what the MCP server advertises
        ///   and these are not MCP tools; <see cref="RoleCatalog" /> says so where the allowlist is
        ///   declared.
        /// </summary>
        public IReadOnlyList<AITool> Tools()
        {
            return new AITool[]
            {
                AIFunctionFactory.Create(Spawn, new AIFunctionFactoryOptions
                {
                    Name = SpawnWorker,
                    Description = "Spawns a worker agent to do one separable part of your task, and "
                        + "returns its id immediately. The worker runs on its own; call "
                        + AwaitWorkers + " to collect what it found. Delegate only a part that is "
                        + "genuinely independent.",
                }),
                AIFunctionFactory.Create(Await, new AIFunctionFactoryOptions
                {
                    Name = AwaitWorkers,
                    Description = "Waits for the workers you spawned and returns each one's typed "
                        + "result: its state, its answer, its citation counts and what it spent. "
                        + "Returns immediately for a worker that has already finished.",
                }),
            };
        }

        /// <summary>
        ///   Spawns one worker. Returns its id, or the registry's refusal as TEXT rather than
        ///   throwing: a cap the operator set is something the model can act on (delegate less,
        ///   await what it has), and a thrown exception would end the orchestrator's turn instead.
        ///   The framework surfaces the returned text to the model, and the trace records the call
        ///   with <c>success</c> false via the runner's invoker.
        /// </summary>
        [Description("Spawns a worker agent for one separable part of the task.")]
        private String Spawn(
            [Description("What this worker should find out. One self-contained part of your task.")]
            String task,
            [Description("An optional short name for the worker, for a human reading the listing.")]
            String? name = null)
        {
            if (String.IsNullOrWhiteSpace(task))
            {
                return "A worker needs a task: say what it should find out.";
            }

            var spawn = new AgentSpawn("worker", task.Trim())
            {
                Name = name,
                ParentId = _orchestrator.Id,
            };

            if (!_registry.TryAdmit(spawn, out var worker, out var problem))
            {
                // The operator's cap, in the operator's words, handed to the model. It is the one
                // reader that can do something about it on this turn.
                return problem;
            }

            if (!_roles.TryGet(worker.Role, out var role, out var roleProblem))
            {
                _registry.Finish(worker.Id, AgentState.Failed, failure: roleProblem);
                return roleProblem;
            }

            _runner.Start(worker, role, systemPromptAppendix: null);
            return String.Format(CultureInfo.InvariantCulture,
                "Worker {0} started. Call {1} to collect its result.", worker.Id, AwaitWorkers);
        }

        /// <summary>
        ///   Waits for workers and returns their typed results.
        /// </summary>
        /// <param name="ids">
        ///   The workers to wait for, or null for every worker this orchestrator has spawned.
        /// </param>
        /// <param name="cancellationToken">
        ///   The orchestrator's own, so its deadline and a cancel both end the wait. Supplied by
        ///   the framework's tool invocation, which is why this needs no timeout of its own: a
        ///   worker that never finishes is bounded by the orchestrator's <c>MaxRunSeconds</c>,
        ///   which is the same bound its other tool calls are under.
        /// </param>
        private async Task<IReadOnlyList<WorkerResult>> Await(
            [Description("The worker ids to wait for. Omit to wait for all of your workers.")]
            IEnumerable<String>? ids = null,
            CancellationToken cancellationToken = default)
        {
            var wanted = Resolve(ids);
            if (wanted.Count == 0)
            {
                return Array.Empty<WorkerResult>();
            }

            // One WhenAll over signals the registry raises at an ending anyway. No polling, no
            // queue, no dispatch: the framework is still running each worker's own loop.
            await Task.WhenAll(wanted.Select(w => w.Finished)).WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            return wanted.Select(Describe).ToList();
        }

        /// <summary>
        ///   Which workers a call means. An id that is not this orchestrator's own is dropped
        ///   rather than awaited: a model that invents an id, or names another agent's worker,
        ///   would otherwise wait on something it has no claim to, and on a busy host that is a
        ///   wait for somebody else's work.
        /// </summary>
        private IReadOnlyList<AgentRecord> Resolve(IEnumerable<String>? ids)
        {
            var mine = _registry.Children(_orchestrator.Id);
            if (ids == null)
            {
                return mine;
            }

            var named = new HashSet<String>(ids.Where(i => !String.IsNullOrWhiteSpace(i)),
                StringComparer.Ordinal);
            return named.Count == 0 ? mine : mine.Where(w => named.Contains(w.Id)).ToList();
        }

        /// <summary>
        ///   One worker as its orchestrator sees it. The citation counts come off the worker's own
        ///   trace through <see cref="AgentTrace.Citations" />, the one home for that read, so an
        ///   orchestrator and the detail route cannot report different numbers for the same run.
        /// </summary>
        private WorkerResult Describe(AgentRecord worker)
        {
            // Through the registry, so the duration and the counters are stamped from ITS clock
            // and read under ITS lock, exactly as the listing and the detail route are. A summary
            // built here would be a second reader with its own idea of "now".
            if (!_registry.TrySummarize(worker.Id, out var summary))
            {
                // Evicted between the await returning and this read. Its id and state are still
                // on the record this holds, which is more use to an orchestrator than nothing.
                return new WorkerResult
                {
                    Id = worker.Id,
                    State = AgentStates.Wire(worker.State),
                    Result = worker.ResultText,
                    Failure = worker.Failure ?? "This worker was evicted before its result was "
                        + "collected (Agents:Limits:RetainFinishedMinutes).",
                };
            }

            var citations = worker.Trace.Citations();

            return new WorkerResult
            {
                Id = summary.Id,
                State = summary.State,
                Result = worker.ResultText,
                Failure = worker.Failure,
                ValidCitations = citations?.Valid,
                DanglingCitations = citations?.Dangling,
                Tokens = summary.TotalTokens,
                Steps = summary.Steps,
                ToolCalls = summary.ToolCalls,
            };
        }
    }

    /// <summary>
    ///   One worker's result as its orchestrator receives it: TYPED, never free prose about what
    ///   the worker did.
    ///
    ///   <para>
    ///     The shape is the point. An orchestrator that received prose would have to parse it, and
    ///     a model parsing another model's prose is where a swarm starts inventing; and because
    ///     the orchestrator is the only composer, anything it cannot read reliably it would
    ///     paraphrase. So it gets the answer, the state, the citation counts and the cost, and the
    ///     mechanics (names, spawns, awaits) stay in the feed and the trace where the role prompts
    ///     say they belong.
    ///   </para>
    /// </summary>
    public sealed class WorkerResult
    {
        [JsonPropertyName("id")]
        public String Id { get; set; } = String.Empty;

        [JsonPropertyName("state")]
        public String State { get; set; } = String.Empty;

        /// <summary>What the worker answered, or null if it did not answer.</summary>
        [JsonPropertyName("result")]
        public String? Result
        {
            get; set;
        }

        /// <summary>Why it did not, when it did not. Present so an orchestrator can say a part
        /// failed rather than substituting a plausible number for it.</summary>
        [JsonPropertyName("failure")]
        public String? Failure
        {
            get; set;
        }

        [JsonPropertyName("validCitations")]
        public Int32? ValidCitations
        {
            get; set;
        }

        [JsonPropertyName("danglingCitations")]
        public Int32? DanglingCitations
        {
            get; set;
        }

        [JsonPropertyName("tokens")]
        public Int64 Tokens
        {
            get; set;
        }

        [JsonPropertyName("steps")]
        public Int64 Steps
        {
            get; set;
        }

        [JsonPropertyName("toolCalls")]
        public Int64 ToolCalls
        {
            get; set;
        }
    }
}
