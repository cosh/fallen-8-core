# API Security Boundary — operator guide

The hosted Fallen-8 API establishes an **authentication trust boundary** and keeps the
remote-code-execution surface **off by default**. Configure it under `Fallen8:Security` in
`appsettings.json`, environment variables, or user-secrets.

> **Honest limit (read this).** In-process Roslyn compilation and plugin loading **cannot be
> sandboxed** — a compiled filter or a loaded plugin runs with the server process's full authority.
> This feature is a *trust boundary* (who may reach the code endpoints) plus an operator *kill
> switch*, **not a sandbox**. Anyone permitted to reach `POST /path`, `PUT /subgraph`, or
> `PUT /plugin` with the capability enabled is **trusted as the process**. Running genuinely
> untrusted submitted code would require out-of-process / WASM isolation (a separate, larger design).

## Configuration keys (`Fallen8:Security`)

| Key | Default | Meaning |
|-----|---------|---------|
| `ApiKey` | `null` | The secret required in the API-key header. **Supply via user-secrets/environment, never checked in.** When null the server runs **unauthenticated** (logs a warning) — only acceptable behind loopback. |
| `ApiKeyHeader` | `X-Api-Key` | Header carrying the key. |
| `EnableDynamicCodeExecution` | `false` | Master switch for the Roslyn compile endpoints (`POST /path`, `PUT /subgraph`). Off ⇒ **403**. |
| `EnableDynamicPluginLoading` | `false` | Master switch for `PUT /plugin`. Off ⇒ **403**, nothing written. |
| `PluginDirectory` | `<base>/plugins` | Isolated directory uploaded DLLs are written to and discovered from — never the app's binary directory. |
| `AllowedCorsOrigins` | `[]` | CORS allow-list. Empty ⇒ deny all cross-origin. No wildcard-with-credentials. |
| `SensitiveRateLimitPermitPerWindow` | `30` | Requests allowed per window on the code/plugin endpoints (429 on breach). |
| `RateLimitWindowSeconds` | `10` | Fixed-window length for that limiter. |
| `AllowRemoteAccess` | `false` | Opt-in for exposing the server off-box. **S6 note:** this flag + a startup warning ship; the app does not yet *force* a loopback bind (that would override your Kestrel/port config). Ensure your bind address is loopback unless you have set an API key and intend remote access. |

## Behaviour

- **Authentication.** With `ApiKey` set, every endpoint requires the key except those marked
  `[AllowAnonymous]` (`/status`, `/vertex/count`, `/edge/count`). Anonymous ⇒ **401**.
- **RCE gates.** `POST /path`, `PUT /subgraph`, `PUT /plugin` require **both** an authenticated caller
  **and** the matching capability flag. Anonymous ⇒ 401; authenticated but disabled ⇒ 403.
- **Perimeter.** Default-deny CORS; a fixed-window rate limiter on the sensitive endpoints (429);
  request-size limits (1 MiB code, 64 MiB plugin) ⇒ 413 on oversize.

## Enabling code execution (trusted single-tenant only)

```jsonc
"Fallen8": {
  "Security": {
    "ApiKey": "<from user-secrets / env>",
    "EnableDynamicCodeExecution": true
  }
}
```

Only do this where every caller who can present the key is trusted as the process.

## Related

- Execution-time CPU/memory/timeout limits on a compiled filter: `features/dynamic-code-resource-limits/`.
- The 401/403 body shape aligns later with `features/api-error-contract/`.
- Satisfies `features/subgraph-quotas/`'s "authenticated the same as the rest of the API" premise.
