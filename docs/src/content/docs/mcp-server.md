---
title: "MCP server"
description: "A Model Context Protocol surface so AI agents call Fallen-8 as typed tools; read-only by default, with tiered writes and three auth modes."
---

Fallen-8 speaks to programs (the REST API) and to humans (F8 Studio). The **MCP server** is
its surface for **AI agents**: a separate service that exposes Fallen-8 as typed
[Model Context Protocol](https://modelcontextprotocol.io) tools any MCP client, Claude Code,
Claude Desktop, IDE agents, other vendors' agents, can discover and call.

It is a **separate deployable**: its own project (`fallen-8-mcp`), its own process and
container image. It never loads the engine; it bridges to a reachable Fallen-8 over the
existing REST API, so one MCP server can front a local scratch graph or a shared instance on
another host. A bug in the agent surface cannot take the database down.

## Running it

**With the compose environment (the default).** The MCP server comes up with the rest of the
environment on `http://localhost:8090`, **anonymous and read-only**, matching this
environment's no-auth-in-the-way posture (the `fallen8` service itself runs with no API key
here). Nothing extra to run:

```bash
npm run env:up
# f8-mcp is on http://localhost:8090, fronting the fallen8 service, read-only.
```

This local-dev posture is **not** for a serious deployment; see "Securing it" below.

**Securing it for a real (off-box) setup.** Everything is env-var configurable on the `f8-mcp`
service, so a serious setup locks it down without code changes: set an auth mode + credential,
and opt into only the tiers you need. Via the compose env, with the token **exported** rather
than inlined (nothing prints it back, and you need it again to register a client):

```bash
export F8_MCP_TOKEN=$(openssl rand -hex 32)
# static bearer + writes enabled, still on the compose network:
F8_MCP_AUTH_MODE=StaticToken F8_MCP_ENABLE_WRITE=true npm run env:up
```

`F8_MCP_ENABLE_ADMIN` and `F8_MCP_ENABLE_CODE` open the other two tiers the same way (the full
list of compose variables is in [Running Fallen-8](/fallen-8-core/running/)).

Or run the image standalone against a Fallen-8 on another host, fully credentialed. No registry
publishes it yet, so build it first (the Dockerfile's context is the repo root):

```bash
docker build -f fallen-8-mcp/Dockerfile -t fallen-8-mcp .

docker run --rm -p 8090:8090 \
  -e Fallen8Target__BaseUrl=https://graph.example:8443 \
  -e Fallen8Target__ApiKey=$F8_KEY \
  -e Mcp__Auth__Mode=StaticToken -e Mcp__Auth__StaticToken=$MCP_TOKEN \
  -e Mcp__Security__BindAddress=0.0.0.0 -e Mcp__Security__AllowRemoteAccess=true \
  fallen-8-mcp
```

For OAuth 2.1 instead of a static token, set `Mcp__Auth__Mode=OAuth` +
`Mcp__Auth__Issuer`/`Mcp__Auth__Audience` (see "Authentication"). The server **fails closed**:
a non-loopback bind refuses to start anonymously unless you explicitly opt in
(`Mcp__Security__AcceptAnonymousRemote=true`, which the demo compose sets and a real one must not).
It also refuses to start with `Mode=StaticToken` and no token, or `Mode=OAuth` and no audience,
instead of coming up and rejecting every call.

**Local development over stdio** (no network transport, loopback Fallen-8). The bridge targets
`http://localhost:8080` unless told otherwise, so point it at whatever your Fallen-8 listens on:
a bare `dotnet run` of the API is on `http://localhost:5000`, the compose environment on `:8080`.

```bash
Fallen8Target__BaseUrl=http://localhost:5000 dotnet run --project fallen-8-mcp -- --stdio
```

## Connecting a client

Claude Code, over Streamable HTTP. The default dev server is anonymous:

```bash
claude mcp add --transport http fallen8 http://localhost:8090
```

When you have secured it with a static bearer (above), add the header:

```bash
claude mcp add --transport http fallen8 http://localhost:8090 \
  --header "Authorization: Bearer $F8_MCP_TOKEN"
```

For stdio, point the client at `dotnet run --project fallen-8-mcp -- --stdio` (or the built
binary) and set `Fallen8Target__BaseUrl` in its environment.

## The tools

Eleven consolidated, capability-oriented tools cover the whole surface (not one per REST route),
so a client loads few schemas and each result stays compact. Nearly every tool takes an optional
`namespace` (a Fallen-8 hosts many isolated graphs; it defaults to `default`); `f8_namespace`
names its target directly instead, and `f8_admin`'s `list_savegames`/`load` are Fallen-8-level.

| Tier | Tool | What it does |
|------|------|--------------|
| read | `f8_overview` | Discover a graph: omit `namespace` to list namespaces; set it for counts, index count, available algorithms/index plugins, and embedding/auth state. `detail:"statistics"` adds the full graph-shape snapshot (label/key cardinalities, degree distribution, and the index inventory itself). **Start here.** |
| read | `f8_get` | Fetch a vertex/edge by id with optional neighbourhood (`include`) and property projection (`fields`). Compact by default, scalar values only, vectors omitted. |
| read | `f8_search` | Find elements: `mode` = `index` \| `property` (un-indexed, one named key) \| `properties` (un-indexed contains scan across every property value) \| `fulltext` \| `vector` \| `semantic`. `kind` and `label` restrict the hits (ignored by fulltext). Returns ids (+score); `fields` enriches with properties. Paginated (`limit`/`cursor`). |
| read | `f8_paths` | Find paths between two vertices, unfiltered or by a registered stored query. Knobs: `algorithm` (free-form: `BLS` by default, `DIJKSTRA`, or any registered `Path` plugin the overview reports), `maxDepth` (default 7), `maxResults`. An empty result can also mean an internal traversal limit was hit, so it is not proof no path exists. |
| read | `f8_analytics` | Run a whole-graph algorithm (PageRank, WCC, communities, centrality, triangle-count), or omit `algorithm` to list them. Optional per-run knobs: `vertexLabel`, `edgePropertyId`, `direction`, `maxResults` (default 25), `maxIterations`, and a numeric `parameters` map (e.g. `{"DampingFactor": 0.85}`). |
| read | `f8_plugins` | The per-namespace [plugin registry](/fallen-8-core/plugin-registration/): `list`/`get`/`invoke` (a graph function by name); `delete` needs the write capability; `register_algorithm`/`register_function` (from C# source) need the code capability. An agent can run every registered plugin category: a graph function through `invoke` here, and a registered algorithm by naming it in the `algorithm` knob of `f8_paths`, `f8_subgraph` or `f8_analytics`. |
| read | `f8_documents` | [Unstructured ingestion](/fallen-8-core/unstructured-ingestion/): `search` (fused dense+lexical chunk retrieval; hits are vertex ids), `list`, `get`, `binding` (the index-binding state), `entities` (the deduplicated entity network); `ingest_text`/`delete`/`bind` need the write capability. Binary file upload stays REST-only (base64 through tool calls wastes tokens). |
| write | `f8_mutate` | One transactional mutation: `create_vertex`, `create_edge`, `create_vertices`, `create_edges` (atomic batch creates), `set_property`, `remove_property`, `remove_element`, `set_embedding`. Property values are JSON-native. Success means the transaction applied. The batch creates **return the assigned ids**; the single creates do not (find them with `f8_search`), and `set_property`/`remove_property`/`remove_element` are no-ops for an absent id, so success does not prove the element existed. |
| write | `f8_subgraph` | Define a subgraph from a stored template (or inline filters when the code capability is on). |
| write | `f8_namespace` | Create, rename, or drop a namespace. |
| admin | `f8_admin` | Durability & maintenance: `save`, `list_savegames`, `load` (by save-game id, optionally restoring a single `restoreNamespace` member), `trim`, `tabula_rasa`. `trim`/`tabula_rasa` are fire-and-forget: they report "enqueued", never "applied". |

Tools carry MCP annotations so clients can surface the right confirmation UX. The five purely-read
tools (`f8_overview`, `f8_get`, `f8_search`, `f8_paths`, `f8_analytics`) are `readOnlyHint`;
`f8_namespace` (drop), `f8_admin` (load/trim/tabula_rasa) and also `f8_plugins`/`f8_documents` are
`destructiveHint`, the last two because they host write/code ops behind a per-op gate, so a client
may ask for confirmation even on their read ops. **Annotations are hints: the real enforcement is
server-side tier gating.**

## Tiers and the code capability

Tools are grouped by the same opt-in tiers Fallen-8 uses everywhere:

- **read** (on by default): discovery, fetch, search, paths, analytics, plus the read ops of the
  plugin registry and documents.
- **write** (`Mcp:Tools:EnableWrite`): mutations, subgraph define, namespace lifecycle, and the
  write ops inside the read-tier tools (`delete` on `f8_plugins`; `ingest_text`/`delete`/`bind` on
  `f8_documents`), which appear in those tools' `op` list only once write is on.
- **admin** (`Mcp:Tools:EnableAdmin`): save/load/trim/tabula_rasa.
- **code** (`Mcp:Tools:EnableCode`): does **not** add tools; it *widens* existing ones with C#
  source: inline filter/cost fragments on `f8_paths`/`f8_subgraph`, and the
  `register_algorithm`/`register_function` ops on `f8_plugins` (whole-type plugin source). Off by
  default so the MCP surface stays token-frugal and does not invite arbitrary C# from agents; the
  target Fallen-8 always accepts the equivalent (auth + the plugin gate permitting), so this is
  purely an MCP-side exposure choice.

A disabled tier's tools are absent from the tool list **and** rejected if called anyway.

## Authentication

Three modes (`Mcp:Auth:Mode`), additive:

- **None**: anonymous. Safe only on loopback / a private network. The server **binds loopback
  by default** and **refuses to start** on a network-reachable bind while anonymous (unless you
  explicitly set `Mcp:Security:AcceptAnonymousRemote=true`).
- **StaticToken**: a shared bearer token (`Authorization: Bearer …`). Pragmatic for a network
  you mostly trust; the compose default is anonymous, so opt in with
  `F8_MCP_AUTH_MODE=StaticToken` + `F8_MCP_TOKEN`.
- **OAuth**: a standards-track OAuth 2.1 resource server that validates JWT access tokens from
  your authorization server (audience binding is mandatory), publishes
  [RFC 9728](https://www.rfc-editor.org/rfc/rfc9728) protected-resource metadata at
  `/.well-known/oauth-protected-resource`, and maps scopes to tiers **fail-closed**:
  `f8:write`/`f8:admin`/`f8:code` each need both the scope **and** the server-side tier flag.

The caller's credential is never forwarded to Fallen-8: the MCP server holds one downstream
credential (the Fallen-8 API key) and presents only that. TLS for the MCP endpoint itself is
the deployment's job (terminate at a proxy, or configure Kestrel certificates); the server
warns if auth is on over a non-loopback cleartext bind.

Origin validation (DNS-rebinding protection) and a fixed-window request rate limiter guard the
HTTP transport. A request that sends no `Origin` header passes validation, deliberately, because
MCP clients are not browsers; `/healthz` and the protected-resource metadata path stay open. The
limiter is on by default (600 requests per 60 seconds) and
`Mcp:Security:RateLimit:PermitPerWindow=0` turns it off.

## Token economy

Agents pay for every tool schema and every byte returned, so the surface is deliberately
frugal: few flat schemas, JSON-native inputs (you never write .NET type names), compact
results (scalar property values by default, vector values omitted, long strings truncated),
and hard pagination caps. `f8_overview` answers "what is here and what can I do" in one call.

The bridge infers a property/literal's type from the JSON value, string → `System.String`,
integer → `System.Int32/64`, real → `System.Double`, bool → `System.Boolean`, which covers the
common cases; typed comparisons against `DateTime`/`decimal` properties are not yet inferred
(embeddings are written via `f8_mutate set_embedding`, not as a typed property). Rich results
ride in the tool result's `structuredContent`; clients must consume that field (the `content`
block is only a one-line summary).

### When a call fails

Every failure comes back the same way: an `isError` result whose single text line is
`{status} {title}: {detail}`, normalized from whatever shape Fallen-8 returned. A transient
failure the agent may retry with backoff (429/503-style) carries a `(retryable)` suffix. An
unknown tool name, or one whose tier is off, answers `404 Unknown or disabled tool` with a hint
to enable the tier (and, under OAuth, hold its scope). Anything unexpected is flattened to a
generic `500 Internal error` so nothing sensitive leaks, and the downstream Fallen-8 API key is
never echoed to the caller.

## Security guidance

Run agent-facing servers **read-only unless you have a concrete need** for writes. An agent can
be talked (via prompt injection) into destructive calls, so keep write/admin/code tiers off by
default, rely on the `destructiveHint` confirmation UX in your client, and treat the `code`
capability (which runs C# fragments as the Fallen-8 process) as a trusted, deliberate choice.

## Configuration reference

| Setting | Meaning |
|---------|---------|
| `Mcp:Transport` | `http` (default) or `stdio` |
| `Mcp:Port` | HTTP port (default 8090) |
| `Mcp:Security:BindAddress` | Kestrel bind (loopback by default; `0.0.0.0` in a container) |
| `Mcp:Security:AllowRemoteAccess` / `AcceptAnonymousRemote` | accept remote callers / allow anonymous remote (fail-closed override) |
| `Mcp:Security:Origins` | allowed cross-origins (loopback allowed by default) |
| `Mcp:Security:RateLimit:PermitPerWindow` / `WindowSeconds` | fixed-window request throttle (default 600 requests per 60 seconds; `0` disables the limiter) |
| `Mcp:Tools:EnableWrite` / `EnableAdmin` / `EnableCode` | tier / capability opt-ins (all off by default) |
| `Mcp:Auth:Mode` | `None` \| `StaticToken` \| `OAuth` |
| `Mcp:Auth:StaticToken` | the shared bearer secret used when `Mode=StaticToken` (env / user-secrets only, never checked in) |
| `Mcp:Auth:Issuer` / `Audience` | the OAuth authorization server's issuer + this server's resource identifier (the token's `aud`; mandatory under OAuth) |
| `Mcp:Auth:SigningKey` | lab-only: validate tokens against this symmetric HMAC key instead of discovering the issuer's JWKS |
| `Mcp:Observability:Otlp:Endpoint`, `Mcp:Identity:*` | OTLP push + fleet identity; see [Observability](/fallen-8-core/observability/) |
| `Fallen8Target:BaseUrl` | the Fallen-8 REST base URL this server bridges to (default `http://localhost:8080`) |
| `Fallen8Target:ApiKey` / `ApiKeyHeader` | the server's single downstream credential (header default `X-Api-Key`) |
| `Fallen8Target:TlsInsecure` | lab-only: skip downstream TLS validation (loudly logged) |

## For contributors: engine → REST → MCP

The MCP surface must not fall behind the database. When a capability grows in the engine and
gains a REST endpoint, it must also be surfaced to agents as an MCP tool, or be a conscious,
reasoned deferral. This is enforced by a test (`McpRestCoverageTest`): a new REST endpoint that
is neither bridged nor deferred fails the build.
