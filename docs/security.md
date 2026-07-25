# Security

Fallen-8's hosted API has one access control: an **API key** that gates *access* to the whole service (all or nothing). Set it and every request needs it; leave it unset and the whole service is open. There is no dynamic-code switch — compiling and running submitted C# fragments is Fallen-8's core "queries are C#" model and is **always available** (auth permitting). Runtime **plugin registration** compiles submitted C# too ([plugin registration](plugin-registration.md)); it is **on by default** and can be turned off globally or **per namespace**. This page is the one home for that posture; other docs reference it.

| Control | Config key | Gates | Default |
|---|---|---|---|
| API key | `Fallen8:Security:ApiKey` | Access to every endpoint | unset → open |
| Plugin registration | `Fallen8:Security:EnableDynamicPluginLoading` (global default) + per-namespace `pluginRegistration` override | Registering plugins from source (`POST /plugins/*`, [plugin registration](plugin-registration.md)) | `true` → allowed; override per namespace via `PATCH /ns/{name}` |

## API key authentication

Set a key and the **entire** service requires it — reads, mutations, and code endpoints alike. Leave it unset and the whole service is open (the API-key scheme authenticates nobody); the server logs a prominent `UNAUTHENTICATED` warning at startup. There is no per-endpoint or per-role model: it is all or nothing.

| Key | Env (compose) | Default | Meaning |
|---|---|---|---|
| `Fallen8:Security:ApiKey` | `Fallen8__Security__ApiKey` (`F8_API_KEY`) | `null` | The secret. Supply from environment or user-secrets — never checked in. |
| `Fallen8:Security:ApiKeyHeader` | `Fallen8__Security__ApiKeyHeader` | `X-Api-Key` | Request header carrying the key. |

The client sends the key in the `X-Api-Key` header (or, as a fallback, `Authorization: Bearer <key>` — the same key). The comparison is constant-time and the key is never logged. Behaviour when a key **is** configured:

| Request | Result |
|---|---|
| Correct key in the header | Authenticated; proceeds |
| Missing key | **401 Unauthorized** |
| Wrong key | **401 Unauthorized** |

**Anonymous exemptions** (reachable without a key even when one is set): `GET /status`, `GET /vertex/count`, `GET /edge/count`, the health probes `GET /healthz` and `GET /readyz`, the Studio SPA shell, and `GET /metrics` by default (see [observability](observability.md)). Everything else requires the key. `GET /status` is also the connection probe: it reports `apiKeyRequired` (server config) and `authenticated` (this request) so a client can tell "reachable" from "authorized".

```bash
# Probe first — is a key required?  Then send it on real calls.
curl http://localhost:8080/status
curl -H "X-Api-Key: <your-key>" http://localhost:8080/storedquery
```

```powershell
Invoke-RestMethod -Uri http://localhost:8080/status
Invoke-RestMethod -Uri http://localhost:8080/storedquery -Headers @{ "X-Api-Key" = "<your-key>" }
```

## Dynamic code execution (always on)

The path and subgraph endpoints compile inline C# filter/cost fragments with Roslyn and run them in-process — see [delegates](delegates.md). This is Fallen-8's query model, so **there is no switch to disable it**. The only gate is authentication: with a key configured, a code-introducing request needs the key like any other request (401 without it); with no key, anyone who can reach the service can run arbitrary in-process C#.

| Endpoint class | Needs a credential (when a key is configured) | Notes |
|---|---|---|
| Inline path fragments (`POST /path/...` with `filter`/`cost`) | yes | Compiled per request (cached by fragment). |
| Inline subgraph fragments (`PUT /subgraph` with `vertexFilter`/`edgeFilter`/`patterns`) | yes | |
| Stored-query **registration** (`POST /storedquery`) | yes | Compiled once at registration ([stored queries](stored-queries.md)). |
| `POST /delegates/validate` | yes | Compile-checks a fragment without running it. |
| Stored-query **invocation** by name, list / get / delete | as any request | No new code is introduced; the pre-compiled artifact runs. |
| Filterless path search (`{}` body), the `semantic` block, analytics, change feed, reads/scans, mutations | as any request | Compile no C# at all. |
| Plugin **registration** (`POST /plugins/algorithm`, `/plugins/function`, `/plugins/{category}/validate`) | yes **and** plugin registration enabled | On by default; disable globally (`EnableDynamicPluginLoading=false`) or per namespace (`PATCH /ns/{name}` `pluginRegistration`) — see [plugin registration](plugin-registration.md). Invoking/listing/deleting a registered plugin is never gated by this. |

**Honest limit.** A compiled fragment — inline or stored — runs in-process with the server's full authority. Authentication is *access control* (who may reach the code endpoints at all); it is **not a sandbox**. Anyone who can present the key has full code execution as the server process. Running genuinely untrusted code would need out-of-process or WASM isolation, which Fallen-8 does not provide. **Therefore: never expose an unauthenticated instance off-box — set an API key (or front the service with an authenticating proxy) before it is reachable beyond localhost or a trusted network.**

## How the key composes

| Posture | Who can reach it | Who can run in-process C# |
|---|---|---|
| **Key unset (open)** — default | anyone who can reach the service | anyone who can reach the service. **Localhost / trusted network only.** |
| **Key set** | only key-holders (reads exempted above) | only key-holders. The recommended exposed posture. |

## Deployment postures

| Posture | API key | Plugin loading | TLS | Shape |
|---|---|---|---|---|
| Localhost dev | unset (open) | off | none | `dotnet run`; iterate on fragments freely on your own machine. |
| Trusted network | set | off | none / optional | Every request authenticated; code runs in-process — keep the network trusted. |
| Exposed | set | off | terminate upstream | Every request authenticated behind a TLS-terminating reverse proxy. |

**TLS.** The app serves plain HTTP (the container listens on `8080`, the dev profile on `5000`); it does **not** terminate TLS and ships no certificate, so `UseHttpsRedirection` is a no-op without an HTTPS port. When exposing the service, put a TLS-terminating reverse proxy in front of it.

**Bind address.** `Fallen8:Security:AllowRemoteAccess` exists but is currently inert — `Program.cs` does not read it, so it forces nothing. The actual bind address is whatever `ASPNETCORE_URLS` / Kestrel / the launch profile sets (the container binds all interfaces on `8080`). Do not rely on a "loopback-by-default" guarantee: set the API key before the service is reachable off-box.

## Other perimeter controls

Additional knobs under `Fallen8:Security`, applied to the sensitive (code/plugin) endpoints:

| Key | Default | Effect |
|---|---|---|
| `AllowedCorsOrigins` | `[]` | CORS allow-list; empty denies all cross-origin. No wildcard-with-credentials. |
| `SensitiveRateLimitPermitPerWindow` / `RateLimitWindowSeconds` | `30` / `10` | Fixed-window rate limit on code/plugin endpoints; breach → **429**. |
| (request body) | 1 MiB | Fragment/registration bodies over the limit → **413**. |

## See also

- [Delegates](delegates.md) — the Roslyn-compiled C# fragments (always available), and `/delegates/validate`
- [Stored queries](stored-queries.md) — named, pre-compiled queries: registration needs a credential, invocation reuses the artifact
- [Semantic traversal](semantic-traversal.md) — the code-free `semantic` block
- [Change feed](change-feed.md) — declarative, compiles no code
- [Plugin registration](plugin-registration.md) — the `EnableDynamicPluginLoading` global default + the per-namespace override
- [Observability](observability.md) — metrics/health endpoints and whether `/metrics` requires the key
- [Running Fallen-8](running.md) — setting `F8_API_KEY` in compose
- [Studio](studio.md) — registering an instance with its key
- [REST API](rest-api.md) — the auth header on the endpoint surface
```
