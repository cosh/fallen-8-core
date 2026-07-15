# Fallen-8 MCP Server — Plan

Companion to [spec.md](./spec.md). A separate deployable that bridges MCP to the existing
REST API. Feature branch: `feature/mcp-server` (branch-only workflow — no GitHub issue/PR).

Ordering principle: prove the bridge end-to-end with the smallest read-only surface first,
then widen tools by tier, then harden the remote transport, then land auth in the two
credentialed phases (static bearer before OAuth — the spec's "authentication eventually,
multiple phases"). Deployment packaging goes last so it ships what actually exists.

## Phase 0 — Scaffold & round-trip harness

Intent: a walking skeleton — MCP client ↔ MCP server ↔ real apiApp — before any surface area.

- [ ] New `fallen-8-mcp` project (net10.0) added to `fallen-8-core.sln`; MIT headers; pinned
  `ModelContextProtocol` + `ModelContextProtocol.AspNetCore` package versions recorded in the
  feature README.
- [ ] `McpServerOptions` (`Mcp:*`) + `Fallen8TargetOptions` (`F8:BaseUrl`, `F8:ApiKey`,
  `F8:TlsInsecure`) bound and validated at startup.
- [ ] `Fallen8RestClient` (typed `HttpClient`): `GET /status` only, `X-Api-Key` header,
  problem+json error surface.
- [ ] Transport selection: `--stdio` flag / `Mcp:Transport` config; Streamable HTTP host on
  8090 otherwise.
- [ ] Test harness in `fallen-8-unittest`: MCP SDK **client** connected to the server, bridge
  pointed at a `WebApplicationFactory<Program>`-hosted apiApp (volatile, test key).
  First round-trip test: `tools/list` shows `f8_status`; calling it returns the real status.
- [ ] Startup posture log line (transport, auth mode `None`, tiers, F8 target) + the
  warn-and-retry `/status` probe + `/healthz` reporting downstream reachability.

## Phase 1 — Read tier + contract pinning

Intent: the default (read-only) surface, honestly mapped and drift-guarded.

- [ ] Read tools: `f8_get_vertex`, `f8_get_edge` (+ adjacency), `f8_scan_index`,
  `f8_fulltext_search`, `f8_range_scan`, `f8_find_paths` (no filter parameters exposed).
- [ ] Bridge DTOs for those endpoints; **contract test** validating every bridged
  path/method/shape against `features/done/web-ui/openapi-v0.1.json`.
- [ ] Error mapping: F8 problem+json → MCP tool error (`isError` + title/detail); test that
  the API key appears in no tool result, error, or log line.
- [ ] `readOnlyHint: true` annotations; agent-oriented one-line descriptions.
- [ ] Round-trip tests against a seeded graph (sample generator) for each read tool.

## Phase 2 — Write + admin tiers, tier enforcement

Intent: opt-in mutation with honest completion semantics; tier gating at list AND call.

- [ ] Tier flags `Mcp:Tools:EnableWrite` / `EnableAdmin` / `EnableCode` (all default false).
- [ ] Write tools (`f8_create_vertices`, `f8_create_edges`, `f8_set_property`,
  `f8_remove_property`, `f8_remove_elements`, `f8_define_subgraph` code-free) — always
  `waitForCompletion=true`; success ⇒ transaction applied.
- [ ] Admin tools (`f8_save`, `f8_load`, `f8_list_savegames`, `f8_trim`, `f8_tabula_rasa`)
  with `destructiveHint` where applicable.
- [ ] Code-tier tools (`f8_find_paths_filtered`, `f8_define_subgraph_filtered`) — double
  opt-in documented (tier flag here + `EnableDynamicCodeExecution` on the F8 side); the
  security honesty note in the tool descriptions.
- [ ] Enforcement tests: `tools/list` per flag matrix; `tools/call` on a disabled tier
  rejected; write round-trip (create → scan finds it); tabula-rasa annotated destructive.

## Phase 3 — Remote transport hardening + static bearer (auth phase B)

Intent: safe to put on a network you mostly trust.

- [ ] **Origin validation** on the HTTP transport (allow-list, loopback defaults) —
  DNS-rebinding protection per the MCP transport security requirements; tests for
  allowed/blocked origins.
- [ ] Loopback bind unless `Mcp:AllowRemoteAccess=true`; `UNAUTHENTICATED`-style warning when
  remote + auth mode `None`.
- [ ] `Mcp:Auth:Mode = None | StaticToken`; static bearer via `Authorization: Bearer`,
  constant-time compare, 401 otherwise; never logged.
- [ ] Tests: missing/wrong/correct token; warning emitted per posture matrix.
- [ ] Document TLS options for the MCP endpoint itself (Kestrel config or fronting proxy),
  referencing the transport-encryption feature — no re-invention.

## Phase 4 — OAuth 2.1 resource server (auth phase C)

Intent: the standards-track "authentication eventually".

- [ ] `Mcp:Auth:Mode = OAuth`: JWT bearer validation against `Mcp:Auth:Issuer` (metadata
  discovery) + `Mcp:Auth:Audience`; audience binding mandatory (wrong-audience ⇒ 401).
- [ ] **RFC 9728 Protected Resource Metadata** served at
  `/.well-known/oauth-protected-resource`; 401 challenges carry `WWW-Authenticate` with the
  `resource_metadata` pointer.
- [ ] Scope→tier mapping (`f8:read`/`f8:write`/`f8:admin`/`f8:code` by default), always
  **intersected** with the server-side tier flags; a scope never enables a disabled tier.
- [ ] **No token passthrough:** F8 receives only the configured API key — pinned by test.
- [ ] Tests with test-minted JWTs (test signing key via config): valid accepted; wrong
  issuer/audience/expiry rejected; scope intersection matrix; PRM document contents.

## Phase 5 — Packaging: container + compose profile + docs

Intent: ship it the way it will actually run.

- [ ] `fallen-8-mcp/Dockerfile` (sdk → aspnet, mirroring the existing image conventions),
  `EXPOSE 8090`.
- [ ] `docker-compose.yml`: `f8-mcp` service under `profiles: [mcp]` — default `up` is
  unchanged; wired to `http://fallen8:8080`, shared `F8_API_KEY`, healthcheck `/healthz`.
- [ ] Standalone-run documentation (remote F8 over HTTPS; `F8__TlsInsecure` lab flag loudly
  discouraged).
- [ ] `features/open/mcp-server/README.md`: client-connection examples (Claude Code
  `claude mcp add --transport http`, stdio config), tier/auth option tables, the trust-chain
  diagram, the prompt-injection guidance (run read-only by default).
- [ ] Root `README.md`: "Use Fallen-8 from AI agents" section (shared with the skill-library
  feature when it lands).

## Phase 6 — Gate

- [ ] Full `dotnet test` green; build 0 warnings/0 errors; compose default + `--profile mcp`
  both verified manually (documented).
- [ ] Council review per the repo merge gate; fix findings on the branch; `git merge --no-ff`
  to `main`; move `features/open/mcp-server/` → `features/done/`.

## Progress

- [ ] Phase 0 — scaffold + client↔server↔apiApp round-trip harness
- [ ] Phase 1 — read tier + OpenAPI contract pinning + error mapping
- [ ] Phase 2 — write/admin/code tiers + enforcement matrix
- [ ] Phase 3 — origin validation, remote-bind posture, static bearer (auth B)
- [ ] Phase 4 — OAuth 2.1 resource server + scope→tier intersection (auth C)
- [ ] Phase 5 — Dockerfile, compose profile, READMEs
- [ ] Phase 6 — council gate, merge + move to done/

## Decision / revisit conditions

- **Bridge over embedding** is a requirement, not a preference; revisit only if the user
  changes the deployment constraint.
- **Single downstream identity** (one F8 API key) is a consequence of F8's all-or-nothing
  auth; per-caller F8 identities require an F8-side multi-credential feature first.
- **Single-instance remote deployment** in v1; horizontal scaling of the Streamable HTTP
  session state is a revisit condition with real demand.
- **Resources/prompts** (MCP's other primitives) are deliberately deferred until the tool
  surface proves itself; the tier architecture accommodates them without rework.
