# API Security Boundary — Specification

> **Status:** Planned (P0 security) — from the 2026-07 principal-architect & performance review.
> Establish an authentication trust boundary for the hosted API and gate the anonymous
> remote-code-execution surface (Roslyn filter compile + arbitrary plugin DLL load).

## 1. Problem / current state

The hosted API (`fallen-8-core-apiApp`) is **fully anonymous**, and two of its endpoints compile
and execute attacker-supplied C# in-process while a third loads an attacker-supplied .NET assembly
into the app's own binary directory. Anyone who can reach the socket has full remote code execution
as the server process. Verified against the current tree:

| # | Exposure | Location | Effect |
|---|----------|----------|--------|
| S1 | `app.UseAuthorization()` is a **no-op gate**: no authentication scheme is registered, `app.UseAuthentication()` is never called, and there is no `[Authorize]` (nor any `FallbackPolicy`) anywhere in the app. So every endpoint is anonymous — mutations, admin, and the code endpoints alike. | `Program.cs:111` (missing `UseAuthentication` before it; grep for `Authorize`/`AddAuthentication`/`AddAuthorization` in `fallen-8-core-apiApp` = 0 hits) | No caller is ever authenticated; authorization has nothing to enforce against. |
| S2 | `POST /path/{from}/to/{to}` compiles a user-supplied C# filter/cost fragment with Roslyn and runs it **in-process, full trust**. The generated assembly references `System.Private.CoreLib` (`typeof(object).Assembly`) plus `mscorlib`/`System`/`System.Runtime`, so a fragment such as `return (v) => { System.Environment.Exit(0); return true; };` kills the process, and reflection from the fragment is unrestricted RCE. | `GraphController.cs:990` → `CodeGenerationHelper.GeneratePathTraverser` (`GraphController.cs:1020`); references built in `CodeGenerationHelper.cs:208-247` (esp. `214`, `241-244`) | Any anonymous caller runs arbitrary code as the server. |
| S3 | `PUT /subgraph` compiles user-supplied filter fragments the same way (same `GetGlobalReferences`, same full trust). | `SubGraphController.cs:97` → `CodeGenerationHelper.TryGenerateSubGraphDefinition` (`SubGraphController.cs:137`) | Same RCE surface as S2. |
| S4 | `PUT /plugin` writes uploaded bytes into `AppContext.BaseDirectory` (the app's **own binary directory**) and registers them; `PluginFactory.Assimilate` invalidates the discovery cache so the assembly is `Assembly.Load`ed and instantiated on the next plugin scan. | `AdminController.cs:320` → `UploadPlugin` (`AdminController.cs:324`) → `PluginFactory.Assimilate` (`Plugin/PluginFactory.cs:175`, default path at `:177`) | Anonymous, persistent RCE: drop a DLL, it executes on next index/service/save/load/path op. |
| S5 | No CORS policy, no rate limiting, no request-body-size limit anywhere (grep for `AddCors`/`UseCors`/`AddRateLimiter`/`MaxRequestBodySize`/`RequestSizeLimit` = 0 hits). The code/DLL endpoints accept unbounded bodies. | `Program.cs` (absent); `appsettings.json:9` `AllowedHosts: "*"` | Cross-origin abuse from a browser, trivial resource-exhaustion DoS, oversized uploads. |
| S6 | The dev launch profile binds loopback (`http://localhost:5000`), but that is a **launch-settings** default only — there is no code/config default in `Program.cs`, so a deployment that sets `ASPNETCORE_URLS`/Kestrel to a public interface exposes the anonymous surface with nothing else changed. | `Properties/launchSettings.json:18-20`; no binding default in `Program.cs` | The safe default is not enforced by the app; it is incidental to the dev profile. |

The `subgraph-quotas` feature's spec already **assumes** subgraph creation "is authenticated the same
as the rest of the API" (`features/subgraph-quotas/spec.md:18`) — a premise that is currently false,
because the rest of the API is not authenticated at all. This feature makes that premise true.

**Honest scope note (must stay in the docs).** In-process Roslyn compilation **cannot be turned
into a sandbox.** Code Access Security is gone from modern .NET; a delegate compiled into and run in
the server's `AppDomain`/process has the process's full authority regardless of which references the
compilation was given (it can reflect its way to anything the process can reach). Therefore the
realistic goal of *this* feature is a **trust boundary**, not a sandbox: decide *who* may reach the
code endpoints and let the operator turn the whole RCE surface off. Anyone permitted to post a filter
is, by construction, **trusted as the process**. Truly running *untrusted* submitted code would
require out-of-process or WASM isolation (a separate, much larger design — see §2 non-goals and the
`dynamic-code-resource-limits` cross-link), and is explicitly not attempted here.

## 2. Goals / non-goals

**Goals**

- A real **authentication scheme** (API key or JWT bearer) plus a default authorization
  **`FallbackPolicy`** that requires an authenticated principal, and the missing
  `app.UseAuthentication()` placed **before** `app.UseAuthorization()`. Unauthenticated requests to
  mutating/admin/code endpoints get **401**.
- An explicit **operator opt-in** for the RCE surface: `Fallen8:EnableDynamicCodeExecution` (gates
  `POST /path` + `PUT /subgraph` compilation) and `Fallen8:EnableDynamicPluginLoading` (gates
  `PUT /plugin`), **both default `false`**. With a flag off, the endpoint returns a clear
  **403** (feature administratively disabled) rather than silently compiling/loading.
- A named **CORS** policy that defaults to deny, a **rate limiter** (`AddRateLimiter`/
  `UseRateLimiter`) with a stricter partition on the code/plugin endpoints, and **request-body-size
  limits** (Kestrel default + a tight `[RequestSizeLimit]` on the code/DLL endpoints).
- A **loopback-by-default binding** enforced by the app unless the operator explicitly opts into a
  public bind via configuration.
- Uploaded plugins land in a **configurable, isolated plugin directory**, never in
  `AppContext.BaseDirectory`.
- Read endpoints stay usable for legitimate clients (specific ones may opt into `[AllowAnonymous]`
  if the operator wants an open read surface).

**Non-goals**

- **True sandboxing of in-process submitted code.** Out of scope by construction (see §1 honesty
  note). Out-of-process / WASM isolation is a separate, larger feature.
- **CPU/memory/time limits** on a compiled filter's *execution* (a trusted caller can still write a
  slow or allocating filter) — that is `dynamic-code-resource-limits`, complementary to this trust
  boundary. The existing `MaxVariableEdgeLength` guard (`CodeGenerationHelper.cs:259`) is a shape
  guard of that kind, **not** an auth gate; it is unrelated to and unaffected by this work.
- **User management / RBAC / per-caller quotas.** One authenticated identity is the unit here;
  per-caller quotas remain `subgraph-quotas`' concern.
- **A problem+json error envelope** for the new 401/403 responses — that aligns later with
  `api-error-contract`; here the codes and a clear message are what matter.
- **Changing the engine's concurrency model.** This is entirely an ASP.NET pipeline + plugin-path
  change; the single-writer transaction model and lock-free snapshot reads are untouched.

## 3. Design sketch

### 3.1 Authentication + the fallback policy (S1)

- Add an authentication scheme selectable by config. Default: **API key** via a custom
  `AuthenticationHandler` reading a configured header (e.g. `X-Api-Key`) compared against a
  configured secret (from user-secrets / environment, never a checked-in default). Offer **JWT
  bearer** (`AddJwtBearer`) as the alternative for deployments that already have an IdP.
- Register a default authorization policy so *everything* requires an authenticated principal unless
  it opts out:
  ```csharp
  builder.Services.AddAuthorization(o =>
      o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
  ```
- Insert the missing middleware in the correct order in `Program.cs` (around the current line 109-113):
  `UseHttpsRedirection()` → **`UseAuthentication()`** → `UseAuthorization()` → `MapControllers()`.
- Read endpoints the operator wants open (e.g. `/status`, `GET /vertex/count`) may carry
  `[AllowAnonymous]`; everything else inherits the fallback policy. The default remains
  authenticated — anonymous read is an explicit opt-out, not the baseline.

### 3.2 Opt-in gate for the RCE surface (S2, S3, S4)

- Bind a small options type from `Fallen8:*` config: `EnableDynamicCodeExecution` and
  `EnableDynamicPluginLoading`, both defaulting to **false**.
- Gate the three endpoints (a shared authorization requirement / action filter, or an explicit check
  at the top of each action) so that with the flag off they return **403** with a message like
  "Dynamic code execution is disabled on this server." *before* any compilation or file write.
  (403 = authenticated but the capability is administratively off; 501 is an acceptable alternative
  signal for "not enabled in this deployment" — pick one and document it.)
- The gate is independent of authentication: even an authenticated caller cannot compile/load unless
  the operator enabled it. This is defense-in-depth for the "trusted as the process" reality of §1.

### 3.3 Isolated plugin directory (S4)

- Replace the `AppContext.BaseDirectory` default in the upload path with a **configured, isolated
  plugin directory** (e.g. `Fallen8:PluginDirectory`), created if absent and distinct from the app's
  binaries. `AdminController.UploadPlugin` passes that directory to `PluginFactory.Assimilate(stream,
  path)` (the overload already accepts a `path`; only the default and the caller change). Plugin
  discovery must scan that directory. This does not make an uploaded DLL safe to run (it is still
  full-trust once loaded) — it removes the "overwrite/plant next to the server's own binaries"
  footgun and keeps loading behind `EnableDynamicPluginLoading`.

### 3.4 CORS, rate limiting, body size (S5)

- **CORS:** one named policy, default deny; allowed origins/headers/methods come from config. No
  wildcard-with-credentials.
- **Rate limiting:** `AddRateLimiter` with a global limiter plus a **stricter named partition**
  applied to `POST /path`, `PUT /subgraph`, and `PUT /plugin` (the expensive/dangerous endpoints).
  `UseRateLimiter()` in the pipeline; a breach returns **429**.
- **Body size:** keep a sane Kestrel `MaxRequestBodySize`; add a tight `[RequestSizeLimit(...)]`
  (small for the code endpoints, a bounded ceiling for `PUT /plugin`) so an oversized body is
  rejected with **413** before it is buffered/compiled/written.

### 3.5 Loopback-by-default binding (S6)

- The app resolves its bind address to **loopback** unless the operator explicitly opts into a
  public interface via configuration (e.g. a `Fallen8:AllowRemoteAccess` flag or an explicit
  `Kestrel`/`Urls` config that the app honours only when the opt-in is set). Startup logs the
  effective binding and, if public + anonymous read enabled, logs a prominent warning. This makes the
  safe posture the *enforced* default, not an accident of `launchSettings.json`.

### 3.6 Test harness

Auth, CORS, rate limiting and body-size are **middleware** — the existing controller tests
(`GraphControllerTest`, `SubGraphControllerTest`) new up the controller directly and bypass the
pipeline, so they cannot observe them. The acceptance tests run through the real pipeline with
`WebApplicationFactory<Program>` (the `Microsoft.AspNetCore.Mvc.Testing` package is **already**
referenced by `fallen-8-unittest`, and `Program` is a public class), configuring the auth/flag/CORS
settings per test via `WithWebHostBuilder`/in-memory config.

## 4. Acceptance criteria

- **Anonymous is rejected.** An unauthenticated request to each mutating endpoint
  (`PUT /vertex`, `PUT /edge`, property/remove mutations), each admin endpoint
  (`/save`, `/load`, `/trim`, `/tabularasa`, `/service`, `/plugin`), and each code endpoint
  (`POST /path`, `PUT /subgraph`) returns **401**. A request carrying the configured credential
  succeeds (same behaviour as today for that caller).
- **RCE gate off by default.** With `EnableDynamicCodeExecution` unset/false, an *authenticated*
  `POST /path` and `PUT /subgraph` return **403** (or the chosen 501) and never invoke Roslyn; with
  it true they compile and run as before. With `EnableDynamicPluginLoading` unset/false, an
  authenticated `PUT /plugin` returns **403** and writes nothing; with it true it stores into the
  isolated plugin directory (asserted) — never `AppContext.BaseDirectory`.
- **Perimeter controls enforced.** A disallowed cross-origin preflight is denied by the CORS policy;
  exceeding the code/plugin endpoints' rate partition returns **429**; a body over the endpoint limit
  returns **413** before any compile/write.
- **Trust-boundary characterization test.** A test documents, through the real pipeline, that (a)
  before the fix an anonymous mutating call would have been served and (b) after the fix it is 401,
  and that the code endpoints execute submitted code *only* when authenticated **and** the flag is on
  — the test's RCE proof uses a **benign** observable side effect (e.g. a filter returning a value
  derived from a runtime type / a sentinel), **never** `Environment.Exit` or anything that harms the
  test host.
- **Reads stay usable.** Any endpoint the operator marks `[AllowAnonymous]` is reachable without a
  credential; the default (unmarked) endpoints require one.
- **Suite green.** Existing controller tests still pass (they construct controllers directly, so
  they are unaffected by the pipeline gate); new pipeline tests are added; build clean.

## 5. Risks

- **False sense of safety.** Callers may read "authentication + opt-in flag" as "the code endpoints
  are now safe to expose." They are not: an authenticated + enabled caller is trusted as the process.
  The §1 honesty note must remain prominent in the spec, the README, and the endpoint XML docs.
- **Breaking existing clients.** Turning on auth 401s every current caller. Mitigate: document the
  credential setup; the flags default the *dangerous* surface off so the safe default is also the
  most restrictive; the change is a deliberate P0 posture shift, not a silent tweak.
- **Config/secret handling.** A checked-in or logged API key/JWT secret would negate the boundary.
  Secrets come from user-secrets/environment; never a default value in `appsettings.json`; never
  logged. `appsettings.json:9` `AllowedHosts: "*"` should be tightened alongside the CORS work.
- **Test-host fragility.** The RCE characterization test must not run destructive fragments in-process
  (see §4). Rate-limit tests must be deterministic (fixed-window with a low cap), not timing-flaky.
- **Plugin-directory migration.** Moving the plugin drop location changes where an operator's
  existing uploaded plugins live; discovery must scan the new directory and the change must be noted
  in the feature README.

## 6. Keep (do not regress)

- **The single-writer transaction model and lock-free snapshot reads.** This feature is entirely at
  the HTTP pipeline + plugin-path layer; the engine's concurrency model, `WaitUntilFinished`, and the
  `Try*(out, …) : bool` conventions are untouched.
- **The collectible codegen load contexts** (`collectible-codegen-assemblies`, LANDED) and the
  subgraph provider cache (`CodeGenerationHelper.cs:267`). Gating *whether* compilation runs must not
  disturb *how* it runs when enabled — collectibility, caching, and value-equality reuse stay as-is.
- **The `MaxVariableEdgeLength` shape guard** (`CodeGenerationHelper.cs:259`) and the existing
  compiler-diagnostic error reporting.
- **HTTPS redirection** (`Program.cs:109`) and the OpenAPI/Scalar dev surface — keep them; add HSTS
  consideration but do not remove redirection.
- **The plugin discovery memoization** from `engine-performance` P5 (`PluginFactory`
  `_candidateTypes` / name maps, invalidated on `Assimilate`): the isolated-directory change must
  keep invalidation correct so a newly uploaded plugin is still discovered.
