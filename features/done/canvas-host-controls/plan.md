# Canvas host controls - implementation plan

Status: all phases done. Spec: [spec.md](spec.md).

## Phase 1 - the magnitude contract

- `src/canvas/styleConfig.ts`: six optional fields on `StyleConfig`, with a block comment stating
  why they exist and why they are deterministic. `DEFAULT_STYLE_CONFIG` deliberately omits them, and
  says so at the point a reader would otherwise add them.
- `src/canvas/styleEngine.ts`: `LABEL_SIZE_DEFAULT` (11) and `EDGE_LABEL_SIZE_DEFAULT` (9) join the
  exported defaults; `ResolvedMagnitudes` + `resolveMagnitudes(config)` is the ONE defaulting site;
  `isUsableMagnitude` is the one positivity rule, exported because the camera handle needs it too.
- `resolveStyles` calls it once and hangs the result on `ResolvedStyles.magnitudes`, so the
  renderers read resolved numbers. `ResolvedStyles` is internal to both embed entries, so adding a
  field is not a host-visible change.
- `fallbackWithin` decides what a scaled mode draws for an element it cannot measure: an explicitly
  configured scalar verbatim, a defaulted one pulled into the configured range. With neither set this
  is `clamp(5, [3, 14]) = 5`, so nothing moves.

## Phase 2 - the DOM-free geometry

`src/canvas/eclipse.ts` widens from "the hover corona's geometry" to "the numeric models of what
sigma and 3d-force-graph actually draw", kept out of the renderers so all of it is unit-tested
without a WebGL context. Its docblock states the new remit and the rule that fidelity to the
library beats geometric elegance.

Added: `sizeToVal` and `SPRITE_SCALE_PER_PX` (moved out of `Canvas3D`, so a test can reach them
without importing three.js), `NODE_REL_SIZE`, `DEFAULT_FOV`, `FIT_PADDING_PX`, `paddingCeiling`,
`clampFitPadding`, `fitDurationMs`, `isUsableCameraRatio`, `fitCameraRatio` (2D), `graphFitDistance`
and `scaleCameraDistance` (3D).

Every third-party formula was read out of the vendored source before being reproduced, and the
disagreements are commented where they sit: `fitToBbox` measures its box from the ORIGIN and divides
by `atan(paddedFov)` rather than `2*tan(paddedFov/2)`, so `graphFitDistance` does too, which is why
it disagrees with `perspectiveScreenRadius` in the same file.

## Phase 3 - the renderers

- `Canvas2D`: label sizes from `styles.magnitudes` at construction, plus a `setSettings` effect so a
  later change lands without a remount, skipped while they still match what the constructor was given.
  `useImperativeHandle` implements the handle, routing every ratio write through `animate` so a running
  fit cannot swallow it. A `ResizeObserver` on the container calls `scheduleRefresh` and nothing else.
- `Canvas3D`: `sizeToVal`/`SPRITE_SCALE_PER_PX` now imported; one `fitNow` callback is the only fit
  path, shared by the mount auto-fit, the resize refit and the handle, so all three re-measure and
  clamp identically; `fitDistance` derives the ratio denominator from the live node cloud against the
  same clamped default inset; `camera.fov ?? 50` became `?? DEFAULT_FOV`. Its existing observer gained
  the refit, guarded by the orbit controls' first `start` event.
- `GraphCanvas`: owns `F8GraphCanvasHandle` and `FIT_DURATION_MS` (it is already the renderer
  boundary and the source of `ElementRef`), and forwards `ref` to whichever renderer is mounted.
- `F8GraphCanvas`: delegates through `useImperativeHandle` rather than handing out the inner
  renderer's handle, because switching `config.renderer` swaps 2D for 3D underneath and a host
  holding the inner object would be holding a dead one. It holds no resize logic: see the spec's
  resize decision and the defect that removed it.

## Phase 4 - packaging

- `package.json`: `@fallen-8/studio`, no longer private, `version` a `0.0.0` placeholder the release
  job overwrites from the tag (MinVer's rule, applied to npm), `publishConfig` with
  `provenance: true` (which also makes a local publish refuse), repository/homepage/bugs metadata,
  and a `./canvas` subpath. A `LICENSE` copy so the tarball carries the licence text.
- `src/embed/canvas.ts` is the ONE home for the canvas surface; `src/embed/index.ts` re-exports it
  wholesale, so the two entries cannot drift.
- `vite.lib.config.ts`: two lib entries. Verified: `canvas.js` pulls one 372 kB chunk with zero
  editor code, against the 5.4 MB shell chunk.
- `scripts/check-lib-artifact.mjs`: existence is now driven BY the exports map, so a new subpath is
  checked without touching the script; and a new tripwire walks the emitted chunk graph from each
  entry and fails the build if the canvas entry reaches the editor. The shell entry is asserted to
  CONTAIN the markers, without which a stale marker would make the check a false green.
- `.github/workflows/release.yml`: an `npm` job on the same test gate, versioned from the tag, that
  skips with a log line when no credential exists or the version is already published.
- The embed host fixture consumes `@fallen-8/studio/canvas` for the canvas, so CI proves the subpath
  resolves through a real bundler.

## Phase 5 - tests

Test doubles were consolidated first, because the alternative was adding the same method to three
hand-rolled fakes: `tests/fakeSigma.ts` is the one Sigma double (now with a recording camera) and
`tests/resizeObserver.ts` the one `ResizeObserver` fake, installed globally from `setup.ts` since
jsdom has neither. That removed roughly 90 lines of duplication across four files.

- `tests/style-engine.test.ts`: `resolveMagnitudes` defaults and hardening; the pixel-identity table;
  configured values reaching resolved sizes; the range clamp; path minimums flooring a small
  configuration and not shrinking a large one; the fallback rule in both directions; and a configured
  `nodeSize` carried through `resolveStyles` into the 3D val, which is the assertion that fails if the
  anchor is ever re-based.
- `tests/eclipse.test.ts`: the size anchor; `fitCameraRatio` at 390/1440/3840 including the
  exactly-1 default, the clamp that would otherwise produce a negative ratio, and the non-finite
  guard; `graphFitDistance` against a transcription of `fitToBbox` as an oracle, plus its
  degenerate cases; `scaleCameraDistance`; and the input guards.
- `tests/canvas-host-controls.test.tsx`: label sizes at construction and live; the handle attached
  and released; the fit's camera state, duration floor and angle reset; the ratio getter and the
  refused setter; and the container observer scheduling a refresh, leaving the camera alone, and
  disconnecting on unmount.

`tests/fakeForceGraph.ts` is the third and last shared double, added once review pointed out that
Canvas3D's wiring had no component coverage at all. It matters that its `zoomToFit` reproduces
three-render-objects' OWN `fitToBbox` arithmetic and moves the camera where the real library would:
that is what makes "ratio 1 right after a default fit, at 1440, 390, 3840 and 140 px" a genuine
comparison between the component and the library rather than the component checked against itself.
That single test is what caught the clamp mismatch, reproducing the 0.718 the reviewer predicted.

## Verification performed

- `npx tsc -b` clean; 948 unit tests over 85 files pass (69 of them new).
- **Pixel identity proven by measurement, not by assertion**: the pre-change resolver was RUN over a
  fixed graph across all five size modes with and without the path overlay, the output saved, and
  the post-change output diffed against it. Identical, re-checked after every subsequent edit.
- **Every new tripwire was mutation-checked**: changing `NODE_SIZE_DEFAULT` fails 6 tests; neutering
  the `ResizeObserver`, the camera-ratio guard and the duration floor fails exactly the 4 tests
  written for them; making the canvas entry import `mountStudio` fails the artifact check with exit 1.
  Each of the six review fixes was reverted in turn and the test written for it failed, including the
  3D clamp mismatch reproducing 0.718 exactly.
- The embed smoke was re-run from a DELETED fixture `node_modules`, which is what proves the
  artifact is self-contained now that the package declares no runtime dependencies.
- Library build, artifact check, and the embed host fixture build all green.
- Docs site builds with all internal links valid.
- Third-party arithmetic confirmed against vendored source rather than memory: `sigma.esm.js`
  (`matrixFromCamera`, `animatedReset`, `setSettings`, `scaleSize`, the tween's division),
  `three-forcegraph.mjs` (`valAccessor(node) || 1`), `three-render-objects.mjs` (`fitToBbox`).
  `graphFitDistance`'s extent term was additionally checked against the library's own expression
  over 200,000 random node clouds: worst absolute difference 0.
- `tools/browser-probe` deliberately not run: it exercises the C# engine as browser-wasm and nothing
  here touches the engine.
