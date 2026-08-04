# Plan: F8 Studio standalone deployable

Phased so each step is independently shippable and keeps the all-in-one image (a bare
`docker compose up`) and the existing web-ui tests behaviorally identical. Phases 1 to 2 are front-end seams; 3 to 4 are the deployable and
topology; 5 to 6 are cross-origin hardening, tests, and docs. No engine or REST-contract change; the
only backend touch is one CORS-policy line plus a preflight test (Phase 5).

All `file:line` references were verified by the pre-spec recon and the adversarial review against the
current code.

## Phase 1: Config seam (behavior-identical)

- Add `fallen-8-web-ui/public/config.js` with the default `window.__F8_CONFIG__ = { apiUrl: "" };`.
- Add a CLASSIC `<script src="/config.js"></script>` in `fallen-8-web-ui/index.html` `<head>`
  (`:30-36`); it runs before the deferred module script currently in `<body>` at `index.html:39`.
- Add `fallen-8-web-ui/src/env.d.ts` augmenting the global `Window` with
  `__F8_CONFIG__?: { apiUrl?: string }` (ambient, no import/export).
- In `src/instances/registry.ts`, add a `configuredApiUrl()` reader (guarded by
  `typeof window !== "undefined"`, wrapping the value in `normalizeBaseUrl`, already imported at
  `:29`) and seed `SAME_ORIGIN_INSTANCE.baseUrl` from it. Do NOT inline a bare const (untestable) and
  do NOT touch `tests/setup.ts` (no change needed: jsdom always defines `window`, and `?.`/`??` handle
  an absent global).
- **Verify:** all existing web-ui unit tests and e2e green; all-in-one and `vite` dev still resolve
  same-origin; add a unit test for `configuredApiUrl()` covering an absent global, `""`, a plain
  origin, a trailing slash (stripped), and surrounding whitespace (trimmed).

## Phase 2: Managed default vs personal instances (`partialize` + `merge` + migration)

- In `src/instances/registry.ts`, configure `persist` with:
  - `partialize` persisting personal (user-added) instances, `activeId`, AND `activeNamespaces`
    (retain it: dropping it regresses the per-instance namespace selection). Drop `namespaceSupport`
    (re-probeable).
  - a custom `merge` that re-injects the freshly synthesized managed default (from `configuredApiUrl()`)
    ahead of the persisted personal instances every load, so the managed default is always present and
    re-synced. (`partialize` alone does not: zustand's default shallow merge would let the persisted
    `instances` replace the seeded array and drop the managed default.)
  - no `version`/`migrate` step: the `merge` already filters any legacy persisted `local` record and
    re-injects the synthesized managed default, and `partialize` stops persisting it on the next
    write, so legacy blobs upgrade transparently (personal instances, `activeId`, and
    `activeNamespaces` preserved).
- Guard the managed default against deletion at the store level in `removeInstance` (`:105-111`), AND
  disable Remove for the managed record in the UI:
  `disabled={instance.id === SAME_ORIGIN_INSTANCE.id || instances.length === 1}` (extend
  `ConnectScreen.tsx:193`) so the button is not a silent no-op once a personal instance exists.
- **Verify:** the ~7 unit tests that seed `SAME_ORIGIN_INSTANCE` and the registry-touching tests
  (`app-shell.test.tsx`, `connect-config.test.tsx`, `namespaces.test.tsx`) stay green or are updated
  in lockstep; add tests for the `partialize`/`merge` split (managed default present after rehydration
  with a persisted personal instance), the managed-default resync when `apiUrl` changes, that
  `activeNamespaces` survives a reload, and the legacy-blob upgrade (a persisted `local` record is
  dropped and the managed default re-synced). (Note: `connect-paths.test.ts`
  is unrelated: it covers `connectPaths` + the `f8.workspace.*` store, not the registry.) Recapture
  `screen-connect.png` only if the Connect chrome visibly changed.

## Phase 3: Standalone nginx image + entrypoint

- Add `fallen-8-web-ui/Dockerfile` (multi-stage): a `node` stage `npm ci` + `npm run build`, then an
  `nginx` stage serving the built site. The build context is the repo root (the compose service sets
  `build.context: .`, `dockerfile: fallen-8-web-ui/Dockerfile`), so the external `COPY`s of
  `features/done/web-ui/openapi-v0.1.json` and `samples/` resolve and the root `.dockerignore` applies.
  Build the SPA in-container; do NOT copy a host `dist/` (`.dockerignore` `**/dist`).
- Add `fallen-8-web-ui/nginx.conf`: `try_files $uri $uri/ /index.html;` after the static match; the
  `.jsonl` to `application/x-ndjson` MIME entry; Cache-Control `no-store` on `/config.js`, `no-cache`
  on `/index.html`, `immutable` long-cache on `/assets/`.
- Add the entrypoint: JS-string-escape `F8_API_URL`, refuse a value containing quote/backslash/newline,
  rewrite `/config.js`, then start nginx (trailing-slash/empty normalization is left to the client-side
  `normalizeBaseUrl`, one home). Add a `HEALTHCHECK`.
- **Verify:** build the image; smoke-test that `/config.js` carries `no-store` and reflects
  `F8_API_URL`; `/samples/*.jsonl` serve as files (not shadowed by the SPA fallback); a deep-link
  reload returns `index.html`.

## Phase 4: Split topology as the default env:up (compose overlay + scripts)

- Add `docker-compose.split.yml` as an OVERLAY over `docker-compose.yml` (two `-f` files), NOT a
  standalone file and NOT a `--profile`/`extends` approach:
  - override `fallen8.build` to `{ context: ., dockerfile: fallen-8-core-apiApp/Dockerfile }` (UI-less
    data plane) and add `Fallen8__Security__AllowedCorsOrigins__0=http://localhost:${F8_UI_PORT:-8081}`;
  - override `ollama`'s `OLLAMA_ORIGINS` to include `http://localhost:${F8_UI_PORT:-8081}` (an env of
    the `ollama` server, not the apiApp);
  - add `f8-studio` (the new UI image, `build.context: .`, `dockerfile: fallen-8-web-ui/Dockerfile`)
    mapping `${F8_UI_PORT:-8081}:80`, `F8_API_URL=http://localhost:${F8_PORT:-8080}`, a healthcheck,
    and `depends_on: fallen8`.
- Give the apiApp image `curl` (`fallen-8-core-apiApp/Dockerfile` base stage) so the base `/status`
  healthcheck runs in the UI-less data plane; otherwise `f8-mcp` (`depends_on: fallen8 service_healthy`)
  cannot start.
- Wire the overlay into `env:up`: `scripts/env-up.js` appends `-f docker-compose.split.yml` (last);
  `env-info.js` prints the UI at `F8_UI_PORT` (8081) and the API at `F8_PORT` (8080);
  `env:down`/`logs`/`status` add the overlay. Retire the now-redundant `env:split:*` scripts.
- Extend `.github/workflows/buildAndTest.yml` `docker` job to
  `docker compose -f docker-compose.yml -f docker-compose.split.yml config -q` so the overlay is
  CI-validated.
- **Verify (live):** `npm run env:up` brings up the UI-less data plane (`:8080`, healthy) + the
  `f8-studio` container (`:8081`, healthy) + `f8-mcp` (healthy, proving the healthcheck fix); the UI
  serves and reaches the API cross-origin (a 204 preflight carrying the allow-origin + max-age); raw
  `docker compose up` (no overlay) is the all-in-one.

## Phase 5: Cross-origin hardening (CORS diagnosability + preflight)

- Add the CORS-aware hint to the Connect/connection-guard surface (`AppShell.tsx` `useConnectionState`
  at `:88`): when the active instance is cross-origin and the probe fails as a network error, surface
  the `AllowedCorsOrigins` guidance instead of a bare "unreachable".
- Add `SetPreflightMaxAge` (about 600s) to the default CORS policy in `Program.cs` (`:411-420`).
- **Verify:** add an apiApp test that a cross-origin preflight `OPTIONS` returns 204 anonymously and is
  not rate-limited (guards the `UseCors` `:526` before `UseRateLimiter` `:527` / auth `:539-540`
  ordering); add a web-ui test that a cross-origin network failure renders the CORS hint and a
  same-origin failure does not.

## Phase 6: Two-origin e2e + docs + architecture diagrams + CI image build

- Cross-origin behavior is covered WITHOUT a browser two-origin harness: `config.js` injection by the
  config-seam unit test plus the CI container smoke test (the real nginx image serving the real
  `config.js` with `no-store`); the CORS contract by `CorsPreflightTest` (a 204 anonymous preflight
  with the max-age, and deny for a disallowed origin, through the real pipeline); and the diagnostics
  by the `isCrossOriginInstance` test. A full two-origin Playwright harness (two servers plus a
  `config.js` rewrite) is deferred as disproportionate for the marginal added signal. *Revisit
  trigger:* an observed cross-origin bug these layers do not catch.
- Extend `.github/workflows/buildAndTest.yml` to build the new web-ui image and smoke-test it (serves
  `/index.html` and a `no-store` `/config.js`).
- Docs (respect single-home assignment from the spec):
  - New `docs/src/content/docs/standalone-ui.mdx` (title "Standalone F8 Studio", slug `standalone-ui`);
    register it in the F8 Studio group in `docs/astro.config.mjs` (`:84-87`).
  - README: one Key-features line linking `https://cosh.github.io/fallen-8-core/standalone-ui/`;
    update the architecture prose and mermaid to add the standalone/split channel (the compose default
    is decoupled; the all-in-one is the bare `docker compose up` path). Reuse the existing
    `classDef`/brand-red styling; no mermaid defaults.
  - `docs/src/content/docs/architecture.md` mermaid (the single source) gains the split channel; prose
    reframed so the split topology is the default `env:up` and the all-in-one is the bare
    `docker compose up` fallback.
  - `index.mdx` CardGrid parity card; `running.mdx` topology + `F8_API_URL` env table (the one home for
    the topologies table); `studio.md` Connect prose (the one home for managed-vs-personal).
  - `debugging.md` "Ports at a glance" (`:122`) split UI row + one-line CORS note.
- Add the cross-link into `features/open/studio-embeddable/spec.md` coupling #6 (reuse this seam) and
  correct its stale `registry.ts:26` citation to `:64-69`.
- **Verify:** `npm --prefix docs ci && npm --prefix docs run build` passes (link-checked); recapture
  `screen-connect.png` if Phase 2 changed the chrome; the two-origin e2e and the CI image smoke-test
  pass.

## Test strategy

- The existing web-ui unit tests plus e2e are the same-origin baseline and must stay green every phase
  (default `config.js` `apiUrl:""`).
- New coverage is added per phase: config reader and normalization (P1); `partialize`/`merge`,
  managed-vs-personal, `activeNamespaces` survival, and migration (P2); nginx serving and cache headers
  (P3); split-overlay bring-up and compose config validation (P4); CORS preflight and diagnosability
  (P5); the two-origin cross-origin flows and the CI image smoke-test (P6).
- No engine/apiApp behavior tests change except the new CORS preflight test (P5); the OpenAPI snapshot
  and MCP coverage tests are untouched (no route change).

## Risks and mitigations

- **Dropped managed default on rehydration:** the custom persist `merge` (P2) re-injects it every load;
  a test asserts it survives with a persisted personal instance. `partialize` alone is insufficient.
- **Silent `activeNamespaces` regression:** `partialize` explicitly retains it (P2), with a
  survives-a-reload test.
- **Untestable config seam:** the `configuredApiUrl()` reader (P1) is a function, not a module-level
  const, so a unit test can set the global and re-read it.
- **Async-hydration hazard (avoided):** staying synchronous leaves the pre-mount `getState()` and
  `useActiveInstance()!` call sites unaffected; no hydration gate is needed.
- **Stale `config.js` after redeploy:** `no-store` on `/config.js` and `no-cache` on `index.html`
  (P3), with a header assertion test.
- **Silent CORS misconfiguration:** the indexed env-var form (P4), the diagnosability hint (P5), and
  the two-origin e2e (P6).
- **Compose overlay wrong-image trap:** override `build` (not `image`) on `fallen8`, since the base
  uses `build: .` (the all-in-one, UI-bearing) which merely tagging a new `image:` would not replace.
- **f8-mcp healthcheck dependency:** the UI-less apiApp image must ship `curl` so `fallen8`'s
  `/status` healthcheck runs; otherwise `f8-mcp` (`depends_on: service_healthy`) never starts. Caught
  live (a config-only check missed it).
- **Port collision (`:3000` Grafana):** the UI default is `:8081` (not `:3000`), so it coexists with
  the observability stack under the default `env:up`; the overlay re-tasks the single `fallen8`
  service rather than adding a second data plane, so `:8080` has one owner.
- **New deployable unguarded by CI:** extend `buildAndTest.yml` to validate the overlay and build +
  smoke-test the UI image (P4/P6).
- **Remote-host `localhost` footgun:** documented prominently plus first-run guidance; the managed
  default is unreachable rather than wrong, and users can add a personal instance that persists.
