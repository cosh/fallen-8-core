# MCP follow-ups — batch writes + dynamic code always-on

> **Status:** Draft/spec. Feature branch `feature/mcp-followups` (branch-only workflow).
> Started from the two apiApp-touching deferrals recorded in
> [mcp-server §7](../../done/mcp-server/spec.md): a batch-transaction write path, and surfacing
> the dynamic-code switch on `/status`. The batch write landed as specified. The second item
> was **superseded** by an owner decision to remove the dynamic-code switch entirely — dynamic
> code execution is now unconditional — so there is no state to surface (see §2).

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

## 2. Dynamic code execution is now always on (flag removed)

**Decision.** The original follow-up was to *surface* `EnableDynamicCodeExecution` on `/status`
so an agent could discover it before submitting a fragment. The owner decided instead to
**remove the switch entirely**: compiling and running agent-emitted C# is Fallen-8's core
"queries are C#" model, and gating it off served no real deployment (an operator who wants the
compile endpoints unreachable simply doesn't expose the service / doesn't hand out the key).
Dynamic code execution is therefore **unconditional**. There is nothing to surface — a boolean
that is always `true` is noise — so the `/status` field and the `f8_overview` flag were dropped.

**What changed (engine/apiApp).**
- `Fallen8SecurityOptions.EnableDynamicCodeExecution` — **removed**. `appsettings.json`, the
  Dockerfile posture comment, the CI smoke-test env, and `docker-compose.yml` drop it.
- The `DynamicCodeExecution` capability, `DynamicCodePolicy`, and the request-shape-aware
  `DynamicCodeCapabilityGate` are **removed**. The compile endpoints (`POST /path`,
  `PUT /subgraph`, `POST /storedquery`, `POST /delegates/validate`) now carry only the standard
  fallback authentication, so they can no longer return `403` for a "code disabled" reason
  (the `403 ProducesResponseType` + XML `<response 403>` are removed on all four).
- Plugin DLL loading keeps its own kill switch (`EnableDynamicPluginLoading`) and its `403`.

**MCP.** The MCP-side `Mcp:Tools:EnableCode` capability stays (off by default) — it is now purely
an MCP-surface exposure choice (whether `f8_paths`/`f8_subgraph` advertise inline-fragment
params), not a mirror of an engine flag. No `/status` or `f8_overview` field is added.

**Auth is unchanged and still layers the same way:** with an API key configured, an anonymous
call to a compile endpoint is `401`; the removal only eliminated the `403`-when-disabled path.

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
  review the diff: additions for `PUT /vertices`/`PUT /edges`, **removals** of the `403`
  response on `/path`, `/subgraph`, `/storedquery`, `/delegates/validate` (deliberate — the
  endpoints no longer 403 for a code reason). `OpenApiDocumentTest` pins it.
- **MCP server (done)** — `McpBridgedEndpoints` gains `PUT /vertices`, `PUT /edges`; the
  coverage/contract tests follow. No `/status`/`f8_overview` flag (dropped per §2).
- **api-security-boundary (done)** — the owning feature: its living README is updated (the
  `EnableDynamicCodeExecution` row/section is gone; dynamic code is documented as always on,
  plugin loading stays gated). The historical spec/plan are left as the record of the earlier
  gated design.
- **stored-query-library (done)** — **security rationale changed.** Its original pitch was
  "register while the switch is on, then lock the engine down to stored-queries-only with the
  switch off." That lockdown mode no longer exists (inline code is always accepted). The
  library is re-documented as a **reuse/curation convenience** (compile once, invoke by name,
  curated catalog); the security matrix now keys on auth only. Living README + `docs/stored-queries.md`
  updated; `StoredQuerySecurityMatrixTest` rewritten for the auth-only matrix.
- **element-embeddings / subgraph-semantic-thresholds / change-feed / graph-analytics** — their
  living READMEs and `docs/` pages referenced the switch only to say "declarative, works with it
  off"; reworded to "compiles no C#". No behaviour change.
- **Studio UI** — the dead `403`-for-disabled-code branches and copy (DelegateEditor,
  StoredQueryControls, Path/Subgraph screens, field help, semantic editors, playwright env) are
  removed/reworded; `tsc` clean.
- **NL-assist** — the dataset/eval harness prose and bootstrap env dropped the flag; no retrain
  needed (the fragment-generation contract is unchanged — `/delegates/validate` still compiles
  identically), so no `RETRAIN-LOG.md` entry.
- **Security posture** — the API key is now the sole access control; an unauthenticated instance
  grants in-process code execution to anyone who can reach it, so the docs stress setting a key
  before exposing off-box.

## Acceptance

- `PUT /vertices`/`PUT /edges` create atomically and return ids in input order (waited); a bad
  edge endpoint rolls the whole batch back to `404`; namespace twins work.
- `f8_mutate create_vertices`/`create_edges` round-trip and return usable ids; the MCP coverage +
  contract tests include the new endpoints; snapshot regenerated.
- `EnableDynamicCodeExecution` is gone from the codebase; the compile endpoints run
  unconditionally (no `403` for a code reason), still `401` when a key is configured and the
  caller is anonymous. `ApiSecurityBoundaryTest` / `StoredQuerySecurityMatrixTest` /
  `DelegateValidationEndpointTest` pin the always-on behaviour.
- Build clean (0 warnings), full suite green, `docs/` + living feature READMEs + Studio UI +
  NL-assist harness updated.
