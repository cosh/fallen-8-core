# Agent host (`fallen-8-agents`)

The LIVING doc for this feature. [spec.md](./spec.md) and [plan.md](./plan.md) are the historical
record and are not rewritten; [findings.md](./findings.md) is the measurement and defect log. The
user-facing page is <https://docs.fallen-8.com/agents/>, and that is where an operator should be
sent rather than here.

**Status:** implemented. Every phase, 0 through 5, is done on `feature/agent-host`, which is
awaiting merge; the merge gate's findings are in [findings.md](./findings.md) section 14.

## What it is, in one paragraph

A separate deployable that RUNS agents against one Fallen-8. It is the other side of
`fallen-8-mcp`: that server exposes this graph's tools to somebody else's agent, this host runs
agents here and reaches the graph only as a client of that server. It holds **no model
configuration and no provider credential**: every model call goes to the instance's own
`POST /chat` with `purpose: agent`.

## Quickstart

The enable-and-spawn walkthrough is on the
[docs page](https://docs.fallen-8.com/agents/#running-one), which is where an operator should be
sent. What is not there, because it is a contributor's path only: run the host outside compose with
`dotnet run --project fallen-8-agents`, which binds loopback, and point it at an instance with
`Fallen8Target__BaseUrl` and at an MCP server with `Agents__Mcp__Endpoint`. The instance still needs
`Fallen8__Agents__Enabled=true` before its own `/agents/*` routes answer.

## Roles and allowlists

The three roles, what each one sees and which of them a caller may spawn are on the
[docs page](https://docs.fallen-8.com/agents/#roles). `Runtime/RoleCatalog.cs` is the one home for
the contract behind them: what a role prompt may and may not be credited with, that an allowlist
narrows and can never widen, what a configured `Agents:Roles:<role>:Tools` does to the shipped list
and which single entry widens it, and why the swarm tools are in no allowlist at all.

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

- **A non-positive value switches a cap OFF for every `Agents:Limits:*` and `Agents:Trace:*` key
  and for `Agents:Feed:MaxSubscribers`**, and the startup line prints `unlimited` for the limits
  rather than a bound of zero. Four keys are floored at 1 instead and have no "off"
  (`Fallen8Target:TimeoutSeconds`, `Agents:Mcp:ConnectTimeoutSeconds`, `Agents:Feed:KeepAliveSeconds`,
  `Agents:Feed:MaxQueuedEvents`); the [docs page](https://docs.fallen-8.com/agents/) says why.
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

What holds, and what does not, is on the
[docs page](https://docs.fallen-8.com/agents/#security-posture): the one authenticated way in and
who else can reach the host, one REST family, the MCP tiers as the real boundary, the bounds on a
spawn's captures, prompt injection unsolved with the defences that work instead, and what does and
does not reach telemetry. The contributor's half is the gate:
`CodeQualityTest.TheAgentHost_CallsTheChatGatewayAndNoOtherRestRoute` fails the suite if this host
grows a REST call outside the `/chat` family.

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
