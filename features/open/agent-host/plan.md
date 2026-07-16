# Fallen-8 Agent Host — Plan

Companion to [spec.md](./spec.md). A separate deployable that runs agents (Microsoft Agent
Framework, MIT) on a local phi model (Ollama) against Fallen-8 via `fallen-8-mcp`.
Feature branch: `feature/agent-host` (branch-only workflow — no GitHub issue/PR).

**Hard dependency:** [mcp-server](../mcp-server/plan.md) phases 0–2 must land first — the
agents' only graph access is that server's tools. Phase 0 here can run before it (the spike
needs any MCP server or a stub tool, not F8 specifically).

Ordering principle: kill the load-bearing risk first (phi tool calling), then a walking
skeleton with one observable agent, then the review surface (feed + trace + tokens), then
swarm, then packaging. Every phase lands with its tests; CI never needs a live model.

## Phase 0 — Spike: phi tool-calling reality check (GATE)

Intent: prove the default model actually invokes tools through the pinned stack, before any
product code depends on it.

- [ ] Throwaway console harness (not merged as product code): `Microsoft.Agents.AI` +
  Ollama `IChatClient` + one trivial local `AIFunction` and one MCP tool.
- [ ] Matrix: `phi4-mini` (default quant), `phi4-mini:3.8b-fp16`, `phi4:14b` — single and
  parallel tool calls, multi-turn with tool results fed back.
- [ ] Record in the feature README: pinned package + Ollama versions, which model reliably
  round-trips tool calls (upstream refs: dotnet/extensions#7094, ollama/ollama#9437), the
  chosen default, and the fallback ladder.
- [ ] **Gate:** a default model that demonstrably calls tools, or the feature stops here and
  the spec's model requirement is renegotiated.
- [ ] The working harness graduates into the gated live smoke test in Phase 1.

## Phase 1 — Scaffold & single-agent walking skeleton

Intent: one spawnable, listable, cancellable agent end-to-end, deterministic in CI.

- [ ] New `fallen-8-agents` project (net10.0) in `fallen-8-core.sln`; MIT headers; exact
  package versions (`Microsoft.Agents.AI`, Ollama/OpenAI-compatible `IChatClient` provider,
  `ModelContextProtocol` client bits) pinned.
- [ ] `AgentHostOptions` bound + validated; startup posture log (bind, auth, model +
  endpoint, MCP target, caps, default budget); loopback-by-default bind.
- [ ] `AgentRegistry` (in-memory, id → handle, state machine per spec §3.2) and
  `AgentRunner` wrapping `ChatClientAgent` + MCP tools fetched at startup.
- [ ] `AgentsController`: `POST /agents`, `GET /agents`, `GET /agents/{id}`,
  `DELETE /agents/{id}` — versioned route, problem+json, OpenAPI annotations.
- [ ] API-key auth middleware in the apiApp's pattern.
- [ ] Tests: scripted `IChatClient` fake (replies, tool calls, usage); lifecycle transitions,
  cancel, list/detail shapes, 404/409 paths. Gated live smoke test (skips without Ollama).

## Phase 2 — Trace, feed, and conversation

Intent: "review what they are doing all the time" — the observability half of the contract.

- [ ] `AgentTrace`: bounded step buffer (modelCall, toolCall, message, spawn, state change;
  drop-oldest marker); `GET /agents/{id}/trace`.
- [ ] `AgentFeedDispatcher` + `GET /agentfeed` (SSE): change-feed frame conventions,
  keep-alive, declarative `agents`/`kinds` filters (400 on junk), all six event kinds.
- [ ] `POST /agents/{id}/messages` (202; reply as `agentMessage` feed event on the same
  thread; 409 when the agent cannot accept input).
- [ ] Tests: SSE frame format + filter grammar via `WebApplicationFactory`; trace bounding;
  conversation round-trip on the fake client; tool-call summaries truncated, no payloads.

## Phase 3 — Tokens, budgets, metrics

Intent: honest cost accounting — the other half of the review contract.

- [ ] Usage deltas from `UsageDetails` accumulated per agent; unreported-usage flag step;
  totals on list/detail and on `agentStateChanged` events.
- [ ] Budget enforcement after each model call ⇒ `budgetExceeded` state + feed event.
- [ ] `AgentDiagnostics` meters (spec §3.6) with the observability containment + tag-hygiene
  rules; Agent Framework's OTel GenAI spans wired to the exporter config.
- [ ] Tests: counter exactness against scripted usage, budget stop, metrics emitted
  (MeterListener), no user input in tag values.

## Phase 4 — Swarm mode

Intent: orchestrator/worker composition on the framework's primitives, bounded by config.

- [ ] `SwarmTools`: `spawn_worker` / `await_workers` as registry-backed `AIFunction`s,
  attached only to `orchestrator` agents; workers are first-class agents with `ParentId`.
- [ ] Caps enforced in the registry (`MaxConcurrentAgents`, `MaxSwarmDepth`,
  `MaxWorkersPerOrchestrator`) — breach = tool error + `toolCalled(success=false)` event.
- [ ] Cascade cancel; worker results delivered to the awaiting orchestrator via Agent
  Framework handoff/concurrent patterns (no bespoke scheduler).
- [ ] Tests (fake client, scripted orchestrator): spawn visibility, every cap, depth limit,
  cascade cancel, await semantics, orchestrator budget independent of workers'.

## Phase 5 — Packaging, docs, land

Intent: ship what exists; leave the repo consistent.

- [ ] `Dockerfile` (sdk → aspnet runtime, house conventions); compose service under
  `profiles: [agents]` wired to the `mcp` profile service; default compose unchanged.
- [ ] Feature `README.md` (LIVING doc): quickstart (Ollama pull, compose profiles, spawn
  curl, subscribe curl), config reference, model fallback ladder from Phase 0, security
  posture (read-only-tiers recommendation, prompt-injection honesty note).
- [ ] Once landed: move `features/open/agent-host/` → `features/done/agent-host/`; check
  whether the skill library wants an "operate the agent host" skill (note there, not here).
- [ ] Full suite green, build clean (warnings-as-errors), convention tests pass for the new
  project.
