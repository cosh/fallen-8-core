# Fallen-8 Agent Host: Specification

> **Status:** Phases 0, 1a, 1b and 2 are IMPLEMENTED on `feature/agent-host` and unmerged; Phase
> 3 (metrics) is next, then 4 (swarm) and 5 (packaging, docs, land). This line said "spec only (no
> implementation yet)" while a whole deployable, the apiApp proxy and the chat-gateway purposes had
> landed, which is the one line a reader checks to place the feature. Per-phase state and dates are
> in [plan.md](./plan.md). Follow the feature workflow in the
> repository root `CLAUDE.md`. Feature branch: `feature/agent-host` (branch-only workflow:
> no GitHub issue or PR).
>
> **Depends on:** [mcp-server](../../done/mcp-server/spec.md) (implemented; agents reach the graph
> exclusively through the `fallen-8-mcp` tools, and this feature adds **no** second graph bridge),
> [model-providers](../../done/model-providers/spec.md), [nahil-backend](../../done/nahil-backend/spec.md)
> and [chat-model-catalog](../../done/chat-model-catalog/spec.md) (implemented; the instance's chat
> gateway is where the agents' model calls go), and [integrations](../../done/integrations/spec.md)
> (implemented; the host adopts its deployment posture: no published port, reached through the
> apiApp's authenticated proxy).
> **Companion:** [skill-library](../skill-library/spec.md) (procedural knowledge agents can be
> primed with; spec only, does not block this feature).
>
> **Revision history:**
> - *2026-07-16*: first draft, written before Nahil, the model-provider selector, the chat model
>   catalog and the integrations runtime existed.
> - *2026-09-09a*: re-grounded against `main`. Six things were stale: the model requirement was
>   "local phi via Ollama"; the Phase 0 tool-calling gate was an open question (tool calling
>   through Nahil is now verified live); "apiApp untouched" was no longer honest; the deployment
>   posture (loopback bind, own API key) predated the integrations pattern; the MCP auth phases
>   the spec waited on had landed; Microsoft Agent Framework is a stable 1.x release. Section 3
>   gained per-run caps, role tool allowlists, a grounding check, typed worker results and
>   retention, and section 7 (impact) was added. The model route chosen that morning was "the
>   host dials the provider itself through a shared transport library".
> - *2026-09-09b*: **the model route is reversed by the operator.** Agents do not care where a
>   model is instantiated or where inference runs: the host asks its Fallen-8 instance, and the
>   instance's own provider selector routes the call, to Nahil as the standard in a Nahil
>   deployment, to the sidecar or to a hosted provider otherwise. The shared transport library of
>   revision a is gone; in its place the instance's chat gateway learns tools and a second
>   server-owned model, and the host gains one small adapter. Section 1 records the decision and
>   its costs.
> - *2026-09-09c*: **model purposes replace the second model slot.** Revision b added an
>   `AgentModel` field beside `Model`; that was the least elegant part, because the thing that
>   varies is the job, not the deployable. Each backend block now carries one server-owned model
>   per **purpose** (`Fallen8:Chat:<Backend>:Models:<Purpose>`, a closed set: `Assist`, `Agent`),
>   the request selects the purpose, and `Fallen8:Chat:<Backend>:Model` is **renamed to
>   `Models:Assist` with no alias** (operator decision). A fine-tune slots in behind a stable
>   purpose; that is how the operator keeps fine-tuning assist models while agents run on
>   general ones.

> **Namespaces (feature graph-namespaces, 2026-07-23):** the REST surface the MCP tools consume is
> namespace-scoped; tools carry a namespace parameter defaulting to `default`. Nothing here adds a
> namespace concept of its own; see [graph-namespaces](../../done/graph-namespaces/).

## 1. Overview and requirements

Fallen-8 gains a way to **run AI agents against the graph as a first-class, observable workload**:
spawn an agent on a task, converse with it, watch what it does, see exactly what it costs, and
compose agents into orchestrator/worker swarms.

Requirements fixed up front (user-stated; 3 was restated twice on 2026-09-09, the second time
being final):

1. **Separate process.** The agent runtime is its own deployable, never in-process with the
   database or the apiApp.
2. **Local MCP.** Agents use the deployment's own MCP server to operate on Fallen-8, which is the
   [mcp-server](../../done/mcp-server/spec.md) feature; this feature is its first in-repo client.
3. **The agent asks the instance.** The host sends every model call to its Fallen-8 instance's
   chat gateway (`POST /chat`), and the instance's provider selector decides where inference
   happens: Nahil, the standard in the operator's deployments; the local sidecar; OpenAI or
   Anthropic. The host holds **no** model configuration and **no** provider credential, and it
   learns which backend and model served a step only from the instance's answer, which already
   names both. The instance owns the agents' model as it owns the NL-assist model: one
   server-owned model name per **purpose** per backend block (3.1a), and the agent purpose is
   never the NL-assist fine-tune (`phi4-f8-mini`, `phi4-f8`), which is trained to emit exactly one
   C# fragment and is not an agent model.
4. **User to agent communication** plus **subscribable events**: a user can send messages to a
   running agent and subscribe to a live event stream of everything agents do.
5. **Spawn and review interface:** an API to start agents on specific tasks, list them, and
   inspect **at any time** what each is doing and **how many tokens it has consumed**.
6. **Swarm mode:** orchestrator agents that decompose a task and spawn worker agents.
7. **No home-grown agent framework.** The agentic loop, tool invocation and multi-agent
   orchestration come from an established MIT-licensed framework.

### The model route: the instance's chat gateway (decision, 2026-09-09b)

Two routes were on the table. **A**: the host dials the provider itself with the instance's
four-way selector, over transports extracted from the apiApp into a shared library. **B**: the
host calls `POST /chat` and the instance routes. Revision a chose A for three reasons: `/chat`
carried no tools on the wire, the instance's one chat model is the NL-assist fine-tune, and
[instance-config](../../done/instance-config/spec.md) decision D8 refuses per-request model choice.
The operator reversed that the same day, and the reversal is right for a reason the morning's
analysis under-weighted: **where a model runs is the deployment's concern, not the agent's.** The
instance already owns the selector, the credential, the deadline and retry rules, the warm-up and
quota waits, the catalog and the per-call provenance stamp. A host that owned a second copy of
all that, plus a second home for the provider key, would be the drift this repository exists to
avoid, and it would make "switch the deployment to Nahil" a two-place change.

The three objections become three bounded costs, each accepted here:

| Objection to B | What this spec does about it |
|---|---|
| `/chat` has no tools on the wire | it gains them (3.1a): a `tools` array on the request, `toolCalls` on assistant turns and on the response, `toolCallId` on tool turns. Additions only; every existing caller is unchanged. |
| the instance's chat model is the NL-assist fine-tune | each backend block gains a **purpose map** (3.1a): `Models:Assist`, the renamed `Model`, and `Models:Agent`, selected by a closed `purpose` enum on the request (`assist`, the default, or `agent`). The client still never names a model, so D8 is upheld rather than revisited. |
| the apiApp sits in every agent step's hot path | an in-network hop measured in milliseconds, against steps measured in tens of seconds on a remote provider (section 5). Accepted, and the trace records both numbers. |

**On "the default is Nahil":** in the operator's deployments Nahil is the standard provider, and
that is what the shipped `nahil` overlay produces when its key is present. The instance's
selector default in code and the overlay mechanics belong to
[model-providers](../../done/model-providers/spec.md) and are not changed by this spec; the host has
no default of its own to set.

### Framework choice (requirement 7)

**Microsoft Agent Framework** (`Microsoft.Agents.AI`, MIT, stable 1.x; 1.20.0 released 2026-08-31),
the merged successor of AutoGen and Semantic Kernel's agent work, and .NET-first. It provides
exactly the pieces this feature must not reinvent:

- `AIAgent` / `ChatClientAgent` over any `Microsoft.Extensions.AI.IChatClient`. Here that client
  is **one small adapter over `POST /chat`** (`Fallen8ChatClient`, 3.1): messages and tools in,
  content or tool calls out, usage from the response's stats. It is a transport adapter, not a
  loop; the loop, the function invocation and the multi-turn bookkeeping are the framework's.
- **Native MCP tool support:** an MCP client's tools (`McpClientTool`, an `AIFunction`) plug into an
  agent directly, which is precisely the `fallen-8-mcp` integration point.
- **Workflows/orchestration** (sequential, concurrent, handoff, group chat): the swarm primitives
  for requirement 6.
- Token usage per response (`UsageDetails`), a per-step seam (a delegating chat client in front
  of the model, the function-invocation middleware around each tool call) and OpenTelemetry GenAI
  instrumentation: the raw material for requirement 5 and for the caps in 3.2.

Considered and rejected: a hand-rolled loop over `IChatClient` (violates requirement 7);
LangChain-style Python stacks (wrong runtime for this solution); Semantic Kernel alone (its agent
story is folded into Agent Framework).

## 2. Goals / non-goals

**Goals**

- A new **`fallen-8-agents`** project (net10.0, ASP.NET Core, namespace `NoSQL.GraphDB.Agents`):
  the agent host process. It references neither the engine nor the apiApp, reaches the graph
  through MCP only, and reaches the instance over REST for exactly one route family, the chat
  gateway. It is held to the same convention tests as the other two REST-only deployables, plus
  one that pins that route family.
- **The chat gateway grows to serve agents** (3.1a): tools on the wire and **model purposes**, one
  server-owned model per purpose per backend, selected by the `purpose` enum. Every existing
  caller keeps its contract.
- **Agent lifecycle API:** spawn an agent on a task, list all agents with live state and totals,
  inspect one agent's full activity trace, message it, cancel it, read the host's posture.
- **Event feed:** a Server-Sent-Events stream of agent events, following the change-feed's SSE
  conventions, streamed through the apiApp's proxy.
- **Honest accounting and hard caps:** per-agent input/output token counters updated from the
  instance's reported usage, plus step, tool-call and wall-clock counters; per-agent budgets on
  all four that stop a runaway agent; OTel metrics in the observability feature's style.
- **Swarm mode:** an `orchestrator` role whose extra tools spawn and await `worker` agents, with
  hard caps (count, depth, budget) and cascade cancellation; workers hand back typed results and
  only the orchestrator composes user-facing text.
- **Least-privilege graph access, twice:** agents see only the MCP tool tiers the operator enabled
  on `fallen-8-mcp` (server side, read-only by default), and within those only the tools their
  role's allowlist admits (host side, enforced in the runner, not in the prompt).
- **The integrations deployment posture:** own container image, opt-in compose service
  (`F8_AGENTS=true`, profile `agents`), **no published port**, reached through the apiApp's
  authenticated proxy at `/agents/*`; startup posture logging in the apiApp's honest style.

**Non-goals**

- **A new agent framework, planner, or memory system.** Agent Framework as-is; where it has a
  primitive, we use it. No long-term memory or fact store of any kind. *Revisit trigger:* a task
  class that provably fails without cross-run memory, measured on the trace.
- **Provider awareness in the host.** No `Agents:Model:*` configuration exists, no provider SDK is
  referenced, no provider credential is held. The host knows a backend only as a name the
  instance's answer carried. *Revisit trigger:* an agent workload that needs a provider the
  instance does not offer, which is a model-providers feature request first.
- **Embedding agents in `fallen-8-core` or the apiApp.** Excluded by requirement 1. The engine does
  not change. The apiApp changes in the two ways section 7 states: the chat gateway grows, and a
  proxy controller for `/agents/*` is added.
- **A second graph bridge.** No graph route is ever called over REST from this project; if agents
  need a graph capability, it becomes an MCP tool on `fallen-8-mcp` first. The convention test in
  3.9 pins the host's REST surface to the chat gateway.
- **Per-request model choice.** D8 stands: the `purpose` enum names a job the server maps to a
  name it owns; there is no `model` field on a spawn or a chat call.
- **Server-side behaviour per purpose.** A purpose is a model name and nothing else; prompts,
  sampling and stop tokens stay the caller's. The moment a purpose would need behaviour of its
  own on the server, that is a new capability, not a purpose.
- **Durable agents.** Agents are in-memory; a host restart ends them (the trace says so).
  *Revisit trigger:* users routinely run agents that must survive deploys. (The integrations
  runtime's in-flight spool is the pattern to copy when that day comes.)
- **Multi-node swarms / distributed scheduling.** One host process, one swarm.
  *Revisit trigger:* a single host's model throughput becomes the bottleneck.
- **Token-by-token streaming to clients.** The feed streams per-step events, not deltas.
  *Revisit trigger:* an interactive UI needs typed-out responses.
- **A web UI.** This feature is the API. The proxy posture is what makes an F8 Studio panel a
  follow-up rather than a redesign: Studio already talks to the instance, and `/agents/*` is on
  it.
- **Sandboxing agent behaviour.** Tool tiers and allowlists bound what agents *can* do; what they
  *choose* to do within those tools is model behaviour (see Risks: prompt injection).
- **A phrase-scanning output validator.** Scanning the final text for banned wording was
  considered and rejected: it fits one prompt's phrasing and rots with it. The rule that swarm
  mechanics never reach user-facing text is a prompt rule plus the structural fact that only
  one agent composes (3.5).
- **An OAuth client flow against the MCP server.** The host presents a static bearer
  (`Agents:Mcp:BearerToken`) and works against a `None` or `StaticToken` MCP server; against an
  `OAuth` server it presents a pre-issued token and runs no acquisition flow. *Revisit trigger:*
  token lifetimes shorter than a host process.

## 3. Design sketch

### 3.1 Projects

```
fallen-8-agents/                 (new project, net10.0, ASP.NET Core, NoSQL.GraphDB.Agents)
  Program.cs                     host, posture log, DI wiring (explicit namespace, like the
                                 other two sidecars, so WebApplicationFactory is unambiguous)
  Configuration/AgentsOptions.cs         Agents:*  (this process)
  Configuration/Fallen8TargetOptions.cs  Fallen8Target:*  (the instance: the small copied options
                                         class the other two sidecars each keep, same spelling)
  Configuration/AgentsMcpOptions.cs      Agents:Mcp:*
  Model/Fallen8ChatClient.cs     IChatClient over POST /chat (purpose agent), on the
                                 fallen-8-rest-client seam; the ONLY REST caller in this project
  Runtime/AgentRegistry.cs       in-memory registry: id -> AgentHandle; caps; retention
  Runtime/AgentRunner.cs         one agent's run loop (Agent Framework), state machine
  Runtime/AgentTrace.cs          bounded per-agent step buffer, byte-capped tool captures
  Runtime/RoleCatalog.cs         roles, their prompts and tool allowlists
  Runtime/SwarmTools.cs          spawn_worker / await_workers tools (orchestrator role)
  Runtime/GroundingCheck.cs      cited tool-call ids vs the trace
  Prompts/assistant.md, orchestrator.md, worker.md   (embedded; refused empty at startup)
  Feed/AgentFeedDispatcher.cs    SSE fan-out (change-feed pattern)
  Hosting/AgentEndpoints.cs      the routes the proxy forwards to (/agent/*)
  Diagnostics/AgentsMetrics.cs   OTel meter + activity source
  Dockerfile
fallen-8-core-apiApp/
  Controllers/Model/ChatREST.cs          + purpose, tools, toolCalls, toolCallId   (3.1a)
  Chat/IChatBackend.cs                   + tool shapes on ChatTurn / options / result
  Chat/{Ollama,OpenAI,Anthropic}ChatBackend.cs   map tools with each SDK's native types
  Chat/ChatBackendFactory.cs             ResolveModel(options, purpose)
  Configuration/Fallen8ChatOptions.cs    Model -> Models:Assist (renamed, no alias), + Models:Agent
  Configuration/Fallen8SettingCatalog.cs four entries renamed, four added (Restart tier)
  Controllers/AgentsController.cs        the proxy: /agents/* -> host /agent/*
  Configuration/Fallen8AgentsOptions.cs  Fallen8:Agents:* (Enabled, Endpoint, TimeoutSeconds)
fallen-8-unittest/               agent-host tests live in the existing suite
```

**Package alignment.** `Microsoft.Agents.AI` 1.20.0 requires `Microsoft.Extensions.AI(.Abstractions)`
10.9.0; the apiApp pins 10.8.0 and the test project references both projects, so the solution
moves to 10.9.0 (and OllamaSharp to the release built against it). `ModelContextProtocol` is
pinned to the version `fallen-8-mcp` uses (1.4.1 today) for the same reason. All versions exact,
per the convention test.

MIT headers everywhere; `Try*(out, ...)` where it fits; MSTest; warnings-as-errors and the
convention tests apply unchanged, and `fallen-8-agents` joins the project lists in
`CodeQualityTest`, including the rule that REST-only deployables reference neither the engine nor
the apiApp.

### 3.1a The chat gateway learns to serve agents

`POST /chat` keeps its contract for every caller it has today and gains, as **additions only**:

| Where | Field | Meaning |
|---|---|---|
| request | `purpose` | `assist` (default, today's behaviour) or `agent`. A closed set of jobs the server maps to a model it owns; never a model name. Unknown value: 400 naming the allowed set. |
| request | `tools[]` | `{ name, description, parameters }`, `parameters` a JSON-schema object. Forwarded to the backend as that backend's native tool definitions. |
| request, assistant turn | `toolCalls[]` | `{ id, name, arguments }` the model produced on an earlier step, replayed as conversation history. |
| request, tool turn | `toolCallId` | which call the `content` answers. |
| response | `toolCalls[]` | the calls the model wants made, same shape; `content` may be empty when present. `model` names the model that served the call (the purpose's model), `backend` and `stats` as today. |

Rules, each with its reason:

- **A purpose is a server-owned model name and nothing else.** `Fallen8:Chat:<Backend>:Models:<Purpose>`,
  the set closed in code (`Assist`, `Agent`); one Restart-tier catalog entry per purpose per
  backend, each shown beside the others on the Configuration surface, in the config view and in
  the startup posture line, each probed for residency. Defaults in the Ollama block only:
  `Assist` is `phi4-f8-mini:latest` (today's value under its new name) and `Agent` is
  `phi4-mini:latest`, the stock tool-capable MIT model the sidecar can pull; no defaults in the
  other three blocks, exactly as `Model` had none there. A `purpose: agent` call against a block
  whose `Models:Agent` is empty answers 503 naming the key, the same shape an incomplete block
  answers today. Prompts, sampling and stop tokens stay the caller's, because NL assist and the
  agent host each own their prompt contract. The set is closed because every caller of a purpose
  is code; *revisit trigger:* an external client that needs a job neither purpose covers.
- **Why purposes rather than a second field.** A purpose is stable; the model behind it is what
  the operator fine-tunes. Today `Assist` points at the delegate fine-tune and `Agent` at a stock
  model; an agent fine-tune trained on Fallen-8's own MCP tools drops in as one setting change
  with no code touched and no client aware of it, and each purpose pins its own version tag
  independently. That is the shape that serves "keep fine-tuning assist models" and "run agents
  on general models" at the same time.
- **`Model` is renamed to `Models:Assist`, with no alias** (operator decision, 2026-09-09). Two
  spellings for one setting is the duplication this repository refuses, and the fine-tune itself
  was renamed the same way. An instance still carrying the old key fails closed with a message
  naming the new one; the one-time sweep the rename costs is listed in section 7. The compose
  variables an operator sets (`F8_NAHIL_CHAT_MODEL` and its siblings) keep their names; only the
  instance keys they map to change.
  - **That fail-closed took code, and the first version of this bullet claimed it for free**
    (review finding, fixed). It said the stale key fails closed "exactly as an incomplete block
    always has", and nothing did it: configuration binding ignores a key no property claims,
    silently, so on Ollama and Nahil, whose `Models:Assist` carries a default, the operator's
    model was REPLACED by a stock one on every request with nothing said. A fine-tuned assist
    model swapped for the sidecar's default is the worst shape a configuration fault can take,
    because the instance keeps answering. `ChatBackendFactory.StaleModelKey` now reads the RAW
    configuration, which is the only way to see a key that binds to nothing, and refuses the
    SELECTED backend's block by name through the same `Validate` the boot warning and the 503
    share. A stale key in a block nobody selected refuses the day it is selected.
- **Tools are mapped per backend with the SDK's native types**, for the reason the backends are
  native today: they forward generation stats the generic abstraction does not expose. If mapping
  four ways proves heavier than expected, the implementer may compose the generic clients from
  the SDKs inside the backends and read stats from the raw representation; the wire contract
  above does not change either way.
- **A request carrying tools is not streamed** (`Fallen8:Chat:Stream` is otherwise untouched), and
  this sentence has been wrong twice, so it is worth being exact. Phase 0 measured parsed tool
  calls on Nahil both streamed and non-streamed, which is a fact about the WIRE, and this section
  concluded from it that no fallback was needed. Implementing it showed the constraint lives one
  layer up, in the client libraries: OllamaSharp documents its tools field as requiring a
  non-streamed request, and the OpenAI SDK delivers streamed calls as fragments to reassemble. So
  the fallback is back, as a rule stated once on `ChatBackendOptions.Tools` and obeyed by all three
  backends. No request without tools is affected.
- **The gates are the existing ones.** The Chat capability must be on; the sensitive rate-limit
  policy applies, so a swarm counts against `Fallen8:Security:SensitiveRateLimitPermitPerWindow`
  and that existing knob is the one to raise; the per-request deadline is
  `Fallen8:Chat:TimeoutSeconds`, the single one, with the Nahil waits inside it. Anthropic's
  no-sampling-parameter rule and the per-request stop tokens are unchanged.
- **MCP coverage:** `POST /chat` stays the conscious deferral it is today; the new fields do not
  change that decision.

### 3.2 Agent model

- **Roles:** `assistant` (default: one agent, one task or conversation), `orchestrator`
  (additionally gets the swarm tools) and `worker` (assistant-shaped, spawned only by an
  orchestrator, answers to it with a typed result). Each role has a prompt file and a **tool
  allowlist** (`Agents:Roles:<role>:Tools`, MCP tool names; empty means every tool the server
  advertises). The allowlist is applied to the tool list the runner hands the agent, so a tool
  outside it is not merely discouraged by the prompt: the model never sees it. Defaults:
  `assistant` and `worker` see every advertised tool; `orchestrator` sees `f8_overview` and the
  swarm tools, because an orchestrator that can look but must delegate decomposes better than one
  that can do everything itself. The MCP server's tiers remain the server-side bound; the
  allowlist can only narrow.
- **States:** `pending -> running <-> waitingForUser -> completed | failed | cancelled |
  budgetExceeded`. `budgetExceeded` carries which budget: `tokens`, `steps`, `toolCalls` or
  `time`. Every transition is a feed event and a trace step.
- **Spawn request:** `{ task, role?, name?, tokenBudget?, systemPromptAppendix? }`. There is no
  `model`: the instance owns it (D8). `tokenBudget` defaults to `Agents:Limits:DefaultTokenBudget`.
- **Per-run caps, all enforced in the runner at the framework's per-step seam, not by the
  model:** `MaxStepsPerRun` (model calls; where the framework has the knob, its
  maximum-iterations setting is that knob), `MaxToolCallsPerRun`, `MaxRunSeconds` (wall clock,
  measured by the host, because reported durations do not cover a remote provider's routing and
  verification passes) and the token budget. A breach ends the run as `budgetExceeded` with the
  budget named. Defaults in 3.8, derived from the measurements in section 5.
- **The runner** builds a `ChatClientAgent` over `Fallen8ChatClient`, with the MCP tools fetched
  from `fallen-8-mcp` at startup (tool list refreshed on reconnect), filtered by the role's
  allowlist, plus the swarm tools for orchestrators. The agent's session persists for the agent's
  lifetime, so `POST .../messages` continues one conversation. The host's deadline on a chat call
  (`Fallen8Target:TimeoutSeconds`) sits **above** the instance's chat budget, for the reason the
  other two sidecars state: two competing deadlines make the nearer one report a vague local
  failure instead of the downstream answer that names what to change.
- **Trace:** every step is recorded: `modelCall` (duration as the host measured it, `backend` and
  `model` as the instance reported them, usage delta, or an `unreportedUsage` marker),
  `toolCall` (tool-call id, tool name, arguments capped at `Agents:Trace:ArgsBytes`, result capped
  at `Agents:Trace:ResultBytes`, `truncated`, total bytes, duration, success, and `error` when it
  failed, which is the framework's own message and is what the model was told too), `message`
  (direction, `messageId`, `inReplyTo`, `text` capped like a result), `spawn`, `citationCheck` and
  state changes. Every field in these lists exists; `error` and `text` are here because they
  shipped while the lists omitted them, so a consumer written from this section dropped the one
  field that says WHY a tool call failed. The buffer is
  bounded (`Agents:Trace:MaxSteps`, oldest dropped with a marker step): review needs recency, not
  an unbounded archive. Provenance is therefore **per step**, not per host: a deployment that
  switches backend mid-day shows it in the trace.
- **Grounding check.** The role prompts ask the agent to cite, in its final text, the tool-call
  ids its figures come from as `[t:<id>]`. On completion the runner counts cited ids that exist in
  the trace and those that do not, records a `citationCheck` step and carries
  `citations: { valid, dangling }` on `agentCompleted`. It is a mechanical count, no judge model
  and no claim schema; it exists so a reviewer can see at a glance whether an answer points back
  at work that actually happened.
- **Retention.** Finished agents stay listed and reviewable for `Agents:Limits:RetainFinishedMinutes`,
  bounded by `MaxRetainedAgents` (oldest finished evicted first). A host restart ends everything;
  the first trace step of every agent says which host instance ran it.
- **Posture check at startup.** The host calls `GET /chat/models` ONCE, bounded and best-effort,
  and logs the outcome: reachable and how many models the instance's backend catalogues, or a
  401/403 that means the Chat capability is off or the key is wrong, or unreachable. It never
  gates and it never learns the agent purpose's model name that way: the model is server-owned
  and is revealed per step by the instance's answer, which `GET /agents/status` then reports as
  `lastSeen { backend, model }`.
  - **Once means once, and the status route now says WHEN** (review finding, fixed). Nothing
    re-probes and no failed completion downgrades the word, so in the case this design itself calls
    ordinary, compose starting the host before the instance answers, it reported `unreachable` for
    the life of the container while every agent ran fine. It is reported beside a `probedAt`, and
    the route that calls it the first thing to read when an agent fails now says what it is: a
    startup probe, to be read against that timestamp and against `lastSeen`, which only a
    completion that actually happened can set. Refreshing it from the adapter was rejected, because
    the probe and the last completion are two different facts and this type's own doc says
    conflating them would let a host that has never run an agent report a model.
  - The caps that row reports include `maxTokenBudget`, which it omitted: it CLAMPS a caller's own
    `tokenBudget` rather than refusing it, so the one cap that silently rewrites a request was the
    one cap the posture surface hid, and there was no pre-flight way to learn it.

### 3.2a The role prompts are load-bearing, and why

Phase 0 measured what decides whether a small model's tool call is parsed at all, and it is not
the transport: it is the prompt. The same model, tool schema and temperature produced parsed tool
calls under a system message that named the tools and forbade invented results, and produced the
literal text `<|tool_call|>` followed by a **fabricated result** under a bare imperative. A
fabricated result is the worst failure this feature can have, because it looks like an answer.

So each role prompt in `Prompts/` must, as a contract rather than a style preference:

1. state that tools exist and that a question needing data is answered by calling one,
2. forbid inventing, guessing or predicting a tool's result,
3. require the `[t:<name>]` citation the grounding check counts (3.2; see the amendment below
   for why it is the tool's NAME and not a tool-call id),
4. and for `orchestrator` and `worker`, state the one-composer rule (3.5).

Two consequences follow. The prompts ship **embedded and are refused when empty at startup**, so a
missing prompt cannot degrade silently into a fabricating agent. And a prompt change is a
behaviour change: each prompt is covered by a test asserting these four properties are present, so
an edit that drops one fails the suite rather than the next agent run.

> **Amendment (2026-09-10, Phase 1b): against the shipped agent model this section's premise is
> reversed, and the prompts ship anyway.**
>
> Phase 1b measured the same question again, this time through the whole shipped path and with the
> role prompts as written. On `phi4-mini:latest`, the only tool-capable model the configured
> platform serves, **any** instruction text stops the model emitting a real tool call: with a bare
> user turn it calls the tool 4/4, and with a system prompt - the shipped one, a terse one, a
> persona-only one, or one that never mentions tools - it writes the call as prose or invents a
> figure, 4/4 in every arm. Moving the instructions into the user turn does not help, streaming does
> not help, and sending the same bodies straight to the platform with Fallen-8 out of the picture
> behaves identically, which is what rules out our own gateway.
>
> So the four properties above remain the right contract for a model that can follow one, and the
> prompts ship unchanged (decision 2026-09-10). What this section may no longer claim is that they
> make tool calling work: on the default deployment they are what stops it. That is a model
> limitation with a named revisit trigger rather than something to code around, and the full table,
> the fabrication samples and the nine other model names that do not resolve are in
> [findings.md](./findings.md) section 1.
>
> One correction of detail, from the same measurement: property 3 asked for a `[t:<id>]` citation
> using the tool-call id. **No backend shows a tool-call id to a model** - it travels on the
> protocol, not in the result the model reads - so asking for one asked the model to invent one, in
> the prompt whose job is to stop invention. The marker is now `[t:<name>]`, the tool's own name.

### 3.3 Control-plane API

The host serves its routes under `/agent/*` on an unpublished port; the apiApp proxies them as
`/agents/*`, Fallen-8-level, gated by a new `Agents` capability: the integrations proxy's posture,
and its client base. **With the capability off the answer depends on whether the instance has a
key**, and this sentence used to say 403 unconditionally. The shared policy pairs
`RequireAuthenticatedUser` with the capability requirement, so an anonymous caller is challenged
before the capability is read: a keyed instance answers **403**, a keyless one (a bare
`dotnet run`) answers **401**. Both are pinned by test, because a client that reads only 403 as
"the feature is absent" shows a broken screen on exactly the second instance. The proxy invents exactly one status, a
503 for an unconfigured or unreachable host; everything the host answered passes through.

The routes are **unversioned and Fallen-8-level**, like the integrations proxy: the controller's
actions carry absolute templates, which by ASP.NET's routing rules discard the class-level
`api/v{version}/[controller]`. This table spelled every row `/api/v0.1/agents...` while the
regenerated OpenAPI snapshot carried `/agents...`, so seven of its eight rows were unreachable as
written and it contradicted its own prose four lines above.

| Method and route (apiApp) | Purpose |
|---|---|
| `POST /agents` | Spawn; 202 with the agent id and initial state |
| `GET /agents` | All agents: id, name, role, state, task, parentId, tokens {input, output, total}, steps, toolCalls, durationMs, budget, createdAt, lastActivityAt |
| `GET /agents/{id}` | One agent, incl. children ids, citations and the last N trace steps |
| `GET /agents/{id}/trace` | The full retained trace |
| `POST /agents/{id}/messages` | User to agent message; 202 with a `messageId`; 409 unless the agent can accept one (running/waitingForUser). **Deferred, 2026-09-10; see 3.4a**, so it is in no snapshot and answers 404 |
| `DELETE /agents/{id}` | Cancel; cascades to live descendants |
| `GET /agents/status` | Host posture: chat gateway reachability, the last backend and model seen, MCP target, tiers seen and tool count, caps, active and retained counts, and the event kinds this host EMITS beside the ones its filter ACCEPTS |
| `GET /agents/feed` | SSE stream (3.4), streamed through the proxy |

The reply to a user message arrives on the feed (and in the trace) as an `agentMessage` event
carrying `inReplyTo: <messageId>`; the POST returns 202 immediately, because a step on a remote
provider takes tens of seconds and the control plane never blocks on inference.

Cancellation is cooperative and honest: the token is observed between steps and passed into the
in-flight chat call and tool call, so a tool call already sent to the graph is not undone; the
trace's last step says so.

### 3.4 Event feed

Same delivery contract family as the change feed (SSE, `id:`/`event:`/`data:`, keep-alive
comments, declarative query-string filters, here `agents` and `kinds`), same
parser-not-compiler stance: unknown filter values are a 400, never a silently empty stream. Event
kinds:

`agentSpawned, agentStateChanged, agentMessage, toolCalled, agentCompleted, agentFailed`

Every event carries `{ seq, ts, agentId, parentId?, kind, role, name, state, tokens, ... }`: the
role, the name and the state are on EVERY kind rather than only on a state change, because a
subscriber rendering a list should not have to join against the listing to label a row. `agentMessage` carries the text,
direction and `messageId`/`inReplyTo`; `toolCalled` carries the tool-call id, tool name, the
capped argument and result summaries with `truncated` and total bytes (never full payloads; the
trace is the place to re-fetch); `agentStateChanged` carries the counters so a subscriber can
render live cost without polling, and so does every other kind, for the same reason; `agentCompleted`
and `agentFailed` carry the result text (capped, with `truncated`), `failure`, `durationMs`, and the
citation counts when a check ran. The counters are `input`, `output`, `total`, `steps` and
`toolCalls`, plus `unreportedUsage`: this said "the four counters" and there are five and a flag,
and the flag is the one that distinguishes a backend reporting zero from a backend reporting
nothing. The proxy forwards the stream with response-headers-read semantics and flushes
per event; this streaming forward is the one new arm on the shared proxy client base. No catch-up
buffer in v1 (`GET .../trace` is the catch-up mechanism); *revisit trigger:* a UI that must survive
reconnects without re-fetching traces.

### 3.4a The conversation route is deferred, and what it would actually have added

**Decided 2026-09-10 (Phase 2).** `POST .../messages` is not implemented, and the reason is that
almost nothing about it is this host's business.

Microsoft Agent Framework owns how an agent interacts. `AgentSession` holds the history and another
turn is `RunAsync(message, session)` on the same session; Phase 0 confirmed a session carries
multiple turns. So multi-turn is a capability this host already has, not one it has to design.

What the route would add is not the conversation. It is three pieces of host bookkeeping the
framework has no opinion on, and each is a decision rather than an implementation:

1. **Does a parked agent keep its concurrency slot?** It is alive and can take another message, so
   it does; and `MaxConcurrentAgents` defaults to 4, so four parked agents block the host. That is
   the same shape as the wedge Phase 1b fixed, arriving by design instead of by accident.
2. **What wall clock applies between turns?** `MaxRunSeconds` is per RUN and its deadline currently
   spans one turn. Spanning the agent instead is a different meaning for the same key.
3. **What makes a parked agent `completed`?** Nothing does. An agent that can always take another
   message never reaches an ending on its own, so retention never starts and `agentCompleted` never
   fires. An idle cap would answer it, and an idle cap is exactly the kind of bespoke lifecycle this
   host should not be inventing on the framework's behalf.

None of the three has a caller yet: no UI, no CLI and no test needs a second turn. Answering them
without one would be guessing, and each guess is visible in configuration forever. So the route
waits for the phase that has a real consumer, which is the Studio work in Phase 5; the framework
side of it needs no further proof.

What ships instead is the observability the same phase was for: the trace, the event feed and the
grounding check. An agent answers its task and completes, which frees its slot at once and fires
`agentCompleted` with its citation counts, and that is a whole interaction.

*Revisit trigger:* a client that needs a second turn. At that point the three questions above are
what to decide, in that order.

### 3.5 Swarm mode

- Orchestrators get two extra function tools, implemented on the registry:
  - `spawn_worker(task, name?)` returns a worker agent id (a real agent: listed, traced, budgeted,
    cancellable like any other, `parentId` set),
  - `await_workers(ids?)` returns each worker's **typed result**
    `{ id, state, result, citations, tokens, steps, toolCalls }`, never free prose about what the
    worker did.
- **Exactly one composer.** Workers answer their orchestrator; only the orchestrator's final text
  is the swarm's user-facing result. Agent names, spawns and awaits are feed events and trace
  steps, and the role prompts say they are never narrated in the result. (The rejected validator
  is in section 2.)
- **Caps, all configuration, all enforced in the registry:** `MaxConcurrentAgents`,
  `MaxSwarmDepth` (an orchestrator's workers cannot themselves orchestrate by default),
  `MaxWorkersPerOrchestrator`. A breached cap is a tool error the orchestrator sees and a
  `toolCalled(success=false)` event the user sees.
- Cancelling an orchestrator cancels its live descendants; a worker finishing feeds its result to
  the awaiting orchestrator via Agent Framework's primitives (handoff/concurrent patterns), no
  bespoke scheduler.

### 3.6 Tokens, counters and observability

- After every chat call the usage the instance reported (`stats.promptTokens`,
  `stats.completionTokens`, mapped to `UsageDetails` by the adapter) is added to the agent's
  counters. Unreported usage is counted as zero and recorded as an `unreportedUsage` trace step;
  the numbers shown are **never estimated**. Expect this step to be routine on Nahil: a tool-call
  reply came back with a completion count of 0 (verified 2026-09-09), which is the reason steps,
  tool calls and wall clock are counted beside tokens rather than instead of them.
- Budget checks run after each step: over any budget means `budgetExceeded`, the loop stops, the
  feed says which budget. Orchestrator budgets bound only their own calls; the global token cap is
  `MaxConcurrentAgents x DefaultTokenBudget` by construction.
- `AgentsMetrics` (observability-feature style, containment included), one meter
  `NoSQL.GraphDB.Agents`: `f8a.agents.tokens` (counter, tags: direction, role),
  `f8a.agents.active` (gauge), `f8a.agents.completed` (counter, tag: outcome incl. the budget
  name), `f8a.agents.step.duration` (histogram, wall clock per chat call, tag: backend as the
  instance named it, a closed set), `f8a.agents.tool.calls` (counter, tags: tool, success), plus
  Agent Framework's own OTel GenAI spans wired into the exporter configuration. Tag hygiene rule
  (no user input in tag values, so no task text and no agent name) applies unchanged; tool and
  backend names are closed sets and are safe. The host declares the same fleet identity as the
  instance it fronts (`Agents:Identity:*`), as the other two sidecars do.

### 3.7 Security posture

- **No published port.** The host binds loopback by default (`dotnet run`), `0.0.0.0` in the
  container, and the compose service publishes nothing; the apiApp's proxy is the only way in, so
  the host needs no second auth story. Publishing the port "to debug it" is the instinct this
  posture rules out.
- **Two credentials, both the operator's, both configuration, neither a provider's:** the
  instance's API key (`Fallen8Target:ApiKey`, reusing `F8_API_KEY` exactly as the other two
  sidecars do, presented on the chat gateway only) and the MCP bearer (`Agents:Mcp:BearerToken`).
  Agents never see either. **No model-provider credential exists on the host**, which is the
  point of the route: the Nahil, OpenAI or Anthropic key lives on the instance and nowhere else.
- **One route family over REST, pinned.** The host's REST calls are `POST /chat` and
  `GET /chat/models`. A convention test holds the host's route list to that family against the
  OpenAPI snapshot, the way `McpContractTest` pins the bridge, so a graph route can never be added
  quietly.
- Tool tiers are decided on `fallen-8-mcp`, not here: read-only by default. This spec recommends
  keeping agent-facing MCP servers read-only unless a task concretely needs writes, and the
  per-role allowlist narrows further.
- Startup posture log: bind, proxy-only posture, instance URL and chat-gateway probe outcome
  (never the key), MCP target + tiers reachable + tool count, caps and default budget.

### 3.8 Configuration

Host (`Agents:*` for this process, `Fallen8Target:*` for the instance it asks):

| Key | Default | Note |
|---|---|---|
| `Agents:BindAddress` / `Port` | `127.0.0.1` / `8120` | container sets `0.0.0.0`; never published |
| `Fallen8Target:BaseUrl` / `ApiKey` / `ApiKeyHeader` | `http://localhost:8080` / none / `X-Api-Key` | the same spelling the other two sidecars use |
| `Fallen8Target:TimeoutSeconds` | `630` | above the largest chat budget a shipped profile sets (600 on Nahil), for the two-deadlines reason |
| `Agents:Mcp:Endpoint` / `BearerToken` | `http://localhost:8090` / none | |
| `Agents:Roles:<role>:Tools` | see 3.2 | MCP tool names; empty = all advertised |
| `Agents:Limits:DefaultTokenBudget` | `100000` | |
| `Agents:Limits:MaxStepsPerRun` | `24` | |
| `Agents:Limits:MaxToolCallsPerRun` | `48` | |
| `Agents:Limits:MaxRunSeconds` | `1800` | a cap, not a target; see section 5 |
| `Agents:Limits:MaxConcurrentAgents` | `4` | Nahil's hourly quota, the step latency and the instance's rate-limit window all argue for few |
| `Agents:Limits:MaxSwarmDepth` | `2` | |
| `Agents:Limits:MaxWorkersPerOrchestrator` | `4` | |
| `Agents:Limits:RetainFinishedMinutes` / `MaxRetainedAgents` | `60` / `200` | |
| `Agents:Trace:MaxSteps` / `ArgsBytes` / `ResultBytes` | `1000` / `2048` / `8192` | |
| `Agents:Feed:KeepAliveSeconds` | `15` | |
| `Agents:Observability:Otlp:Endpoint`, `Agents:Identity:*` | as the other sidecars | |

Instance:

| Key | Default | Note |
|---|---|---|
| `Fallen8:Chat:<Backend>:Models:Assist` | Ollama `phi4-f8-mini:latest`, others none | the renamed `Model`; Restart tier, catalogued |
| `Fallen8:Chat:<Backend>:Models:Agent` | Ollama `phi4-mini:latest`, others none | required for `purpose: agent` on that backend; the overlays set it |
| `Fallen8:Agents:Enabled` | `false` | the `/agents/*` routes answer 403 |
| `Fallen8:Agents:Endpoint` | empty | the proxy answers 503 rather than timing out |
| `Fallen8:Agents:TimeoutSeconds` | `30` | the small routes; the feed is a stream and takes none |

Compose: `F8_AGENTS=true` activates the `agents` profile, sets the two instance keys and makes the
sidecar pull the agent model. The base file and the overlays map their existing chat-model
variables to the renamed key (`F8_NAHIL_CHAT_MODEL` to `Fallen8__Chat__Nahil__Models__Assist`, and
likewise for the sidecar, OpenAI and Anthropic); the `nahil` overlay sets `Models__Agent` from
`F8_NAHIL_AGENT_MODEL` (default `phi4-mini:latest`, which Nahil catalogues); the `openai` and
`anthropic` overlays set their `Models__Agent` from the chat model variable by default, since
those are general models. All of it lands on the `fallen8` service: the `f8-agents` service
carries no model setting at all.

### 3.9 Test harness

- **No live model in CI.** The runner is tested against a scripted `IChatClient` fake
  (deterministic replies, tool-call sequences including a malformed sibling call, usage numbers
  including zero): lifecycle, trace, every budget and cap, allowlists, cascade cancel, feed events,
  grounding counts, retention are all assertable without a model.
- **The chat gateway's growth is tested where the gateway is tested:** the `FakeChatBackend` seam
  `ChatEndpointTest` already uses gains tools and tool calls; each backend's mapping is tested with
  the handler-injected transport fakes the backends already accept; `purpose` to model
  resolution, the 503 for an empty purpose and the 400 for an unknown one are
  `ChatBackendFactoryTest` and `ChatEndpointTest` cases; the setting-catalog equivalence tests pick
  up the renamed and added keys.
- **`Fallen8ChatClient` is tested against a hosted apiApp** with a fake backend (the seam above),
  so the adapter's wire mapping is proven against the real controller, not a mock of it.
- **An in-process stub MCP server** (the real protocol over Streamable HTTP via
  `ModelContextProtocol.AspNetCore` under `WebApplicationFactory`) with configurable latency and
  error injection, for runner tests that need real tool plumbing; and one integration test wiring
  the real `fallen-8-mcp` in process against a hosted apiApp, as `McpBridgeTest` already does.
- **Proxy tests** via `WebApplicationFactory` over the apiApp: capability gate (403/401), the one
  invented 503, pass-through of host statuses, SSE frame format and filter grammar through the
  streaming forward, and the no-request-body-in-logs rule the integrations proxy pins.
- **Convention:** `fallen-8-agents` in `CodeQualityTest`'s lists and in the REST-only rule; the new
  route-family pin (3.7).
- A **gated** live smoke test (skips cleanly unless an instance with the Chat capability is
  configured; in this repository's environment that instance routes to Nahil) runs one real
  agent end-to-end with one tool call. The Phase 0 harness graduates into it.

## 4. Acceptance criteria

- **Spawn/review round-trip.** Spawn an assistant on a task through the proxy; `GET /agents` shows
  it running with growing counters; the trace shows chat and tool steps, each chat step naming the
  backend and model the instance reported; the feed streamed the same as events through the
  proxy; the final `agentCompleted` carries the result and its citation counts.
- **Provider-agnostic by construction.** The host has no model configuration and no provider
  package; switching the instance's chat backend changes what the trace names and nothing on the
  host.
- **The gateway is unchanged for its existing callers.** Every existing chat test passes
  unchanged apart from spelling the renamed key; a request without `purpose` and `tools` behaves
  exactly as before; D8 holds (no client names a model).
- **Purposes are the only model selector.** `Models:Assist` serves NL assist under its new name,
  `Models:Agent` serves agents, no other key names a model, and the old `Model` key is refused
  with a message naming the new one.
- **Conversation.** *(Deferred 2026-09-10, see 3.4a: the framework already supports a second turn;
  what is undecided is slot, clock and ending, and no client needs it yet.)* A `waitingForUser`
  agent accepts `POST .../messages`, the POST returns a
  `messageId`, and the reply arrives as an `agentMessage` feed event with `inReplyTo` on the same
  conversation.
- **Counters are real.** Token counters equal the sum of instance-reported usage exactly, with an
  `unreportedUsage` step wherever the instance reported nothing; each of the four budgets stops
  the agent with `budgetExceeded` naming that budget and a feed event.
- **Swarm.** An orchestrator spawns workers visible as first-class agents (`parentId` set); caps
  are enforced as tool errors; cancelling the orchestrator cancels its live workers;
  `await_workers` delivers typed results; only the orchestrator's text is the result.
- **Least privilege, twice.** With `fallen-8-mcp` at default tiers an agent has read tools only;
  with a role allowlist the model is never offered a tool outside it; no credential ever appears in
  any trace, event or log line.
- **Separate deployable, proxy-only, one REST family.** Runs as its own container with no F8
  assemblies loaded and no published port; its REST calls are the chat gateway only, pinned by a
  test; `docker compose up` default is unchanged unless `F8_AGENTS=true`.
- **Suite green, build clean,** the engine untouched.

## 5. Risks

**Phase 0 ran on 2026-09-09 and the gate passed.** Measured against Nahil with
`phi4-mini:latest` (routable, warm), a one-tool schema, temperature 0, direct to the provider (the
instance hop is not in these numbers), plus a framework probe built and run on net10.0:

| Observation | Consequence in this spec |
|---|---|
| Parsed `tool_calls` came back **both streamed and non-streamed** | streaming is NOT the variable and `Fallen8:Chat:Stream` needs no exception for tools; the earlier `stream=false` fallback is dropped from 3.1a |
| Streamed returned ONE clean call; non-streamed returned two, the first carrying the parameter schema as its arguments | streaming is the better shape here, and the runner plus the scripted fake must still tolerate a malformed sibling call |
| **Prompt shape decides it.** A system message naming the tools and forbidding invented results: 2/2 parsed. A user turn ending "Use the tool to find out.": 5/5 parsed. A bare "Use the tool." or a bare imperative: 0/3, emitting the literal text `<\|tool_call\|>` followed by a **fabricated result** | the role prompts are load-bearing, not cosmetic (3.2a). This is the concrete form of the known upstream flakiness |
| Completion token count was 0 on one tool-call reply; a warm step ranged 0.3 s to 41 s while the reported total duration said 45 ms | tokens alone undercount and reported durations are not wall clock, so steps, tool calls and host-measured wall clock are budgets too |
| The catalog said `completion` only, never `tools` | no gating on a tools capability anywhere; the posture probe logs, it does not refuse |
| `Microsoft.Agents.AI` 1.20.0 + `Microsoft.Extensions.AI` 10.9.0 + `ModelContextProtocol` 1.4.1 restore and build on net10.0; `ChatClientAgent` ran the tool loop with **no** `UseFunctionInvocation` wiring, delivered tools to the chat client as `ChatOptions.Tools`, and carried a session across two turns | the framework choice and the adapter seam are confirmed as specified. `ChatClientAgentOptions` has no `Instructions` property (it is on `ChatOptions`), and `AgentResponse.Usage` is the RUN aggregate, so per-step usage is counted at the adapter, not read off the response |

- **The fallback ladder has no second rung on Nahil today.** `qwen3:4b` and `qwen3:8b` resolve in
  Nahil's catalog but answer 503 with `no attached worker serves class S1`, explicitly saying
  retrying will not help; every other tool-capable model probed (llama3.2, qwen2.5, mistral-nemo,
  hermes3, granite, gpt-oss, the fp16 phi4-mini variant) answers 404. So on a Nahil deployment the
  agent model is `phi4-mini:latest` plus a disciplined system prompt, and widening the ladder is a
  **Nahil-side ask** (a worker subscribing to the class, or the model named in that platform's
  wanted-models list) recorded like the other Nahil-side dependencies. On the local sidecar and on
  the hosted providers the ladder is unconstrained.
- **A permanently unservable model is currently reported as a warm-up.** That same 503 carries no
  `Retry-After`, and `NahilWarmupRetryHandler` retries any 503 with a backoff until the caller's
  budget expires, so a 600 s chat budget spends ten minutes and then says the model "was not
  available in time" when the very first response said retrying is futile. For an agent that is a
  whole wall-clock cap burned on one step. Fixed in the same push as the default-backend flip
  (see [nahil-default-backend](../../done/nahil-default-backend/spec.md)), because a default that fails
  closed has to fail closed HONESTLY.
- **Four-way tool mapping.** Tools reach four SDKs with four native shapes. Bounded, tested per
  backend against transport fakes, and 3.1a names the escape hatch if it proves heavier than
  expected.
- **The rename is wide but shallow.** It touches every place that spells the chat model key
  (section 7 lists them). It is mechanical, it lands in one phase, and an instance that missed it
  says so at startup rather than serving the wrong model.
- **Quotas, latency and the rate limit.** Nahil's per-key hourly token budget answers 429, which
  the instance waits out inside its chat budget; four concurrent agents on that key will stall
  rather than fail, and that is intended. The instance's sensitive rate-limit window bounds a
  swarm too. The defaults in 3.8 are conservative for these reasons and are configuration.
- **Prompt injection via graph data:** graph property values flow into agent context through
  tool results; hostile data can steer an agent. Mitigations: read-only default tiers, role
  allowlists, budgets, full traceability (every tool call is visible), the grounding count, and
  the mcp-server spec's standing guidance. Not solvable here; stated honestly.
- **Framework velocity:** Agent Framework is stable but releases monthly; pin exact versions and
  keep the runner behind our own thin `AgentRunner` seam so an API change is one file's blast
  radius.
- **License nuance:** Agent Framework is MIT; the MCP C# SDK (a dependency of both this and
  mcp-server) is Apache-2.0 since 1.0. Permissive and compatible, but "all MIT" would be
  inaccurate; recorded so the posture stays honest.

## 6. Keep (do not regress)

- **`fallen-8-core` is untouched.** Agents are a client-side workload; every graph capability they
  need arrives as an MCP tool first.
- **The mcp-server trust chain:** this host is just another MCP client; it never bypasses tiers.
- **D8 and the model-providers rules stay the instance's:** the server owns every model name,
  the provider credential lives on the instance and on no read surface or log line, one deadline
  per call, explicit model tags. The host adds no second home for any of it.
- **The chat gateway's existing contract:** additions only on the wire, every current caller
  unchanged; the one configuration rename is the whole of the breaking surface, and it fails
  closed with the new key's name.
- **The integrations deployment posture** (no published port, proxy-only, one invented status,
  no request body in the proxy's logs).
- **The observability containment rule** (instrument callbacks never fault the observed) and tag
  hygiene, applied to every new meter.
- **The change-feed SSE conventions:** one house style for event streams, not two.
- **Compose default behaviour** and the repo's test bar (MSTest, edge cases, no live-model
  dependence in CI).

## 7. Impact on existing features

| Layer | Impact |
|---|---|
| The engine (`fallen-8-core`) | **No change.** |
| The chat gateway (`fallen-8-core-apiApp`, `/chat`) | **Additions only on the wire** (3.1a): `purpose`, `tools`, `toolCalls`, `toolCallId`; tool shapes on `IChatBackend` and its three types; native tool mapping in the Ollama-protocol, OpenAI and Anthropic backends; `Models:Assist` (renamed from `Model`, no alias) and `Models:Agent` on the four backend blocks, four catalog entries renamed and four added; `ChatBackendFactory.ResolveModel(options, purpose)`; the startup posture line, the config view and the residency probe report per purpose. NL assist and Studio send neither new field and see no change. |
| The `Model` rename | **One-time sweep, no alias, in one phase:** `Fallen8ChatOptions`, `ChatBackendFactory`, `ChatModelCatalog` and the residency probe, `Fallen8SettingCatalog`; `docker-compose.yml` and the `nahil`, `openai` and `anthropic` overlays (the `F8_*_CHAT_MODEL` variables keep their names, only the keys they map to change), `.env.example`; Studio's picker key in `ConfigurationSurface.tsx`; the docs pages that spell the key (`nahil.md`, `model-providers.md`, `running.mdx`, `nl-assist.md` where it does); the Configuration screenshots; every test that spells it. An instance still carrying the old key fails closed naming the new one: a boot WARNING and a 503 on the chat routes, from the one `Validate` both read, because the boot line is deliberately not a startup failure (a chat misconfiguration must not take a graph database down). That took `ChatBackendFactory.StaleModelKey` reading the raw configuration; see 3.8, where the first version of the promise is corrected. The fine-tune fixtures and `RETRAIN-LOG.md` do not spell the key. |
| The proxy (`fallen-8-core-apiApp`, `/agents/*`) | **Seven proxied operations over five paths, one options class, one capability arm.** Seven, not the eight this row claimed in four places: `POST /agents/{id}/messages` was deferred in the same commit that added 3.4a, and 3.3's table was updated while this row was not. A reviewer reconciling the snapshot against this table counts seven and cannot tell a deferral from a route dropped in a rebase, which is the one thing the sweep exists to make visible. `AgentsController` (Fallen-8-level, `Fallen8.Agents` policy) on the shared sidecar-proxy client base, which gains one streaming-forward arm for the feed; `Fallen8AgentsOptions`; an `Agents` arm in `DynamicCapabilityAuthorization.Capability`. `Microsoft.Extensions.AI.Abstractions` moves to 10.9.0 with OllamaSharp following. |
| The pinned OpenAPI snapshot | regenerated with `scripts/update-openapi-snapshot.ps1`, additions only: the seven operations (`/agents` GET and POST, `/agents/feed`, `/agents/status`, `/agents/{id}` GET and DELETE, `/agents/{id}/trace`) and the new chat fields. Phase 1a's six documented removals are the one exception, and they are recorded in findings.md. |
| `NamespaceEndpointTest` | the implementation chose the PREFIX rule, as `/savegames` has: one `path.StartsWith("/agents")` entry rather than one per route, so a route added later needs no gate change. This row offered both and named the per-route count first, which is not what shipped. |
| The MCP coverage gate (`McpRestCoverageTest`) | `POST /chat` stays deferred, unchanged. **One new deferral rule, with its reason,** for the `/agents/*` family: agents compose agents through the orchestrator role's swarm tools, inside the host's caps; bridging spawn to the MCP server would put agent creation behind the graph's tool tiers, where none of those caps apply. *Revisit when an agent outside the host needs to delegate to hosted agents.* |
| `CodeQualityTest` and the standing gates | `fallen-8-agents` joins `_allProjects`, `_productProjects` and the REST-only rule (`TheRestOnlyDeployables_ReferenceNeitherTheEngineNorTheApiApp`); a new test pins its REST route family to the chat gateway; warnings stay errors. The test project gains the project reference. |
| `fallen-8-mcp` | **No change.** It gains its first in-repo client; its docs page gains a pointer. |
| F8 Studio (`fallen-8-web-ui`) | **No agent UI in v1** (non-goal). The Configuration surface renders `/config`, so the purpose keys appear without code; the catalog picker binds one key today (`ConfigurationSurface.tsx`, the `Fallen8:Chat:<Backend>:Model` line) and becomes one picker per purpose fed by the same `GET /chat/models` catalog, with the card showing model and residency per purpose. The Configuration screenshots are recaptured. |
| The docs site (`docs/`) | **One new page** `docs/src/content/docs/agents.md` in the *AI agents* sidebar group (what an agent run is, spawning and reviewing, the feed, budgets and caps, roles and allowlists, swarm mode, the security posture and the prompt-injection honesty note, configuration). `nl-assist.md` and `rest-api.mdx` gain the `purpose` and tools fields where they describe the chat body; `model-providers.md` states that each backend now carries one server-owned model per purpose and what a purpose is; `nahil.md`'s settings block spells both purpose keys and gains a sentence on quotas as agents see them; `running.mdx` where it spells the key; `mcp-server.md` gains a pointer. The README "Key features" list gains a one-line entry linking `https://docs.fallen-8.com/agents/`. The link-checked build must stay green. |
| The architecture diagrams | **Both change, in the same PR:** a new deployable and a new channel. In the root `README.md` diagram a node on the internal side (no host port) reaching the MCP server and the instance's chat gateway, and **not** the model provider, because it never does; in `docs/src/content/docs/architecture.md` the same node plus its OTLP push and the apiApp proxy edge. Colours stay the fixed dark surfaces with the `#E2001A` accent. |
| The compose environment | **One service, one profile, the model variables on the instance.** `f8-agents` on the `agents` profile (opt-in via `F8_AGENTS=true`), unpublished with `expose:` documenting the port, `read_only` with a `/tmp` tmpfs, no volume; `Fallen8Target__BaseUrl=http://fallen8:8080` and `Fallen8Target__ApiKey=${F8_API_KEY:-}` exactly as the other two sidecars; `Agents__Mcp__Endpoint=http://f8-mcp:8090` and the MCP token variable reused. The `fallen8` service gains `Fallen8__Agents__Enabled` and `Fallen8__Agents__Endpoint`. The base file and the `nahil`, `openai` and `anthropic` overlays map their chat-model variables to `Fallen8__Chat__<Backend>__Models__Assist` and set `Models__Agent` on the `fallen8` service. `scripts/ollama-init.sh` pulls the agent model when `F8_AGENTS=true`; `scripts/env-up.js` pushes the profile; `env:down`/`env:logs`/`env:status` pass it; `.env.example` documents `F8_AGENTS` and `F8_NAHIL_AGENT_MODEL`; `.github/workflows/release.yml` gains the image to the multi-arch matrix. |
| NL assist (`nl-assist-finetune`) | **No impact, no retrain entry.** Its calls carry no `purpose` and keep the assist model, now under `Models:Assist`; agents never use the fine-tune; no delegate surface changes. |
| Skill library (`features/open/skill-library`) | a note that its catalog wants an "operate the agent host" skill once this lands; recorded there, not here. |
| Sample graphs, stored queries, provider descriptors, browser probe | **No change.** Nothing here touches the engine, persistence or an index, so the browser probe is not implicated. |
