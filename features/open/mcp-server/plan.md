# Fallen-8 MCP Server — Plan

Companion to [spec.md](./spec.md). A separate deployable that bridges MCP to the existing
REST API, with a small, token-frugal, namespace-aware tool surface. Feature branch:
`feature/mcp-server` (branch-only workflow — no GitHub issue/PR).

Ordering principle: prove the bridge end-to-end with the smallest read-only surface first,
then widen tools by tier, then harden the remote transport, then land auth in the two
credentialed phases (static bearer before OAuth — the spec's "authentication eventually,
multiple phases"). Deployment packaging goes last so it ships what actually exists. Namespace
awareness and token economy are **not** phases — they are built into every tool from Phase 1.

## Phase 0 — Scaffold & round-trip harness

Intent: a walking skeleton — MCP client ↔ MCP server ↔ real apiApp — before any surface area.

- [ ] New `fallen-8-mcp` project (net10.0, root namespace `NoSQL.GraphDB.Mcp`) added to
  `fallen-8-core.sln`; MIT headers; **pinned** `ModelContextProtocol` +
  `ModelContextProtocol.AspNetCore` versions recorded in the feature README (verify the current
  stable `1.4.x` on NuGet at implementation time; do not adopt `2.0-preview` unless a needed
  feature requires it).
- [ ] `McpServerOptions` (`Mcp:*`) + `Fallen8TargetOptions` (`Fallen8Target:BaseUrl`,
  `Fallen8Target:ApiKey`, `Fallen8Target:ApiKeyHeader`, `Fallen8Target:TlsInsecure`) bound and
  validated at startup.
- [ ] `Fallen8RestClient` (typed `HttpClient`): `GET /status` only for now, configurable
  api-key header, defensive error surface (problem+json **or** string body → normalized error).
- [ ] Transport selection: `--stdio` flag / `Mcp:Transport` config; Streamable HTTP host on
  8090 otherwise.
- [ ] Test harness in `fallen-8-unittest`: MCP SDK **client** connected to the server, bridge
  pointed at a `WebApplicationFactory<Program>`-hosted apiApp (volatile, test key).
  First round-trip test: `tools/list` shows `f8_overview`; calling it returns real status +
  capability flags.
- [ ] Startup posture log line (transport, bind, auth mode `None`, tiers, F8 target +
  capabilities) + the warn-and-retry `/status` probe + `/healthz` reporting downstream
  reachability.

## Phase 1 — Read tier + contract pinning + token economy

Intent: the default (read-only) surface — consolidated, namespace-aware, compact, drift-guarded.

- [ ] Read tools: `f8_overview` (namespace directory + status + `detail:statistics`), `f8_get`
  (element + `include` neighbourhood), `f8_search` (`mode:property|fulltext|range|vector|
  semantic`, `limit`/`cursor`, `fields`), `f8_paths` (unfiltered + `storedQuery`), `f8_analytics`
  (read-only run + algorithm discovery via overview).
- [ ] **Namespace routing** in the bridge: optional `namespace` → `/ns/{ns}/…` vs bare route;
  covered by a non-`default`-namespace round-trip test.
- [ ] **Token economy**: compact element encoding (`id`/`label`/property keys by default),
  `include`/`fields` expansion, `limit`(25)/`cursor`(cap 200), null/default omission,
  `structuredContent`+`outputSchema` where supported; result-shape + schema-size tests.
- [ ] Bridge DTOs for those endpoints; **contract test** validating every bridged
  path/method/shape (incl. the `/ns/{ns}` twins and the exact `POST /path/{from}/to/{to}`
  template) against `features/done/web-ui/openapi-v0.1.json`.
- [ ] Error mapping: problem+json **and** plain-string body → normalized MCP tool error
  (`isError` + `{status,title,detail}`); test that the API key appears in no result/error/log.
- [ ] `readOnlyHint:true` annotations; terse agent-oriented descriptions; detail in parameter
  docs (token-frugal).
- [ ] Round-trip tests against a seeded graph for each read tool + each search mode available.

## Phase 2 — Write + admin tiers, tier/capability gating

Intent: opt-in mutation with honest completion semantics; gating at list AND call.

- [ ] Tier flags `Mcp:Tools:EnableWrite` / `EnableAdmin` and the `EnableCode` capability
  (all default false).
- [ ] Write tools: `f8_mutate` (single-transaction `op:create_vertex|create_edge|set_property|
  remove_property|remove_element`, always `waitForCompletion`, rolled-back ⇒ tool error),
  `f8_subgraph` (code-free define), `f8_namespace` (`op:create|rename|drop`, `drop` destructive),
  `f8_analytics` write-back (gated on write tier).
- [ ] Admin tool: `f8_admin` (`op:save|load|list_savegames|trim|tabula_rasa`) with honest
  namespace vs `[Fallen8Level]` scoping (§3.4) and `destructiveHint` on trim/tabula_rasa/load;
  bridge the **HEAD** methods for trim/tabularasa correctly.
- [ ] `code` capability: widen `f8_paths`/`f8_subgraph` with inline `filter`/`cost` fragment
  parameters, present in the schema only when `EnableCode` **and** the target F8 reports
  `EnableDynamicCodeExecution=true` (read from `/status`); the security honesty note in the
  parameter descriptions.
- [ ] Enforcement tests: `tools/list` per flag matrix (incl. `code`-off hides fragment params);
  `tools/call` on a disabled tier rejected; write round-trip (mutate → search finds it) in a
  non-`default` namespace; tabula-rasa annotated destructive.

## Phase 3 — Remote transport hardening + static bearer (auth phase B)

Intent: safe to put on a network you mostly trust.

- [ ] **Origin validation** on the HTTP transport (allow-list, loopback defaults) —
  DNS-rebinding protection; tests for allowed/blocked origins.
- [ ] **Loopback bind by default; open only on `Mcp:Security:AllowRemoteAccess=true`** — the
  MCP server's own enforcement (do **not** describe it as mirroring the apiApp's inert flag);
  `UNAUTHENTICATED`-style warning when remote + auth mode `None`. Test the bind and the warning.
- [ ] `Mcp:Auth:Mode = None | StaticToken`; static bearer via `Authorization: Bearer`,
  constant-time compare, 401 otherwise; never logged.
- [ ] Tests: missing/wrong/correct token; warning emitted per posture matrix.
- [ ] Document TLS options for the MCP endpoint (standard Kestrel cert config or a fronting
  proxy) — a deployment concern by project decision, no in-app machinery.

## Phase 4 — OAuth 2.1 resource server (auth phase C)

Intent: the standards-track "authentication eventually".

- [ ] `Mcp:Auth:Mode = OAuth`: JWT bearer validation against `Mcp:Auth:Issuer` (metadata
  discovery) + `Mcp:Auth:Audience`; audience binding mandatory (wrong-audience ⇒ 401).
- [ ] **RFC 9728 Protected Resource Metadata** at `/.well-known/oauth-protected-resource`; 401
  challenges carry `WWW-Authenticate` with the `resource_metadata` pointer.
- [ ] Scope→tier mapping (`f8:read`/`f8:write`/`f8:admin`/`f8:code`), always **intersected**
  with the server-side tier flags; a scope never enables a disabled tier.
- [ ] **No token passthrough:** F8 receives only the configured API key — pinned by test.
- [ ] Tests with test-minted JWTs (test signing key via config): valid accepted; wrong
  issuer/audience/expiry rejected; scope intersection matrix; PRM document contents.

## Phase 5 — Packaging: container + compose profile + docs

Intent: ship it the way it will actually run.

- [ ] `fallen-8-mcp/Dockerfile` (sdk → aspnet, mirroring the existing image conventions),
  `EXPOSE 8090`.
- [ ] `docker-compose.yml`: `f8-mcp` service under `profiles: [mcp]` (new pattern — default
  `up` unchanged); wired to `http://fallen8:8080`, shared `F8_API_KEY` → `Fallen8Target__ApiKey`,
  healthcheck `/healthz`. Add an **`env:up --profile mcp` / `env:mcp` path in
  `scripts/env-up.js`** (the repo's real compose driver) rather than assuming raw
  `docker compose up`; keep the gpu.yml layering intact.
- [ ] Standalone-run documentation (remote F8 over HTTPS; `Fallen8Target__TlsInsecure` lab flag
  loudly discouraged).
- [ ] `features/open/mcp-server/README.md`: client-connection examples (Claude Code
  `claude mcp add --transport http`, stdio config), the 9-tool table, tier/auth/token-economy
  option tables, the trust-chain diagram, the prompt-injection guidance (run read-only by
  default), and the pinned SDK/protocol versions.
- [ ] Root `README.md`: "Use Fallen-8 from AI agents" section (shared with the skill-library
  feature when it lands).
- [ ] User-facing `docs/mcp-server.md` deep-dive (per the repo's `docs/` convention) when the
  behaviour is user-visible.

## Phase 6 — Gate

- [ ] Full `dotnet test` green; build 0 warnings/0 errors; compose default + `--profile mcp`
  both verified manually (documented).
- [ ] Council/architect review per the repo merge gate; fix findings on the branch;
  `git merge --no-ff` to `main`; move `features/open/mcp-server/` → `features/done/`.

## Progress

- [ ] Phase 0 — scaffold + client↔server↔apiApp round-trip harness
- [ ] Phase 1 — read tier (consolidated, namespace-aware, token-frugal) + contract pinning + error mapping
- [ ] Phase 2 — write/admin tiers + code capability + enforcement matrix
- [ ] Phase 3 — origin validation, loopback-bind enforcement, static bearer (auth B)
- [ ] Phase 4 — OAuth 2.1 resource server + scope→tier intersection (auth C)
- [ ] Phase 5 — Dockerfile, compose profile + env-up integration, READMEs, docs
- [ ] Phase 6 — architect/council gate, merge + move to done/

## Decision / revisit conditions

- **Consolidated 9-tool surface** for token economy; split an individual tool only if its
  mode-union hurts model accuracy in real use. The one open consolidation call for the review:
  `f8_namespace` as its own tool vs folding create/rename/drop into `f8_admin` ops.
- **Bridge over embedding** is a requirement, not a preference; revisit only if the user changes
  the deployment constraint.
- **Single downstream identity** (one F8 API key) is a consequence of F8's all-or-nothing auth;
  per-caller F8 identities require an F8-side multi-credential feature first.
- **Single-instance remote deployment** in v1; the MCP 2026-07 stateless core is the scale-out
  revisit path.
- **Batch-transaction endpoint** on the apiApp (efficient bulk agent writes): deferred to its
  own feature unless the user wants it in v1 (spec §7).
- **Resources/prompts/Apps/Tasks** deferred until the tool surface proves itself; the change
  feed is their natural first tenant.
