# F8 Studio: standalone deployable (decoupled from the data plane)

Status: open (spec/plan only). Owner: TBD. Feature branch: `feature/standalone-ui`
(branch-only workflow; no GitHub issue/PR unless asked).

Related: [web-ui](../../done/web-ui/), [studio-embeddable](../studio-embeddable/) (shares the
registry config-injection seam this feature introduces; see "The shared seam"),
[api-security-boundary](../../done/api-security-boundary/) (the CORS allow-list this feature
sets from the split overlay), [change-feed](../../done/change-feed/) (cross-origin SSE),
[bulk-import-export](../../done/bulk-import-export/) (cross-origin multipart).

> **Namespaces (feature graph-namespaces, 2026-07-23):** the standalone UI reaches a data plane
> through the same namespace-scoped REST surface as the all-in-one; a configured `apiUrl` plus the
> per-instance namespace scoping already in the registry needs no change here.

## Motivation

Let users **deploy F8 Studio (`fallen-8-web-ui`) as its own artifact**, a static web server that
talks to an arbitrary Fallen-8 REST data plane, instead of only as the UI baked into the all-in-one
container's `wwwroot`. This decouples the UI deployment from the data-plane deployment: an operator
can run the data plane in one place and the Studio in another, pointing the Studio at the data plane's
REST endpoint at container start.

`npm run env:up` runs F8 Studio as its **own container** (the split topology) by default: a UI-less
data plane at `:8080` and the F8 Studio UI at `:8081`. The all-in-one (UI baked into the API
container, both on `:8080`) remains available via a bare `docker compose up`, so this is a topology
choice, not a removal. The engine and the REST contract are untouched.

## Starting position (already decoupled: do not regress)

The UI is already ~80% decoupled at the code level. These properties must be preserved:

- **One transport choke-point.** Every REST call (JSON, multipart, bulk NDJSON, and the SSE change
  feed) goes through `buildUrl(instance.baseUrl, path)` in
  [`src/api/client.ts:108-123`](../../../fallen-8-web-ui/src/api/client.ts); base URL and auth are
  read per-instance, never from a global constant. An empty `baseUrl` yields root-relative paths,
  i.e. same-origin, end to end (`describeEndpoint` renders `""` as "same origin",
  `src/instances/types.ts:60-62`).
- **Auth is header-based, never cookies** (`authHeaders`, `client.ts:125-134`;
  server side `Security/ApiKeyAuthenticationHandler.cs`). No `SameSite`/credentialed-fetch concerns
  cross-origin.
- **The change feed uses fetch-streaming, not `EventSource`** (`src/api/changefeed.ts:284-287`), so
  it carries the auth header cross-origin.
- **Samples ride along in `dist/`.** `vite.config.ts` `serveSamples()` copies repo-root `samples/`
  into `dist/samples/` at `writeBundle` (`vite.config.ts:89-96`), so a standalone build is
  self-contained for the sample gallery.

The remaining couplings this feature removes: (1) no way to seed a default endpoint other than the
hardcoded same-origin `SAME_ORIGIN_INSTANCE`; (2) no static-server deployable; (3) the target data
plane's CORS is deny-all by default; (4) no split-topology compose story.

## The shared seam: registry config-injection

Both this feature and [studio-embeddable](../studio-embeddable/) need the **same seam**: seed the
registry's default ("managed") instance from an **external source** instead of the hardcoded
`SAME_ORIGIN_INSTANCE` (`src/instances/registry.ts:64-69`). They differ only in the *producer*:

- **standalone-ui (this feature):** a runtime `config.js` sets `window.__F8_CONFIG__ = { apiUrl }`,
  which the registry reads to seed the managed default. Built once, pointed anywhere at container
  start.
- **studio-embeddable (coupling #6):** a host SaaS portal passes `StudioConfig.instances` in-process
  at `mountStudio(el, config)`.

There is **one seam, two producers**. This feature introduces the registry-side injection point (a
small `configuredApiUrl()` reader and a persist `merge` that re-seeds the managed default each load);
the embeddable feature's `StudioConfig.instances` becomes a second producer into the same point, not
a parallel path. studio-embeddable's spec is annotated to reuse this seam.

## Design

### 1. Runtime config seam (`config.js` to `window.__F8_CONFIG__`)

- **New `fallen-8-web-ui/public/config.js`** shipping the default `window.__F8_CONFIG__ = { apiUrl: "" };`
  (empty means same-origin). Because it lives in `public/`, it lands at `dist/config.js` on a plain
  build (and `wwwroot/config.js` on `build:apiapp`), served at the origin root.
- **`index.html` loads it as a CLASSIC script.** Add `<script src="/config.js"></script>` in the
  `<head>` (currently `index.html:30-36`). It runs during parse, before the deferred `type="module"`
  entry script currently in `<body>` at `index.html:39`, and therefore before `registry.ts`
  module-eval (boot chain `main.tsx:30` to `routes.tsx:35` to `registry.ts:71-130`). It MUST be a
  classic script (not `type="module"`/`async`) or the ordering guarantee is lost.
- **New `fallen-8-web-ui/src/env.d.ts`** augments the global `Window`
  (`interface Window { __F8_CONFIG__?: { apiUrl?: string } }`) as an ambient, import-free declaration;
  none exists today, so reading the global otherwise fails the TypeScript build.
- **A small `configuredApiUrl()` reader, not an inline const.** The managed default's `baseUrl` is
  seeded by a function `configuredApiUrl()` that reads the global behind a `typeof window !== "undefined"`
  guard and passes it through `normalizeBaseUrl` (already imported at `registry.ts:29`; it maps
  `""`/`"/"` to same-origin and strips a trailing slash, `types.ts:54-58`). A function, because a bare
  module-level const is evaluated once at import and cannot be re-read by a unit test that sets the
  global afterward, so the seam would be untestable; the reader is unit-tested directly.
- **Behavior of the all-in-one is unchanged** (default `apiUrl:""` to root-relative to same-origin).
  Note this is behavior-identical, not byte-for-byte: `index.html` gains one script tag.

### 2. Default endpoint

- The split overlay wires `F8_API_URL=http://localhost:${F8_PORT:-8080}` on the UI container so it
  tracks the data plane's published host port. This is the **host-published, browser-reachable**
  address, never the container name/IP: the SPA runs in the browser, which is not on the docker
  network, so `fallen8:8080` and the `172.x` bridge IP are unreachable.
- **Name note.** `F8_API_URL` is also read by `vite.config.ts:60` as the dev-proxy target (Node,
  dev-only, default `http://localhost:5000`). The two live in different execution contexts (the dev
  machine at build/dev time vs the nginx container at run time) and never collide, but the spec calls
  it out so an operator is not surprised. The container-runtime knob is the operator-facing one.
- **Remote-host caveat (documented prominently).** `localhost` resolves to the *visitor's* machine.
  For any non-loopback deployment the operator MUST set `F8_API_URL` to the data plane's
  browser-reachable public URL; otherwise the managed default points at each visitor's own
  `localhost:8080`. First-run copy guides the user to Connect when the managed default is
  unreachable.

### 3. Managed default vs personal instances

The registry persists its whole state to `localStorage` under `f8.instances` via zustand `persist`
(`registry.ts:71-129`, no `partialize` today). The goal: the operator-provided managed default
re-syncs from `config.js` on every load (so a redeployed `F8_API_URL` propagates), while a user's
personal instances persist untouched. Two coupled pieces are required (this is not a "no reconciler"
design; the reconciler is the `merge`):

- **`partialize` persists personal (user-added) instances, `activeId`, AND `activeNamespaces`.** The
  managed default is NOT persisted; it is synthesized from `configuredApiUrl()` at store init. Do NOT
  drop `activeNamespaces` (the per-instance namespace selection, feature graph-namespaces): omitting
  it would silently regress the behavior-identical contract. `namespaceSupport` may be dropped (it is
  a re-probeable `/ns` cache); state that explicitly.
- **A custom persist `merge` re-injects the synthesized managed default ahead of the persisted
  personal instances on every load.** zustand's default `merge` is a shallow top-level spread, so the
  persisted `instances` array (personal-only, or empty) would otherwise REPLACE the creator-seeded
  array and drop the managed default from in-memory state, leaving a persisted `activeId==="local"`
  resolving to null and breaking the app. The merge is the mechanism that makes "always present" and
  "re-synced every load" true; `partialize` alone does not.
- **A runtime (in-memory) delete-guard on the managed default is required.** The managed default lives
  in the in-memory `instances` array, so `removeInstance` (`registry.ts:105-111`) can delete it
  regardless of what is persisted. Guard it at the store level, AND disable Remove for the managed
  record specifically in the UI (`disabled={instance.id === SAME_ORIGIN_INSTANCE.id || instances.length === 1}`,
  extending the existing `length===1` guard at `ConnectScreen.tsx:193`) so the button is not an
  actionable-looking no-op once a personal instance exists.
- **Legacy blobs upgrade transparently, no `version`/`migrate` needed.** The custom `merge` filters
  any persisted managed (`local`) record and re-injects the synthesized one, and `partialize` stops
  persisting it on the next write, so a returning user's stale `local` record is dropped without a
  version bump; personal instances, `activeId`, and `activeNamespaces` are preserved.
- **`SAME_ORIGIN_INSTANCE` stays exported and the managed default is present synchronously on first
  render** (`apiUrl:""` when `config.js` is absent), so the ~7 unit tests that seed it and the
  `e2e/first-run.spec.ts` "same origin" assertion stay green.
- **Synchronous only.** No async `InstanceStore` interface is introduced. The registry is read
  synchronously and pre-mount (`routes.tsx` redirect loader `useRegistry.getState()`;
  `useActiveInstance()!` non-null assertions), so going async would force hydration gates for zero
  present benefit. Async storage plus a `RemoteInstanceStore` are deferred (see Limitations).

### 4. Standalone deployable (static nginx image)

- **New `fallen-8-web-ui/Dockerfile`**, multi-stage: a `node` stage runs `npm ci` plus `npm run build`,
  then an `nginx` stage serves the built site. The compose service builds it with **`build.context: .`
  (the repo root)** and `dockerfile: fallen-8-web-ui/Dockerfile`, mirroring the all-in-one build, so
  the external `COPY`s of `features/done/web-ui/openapi-v0.1.json` and `samples/` resolve and the root
  `.dockerignore` (`**/dist` excluded, `features/done/web-ui/openapi-v0.1.json` re-included) applies.
  **Build the SPA inside the image**; do NOT `COPY` a host-built `dist/` (stripped by `.dockerignore`
  `**/dist`, yielding an empty site).
- **New `fallen-8-web-ui/nginx.conf`:**
  - `try_files $uri $uri/ /index.html;` for SPA deep-link fallback, **after** the static-file match
    so existing `/samples/*.jsonl` are served, not shadowed.
  - `.jsonl` to `application/x-ndjson` for Content-Type parity with the apiApp
    (`Program.cs:521-523`). On nginx this is a correctness nicety, not a hard requirement (the client
    reads samples via `.blob()`), unlike ASP.NET where an unmapped type falls through to the SPA
    fallback.
  - Cache-Control: `no-store` on `/config.js`, `no-cache` (revalidate) on `/index.html`, and
    `public, max-age=31536000, immutable` on the hashed `/assets/`. Without this a cached `config.js`
    keeps pointing at the old `apiUrl` after a redeploy.
- **Entrypoint rewrites `/config.js` from `F8_API_URL` before starting nginx**, kept minimal: safely
  JS-string-escape the value and refuse a value containing characters that would break the JS literal
  (quote, backslash, newline). Trailing-slash/empty **normalization lives in one home**, the
  client-side `normalizeBaseUrl` (do not also normalize in the entrypoint). No absolute-origin
  "fail-fast" validation (gold-plating for an operator-set env var).
- **`HEALTHCHECK`** on `GET /config.js` (or `/index.html`).

### 5. Topologies

- **`npm run env:up` runs the split topology by default.** `scripts/env-up.js` layers
  `docker-compose.split.yml` (last, so its overrides win) onto the base + observability files, so the
  default dev environment is the decoupled one: a UI-less data plane at `:8080` and the F8 Studio UI
  container at `:8081`, plus the existing sidecars. `env:down`/`logs`/`status` include the same
  overlay; the old `env:split:*` scripts are retired (redundant now that `env:up` IS the split).
- **`docker-compose.split.yml` is an OVERLAY**, applied via
  `docker compose -f docker-compose.yml -f docker-compose.split.yml` (NOT `--profile split`, which is
  additive and cannot stop the default `fallen8` co-starting, and NOT `extends`, which cannot
  selectively inherit env). It reuses every base sidecar (ollama, embeddings, chat, observability)
  with zero duplication and only overrides/adds what changes:
  - **`fallen8`:** override `build` to `{ context: ., dockerfile: fallen-8-core-apiApp/Dockerfile }`
    (the UI-less apiApp image, so the data plane serves API only at `:8080`; overriding `image:` alone
    would not change the inherited `build: .`). That image installs `curl`, so the base `/status`
    healthcheck still runs and `f8-mcp`'s `service_healthy` dependency stays satisfiable. Add
    `Fallen8__Security__AllowedCorsOrigins__0=http://localhost:${F8_UI_PORT:-8081}`.
  - **`ollama`:** override `OLLAMA_ORIGINS` to include the UI origin
    (`http://localhost:${F8_PORT:-8080},http://localhost:${F8_UI_PORT:-8081}`) so browser-direct
    NL-assist works from the split UI. (`OLLAMA_ORIGINS` is read by the ollama server, not the apiApp.)
  - **`f8-studio` (new):** the UI image, mapping `${F8_UI_PORT:-8081}:80`, env
    `F8_API_URL=http://localhost:${F8_PORT:-8080}`, a healthcheck, and `depends_on: fallen8`.
- **Raw `docker compose up`** (no overlay) is the **all-in-one** (UI baked into the API container,
  both on `:8080`); CI's `docker compose config` (default file only) and the all-in-one build/smoke
  are unaffected.
- **Ports:** the data plane keeps `:8080` (`F8_PORT`); the UI is `F8_UI_PORT`, default `8081` (chosen
  to avoid the API on `:8080` and Grafana on `:3000`, so it coexists with the observability stack in
  the default `env:up`).

### 6. CORS and cross-origin correctness

- **The data-plane service sets the allow-list** using the indexed array form the binder requires:
  `Fallen8__Security__AllowedCorsOrigins__0=http://localhost:${F8_UI_PORT:-8081}`. A bare
  `Fallen8__Security__AllowedCorsOrigins=...` does NOT bind a `String[]` and CORS silently stays
  deny-all (the root `Dockerfile:49` documents the indexed form). This reuses the existing
  api-security-boundary seam (`Program.cs:411-420`); `AllowAnyHeader()`/`AllowAnyMethod()` are already
  set and no credentials are used (bearer-header auth).
- **Preflight hardening.** `UseCors` (`Program.cs:526`) already precedes `UseRateLimiter` (`:527`) and
  `UseAuthentication`/`UseAuthorization` (`:539-540`); add a test that a cross-origin preflight
  `OPTIONS` returns 204 anonymously and does not consume a rate token, and add `SetPreflightMaxAge`
  (about 600s) to the default policy so the SSE reconnect loop and bulk import do not preflight on
  every request. nginx serves only the UI and is not in the API path, so there is no SSE
  proxy-buffering concern.
- **CORS diagnosability.** Today `useConnectionState` (`AppShell.tsx:88`) maps any fetch error to
  "unreachable", so a missing allow-list entry (the single most likely standalone misconfiguration) is
  indistinguishable from a down server. Add a hint on the Connect/connection-guard surface: when the
  active instance is cross-origin (non-empty `baseUrl`, different origin) and the probe fails as a
  network error, guide the operator to verify the data plane's `AllowedCorsOrigins` includes this UI's
  origin.
- **Browser-direct NL-assist** works because the overlay sets `OLLAMA_ORIGINS` on the `ollama` service
  to include the UI origin (see §5); the default server-side `/chat` and embeddings path keeps working
  because the overlay retains the base `ollama` sidecar.

### Limitations and named revisit triggers

- **Root-hosting only.** Assets and `/samples` use absolute root paths and Vite `base` is `/`; the
  runtime seam cannot fix a sub-path deployment. *Revisit trigger:* a reverse proxy that mounts the
  UI under a sub-path, which needs a build-time `base` knob.
- **Bearer-header auth only cross-origin.** Works because auth is a header, so `AllowCredentials`
  stays off. *Revisit trigger:* cookie/session cross-origin auth, which needs an explicit
  `AllowCredentials` plus exact-origin decision.
- **Async storage plus `RemoteInstanceStore` plus a per-user `/user/instances` endpoint are
  deferred.** *Revisit trigger:* a real identity/auth concept (the same trigger studio-embeddable and
  multi-instance-host name).
- **Exactly one managed default.** *Revisit trigger:* multiple operator-seeded instances.
- **No browser two-origin e2e harness.** Cross-origin behavior is covered by the config-seam and
  cross-origin unit tests, the `CorsPreflightTest` integration test, and the CI container smoke test;
  a full two-origin Playwright harness is deferred as disproportionate. *Revisit trigger:* an observed
  cross-origin bug these layers do not catch.

## Non-goals (right-sizing)

- No async storage abstraction and no named `InstanceStore` interface now (YAGNI; the only consumer is
  the deferred remote store).
- No fake user/identity system.
- No sub-path hosting.
- No new REST endpoint (so no OpenAPI snapshot change and no MCP bridge change).
- No cookie-based cross-origin auth.

## Impact on existing features (mandatory cross-feature sweep)

Grounded in the recon and the adversarial review against the actual code; `file:line` evidence in the
plan.

| Layer | Impacted | What changes |
|-------|----------|--------------|
| Engine (`fallen-8-core`) | No | No engine capability grows; the engine to REST to MCP propagation rule is not triggered. |
| REST contract / OpenAPI snapshot | No | No new/changed route. Do NOT regenerate `features/done/web-ui/openapi-v0.1.json`; `OpenApiDocumentTest` stays green. |
| MCP (`fallen-8-mcp`) | No | No new REST op to bridge; `McpRestCoverageTest`/`McpContractTest` stay green. The deferred `/user/instances` will run its own sweep when it lands. |
| api-security-boundary (CORS) | Config + one line | The split overlay SETS `AllowedCorsOrigins` (a runtime value); the only apiApp code touch is `SetPreflightMaxAge` plus a preflight test. |
| CI (`.github/workflows/`) | Yes | `buildAndTest.yml`'s `docker` job today validates only the default compose and builds only the all-in-one image. Extend it to `docker compose -f docker-compose.yml -f docker-compose.split.yml config -q` and to build + smoke-test the new web-ui image, so the new deployable has automated coverage. |
| Studio UI (`fallen-8-web-ui`) | Yes | `public/config.js`, `index.html`, `env.d.ts`, `registry.ts` (`configuredApiUrl()` reader + `partialize` + `merge` + migration + managed delete-guard), `ConnectScreen.tsx` (disable Remove for the managed record + CORS hint), new `Dockerfile`/`nginx.conf`/entrypoint. |
| studio-embeddable | Yes (shared) | Its coupling #6 reuses this feature's registry config-injection seam instead of a parallel path; a one-line pointer is added to its spec and its stale `registry.ts:26` citation is corrected to `:64-69`. |
| Screenshots | Maybe one | `docs/src/assets/images/screen-connect.png` only if the Connect chrome visibly changes; the same-origin all-in-one keeps "same origin", so recapture is avoidable if only the managed-record Remove button is disabled. |
| Docs site | Yes | New page `docs/src/content/docs/standalone-ui.mdx` + sidebar entry in `docs/astro.config.mjs` (F8 Studio group, `:84-87`); README Key-features line + architecture diagram/prose; `docs/src/content/docs/architecture.md` mermaid (single source) gains the split channel; `index.mdx` CardGrid parity; `running.mdx` topology + env-var table; `studio.md` Connect prose. Diagram style fixed (dark surfaces, brand red `#E2001A`; reuse existing `classDef`s). |
| Debugging doc | Light | `docs/src/content/docs/debugging.md` "Ports at a glance" now lists the UI (`:8081`) and REST API (`:8080`) as separate default services; launchSettings/`.vscode` unchanged. |
| NL-assist finetune | No | This feature touches no delegate-fragment/prompt/`type-model` surface, so per `nl-assist-finetune/RETRAIN-LOG.md` (its rule keys on the fragment surface the model drafts against) it needs no entry. |
| Samples / stored queries | Served-by only | nginx must serve `/samples` (content and manifest unchanged); stored queries follow `apiUrl` to the remote plane, no client persistence. |
| Observability identity | No | The UI emits no OTLP and needs no fleet identity; the UI's `:8081` default avoids Grafana's `:3000`, so both run in the default `env:up`. |
| Tests | Yes | New unit tests (`configuredApiUrl()` reader; `partialize`/`merge` managed-vs-personal seed, resync, `activeNamespaces` survival, and legacy-blob upgrade; injected-`apiUrl` normalization; `isCrossOriginInstance`); a backend `CorsPreflightTest` (real-pipeline preflight). Cross-origin is covered by these plus the CI container smoke test rather than a browser two-origin harness (deferred, see plan). `setup.ts` needs NO change (jsdom always has `window`; the reader's `?.`/`??` handle an absent global). |

**Single-home doc assignment (one-home-per-explanation):**

- Deployment mechanics (`config.js`, entrypoint, nginx image, split overlay) live once on
  `standalone-ui.mdx`.
- The "how to run / topologies plus `F8_API_URL`" table lives once in `running.mdx`;
  `standalone-ui.mdx` links to it rather than duplicating it.
- The managed-vs-personal instance concept gets one home in `studio.md` Connect (it is user-visible in
  the all-in-one too); other pages point there.
- The README (simple view) and `architecture.md` (full view) mermaid pair is a sanctioned pair, not
  duplication; do not add prose that re-explains the seam beside them.

## Behavior-preservation contract

Every phase lands with the all-in-one, dev (`vite` proxy), and the existing web-ui unit tests plus
e2e suite green, because `config.js` defaults `apiUrl:""` (same-origin), `SAME_ORIGIN_INSTANCE` stays
present synchronously on first render, and `activeNamespaces` is retained by `partialize`.
Cross-origin behavior is exercised by the new two-origin e2e profile, not by the existing
single-origin suite.
