# Canvas host controls - make the graph canvas consumable at any viewport size

Status: implemented and merged.

## Problem

`F8GraphCanvas` is the component-level embed (feature `studio-embeddable`), and a host's only
control surface over it is the `StyleConfig` object it passes plus the CSS box it renders into.
That surface exposed *modes* (`nodeSizeMode`, `edgeWidthMode`, ...) but no *magnitudes*: every
visual size was a module constant.

Sigma node `size` is a radius in absolute px, and `autoRescale` fits node *positions* to the
container while sizes stay absolute. So the wider the box, the smaller the graph reads. Measured
on a live page with the same 8-vertex / 12-edge graph:

| container | node radius | label | radius as a fraction of width |
| --- | --- | --- | --- |
| 1440 x 601 | 5 px | 11 px | 1 / 288 |
| 3827 x 1164 | 5 px | 11 px | 1 / 765 |

At 3827 px the graph is a hairline star with unreadable labels in a black field; at 1440 the same
code and data read correctly. The only workaround available to a host was capping the container
width in CSS, which throws away screen an engaged visitor could use.

Two further gaps had the same root cause, a host having no reach into the renderer:

- **No imperative handle.** Nothing could fit, zoom or re-centre the camera. `Canvas3D` called
  `zoomToFit` exactly once, when the graph went from empty to non-empty.
- **Not a package.** The library artifact existed but `package.json` was `private`, so consumers
  sparse-checked-out a pinned tag and *copied* `src/` into their own tree.

## Decisions

- **Optional fields, and omission must be byte-identical.** `F8GraphCanvasProps` is a frozen
  contract because hosts pin a tag and copy the source. Optional magnitude fields whose omission
  reproduces the previous rendering exactly do not break it, and that property is the requirement,
  proven by a before/after table captured by RUNNING the pre-feature resolver (see plan, phase 5).
- **The exported constants stay, and become host-facing.** `NODE_SIZE_DEFAULT` and friends are the
  documented defaults, so they are exported all the way out to the canvas entry: a host scaling to
  its container needs to know what it is scaling from.
- **Deterministic, never implicit.** Nothing scales itself with the viewport or the device pixel
  ratio. A host gets knobs and `fitToView`, and decides; the same config always renders the same
  picture.
- **One home for defaulting.** `resolveMagnitudes(config)` in `styleEngine.ts` resolves all six
  fields once and rides on `ResolvedStyles.magnitudes`. No renderer writes a fallback expression.
- **Magnitudes are NOT baked into `DEFAULT_STYLE_CONFIG`.** `instanceStore` seeds the persisted
  per-instance style config from it and rehydrates with the persisted copy WINNING, so a magnitude
  spelled there would be frozen into every existing workspace's local storage and would outrank the
  documented default forever. That is a data-migration hazard, not a style choice.
- **An explicit fallback is honoured; only a defaulted one follows the range.** `nodeSize` doubles
  as what a scaled mode draws for an element it cannot measure. A host that names a number gets that
  number, because that is what the field documents, even outside `nodeSizeRange`. A host that sets
  only a RANGE gets a fallback pulled inside it, rather than its property-less elements drawn at the
  stock 5 and off the bottom of its own scale. With neither set this is clamp(5, [3, 14]) = 5.
- **A magnitude must be a finite number above zero.** Hosts call in from plain JavaScript, where 0,
  -1 and NaN arrive without a type error, and each is a divisor downstream. Zero matters most:
  sigma draws no node at all, while `three-forcegraph` reads a falsy `nodeVal` as 1 and draws a
  DEFAULT-sized sphere, so honouring it would split the two renderers. An unusable value falls back.
  An INVERTED range is honoured as given: "larger value, smaller element" is a legitimate ask, and
  sorting it silently would invert the host's intent.
- **The 3D size anchor is the DEFAULT node size, not the configured one.** `sizeToVal` divides by
  `NODE_SIZE_DEFAULT` because that divisor is the constant of proportionality between world units
  and 2D pixels, not "the size a node happens to have". Dividing by the configured value would
  normalise the knob away: `nodeSize: 20` would render in 3D exactly like the default while 2D grew
  fourfold. This contradicts the originating request, which asked to "derive it from the resolved
  value"; the acceptance criterion "3D radii stay proportional to 2D" wins. The anchor has two
  sites, not one: the image sprite's `1.6` is the same anchor seen from the sprite side, so both now
  derive from it.
- **The ratio means the same thing in both renderers, and is derived, never stored.** 1 means "the
  graph just fits the renderer's own inset"; 2 is twice as far out. 3D derives it by reproducing
  `three-render-objects`' `fitToBbox` verbatim (origin-anchored extent, `atan(paddedFov)` rather
  than the geometrically correct `2*tan(paddedFov/2)`), so the number is 1 exactly where
  `zoomToFit` parks. Remembering a fitted distance was rejected: Studio's expand-on-demand merges
  would stale it immediately, breaking "1 means fits" precisely when it matters.
- **The ratio is measured against the renderer's DEFAULT frame in both.** 2D cannot re-base, because
  its number IS sigma's `camera.ratio`, whose reference is the live `stagePadding` that `fitToView`
  deliberately never writes. So 3D does not remember the last fit's inset either. The corollary is
  documented where it matters: a host that fits with a custom inset leaves the ratio off 1 and owns
  its own resize policy from then on, which is the safe direction to fail.
- **2D padding is a zoom, not a `stagePadding` write.** Omitting `paddingPx` yields a ratio of
  exactly 1, so the default path is a plain camera reset and cannot move a pixel. It also stays
  transient camera state instead of mutating a persistent setting and forcing an O(N+E) re-index.
  The consequence is documented rather than designed away: a camera ratio also divides element sizes
  by `sqrt(ratio)` where `stagePadding` would not, so asking for margin zooms out, exactly as a
  user's wheel-zoom does.
- **Each renderer owns its own resize policy, and neither resets a camera.** 2D re-measures and
  repaints (`scheduleRefresh`, the same call sigma's own window handler makes), which re-frames a
  graph whose camera is untouched and leaves a panned or zoomed one alone. 3D genuinely needs a
  re-fit, because its fit distance depends on the aspect ratio and on a field of view measured
  against height, so it re-fits until its orbit controls report the first interaction. Nothing lives
  on the embed wrapper: only the renderer can tell whether the visitor has taken the camera.
- **The published package has no dependencies at all beyond the React peers.** The lib build bundles
  sigma, graphology, three and the editor, so a consumer needs none of them; they sit in
  `devDependencies`, which is how a bundled library says "build-time only". Left under
  `dependencies` they would have made `npm install @fallen-8/studio` download tens of megabytes of
  exactly the code the `./canvas` subpath exists to avoid.
- **One package, not a second one.** `fallen-8-web-ui` is published as `@fallen-8/studio` with a
  `./canvas` subpath, rather than adding a separate `@fallen-8/graph-canvas`. A second package would
  duplicate an artifact that already exists and, by externalising sigma/graphology/three, would
  break the shipped artifact's self-contained promise. `react`/`react-dom` remain the only peers.

## Corrections to the request

Recorded because the originating request asserted them and the code deliberately does otherwise.

1. **"A `ResizeObserver`-driven `fitToView` alone would fix the defect."** False for the 2D
   renderer, where the defect was measured. Rendered node radius is `size / sqrt(camera.ratio)`
   with no viewport term, and the fit ratio is scale-invariant under a uniform container scale, so
   a refit cannot make radius track container width; `labelSize` is used verbatim with no camera
   term at all, so no camera operation can change it. It also collides with "do not make sizes
   viewport-dependent". The observer is still worth having for a DIFFERENT reason: sigma binds only
   a window `resize` listener, so a container-only reflow (a Studio panel collapsing) leaves its
   canvases at a stale pixel size. `Canvas2D` therefore observes its container and calls
   `scheduleRefresh`, which re-measures and rebuilds the matrix while leaving the camera alone: a
   user who merely opened a panel is not yanked back to a fit. The defect itself is fixed by the
   magnitudes.
2. **"Derive the `sizeToVal` divisor from the resolved value."** Backwards; see Decisions.
3. **The observer cannot live only in `F8GraphCanvas`.** Studio's screens render `GraphCanvas`
   directly, so an embed-level observer would leave the motivating collapsing-panel case unfixed.
   The renderer owns the refresh; the embed wrapper additionally refits for the host-layout case a
   host cannot reach itself.

## Functional requirements

- **FR-1 Magnitudes.** `StyleConfig` gains optional `nodeSize`, `nodeSizeRange`, `edgeWidth`,
  `edgeWidthRange`, `labelSize`, `edgeLabelSize`. `resolveStyles` honours them; `Canvas2D` passes
  the label sizes into Sigma's settings at construction AND keeps them live via `setSettings`, so a
  host raising `labelSize` needs no remount.
- **FR-2 Omission is identical.** With no magnitude configured, every resolved size and width, in
  every size mode, with and without the path overlay, equals what the pre-feature code produced.
- **FR-3 Both renderers move together.** A configured `nodeSize` changes 2D radii and 3D sphere
  radii proportionally; a configured `labelSize`/`edgeLabelSize` changes 2D label text.
- **FR-4 Path minimums still win.** `PATH_NODE_MIN_SIZE` / `PATH_EDGE_MIN_WIDTH` floor a highlighted
  path however small the configured magnitudes, and never SHRINK one configured larger.
- **FR-5 Imperative handle.** `F8GraphCanvasHandle` (`fitToView(durationMs?, paddingPx?)`,
  `getCameraRatio()`, `setCameraRatio(ratio)`) is implemented by both renderers and reachable
  through `F8GraphCanvas`'s `ref`. `fitToView` frames the whole graph at container widths of 390,
  1440 and 3840 px.
- **FR-6 Hostile input cannot break a canvas.** A non-finite or zero duration never reaches sigma's
  tween (which divides by it); a fit inset is clamped below the fraction of the axis that sends
  either renderer's fit to Infinity or negative; a camera ratio of 0, negative or NaN is refused.
- **FR-7 Re-frame on resize.** A container reflow re-measures and repaints both renderers. 2D never
  moves the camera; 3D re-fits until its orbit controls report a first interaction, and never after.
  A fit also re-measures its container first, so a host may fit straight from its own layout handler.
- **FR-8 Published package.** `@fallen-8/studio` and `@fallen-8/studio/canvas`, with `react` and
  `react-dom` as the only peers, published from the release tag with a provenance attestation. The
  canvas subpath must not reach the code editor's chunk graph, enforced by the artifact check.

## Non-goals

- No style-panel UI for the magnitudes: they are a host contract, and Studio renders unchanged (so
  no screenshot is affected).
- No viewport- or DPR-derived sizing, no "auto" mode.
- No per-axis exact padding in 2D: the inset is exact on the binding axis, and matching both axes
  would mean re-implementing sigma's correction ratio for at most a few px.
- No change to any existing `StyleConfig` field, `CanvasNode`, `CanvasEdge` or `ElementRef`.
- No `npm publish` from a developer machine: `publishConfig.provenance` makes releases CI-only.

## Impact on existing features

- **`studio-embeddable`** owns the embed contract this extends. Its spec/plan are the historical
  record and are not rewritten; the living contract is the docs page, which grew a sizing section
  and a camera section.
- **`canvas-view-controls`** is about the working SET (clear, show whole graph), not the camera.
  Untouched, and its button cluster is where a future "Fit view" control would sit.
- **`canvas-find-connect`** shares `eclipse.ts`. The hover corona is unchanged, but the module's
  remit widened to all DOM-free canvas geometry and its docblock says so; `worldRadiusForVal` now
  names `NODE_REL_SIZE` instead of inlining 4.
- **Studio UI**: renders identically (FR-2), so no screenshot is stale. Behaviour changes only on
  container resize, where it strictly improves: a collapsing panel used to leave sigma's canvases
  at a stale pixel size.
- **Pre-existing bug fixed in passing**: `Canvas3D`'s mount auto-fit passed 60 px straight into
  `zoomToFit`, whose padding is an unguarded divisor, parking the camera at Infinity in a container
  exactly 120 px tall and silently doing nothing below that. Both fit paths now share one clamp.
- **REST / OpenAPI / MCP / provider descriptors / engine**: no change. This feature is entirely
  `fallen-8-web-ui` plus the release workflow, so the OpenAPI snapshot, `McpRestCoverageTest` and
  the provider-descriptor snapshot are unaffected.
- **`tools/browser-probe`**: unaffected. It runs the C# engine as browser-wasm; nothing here touches
  the engine.
- **NL-assist dataset/eval**: unaffected, no prompt or contract surface changed. No `RETRAIN-LOG`
  entry needed.
- **Release pipeline**: `release.yml` gains an `npm` job. It is gated on the same tests as the
  others and skips with a log line while no credential exists, so it cannot redden a release that
  published everything else.

## Defects found in review and fixed

An adversarial review of the finished diff found these, all now fixed and each pinned by a test that
was checked to FAIL without its fix:

- **The 3D ratio disagreed with the 3D fit in a short container.** The fit clamped its inset against
  the live height while the ratio's denominator used the raw default, so `getCameraRatio()` returned
  about 0.72 for a perfectly fitted graph in a 140 px-tall box, and the two renderers stopped
  agreeing on what 1 means. Both now clamp identically.
- **The embed wrapper's resize refit destroyed a pan.** Its guard asked whether the camera ratio was
  still 1, which reads as "nobody has touched it" and is not: sigma's drag handlers write x and y and
  never touch the ratio, so a visitor who had panned was silently recentred on the host's next
  reflow. The observer is gone; see the resize decision above.
- **`setCameraRatio` could be silently swallowed.** A fit tween keeps its own start-state snapshot
  and interpolates over any `setState`, so a host driving a slider during a 600 ms fit lost its
  write. Every ratio write now goes through `animate`, which is the only public way to cancel a
  running tween.
- **A 3D fit used the pre-resize box.** Nothing re-measured the container at fit time, so a host
  fitting from its own layout handler framed the box it had just left. 2D already called
  `sigma.resize()`; 3D now pushes the live size in first.
- **The mount re-applied the constructor's own label sizes**, paying a second full re-index for no
  visual change.
- **The published package declared 15 bundled dependencies** (see the packaging decision above).
- **Four documentation claims were false**: that extreme camera ratios clamp in 2D (nothing clamps
  them), that a ratio of k scales elements by 1/k (it is 1/sqrt(k)), that the package installs no
  transitive weight (it did), and that it is on the registry today (publishing is wired but not yet
  switched on). Plus one duplicated JSDoc block that no tooling would ever have shown.

## Follow-ups deliberately not done

- A "Fit view" button in the Studio canvas toolbar. The handle makes it a small addition, but it is
  a UI change with screenshot consequences and no user has asked for it.
- A canvas-only stylesheet. The `./canvas` subpath splits the JavaScript, not the CSS, so a
  canvas-only host still loads the whole scoped Studio stylesheet. Documented as a boundary.
