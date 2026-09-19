---
title: "Agents"
description: "A sidecar that RUNS agents against your Fallen-8: it holds no model configuration and no provider key, reads the graph only through the MCP server, and leaves a bounded trace and an event feed for every run."
---

An **agent** is a model given a task, a set of tools and a budget, left to decide for itself which
tools to call until it can answer. The agent host runs them against your Fallen-8.

It is the other side of the [MCP server](/mcp-server/). That one exposes this graph's tools to
somebody else's agent, in somebody else's client. This one runs agents *here*, and reaches the
graph as a client of that same MCP server, so an agent can call exactly the tools you enabled
there and nothing else.

It is a **separate deployable** (`fallen-8-agents`), its own process and container image, and it
holds two things deliberately: **no model configuration and no provider credential**. Every model
call goes to your instance's own `POST /chat` with `purpose: agent`, so you configure a model once,
on the instance, and the host inherits it. Point that instance at Nahil, at a local Ollama, at
OpenAI or at Anthropic and the agents follow, with no second place to change.

:::caution[Off by default, and it is a decision rather than caution]
Every other capability here defaults on. This one does not. An agent decides for itself which
tools to call, which means what it can do is whatever the MCP server advertises: enable the write
tier there and an agent can write. That is a decision to make deliberately, so it is opt-in at
both ends (`F8_AGENTS=true`, `Fallen8:Agents:Enabled`).
:::

## What you get for a run

Every agent leaves three things behind, and they are the reason this exists rather than a chat loop
in a script:

- **A bounded trace.** Every model call (with the backend and model that served it), every tool
  call (with the arguments and result, byte-capped), the state changes, and the mechanical citation
  count. Past `Agents:Trace:MaxSteps` the oldest steps go and a marker row says how many, so a
  short run is never mistaken for a truncated one.
- **An event feed.** Server-sent events in the same dialect as the [change feed](/change-feed/), so
  a client that can read one can read the other: `id:`/`event:`/`data:`, keep-alive comments, and
  declarative `agents` and `kinds` filters. Every event carries the live counters, so a subscriber
  renders cost without polling.
- **Hard budgets.** Model calls, tool calls, wall clock and tokens, each enforced in this process,
  because a model that is looping is exactly the model that will not honour an instruction to stop.
  A breach ends the run as `budgetExceeded` and names *which* budget.

Nothing here is durable. A restart ends every agent and forgets every finished one, which is why
every listing and every trace names the host instance that produced it: two traces from different
processes are never silently compared.

## Running one

```bash
# Turn it on. This brings up the sidecar AND opens the instance's /agents routes.
F8_AGENTS=true npm run env:up

# Start an agent. 202 with its id: nothing on this control plane waits for a model,
# because one call against a remote provider was measured at up to 41 seconds.
curl -X POST http://localhost:8080/agents \
  -H 'Content-Type: application/json' \
  -d '{"task":"How many vertices are in the default namespace?"}'

# Watch it work.
curl -N http://localhost:8080/agents/feed

# Read what it did.
curl http://localhost:8080/agents/<id>
curl http://localhost:8080/agents/<id>/trace
```

On a secured instance every one of those carries your API key, exactly as the rest of the API
does. With agents off they answer **403** on a keyed instance and **401** on a keyless one: a
client that treats only 403 as "this feature is absent" will misread a bare `dotnet run`.

## Roles

A spawn names a **role**, which is a prompt plus a tool allowlist. The allowlist narrows what the
MCP server advertises and can never widen it, and it is applied to the tool list the model is
handed, so a tool outside it is not merely discouraged: the model never sees it.

| Role | Sees | For |
| --- | --- | --- |
| `assistant` | every tool the MCP server advertises | the default: one agent, one task, one answer |
| `orchestrator` | the overview read, plus the two swarm tools | breaking a task into parts and composing the results |
| `worker` | every tool the MCP server advertises | one separable part of a larger task, spawned by an orchestrator |

A `worker` cannot be spawned over the API. It reports a typed result to the orchestrator that gave
it its part of a task, so one started by hand would have nobody to report to.

`Agents:Roles:<role>:Tools` **replaces** a role's shipped allowlist rather than adding to it:
absent or empty keeps what the role ships with (so an empty list leaves `orchestrator` on the
overview read), a list of tool names means exactly those, and a lone `*` means every tool the
server advertises, which is the only value that widens. A list holding nothing but blanks counts as
empty, and `*` beside a tool name is refused at startup rather than guessed at. The MCP tiers bound
all of them.

## Swarm mode

An orchestrator gets two extra tools: `spawn_worker(task, name?)` and `await_workers(ids?)`. A
worker is a **real agent** in every sense that matters: listed, traced, budgeted and cancellable
like any other, with its own token budget rather than a slice of its orchestrator's.

`await_workers` returns each worker's **typed** result (state, answer, citation counts, cost),
never prose about what the worker did. That is deliberate: an orchestrator handed prose would have
to parse another model's writing, and because it is the only composer, whatever it cannot read
reliably it would paraphrase.

Three caps bound a swarm, all configuration and all enforced when an agent is admitted:

| Cap | Default | Bounds |
| --- | --- | --- |
| `Agents:Limits:MaxConcurrentAgents` | 4 | how many agents run at once on this host |
| `Agents:Limits:MaxSwarmDepth` | 2 | how deep it nests; 2 means "delegate once", so workers cannot orchestrate in turn |
| `Agents:Limits:MaxWorkersPerOrchestrator` | 4 | how many workers one orchestrator spawns **over its whole life** |

The last one counts over a life rather than at once on purpose. An orchestrator's token budget
bounds its own calls, not its workers', so a live-only count would let it spawn its allowance,
await them, and spawn again without limit.

A breached cap comes back to the model as a tool error it can act on (delegate less, compose what
it has) and to you as a `toolCalled` event with `success: false`.

**A worker does not outlive its orchestrator.** However an orchestrator ends, whether it answers,
fails, is cancelled or runs out of budget, its live workers are cancelled and each row says which
ending stopped it. Only the orchestrator reads a worker's result, so a worker whose orchestrator has
gone would spend its budget and hold a concurrency slot for an answer nobody will compose. The same
rule refuses a late delegation: an orchestrator that has already ended cannot take a new worker, and
`spawn_worker` says so.

## Exactly one composer

Only the orchestrator's final text is the answer. Workers write for their orchestrator, and the
swarm's mechanics stay where mechanics belong: agent names, spawns and awaits are feed events and
trace steps, never narration in the result. The role prompts say so, and they are embedded in the
image rather than mounted, because a deployment that could edit or lose a prompt could quietly
turn an agent into a confident liar.

## Citations, and what they do and do not mean

The role prompts require every figure, name or id in an answer to carry the tool call it came from,
written `[t:<name>]`. The host then counts those citations against the calls the run actually made
and records the pair on the trace and the ending event.

It is a **count, never a judgement**, and the numbers are worth reading precisely:

- A high `valid` count is not a correct answer. An agent can cite a real call and still misread it.
- A `dangling` count is not proof of a lie. The model may have named a tool it holds but did not
  call, or one that does not exist.
- What the pair is good for is the shape a fabricating run has: every figure asserted, no citations
  at all, and a trace with no tool calls in it.

:::note[Measured, and worth knowing before you choose a model]
The stock model the sidecar pulls by default emits no parsed tool call once **any** instruction
text is present: with no instructions it calls the tool, and with a role prompt it writes the
literal text of a tool-call marker followed by an invented result. Verified against the platform
directly, so it is the model rather than this gateway. Agents that actually call tools need a
tool-capable model named in `Fallen8:Chat:<Backend>:Models:Agent` (and in `F8_AGENT_MODEL` if you
run it locally). The citation count is what makes a fabricating run visible when they do not.
:::

## Security posture

- **No host port.** The container publishes none. The browser and your scripts reach it through the
  API's authenticated proxy at `/agents/*`, which is already the front door.
- **One REST family.** The host calls this instance's `/chat` and nothing else; a convention test
  enforces it, so it cannot quietly grow a graph route of its own.
- **The MCP tiers are the boundary.** An agent can do what that server advertises. Leaving write
  and admin off is the recommended posture: an agent that can only read cannot be talked into a
  write.
- **Prompt injection is real and is not solved here.** A graph's own content becomes part of what a
  model reads, so a hostile value in a property can try to steer an agent. The defences that
  actually hold are the allowlist, the MCP tiers and the budgets, all enforced outside the model;
  the prompt is not one of them.
- **No caller text in telemetry.** The host's meter tags by closed sets only: role, token
  direction, outcome, backend, tool name, success. No task and no name you sent reaches a tag or a
  span, because the framework is given the agent's ROLE as its telemetry name. What does travel is
  the agent id: Microsoft Agent Framework puts it in its `invoke_agent` span's name and in
  `gen_ai.agent.id`, which is what ties a trace to a run. A collector that derives metrics from
  span names would therefore see one series per run; the shipped one bounds that name before it
  does.

## Configuration

Host (`Agents:*` for the sidecar, `Fallen8Target:*` for the instance it asks):

| Key | Default | Note |
| --- | --- | --- |
| `Agents:BindAddress` / `Port` | `127.0.0.1` / `8120` | the image sets `0.0.0.0`; never published |
| `Fallen8Target:BaseUrl` / `ApiKey` / `ApiKeyHeader` | `http://localhost:8080` / none / `X-Api-Key` | the instance every model call goes to |
| `Fallen8Target:TimeoutSeconds` | `630` | above the largest chat budget a shipped profile sets, so the instance's own error wins rather than being cut off here |
| `Agents:Mcp:Endpoint` / `BearerToken` | `http://localhost:8090` / none | how an agent reaches the graph |
| `Agents:Mcp:ConnectTimeoutSeconds` | `15` | the startup handshake; an unreachable server leaves the toolset empty with a reason rather than failing the host |
| `Agents:Roles:<role>:Tools` | see above | MCP tool names, which replace the role's shipped list; see [Roles](#roles) |
| `Agents:Limits:DefaultTokenBudget` | `100000` | what a spawn naming no budget gets |
| `Agents:Limits:MaxTokenBudget` | `400000` | the ceiling on a caller's own `tokenBudget`, which it clamps rather than refuses |
| `Agents:Limits:MaxStepsPerRun` | `24` | model calls |
| `Agents:Limits:MaxToolCallsPerRun` | `48` | |
| `Agents:Limits:MaxRunSeconds` | `1800` | wall clock, measured by the host: a backend's reported duration does not cover a remote provider's routing |
| `Agents:Limits:MaxConcurrentAgents` | `4` | low because the provider quota and the instance's rate limit are shared by all of them |
| `Agents:Limits:MaxSwarmDepth` / `MaxWorkersPerOrchestrator` | `2` / `4` | see Swarm mode |
| `Agents:Limits:RetainFinishedMinutes` / `MaxRetainedAgents` | `60` / `200` | how long a finished agent stays readable |
| `Agents:Trace:MaxSteps` / `ArgsBytes` / `ResultBytes` | `1000` / `2048` / `8192` | rows a trace holds (the marker is one of them) and the byte caps on a capture |
| `Agents:Feed:KeepAliveSeconds` / `MaxSubscribers` / `MaxQueuedEvents` | `15` / `16` / `512` | past the queue bound a subscriber is dropped rather than thinned, and its stream ends |
| `Agents:Observability:Otlp:Endpoint`, `Agents:Identity:*` | unset | OTLP push and fleet identity; see [Observability](/observability/) |

A non-positive value switches a cap OFF for every `Agents:Limits:*` key, every `Agents:Trace:*` key
and `Agents:Feed:MaxSubscribers`, and for the `Agents:Limits:*` ones the startup line prints
`unlimited` rather than a bound of zero.

Four keys are floored at **1** instead, and none of them has an "off":
`Fallen8Target:TimeoutSeconds`, `Agents:Mcp:ConnectTimeoutSeconds`, `Agents:Feed:KeepAliveSeconds`
and `Agents:Feed:MaxQueuedEvents`. A model call with no deadline, a startup handshake that never
gives up and an unbounded subscriber queue are each worse than the bound they would replace, so a
`0` in them is a one second deadline, a one second handshake, a keep-alive every second and a queue
of one. Set the number you mean: to wait longer for a completion, raise
`Fallen8Target:TimeoutSeconds` above the instance's own `Fallen8:Chat:TimeoutSeconds`.
`GET /agents/status` reports the deadline in force, so a `0` reads back as `1` there.

Instance:

| Key | Default | Note |
| --- | --- | --- |
| `Fallen8:Agents:Enabled` | `false` | the `/agents/*` routes refuse until this is on |
| `Fallen8:Agents:Endpoint` | empty | the proxy answers 503 rather than timing out |
| `Fallen8:Agents:TimeoutSeconds` | `30` | the small routes; the feed is a stream and takes none |
| `Fallen8:Chat:<Backend>:Models:Agent` | `phi4-mini:latest` on Ollama and Nahil, none on OpenAI and Anthropic | the model the agent purpose resolves to; see [one model per purpose](/semantic-traversal/#one-model-per-purpose) |

## What it reports about itself

`GET /agents/status` is the first thing to read when an agent fails: whether the host could reach
the chat gateway at startup **and when it looked**, what backend and model last served a step,
whether the MCP server answered and how many tools it advertises, what each role actually ended up
with, the caps a run is held to, and how many agents are active and retained.

The reachability word is one startup probe and nothing refreshes it, which is why it carries a
timestamp. A host that came up before its instance answered will say `unreachable` for the life of
the container while every agent runs fine, so read it against that timestamp and against the
last-seen model: a completion can only have been served by a gateway that answered.

## See also

- [MCP server](/mcp-server/): the tool surface an agent calls, and the tiers that bound it
- [Model providers](/model-providers/): which backend serves the agent purpose, and how to choose
- [Nahil](/nahil/): the default remote backend, and its shared quota
- [Observability](/observability/): where the agent meter and the GenAI spans go
- [Configuration](/configuration/): the instance keys and how they are written
