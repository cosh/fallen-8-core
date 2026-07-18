# Plan — F8 Studio embeddable

Phased so each step is independently shippable and keeps the standalone app behaviorally identical
(default `StudioConfig` == today). Phases 1–4 are pure front-end seams; Phase 5 is packaging and only
lands when a host consumer exists. No engine or API changes are required.

## Phase 1 — Mount seam & config boundary

- Add `src/app/mount.tsx` exporting `mountStudio(el, config?)` and `<F8Studio config?>`. Move the
  provider tree (QueryClient + RouterProvider) here.
- Reduce `src/main.tsx` to `mountStudio(document.getElementById("root")!)` — the standalone entry with
  no config.
- Add `StudioConfigProvider` (React context) + `useStudioConfig()`; give it a `defaultStudioConfig`
  that equals today's behavior.
- **Verify:** existing tests green; add a test that `mountStudio(el)` renders the shell and seeds
  `SAME_ORIGIN_INSTANCE` exactly as `main.tsx` did.

## Phase 2 — Instance/credential injection

- Registry (`src/instances/registry.ts`) initializes from `StudioConfig.instances ?? [SAME_ORIGIN_INSTANCE]`
  and `activeInstanceId`. `lockInstances` hides the add/remove/connect affordances.
- Namespace every `localStorage` key through a `storageKey(name)` helper that prepends
  `config.storageNamespace` (default `""`, so keys stay `f8.instances` etc.).
- Document the OIDC/JWT path: a host token provider becomes an `apiKey`-shaped auth entry (or a future
  `oidc` union arm), with `authHeaders` already the single consumer.
- **Verify:** instance-isolation + auth-header tests still green; add tests for a host-supplied instance
  and a non-empty storage namespace.

## Phase 3 — Router basepath

- Thread `config.basepath` into `createRouter` (`src/app/routes.tsx`); default `""` (root, as today).
- Optionally support `history: createMemoryHistory()` when the host owns the address bar.
- **Verify:** routing tests green at default basepath; add a test that routes resolve under a non-empty
  basepath.

## Phase 4 — CSS scoping & theming

- Wrap Studio content in a `.f8-studio` root container; scope the Tailwind preflight and the
  `.panel/.btn/.input/...` primitives under it (Tailwind v4 `@layer` / `:where(.f8-studio)`), so they
  neither leak into nor get reset by the host DOM. Standalone wraps its `#root` in `.f8-studio` →
  pixel-identical.
- Convert the `@theme` hex tokens to CSS custom properties on `.f8-studio`, defaulting to today's
  values; `config.theme` overrides them. Drop the hard `html.dark` dependency (keep the dark defaults).
- Give `Dialog.Portal` a `container` = the Studio root so modals stay inside an embedded region.
- **Verify:** style-engine tests green; visual check that standalone is unchanged; add a test that a
  `config.theme` override reaches the tokens.

## Phase 5 — Packaging (only when a host consumes it)

- Add a vite **library-mode** build target exposing `mountStudio` / `F8Studio`, React as a peer dep,
  alongside the existing SPA build. CI keeps building the standalone SPA; the lib build is opt-in.
- Add `nlAssist` transport wiring (Phase 2 config) so the host can proxy or disable LLM calls.
- **Verify:** standalone `build:apiapp` output unchanged; a smoke test mounts the library build into a
  bare host page.

## Test strategy

- Reuse the existing 265 vitest tests as the standalone-behavior baseline (must stay green every phase).
- Each phase adds targeted tests for its new seam (enumerated above), all asserting the default config
  reproduces current behavior.
- No engine/apiApp tests are affected (no backend change).

## Risks & mitigations

- **CSS scoping regressions** (Phase 4) are the main visual risk → land behind a visual diff of the
  standalone screens; scope with `:where()` (zero specificity bump) to avoid cascade surprises.
- **Storage-key migration**: default namespace is empty, so existing users' `f8.*` keys are untouched;
  only host embeds with an explicit namespace get prefixed keys.
- **Router basepath** interacting with the canvas deep-links → covered by the Phase 3 basepath test.
