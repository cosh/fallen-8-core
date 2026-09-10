# Fallen-8 Agent Host: Plan

Companion to [spec.md](./spec.md). A separate deployable that runs agents (Microsoft Agent
Framework, MIT) against Fallen-8 via `fallen-8-mcp`, asking its Fallen-8 instance for every model
call so the instance's own provider selector decides where inference runs.
Feature branch: `feature/agent-host` (branch-only workflow: no GitHub issue or PR).

> Revised 2026-09-09 together with the spec, three times. The morning revision made Phase 0 a
> pinned-stack check rather than a "does phi call tools at all" gate, added an extraction phase
> for the apiApp's model transports, put the apiApp proxy beside the host, and added the caps,
> allowlists and typed worker results. The afternoon reversal of the model route replaced the
> extraction phase with **Phase 1a: the chat gateway learns to serve agents**, and the host's model
> code shrank to one adapter over `POST /chat`. The third revision replaced the `AgentModel` field
> with **model purposes** and the rename of `Model` to `Models:Assist`; the rename sweep lives in
> Phase 1a because the environment must keep working at every merge.

**Hard dependencies, all on `main`:** [mcp-server](../../done/mcp-server/plan.md) (the agents'
only graph access), [model-providers](../../done/model-providers/plan.md),
[nahil-backend](../../done/nahil-backend/plan.md) and
[chat-model-catalog](../../done/chat-model-catalog/plan.md) (the chat gateway Phase 1a grows),
[integrations](../../done/integrations/plan.md) (the proxy client base Phase 1b reuses).

Ordering principle: pin the stack and pick the default agent model first, then teach the gateway
tools and purposes before anything depends on them, then a walking skeleton with one observable
agent behind the proxy, then the review surface (feed, trace, counters), then caps and metrics,
then swarm, then packaging. Every phase lands with its tests; CI never needs a live model.

## Phase 0: pinned-stack check and default agent model (GATE, small) - DONE 2026-09-09

Intent: prove the pinned versions round-trip tool calls, and pick the default agent model with
numbers rather than hope. Not merged as product code. **The gate passed**; the measurements and
their consequences are recorded in [spec.md](./spec.md) section 5, and the two design rules they
produced are spec sections 3.1a (streaming needs no exception for tools) and 3.2a (the role
prompts are load-bearing).

- [x] Throwaway console project outside the repo tree: `Microsoft.Agents.AI` 1.20.0 +
  `Microsoft.Extensions.AI` 10.9.0 + `ModelContextProtocol` 1.4.1 on net10.0. Restored, built and
  ran green. Confirmed: `ChatClientAgent(client, instructions, name, description, tools)` runs the
  tool loop with no `UseFunctionInvocation` wiring; tools reach the chat client as
  `ChatOptions.Tools`; a session carries multiple turns; `AgentResponse` exposes
  `Text`/`Usage`/`Messages`/`FinishReason`. Two API corrections for the implementer:
  `ChatClientAgentOptions` has NO `Instructions` property (it lives on `ChatOptions`), and
  `AgentResponse.Usage` is the RUN aggregate, so per-step usage is counted at the adapter.
- [x] Live matrix against Nahil (`phi4-mini:latest`, temperature 0, one-tool schema): streamed and
  non-streamed, several prompt shapes, repeated runs. Result: streaming is not the variable, prompt
  shape is. Streamed is the cleaner shape. Full table in spec section 5.
- [x] Fallback ladder probed: no second rung is available on Nahil today (qwen3 resolves but no
  worker serves its class; every other tool-capable model 404s). Recorded as a Nahil-side ask.
- [x] One defect found and scheduled: a permanently unservable model's 503 is retried as a warm-up
  for the caller's whole budget. Fixed with the default-backend flip, not here.
- [ ] The harness graduates into the gated live smoke test in Phase 1b (carry the working prompt
  shape into `Prompts/assistant.md`).

## Phase 1a: the chat gateway learns to serve agents (apiApp)

Intent: tools on the wire and model purposes, with every existing caller unchanged and the
environment working at every merge.

> **Split in two, deliberately, and the first half is DONE** (2026-09-10, branch
> `feature/agent-host`). Purposes plus the rename is a coherent unit that lands green on its own,
> and mixing it with three SDKs' tool mapping would have made both unreviewable. The second half is
> still open; the items below say which half each belongs to.
>
> **What the first half turned up that this plan did not predict:**
>
> - **The model had to move from construction time to per request.** One backend now serves two
>   models, so baking one in at construction is no longer possible. Ollama and Anthropic already
>   took the model per request; the OpenAI SDK binds it at construction, so that backend keeps one
>   thin client per model over the one shared transport.
> - **The model's configuration key had to become its own value.** `OllamaConnection` and
>   `RemoteModelTarget` derived three leaf names (endpoint, model, credential) from one section key,
>   so a purpose-qualified section produced nonsense such as `...:Models:Assist:Endpoint`. The model
>   key is now separate and defaults to today's spelling, which is why no embedding message changed.
> - **The per-request check has to be NARROW.** Re-validating the whole target on every call refused
>   any backend a caller supplied itself, which broke the injected-fake seam every chat test uses.
>   It resolves the purpose's model and nothing else.
> - Reported state gained `agentModel` beside `model`, and `model` keeps its meaning (the assist
>   model), so no existing reader of it changed.

- [x] **(first half)** `ChatREST.cs`: `purpose` (`assist` default, `agent`; unknown is a 400
  naming the set). XML docs; statuses unchanged, the schema grew.
- [ ] **(second half)** `ChatREST.cs`: `tools[]` on the request, assistant `toolCalls[]`, tool
  `toolCallId`, response `toolCalls[]`. Two knock-ons found while reading, both of which have to
  admit "no content but a tool call": `Content` is `[Required]` on a message and the controller
  refuses an empty one, yet an assistant turn that only calls a tool has no content; and
  `Fallen8ChatProvider` turns empty content into a 502, which a tool-call reply would trip.
- [ ] **(second half)** `IChatBackend`: tool definitions on `ChatBackendOptions`, tool calls and
  tool-call id on `ChatTurn`, `ToolCalls` on `ChatBackendResult`. The three SDK surfaces were read
  and confirmed, so the mapping is known rather than guessed:
  - Ollama (OllamaSharp 5.4.27): `ChatRequest.Tools` of `Tool { Type, Function }` with
    `Function { Name, Description, Parameters }`; a reply carries `Message.ToolCalls` of
    `Message.ToolCall { Id, Function { Name, Arguments } }`, and a tool turn uses `Message.ToolName`.
  - OpenAI (2.13.0): `ChatTool.CreateFunctionTool(name, description, BinaryData, strict?)` into
    `ChatCompletionOptions.Tools`; a reply carries `ChatToolCall { FunctionName, FunctionArguments }`;
    history uses `AssistantChatMessage(IEnumerable<ChatToolCall>)` and `ToolChatMessage`.
  - Anthropic (12.44.0): `MessageCreateParams.Tools` of `ToolUnion` over
    `Tool { Name, Description, InputSchema }`; a reply carries `ToolUseBlock` and history a
    `ToolResultBlockParam`. **The most involved of the three** (a union plus content blocks), and
    the one to weigh the spec's escape hatch against: composing the generic
    `Microsoft.Extensions.AI` client inside the backend and reading stats off the raw
    representation. Phase 0 confirmed that no `stream=false` fallback is needed on any of them.
- [ ] **Model purposes and the rename.** `Fallen8ChatOptions.<Backend>.Models.{Assist,Agent}`
  replaces `Model` on the four blocks, no alias (Ollama defaults `phi4-f8-mini:latest` and
  `phi4-mini:latest`, the others none); `ChatBackendFactory.ResolveModel(options, purpose)`, a 503
  naming the key when `purpose: agent` meets an empty `Models:Agent`; `Fallen8SettingCatalog`
  renames four Restart-tier entries and adds four; the startup posture line, the config view and
  the residency probe report per purpose; `ChatModelCatalog` unchanged in behaviour, its key
  spelling updated.
- [ ] **The rename sweep, same phase:** `docker-compose.yml` and the `nahil`, `openai`, `anthropic`
  overlays map the existing `F8_*_CHAT_MODEL` variables to `Fallen8__Chat__<Backend>__Models__Assist`
  and set `Models__Agent` (Nahil from `F8_NAHIL_AGENT_MODEL`, default `phi4-mini:latest`; OpenAI
  and Anthropic from their chat model variable); `.env.example`; Studio's picker key in
  `ConfigurationSurface.tsx` (the per-purpose picker follows in Phase 5); the docs pages that
  spell the key (`nahil.md`, `model-providers.md`, `running.mdx`, `nl-assist.md` where it does);
  every test that spells it.
- [x] **(first half)** Tests for purposes, in `ChatEndpointTest.Purposes.cs`, its own file over the
  sibling's harness: a purpose selects the model and omitting it equals `assist`; an unknown purpose
  is a 400 listing both; a purpose with no model is a 503 naming its key while the other purpose
  still answers; both models are reported. Mutation-checked, since making the provider ignore the
  purpose fails two of them. `ChatBackendFactoryTest` gained purpose resolution against a block
  whose two models DIFFER, so a resolver that ignored its argument cannot pass.
- [ ] **(second half)** Tests: the fake seam carries tools and tool calls; per-backend mapping
  against the handler-injected transport fakes; the no-content-but-a-tool-call path end to end.
- [x] **(first half)** OpenAPI snapshot regenerated: `purpose` and `agentModel` added, one
  description reworded, nothing else removed. `McpRestCoverageTest`: the `/chat` deferral is
  unchanged.
- [ ] **(second half)** Regenerate again for the tool shapes.
- [ ] Solution-wide package alignment: `Microsoft.Extensions.AI.Abstractions` 10.9.0, OllamaSharp
  to match; exact versions. **Neither half of 1a needs it**, so it moves to Phase 1b, which is the
  first code to reference `Microsoft.Agents.AI`. Phase 0 confirmed 10.9.0 restores and builds on
  net10.0. One gotcha for whoever does it: OllamaSharp 5.4.27's source generator raises CS9057 in a
  fresh project on SDK 10.0.201, which under warnings-as-errors fails a NEW project that references
  it. The apiApp does not hit this.
- [x] **(first-half gate, met)** Full suite green at 2527 passed, up by the four new tests; build
  clean; docs site links valid; all four compose profiles parse and resolve their purpose keys; the
  30 Studio picker tests pass with the renamed key. Every existing chat, catalog and configuration
  test passes with no change beyond spelling.
- [ ] **(second-half gate)** A request carrying tools round-trips a tool call on the default
  backend, and a backend that cannot map tools REFUSES a request carrying them rather than ignoring
  them silently.

## Phase 1b: scaffold, adapter, proxy and single-agent walking skeleton

Intent: one spawnable, listable, cancellable agent end-to-end through the apiApp proxy,
deterministic in CI.

- [ ] New `fallen-8-agents` project (net10.0, `NoSQL.GraphDB.Agents`, explicit `Program` namespace)
  in `fallen-8-core.sln`, referencing `fallen-8-rest-client`; MIT headers; exact versions
  (`Microsoft.Agents.AI`, `ModelContextProtocol` matched to `fallen-8-mcp`). No provider SDK.
- [ ] `Fallen8TargetOptions` (the small copied options class, same spelling as the other two
  sidecars; `TimeoutSeconds` 630), `AgentsOptions`, `AgentsMcpOptions`, bound and validated.
- [ ] `Fallen8ChatClient`: `IChatClient` over `POST /chat` with `purpose: agent` on the
  `fallen-8-rest-client` seam; messages and tools to the wire, `toolCalls` to
  `FunctionCallContent`, stats to `UsageDetails`, `backend` and `model` onto the response.
- [ ] Startup posture log (bind and proxy-only posture, instance URL and the bounded
  `GET /chat/models` probe outcome, MCP target + tiers + tool count, caps, default budget);
  loopback-by-default bind.
- [ ] `RoleCatalog` (three roles, embedded prompts refused when empty, per-role tool allowlists
  applied to the fetched MCP tool list), `AgentRegistry` (in-memory, state machine per spec 3.2,
  retention) and `AgentRunner` wrapping `ChatClientAgent` + MCP tools fetched at startup.
- [ ] Host endpoints under `/agent/*`: spawn, list, detail, cancel, status. Problem+json in the
  house shape.
- [ ] apiApp: `Fallen8AgentsOptions`, the `Agents` capability arm, `AgentsController` proxying
  `/agents/*` on the shared sidecar-proxy client base (small routes; the feed follows in Phase 2).
  OpenAPI snapshot regenerated (additions only); `McpRestCoverageTest` deferral rule with the
  spec's reason; `NamespaceEndpointTest` entries.
- [ ] Convention: `fallen-8-agents` in `CodeQualityTest`'s lists and the REST-only rule; the new
  route-family pin (`/chat`, `/chat/models` and nothing else) against the OpenAPI snapshot.
- [ ] Tests: scripted `IChatClient` fake (replies, tool calls incl. a malformed sibling, usage
  incl. zero); `Fallen8ChatClient` against a hosted apiApp with a fake backend; lifecycle
  transitions, cancel, allowlist filtering, list/detail shapes, 404/409 paths; proxy tests
  (403/401 gate, the one invented 503, pass-through, no request body in logs). Gated live smoke
  test (skips without a configured instance).

## Phase 2: trace, feed and conversation

Intent: "review what they are doing all the time", the observability half of the contract.

- [ ] `AgentTrace`: bounded step buffer (modelCall with backend and model as reported, toolCall with
  byte-capped captures and `truncated`/total bytes, message, spawn, citationCheck, state change;
  drop-oldest marker); `GET .../trace`.
- [ ] `AgentFeedDispatcher` + `GET /agent/feed` (SSE): change-feed frame conventions, keep-alive,
  declarative `agents`/`kinds` filters (400 on junk), all six event kinds. The apiApp proxy gains
  its streaming-forward arm and `GET /agents/feed`.
- [ ] `POST .../messages` (202 with `messageId`; reply as `agentMessage` with `inReplyTo` on the
  same session; 409 when the agent cannot accept input).
- [ ] `GroundingCheck`: cited `[t:<id>]` ids counted against the trace; `citations` on detail and on
  `agentCompleted`.
- [ ] Tests: SSE frame format and filter grammar through the proxy via `WebApplicationFactory`;
  trace bounding and byte caps; conversation round-trip on the fake client; citation counts
  (valid, dangling, none); summaries truncated, no payloads.

## Phase 3: counters, budgets, metrics

Intent: honest cost accounting and hard stops, the other half of the review contract.

- [ ] Usage from the instance's stats accumulated per agent; `unreportedUsage` step; steps, tool
  calls and wall clock counted by the host; all four on list/detail and on `agentStateChanged`.
- [ ] Budget enforcement after each step at the framework's per-step seam (maximum iterations
  where the framework has the knob): `budgetExceeded` naming `tokens`, `steps`, `toolCalls` or
  `time`; feed event.
- [ ] `AgentsMetrics` (spec 3.6) with the observability containment and tag-hygiene rules; Agent
  Framework's OTel GenAI spans wired to the exporter configuration; fleet identity declared.
- [ ] Check the 3.8 defaults against the Phase 0 numbers, including the instance hop, and adjust
  the spec table if they moved.
- [ ] Tests: counter exactness against scripted usage, each budget stops with its name, metrics
  emitted (`MeterListener`), no user input in tag values.

## Phase 4: swarm mode

Intent: orchestrator/worker composition on the framework's primitives, bounded by configuration.

- [ ] `SwarmTools`: `spawn_worker` / `await_workers` as registry-backed `AIFunction`s, attached only
  to `orchestrator` agents; workers are first-class `worker`-role agents with `parentId`;
  `await_workers` returns typed results.
- [ ] Caps enforced in the registry (`MaxConcurrentAgents`, `MaxSwarmDepth`,
  `MaxWorkersPerOrchestrator`): breach = tool error + `toolCalled(success=false)` event.
- [ ] Cascade cancel; worker results delivered to the awaiting orchestrator via Agent Framework
  handoff/concurrent patterns (no bespoke scheduler); orchestrator prompt states the one-composer
  rule.
- [ ] Tests (fake client, scripted orchestrator): spawn visibility, every cap, depth limit, cascade
  cancel, await semantics and result shape, orchestrator budget independent of workers',
  orchestrator allowlist (no graph tool beyond `f8_overview` offered).

## Phase 5: packaging, docs, land

Intent: ship what exists; leave the repo consistent.

- [ ] `Dockerfile` (sdk to aspnet runtime, house conventions, copies `fallen-8-rest-client`);
  compose service `f8-agents` on the `agents` profile: unpublished, `expose:` only, `read_only`,
  `/tmp` tmpfs, no volume, `Fallen8Target__*` and MCP variables reused, **no model setting**;
  `fallen8` gains the two instance keys; `scripts/ollama-init.sh` pulls the agent model when
  `F8_AGENTS=true`; `env-up.js`, `env:down`/`logs`/`status`, `.env.example` (`F8_AGENTS`),
  `release.yml` matrix. Default compose unchanged.
- [ ] Studio: one catalog picker per purpose beside each other in `ConfigurationSurface.tsx`, the
  card showing model and residency per purpose; recapture the Configuration screenshots.
- [ ] Docs page `docs/src/content/docs/agents.md` registered in the *AI agents* sidebar group; the
  chat-body fields on `nl-assist.md` and `rest-api.mdx`; the purposes paragraph on
  `model-providers.md`; the quotas sentence on `nahil.md`; a pointer on `mcp-server.md`; README
  "Key features" line; **both architecture diagrams** updated in the same PR (the host reaches the
  MCP server and the chat gateway, never a provider); docs build green.
- [ ] Feature `README.md` (LIVING doc): quickstart (enable, spawn curl, subscribe curl), roles and
  allowlists, configuration reference incl. the two purposes, the Phase 0 record and fallback
  ladder, security posture (proxy-only, one REST family, read-only-tiers recommendation,
  prompt-injection honesty note).
- [ ] Once landed: move `features/open/agent-host/` to `features/done/agent-host/`; add the
  "operate the agent host" note to the skill library's spec (there, not here).
- [ ] Full suite green, build clean (warnings-as-errors), convention tests pass for the new
  project.
