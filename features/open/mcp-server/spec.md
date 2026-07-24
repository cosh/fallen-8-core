# Fallen-8 MCP Server — Specification

> **Status:** Draft, spec only (no implementation yet). Follow the feature workflow in the
> repository root `CLAUDE.md`. Feature branch: `feature/mcp-server` (branch-only workflow —
> no GitHub issue/PR).
>
> **Companion feature:** [skill-library](../skill-library/spec.md) teaches agents *how* to use
> Fallen-8 well; this feature gives them the *tools* to do it. Neither blocks the other; the
> skill library gains an MCP-alignment phase once this lands.
>
> **Revision note (2026-07-24):** this spec was re-grounded against the repo after a drift
> audit. The material changes over the first draft: (a) the tool surface was **consolidated**
> into a small, token-efficient set (§3.2) instead of one-tool-per-endpoint; (b) the loopback
> claim no longer says it "mirrors" an apiApp posture flag that turned out to be inert
> (§3.3); (c) the error-mapping design now tolerates the plain-string error bodies the REST
> surface actually returns today and cites [api-error-envelope](../api-error-envelope/spec.md)
> (§3.2); (d) namespaces are threaded through the whole design, not just a header note (§3.4);
> (e) the config prefix and compose story were reconciled with the repo's real conventions
> (§3.6, §3.7). The "Impact on existing features" sweep is §7.

> **Namespaces (feature graph-namespaces, 2026-07-23):** a Fallen-8 is a *collection of
> namespaces*; each namespace is one isolated graph. The REST surface is namespace-scoped —
> every **data** route also answers under `/ns/{ns}/…`, and a bare route aliases the reserved
> `default` namespace. Save-games and factory-reset-all are *Fallen-8-level* (`[Fallen8Level]`),
> not namespaced. This distinction is load-bearing for the tool design (§3.4);
> see [graph-namespaces](../../done/graph-namespaces/).

## 1. Overview & requirements

Fallen-8 speaks to programs (REST + OpenAPI) and to humans (F8 Studio). It has no first-class
surface for **AI agents** — and agents are expected to become the primary users of Fallen-8.
The Model Context Protocol (MCP) is the established open standard for that surface: an MCP
server exposes typed tools/resources that any MCP client (Claude Code, Claude Desktop/claude.ai,
IDEs, other vendors' agents) can discover and call.

Three requirements are fixed up front (user-stated):

1. **Remote MCP.** The server speaks MCP's **Streamable HTTP** transport so agents connect
   over the network — not only a locally spawned process. stdio remains supported for local
   development because the SDK gives it nearly for free.
2. **Authentication, eventually — multiple phases required.** The rollout is explicitly
   phased: network-trusted first, a pragmatic static bearer token next, then standards-track
   **OAuth 2.1 resource-server** support per the MCP authorization specification.
3. **Deployable outside of Fallen-8.** The MCP server is its **own deployable** (own project,
   own process, own container image), never middleware inside `fallen-8-core-apiApp`. It
   bridges to *any* reachable Fallen-8 instance over the existing REST API.

A fourth principle, stated by the user for this revision and treated as a first-class design
constraint:

4. **A small, token-frugal surface.** Agents pay for every tool schema on every turn and for
   every byte a tool returns. The surface is a handful of **consolidated, capability-oriented
   tools** (not one per REST route), and results are **compact and paginated by default** with
   opt-in expansion. Token economy is a design goal, not an afterthought (§3.2, §3.5).

### Why a separate deployable (beyond the requirement)

- **Blast radius:** an agent-facing surface (new SDK, new protocol, session state) stays out
  of the database process; an MCP bug cannot take down the graph.
- **Independent lifecycle:** the MCP surface can version/scale/restart independently, and one
  MCP server can front different F8 instances (local scratch, shared staging).
- **Clean trust chain:** the MCP server holds exactly one downstream credential (the F8 API
  key) and never mixes it with caller credentials (§3.9).

## 2. Goals / non-goals

**Goals**

- A new **`fallen-8-mcp`** project in the solution: ASP.NET Core host + the official MCP C#
  SDK (`ModelContextProtocol` / `ModelContextProtocol.AspNetCore`; **pin exact versions at
  implementation time** — the stable line is `1.4.x`, a `2.0.0-preview` exists, and the SDK
  moves fast), supporting **stdio** and **Streamable HTTP** transports.
- A **REST bridge, not an engine embedding:** no project reference to `fallen-8-core` or the
  apiApp. The bridge defines its own minimal DTOs and pins them with a **contract test against
  the OpenAPI snapshot** (`features/done/web-ui/openapi-v0.1.json`) — the same drift-guard
  pattern the web UI uses.
- **A consolidated tool surface** (§3.2) organised by the repo's opt-in security tiers
  (default = least):
  - `read` (default **on**): overview/discovery, element+neighbourhood fetch, search
    (property/fulltext/range/vector/semantic), unfiltered/stored-query path finding, whole-graph
    analytics.
  - `write` (default **off**): single-transaction mutations, code-free subgraph definitions,
    namespace lifecycle, analytics write-back.
  - `admin` (default **off**): save/load, list save-games, trim, tabula rasa — annotated
    `destructiveHint` where applicable.
  - `code` (a **capability**, default **off**, double opt-in): it does **not** add tools — it
    *widens* `f8_paths` and `f8_subgraph` with inline C# filter/cost fragment parameters, and
    unlocks analytics/subgraph fragments. Effective only when the F8 instance has
    `EnableDynamicCodeExecution=true` **and** this capability is flagged on; when off, the
    fragment parameters are absent from the tool schema (so they cost no tokens) and rejected
    on call.
- **Namespace-aware throughout** (§3.4): every namespace-scoped tool carries an optional
  `namespace` parameter (defaulting to the reserved `default`); Fallen-8-level tools
  (save-game listing, collection-wide reset) deliberately do **not**.
- **Auth in three phases** (§3.8): anonymous network-trusted → static bearer → OAuth 2.1
  resource server (RFC 9728 protected-resource metadata, audience-bound tokens).
- **Own container image + opt-in compose service**, configured entirely by environment; equally
  runnable against a Fallen-8 on another host ("outside of Fallen-8" in the literal deployment
  sense).
- **Honest posture logging** at startup (transport, bind, auth mode, enabled tiers, target F8)
  in the style of the apiApp's security warnings.

**Non-goals**

- **Embedding MCP endpoints into `fallen-8-core-apiApp`** — excluded by requirement 3.
- **Modifying the apiApp / REST contract in v1.** The bridge consumes the public REST contract
  as-is. Where an apiApp change would materially help the agent experience (a batch-transaction
  endpoint for efficient writes; uniform problem+json bodies), it is recorded as an *impact*
  (§7) and raised with the user — never silently assumed.
- **Being an OAuth authorization server** (no login UI, no token minting, no dynamic client
  registration hosting). Phase C validates tokens issued by an external AS (Entra ID,
  Keycloak, Auth0, …).
- **Per-user Fallen-8 identities.** F8 auth is a single all-or-nothing credential
  (`api-security-boundary`); every authorized MCP caller maps to that one downstream identity.
  Finer-grained mapping becomes possible only if F8 itself grows multi-credential auth.
- **The legacy HTTP+SSE transport** — Streamable HTTP only for remote; stdio for local.
- **MCP sampling/elicitation client features, prompts, and the 2026-07 Apps/Tasks extensions**
  in v1. Resources are a stretch tier in the plan; the **change feed is the natural first
  resource/Task** and is deferred *with* them (§3.2 note, §7).
- **A sandbox for the `code` capability.** The `api-security-boundary` honesty note applies
  transitively: an agent allowed to submit filter fragments is trusted as the F8 process.

## 3. Design sketch

### 3.1 Project & solution shape

```
fallen-8-mcp/                    (new project, net10.0, ASP.NET Core)
  Program.cs                     host: stdio | streamable-http by config
  Configuration/McpServerOptions.cs
  Bridge/Fallen8RestClient.cs    typed HttpClient over the REST surface
  Bridge/Dto/…                   minimal request/response records (contract-tested)
  Bridge/ResultShaping.cs        compact/paginated result encoding (§3.5)
  Tools/OverviewTool.cs GetTool.cs SearchTool.cs PathsTool.cs AnalyticsTool.cs
  Tools/MutateTool.cs SubgraphTool.cs AdminTool.cs NamespaceTool.cs
  Tools/ToolTiers.cs             tier registration + list/call gating (§3.6)
  Dockerfile
fallen-8-unittest/               MCP tests live in the existing suite (repo convention)
```

MIT license headers on every source file; `Try*(out, …)` style where the pattern fits;
MSTest for everything. One namespace root: `NoSQL.GraphDB.Mcp.*`.

### 3.2 Tools (v1 surface) — consolidated

Design rule: **one tool per cohesive capability**, not per REST route. Each tool is
`f8_`-prefixed, snake_case; carries a JSON schema (SDK-generated from typed parameters, using
JSON-Schema-2020-12 `oneOf`/discriminated shapes where a tool spans a few modes); a **terse**
one-line description written for agent consumption (descriptions are always-in-context tokens —
keep them short and lean on parameter descriptions for detail); and MCP tool annotations
(`readOnlyHint`, `destructiveHint`, `idempotentHint`). Nine tools cover the whole v1 surface and
the set grows sub-linearly as REST endpoints are added.

| Tier | Tool | What it does | Bridges to |
|------|------|--------------|------------|
| read | `f8_overview` | Discovery & capabilities. No `namespace` ⇒ list namespaces + collection info; with `namespace` ⇒ that graph's status (counts, index inventory, available path/analytics plugins, embedding-provider state, auth/capability flags). `detail:"statistics"` adds the full graph-shape snapshot. | `GET /ns`, `GET /status` (+`/ns/{ns}/status`), `GET /statistics` |
| read | `f8_get` | Fetch one element and, on request, its neighbourhood. `kind:vertex\|edge`, `id`, `include:[properties,out_edges,in_edges,source,target,degree]` (default minimal). | `GET /vertex/{id}`, `GET /edge/{id}`, the `…/edges/*` + `…/source\|target` adjacency routes |
| read | `f8_search` | Find elements. `mode:property\|fulltext\|range\|vector\|semantic` + mode fields; `limit`(default 25, cap 200)+`cursor`; `fields` projection. `semantic`/`vector` require the target's embedding/vector index. | `POST /scan/index/all`, `/scan/index/fulltext`, `/scan/index/range`, `/scan/index/vector`, `/embedding/search` |
| read | `f8_paths` | Path finding between two elements. `algorithm`, `maxDepth`/`maxResults`, optional `storedQuery` name. **`code` capability** widens it with inline `filter`/`cost` fragments. | `POST /path/{from}/to/{to}` |
| read | `f8_analytics` | Run a declarative whole-graph algorithm (PageRank, WCC, communities, centrality, triangle-count). `algorithm`+`options`, optional `partitionId`. `writeBack` requires the **write** tier. | `POST /analytics/{algo}` (+`/partition/{id}`); algorithm list via `f8_overview` |
| write | `f8_mutate` | Apply **one** mutation as a transaction (always `waitForCompletion`; success ⇒ applied). `op:create_vertex\|create_edge\|set_property\|remove_property\|remove_element` + op fields. | `PUT /vertex`, `PUT /edge`, `PUT`/`DELETE /graphelement/{id}[/{propId}]` |
| write | `f8_subgraph` | Define/compute a subgraph from a code-free pattern (or `storedQuery`). **`code` capability** widens it with inline fragments. | `PUT /subgraph` |
| write | `f8_namespace` | Namespace lifecycle: `op:create\|rename\|drop` (list is via `f8_overview`). `drop` carries `destructiveHint`. | `PUT`/`PATCH`/`DELETE /ns/{name}` |
| admin | `f8_admin` | Durability & maintenance: `op:save\|load\|list_savegames\|trim\|tabula_rasa`. `trim`/`tabula_rasa`/`load` carry `destructiveHint`. Honest scoping (see §3.4): `save`/`load`/`trim`/`tabula_rasa` are namespace-scoped; `list_savegames` and the collection-wide variants are Fallen-8-level. | `PUT /save`, `PUT /load`, `GET /savegames`, `HEAD /trim`, `HEAD /tabularasa` (+ `/save/all`, `/tabularasa/all`) |

**REST-shape precision** (the bridge must honour these; they are not obvious from the tool
names): creates are **PUT** (`PUT /vertex`, `PUT /edge`), property/element removal is
**PUT/DELETE** on `/graphelement/…`, and **`trim`/`tabularasa` are HTTP `HEAD`**. Element getters
use route params `{vertexIdentifier}`/`{edgeIdentifier}`. `POST /path/{from}/to/{to}` is verbatim.

**Tier gating happens at both levels** (§3.6): disabled tiers are absent from `tools/list`
*and* rejected on `tools/call` (defence against clients replaying cached tool lists). The
`code` capability additionally gates the *parameters* of `f8_paths`/`f8_subgraph`, not whole
tools.

**Result mapping & errors.** The REST surface does **not** return a uniform error body today:
per [api-error-envelope](../api-error-envelope/spec.md) most endpoints the bridge consumes
(vertex/edge getters, scans, path, mutations, subgraph) still return **plain-string** bodies
via `BadRequest("…")`/`NotFound("…")`; only a few paths emit RFC 7807 `application/problem+json`
(the global net from [api-error-contract](../../done/api-error-contract/), and
`EmbeddingController` 502/503). The bridge therefore maps errors defensively: **if** the body is
problem+json, use its `title`/`detail`; **else** treat the string body as the detail. Either way
the MCP tool result is `isError:true` with a compact `{status,title,detail}` and the F8 API key
never appears in any result or log. When api-error-envelope lands (it preserves the
`type/title/status/detail` shape, so the mapping does not change — it only makes the
problem+json branch universal), the string-body fallback becomes dead code and the error-mapping
test can assert problem+json everywhere. Write tools always pass `waitForCompletion` so a
success result means the transaction is applied — an agent must never act on an
enqueued-but-unapplied write.

> **Deferred, deliberately** (each has a home when demand appears — not silent omissions):
> - **Change feed** (`GET /changefeed`, SSE): a long-lived server-push stream maps to an MCP
>   **resource subscription** or the 2026-07 **Tasks** extension, both deferred with resources
>   (§2). Not a request/response tool.
> - **Bulk import/export** (`/bulk/*`): operator-tier, stream-shaped; revisit if agents need
>   dataset ingest. `POST /bulk/import` (empty-graph) could become an admin-tier `f8_admin`
>   op later.
> - **Raw sample-graph generation** (`PUT /unittest`): a dev/test convenience; the test
>   harness seeds through the write tools themselves (§3.10), so no tool is needed.

### 3.3 Transports & remote hardening

- **stdio** for local development (`fallen-8-mcp --stdio`), loopback F8 by default.
- **Streamable HTTP** (default port **8090**) via `ModelContextProtocol.AspNetCore`:
  - **Origin validation** on every HTTP request (DNS-rebinding protection, per the MCP
    transport security requirements): configurable allow-list, loopback origins allowed by
    default, everything else rejected.
  - **Loopback bind by default; opens up only when `Mcp:Security:AllowRemoteAccess=true`.**
    This is the MCP server's **own** enforcement — Program.cs binds Kestrel to a loopback
    address unless the flag is set. It is *not* described as mirroring the apiApp: the apiApp's
    `Fallen8:Security:AllowRemoteAccess` flag is documented as **reserved and not enforced**
    (Program.cs never reads it; it binds wherever `ASPNETCORE_URLS`/Kestrel says), so there is
    no existing behaviour to mirror. The MCP server borrows the *name and the honest-warning
    voice*, and actually enforces the bind. When remote + auth mode `None`, it logs a
    prominent `UNAUTHENTICATED` warning (voice borrowed from the apiApp's missing-key warning;
    the *trigger* — a non-loopback bind — is the MCP server's own).
  - Session management per the SDK (session IDs are not auth — §3.8 is). The 2026-07 MCP
    **stateless core** is noted as a future scale-out enabler (§8).
- TLS for the MCP endpoint itself is the deployment's job (project decision: no in-app TLS):
  standard Kestrel certificate configuration or a fronting proxy (Caddy/Traefik), covered by a
  docs recipe — not re-invented here.

### 3.4 Namespace model (threaded through the whole surface)

A Fallen-8 hosts many namespaces; the tool surface makes the addressed namespace explicit and
optional, defaulting to the reserved `default`:

- **Namespace-scoped tools** — `f8_get`, `f8_search`, `f8_paths`, `f8_analytics`, `f8_mutate`,
  `f8_subgraph`, and the `save`/`load`/`trim`/`tabula_rasa` ops of `f8_admin` — take an optional
  `namespace` string. The bridge routes to `/ns/{namespace}/…` when it is set and non-`default`,
  and to the bare route otherwise (both resolve to the same engine; the bare route is the
  `default` alias).
- **Fallen-8-level tools** — `f8_admin`'s `list_savegames` (and the collection-wide
  `save/all`, `tabula_rasa/all`) — are `[Fallen8Level]` and take **no** `namespace` param.
  `load` restores from a registry entry and takes an *optional* `?namespace=` **member selector**
  (pick one namespace out of a multi-namespace save-game), which is a different thing from route
  scoping — the tool models it as an explicit `restoreNamespace` field, documented as such.
- **`f8_overview`** is the namespace directory: with no `namespace` it lists the namespaces and
  the reserved `default`; with one, it addresses that graph. This is how an agent discovers
  which namespaces exist before working in one.
- **`f8_namespace`** creates/renames/drops namespaces (the CRUD the header note alone used to
  omit).

Every tool's schema documents its namespace behaviour; the acceptance criteria (§4) and the
test harness (§3.10) exercise a non-`default` namespace end-to-end.

### 3.5 Token economy (how the wire stays small)

Agents are the main users; tokens are the budget. The design keeps both the tool *schemas* and
the tool *results* small:

- **Few schemas.** Nine tools, terse descriptions, detail pushed to parameter docs. Disabled
  tiers/capabilities are absent from `tools/list`, so a read-only server advertises only five
  compact schemas and the `code` fragment parameters cost nothing when off.
- **Compact results by default.** Elements render as minimal records (`id`, `label`, and
  property *keys*), not full JSON dumps; `f8_get`'s `include` and `f8_search`'s `fields`
  opt into more. Null/default fields are omitted from the envelope.
- **Pagination with hard caps.** `f8_search` (and any list-shaped result) defaults to a small
  `limit` (25) with an opaque `cursor`, capped (200); the result states whether more exist. The
  server never dumps a whole graph into a tool result.
- **Structured content.** Where the SDK/protocol support it, results use `structuredContent`
  with a declared `outputSchema` so clients can consume fields without re-parsing prose; a
  short human-readable `content` summary accompanies it. Large collections still paginate.
- **One discovery call.** `f8_overview` answers "what is here and what can I do" (counts,
  indices, available algorithms, embedding on/off, dynamic-code on/off, auth state) in a single
  cheap round-trip, so agents don't probe with many small calls.

These are testable: result-shape tests assert the compact default and the cap; a schema-size
guard keeps `tools/list` lean.

### 3.6 Tier & capability gating

- Config flags `Mcp:Tools:EnableWrite` / `EnableAdmin` (default false) and the
  `Mcp:Tools:EnableCode` capability (default false). Tools for a disabled tier are not
  registered (absent from `tools/list`) and are rejected on `tools/call`.
- `EnableCode` is effective only when the target F8 reports `EnableDynamicCodeExecution=true`
  (read from `GET /status`, which surfaces the flag); otherwise the server logs that the
  capability is requested-but-inert and keeps the fragment parameters hidden. This is the
  double opt-in.
- Under OAuth (§3.8) a caller's scopes are **intersected** with the server-side tier flags — a
  scope can never enable a tier the operator turned off.

### 3.7 Configuration

- **Config-section prefix.** The MCP server owns two clean sections: `Mcp:*` for its own
  behaviour (`Mcp:Transport`, `Mcp:Port`, `Mcp:Security:AllowRemoteAccess`,
  `Mcp:Security:Origins`, `Mcp:Auth:*`, `Mcp:Tools:*`) and `Fallen8Target:*` for the downstream
  connection (`Fallen8Target:BaseUrl`, `Fallen8Target:ApiKey`, `Fallen8Target:ApiKeyHeader`
  default `X-Api-Key`, `Fallen8Target:TlsInsecure` default false). **Rationale for not reusing
  a bare `F8:*` prefix** (the first draft's choice): the repo's .NET config sections are all
  `Fallen8:*`, and `F8_*` already means the compose *shell* variables that substitute into
  `Fallen8__*` keys (`F8_API_KEY`, `F8_PORT`, …) — a bare `F8:*` section would collide with
  that family and read as an embedded engine. `Fallen8Target:*` says "a remote Fallen-8 I point
  at," which is exactly what it is; env form `Fallen8Target__BaseUrl`.
- The compose service maps the existing `F8_API_KEY` shell variable into
  `Fallen8Target__ApiKey`, reusing the established wiring (§3.9, §3.10).

### 3.8 Authentication phases (the "multiple phases" requirement)

**Phase A — network-trusted (anonymous).** No caller auth; safe only on loopback/private
compose networks. The server logs a prominent `UNAUTHENTICATED` warning when bound
non-loopback. Default posture, suitable for local agents.

**Phase B — static bearer token.** `Mcp:Auth:StaticToken` (env/user-secrets, never checked
in): requests must carry `Authorization: Bearer <token>`; constant-time comparison; 401
otherwise. Explicitly documented as a **pragmatic, non-standard** trusted-network mode — not
MCP-spec OAuth; some MCP clients will need manual header configuration.

**Phase C — OAuth 2.1 resource server (standards-track, per the MCP authorization spec):**

- Validates **JWT access tokens** issued by a configured external authorization server:
  `Mcp:Auth:Issuer` (metadata discovery per RFC 8414/OIDC), `Mcp:Auth:Audience` (this
  server's canonical resource identifier).
- Serves **Protected Resource Metadata** (RFC 9728) at
  `/.well-known/oauth-protected-resource`, naming the authorization server(s) — this is how
  MCP clients discover where to get a token.
- Challenges with `401` + `WWW-Authenticate` carrying the `resource_metadata` pointer.
- **Audience binding is mandatory** (RFC 8707 resource indicators): tokens minted for another
  service are rejected; scopes (e.g. `f8:read`, `f8:write`, `f8:admin`, `f8:code`) may further
  narrow tiers per caller — scope→tier mapping configurable, intersected with the server-side
  tier flags (§3.6).
- **No token passthrough:** the caller's token is never forwarded to Fallen-8. The F8 API key
  is the server's own credential (§3.9). Phases are additive and selected by config
  (`Mcp:Auth:Mode = None | StaticToken | OAuth`).

### 3.9 Downstream trust chain (MCP server → Fallen-8)

- Config: `Fallen8Target:BaseUrl` (e.g. `http://fallen8:8080` in-network, `https://…`
  cross-host), `Fallen8Target:ApiKey` (sent as the `Fallen8Target:ApiKeyHeader`, default
  `X-Api-Key`; the apiApp also accepts it as `Authorization: Bearer`, so a target that changed
  the header name is honoured), `Fallen8Target:TlsInsecure` (default `false`; lab-only escape
  hatch for self-signed F8 certificates, loudly logged).
- At startup the server probes `GET /status`, logs the F8 version and its capability flags
  (dynamic-code, embedding), and **warns-and-retries** rather than crashing (compose ordering:
  F8 may come up later); the `/healthz` endpoint reports downstream reachability so
  orchestrators see the truth.
- The API key is the **server's** identity. Callers never learn it; it never appears in tool
  results, errors, or logs.

### 3.10 Deployment

- `fallen-8-mcp/Dockerfile` (sdk build → aspnet runtime, mirroring the existing Dockerfile
  conventions), `EXPOSE 8090`.
- `docker-compose.yml`: an `f8-mcp` service under **`profiles: [mcp]`** so the default
  environment is unchanged (no compose file uses `profiles` today — this introduces the
  pattern deliberately). It is wired to `http://fallen8:8080` with `F8_API_KEY` shared via
  env (→ `Fallen8Target__ApiKey`); healthcheck on `/healthz`. **The repo drives compose via
  `npm run env:up` (`scripts/env-up.js`), not raw `docker compose up`** — so the packaging
  phase adds an `env:up --profile mcp` path (or an `env:mcp` script) rather than assuming a
  bare `docker compose up`; a raw `--profile mcp up` bypasses the wrapper's automatic
  `docker-compose.gpu.yml` layering, which the docs call out.
- Documented standalone run: `docker run … -e Fallen8Target__BaseUrl=https://graph.example:8443 …`
  — the "outside of Fallen-8" deployment in the literal sense.

### 3.11 Test harness

- **In-process round-trips:** the MCP SDK's *client* connects to the server over an in-memory
  or loopback transport while the bridge points at a `WebApplicationFactory<Program>`-hosted
  real apiApp (volatile durability, test API key) — asserting genuine end-to-end behaviour:
  `f8_mutate(create_vertex)` → `f8_search` finds it; `f8_paths` returns the seeded path; the
  round-trip runs against a **non-`default` namespace** to pin the namespace routing.
- **Tier tests:** `tools/list` contents per tier/capability flags (including that `code`-off
  hides the fragment parameters); `tools/call` on a disabled tier rejected even when the tool
  name is known.
- **Token-economy tests:** compact default encoding, `limit`/`cursor` cap, `include`/`fields`
  expansion, and a `tools/list` schema-size guard.
- **Auth tests:** Phase B — missing/wrong/correct bearer (401/401/200). Phase C — test-minted
  JWTs against a test signing key via config: wrong audience rejected, wrong issuer rejected,
  valid token accepted, scope→tier intersection enforced, PRM document served, 401 carries
  `WWW-Authenticate` with `resource_metadata`.
- **Contract test:** every bridged endpoint's path/method/DTO shape validated against the
  pinned OpenAPI snapshot (drift in the REST surface fails this suite, not production) — this
  test is what would have caught the PUT-vs-POST / HEAD / route-param drifts.
- **Error mapping tests:** both branches — problem+json → `title/detail`, and plain-string body
  → detail; key never leaks into content.

## 4. Acceptance criteria

- **Round-trip.** An MCP client over Streamable HTTP can list tools, call `f8_overview`, mutate
  (write tier on) vertices/edges in a **non-`default` namespace**, and read them back — against
  a real apiApp instance.
- **Small surface.** The default (read-only) `tools/list` contains exactly the five read tools;
  enabling write/admin adds their tools; the `code` capability adds *parameters* to
  `f8_paths`/`f8_subgraph`, not tools.
- **Token economy.** `f8_search` paginates with a capped default `limit`; element results are
  compact unless `include`/`fields` opt in; the schema-size guard holds.
- **Tiers.** Calls to disabled tiers are rejected even when the tool name is known; destructive
  ops carry `destructiveHint`; the `code` capability additionally requires the F8-side flag to
  be effective.
- **Auth phases.** Mode `None` warns when non-loopback; `StaticToken` enforces the bearer
  (constant-time); `OAuth` serves RFC 9728 metadata, challenges correctly, rejects
  wrong-audience/issuer tokens, honours scope→tier intersection. No mode forwards caller
  credentials to F8.
- **Loopback default.** With `Mcp:Security:AllowRemoteAccess` unset, the HTTP transport binds
  loopback and refuses a remote bind; origin validation rejects unlisted origins.
- **Honest writes.** Write tools return success only after `waitForCompletion` confirms the
  transaction applied; a rolled-back transaction surfaces as a tool error.
- **Honest errors.** Both string-body and problem+json F8 errors map to a clean MCP tool error;
  no API key leakage.
- **Contract pinned.** The OpenAPI-snapshot contract test covers every bridged endpoint,
  including the PUT/DELETE/HEAD methods and the `/ns/{ns}` twins.
- **Suite green**, build clean (0 warnings), existing projects untouched except the solution
  file.

## 5. Risks

- **Prompt-injection × write/admin/code tools:** an agent can be talked into destructive
  calls. Mitigations: least-privilege tier defaults, `destructiveHint` annotations (clients
  surface confirmation UX), the code capability's double opt-in, and README guidance to run
  agent-facing servers read-only unless there is a concrete need.
- **SDK/spec velocity:** the MCP C# SDK (`1.4.x` stable, `2.0-preview`) and the MCP spec (the
  **2026-07-28 RC** is the largest revision since launch) both move fast. Mitigations: pin
  exact package + protocol versions; the round-trip tests use the SDK's own client, so a
  breaking change fails loudly in CI; the 2026-07 features (stateless core, Apps, Tasks,
  structured content, JSON-Schema-2020-12 composition) are noted but not depended on in v1.
- **Version skew** between the MCP server and the F8 instance: the startup `/status` probe logs
  both versions and capabilities; the contract test pins the REST shape the bridge was built
  against; mismatches surface as tool errors with the problem+json/string detail, not silent
  corruption.
- **Write chattiness / no batch:** the REST surface has no batch-transaction endpoint (creates
  are single-element `PUT`; `/bulk/import` requires an empty graph). `f8_mutate` is therefore
  one-op-per-call and honestly non-batched; building a large graph is many round-trips. A
  batch-transaction REST endpoint on the apiApp would fix this cleanly — recorded as an impact
  (§7) for the user to decide, not worked around with non-atomic client-side fan-out.
- **Token-passthrough temptation** (Phase C): forwarding caller tokens downstream would break
  audience binding and leak authority — explicitly forbidden and tested (F8 receives only the
  API key).
- **Session/state semantics** of Streamable HTTP behind load balancers: v1 documents
  single-instance deployment; the 2026-07 stateless-core is the revisit path for scale-out (§8).
- **Scope creep into an auth product:** the AS integration surface is deliberately
  issuer+audience+scopes only.

## 6. Keep (do not regress)

- **`fallen-8-core-apiApp` is untouched** in v1: no MCP packages, no new endpoints, no auth
  changes. The bridge consumes the public REST contract only.
- **The `api-security-boundary` posture:** the F8 API key remains required/optional exactly as
  configured there; the MCP server neither weakens nor bypasses it (all calls carry the key
  like any other client).
- **The pinned OpenAPI snapshot** (`features/done/web-ui/openapi-v0.1.json`) remains the single
  REST-contract source of truth — the MCP contract test reads it, never forks it.
- **Compose default behaviour:** `npm run env:up` / `docker compose up` without the `mcp`
  profile starts exactly today's services.
- **The repo's test bar and quality gates:** every tier/auth/bridge/token-economy behaviour
  lands with MSTest tests; warnings-as-errors; MIT headers; no `Console.Write*`/`DateTime.Now`
  in product code.

## 7. Impact on existing features (cross-feature sweep)

Per the mandatory cross-feature impact check, this feature touches:

- **REST contract / OpenAPI snapshot** — *consumes only.* The contract test reads
  `openapi-v0.1.json`; no snapshot change. If the apiApp routes change, the MCP suite fails
  first. No action for other features.
- **api-error-envelope (open)** — *dependency, non-blocking.* v1 tolerates today's plain-string
  error bodies; when api-error-envelope lands it makes problem+json universal without changing
  the mapping. No coordination needed beyond the citation in §3.2; the error-mapping test
  tightens once it's done. **No apiApp change requested by this feature.**
- **graph-analytics / observability / vector-index / embedding-provider (done)** — *surfaced,
  not changed.* Their endpoints become `f8_analytics` / `f8_overview detail:statistics` /
  `f8_search mode:vector|semantic`. Read-only consumption; no impact on those features.
- **Efficient writes (new opportunity, needs a decision).** An agent-friendly write path wants
  a **batch-transaction REST endpoint** (create/modify many elements in one atomic
  transaction), which the engine already supports internally but the REST surface does not
  expose. Adding it would touch `fallen-8-core-apiApp` (and its OpenAPI snapshot), which this
  feature's non-goals keep untouched in v1. **Options for the user:** (a) ship v1 with
  single-op `f8_mutate` and open a separate `batch-transaction-endpoint` feature later; (b)
  land that endpoint first and have `f8_mutate` bridge to it. Default assumption unless told
  otherwise: **(a)**.
- **Studio UI / NL-assist dataset** — *no impact.* Different surface; no engine or REST
  contract change. No RETRAIN-LOG entry required.
- **skill-library (open, companion)** — gains an MCP-alignment phase once this lands (its
  concern, tracked there).

## 8. Decision / revisit conditions

- **Bridge over embedding** is a requirement, not a preference; revisit only if the user
  changes the deployment constraint.
- **Consolidated 9-tool surface** is chosen for token economy; revisit if real agent usage
  shows a consolidated tool's mode-union hurting model accuracy (split that one tool then, not
  the whole surface). `f8_namespace` vs folding its ops into `f8_admin` is the one open
  consolidation call for the architect review.
- **Single downstream identity** (one F8 API key) is a consequence of F8's all-or-nothing auth;
  per-caller F8 identities require an F8-side multi-credential feature first.
- **Single-instance remote deployment** in v1; the MCP 2026-07 **stateless core** is the
  revisit path for horizontal scale-out when there is real demand.
- **Resources/prompts/Apps/Tasks** (MCP's other primitives) are deliberately deferred until the
  tool surface proves itself; the change feed is their natural first tenant.
- **Batch-transaction endpoint** on the apiApp: deferred to its own feature unless the user
  wants efficient bulk agent writes in v1 (§7).
