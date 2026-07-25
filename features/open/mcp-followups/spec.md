# MCP follow-ups — batch writes + dynamic-code discovery

> **Status:** Draft/spec. Feature branch `feature/mcp-followups` (branch-only workflow).
> These are the two apiApp-touching deferrals recorded in
> [mcp-server §7](../../done/mcp-server/spec.md): a batch-transaction write path and surfacing
> the dynamic-code switch on `/status`. Both close an **engine → REST → MCP** gap (a capability
> the engine already has that agents cannot reach efficiently or discover).

## 1. Batch writes (`PUT /vertices`, `PUT /edges`)

**Why.** The engine already has atomic multi-element transactions
(`CreateVerticesTransaction`/`CreateEdgesTransaction`), but the REST surface only exposes
single-element `PUT /vertex`/`PUT /edge`, and those return `202` with **no id** — so an agent
building a graph pays one round-trip per element and cannot learn the assigned ids. That is the
biggest write-path footgun the mcp-server review named.

**Contract.**
- `PUT /vertices` — body `List<VertexSpecification>`; query `waitForCompletion` (default false).
  Runs ONE `CreateVerticesTransaction` (atomic). Waited ⇒ `200` with the assigned vertex ids in
  input order (`IEnumerable<int>`); unwaited ⇒ `202`. Namespace-scoped (twinned `/ns/{ns}/…`).
- `PUT /edges` — body `List<EdgeSpecification>`; same shape; runs ONE `CreateEdgesTransaction`.
  A referenced vertex missing ⇒ the whole batch rolls back atomically ⇒ `404` (waited).
- Errors mirror the single endpoints via the shared rollback mapping (400 invalid, 404 missing
  endpoint, 409 quota/conflict, 500 internal). Bodies stay plain-string (api-error-contract).
- **Not changed:** the single `PUT /vertex`/`PUT /edge` keep their contract. Ids are returned
  only on the waited batch path (the transaction must complete to know them).
- **Deferred:** edges referencing vertices created in the *same* request (symbolic refs) — needs
  engine ref-resolution; agents create vertices, read the ids, then create edges. Noted, not built.

**MCP.** `f8_mutate` gains two ops: `create_vertices` (param `vertices: [{label, properties}]`)
and `create_edges` (param `edges: [{source, target, edgePropertyId, label, properties}]`), both
`waitForCompletion=true`, returning the ids. Property values stay JSON-native. This is the
honest fix for "create returns no id": a batch of one returns its id too.

## 2. Dynamic-code discovery (`EnableDynamicCodeExecution` on `/status`)

**Why.** `Fallen8:Security:EnableDynamicCodeExecution` is a genuine, off-by-default **security
gate** (it turns the Roslyn compile endpoints into in-process code execution — kept configurable
on purpose; see mcp-server §3.6). Because it varies, an agent needs to know whether the `code`
capability will work *before* submitting a fragment (today it only learns via a `403`).

**Contract.** `StatusREST` gains `dynamicCodeExecutionEnabled` (bool), populated by
`AdminController.Status()` from `Fallen8SecurityOptions.EnableDynamicCodeExecution`. Read-only,
no new config, the flag stays configurable and off by default.

**MCP.** `f8_overview` reports `dynamicCodeEnabled` in the per-namespace status block, so an
agent (and the MCP `code` capability) can see it up front. The MCP server's `code` double
opt-in keeps the `403` fallback (belt-and-suspenders); the difference is it is now *discoverable*.

## 3. MCP in the default environment (user-requested)

The MCP server joins the default `npm run env:up` (no longer an opt-in `mcp` profile), on
`http://localhost:8090`, **anonymous and read-only** — matching this environment's explicit
no-auth-in-the-way posture (the `fallen8` service runs with no API key here too). The container
binds `0.0.0.0` (it must, to be reachable), so the compose sets `AcceptAnonymousRemote=true` as
the explicit, logged opt-in the server's fail-closed startup requires.

**Securing a serious setup stays fully possible** (the user's condition): every knob is env-var
config on the `f8-mcp` service — `F8_MCP_AUTH_MODE=StaticToken|OAuth`, `F8_MCP_TOKEN`, the tier
flags — and the standalone image run in `docs/mcp-server.md` is credentialed with no
`AcceptAnonymousRemote`. Demo = open; real = locked down, no code change.

Also: MCP now has a **Key features** entry in the root README and a `docs/` index row, and
`CLAUDE.md` records the convention that every user-facing key feature earns such an entry.

## Impact on existing features (cross-feature sweep)

- **OpenAPI snapshot** — both changes alter the served document; regenerate
  `features/done/web-ui/openapi-v0.1.json` (`pwsh scripts/update-openapi-snapshot.ps1`) and
  review the additive diff. `OpenApiDocumentTest` pins it.
- **MCP server (done)** — `McpBridgedEndpoints` gains `PUT /vertices`, `PUT /edges`; the
  coverage/contract tests follow; `StatusDto`/`f8_overview` gain the flag. The spec §3.6 note is
  updated (dynamic-code state is now surfaced, not only inferred from a 403).
- **change-feed** — batch creates already emit per-element change events (the transactions call
  `DescribeChanges`); no change.
- **Studio UI / NL-assist** — no impact (additive REST endpoints; no engine/contract removal).
- **Security posture** — unchanged: `EnableDynamicCodeExecution` stays a configurable,
  off-by-default gate; `/status` only *reports* its value (it already reports `apiKeyRequired`).

## Acceptance

- `PUT /vertices`/`PUT /edges` create atomically and return ids in input order (waited); a bad
  edge endpoint rolls the whole batch back to `404`; namespace twins work.
- `/status` reports `dynamicCodeExecutionEnabled`; `f8_overview` surfaces it.
- `f8_mutate create_vertices`/`create_edges` round-trip and return usable ids; the MCP coverage +
  contract tests include the new endpoints; snapshot regenerated.
- Build clean (0 warnings), full suite green, `docs/` updated (mcp-server.md + rest-api.md note).
