# API Security Boundary — operator guide

The hosted Fallen-8 API establishes an **authentication trust boundary**. Dynamic code
execution (the Roslyn compile endpoints) is **always on** — running agent-emitted C# is
Fallen-8's core "queries are C#" model, so there is no switch for it. Plugin DLL loading is a
separate opt-in, off by default. Configure everything under `Fallen8:Security` in
`appsettings.json`, environment variables, or user-secrets.

> **Update (dynamic code is always on).** An earlier revision of this feature gated the compile
> endpoints behind `EnableDynamicCodeExecution` (default off). That flag has been **removed**:
> dynamic code execution is unconditional. The only gate on `POST /path`, `PUT /subgraph`,
> `POST /storedquery`, and `POST /delegates/validate` is authentication (the API key when one is
> configured). Plugin loading keeps its own kill switch (`EnableDynamicPluginLoading`).

> **Honest limit (read this).** In-process Roslyn compilation and plugin loading **cannot be
> sandboxed** — a compiled filter or a loaded plugin runs with the server process's full authority.
> Authentication is the *trust boundary* (who may reach the code endpoints), **not a sandbox**.
> Anyone permitted to reach `POST /path`, `PUT /subgraph`, or `PUT /plugin` is **trusted as the
> process**. Therefore an unauthenticated instance grants arbitrary in-process code execution to
> anyone who can reach it — **set an API key before exposing the service off-box.** Running
> genuinely untrusted submitted code would require out-of-process / WASM isolation (a separate,
> larger design).

## Configuration keys (`Fallen8:Security`)

| Key | Default | Meaning |
|-----|---------|---------|
| `ApiKey` | `null` | The secret required in the API-key header. **Supply via user-secrets/environment, never checked in.** When null the server runs **unauthenticated** (logs a warning) — only acceptable behind loopback. |
| `ApiKeyHeader` | `X-Api-Key` | Header carrying the key. |
| `EnableDynamicPluginLoading` | `false` | Master switch for `PUT /plugin`. Off ⇒ **403**, nothing written. (There is no equivalent switch for the Roslyn compile endpoints — those are always on.) |
| `PluginDirectory` | `<base>/plugins` | Isolated directory uploaded DLLs are written to and discovered from — never the app's binary directory. |
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
- **Plugin loading.** `PUT /plugin` still requires **both** an authenticated caller **and**
  `EnableDynamicPluginLoading=true`. Anonymous ⇒ 401; authenticated but disabled ⇒ 403.
- **Perimeter.** Default-deny CORS; a fixed-window rate limiter on the sensitive endpoints (429);
  request-size limits (1 MiB code, 64 MiB plugin) ⇒ 413 on oversize.

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
