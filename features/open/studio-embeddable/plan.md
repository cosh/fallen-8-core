# Plan - F8 Studio embeddable

Phased so each step is independently shippable and keeps the standalone app behaviorally identical
(default `StudioConfig` == today). Phases 1-5 are pure front-end seams; Phase 6 is packaging and only
lands when a host consumer exists. No engine or API changes are required.

## Phase 1 - Mount seam & config boundary

- Add `src/app/mount.tsx` exporting `mountStudio(el, config?)` and `<F8Studio config?>`. Move the
  provider tree (QueryClient + RouterProvider) here.
- Reduce `src/main.tsx` to `mountStudio(document.getElementById("root")!)` - the standalone entry with
  no config.
- Add `StudioConfigProvider` (React context) + `useStudioConfig()`; give it a `defaultStudioConfig`
  that equals today's behavior.
- **Verify:** existing tests green; add a test that `mountStudio(el)` renders the shell and seeds
  `SAME_ORIGIN_INSTANCE` exactly as `main.tsx` did.

## Phase 2 - Instance, credential & namespace injection

- Registry (`src/instances/registry.ts`) initializes from `StudioConfig.instances ?? [SAME_ORIGIN_INSTANCE]`
  and `activeInstanceId`. `lockInstances` hides the add/remove/connect affordances. Host-supplied
  instances are managed (never persisted), reusing the existing partialize/merge filter that already
  keeps the config.js-seeded default out of `localStorage`.
- Add the `bearer` auth arm (`{ kind: "bearer"; getToken(): Promise<string> }`). The token resolves
  at the transport choke points; the standalone kinds keep their synchronous fast path.
- Seed the per-instance active namespace from `StudioConfig.namespace`; `lockNamespace` hides the
  namespace switcher. The `GET /ns` support probe and pre-namespace degradation are unchanged.
- Namespace every `localStorage` key through a `storageKey(name)` helper that prepends
  `config.storageNamespace` (default `""`, so keys stay `f8.instances` etc.).
- **Verify:** instance-isolation + auth-header tests still green; add tests for a host-supplied
  instance, a bearer instance (token resolved, never persisted), a pinned/locked namespace, and a
  non-empty storage namespace.

## Phase 3 - Router basepath

- Thread `config.basepath` into `createRouter` (`src/app/routes.tsx`); default `""` (root, as today).
- Optionally support `history: createMemoryHistory()` when the host owns the address bar.
- **Verify:** routing tests green at default basepath; add a test that routes resolve under a non-empty
  basepath.

## Phase 4 - CSS scoping & theming

- Wrap Studio content in a `.f8-studio` root container; scope the generic-named
  `.panel/.btn/.input/...` primitives under it with `:where(.f8-studio)` (zero specificity
  change), so they neither leak into nor collide with the host DOM. The name-namespaced
  families (`.f8fr-*`, `.eclipse-highlight`) stay unscoped. Standalone wraps its `#root` in
  `.f8-studio`, pixel-identical. Tailwind's preflight stays global here: it cannot be
  import-scoped in plain CSS, and only the packaged library artifact (Phase 6) ever meets a
  host DOM - the scoped preflight is a packaging concern and moved there.
- Convert the `@theme` tokens (colors **and** the `--font-*` type stack) to CSS custom properties on
  `.f8-studio`, defaulting to today's values; `config.theme` overrides them. Drop the hard `html.dark`
  dependency (keep the dark defaults).
- Give `Dialog.Portal` a `container` = the Studio root so modals stay inside an embedded region.
- **Verify:** style-engine tests green; visual check that standalone is unchanged; add a test that a
  `config.theme` override reaches the tokens.

## Phase 5 - Canvas component export

- Freeze the `GraphCanvas` prop contract (`CanvasNode`, `CanvasEdge`, `StyleConfig`, `ElementRef`)
  as public API and export it as `F8GraphCanvas`; the component already has no store or query
  dependencies, so this is contract + naming work, not extraction work.
- Make its styles self-contained under the `.f8-studio` scope (it must render on a page that never
  loaded the app shell).
- **Verify:** a test renders `F8GraphCanvas` with literal node/edge data outside the app shell and
  asserts selection callbacks fire; existing canvas tests green.

## Phase 6 - Packaging (only when a host consumes it)

- Add a vite **library-mode** build target exposing `mountStudio` / `F8Studio` / `F8GraphCanvas`
  (the `src/embed/index.ts` surface), React as a peer dep, alongside the existing SPA build. CI
  keeps building the standalone SPA; the lib build is opt-in.
- Ship a scoped preflight with the library artifact (the standalone build keeps Tailwind's
  global preflight; see Phase 4).
- Add the `nlAssist` StudioConfig field and its transport wiring so the host can proxy or
  disable LLM calls.
- **Verify:** standalone `build:apiapp` output unchanged; a smoke test mounts the library build into a
  bare host page.

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
  Mitigation: resolve the token once per request at those choke points (or an async `authHeaders`
  with a sync fast path for `none`/`apiKey`); do not fan the async change out into screens.
- **Storage-key migration**: default namespace is empty, so existing users' `f8.*` keys are untouched;
  only host embeds with an explicit namespace get prefixed keys.
- **Router basepath** interacting with the canvas deep-links: covered by the Phase 3 basepath test.
- **Frozen canvas prop contract** (Phase 5): once exported, `CanvasNode`/`StyleConfig` shape changes
  become breaking API changes for hosts. Mitigation: keep the exported types minimal (what the
  component reads, not the whole workspace-store shape).
