# API Security Boundary — operator guide

The hosted Fallen-8 API establishes an **authentication trust boundary**. Dynamic code
execution (the Roslyn compile endpoints) is **always on** — running agent-emitted C# is
Fallen-8's core "queries are C#" model, so there is no switch for it. Runtime plugin
*registration* (source-based) is a separate opt-in, off by default. Configure everything under
`Fallen8:Security` in `appsettings.json`, environment variables, or user-secrets.

> **Update (plugin DLL upload removed).** An earlier revision exposed `PUT /plugin`, which
> uploaded and loaded an external DLL in-process. That endpoint has been **removed** (feature
> plugin-registration); runtime plugins are now authored as C# **source** and registered under
> `POST /plugins/*` (compiled, contract-validated, namespace-scoped — see
> [docs/plugin-registration.md](../../../docs/plugin-registration.md)). The
> `EnableDynamicPluginLoading` switch survives, **repurposed** to gate that source-registration
> surface; `PluginDirectory` is gone.

> **Update (dynamic code is always on).** An earlier revision of this feature gated the compile
> endpoints behind `EnableDynamicCodeExecution` (default off). That flag has been **removed**:
> dynamic code execution is unconditional. The only gate on `POST /path`, `PUT /subgraph`,
> `POST /storedquery`, and `POST /delegates/validate` is authentication (the API key when one is
> configured). Plugin loading keeps its own kill switch (`EnableDynamicPluginLoading`).

> **Honest limit (read this).** In-process Roslyn compilation and plugin loading **cannot be
> sandboxed** — a compiled filter or a loaded plugin runs with the server process's full authority.
> Authentication is the *trust boundary* (who may reach the code endpoints), **not a sandbox**.
> Anyone permitted to reach `POST /path`, `PUT /subgraph`, or `POST /plugins/*` is **trusted as the
> process**. Therefore an unauthenticated instance grants arbitrary in-process code execution to
> anyone who can reach it — **set an API key before exposing the service off-box.** Running
> genuinely untrusted submitted code would require out-of-process / WASM isolation (a separate,
> larger design).

## Configuration keys (`Fallen8:Security`)

| Key | Default | Meaning |
|-----|---------|---------|
| `ApiKey` | `null` | The secret required in the API-key header. **Supply via user-secrets/environment, never checked in.** When null the server runs **unauthenticated** (logs a warning) — only acceptable behind loopback. |
| `ApiKeyHeader` | `X-Api-Key` | Header carrying the key. |
| `EnableDynamicPluginLoading` | `true` | GLOBAL DEFAULT for source plugin **registration** (`POST /plugins/*`). On by default (consistent with the always-on Roslyn compile endpoints); a namespace can override it via `PATCH /ns/{name}` `pluginRegistration` (enabled/disabled/inherit). When effectively off ⇒ **403**, nothing compiled. Invoking/listing/deleting a registered plugin is never gated by it. |
| `AllowedCorsOrigins` | `[]` | CORS allow-list. Empty ⇒ deny all cross-origin. No wildcard-with-credentials. |
| `SensitiveRateLimitPermitPerWindow` | `30` | Requests allowed per window on the code/plugin endpoints (429 on breach). |
| `RateLimitWindowSeconds` | `10` | Fixed-window length for that limiter. |
| `AllowRemoteAccess` | `false` | Opt-in for exposing the server off-box. **S6 note:** this flag + a startup warning ship; the app does not yet *force* a loopback bind (that would override your Kestrel/port config). Ensure your bind address is loopback unless you have set an API key and intend remote access. |

## Behaviour

- **Authentication.** With `ApiKey` set, every endpoint requires the key except those marked
  `[AllowAnonymous]` (`/status`, `/vertex/count`, `/edge/count`). Anonymous ⇒ **401**.
- **Code endpoints.** `POST /path`, `PUT /subgraph`, `POST /storedquery`, `POST /delegates/validate`
  compile and run C# unconditionally; they carry only the standard authentication (anonymous ⇒ 401
  when a key is set). There is no capability flag and no 403 for a "code disabled" reason.
- **Plugin registration.** `POST /plugins/algorithm`, `POST /plugins/function`, and the
  `/plugins/{category}/validate` compile-checks require **both** an authenticated caller **and**
  `EnableDynamicPluginLoading=true`. Anonymous ⇒ 401; authenticated but disabled ⇒ 403. Invoking a
  registered plugin (via `/path`/`/analytics`/`/subgraph` or `POST /plugins/function/{name}/invoke`),
  listing, and deletion carry only the standard authentication.
- **Perimeter.** Default-deny CORS; a fixed-window rate limiter on the sensitive endpoints (429);
  a 1 MiB request-size limit on the code + plugin-registration endpoints ⇒ 413 on oversize.

## Securing an exposed instance

```jsonc
"Fallen8": {
  "Security": {
    "ApiKey": "<from user-secrets / env>"
  }
}
```

The code endpoints run in-process with full trust, so set the key (or front the service with an
authenticating proxy) before it is reachable off-box. Only run unauthenticated behind loopback or
a fully trusted network.

## Related

- Execution-time CPU/memory/timeout limits on a compiled filter: `features/dynamic-code-resource-limits/`.
- The 401/403 body shape aligns later with `features/api-error-contract/`.
- Satisfies `features/subgraph-quotas/`'s "authenticated the same as the rest of the API" premise.
