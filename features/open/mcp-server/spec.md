# Fallen-8 MCP Server — Specification

> **Status:** Draft, spec only (no implementation yet). Follow the feature workflow in the
> repository root `CLAUDE.md`. Feature branch: `feature/mcp-server` (branch-only workflow —
> no GitHub issue/PR).
>
> **Companion feature:** [skill-library](../skill-library/spec.md) teaches agents *how* to use
> Fallen-8 well; this feature gives them the *tools* to do it. Neither blocks the other; the
> skill library gains an MCP-alignment phase once this lands.
>
> **Revision history:**
> - *2026-07-24a* — re-grounded against the repo after a drift audit: consolidated tool surface,
>   honest loopback/error-mapping, namespaces threaded through, `Fallen8Target:*`/`Mcp:*` config.
> - *2026-07-24b* — incorporated a five-lens principal architecture review. Material changes:
>   the tool surface is authored with **low-level `ListTools`/`CallTool` handlers and
>   hand-authored flat, enum-discriminated schemas** (not SDK typed-parameter generation, which
>   cannot hide params or vary per session) (§3.2); the **`code` capability no longer probes
>   `/status`** (that field does not exist) and relies on Fallen-8's own `403` (§3.6); the
>   packaged remote profile is **credentialed by construction** and the server **fails closed**
>   on remote+anonymous (§3.3, §3.10); **all caller input is percent-encoded/validated** before
>   it touches a downstream URL (§3.9); the bridge **absorbs .NET type names** and enforces
>   **byte budgets / large-value omission** (§3.5); `f8_admin load` bridges the **id-based
>   registry-restore** endpoint (§3.4); the honesty guarantees are scoped to what each endpoint
>   actually delivers (fire-and-forget `trim`, no-op mutations) (§3.7); the target **protocol
>   revision is pinned** (§3.2); an explicit **annotation matrix** (§3.2) and **fail-closed
>   scope→tier** rule (§3.8) are added.

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
   over the network — not only a locally spawned process. (Streamable HTTP is the sole remote
   transport; the **legacy dual-endpoint HTTP+SSE transport** is dropped. Streamable HTTP still
   uses SSE as its server→client stream *framing* — that content type stays enabled.) stdio
   remains supported for local development because the SDK gives it nearly for free.
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
   tools** (not one per REST route), results are **compact, byte-bounded, and paginated by
   default** with opt-in expansion, and .NET plumbing (type names) is absorbed by the bridge so
   agents never emit it. Token economy is a design goal, not an afterthought (§3.2, §3.5).

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
  implementation time** — the stable line is `1.4.x`, a `2.0.0-preview` exists, the SDK moves
  fast), supporting **stdio** and **Streamable HTTP** transports.
- A **REST bridge, not an engine embedding:** no project reference to `fallen-8-core` or the
  apiApp. The bridge defines its own minimal DTOs and pins them with a **contract test against
  the OpenAPI snapshot** (`features/done/web-ui/openapi-v0.1.json`) — the same drift-guard
  pattern the web UI uses (scoped to what the snapshot truthfully encodes — §3.11).
- **A consolidated, hand-authored tool surface** (§3.2) organised by the repo's opt-in security
  tiers (default = least): `read` (default **on**), `write` (default **off**), `admin` (default
  **off**), and `code` — a **capability** (default **off**, double opt-in) that *widens*
  `f8_paths`/`f8_subgraph` with inline fragment parameters rather than adding tools.
- **Namespace-aware throughout** (§3.4): namespace-scoped tools carry an optional `namespace`
  (default reserved `default`); Fallen-8-level tools deliberately do **not**.
- **Auth in three phases** (§3.8): anonymous network-trusted → static bearer → OAuth 2.1
  resource server (RFC 9728 PRM, audience-bound tokens, **fail-closed** scope→tier).
- **Own container image + opt-in, credentialed compose service**, configured entirely by
  environment; equally runnable against a Fallen-8 on another host.
- **Honest posture logging** at startup (transport, bind, auth mode, enabled tiers, target F8),
  routed to **stderr/ILogger only** so stdout stays a clean JSON-RPC frame stream in stdio mode.

**Non-goals**

- **Embedding MCP endpoints into `fallen-8-core-apiApp`** — excluded by requirement 3.
- **Modifying the apiApp / REST contract in v1.** The bridge consumes the public REST contract
  as-is. Where an apiApp change would materially help (a batch-transaction endpoint; a
  dynamic-code flag on `/status`; uniform problem+json bodies), it is recorded as an *impact*
  (§7) and raised with the user — never silently assumed or worked around dishonestly.
- **Being an OAuth authorization server** (no login UI, no token minting, no DCR hosting).
  Phase C validates tokens issued by an external AS (Entra ID, Keycloak, Auth0, …).
- **Per-user Fallen-8 identities.** F8 auth is a single all-or-nothing credential
  (`api-security-boundary`); every authorized MCP caller maps to that one downstream identity.
- **MCP sampling/elicitation, prompts, and the 2026-07 Apps/Tasks extensions** in v1. Resources
  are a stretch tier; the change feed stays REST/SSE-side (§3.2 note).
- **A sandbox for the `code` capability.** An agent allowed to submit filter fragments is
  trusted as the F8 process (`api-security-boundary` honesty, transitively).

## 3. Design sketch

### 3.1 Project & solution shape

```
fallen-8-mcp/                    (new project, net10.0, ASP.NET Core, root ns NoSQL.GraphDB.Mcp)
  Program.cs                     host: stdio | streamable-http by config
  Configuration/McpServerOptions.cs
  Bridge/Fallen8RestClient.cs    typed HttpClient; INJECTABLE primary handler (testability seam)
  Bridge/Dto/…                   minimal request/response records (contract-tested)
  Bridge/UrlSafety.cs            percent-encode + validate every caller-supplied path/query part
  Bridge/ValueMapping.cs         JSON-native value ⇄ Fallen-8 typed-property (FQTN) mapping
  Bridge/ResultShaping.cs        compact/byte-bounded/paginated result encoding (§3.5)
  Tools/ToolCatalog.cs           per-session ListTools/CallTool: builds schemas + gates by tier/scope
  Tools/Schemas/…                hand-authored flat JSON input/output schemas (§3.2)
  Tools/*Handler.cs              one handler per tool; server-side param validation
  Dockerfile
fallen-8-unittest/               MCP tests live in the existing suite (repo convention)
```

MIT license headers on every source file; `Try*(out, …)` style where it fits; MSTest for
everything. **The new project is added to `CodeQualityTest.cs`'s `_allProjects` and
`_productProjects` lists** so the MIT-header / no-`Console.Write*` / no-`DateTime.Now` /
exact-package-version gates actually run on it (they iterate a hardcoded project list;
`Directory.Build.props` warnings-as-errors already covers "any future sibling").

### 3.2 Tools (v1 surface) — consolidated, hand-authored

**Authoring model (decided).** Tools are authored with the SDK's **low-level `ListTools` /
`CallTool` handlers**, not the attribute/typed-parameter path. Rationale: the typed-parameter
path emits exactly one *flat, static* JSON object schema per method and cannot (a) hide/show a
parameter by config, nor (b) present a different tool list/schema per caller. The design needs
all of that — the `code` fragment parameters are absent when the capability is off (so they
cost zero tokens), and under OAuth a caller's tool list is intersected with its scopes. So the
catalog **builds each tool's `inputSchema` (and `outputSchema`) by hand from the authenticated
session's tier+scope, and validates arguments server-side** in the handler.

**Schema shape (decided): flat, enum-discriminated — never `oneOf`/`anyOf`/`$ref`.** Multi-mode
tools use a required `mode`/`op` **enum** plus conditionally-relevant sibling fields whose
applicability is stated in the parameter description; the handler enforces the mode↔field
coupling and returns a precise `isError` message on mismatch. Composition (`oneOf`/`$ref`) is a
tool-selection-accuracy hazard — mid-2026 client tool-calling layers commonly strip or
mishandle it — so an acceptance test asserts the advertised schemas contain no `oneOf`/`anyOf`/
`$ref`.

**Protocol revision (pinned).** The server targets and advertises MCP **`2025-06-18`** (the
stable line the SDK `1.4.x` implements) and negotiates it at `initialize`; it advertises the
`tools` capability only (not resources/prompts). Consequence: `structuredContent` **must be a
JSON object**, so every list-shaped result is wrapped `{ items: [...], nextCursor?, hasMore }`
— never a bare array. RC-only features (bare-value `structuredContent`, JSON-Schema-2020-12
composition, Apps/Tasks) are **not** depended on; adopting a later revision is a §8 revisit.

Nine tools cover the whole v1 surface; each is `f8_`-prefixed, snake_case, with a **terse**
one-line description (descriptions are always-in-context tokens — detail lives in parameter
docs) and an explicit annotation set (matrix below).

| Tier | Tool | What it does | Bridges to |
|------|------|--------------|------------|
| read | `f8_overview` | Discovery & capabilities. No `namespace` ⇒ list namespaces + collection info; with `namespace` ⇒ that graph's status (counts, index inventory, available path/analytics plugins, embedding-provider state, auth state). `detail:"statistics"` adds the full graph-shape snapshot. | `GET /ns`, `GET /status` (+`/ns/{ns}/status`), `GET /statistics` |
| read | `f8_get` | Fetch one element (its single getter already returns properties + grouped adjacency, so `include`/`fields` is a projection over **one** call). `kind:vertex\|edge`, `id`, `include:[out_edges,in_edges,source,target,degree]`, `fields` (property projection). Default: `id`, `label`, **scalar** property values (vector/array values omitted — key+type+length shown). | `GET /vertex/{id}`, `GET /edge/{id}` |
| read | `f8_search` | Find elements. `mode:index\|property\|fulltext\|vector\|semantic` + mode fields; `limit`(default 25, cap 200)+`cursor`; optional `fields` enrichment. **id-first**: each hit is `{id, score?}` (score for vector/semantic/fulltext); label/property enrichment via `fields` (bridge owns the N+1 GET cost, documented). `index`→indexed scan; `property`→un-indexed `/scan/graph/property/{key}` (cold-graph search, no index needed). | `POST /scan/index/all`, `/scan/graph/property/{key}`, `/scan/index/fulltext`, `/scan/index/vector`, `/embedding/search` |
| read | `f8_paths` | Path finding between two elements. `algorithm`, `maxDepth`/`maxResults`, optional `storedQuery` name. **`code` capability** widens it with inline `filter`/`cost` fragments. | `POST /path/{from}/to/{to}` |
| read | `f8_analytics` | Run a declarative whole-graph algorithm (PageRank, WCC, communities, centrality, triangle-count), returning results to the caller. **Read-only in v1** (no write-back — that split-out is deferred, §8). `algorithm`+`options`, optional `partitionId`. | `POST /analytics/{algo}` (+`/partition/{id}`); algorithm list via `f8_overview` |
| write | `f8_mutate` | Apply **one** mutation as a transaction. `op:create_vertex\|create_edge\|set_property\|remove_property\|remove_element\|set_embedding` + op fields; property/literal **values are JSON-native** (bridge infers the Fallen-8 type; explicit `type` escape hatch for `Single[]`/`DateTime`). Always `waitForCompletion`. Honesty per op — see §3.7. | `PUT /vertex`, `PUT /edge`, `PUT`/`DELETE /graphelement/{id}[/{propId}]`, `PUT /graphelement/{id}/embedding/{name}` |
| write | `f8_subgraph` | Define/compute a subgraph from a code-free pattern (or `storedQuery`). **`code` capability** widens it with inline fragments. | `PUT /subgraph` |
| write | `f8_namespace` | Namespace lifecycle: `op:create\|rename\|drop` (list is via `f8_overview`). Kept a **separate** write-tier tool (not folded into `f8_admin`) so namespace CRUD gates independently of admin/durability — the common `write-on / admin-off` posture needs create/rename without exposing save/wipe. `drop` is destructive. | `PUT`/`PATCH`/`DELETE /ns/{name}` |
| admin | `f8_admin` | Durability & maintenance. `op:save\|load\|list_savegames\|trim\|tabula_rasa`. Honest scoping (§3.4): `save`/`trim`/`tabula_rasa` are namespace-scoped; `list_savegames` and `load` are Fallen-8-level (`load` restores a registry entry by **id** with an optional `restoreNamespace` member selector). | `PUT /save`, `PUT /savegames/{id}/load`, `GET /savegames`, `HEAD /trim`, `HEAD /tabularasa` |

**Annotation matrix** (annotations are *untrusted hints* clients use for confirmation UX; real
enforcement is server-side gating, §3.6). `openWorldHint:false` on every tool (closed graph
domain); each tool gets a short human-readable `title`.

| Tool / op | readOnlyHint | destructiveHint | idempotentHint |
|-----------|:---:|:---:|:---:|
| `f8_overview`, `f8_get`, `f8_search`, `f8_paths`, `f8_analytics` | ✓ | — | ✓ |
| `f8_mutate` create_vertex / create_edge | — | — | — |
| `f8_mutate` set_property / remove_property / remove_element / set_embedding | — | — | ✓ |
| `f8_subgraph` (define) | — | — | ✓ |
| `f8_namespace` create / rename | — | — | ✓ (create), — (rename) |
| `f8_namespace` drop | — | ✓ | ✓ |
| `f8_admin` save / list_savegames | — | — | ✓ / ✓ |
| `f8_admin` load | — | ✓ | ✓ |
| `f8_admin` trim / tabula_rasa | — | ✓ | ✓ |

**REST-shape precision** (the bridge must honour these): creates are **PUT** (`PUT /vertex`,
`PUT /edge`); property/element removal is **PUT/DELETE** on `/graphelement/…`; **`trim`/
`tabularasa` are HTTP `HEAD`** (204, fire-and-forget — §3.7); element getters use route params
`{vertexIdentifier}`/`{edgeIdentifier}`; `POST /path/{from}/to/{to}` is verbatim.

**Tier & capability gating** happens at both levels (§3.6): disabled tiers are absent from
`tools/list` *and* rejected on `tools/call`. The `code` capability gates the *parameters* of
`f8_paths`/`f8_subgraph`, not whole tools.

**Result mapping & errors.** The REST surface returns errors in **mixed** shapes — plain-string
bodies via `BadRequest("…")`/`NotFound("…")` (most of Graph/SubGraph), RFC 7807
`application/problem+json` (Namespaces fully; Analytics/SaveGames/Embedding per-status; the
global net from [api-error-contract](../../done/api-error-contract/)), and a **soft-not-found**
convention where getters answer `204`/`200`-null. The bridge maps errors by three rules,
distinguishing them via status code + `Content-Type`: (1) `application/problem+json` body → use
`title`/`detail`; (2) other 4xx/5xx with a string/empty body → status + string as detail; (3)
`204`/`200`-null from a getter, or `200`-empty from a scan → an explicit "not found"/empty
result, **not** an error. Rate-limit `429` maps to a retryable tool error with backoff guidance;
the 1 MB path-request limit `413` maps as a clear tool error. The MCP tool result is
`isError:true` with a compact `{status,title,detail}`; the F8 API key never appears in any
result or log. When [api-error-envelope](../api-error-envelope/spec.md) lands (it preserves the
`type/title/status/detail` shape and makes problem+json universal), rule (2)'s string branch
becomes dead code — no design change. The **contract test does not and cannot pin error-body
shape** (the OpenAPI snapshot advertises `ProblemDetails` where the runtime returns strings);
error shape is pinned by the live round-trip error-mapping tests (§3.11).

> **Deferred, deliberately** (homes exist when demand appears — not silent omissions):
> - **Change feed** (`GET /changefeed`, SSE): a continuous *ordered delta stream* has no clean
>   MCP-native primitive today — resource-updated notifications lose the per-event delta and
>   ordering; the 2026-07 Tasks extension models *bounded* operations that complete. So it stays
>   on the REST/SSE side, out of MCP, until the protocol grows a streaming primitive.
> - **Bulk import/export** (`/bulk/*`): operator-tier, stream-shaped; `POST /bulk/import`
>   (empty-graph) could become an admin op later if agents need dataset ingest.
> - **Analytics write-back**: a distinct write-tier tool with no `readOnlyHint`, deferred (§8).
> - **Raw sample-graph generation** (`PUT /unittest`): a dev/test convenience; superseded by the
>   write tools for agent use.

### 3.3 Transports & remote hardening

- **stdio** for local development (`fallen-8-mcp --stdio`), loopback F8 by default. In stdio
  mode **all logging goes to stderr** — a single stray stdout line breaks the JSON-RPC frame
  stream (a test asserts stdout carries only protocol frames).
- **Streamable HTTP** (default port **8090**) via `ModelContextProtocol.AspNetCore`:
  - **Origin validation** on every HTTP request (DNS-rebinding protection): a **missing/empty
    Origin is allowed** (the primary clients — Claude Code, Desktop, IDE agents — are not
    browsers and send none); a **present-but-unlisted** Origin is rejected; loopback origins are
    allowed by default.
  - **Two separate concepts, not one flag.** `Mcp:Security:BindAddress` controls where Kestrel
    listens (loopback by default; a container must set `0.0.0.0` to be reachable).
    `Mcp:Security:AllowRemoteAccess` controls whether the server *accepts remote callers at all*.
    The server enforces this itself (the apiApp's identically-named flag is **inert** — it never
    reads it — so this is the MCP server's own behaviour, not a mirror). **Fail-closed:** if the
    effective bind is non-loopback **and** `AllowRemoteAccess` is false **or** auth mode is
    `None`, the server **refuses to start** unless an explicit
    `Mcp:Security:AcceptAnonymousRemote=true` override is set (loudly logged) — warn-and-continue
    is not enough for an agent gateway fronting a full-authority key.
  - A lightweight fixed-window **rate limiter** on the HTTP transport (mirroring the apiApp's
    `SensitiveRateLimit` pattern, right-sized — not enterprise machinery) gives the single-process
    downstream backpressure against a looping agent.
  - Session management per the SDK (session IDs are not auth — §3.8). The 2026-07 stateless core
    is a future scale-out enabler (§8).
- **TLS is the deployment's job** (no in-app TLS feature) — *but that deferral carries
  obligations once auth exists* (§3.8): the server **warns/refuses** when `Auth != None` + a
  non-loopback bind + no TLS indicated (neither an HTTPS bind nor a trusted
  `X-Forwarded-Proto=https`); it honours `ForwardedHeaders` (`X-Forwarded-Proto`/`Host`) so
  Phase C metadata is built off the canonical external HTTPS URL. Termination itself is standard
  Kestrel cert config or a fronting proxy (Caddy/Traefik), covered by a docs recipe.

### 3.4 Namespace model (threaded through the whole surface)

A Fallen-8 hosts many namespaces; the tool surface makes the addressed namespace explicit and
optional, defaulting to the reserved `default`:

- **Namespace-scoped tools** — `f8_get`, `f8_search`, `f8_paths`, `f8_analytics`, `f8_mutate`,
  `f8_subgraph`, and `f8_admin`'s `save`/`trim`/`tabula_rasa` ops — take an optional `namespace`
  string. The bridge routes to `/ns/{namespace}/…` when it is set and non-`default`, to the bare
  route otherwise (the bare route is the `default` alias). The namespace is **validated against
  Fallen-8's name rule and percent-encoded** before use (§3.9).
- **Fallen-8-level tools** — `f8_admin`'s `list_savegames` and `load` — take **no** `namespace`
  route param. `list_savegames` (`GET /savegames`) returns registry entries by **id**; `load`
  (`PUT /savegames/{id}/load`) restores by that id, with an optional `restoreNamespace` field
  (the endpoint's `?namespace=` **member selector** — pick one namespace out of a multi-namespace
  save-game), which is distinct from route scoping. This pairing is what makes the
  discover→restore and save→id→load-later workflows buildable.
- **`f8_overview`** is the namespace directory (no `namespace` ⇒ list; with one ⇒ address it).
- **`f8_namespace`** creates/renames/drops namespaces.

The acceptance criteria (§4) and the test harness (§3.11) exercise a **non-`default`** namespace
end-to-end.

### 3.5 Token economy (how the wire stays small)

Agents are the main users; tokens are the budget. The design keeps schemas, inputs, and results
all small:

- **Few, flat schemas.** Nine tools, terse descriptions, detail in parameter docs. Disabled
  tiers/capabilities are absent from `tools/list`, so a read-only server advertises only five
  compact schemas and the `code` params cost nothing when off.
- **No .NET plumbing in inputs.** `f8_mutate` property values and `f8_search` literals/range
  bounds are **JSON-native**; the bridge infers the Fallen-8 type (`ValueMapping`) from the JSON
  value (string→`System.String`, integer→`System.Int32`, real→`System.Double`, bool→`System.Boolean`,
  array-of-number→`System.Single[]`, …), with an explicit `type` escape hatch for the rare
  non-default (`DateTime`, `Single[]` embeddings). Agents never emit `"System.Int32"`.
- **Compact, byte-bounded results.** Elements render as minimal records (`id`, `label`, scalar
  property values); **vector/array property values are omitted by default** (key + type + length
  shown), long strings are **truncated with a marker + original length**, and every result
  carries a **byte budget**: on overflow the envelope sets `truncated:true` with guidance
  ("narrow with `fields`/`limit`"). Count caps alone do not bound tokens (a 1536-dim embedding
  blows the budget at `limit=1`), so the byte budget is the real guard.
- **Pagination with hard caps.** List results default to `limit` 25 (cap 200) with a **stateless
  cursor = offset over ascending id**; the result states `hasMore`. Documented caveat: the bridge
  fetches the full id set from F8 (scans are unpaged server-side) and slices client-side, so
  concurrent mutation can skip/duplicate across pages. For vector/semantic, **`limit` drives `k`**
  (one knob; re-run with larger `k` + slice for later pages) — `k` is not separately exposed.
- **`structuredContent` + `outputSchema`**, with the `content` block constrained to a **tiny
  O(1) stat/pointer line** (e.g. `"12 matches; showing 1–12; cursor=…"`) — never a
  re-serialization of the structured payload (that would double the most expensive part of every
  response). A test asserts `content` length is O(1) in result size, and that target clients
  actually consume `structuredContent` before we pay for `outputSchema` everywhere.
- **One discovery call.** `f8_overview` answers "what is here and what can I do" in a single
  cheap round-trip (counts, indices, available algorithms, embedding on/off, auth state) so agents
  don't probe with many small calls. (It reports only what `/status` actually exposes — it does
  **not** report the dynamic-code switch, which `/status` does not carry; §3.6.)

Testable: result-shape + **byte-budget** guards, a `tools/list` **schema-size guard with a
concrete byte budget** (asserting on serialized bytes incl. descriptions), the O(1)-`content`
assertion, and the no-`oneOf` assertion.

### 3.6 Tier & capability gating

- Config flags `Mcp:Tools:EnableWrite` / `EnableAdmin` (default false) and the
  `Mcp:Tools:EnableCode` capability (default false). Tools for a disabled tier are not
  advertised in `tools/list` (per session) and are rejected on `tools/call`.
- **`EnableCode` is a single MCP-side flag.** When off, the `filter`/`cost` fragment parameters
  are absent from the `f8_paths`/`f8_subgraph` schemas. When on, the fragments are forwarded and
  **Fallen-8's own gate** returns `403` if its `EnableDynamicCodeExecution` is off — surfaced as
  a normal tool error. The server does **not** probe the target's dynamic-code state
  (`/status` does not expose it, and adding it would touch the apiApp — a v1 non-goal). This is
  the honest "double opt-in": MCP-side capability on **and** the target accepts the fragment.
- Under OAuth (§3.8) a caller's scopes are **intersected** with the server-side tier flags — a
  scope can never enable a tier the operator turned off, and (fail-closed, §3.8) absence of a
  scope never grants a tier.

### 3.7 Write honesty (success means exactly what the endpoint guarantees)

Different endpoints give different guarantees; the tool results say precisely which:

- **`f8_mutate` create_vertex / create_edge** — awaited (`waitForCompletion=true`); a rolled-back
  transaction (e.g. edge to a missing vertex → `404`) surfaces as a **tool error**. Success ⇒
  the element was created.
- **`f8_mutate` set_property / remove_property / remove_element / set_embedding** — awaited, but
  an absent-but-in-range element id is a **committed no-op** returning success (an out-of-range
  id rolls back → `500` → tool error). So success means "the transaction applied"; it does **not**
  assert the element existed or that a value changed. The tool description says so; the bridge
  does not fabricate a not-found the REST surface cannot give.
- **`f8_admin` save / load** — awaited; success ⇒ persisted/restored.
- **`f8_admin` trim / tabula_rasa** — **HEAD, `204`, fire-and-forget**: the transaction is
  *enqueued*, not awaited (there is no completion signal). The tool result reports **"enqueued"**,
  never "applied". The general "success ⇒ applied" guarantee is scoped to the awaited paths above.

### 3.8 Authentication phases (the "multiple phases" requirement)

**Phase A — network-trusted (anonymous).** No caller auth; safe only on loopback/private
compose networks. Non-loopback + anonymous is a **fail-closed startup refusal** unless explicitly
overridden (§3.3). Default posture for local agents.

**Phase B — static bearer token.** `Mcp:Auth:StaticToken` (env/user-secrets, never checked in):
requests carry `Authorization: Bearer <token>`; **compared by SHA-256 digest + `FixedTimeEquals`**
(no length- or content-timing leak); `401` otherwise; never logged. Documented as a **pragmatic,
non-standard** trusted-network mode.

**Phase C — OAuth 2.1 resource server (standards-track, per the MCP authorization spec):**

- Validates **JWT access tokens** from a configured external AS: `Mcp:Auth:Issuer` (metadata
  discovery per RFC 8414/OIDC), `Mcp:Auth:Audience` (this server's **canonical external HTTPS**
  resource identifier — built off `ForwardedHeaders`, §3.3).
- Serves **RFC 9728 Protected Resource Metadata** at `/.well-known/oauth-protected-resource`;
  `401` challenges carry `WWW-Authenticate` with the `resource_metadata` pointer.
- **Audience binding is mandatory:** validates the `aud` claim equals the canonical resource
  identifier (RFC 9728 / MCP auth). (RFC 8707 is the *client*→AS request contract, not RS
  behaviour — the RS does not implement it.)
- **Scope→tier is fail-closed:** `read` is the floor; `write`/`admin`/`code` each **require** an
  explicit scope (`f8:write`/`f8:admin`/`f8:code`) **and** the server-side tier flag. A valid,
  audience-bound token with no `f8:*` scope unlocks nothing beyond `read`.
- **No token passthrough:** the caller's token is never forwarded to Fallen-8. Phases are additive,
  selected by `Mcp:Auth:Mode = None | StaticToken | OAuth`.

### 3.9 Downstream trust chain & URL safety (MCP server → Fallen-8)

- Config: `Fallen8Target:BaseUrl`, `Fallen8Target:ApiKey` (sent as `Fallen8Target:ApiKeyHeader`,
  default `X-Api-Key`; the apiApp also accepts `Authorization: Bearer`), `Fallen8Target:TlsInsecure`
  (default `false`; lab-only, loudly logged). **Config-prefix rationale:** the repo's .NET config
  sections are all `Fallen8:*` and `F8_*` already means the compose *shell* variables that
  substitute into `Fallen8__*`; a bare `F8:*` section would collide and read as an embedded
  engine, so the remote connection is `Fallen8Target:*` ("a remote Fallen-8 I point at") and the
  server's own behaviour is `Mcp:*`.
- **URL-construction integrity is the security boundary** (tier gating is enforced purely by
  *which routes the bridge builds*, and the downstream key is full-authority). Therefore **every
  caller-supplied path/query component — namespace, element id, save-game id, storedQuery name —
  is validated and percent-encoded** (`UrlSafety`) before it touches a downstream URL; a namespace
  is additionally checked against Fallen-8's name rule (names permit `?`/`#`/`%`/spaces/Unicode).
  A raw caller string never lands in a URL path or query. A security/contract test drives
  injection strings (`foo?api-version=`, `a#b`, `%2e%2e`, `x/save/all`) and asserts they cannot
  reach an unintended route+verb.
- Startup probes `GET /status`, logs the F8 version + capability flags, and **warns-and-retries**
  rather than crashing (compose ordering). `/healthz` is a **status-only up/down probe** — it does
  **not** disclose the target URL/version/capabilities to anonymous callers (that detail goes to
  logs).
- The API key is the **server's** identity; callers never learn it; it never appears in results,
  errors, or logs.

### 3.10 Deployment

- `fallen-8-mcp/Dockerfile` (sdk build → aspnet runtime), `EXPOSE 8090`.
- `docker-compose.yml`: an `f8-mcp` service under **`profiles: [mcp]`** (no compose file uses
  `profiles` today — introduced deliberately; default `up` unchanged). It binds `0.0.0.0` inside
  the container (loopback-default is a no-op there) and is therefore **credentialed by
  construction**: a required `Mcp:Auth:StaticToken` (Phase B) is pre-wired via env, so the
  packaged remote deployment is never anonymous-on-a-reachable-bind. Downstream `F8_API_KEY` →
  `Fallen8Target__ApiKey`; healthcheck on `/healthz`. **The repo drives compose via
  `npm run env:up` (`scripts/env-up.js`)** — the packaging phase adds an `env:up --profile mcp` /
  `env:mcp` path there (keeping the automatic `docker-compose.gpu.yml` layering) rather than
  assuming a raw `docker compose up`.
- Documented standalone run:
  `docker run … -e Fallen8Target__BaseUrl=https://graph.example:8443 -e Mcp__Auth__StaticToken=… …`.

### 3.11 Test harness

- **In-process end-to-end round-trips.** The MCP SDK's *client* connects to the server; the
  bridge's `Fallen8RestClient` points at a `WebApplicationFactory<TApiAppProgram>`-hosted real
  apiApp (volatile durability, test API key). Because the WAF `TestServer` has **no TCP port**,
  `Fallen8RestClient` exposes an **injectable primary `HttpMessageHandler`** and the harness
  injects `factory.Server.CreateHandler()`. The two `Program` types are disambiguated
  (`WebApplicationFactory<NoSQL.GraphDB.Mcp.Program>` vs the apiApp's). **At least one round-trip
  runs over the real Streamable HTTP transport (loopback Kestrel)** — not only in-memory — so
  requirement #1 is genuinely proven. The end-to-end assertion: `f8_mutate(create_vertex)` in a
  **non-`default`** namespace → `f8_search` finds it → `f8_paths` returns the seeded path.
- **Read-tier seeding (Phase 1)** is done by the harness against the WAF apiApp **via its own REST
  client** (in-process, independent of the MCP write tier, which arrives in Phase 2).
- **Tier/scope tests:** `tools/list` per tier/capability/scope matrix (incl. `code`-off hides the
  fragment params; an OAuth caller lacking a scope does not see that tier's tools); `tools/call` on
  a disabled tier rejected even when the name is known.
- **Token-economy tests:** compact/scalar-only default, vector-value omission, string truncation
  marker, **byte-budget** overflow → `truncated:true`, O(1) `content`, `limit`/`cursor` cap, the
  `tools/list` **byte** schema-size guard, and the **no-`oneOf`/`$ref`** schema assertion.
- **Auth tests:** Phase B — missing/wrong/correct bearer (401/401/200), digest compare. Phase C —
  test-minted JWTs (test signing key): wrong audience/issuer/expiry rejected, valid accepted,
  **fail-closed scope→tier matrix** (no-scope, partial-scope), PRM served, `401` carries
  `WWW-Authenticate` with `resource_metadata`. Startup **fail-closed** refusal on non-loopback +
  anonymous.
- **URL-safety test:** injection strings cannot reach an unintended route (§3.9).
- **Contract test:** every bridged path/method (incl. `HEAD`)/route-param/**success**-DTO shape
  against the pinned OpenAPI snapshot; **scoped explicitly to success shapes** — it does not guard
  error bodies (§3.2).
- **Error-mapping tests:** problem+json → `title/detail`; plain-string → detail; `204`/`200`-null
  → not-found/empty; `429` retryable; key never leaks.

## 4. Acceptance criteria

- **Round-trip.** An MCP client over **Streamable HTTP (loopback Kestrel)** lists tools, calls
  `f8_overview`, mutates (write tier on) vertices/edges in a **non-`default`** namespace, and reads
  them back — against a real apiApp instance.
- **Small, flat surface.** Default (read-only) `tools/list` = exactly the five read tools; enabling
  write/admin adds their tools; the `code` capability adds *parameters* to `f8_paths`/`f8_subgraph`,
  not tools; advertised schemas contain **no `oneOf`/`anyOf`/`$ref`**; the `tools/list` byte budget
  holds.
- **Token economy.** JSON-native inputs (no FQTNs); compact scalar-only element default with
  vector values omitted; byte-budget overflow signals `truncated:true`; `f8_search` paginates
  id-first with scores preserved; `content` is O(1).
- **Tiers/scope.** Calls to disabled tiers are rejected even when the tool name is known; the
  annotation matrix is applied; the `code` capability requires the target to accept the fragment
  (its `403` surfaces as a tool error); OAuth scope→tier is **fail-closed**.
- **Auth phases.** Non-loopback + anonymous **fails closed at startup** (unless overridden);
  `StaticToken` enforces the bearer (digest, constant-time); `OAuth` serves RFC 9728 metadata,
  challenges correctly, rejects wrong-audience/issuer, honours fail-closed scope→tier. No mode
  forwards caller credentials to F8.
- **URL safety.** Caller-supplied namespace/id/storedQuery cannot inject a route; all are
  encoded/validated.
- **Honest writes.** Creates return success only after the transaction applies; property/element
  mutations disclose no-op-on-absent semantics; `trim`/`tabula_rasa` report "enqueued".
- **Honest errors.** problem+json, plain-string, and soft-not-found responses all map cleanly; no
  key leakage.
- **Contract pinned** (success shapes + methods incl. HEAD + `/ns` twins); **error shape pinned by
  round-trip tests**.
- **Suite green**, build clean (0 warnings), `CodeQualityTest` covers the new project, existing
  projects untouched except the solution file.

## 5. Risks

- **Prompt-injection × write/admin/code tools.** Mitigations: least-privilege tier defaults, the
  annotation matrix (§3.2) that clients use for confirmation UX, the `code` double opt-in, the
  transport rate limiter, and README guidance to run agent-facing servers read-only unless there
  is a concrete need. Annotations are hints; server-side gating is the enforcement.
- **SDK/spec velocity.** The C# SDK (`1.4.x`/`2.0-preview`) and the MCP spec (the **2026-07-28 RC**
  is the largest revision since launch) move fast. Mitigations: pin exact package + **protocol
  revision (`2025-06-18`)**; SDK-client round-trip tests fail loudly on a breaking change; RC
  features are noted, not depended on.
- **Schema-generation assumption.** The whole tier/token story needs per-session, flat,
  enum-discriminated schemas — hence the **low-level handler** decision (§3.2). Phase 0 pins the
  advertised schema shape with an assertion test before the 9-tool surface is committed.
- **Write chattiness / no batch.** No batch-transaction REST endpoint exists (creates are
  single-element; `/bulk/import` needs an empty graph), so `f8_mutate` is one-op-per-call.
  Building a large graph is many round-trips. A batch endpoint on the apiApp would fix this —
  recorded as an impact (§7), not worked around with non-atomic client-side fan-out.
- **Version skew** MCP server ↔ F8: the startup `/status` probe logs both; the contract test pins
  the success shape; mismatches surface as tool errors, not silent corruption.
- **Token-passthrough temptation** (Phase C): forbidden and tested (F8 receives only the API key).
- **Session/state semantics** of Streamable HTTP behind load balancers: v1 documents
  single-instance; the 2026-07 stateless core is the scale-out revisit (§8).

## 6. Keep (do not regress)

- **`fallen-8-core-apiApp` is untouched** in v1: no MCP packages, no new endpoints, no auth
  changes. The bridge consumes the public REST contract only.
- **The `api-security-boundary` posture:** the F8 API key remains required/optional exactly as
  configured; the MCP server neither weakens nor bypasses it.
- **The pinned OpenAPI snapshot** remains the single REST-contract source of truth; the contract
  test reads it (success shapes), never forks it.
- **Compose default behaviour:** `npm run env:up` / `docker compose up` without the `mcp` profile
  starts exactly today's services.
- **The repo's test bar and quality gates:** MSTest tests for every behaviour; warnings-as-errors;
  MIT headers; no `Console.Write*`/`DateTime.Now` in product code — and the new project is added to
  `CodeQualityTest`'s lists so those gates actually run.

## 7. Impact on existing features (cross-feature sweep)

- **REST contract / OpenAPI snapshot** — *consumes only* (success shapes). No snapshot change.
- **api-error-envelope (open)** — *dependency, non-blocking.* v1 tolerates today's mixed error
  bodies; when it lands, the string branch becomes dead code. No apiApp change requested here.
- **graph-analytics / observability / vector-index / embedding-provider (done)** — *surfaced, not
  changed.* Consumed read-only via `f8_analytics` / `f8_overview` / `f8_search`; `set_embedding`
  writes via the existing `PUT /graphelement/{id}/embedding/{name}`. No impact on those features.
- **Dynamic-code discovery.** The `code` capability would ideally read the target's
  `EnableDynamicCodeExecution`, but `/status` does not expose it. v1 does **not** request an apiApp
  change; it relies on Fallen-8's `403` (§3.6). **Optional future impact for the user:** add an
  `EnableDynamicCodeExecution` flag to `StatusREST` so `f8_overview` can advertise it proactively —
  a small apiApp+snapshot change, deferred unless wanted.
- **Efficient writes (opportunity, needs a decision).** An agent-friendly write path wants a
  **batch-transaction REST endpoint** (many elements, one atomic transaction) which the engine
  supports internally but the REST surface does not expose. Adding it touches the apiApp + snapshot
  (a v1 non-goal). **Options:** (a) ship v1 with single-op `f8_mutate`, open a
  `batch-transaction-endpoint` feature later; (b) land that endpoint first. **Default: (a).**
- **Studio UI / NL-assist dataset** — *no impact* (no engine/REST change; no RETRAIN-LOG entry).
- **skill-library (open, companion)** — gains an MCP-alignment phase once this lands (tracked
  there). **agent-host (open)** depends on this feature's Phases 0–2 — coordinate, don't duplicate.

## 8. Decision / revisit conditions

- **Low-level handler tool-authoring + flat enum-discriminated schemas** — required by the
  tier/scope/token design; revisit only if a target client is verified to honour composition.
- **Protocol `2025-06-18`** pinned; adopting a later revision (RC stateless core, bare-value
  `structuredContent`, Apps/Tasks) is a revisit with real demand.
- **`f8_namespace` kept separate** (not folded into `f8_admin`): tier-gating clarity —
  namespace CRUD is write-tier, durability is admin-tier, and the common `write-on/admin-off`
  posture needs the former without the latter.
- **`f8_analytics` read-only in v1**; write-back is a deferred distinct write-tier tool.
- **OAuth (Phase C) is a tracked fast-follow.** v1 may merge at **Phase 3 (static bearer)** with
  Phase 4 continuing on the same branch — it is the largest, most SDK/spec-velocity-exposed phase,
  and read/write/bearer is independently valuable.
- **Bridge over embedding** is a requirement; revisit only if the deployment constraint changes.
- **Single downstream identity** (one F8 API key) follows from F8's all-or-nothing auth; per-caller
  F8 identities need an F8-side multi-credential feature first.
- **Single-instance remote deployment** in v1; the MCP 2026-07 stateless core is the scale-out path.
- **Batch-transaction endpoint** and the **`/status` dynamic-code flag** are deferred apiApp
  impacts (§7), taken up only on the user's word.
