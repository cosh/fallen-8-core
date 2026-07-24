# Fallen-8 MCP Server — Plan

Companion to [spec.md](./spec.md). A separate deployable that bridges MCP to the existing
REST API, with a small, token-frugal, namespace-aware, hand-authored tool surface. Feature
branch: `feature/mcp-server` (branch-only workflow — no GitHub issue/PR).

Ordering principle: prove the bridge end-to-end with the smallest read-only surface first, then
widen tools by tier, then harden the remote transport, then land auth in the two credentialed
phases (static bearer before OAuth). Deployment packaging goes last so it ships what exists.
Namespace awareness, URL safety, token economy, and the loopback-safe default are **built into
every phase**, not bolted on.

## Phase 0 — Scaffold, schema-shape proof & round-trip harness

Intent: a walking skeleton — MCP client ↔ MCP server ↔ real apiApp — plus proof that the chosen
tool-authoring model produces the schemas the whole design depends on, before any surface area.

- [ ] New `fallen-8-mcp` project (net10.0, root ns `NoSQL.GraphDB.Mcp`) added to
  `fallen-8-core.sln`; MIT headers; **pinned** `ModelContextProtocol` +
  `ModelContextProtocol.AspNetCore` versions recorded in the feature README (verify current stable
  `1.4.x` on NuGet; confirm it supports `structuredContent` + `outputSchema`; do not adopt
  `2.0-preview` unless a needed feature requires it).
- [ ] **Add `fallen-8-mcp` to `CodeQualityTest.cs` `_allProjects` and `_productProjects`** so the
  MIT-header / no-`Console.Write*` / no-`DateTime.Now` / exact-version gates run on it.
- [ ] **Tool-authoring skeleton via low-level `ListTools`/`CallTool` handlers** (not typed-param
  attributes): a `ToolCatalog` that builds hand-authored **flat** `inputSchema` per session/tier.
  **Schema-shape proof test**: assert a multi-mode tool's advertised schema is a flat object with
  an enum discriminator and **no `oneOf`/`anyOf`/`$ref`**, and that a disabled-tier/capability
  parameter is absent. This de-risks the load-bearing assumption before the surface is built.
- [ ] **Pin protocol `2025-06-18`** at `initialize`; advertise the `tools` capability only. Test
  asserts the negotiated version + declared capabilities. `structuredContent` is object-wrapped.
- [ ] `McpServerOptions` (`Mcp:*`) + `Fallen8TargetOptions` (`Fallen8Target:*`) bound + validated.
  **Loopback-safe bind is the Phase-0 default** (the free safety default lives with the transport
  it protects); `BindAddress` vs `AllowRemoteAccess` are separate concepts (§3.3).
- [ ] `Fallen8RestClient` (typed `HttpClient`, **injectable primary handler**): `GET /status` only;
  configurable api-key header; `UrlSafety` (percent-encode + validate) used for any path/query part.
- [ ] Transport selection: `--stdio` flag / `Mcp:Transport`; Streamable HTTP host on 8090 otherwise.
  **stdio logging goes to stderr only** (test: stdout carries only protocol frames).
- [ ] Test harness in `fallen-8-unittest`: MCP SDK **client** ↔ server; bridge handler injected
  with `WebApplicationFactory<apiApp Program>.Server.CreateHandler()` (WAF has no TCP port);
  disambiguate the two `Program` types. First round-trip: `tools/list` shows `f8_overview`; calling
  it returns real status + capabilities. **One round-trip over real Streamable HTTP (loopback
  Kestrel).**
- [ ] Startup posture log (transport, bind, auth `None`, tiers, F8 target + capabilities) +
  warn-and-retry `/status` probe + **status-only `/healthz`** (no topology disclosure).

## Phase 1 — Read tier + contract pinning + token economy

Intent: the default (read-only) surface — consolidated, namespace-aware, compact, byte-bounded,
drift-guarded.

- [ ] Read tools (hand-authored flat schemas): `f8_overview`, `f8_get` (single-getter projection +
  `fields`, scalar-only default, vector values omitted), `f8_search` (`mode:index|property|fulltext
  |vector|semantic`, **id-first `{id,score?}`**, `limit`/`cursor`, `fields` enrichment owns the
  N+1 GET cost; `property` mode = un-indexed `/scan/graph/property/{key}`), `f8_paths` (unfiltered +
  `storedQuery`), `f8_analytics` (read-only, no write-back).
- [ ] **Namespace routing** in the bridge (optional `namespace` → `/ns/{ns}/…`, encoded+validated);
  non-`default`-namespace round-trip test.
- [ ] **Token economy**: JSON-native value mapping (read side: search literals/range bounds),
  compact scalar-only records, vector/array-value omission (key+type+length), string truncation
  marker, **per-result byte budget → `truncated:true`**, stateless `cursor`=offset-over-id,
  `limit` drives vector `k`, `structuredContent`+`outputSchema` with **O(1) `content`** line.
- [ ] Bridge DTOs; **contract test** (paths/methods incl. HEAD/route-params/**success** shapes,
  incl. `/ns` twins and verbatim `POST /path/{from}/to/{to}`) vs the OpenAPI snapshot — **success
  shapes only**.
- [ ] Error mapping (three rules): problem+json→`title/detail`; string→detail; `204`/`200`-null→
  not-found/empty; `429` retryable; `413` mapped; **key never leaks** (test). Normalize
  "index not found" vs "zero matches" across search modes. `mode:semantic` discloses its
  embedding-provider dependency (`403` when off).
- [ ] Annotation matrix applied to read tools (`readOnlyHint`+`idempotentHint`, `openWorldHint:false`,
  `title`); terse descriptions, detail in param docs.
- [ ] **Read-tier seeding via the WAF apiApp's own REST client** (independent of the write tier);
  round-trip tests per read tool + per available search mode.
- [ ] **Token-economy guards**: byte-budget overflow, O(1) `content`, `tools/list` **byte** schema
  guard, no-`oneOf`/`$ref` assertion.

## Phase 2 — Write + admin tiers, tier/capability gating

Intent: opt-in mutation with per-op honest completion semantics; gating at list AND call.

- [ ] Tier flags `Mcp:Tools:EnableWrite` / `EnableAdmin` + `EnableCode` capability (all default
  false).
- [ ] Write tools: `f8_mutate` (`op:create_vertex|create_edge|set_property|remove_property|
  remove_element|set_embedding`; **JSON-native values**, FQTN inferred by the bridge with a `type`
  escape hatch; always `waitForCompletion`; **per-op honesty** per §3.7 — creates await/rollback→
  error, property/element mutations disclose no-op-on-absent, out-of-range→`500`→error),
  `f8_subgraph` (code-free define), `f8_namespace` (`op:create|rename|drop`, `drop` destructive;
  separate write-tier tool).
- [ ] Admin tool: `f8_admin` (`op:save|load|list_savegames|trim|tabula_rasa`) with honest scoping
  (§3.4: save/trim/tabula_rasa namespace-scoped; `list_savegames`+`load` Fallen-8-level; `load` =
  **`PUT /savegames/{id}/load`** by id with `restoreNamespace` selector) and **fire-and-forget
  honesty** for HEAD trim/tabula_rasa (report "enqueued").
- [ ] `code` capability: widen `f8_paths`/`f8_subgraph` with `filter`/`cost` params **only when
  `EnableCode`** (params absent otherwise); forward fragments and let Fallen-8's `403` surface as a
  tool error (no `/status` probe); security honesty note in the param descriptions.
- [ ] Annotation matrix applied to write/admin ops (destructive/idempotent per the table);
  URL-safety used for id/namespace/savegame-id/storedQuery.
- [ ] Enforcement tests: `tools/list` per flag matrix (incl. `code`-off hides fragment params);
  `tools/call` on a disabled tier rejected; write round-trip (mutate → search finds it) in a
  non-`default` namespace; destructive ops annotated; no-op-on-absent and fire-and-forget pinned.

## Phase 3 — Remote transport hardening + static bearer (auth phase B)  ← v1 merge candidate

Intent: safe to put on a network you mostly trust. **v1 may merge here** with OAuth as a tracked
fast-follow (§8).

- [ ] **Origin validation**: missing/empty Origin allowed (non-browser clients), present-unlisted
  rejected, loopback allowed; tests for all three.
- [ ] **Fail-closed remote posture**: non-loopback bind + (`AllowRemoteAccess=false` or `Auth=None`)
  ⇒ startup refusal unless `AcceptAnonymousRemote=true` (loudly logged). Test the refusal + override.
- [ ] Lightweight fixed-window **rate limiter** on the HTTP transport (right-sized, mirrors the
  apiApp's `SensitiveRateLimit`).
- [ ] `Mcp:Auth:Mode = None | StaticToken`; bearer compared by **SHA-256 digest + `FixedTimeEquals`**
  (no length/content timing leak), `401` otherwise, never logged. Tests: missing/wrong/correct.
- [ ] Document TLS options + the **auth-over-cleartext guard** and `ForwardedHeaders` handling
  (obligations the "TLS is the deployment's job" deferral carries once auth exists).

## Phase 4 — OAuth 2.1 resource server (auth phase C) — fast-follow

Intent: the standards-track "authentication eventually".

- [ ] `Mcp:Auth:Mode = OAuth`: JWT validation vs `Mcp:Auth:Issuer` (discovery) + `Mcp:Auth:Audience`
  (**canonical external HTTPS** URL via `ForwardedHeaders`); audience binding mandatory
  (validate `aud`; RFC 9728 / MCP auth — not RFC 8707 RS behaviour).
- [ ] **RFC 9728 PRM** at `/.well-known/oauth-protected-resource`; `401` carries `WWW-Authenticate`
  with `resource_metadata`.
- [ ] **Fail-closed scope→tier**: `read` floor; `write`/`admin`/`code` each require explicit scope
  AND server flag; no-scope token unlocks nothing beyond read. Intersected with tier flags.
- [ ] **No token passthrough** (F8 receives only the API key) — pinned by test.
- [ ] Tests (test-minted JWTs): valid accepted; wrong issuer/audience/expiry rejected; **no-scope
  and partial-scope** matrix; PRM contents; `401` challenge shape.

## Phase 5 — Packaging: container + credentialed compose profile + docs

Intent: ship it the way it will actually run.

- [ ] `fallen-8-mcp/Dockerfile` (sdk → aspnet), `EXPOSE 8090`.
- [ ] `docker-compose.yml`: `f8-mcp` under `profiles: [mcp]` (new pattern; default `up` unchanged);
  binds `0.0.0.0` and is **credentialed by construction** (pre-wired required
  `Mcp__Auth__StaticToken`); `F8_API_KEY` → `Fallen8Target__ApiKey`; healthcheck `/healthz`. Add an
  **`env:up --profile mcp` / `env:mcp` path in `scripts/env-up.js`** (keep gpu.yml layering).
- [ ] Standalone-run docs (remote F8 over HTTPS; static token required; `Fallen8Target__TlsInsecure`
  lab flag loudly discouraged).
- [ ] `features/open/mcp-server/README.md`: client-connection examples (Claude Code
  `claude mcp add --transport http`, stdio config), the 9-tool table + annotation matrix,
  tier/auth/token-economy tables, trust-chain diagram, prompt-injection guidance (read-only by
  default), pinned SDK + protocol versions.
- [ ] Root `README.md`: "Use Fallen-8 from AI agents" section (shared with skill-library later).
- [ ] User-facing `docs/mcp-server.md` deep-dive (per the `docs/` convention).

## Phase 6 — Gate

- [ ] Full `dotnet test` green; build 0 warnings/0 errors; `CodeQualityTest` covers the new project;
  compose default + `--profile mcp` both verified manually (documented).
- [ ] Council/architect review per the repo merge gate; fix findings on the branch;
  `git merge --no-ff` to `main`; move `features/open/mcp-server/` → `features/done/`. (OAuth may be a
  tracked fast-follow on the same branch — §8.)

## Progress

- [ ] Phase 0 — scaffold + schema-shape proof + client↔server↔apiApp round-trip (real Streamable HTTP)
- [ ] Phase 1 — read tier (consolidated, namespace-aware, token-frugal, byte-bounded) + contract + errors
- [ ] Phase 2 — write/admin tiers + code capability + per-op honesty + enforcement matrix
- [ ] Phase 3 — origin validation, fail-closed remote posture, rate limiter, static bearer (auth B) — v1 merge candidate
- [ ] Phase 4 — OAuth 2.1 resource server + fail-closed scope→tier (auth C) — fast-follow
- [ ] Phase 5 — Dockerfile, credentialed compose profile + env-up integration, READMEs, docs
- [ ] Phase 6 — architect/council gate, merge + move to done/

## Decision / revisit conditions

- **Low-level handler tool-authoring + flat enum-discriminated schemas** — required by the
  tier/scope/token design (SDK typed-param generation cannot hide params or vary per session);
  proven in Phase 0.
- **Protocol `2025-06-18`** pinned (`structuredContent` object-wrapped); later revisions are a §8
  revisit.
- **`f8_namespace` separate; `f8_analytics` read-only** in v1 (write-back deferred).
- **OAuth (Phase 4) is a fast-follow** — v1 may merge at Phase 3.
- **Single downstream identity**; **single-instance remote**; **batch-transaction endpoint** and a
  **`/status` dynamic-code flag** are deferred apiApp impacts (spec §7), taken up only on the user's
  word.
