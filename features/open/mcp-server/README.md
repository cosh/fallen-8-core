# Fallen-8 MCP Server — feature README (living doc)

> **Usage docs live in [docs/mcp-server.md](../../../docs/mcp-server.md)** (how to run, connect
> a client, the tool table, tiers/auth, config). This README is the contributor living doc:
> architecture, pinned versions, layout, and the test/gate map. The historical record is
> [spec.md](./spec.md) + [plan.md](./plan.md) (not rewritten).

## What it is

`fallen-8-mcp` is a **separate deployable** that bridges the Model Context Protocol to a
reachable Fallen-8 over its **REST API** — never an engine embedding (no reference to
`fallen-8-core` or the apiApp). It gives AI agents, the expected primary users of Fallen-8, a
small, token-frugal, namespace-aware tool surface.

## Architecture

- **Tool authoring:** the low-level MCP `ListTools`/`CallTool` handlers with **hand-authored,
  flat, enum-discriminated** JSON schemas (no `oneOf`/`anyOf`/`$ref` — a client
  tool-selection hazard). This is what lets the advertised set and each schema vary by the
  enabled tiers and, under OAuth, by the caller's scopes. Home: `Tools/ToolCatalog.cs` +
  `Tools/*Tool.cs` + `Tools/SchemaBuilder.cs`.
- **Bridge:** `Bridge/Fallen8RestClient.cs` owns URL-safe route construction
  (`Bridge/UrlSafety.cs`), namespace scoping (`/ns/{ns}/…` vs the bare default), the
  three-rule error mapping (problem+json / plain-string / `204`-soft-not-found →
  `BridgeError`), JSON-native value mapping (`Bridge/ValueMapping.cs` — agents never emit .NET
  type names), and compact/byte-bounded result shaping (`Bridge/ElementProjection.cs`).
- **Transports:** stdio (console host) and Streamable HTTP (Kestrel, loopback-bound by
  default). `Hosting/McpHost.cs` (composition), `Program.cs` (both transports),
  `Hosting/TransportSecurity.cs` (origin/bearer/posture), `Hosting/McpOAuth.cs` (Phase C).
- **Protocol revision pinned:** `2025-06-18` (so `structuredContent` is object-wrapped). Later
  MCP revisions (RC stateless core, Apps/Tasks) are a revisit, not a dependency.

## Pinned versions

- `ModelContextProtocol.AspNetCore` **1.4.1** (brings `ModelContextProtocol` + `.Core`).
- `Microsoft.AspNetCore.Authentication.JwtBearer` **10.0.9** (OAuth 2.1 resource server).
- `net10.0`, root namespace `NoSQL.GraphDB.Mcp`.

## Engine → REST → MCP (the propagation rule)

A capability that grows in the engine and reaches the REST surface **must** also be surfaced to
agents as an MCP tool, or be a conscious, reasoned deferral. This is enforced, not aspirational:
`fallen-8-unittest/McpRestCoverageTest.cs` fails the build if a REST operation in the OpenAPI
snapshot is neither in `McpBridgedEndpoints` nor matched by a deferral rule (with a reason). See
also `CLAUDE.md` (Architecture notes + Quality gates).

## Tests / gates

- `McpToolSurfaceTest` — schema-shape proof (flat, no composition), tier gating, annotations.
- `McpUrlSafetyTest` — namespace validation + injection encoding.
- `McpBridgeTest` — three-rule error mapping + a walking-skeleton round-trip vs a hosted apiApp.
- `McpReadToolsTest` / `McpWriteToolsTest` — read + write/admin round-trips and the enforcement
  matrix (incl. namespace-scoped isolation and per-op write honesty).
- `McpTransportTest` — origin validation, static bearer, rate limiter, fail-closed posture.
- `McpOAuthTest` — JWT validation, RFC 9728 metadata + challenge, fail-closed scope→tier, no
  token passthrough.
- `McpContractTest` — bridged routes/methods pinned against the OpenAPI snapshot.
- `McpRestCoverageTest` — the engine→REST→MCP governance gate (above).
- `fallen-8-mcp` is registered in `CodeQualityTest` (MIT headers, no `Console.Write*`, exact
  package versions).

## Build & run

```bash
dotnet build fallen-8-mcp/fallen-8-mcp.csproj
dotnet run   --project fallen-8-mcp -- --stdio     # local stdio
F8_MCP=1 F8_MCP_TOKEN=… npm run env:up             # compose profile (see docs/mcp-server.md)
```
