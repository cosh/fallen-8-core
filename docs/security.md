# Security

Fallen-8's hosted API has two independent security controls that compose: an **API key** gates *access* to the whole service (all or nothing), and the **dynamic-code switch** gates *compilation of arbitrary C#* (off by default). They are orthogonal — one decides who may call, the other decides whether anyone may run submitted code. The key defaults to unset (open, for localhost or a trusted network); the switch defaults to off (no submitted code runs until an operator opts in). This page is the one home for that posture; other docs reference it.

| Control | Config key | Gates | Default |
|---|---|---|---|
| API key | `Fallen8:Security:ApiKey` | Access to every endpoint | unset → open |
| Dynamic code | `Fallen8:Security:EnableDynamicCodeExecution` | Compiling submitted C# fragments | `false` → code refused |

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

## Dynamic code execution switch

`EnableDynamicCodeExecution` is the kill switch for the one endpoint class that compiles arbitrary C# — the Roslyn compilation of filter/cost fragments described in [delegates](delegates.md). Off by default; independent of the API key.

| Key | Env (compose) | Default |
|---|---|---|
| `Fallen8:Security:EnableDynamicCodeExecution` | `Fallen8__Security__EnableDynamicCodeExecution` (`F8_ENABLE_DYNAMIC_CODE`) | `false` |

The gate is **request-shape-aware**: only a request that actually *introduces* inline C# is gated. Precisely what needs the switch ON, and what does not:

| Gated — needs the switch ON | Ungated — works with the switch OFF |
|---|---|
| Inline path fragments (`POST /path/{from}/to/{to}` with a `filter`/`cost` fragment) | Stored-query **invocation** by name (`storedQuery` on `/path`, `/subgraph`) |
| Inline subgraph fragments (`PUT /subgraph` with `vertexFilter`/`edgeFilter`/`patterns`) | Stored-query list / get / delete |
| Stored-query **registration** (`POST /storedquery`) | Filterless path search (`{}` body) |
| `POST /delegates/validate` | The `semantic` block (query-vector filters/costs are declarative) |
| Plugin DLL upload — a *separate* switch, `EnableDynamicPluginLoading` ([plugins](plugins.md)) | Analytics, change feed, bulk import/export, all reads/scans, mutations |

With the switch **off**, an authenticated caller posting a code-introducing request gets **403 Forbidden** before any compilation runs. A request that mixes a stored-query reference with an inline fragment is treated as introducing code, so it is also 403. Because these two mechanisms stack, **authentication is evaluated first**: an anonymous code request against a key-protected server is 401, never a 403 that would leak the switch state.

Three neighbours lean on the ungated column and are documented there: stored-query registration needs the switch while invocation by name does not ([stored queries](stored-queries.md)); the `semantic` block is code-free and works with the switch off ([semantic traversal](semantic-traversal.md)); the change feed is declarative and works with the switch off ([change feed](change-feed.md)).

## How they compose

The switch gates code either way; the key gates access either way. The four combinations:

| | Code switch OFF (default) | Code switch ON |
|---|---|---|
| **Key unset (open)** | **Default.** Anyone who can reach the service uses reads, mutations, stored-query invocation, analytics, change feed, and bulk; inline fragments, registration, and `/delegates/validate` return 403. | Anyone who can reach the service can compile and run arbitrary in-process C#. Localhost dev only. |
| **Key set** | Every request needs the key; code-introducing requests still 403. The recommended exposed posture. | Every request needs the key; any key-holder can compile and run arbitrary in-process C#. Trusted single-tenant only. |

**Honest limit.** A permitted fragment — inline or stored — runs in-process with the server's full authority. This is *provenance control* (who may introduce code, and whether code is accepted at all), **not a sandbox**. Anyone who can present the key while the switch is on has full code execution as the server process; a stored query provisioned during a switch-on window keeps that authority when invoked later. Running genuinely untrusted code would need out-of-process or WASM isolation, which Fallen-8 does not provide.

## Deployment postures

| Posture | API key | Code switch | TLS | Shape |
|---|---|---|---|---|
| Localhost dev | unset (open) | on | none | `dotnet run`; iterate on fragments freely on your own machine. |
| Trusted network | set | off (after provisioning) | none / optional | Register a vetted set of stored queries with the switch on, then run day-to-day with it off — only the approved set executes. |
| Exposed | set | off | terminate upstream | Only vetted stored queries run; every request is authenticated. |

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

- [Delegates](delegates.md) — the Roslyn-compiled C# fragments the switch gates, and `/delegates/validate`
- [Stored queries](stored-queries.md) — registration needs the switch; invocation by name does not
- [Semantic traversal](semantic-traversal.md) — the code-free `semantic` block that works with the switch off
- [Change feed](change-feed.md) — declarative, works with the switch off
- [Plugins](plugins.md) — the separate `EnableDynamicPluginLoading` switch
- [Observability](observability.md) — metrics/health endpoints and whether `/metrics` requires the key
- [Running Fallen-8](running.md) — setting `F8_API_KEY` and `F8_ENABLE_DYNAMIC_CODE` in compose
- [Studio](studio.md) — registering an instance with its key; the fragment editor needs the switch
- [REST API](rest-api.md) — the auth header on the endpoint surface
