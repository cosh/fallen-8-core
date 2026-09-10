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
- [x] The harness graduated into `AgentLiveSmokeTest` (gated, `[Ignore]`d, needs
  `F8_TEST_AGENT_BASEURL`). The parenthesis - carry the working prompt shape into the role prompt -
  turned out to be impossible as written: Phase 1b measured that on this model there IS no
  instruction text that keeps tool calling working. See [findings.md](./findings.md) section 1 and
  the amendment to spec 3.2a.

## Phase 1a: the chat gateway learns to serve agents (apiApp)

Intent: tools on the wire and model purposes, with every existing caller unchanged and the
environment working at every merge.

> **DONE** (2026-09-10, branch `feature/agent-host`, two commits). Split in two deliberately:
> purposes plus the rename first, tool calling second. Each landed green on its own, and mixing
> them would have made both unreviewable.
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
- [x] **(second half)** `ChatREST.cs`: `tools[]` on the request, assistant `toolCalls[]`, tool
  `toolCallId`, response `toolCalls[]` (absent rather than null when there are none). Both predicted
  knock-ons were real and are handled: `[Required]` came off `Content` and the controller now
  refuses an empty turn only when it carries no calls either, and the provider's empty-content 502
  became empty-AND-no-calls.
- [x] **(second half)** `IChatBackend`: tool definitions on `ChatBackendOptions`, tool calls and
  tool-call id on `ChatTurn`, `ToolCalls` on `ChatBackendResult`, plus `ChatTool`/`ChatToolCall` as
  the lowest common shape. All three backends map it, and every SDK type was read off the assembly
  first rather than guessed:
  - Ollama (OllamaSharp 5.4.27): `ChatRequest.Tools` of `Tool { Type, Function }` with
    `Function { Name, Description, Parameters }`; a reply carries `Message.ToolCalls` of
    `Message.ToolCall { Id, Function { Name, Arguments } }`, and a tool turn uses `Message.ToolName`.
  - OpenAI (2.13.0): `ChatTool.CreateFunctionTool(name, description, BinaryData, strict?)` into
    `ChatCompletionOptions.Tools`; a reply carries `ChatToolCall { FunctionName, FunctionArguments }`;
    history uses `AssistantChatMessage(IEnumerable<ChatToolCall>)` and `ToolChatMessage`.
  - Anthropic (12.44.0): `MessageCreateParams.Tools` of `ToolUnion` over
    `Tool { Name, Description, InputSchema }`; a reply carries `ToolUseBlock` and history a
    `ToolResultBlockParam`. Feared to be the awkward one and was not: `ToolUnion` has an implicit
    conversion from `Tool`, and the blocks expose plain dictionaries. **The spec's escape hatch was
    not needed** and no backend refuses tools.

  Three things the mapping turned up:

  - **Each backend carries the caller's JSON Schema VERBATIM**, not through the SDK's own schema
    type. All three ship one that models a subset of JSON Schema, so mapping through it would
    silently drop what it does not describe (a `required` array, in the test's case). Ollama's tools
    field takes plain objects, and Anthropic's `InputSchema` takes a raw-data dictionary, so both
    carry the schema as written.
  - **Phase 0's "no `stream=false` fallback needed" was true of the WIRE and false of the client
    libraries.** OllamaSharp documents its tools field as requiring a non-streamed request, and the
    OpenAI SDK delivers streamed calls as fragments to reassemble. So a request carrying tools is
    not streamed on any backend, stated once on `ChatBackendOptions.Tools` and pointed at from the
    three. It affects no other request.
  - **The seam's own types shadow two SDK types.** `ChatTool` and `ChatToolCall` live in the same
    namespace as the OpenAI backend, so that file aliases the SDK's identically-named types.
- [x] **Model purposes and the rename.** `Fallen8ChatOptions.<Backend>.Models.{Assist,Agent}`
  replaces `Model` on the four blocks, no alias (Ollama defaults `phi4-f8-mini:latest` and
  `phi4-mini:latest`, the others none); `ChatBackendFactory.ResolveModel(options, purpose)`, a 503
  naming the key when `purpose: agent` meets an empty `Models:Agent`; `Fallen8SettingCatalog`
  renames four Restart-tier entries and adds four; the startup posture line, the config view and
  the residency probe report per purpose; `ChatModelCatalog` unchanged in behaviour, its key
  spelling updated.
- [x] **The rename sweep, same phase** (one gap closed in Phase 1b: `.env.example` documented
  neither `F8_*_AGENT_MODEL` variable, though all three overlays already read them, and the block
  added for them first claimed a fallback to the chat model that no overlay implements):
  `docker-compose.yml` and the `nahil`, `openai`, `anthropic`
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
- [x] **(second half)** Tests: `ChatToolMappingTest` pins each provider's spelling against an
  injected transport (schema intact on the wire, a call read back, a replayed call and its result,
  no streaming when tools are present, the empty-object schema for a tool taking no arguments);
  `ChatEndpointTest.Tools.cs` pins the contract a client sees (the round trip, the replay with no
  content on the assistant turn, five malformed requests refused at the edge, empty-and-no-calls
  still a 502, and no tools meaning nothing tool-shaped on the call). Mutation-checked: dropping
  the tools in the Ollama backend fails three, and dropping the tool-call id in the controller
  fails one.
- [x] **(first half)** OpenAPI snapshot regenerated: `purpose` and `agentModel` added, one
  description reworded, nothing else removed. `McpRestCoverageTest`: the `/chat` deferral is
  unchanged.
- [x] **(second half)** Regenerated: two new schemas and four new properties. The only removals
  are the `content` requirement, which is now conditional by design, and two rewordings.
- [x] Solution-wide package alignment (done in Phase 1b): `Microsoft.Extensions.AI.Abstractions` 10.9.0, OllamaSharp
  to match; exact versions. **Neither half of 1a needs it**, so it moves to Phase 1b, which is the
  first code to reference `Microsoft.Agents.AI`. Phase 0 confirmed 10.9.0 restores and builds on
  net10.0. One gotcha for whoever does it: OllamaSharp 5.4.27's source generator raises CS9057 in a
  fresh project on SDK 10.0.201, which under warnings-as-errors fails a NEW project that references
  it. The apiApp does not hit this.
- [x] **(first-half gate, met)** Full suite green at 2527 passed, up by the four new tests; build
  clean; docs site links valid; all four compose profiles parse and resolve their purpose keys; the
  30 Studio picker tests pass with the renamed key. Every existing chat, catalog and configuration
  test passes with no change beyond spelling.
- [x] **(second-half gate, met and then some)** Full suite green at 2540 passed, build clean, docs
  site links valid, both compose profiles parse, Studio's 30 picker tests pass. No backend refuses
  tools, because all three map them.

  **Verified against the LIVE service, which is what found the one real bug.** Offering a tool with
  `purpose: agent` returned a parsed call in 5 s on the agent model; handing the result back
  produced "The Fallen-8 namespace 'default' contains 8 vertices." The bug in between: this
  protocol types `content` as a string and answers **422** to a null, killing the whole request -
  and an assistant turn that only called a tool has no text, so null was exactly what reached it.
  No stub could catch that, because a stub accepts whatever it is handed. It is now pinned by
  asserting the JSON TYPE of that field rather than its value.

  One measured thing to carry into Phase 1b: the live model returned arguments shaped
  `{"parameters":{...},"type":"count_vertices"}` instead of the schema's own shape. That is the
  known small-model flakiness, passed through faithfully because nothing here validates arguments
  against the schema. The runner has to tolerate it, which spec section 3.2a already requires.

## Phase 1b: scaffold, adapter, proxy and single-agent walking skeleton

Intent: one spawnable, listable, cancellable agent end-to-end through the apiApp proxy,
deterministic in CI.

> **DONE** (2026-09-10, branch `feature/agent-host`). Full suite green at 2616 passed, build clean
> under warnings-as-errors, and verified against a live instance and a real model.
>
> **The gate passed on the code and failed on the model**, which is the one thing this phase could
> not have discovered any other way. The host, the adapter, the caps, the registry and the proxy all
> work; the only tool-capable model the configured platform serves cannot be given instructions
> without it stopping emitting tool calls. That is recorded in
> [findings.md](./findings.md) section 1, with the measurement table, and spec 3.2a is amended
> because its premise no longer holds for the default deployment. Decision (2026-09-10): the prompts
> ship as written, the gap is recorded, and a tool-capable model is a platform ask.
>
> **What live verification found that the suite did not**, both fixed and now pinned:
>
> - **The role prompt never reached the model.** The framework carries an agent's system prompt on
>   `ChatOptions.Instructions`, not in the message list, and the adapter mapped only the messages.
>   Requests left with nine prompt tokens. The test meant to cover it asserted on the property the
>   framework had set, so it passed the whole time; the replacement asserts what the gateway
>   received.
> - **A hung gateway was reported as an agent somebody cancelled**, because the adapter's own
>   deadline and a caller's cancel are the same exception type. Fixed by adopting the shared
>   `fallen-8-rest-client` seam, which exists for exactly that split - and which this project
>   referenced but did not use until now.
>
> **What an adversarial review found** (69 candidates across seven dimensions, each verified by a
> skeptic instructed to refute it). The ones that survived and are fixed: a run whose setup threw
> wedged an agent in `pending` forever with its concurrency slot held and nothing logged; a run that
> stopped without answering was recorded as `completed`; the documented tool allowlist key could not
> bind, so a configured allowlist silently did nothing; a caller could name its own token budget
> without limit; `worker` and `orchestrator` were spawnable despite having no one to report to and no
> delegation tools; six false claims in comments, log lines and `.env.example`; and four test gaps,
> including `MaxToolCallsPerRun` having no test at all and two tests that depended on how long
> Windows takes to refuse a loopback connection. All of it is in
> [findings.md](./findings.md) sections 2 to 7.

- [x] New `fallen-8-agents` project (net10.0, `NoSQL.GraphDB.Agents`, explicit `Program` namespace)
  in `fallen-8-core.sln`, referencing `fallen-8-rest-client`; MIT headers; exact versions
  (`Microsoft.Agents.AI` 1.20.0, `ModelContextProtocol` 1.4.1 matched to `fallen-8-mcp`). No
  provider SDK. The seam reference is load-bearing rather than nominal: it owns the
  timed-out-versus-unreachable split, which here decides whether an agent reports itself failed or
  cancelled.
- [x] `Fallen8TargetOptions` (the small copied options class, same spelling as the other two
  sidecars; `TimeoutSeconds` 630), `AgentsOptions` with nested `McpOptions`, `LimitsOptions` and
  `RoleOptions`, bound and clamped at the point of use rather than validated at startup. Two things
  the plan did not predict: `Agents:Roles:<role>:Tools` needs a nested class, because a
  `Dictionary<String, List<String>>` binds `...:<role>:0` and made every configured allowlist a
  silent no-op; and an over-large `MaxRunSeconds` cannot be armed on a timer at all, so it is
  clamped with a warning instead of throwing inside a run.
- [x] `Fallen8ChatClient`: `IChatClient` over `POST /chat` with `purpose: agent` on the
  `fallen-8-rest-client` seam; messages and tools to the wire, `toolCalls` to
  `FunctionCallContent`, stats to `UsageDetails`, `backend` and `model` onto the response and onto
  an immutable `LastSeen` pair. Three things the plan did not predict: **instructions are not a
  message** and dropping them was the phase's worst bug; usage has to be decided on the token
  FIELDS, because the gateway sends a `stats` object on every answer and nulls what a backend did
  not fill; and the provenance pair needs one volatile reference rather than two properties, since
  several agents share the client and a mismatched pair describes a deployment that does not
  exist.
- [x] Startup posture log (bind and proxy-only posture, instance URL and the bounded
  `GET /chat/models` probe outcome, MCP target + tool count, caps, default budget); loopback-by-default
  bind. Said AFTER both probes rather than at registration, so the line reports what is true rather
  than what was configured. `ProbeChatAsync` is public so its 401 and 403 arms - the two an operator
  can actually fix - are testable; all five outcomes are pinned.
- [x] `RoleCatalog` (three roles, embedded prompts refused when empty, per-role tool allowlists
  applied to the fetched MCP tool list), `AgentRegistry` (in-memory, state machine per spec 3.2,
  retention, the concurrency cap and the cancellation tokens) and `AgentRunner` wrapping
  `ChatClientAgent` + MCP tools fetched at startup. The runner's pipeline order is load-bearing and
  says so: the shared adapter at the bottom, the per-agent meter above it, the framework's
  tool-invoking client on top, so the meter sees every call the loop makes rather than one per user
  message. `IAgentToolSource` was extracted so a runner test drives a real tool loop with a local
  function; `UseProvidedChatClientAsIs` is set because the pipeline is already complete and two tool
  loops over one request would each invoke every call.
- [x] Host endpoints under `/agent/*`: spawn, list, detail, cancel, status, plus `/health`.
  Problem+json in the house shape. The concurrency cap answers **429** rather than 503, because the
  host is healthy and 503 is what the proxy's own invented status means. `worker` and `orchestrator`
  are refused to a caller with the reason, since neither can do what its prompt says in this phase.
  One agent is reported through `TrySummarize`, which builds the summary under the registry's lock:
  `Finish` writes six fields in sequence, so a summary taken outside it can show an ending that is
  half recorded.
- [x] apiApp: `Fallen8AgentsOptions`, the `Agents` capability arm, `AgentsController` proxying
  `/agents/*` on the shared sidecar-proxy client base (small routes; the feed follows in Phase 2).
  OpenAPI snapshot regenerated, additions only (three paths, five operations);
  `McpRestCoverageTest` deferral recorded with its reason - a loop, not frugality: bridging the
  spawn route would hand an agent a tool that spawns agents holding that same tool;
  `NamespaceEndpointTest` exempts the prefix. Three settings catalogued, which the suite's own
  reflection gate required. The documented statuses now name BOTH halves of the capability gate: 403
  on a keyed instance and **401** on a keyless one, which this repository's docs widely get wrong.
- [x] Convention: `fallen-8-agents` in `CodeQualityTest`'s lists and the REST-only rule; the new
  route-family pin (`/chat`, `/chat/models` and nothing else) against the OpenAPI snapshot, so a
  route family added to the instance is covered the moment the snapshot is regenerated.
  Mutation-checked twice, and the second time changed it: the first version saw only complete string
  literals, which is not how a graph call is written. It now also matches the leading segment of an
  interpolated path, anchored at both ends - an opening quote alone matched the second fragment of
  any concatenated sentence and flagged the word "delegates" in a refusal message.
- [x] Tests, 76 of them across four files. Scripted `IChatClient` fake (replies, tool calls
  including several in one response, usage including zero and absent, a wait, a throw);
  `AgentChatAdapterTest` drives the adapter through a hosted apiApp and a fake backend and asserts
  what the backend RECEIVED, which is the only shape that catches a wire mistake; lifecycle,
  all four budgets, cascade cancel, retention and eviction against a hand-moved clock, allowlist
  filtering and the configuration binder; proxy tests (403 keyed and 401 keyless, the one invented
  503, pass-through of four statuses, the shipped client's unreachable and caller-cancellation arms,
  and no request body in logs, now running the shipped client with a sink-is-wired guard);
  `AgentPostureTest` runs the MCP toolset against the **real `fallen-8-mcp` server hosted in
  process**, whose success path nothing reached before. Two tests that depended on connection-refusal
  latency now use a socket that accepts and never answers, so they behave the same on Linux. Gated
  live smoke test in place.

## Phase 2: trace, feed and conversation

Intent: "review what they are doing all the time", the observability half of the contract.

> **DONE** (2026-09-10, branch `feature/agent-host`), except the conversation route, which is
> DEFERRED with a recorded reason rather than built. Full suite green at 2658 passed, build clean,
> OpenAPI snapshot regenerated with additions only.
>
> **The conversation route was the phase's real decision and the answer was "not ours to make
> yet".** Microsoft Agent Framework owns how an agent interacts: a session holds the history and
> another turn is one more `RunAsync` on it, which Phase 0 already proved. What
> `POST .../messages` would add is three pieces of host bookkeeping the framework has no opinion on
> - whether a parked agent holds a concurrency slot, what wall clock applies between turns, and what
> makes an agent that can always take another message ever reach an ending. No client needs a second
> turn today, so answering those would be guessing, and each guess would live in configuration
> forever. Recorded as spec section 3.4a with the revisit trigger.
>
> **One correction carried in from Phase 1b:** the grounding check counts `[t:<name>]`, the tool's
> own name, not `[t:<id>]`. No backend shows a tool-call id to a model, so the original marker asked
> the model to invent one.
>
> **Two things the trace design turned up that the plan did not predict.** A capture has to be cut
> on a UTF-8 CHARACTER boundary, not a byte index, or a truncated result is invalid UTF-8 and a
> reviewer gets a replacement character instead of the text; and the drop marker has to REPLACE what
> it reports on, because a marker appended past a full buffer grows it by one on every overflow,
> which is a slow leak in the one structure that exists to be bounded. Both are pinned.

- [x] `AgentTrace`: bounded step buffer (modelCall with backend and model as reported, toolCall with
  byte-capped captures and `truncated`/total bytes, message, spawn, citationCheck, state change;
  drop-oldest marker); `GET .../trace`. Lives ON the agent record, so it is evicted exactly when the
  agent is and there is no second lifetime to get wrong. The detail route carries a 20-step tail
  plus the total, so a reader can tell a tail from a whole run. Provenance is per step, read off
  each response rather than from the adapter's aggregate, which belongs to whichever call finished
  last.
- [x] `AgentFeedDispatcher` + `GET /agent/feed` (SSE): the change feed's own frame conventions, so a
  client that reads one reads the other; keep-alive comments while idle; declarative `agents`/`kinds`
  filters, 400 on junk with the accepted set named. An `agents` filter also admits the workers that
  agent spawned, so subscribing to an orchestrator shows its swarm rather than its own two events.
  The apiApp proxy gained its streaming-forward arm, which needed `HttpClient.Timeout` disarmed and
  a per-call budget on the small arm instead: one number cannot serve a 30-second listing and a feed
  that stays open for hours. Publishing never waits on a subscriber - a slow reader would otherwise
  apply back-pressure to a model call - so a subscriber past `MaxQueuedEvents` is DROPPED rather
  than silently thinned, and reconnects to the trace to catch up.
- [ ] **DEFERRED, 2026-09-10.** `POST .../messages` (202 with `messageId`; reply as `agentMessage`
  with `inReplyTo` on the same session; 409 when the agent cannot accept input). The framework
  already does the interacting; the three undecided questions are the host's slot, clock and ending
  policy, and no client needs a second turn yet. Reason and revisit trigger in spec section 3.4a.
  The `agentMessage` event kind and the `message` trace step ship anyway, because the swarm phase
  uses them for worker traffic.
- [x] `GroundingCheck`: cited `[t:<name>]` tool names counted against the trace; `citations` on
  detail and on `agentCompleted`. Names, not ids, for the reason Phase 1b measured. Occurrences
  rather than distinct names, so an answer that cited its first figure and asserted the other nine
  does not read as fully grounded. Lenient about the `#2` occurrence suffix and whitespace, because
  a small model reproduces a format approximately and a real citation counted as absent is the worse
  error; strict about the name, which is the part being checked.
- [x] Tests, 42 of them. `AgentTraceTest` pins the bound (oldest dropped, marker carries the running
  total, sequence numbers do not restart so a gap is the evidence, a marker cannot itself grow the
  buffer), the byte caps (original size reported, cut on a character boundary, a surrogate pair kept
  whole or dropped whole, null stays null), the citation counts (valid, dangling, none, occurrences,
  the tolerated suffix, seven non-citations) and the filter grammar (unknown kind refused with the
  set named, comma-separated or repeated, empty means everything, a parent id admits its workers).
  `AgentEndpointTest` pins the SSE contract against the REAL host: the frame shape, `event:` per
  kind, the counters on every event, no catch-up (the spawn happens after the subscribe), the kind
  filter, keep-alives on an idle stream, the subscriber bound, and both proxy arms including that
  the feed goes through the STREAMING one and a refusal reaches the caller as a refusal rather than
  an empty stream. The conversation round-trip is not here, for the reason above.

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
