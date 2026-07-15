# Fallen-8 MCP Server — Specification

> **Status:** Draft, spec only (no implementation yet). Follow the feature workflow in the
> repository root `CLAUDE.md`. Feature branch: `feature/mcp-server` (branch-only workflow —
> no GitHub issue/PR).
>
> **Companion feature:** [skill-library](../skill-library/spec.md) teaches agents *how* to use
> Fallen-8 well; this feature gives them the *tools* to do it. Neither blocks the other; the
> skill library gains an MCP-alignment phase once this lands.

## 1. Overview & requirements

Fallen-8 today speaks to programs (REST + OpenAPI) and to humans (F8 Studio). It has no
first-class surface for **AI agents**. The Model Context Protocol (MCP) is the established
open standard for that surface: an MCP server exposes typed tools/resources that any MCP
client (Claude Code, Claude Desktop/claude.ai, IDEs, other vendors' agents) can discover and
call.

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

### Why a separate deployable (beyond the requirement)

- **Blast radius:** an agent-facing surface (new SDK, new protocol, session state) stays out
  of the database process; an MCP bug cannot take down the graph.
- **Independent lifecycle:** the MCP surface can version/scale/restart independently, and one
  MCP server can front different F8 instances (local scratch, shared staging).
- **Clean trust chain:** the MCP server holds exactly one downstream credential (the F8 API
  key) and never mixes it with caller credentials (§3.5).

## 2. Goals / non-goals

**Goals**

- A new **`fallen-8-mcp`** project in the solution: ASP.NET Core host + the official MCP C#
  SDK (`ModelContextProtocol` / `ModelContextProtocol.AspNetCore`; exact package versions
  pinned at implementation time), supporting **stdio** and **Streamable HTTP** transports.
- A **REST bridge, not an engine embedding:** no project reference to `fallen-8-core` or the
  apiApp. The bridge defines its own minimal DTOs and pins them with a **contract test against
  the OpenAPI snapshot** (`features/done/web-ui/openapi-v0.1.json`) — the same drift-guard
  pattern the web UI uses.
- **Tool tiers** mirroring the repository's opt-in security philosophy (default = least):
  - `read` (default **on**): status, get vertex/edge, index scans, fulltext search,
    unfiltered path search.
  - `write` (default **off**): create vertices/edges, set/remove properties, remove elements,
    code-free subgraph definitions.
  - `admin` (default **off**): save, load, list save-games, trim, tabula rasa — annotated
    `destructiveHint` where applicable.
  - `code` (default **off**, double opt-in): anything carrying C# filter/cost fragments
    (filtered paths, code-bearing subgraph recipes). Enabling it requires the F8 instance to
    have `EnableDynamicCodeExecution=true` **and** this tier flagged on.
- **Auth in three phases** (§3.4): anonymous network-trusted → static bearer → OAuth 2.1
  resource server (RFC 9728 protected-resource metadata, audience-bound tokens).
- **Own container image + opt-in compose service** (`--profile mcp`), configured entirely by
  environment; equally runnable against a Fallen-8 on another host ("outside of Fallen-8" in
  the literal deployment sense).
- **Honest posture logging** at startup (transport, auth mode, enabled tiers, target F8) in
  the style of the apiApp's security warnings.

**Non-goals**

- **Embedding MCP endpoints into `fallen-8-core-apiApp`** — excluded by requirement 3.
- **Being an OAuth authorization server** (no login UI, no token minting, no dynamic client
  registration hosting). Phase C validates tokens issued by an external AS (Entra ID,
  Keycloak, Auth0, …).
- **Per-user Fallen-8 identities.** F8 auth is a single all-or-nothing API key
  (`api-security-boundary`); every authorized MCP caller maps to that one downstream
  identity. Finer-grained mapping becomes possible only if F8 itself grows multi-credential
  auth (future feature).
- **The legacy HTTP+SSE transport** — Streamable HTTP only for remote; stdio for local.
- **MCP sampling/elicitation client features** and prompt templates in v1 (resources are a
  stretch tier in the plan; prompts can follow demand).
- **A sandbox for the `code` tier.** The `api-security-boundary` honesty note applies
  transitively: an agent allowed to submit filter fragments is trusted as the F8 process.

## 3. Design sketch

### 3.1 Project & solution shape

```
fallen-8-mcp/                    (new project, net10.0, ASP.NET Core)
  Program.cs                     host: stdio | streamable-http by config
  Configuration/McpServerOptions.cs
  Bridge/Fallen8RestClient.cs    typed HttpClient over the REST surface
  Bridge/Dto/…                   minimal request/response records (contract-tested)
  Tools/ReadTools.cs / WriteTools.cs / AdminTools.cs / CodeTools.cs
  Dockerfile
fallen-8-unittest/               MCP tests live in the existing suite (repo convention)
```

MIT license headers on every source file; `Try*(out, …)` style where the pattern fits;
MSTest for everything.

### 3.2 Tools (v1 surface)

Names are `f8_`-prefixed, snake_case; every tool carries a JSON schema (SDK-generated from
typed parameters), a one-line description written for agent consumption, and MCP tool
annotations (`readOnlyHint`, `destructiveHint`, `idempotentHint`).

| Tier | Tool | Bridges to |
|------|------|------------|
| read | `f8_status` | `GET /status` |
| read | `f8_get_vertex`, `f8_get_edge` | `GET /vertex/{id}`, `GET /edge/{id}` (+ adjacency lookups) |
| read | `f8_scan_index`, `f8_fulltext_search`, `f8_range_scan` | the scan endpoints |
| read | `f8_find_paths` | `POST /path/{from}/to/{to}` **without** filter fragments |
| write | `f8_create_vertices`, `f8_create_edges` | the transaction endpoints (`waitForCompletion=true`) |
| write | `f8_set_property`, `f8_remove_property`, `f8_remove_elements` | property/removal endpoints |
| write | `f8_define_subgraph` | `PUT /subgraph`, **code-free recipes only** |
| admin | `f8_save`, `f8_load`, `f8_list_savegames` | save-game endpoints |
| admin | `f8_trim`, `f8_tabula_rasa` | maintenance endpoints (`destructiveHint: true`) |
| code | `f8_find_paths_filtered`, `f8_define_subgraph_filtered` | same endpoints **with** C# fragments |

Tier gating happens at **both** levels: disabled tiers are absent from `tools/list` *and*
rejected on `tools/call` (defense against clients replaying cached tool lists).

Result mapping: F8's problem+json errors (`api-error-contract`) map to MCP tool errors
(`isError: true` + the problem title/detail); the API key never appears in any error or log.
Write tools always pass `waitForCompletion` so a success result means the transaction is
applied — an agent must never act on an enqueued-but-unapplied write.

### 3.3 Transports & remote hardening

- **stdio** for local development (`fallen-8-mcp --stdio`), loopback F8 by default.
- **Streamable HTTP** (default port **8090**) via `ModelContextProtocol.AspNetCore`:
  - **Origin validation** on every HTTP request (DNS-rebinding protection, per the MCP
    transport security requirements): configurable allow-list, loopback origins allowed by
    default, everything else rejected.
  - Binds loopback unless `Mcp:AllowRemoteAccess=true` — the apiApp's posture flag, mirrored.
  - Session management per the SDK (session IDs are not auth — §3.4 is).
- TLS termination for the MCP endpoint itself follows the same recipe as
  [transport-encryption](../transport-encryption/spec.md): standard Kestrel config or a
  fronting proxy; documented, not re-invented here.

### 3.4 Authentication phases (the "multiple phases" requirement)

**Phase A — network-trusted (anonymous).** No caller auth; safe only on loopback/private
compose networks. The server logs a prominent `UNAUTHENTICATED` warning when bound
non-loopback (same voice as the apiApp's warning). Default posture, suitable for local agents.

**Phase B — static bearer token.** `Mcp:Auth:StaticToken` (env/user-secrets, never checked
in): requests must carry `Authorization: Bearer <token>`; constant-time comparison; 401
otherwise. Explicitly documented as a **pragmatic, non-standard** trusted-network mode — it is
not MCP-spec OAuth, and some MCP clients will need manual header configuration.

**Phase C — OAuth 2.1 resource server (standards-track, per the MCP authorization spec):**

- Validates **JWT access tokens** issued by a configured external authorization server:
  `Mcp:Auth:Issuer` (metadata discovery per RFC 8414/OIDC), `Mcp:Auth:Audience` (this
  server's canonical resource identifier).
- Serves **Protected Resource Metadata** (RFC 9728) at
  `/.well-known/oauth-protected-resource`, naming the authorization server(s) — this is how
  MCP clients discover where to get a token.
- Challenges with `401` + `WWW-Authenticate` carrying the `resource_metadata` pointer.
- **Audience binding is mandatory** (RFC 8707 resource indicators): tokens minted for another
  service are rejected; scopes (e.g. `f8:read`, `f8:write`, `f8:admin`) may further narrow
  tiers per caller — scope→tier mapping configurable, intersected with the server-side tier
  flags (a scope can never enable a tier the operator turned off).
- **No token passthrough:** the caller's token is never forwarded to Fallen-8. The F8 API key
  is the server's own credential (§3.5). Phases are additive and selected by config
  (`Mcp:Auth:Mode = None | StaticToken | OAuth`).

### 3.5 Downstream trust chain (MCP server → Fallen-8)

- Config: `F8:BaseUrl` (e.g. `http://fallen8:8080` in-network, `https://…` cross-host),
  `F8:ApiKey` (sent as `X-Api-Key`), `F8:TlsInsecure` (default `false`; lab-only escape hatch
  for self-signed F8 certificates, loudly logged).
- At startup the server probes `GET /status`, logs the F8 version, and **warns-and-retries**
  rather than crashing (compose ordering: F8 may come up later); the `/healthz` endpoint
  reports downstream reachability so orchestrators see the truth.
- The API key is the **server's** identity. Callers never learn it; it never appears in tool
  results, errors, or logs.

### 3.6 Deployment

- `fallen-8-mcp/Dockerfile` (sdk build → aspnet runtime, mirroring the existing Dockerfile
  conventions), `EXPOSE 8090`.
- `docker-compose.yml`: an `f8-mcp` service under **`profiles: [mcp]`** so the default
  environment is unchanged; wired to `http://fallen8:8080` with `F8_API_KEY` shared via env;
  healthcheck on `/healthz`.
- Documented standalone run: `docker run … -e F8__BaseUrl=https://graph.example:8443 …` —
  the "outside of Fallen-8" deployment in the literal sense.

### 3.7 Test harness

- **In-process round-trips:** the MCP SDK's *client* connects to the server over an in-memory
  or loopback transport while the bridge points at a `WebApplicationFactory<Program>`-hosted
  real apiApp (volatile durability, test API key) — asserting genuine end-to-end behaviour:
  `f8_create_vertices` → `f8_scan_index` finds it; `f8_find_paths` returns the seeded path.
- **Tier tests:** `tools/list` contents per tier flags; `tools/call` on a disabled tier
  rejected even when the tool name is known.
- **Auth tests:** Phase B — missing/wrong/correct bearer (401/401/200). Phase C — test-minted
  JWTs against a test signing key via config: wrong audience rejected, wrong issuer rejected,
  valid token accepted, scope→tier intersection enforced, PRM document served, 401 carries
  `WWW-Authenticate` with `resource_metadata`.
- **Contract test:** every bridged endpoint's path/method/DTO shape validated against the
  pinned OpenAPI snapshot (drift in the REST surface fails this suite, not production).
- **Error mapping tests:** F8 problem+json → MCP tool error; key never leaks into content.

## 4. Acceptance criteria

- **Round-trip.** An MCP client over Streamable HTTP can list tools, call `f8_status`, create
  vertices/edges (write tier on), and read them back — against a real apiApp instance.
- **Separate deployable.** The MCP server runs as its own process/container with no F8
  assemblies loaded, pointed at an F8 base URL on another host; the compose default
  (`docker compose up`) is unchanged unless `--profile mcp` is requested.
- **Tiers.** Default exposes read tools only; each tier flag adds exactly its tools to
  `tools/list`; calls to disabled tiers are rejected; destructive tools carry
  `destructiveHint`; the `code` tier additionally requires the F8-side flag to be effective.
- **Auth phases.** Mode `None` warns when non-loopback; `StaticToken` enforces the bearer
  (constant-time); `OAuth` serves RFC 9728 metadata, challenges correctly, rejects
  wrong-audience/issuer tokens, honours scope→tier intersection. No mode forwards caller
  credentials to F8.
- **Origin validation.** Cross-origin requests from unlisted origins are rejected on the HTTP
  transport (DNS-rebinding protection).
- **Honest writes.** Write tools return success only after `waitForCompletion` confirms the
  transaction applied.
- **Contract pinned.** The OpenAPI-snapshot contract test covers every bridged endpoint.
- **Suite green**, build clean, existing projects untouched except the solution file.

## 5. Risks

- **Prompt-injection × write/admin/code tools:** an agent can be talked into destructive
  calls. Mitigations: least-privilege tier defaults, `destructiveHint` annotations (clients
  surface confirmation UX), the code tier's double opt-in, and README guidance to run
  agent-facing servers read-only unless there is a concrete need.
- **SDK maturity/velocity:** the MCP C# SDK and the MCP spec both move. Mitigations: pin
  exact package + protocol versions; the round-trip tests use the SDK's own client, so a
  breaking SDK change fails loudly in CI.
- **Version skew** between the MCP server and the F8 instance: the startup `/status` probe
  logs both versions; the contract test pins the REST shape the bridge was built against;
  mismatches surface as tool errors with the problem+json detail, not silent corruption.
- **Token-passthrough temptation** (Phase C): forwarding caller tokens downstream would break
  audience binding and leak authority — explicitly forbidden and tested (F8 receives only the
  API key).
- **Session/state semantics** of Streamable HTTP behind load balancers: v1 documents
  single-instance deployment; horizontal scale-out is a revisit condition.
- **Scope creep into an auth product:** the AS integration surface is deliberately
  issuer+audience+scopes only.

## 6. Keep (do not regress)

- **`fallen-8-core-apiApp` is untouched** (beyond nothing at all in v1): no MCP packages, no
  new endpoints, no auth changes. The bridge consumes the public REST contract only.
- **The `api-security-boundary` posture:** the F8 API key remains required/optional exactly
  as configured there; the MCP server neither weakens nor bypasses it (all calls carry the
  key like any other client).
- **The pinned OpenAPI snapshot** (`features/done/web-ui/openapi-v0.1.json`) remains the
  single REST-contract source of truth — the MCP contract test reads it, never forks it.
- **Compose default behaviour:** `docker compose up` without profiles starts exactly today's
  services.
- **The repo's test bar:** every tier/auth/bridge behaviour lands with tests in
  `fallen-8-unittest`, MSTest, arrange/act/assert.
