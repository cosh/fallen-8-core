# Fallen-8 Agent Host — Specification

> **Status:** Draft, spec only (no implementation yet). Follow the feature workflow in the
> repository root `CLAUDE.md`. Feature branch: `feature/agent-host` (branch-only workflow —
> no GitHub issue/PR).
>
> **Depends on:** [mcp-server](../mcp-server/spec.md) (phases 0–2). Agents reach the graph
> exclusively through the `fallen-8-mcp` tools; this feature adds **no** second bridge.
> **Companions:** [skill-library](../skill-library/spec.md) (procedural knowledge the agents
> can be primed with) and [nl-assist-finetune](../nl-assist-finetune/spec.md) (a specialized
> local model an operator may point agents at). Neither blocks this feature.

## 1. Overview & requirements

Fallen-8 gains a way to **run AI agents against the graph as a first-class, observable
workload**: spawn an agent on a task, converse with it, watch what it does, see exactly what
it costs, and compose agents into orchestrator/worker swarms.

Requirements fixed up front (user-stated):

1. **Separate process.** The agent runtime is its own deployable, never in-process with the
   database or the apiApp.
2. **Local MCP.** Agents use a locally deployed MCP server to operate on Fallen-8 — that is
   the [mcp-server](../mcp-server/spec.md) feature; this feature is its first in-repo client.
3. **Local phi model.** The default model is **Phi-4-mini via Ollama** — the same MIT-only,
   local-first model posture the NL assist established. Model name and endpoint are config.
4. **User ↔ agent communication** plus **subscribable events**: a user can send messages to
   a running agent and subscribe to a live event stream of everything agents do.
5. **Spawn & review interface:** an API to start agents on specific tasks, list them, and
   inspect **at any time** what each is doing and **how many tokens it has consumed**.
6. **Swarm mode:** orchestrator agents that decompose a task and spawn worker agents.
7. **No home-grown agent framework.** The agentic loop, tool invocation, and multi-agent
   orchestration come from an established MIT-licensed framework.

### Framework choice (requirement 7)

**Microsoft Agent Framework** (`Microsoft.Agents.AI`, MIT, v1.0, .NET-first — the merged
successor of AutoGen and Semantic Kernel's agent work). It provides exactly the pieces this
feature must not reinvent:

- `AIAgent`/`ChatClientAgent` over any `Microsoft.Extensions.AI.IChatClient` — so the model
  backend is swappable config (Ollama today, anything OpenAI-compatible tomorrow).
- **Native MCP tool support** — an MCP client's tools plug into an agent directly, which is
  precisely the `fallen-8-mcp` integration point.
- **Workflows/orchestration** (sequential, concurrent, handoff, group chat) — the swarm
  primitives for requirement 6.
- Token usage surfaced per response (`UsageDetails`) and OpenTelemetry GenAI instrumentation
  — the raw material for requirement 5.

Considered and rejected: hand-rolled loop over `IChatClient` (violates requirement 7);
LangChain-style Python stacks (wrong runtime for this solution); Semantic Kernel alone
(its agent story is folded into Agent Framework).

## 2. Goals / non-goals

**Goals**

- A new **`fallen-8-agents`** project (net10.0, ASP.NET Core, namespace
  `NoSQL.GraphDB.Agents`): the agent host process.
- **Agent lifecycle API:** spawn an agent on a task, list all agents with live state and
  token totals, inspect one agent's full activity trace, message it, cancel it.
- **Event feed:** a Server-Sent-Events stream of agent events (spawned, state changed,
  message, tool call, completed/failed), following the change-feed's SSE conventions.
- **Honest accounting:** per-agent input/output token counters updated from real model
  usage data, per-agent token **budgets** that stop a runaway agent, and OTel metrics in the
  observability feature's style.
- **Swarm mode:** an `orchestrator` role whose extra tools spawn and await `worker` agents,
  with hard caps (count, depth, budget) and cascade cancellation.
- **Least-privilege graph access:** agents see only the MCP tool tiers the operator enabled
  on `fallen-8-mcp` (read-only by default, per that spec's tier defaults).
- **Own container image + opt-in compose service** (`--profile agents`), configured by
  environment; startup posture logging in the apiApp's honest style.

**Non-goals**

- **A new agent framework, planner, or memory system** — Agent Framework as-is; where it has
  a primitive, we use it.
- **Embedding agents in `fallen-8-core` or the apiApp** — excluded by requirement 1. Neither
  project changes.
- **A second graph bridge.** No REST client to the apiApp in this project; if agents need a
  capability, it becomes an MCP tool on `fallen-8-mcp` first.
- **Durable agents.** Agents are in-memory; a host restart ends them (the trace says so).
  *Revisit trigger:* users routinely run agents that must survive deploys.
- **Multi-node swarms / distributed scheduling.** One host process, one swarm.
  *Revisit trigger:* a single host's model throughput becomes the bottleneck.
- **Token-by-token streaming to clients.** The feed streams per-step events, not deltas.
  *Revisit trigger:* an interactive UI needs typed-out responses.
- **A web UI.** This feature is the API; an F8-Studio panel is a natural follow-up feature.
- **Sandboxing agent behaviour.** Tool tiers bound what agents *can* do; what they *choose*
  to do within those tools is model behaviour (see Risks: prompt injection).

## 3. Design sketch

### 3.1 Project & solution shape

```
fallen-8-agents/                 (new project, net10.0, ASP.NET Core)
  Program.cs                     host, posture log, DI wiring
  Configuration/AgentHostOptions.cs
  Runtime/AgentRegistry.cs       in-memory registry: id → AgentHandle
  Runtime/AgentRunner.cs         one agent's run loop (Agent Framework), state machine
  Runtime/AgentTrace.cs          bounded per-agent step buffer
  Runtime/SwarmTools.cs          spawn_worker / await_workers tools (orchestrator role)
  Feed/AgentFeedDispatcher.cs    SSE fan-out (change-feed pattern)
  Controllers/AgentsController.cs
  Controllers/AgentFeedController.cs
  Diagnostics/AgentDiagnostics.cs  OTel meters/activity source
  Dockerfile
fallen-8-unittest/               agent-host tests live in the existing suite
```

MIT headers everywhere; `Try*(out, …)` where it fits; MSTest; warnings-as-errors and the
convention tests apply unchanged.

### 3.2 Agent model

- **Roles:** `assistant` (default — one agent, one task/conversation) and `orchestrator`
  (additionally gets the swarm tools; its workers are `assistant` agents with `ParentId`).
- **States:** `pending → running ⇄ waitingForUser → completed | failed | cancelled |
  budgetExceeded`. Every transition is a feed event and a trace step.
- **Spawn request:** `{ task, role?, name?, model?, tokenBudget?, systemPromptAppendix? }` —
  model defaults to `AgentHost:Model` (default `phi4-mini`), budget to
  `AgentHost:DefaultTokenBudgetTokens`.
- **The runner** builds a `ChatClientAgent` over the configured `IChatClient` (Ollama
  endpoint) with the MCP tools fetched from `fallen-8-mcp` at startup (tool list refreshed
  on reconnect), plus the swarm tools for orchestrators. The agent thread persists for the
  agent's lifetime, so `POST …/messages` continues one conversation.
- **Trace:** every step is recorded — `modelCall` (duration, usage delta), `toolCall`
  (tool name, arguments summary, result summary, success), `message` (direction), `spawn`,
  state changes. The buffer is bounded (`AgentHost:MaxTraceSteps`, default 1000, oldest
  dropped with a marker step) — review needs recency, not an unbounded archive.

### 3.3 Control-plane API (mirrors apiApp conventions: versioned route, problem+json, OpenAPI)

| Method & route | Purpose |
|---|---|
| `POST /api/v0.1/agents` | Spawn; returns the agent id + initial state |
| `GET /api/v0.1/agents` | All agents: id, name, role, state, task, parentId, tokens {input, output, total}, budget, createdAt, lastActivityAt |
| `GET /api/v0.1/agents/{id}` | One agent, incl. children ids and the last N trace steps |
| `GET /api/v0.1/agents/{id}/trace` | The full retained trace |
| `POST /api/v0.1/agents/{id}/messages` | User → agent message; 409 unless the agent can accept one (running/waitingForUser) |
| `DELETE /api/v0.1/agents/{id}` | Cancel; cascades to live descendants |
| `GET /agentfeed` | SSE stream (§3.4) |

The reply to a user message arrives on the feed (and in the trace) as an `agentMessage`
event — the POST returns 202 immediately; a small local model can take seconds to answer
and the control plane never blocks on inference.

### 3.4 Event feed

Same delivery contract family as the change feed (SSE, `id:`/`event:`/`data:`, keep-alive
comments, declarative query-string filters — here `agents` and `kinds`), same
parser-not-compiler stance. Event kinds:

`agentSpawned, agentStateChanged, agentMessage, toolCalled, agentCompleted, agentFailed`

Every event carries `{ seq, ts, agentId, parentId?, kind, … }`; `agentMessage` carries the
text and direction; `toolCalled` carries the tool name and a truncated argument/result
summary (never full payloads — re-fetch via the trace when needed). `agentStateChanged`
carries the token totals so a subscriber can render live cost without polling. No catch-up
buffer in v1 (`GET …/trace` is the catch-up mechanism); *revisit trigger:* a UI that must
survive reconnects without re-fetching traces.

### 3.5 Swarm mode

- Orchestrators get two extra function tools, implemented on the registry:
  - `spawn_worker(task, name?)` → worker agent id (a real agent: listed, traced, budgeted,
    cancellable like any other),
  - `await_workers(ids?)` → the workers' final results/states.
- **Caps, all config, all enforced in the registry (not by the model):**
  `MaxConcurrentAgents` (default 8), `MaxSwarmDepth` (default 2 — an orchestrator's workers
  cannot themselves orchestrate by default), `MaxWorkersPerOrchestrator` (default 8). A
  breached cap is a tool error the orchestrator sees and a `toolCalled(success=false)` event
  the user sees.
- Cancelling an orchestrator cancels its live descendants; a worker finishing feeds its
  result to the awaiting orchestrator via Agent Framework's primitives (handoff/concurrent
  patterns) — no bespoke scheduler.

### 3.6 Tokens & observability

- After every model call the usage delta (`UsageDetails.InputTokenCount/OutputTokenCount`)
  is added to the agent's counters; unreported usage (some backends omit it) is counted as
  zero and flagged once per agent in the trace — the numbers shown are **never estimated**.
- Budget check after each call: over budget ⇒ state `budgetExceeded`, loop stops, feed
  event. Orchestrator budgets bound only their own calls; the global cap is
  `MaxConcurrentAgents × DefaultTokenBudgetTokens` by construction.
- `AgentDiagnostics` (observability-feature style, containment included):
  `fallen8.agents.tokens` (counter, tags: direction, role), `fallen8.agents.active`
  (gauge), `fallen8.agents.completed` (counter, tag: outcome), `fallen8.agents.model.call.duration`
  (histogram), plus Agent Framework's own OTel GenAI spans wired into the exporter config.
  Tag hygiene rule (no user input in tag values) applies unchanged.

### 3.7 Security posture

- Binds loopback unless `AgentHost:AllowRemoteAccess=true`; API-key header auth in the
  apiApp's pattern (`AgentHost:ApiKey`, warn-and-run-unauthenticated when blank on loopback).
- The host holds exactly one downstream credential set: the MCP endpoint (+ bearer token
  when that feature's auth phases land). Agents never see credentials; tool tiers are
  decided on `fallen-8-mcp`, not here — read-only by default, and this spec recommends
  keeping agent-facing MCP servers read-only unless a task concretely needs writes.
- Startup posture log: bind, auth mode, model + endpoint, MCP target + reachable tiers,
  caps and default budget.

### 3.8 Configuration (`AgentHost:*`)

`Model` (default `phi4-mini`), `OllamaEndpoint` (default `http://localhost:11434`),
`McpEndpoint` (default `http://localhost:8090`), `McpBearerToken?`,
`DefaultTokenBudgetTokens` (default 100k), `MaxConcurrentAgents` (8), `MaxSwarmDepth` (2),
`MaxWorkersPerOrchestrator` (8), `MaxTraceSteps` (1000), `ApiKey?`, `AllowRemoteAccess`
(false), `FeedKeepAliveSeconds` (15).

### 3.9 Test harness

- **No live model in CI.** The runner is tested against a scripted `IChatClient` fake
  (deterministic replies, tool-call sequences, usage numbers) — lifecycle, trace, budgets,
  caps, cascade cancel, feed events are all assertable without Ollama.
- MCP tools mocked at the `AIFunction` seam for runner tests; one integration test wires a
  real in-process `fallen-8-mcp` (once that feature lands) to prove tool plumbing.
- A **gated** live smoke test (skips cleanly unless an Ollama endpoint is configured) runs
  one real phi agent end-to-end — the Phase 0 spike graduates into this test.
- Controller tests via `WebApplicationFactory`: API shapes, problem+json errors, SSE frame
  format, filter grammar (400 on junk, never silently-empty streams).

## 4. Acceptance criteria

- **Spawn/review round-trip.** Spawn an assistant on a task; `GET /agents` shows it running
  with growing token totals; the trace shows model and tool steps; the feed streamed the
  same as events; the final `agentCompleted` carries the result.
- **Conversation.** A `waitingForUser` agent accepts `POST …/messages` and the reply arrives
  as an `agentMessage` feed event on the same conversation thread.
- **Tokens are real.** Counters equal the sum of backend-reported usage exactly; a budget
  breach stops the agent with state `budgetExceeded` and a feed event.
- **Swarm.** An orchestrator spawns workers visible as first-class agents (parentId set);
  caps are enforced as tool errors; cancelling the orchestrator cancels its live workers;
  `await_workers` delivers results.
- **Least privilege.** With `fallen-8-mcp` at default tiers, an agent has read tools only;
  no credential ever appears in any trace, event, or log line.
- **Separate deployable.** Runs as its own process/container with no F8 assemblies loaded;
  `docker compose up` default is unchanged unless `--profile agents` is requested.
- **Suite green, build clean**, apiApp and engine untouched except the solution file.

## 5. Risks

- **Phi-4-mini tool calling through `IChatClient` is flaky** (known upstream issues:
  dotnet/extensions#7094, ollama/ollama#9437 — the model emits tool-call text that isn't
  always parsed into tool invocations). This is the feature's load-bearing risk, so it is
  **Phase 0 of the plan**: a spike proving tool-call round-trips on the pinned
  Ollama + Agent Framework versions, with the fallback ladder documented (fp16 quant →
  `phi4:14b` → any tool-capable local model via the same config knob). The feature does not
  proceed past Phase 0 until a default model demonstrably calls tools.
- **Prompt injection via graph data:** graph property values flow into agent context through
  tool results; hostile data can steer an agent. Mitigations: read-only default tiers,
  budgets, full traceability (every tool call is visible), and the mcp-server spec's
  standing guidance. Not solvable here; stated honestly.
- **Small-model quality:** phi may loop or stall on complex orchestration. Budgets +
  `MaxSwarmDepth` bound the damage; the model knob and the skill-library/fine-tune
  companions are the quality levers.
- **Framework velocity:** Agent Framework is v1 but young; pin exact versions, keep the
  runner behind our own thin `AgentRunner` seam so an API change is one file's blast radius.
- **License nuance:** Agent Framework is MIT; the MCP C# SDK (a dependency of both this and
  mcp-server) moved from MIT to **Apache-2.0** at 1.0. Permissive and compatible, but "all
  MIT" would be inaccurate — recorded here so the posture stays honest.

## 6. Keep (do not regress)

- **`fallen-8-core` and `fallen-8-core-apiApp` are untouched.** Agents are a client-side
  workload; every capability they need arrives as an MCP tool first.
- **The mcp-server trust chain (§3.5 there):** this host is just another MCP client; it
  never bypasses tiers or holds the F8 API key.
- **The observability containment rule** (instrument callbacks never fault the observed) and
  tag hygiene, applied to every new meter.
- **The change-feed SSE conventions** — one house style for event streams, not two.
- **Compose default behaviour** and the repo's test bar (MSTest, edge cases, no live-model
  dependence in CI).
