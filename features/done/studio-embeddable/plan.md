# Plan - F8 Studio embeddable

Phased so each step is independently shippable and keeps the standalone app behaviorally identical
(default `StudioConfig` == today). Phases 1-5 are pure front-end seams; Phase 6 is packaging and only
lands when a host consumer exists. No engine or API changes are required.

## Phase 1 - Mount seam & config boundary (landed)

- Add `src/app/mount.tsx` exporting `mountStudio(el, config?)` and `<F8Studio config?>`. Move the
  provider tree (QueryClient + RouterProvider) here.
- Reduce `src/main.tsx` to `mountStudio(document.getElementById("root")!)` - the standalone entry with
  no config.
- Add `StudioConfigContext` (React context) + `useStudioConfig()`; the empty default config
  equals today's behavior. (This plan originally named these `StudioConfigProvider` /
  `defaultStudioConfig`; the names above are the code's.)
- **Verify:** existing tests green; add a test that `mountStudio(el)` renders the shell and seeds
  `SAME_ORIGIN_INSTANCE` exactly as `main.tsx` did.

## Phase 2 - Instance, credential & namespace injection (landed)

- Registry (`src/instances/registry.ts`) initializes from `StudioConfig.instances ?? [SAME_ORIGIN_INSTANCE]`
  and `activeInstanceId`. `lockInstances` hides the add/remove/connect affordances **and** the
  Connect screen's activation radios, listing only the host's instances there (the shell's switcher
  is a static label, and Connect stays reachable). Host-supplied instances are managed (never
  persisted), reusing the existing partialize/merge filter that already keeps the config.js-seeded
  default out of `localStorage`; `"local"` stays a reserved id under a host config so a legacy
  whole-state blob cannot resurrect it as a personal instance.
- Add the `bearer` auth arm (`{ kind: "bearer"; getToken(): Promise<string> }`). The token resolves
  at the transport choke points; the standalone kinds keep their synchronous fast path, and the sync
  `authHeaders` throws for bearer so a forgotten call site cannot send an unauthenticated request.
- Seed the per-instance active namespace from `StudioConfig.namespace`; `lockNamespace` hides the
  namespace switcher, the namespace management panel and the recover-state switch. The `GET /ns`
  support probe and pre-namespace degradation are unchanged.
- Namespace every `localStorage` key through a `storageKey(name)` helper that prepends
  `config.storageNamespace` (default `""`, so keys stay `f8.instances` etc.). The module-level
  stores skip their import-time hydration and each merge derives persisted state from storage plus
  config alone, so a prefixed embed neither inherits bare-key state nor the previous mount's; the
  memoized workspace stores are dropped per mount for the same reason (they bake their key in at
  creation). Config application lives in `src/app/applyStudioConfig.ts` so it is testable without
  the React shell.
- **Verify:** instance-isolation + auth-header tests still green; add tests for a host-supplied
  instance, a bearer instance (token resolved, never persisted, rejection fails the request), a
  pinned/locked namespace, a non-empty storage namespace, the reserved-id case, and cross-tenant
  isolation across sequential mounts (workspace drafts, NL-assist config incl. its api key,
  first-run dismissals, active namespace).

## Phase 3 - Router basepath (landed)

- Thread `config.basepath` into `createRouter` (`src/app/routes.tsx`); default `""` (root, as today).
- Support `history: "memory"` when the host owns the address bar.
- **Verify:** routing tests green at default basepath; add tests that hrefs carry a non-empty
  basepath, that a navigation resolves under it (prefix applied at the history layer, route matched
  back, host address bar untouched), and that a legacy-path redirect stays inside it.

## Phase 4 - CSS scoping & theming (landed)

- Wrap Studio content in a `.f8-studio` root container; scope the generic-named
  `.panel/.btn/.input/...` primitives under it with `:where(.f8-studio)` (zero specificity
  change), so they neither leak into nor collide with the host DOM. The name-namespaced
  families (`.f8fr-*`, `.eclipse-highlight`) stay unscoped. Standalone wraps its `#root` in
  `.f8-studio`, pixel-identical. Tailwind's preflight stays global here: it cannot be
  import-scoped in plain CSS, and only the packaged library artifact (Phase 6) ever meets a
  host DOM - the scoped preflight is a packaging concern and moved there.
- Keep today's token values where Tailwind emits them and let `config.theme` override them as inline
  custom properties on `.f8-studio` (colors **and** the `--font-*` type stack). Drop the hard
  `html.dark` dependency (keep the dark defaults). Emitting the defaults on the scope root rather
  than `:root` only matters for the library artifact, so it rides with packaging.
- Give `Dialog.Portal` a `container` = the Studio root so modals stay inside an embedded region.
  This is load-bearing for the scoping above, not just for embedding: the modal primitives are
  scoped to the root, so a portal that escapes it renders unstyled.
- **Verify:** style-engine tests green; a test that a `config.theme` override reaches the tokens;
  a test that a portalled overlay lands inside the scope root. Visual check (done 2026-08-06):
  recaptured `screen-connect.png` against a real apiApp via the e2e harness and compared it to the
  committed baseline - identical but for the rendered "created" date, so the standalone rendering is
  unchanged and the committed screenshots stand.

## Phase 5 - Canvas component export (landed)

- Freeze the `GraphCanvas` prop contract (`CanvasNode`, `CanvasEdge`, `StyleConfig`, `ElementRef`)
  as public API and export it as `F8GraphCanvas`; the component already has no store or query
  dependencies, so this is contract + naming work, not extraction work.
- Make its styles self-contained under the `.f8-studio` scope (it must render on a page that never
  loaded the app shell).
- **Verify:** a test renders `F8GraphCanvas` with literal node/edge data outside the app shell and
  asserts selection callbacks fire; existing canvas tests green.

## Phase 6 - Packaging (landed 2026-08-12)

What shipped, including where reality overruled the plan's guesses:

- **The build target** is `npm run build:lib`: `vite build -c vite.lib.config.ts` (library mode
  over the `src/embed/index.ts` surface, ESM, `dist-lib/`), then `tsc -p tsconfig.lib.json`
  (declarations into `dist-lib/types` - after vite, which empties the outDir), then
  `scripts/check-lib-artifact.mjs`, whose exit code is the artifact's verdict: entry and
  declarations exist, no `process.env.NODE_ENV` read survives, and every stylesheet selector
  carries `.f8-studio`. package.json gained the exports map (`types` condition first, plus
  `./styles.css` - the host imports the stylesheet explicitly), `files: ["dist-lib"]`, and
  react/react-dom moved to peerDependencies (kept in devDependencies for the repo's own builds).
- **Four lib-only settings** the plan did not foresee, each load-bearing: `process.env.NODE_ENV`
  is defined away (vite deliberately preserves it in lib mode and bundled deps read it, so a
  host without the define throws); the monaco editor worker is aliased to `?worker&inline`
  (lib mode cannot emit a separately served worker asset); `VITE_F8_SAMPLES_BASE` is defined to
  the repository's raw GitHub mirror (a host origin serves no `/samples`); `publicDir` is off
  (favicon and config.js are standalone-page concerns).
- **The scoped preflight became a whole-stylesheet scoping pass**: a local postcss plugin in the
  lib config rewrites every selector under `.f8-studio` (`:root`/`:host`/`html`/`body`/`#root`
  become the scope root; `*` and bare pseudo-elements become the root plus its descendants;
  keyframe steps stay untouched). One mechanism ships the scoped preflight AND relocates the
  `@theme` token defaults off the host's `:root` - the two lib-only CSS problems phase 4 parked
  here - and the standalone build never sees it.
- **The emitted declarations were a trap the plan missed**: the TanStack `Register` augmentation
  moved from `routes.tsx` into `src/types/router-register.d.ts` (a declaration INPUT, consumed
  in-repo but never re-emitted), because riding the d.ts chain into the artifact would hijack
  the router types of any host that registers its own TanStack router.
- **The shell logo moved from `public/` to `src/assets/`** and is imported as a module, so the
  embed does not 404 `/F8White.svg` against the host origin (the artifact inlines it; the SPA
  hashes it; index.html's dark favicon references the same file). Knock-on: the all-in-one
  container smoke curls `/F8Black.svg` now.
- **`nlAssist` landed as `"disabled" | "instance-only"`**, not the host-transport arm the spec
  once sketched (see the spec's non-goals for the re-derivation). Enforced in `generateChat`
  via `resolveNlConfig` - a persisted custom config cannot re-route an embed - and both NL
  panels plus `NlBackendConfig` render from the same resolution. Pinned by
  `tests/embed-nl-assist.test.tsx` (10 tests: transport, panels, defaults).
- **The smoke is a real consumer, not a static page**: `e2e-embed/host` is a tiny app depending
  on the package via `file:` (so the exports map, the `types` condition and peer resolution are
  what actually resolve), built with a stock vite and driven by `playwright.embed.config.ts`.
  Its one accommodation is `resolve.dedupe` for react: the `file:` symlink would otherwise
  resolve two React copies (invalid-hook-call #321), a topology a registry install cannot
  produce because the package ships `dist-lib` only. The spec asserts: mount, load-time canvas
  (sigma), scoped styles in BOTH directions (host body/`.panel` untouched, theme token on the
  scope root), the inlined logo, the monaco editor opening and closing, zero page errors and
  zero unexpected console errors, and an unmount that leaves the host region empty.
- **CI runs both** (the plan's "lib build is opt-in" stance was reversed when a host consumer
  materialized - an artifact nothing compiles rots silently): the `ui` job runs `build:lib`,
  the `e2e` job runs the embed smoke (it is the job with a browser installed).
- **The discoverability and architecture gates fired with this phase, as designed**: docs page
  `embed-studio` registered in the astro sidebar's F8 Studio group, the README key-features
  line, and the embedded-Studio client shape in both architecture diagrams.
- **Verify (done):** the full vitest suite green with the standalone default untouched; the
  embed smoke green end to end; docs build link-clean.

## Test strategy

- The existing vitest suite is the standalone-behavior baseline (must stay green every phase).
- Each phase adds targeted tests for its new seam (enumerated above), all asserting the default config
  reproduces current behavior.
- No engine/apiApp tests are affected (no backend change).

## Risks & mitigations

- **CSS scoping regressions** (Phase 4) are the main visual risk: land behind a visual diff of the
  standalone screens; scope with `:where()` (zero specificity bump) to avoid cascade surprises.
- **Async auth ripple** (Phase 2): `authHeaders` is synchronous today and is called from `apiRequest`,
  the change feed, and the export endpoints. A bearer provider is inherently async (tokens expire).
  Mitigation: resolve the token once per request at those choke points (`resolveAuthHeaders`), keep a
  sync path for `none`/`apiKey`, and do not fan the async change out into screens. The sync helper
  throws for bearer and a convention test keeps it out of every other module, so a new call site
  cannot silently reintroduce an unauthenticated path.
- **Config-scoped module state** (Phase 2): the config, the instance registry and the persisted keys
  are module-level, so a second SIMULTANEOUS mount would cross-bind two embeds. Mitigation: count
  live mounts and throw on the second (the non-goal, enforced rather than documented); sequential
  mounts are isolated by dropping per-mount memoized state and deriving persisted state from storage
  plus config alone.
- **Storage-key migration**: default namespace is empty, so existing users' `f8.*` keys are untouched;
  only host embeds with an explicit namespace get prefixed keys.
- **Router basepath** interacting with the canvas deep-links: covered by the Phase 3 basepath test.
- **Frozen canvas prop contract** (Phase 5): once exported, `CanvasNode`/`StyleConfig` shape changes
  become breaking API changes for hosts. Mitigation: keep the exported types minimal (what the
  component reads, not the whole workspace-store shape).
