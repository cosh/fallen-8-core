# Agent host (`fallen-8-agents`)

The LIVING doc for this feature. [spec.md](./spec.md) and [plan.md](./plan.md) are the historical
record and are not rewritten; [findings.md](./findings.md) is the measurement and defect log. The
user-facing page is <https://docs.fallen-8.com/agents/>, and that is where an operator should be
sent rather than here.

**Status:** phases 0 through 4 complete, phase 5 in progress, not merged.

## What it is, in one paragraph

A separate deployable that RUNS agents against one Fallen-8. It is the other side of
`fallen-8-mcp`: that server exposes this graph's tools to somebody else's agent, this host runs
agents here and reaches the graph only as a client of that server. It holds **no model
configuration and no provider credential**: every model call goes to the instance's own
`POST /chat` with `purpose: agent`.

## Quickstart

```bash
# The sidecar plus the instance's own /agents routes.
F8_AGENTS=true npm run env:up

# Spawn. 202 with the id; nothing on this control plane waits for a model.
curl -X POST http://localhost:8080/agents \
  -H 'Content-Type: application/json' \
  -d '{"task":"How many vertices are in the default namespace?"}'

# Subscribe (SSE, the change feed's dialect; filters are ?agents= and ?kinds=).
curl -N http://localhost:8080/agents/feed

# Read it back.
curl http://localhost:8080/agents/<id>        # summary, children, citations, trace tail
curl http://localhost:8080/agents/<id>/trace  # the whole retained trace
curl -X DELETE http://localhost:8080/agents/<id>   # cancel; cascades to live workers
curl http://localhost:8080/agents/status      # why did my agent fail
```

Without the compose environment: run the host with `dotnet run --project fallen-8-agents`, point
`Fallen8Target:BaseUrl` at an instance and `Agents:Mcp:Endpoint` at an MCP server, and set
`Fallen8:Agents:Enabled=true` plus `Fallen8:Agents:Endpoint` on the instance so the proxy has
something to reach.

## Roles and allowlists

| Role | Shipped allowlist | Spawnable by a caller |
| --- | --- | --- |
| `assistant` | empty, which means every tool the MCP server advertises | yes (the default) |
| `orchestrator` | `f8_overview`, plus the two swarm tools appended after the filter | yes |
| `worker` | empty, so every advertised tool | **no**: only an orchestrator's `spawn_worker` |

An allowlist NARROWS and can never widen: it is applied to the tool list handed to the model, so a
tool outside it is never seen rather than merely discouraged, and the MCP server's own tiers remain
the outer bound. A configured `Agents:Roles:<role>:Tools` REPLACES the shipped list rather than
adding to it, because the only reason to configure one is to say "exactly these"; an entry with no
list is "this role is mentioned", not "this role may use nothing".

The swarm tools are not in any allowlist. They are not MCP tools, `SwarmTools` implements them, and
the runner appends them for the orchestrator role alone.

## Where each thing lives

| Concern | Type |
| --- | --- |
| The model call | `Model/Fallen8ChatClient.cs` (an `IChatClient` over `POST /chat`) |
| Per-run metering and the four budgets | `Runtime/AgentBudgetChatClient.cs` |
| Lifecycle, caps, retention, cascade cancel | `Runtime/AgentRegistry.cs` |
| The run itself, and the framework pipeline | `Runtime/AgentRunner.cs` |
| One call site per fact: trace, feed and meter | `Runtime/AgentJournal.cs` |
| The bounded trace and the byte caps | `Runtime/AgentTrace.cs` |
| The event feed and its filter grammar | `Runtime/AgentFeed.cs` |
| The swarm's two tools | `Runtime/SwarmTools.cs` |
| A tool call that was refused rather than performed | `Runtime/ToolRefusal.cs` |
| The mechanical citation count | `Runtime/GroundingCheck.cs` |
| Roles, prompts, allowlists | `Runtime/RoleCatalog.cs`, `Prompts/*.md` |
| The meter | `Diagnostics/AgentsMetrics.cs` |
| Routes, DI, startup posture | `Hosting/*` |
| The apiApp's proxy | `fallen-8-core-apiApp/Agents/`, `Controllers/AgentsController.cs` |

## Configuration

The full reference is on the [docs page](https://docs.fallen-8.com/agents/) and in
[spec.md](./spec.md) 3.8, which also records the Phase 0 arithmetic behind the defaults. Two things
worth knowing here:

- **Every cap treats a non-positive value as OFF**, and the startup line prints `unlimited` rather
  than a bound of zero. All eight of them.
- **`Agents:*` is covered by no reflection gate.** The apiApp's setting-catalog governance filters
  on sections prefixed `Fallen8:`, and this host's sections are `Agents` and `Fallen8Target`, as
  with the other two sidecars. So a typo in one of its own option names is caught by nothing.

## Phase 0, and the fallback ladder

Phase 0 measured the framework and the model before any of this was built, and both answers shaped
what shipped. [findings.md](./findings.md) section 1 is the record; the short version:

- The framework works as assumed: `ChatClientAgent` runs the tool loop, tools arrive on
  `ChatOptions.Tools`, and an agent's system prompt travels on `ChatOptions.Instructions` rather
  than in the message list. That last one is not a detail: the adapter dropped the role prompt
  entirely until it was found live, and the unit test that "covered" it asserted on the property
  the framework had set, so it passed throughout.
- **The shipped agent model cannot do tool calling with any instructions present.** With no
  instructions it calls the tool; with a role prompt it emits the literal text of a tool-call
  marker followed by an invented result. Verified identically against the platform directly, so
  our gateway is exonerated. The decision (2026-09-10) was to keep the prompts, record the gap and
  treat a tool-capable model as a platform ask.

The ladder when an agent answers badly, in the order worth checking: `GET /agents/status` for the
gateway and the toolset; the trace's `citationCheck` step for a fabricating shape (figures
asserted, no citations, no tool calls); the `modelCall` steps for which backend and model actually
served each step; then the model itself.

## Security posture

- **No host port on the container**, and the proxy at `/agents/*` is the only way in. An agent
  decides for itself which tools to call, so a published port would hand "spawn an agent" to
  anything on the host.
- **One REST family.** The host calls the `/chat` family and nothing else;
  `CodeQualityTest.TheAgentHost_CallsTheChatGatewayAndNoOtherRestRoute` enforces it.
- **The MCP tiers are the real boundary.** Leaving write and admin off is the recommended posture.
- **Prompt injection is not solved here, and the honest version matters.** A graph's own content
  becomes part of what a model reads, so a hostile property value can try to steer an agent. What
  actually holds is enforced outside the model: the allowlist, the MCP tiers, and the four budgets.
  The prompt is not a control.
- **No user text in a metric tag**: no task, no agent name, no agent id.

## Testing

No live model anywhere in CI. `AgentRuntimeTest` drives the real framework pipeline against a
scripted `IChatClient`, with real local functions as tools, so the loop that invokes them is the
real one. `AgentChatAdapterTest` drives the adapter through a REAL hosted apiApp with a fake
backend behind it, because a stubbed handler proves nothing about the wire: the one bug Phase 1a's
live verification found was an assistant turn with null content, and every unit test passed.

Two harness facts worth knowing before writing a swarm test:

1. One script shared by an orchestrator and its workers is indexed by a single counter, so which
   turn each agent gets is a race. Pass `workerScript:` and the client scripts workers separately.
2. An instant worker cannot prove that `await_workers` waits, because a broken await still finds it
   finished. Use `Script.SaysAfter(...)`.

`AgentLiveSmokeTest` is `[Ignore]`d and talks to a real instance and a real model; it is the only
thing that has ever caught an adapter-level wire bug.
