# API Security Boundary — Plan

Companion to [spec.md](./spec.md). P0 security. Trust boundary first (authentication), then the
RCE gate, then the perimeter controls. Every gate is pinned by a pipeline test.

GitHub issue: to be opened (label: feature). Feature branch: `feature/api-security-boundary`.

## Phase 0 — Baseline & guardrails
Intent: stand up a real-pipeline test harness and pin the current (insecure) reality so the fix
visibly flips it. This is security, not perf — the guardrail is **characterization tests through
`WebApplicationFactory<Program>`**, not an opt-in `[Ignore]` benchmark.
- [ ] Add an `ApiSecurityBoundaryTest` class using `WebApplicationFactory<Program>` (package
  `Microsoft.AspNetCore.Mvc.Testing` is already referenced; `Program` is public), with a helper to
  build a client and to inject in-memory `Fallen8:*` / auth config per test via `WithWebHostBuilder`.
- [ ] Characterization test capturing today's behaviour: an anonymous `PUT /vertex` (and one admin,
  one code endpoint) is currently served (documents S1). This test is REWRITTEN in Phase 1 to expect
  401 — its purpose is to make the boundary change visible and permanent.
- [ ] Characterization test documenting the RCE surface with a **benign** proof only: an authenticated
  (once auth exists) `POST /path` filter that returns a value derived from a runtime type / writes a
  sentinel, proving submitted code executes in-process. NEVER `Environment.Exit` or any host-harming
  fragment.

## Phase 1 — Authentication trust boundary (S1)
Intent: no anonymous access to mutating/admin/code endpoints; fix the missing middleware.
- [ ] Add a config-selectable auth scheme: a custom API-key `AuthenticationHandler` (header + secret
  from user-secrets/env) and/or `AddJwtBearer`. No default/checked-in secret.
- [ ] `AddAuthorization` with a `FallbackPolicy` = `RequireAuthenticatedUser()`.
- [ ] Insert `app.UseAuthentication()` **before** `app.UseAuthorization()` in `Program.cs`
  (currently only `:111` `UseAuthorization`); keep order `UseHttpsRedirection` → `UseAuthentication`
  → `UseAuthorization` → `MapControllers`.
- [ ] Mark the specific read endpoints the operator wants open with `[AllowAnonymous]` (e.g.
  `/status`, `/vertex/count`); leave everything else on the fallback policy.
- [ ] Flip the Phase 0 characterization tests: anonymous mutating/admin/code → 401; credentialed →
  works.

## Phase 2 — Opt-in gate for the RCE surface (S2, S3, S4)
Intent: the compile + plugin-load endpoints are off unless the operator enables them.
- [ ] Bind `Fallen8:EnableDynamicCodeExecution` and `Fallen8:EnableDynamicPluginLoading` (both
  default **false**) into an options type.
- [ ] Gate `POST /path` (`GraphController.cs:990`) and `PUT /subgraph` (`SubGraphController.cs:97`)
  so a disabled flag returns **403** BEFORE `CodeGenerationHelper.GeneratePathTraverser`
  (`GraphController.cs:1020`) / `TryGenerateSubGraphDefinition` (`SubGraphController.cs:137`) runs.
- [ ] Gate `PUT /plugin` (`AdminController.cs:320`) so a disabled flag returns **403** before
  `PluginFactory.Assimilate`.
- [ ] Tests: flag off ⇒ authenticated call to each of the three endpoints is 403 and no
  compilation/file write occurs; flag on ⇒ prior behaviour (compile/run/store).

## Phase 3 — Isolated plugin directory (S4)
Intent: uploaded DLLs never land next to the server binaries.
- [ ] Add `Fallen8:PluginDirectory` config (created if absent, distinct from `AppContext.BaseDirectory`).
- [ ] `AdminController.UploadPlugin` (`:324`) passes that directory to the existing
  `PluginFactory.Assimilate(stream, path)` overload; change the default in `PluginFactory.Assimilate`
  (`Plugin/PluginFactory.cs:175`, default at `:177`) so it no longer defaults to
  `AppContext.BaseDirectory`.
- [ ] Ensure plugin discovery scans the configured directory and that `Assimilate`'s cache
  invalidation (engine-performance P5) still makes a freshly uploaded plugin discoverable.
- [ ] Test: with plugin loading enabled, an upload writes into the isolated directory (asserted) and
  is discoverable; nothing is written to `AppContext.BaseDirectory`.

## Phase 4 — Perimeter controls (S5, S6)
Intent: CORS default-deny, rate limiting, body-size limits, loopback-by-default.
- [ ] Named CORS policy (default deny; origins/methods/headers from config); `UseCors` in the
  pipeline; tighten `appsettings.json:9` `AllowedHosts`.
- [ ] `AddRateLimiter` (global + a stricter named partition on `POST /path`, `PUT /subgraph`,
  `PUT /plugin`); `UseRateLimiter`; breach ⇒ 429.
- [ ] Kestrel `MaxRequestBodySize` + tight `[RequestSizeLimit]` on the code/DLL endpoints; oversize ⇒ 413.
- [ ] Loopback-by-default binding: the app resolves to loopback unless an explicit
  `Fallen8:AllowRemoteAccess` (or equivalent) opt-in is set; log the effective binding, warn on
  public + anonymous-read.
- [ ] Tests: disallowed CORS preflight denied; rate partition breach ⇒ 429 (deterministic
  fixed-window, low cap); over-limit body ⇒ 413.

## Measure & document
Intent: make the boundary and its honest limits legible.
- [ ] `features/api-security-boundary/README.md`: how to set the credential, how to enable the RCE
  flags, the isolated plugin directory, the CORS/rate/body knobs, and the loopback default.
- [ ] The §1 honesty note (in-process code is trusted-as-the-process; no sandbox; untrusted execution
  needs out-of-process/WASM) is repeated in the README and in the XML `<remarks>` of `POST /path`,
  `PUT /subgraph`, `PUT /plugin`.
- [ ] Update `POST /path` / `PUT /subgraph` / `PUT /plugin` `[ProducesResponseType]` + `<response>`
  docs to declare **401** and **403** (and 429/413 where applicable) so OpenAPI matches reality.
- [ ] Note the resolved cross-links: this satisfies `subgraph-quotas`' "authenticated the same as the
  rest of the API" premise (`features/subgraph-quotas/spec.md:18`); execution-time CPU/memory/timeout
  limits remain `dynamic-code-resource-limits`; the 401/403 body shape aligns later with
  `api-error-contract`; the `Program.cs` startup changes coordinate with `hosted-durability-lifecycle`.
- [ ] Full `dotnet test` green; build 0 warnings / 0 errors. Numbers/measurements (if any perimeter
  timing is captured): to be captured on this box.

## Outcome (what shipped)
- **S1** — `ApiKeyAuthenticationHandler` (`X-Api-Key`, constant-time compare) registered as the
  `Fallen8ApiKey` scheme; `AddAuthorization` sets a `FallbackPolicy` requiring an authenticated user
  when a key is configured; the missing `app.UseAuthentication()` is added before `UseAuthorization()`.
  With no key configured the server logs a loud UNAUTHENTICATED warning and leaves the fallback open
  (dev posture; the dangerous surface is still gated).
- **S2/S3/S4** — two authorization policies (`DynamicCodeExecution`, `DynamicPluginLoading`, each
  RequireAuthenticatedUser + a flag requirement handled by `DynamicCapabilityAuthorizationHandler`)
  applied via `[Authorize(Policy=…)]` on `POST /path`, `PUT /subgraph`, `PUT /plugin`. Flags default
  **false** → authenticated caller gets **403** before any compile/load; anonymous gets **401**.
- **S4 directory** — `PUT /plugin` writes into the configured isolated `PluginDirectory`
  (default `<base>/plugins`), never `AppContext.BaseDirectory`; `PluginFactory.AddPluginSearchDirectory`
  makes that directory a discovery location so an uploaded plugin is still found.
- **S5** — default-deny CORS policy (origins from config), a fixed-window rate limiter on the sensitive
  endpoints (`[EnableRateLimiting]`, 429 on breach), and `[RequestSizeLimit]` on the code (1 MiB) and
  plugin (64 MiB) endpoints.
- **S6** — `AllowRemoteAccess` flag bound + a prominent startup warning; hard loopback enforcement
  deferred (see the Decision) to avoid overriding operator Kestrel/port config.
- Tests: `ApiSecurityBoundaryTest` (anonymous→401, valid key accepted, open read reachable, code gate
  off→403 / on→reaches action, plugin gate off→403+nothing-written, controller-level isolated-dir
  write). `OpenApiDocumentTest` runs volatile+no-key so the pipeline still serves the doc.
  Full suite green: **386 passed, 0 failed, 14 skipped**.

## Progress
- [x] Phase 0 — pipeline test harness (`WebApplicationFactory<Program>`) + characterization (the
  anonymous→401 tests double as the before/after boundary proof)
- [x] Phase 1 — authentication scheme + fallback policy + missing `UseAuthentication` (S1)
- [x] Phase 2 — opt-in `EnableDynamicCodeExecution` / `EnableDynamicPluginLoading` gate (S2/S3/S4)
- [x] Phase 3 — isolated plugin directory (S4)
- [x] Phase 4 — CORS + rate limiting + body-size (S5); **loopback-by-default (S6) partial**: flag +
  warning shipped, hard bind enforcement deferred
- [x] Measure & document (README, honest-limits note in the endpoint `<remarks>`, OpenAPI 401/403 docs)

## Decision / revisit condition

**True untrusted-code execution is explicitly deferred.** In-process Roslyn cannot be sandboxed
(Code Access Security is gone), so this feature draws a *trust boundary* (who may reach the code
endpoints) and an operator *kill switch* (the opt-in flags), not a sandbox. Anyone allowed to post a
filter is trusted as the process. Revisit — as a separate, larger feature — only if the product must
accept genuinely untrusted submitted code; that would require **out-of-process or WASM isolation**
with a resource/timeout budget, and would build on `dynamic-code-resource-limits` rather than reopen
this trust-boundary work.

Related prior decisions this does NOT reopen: `collectible-codegen-assemblies` (LANDED — the gate
controls *whether* compilation runs, never how the collectible contexts work) and the
`MaxVariableEdgeLength` shape guard (a resource guard, not an auth gate).
