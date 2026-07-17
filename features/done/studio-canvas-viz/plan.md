# Plan — studio-canvas-viz

Everything lives in `fallen-8-web-ui`. New dependencies: `@sigma/node-image`
(image nodes for Sigma v3), `3d-force-graph` + `three` (3D renderer; lazy-loaded so
the 2D-only bundle does not pay for three.js).

## Phase 1 — properties on the canvas model + style config

- `state/instanceStore.ts`: `CanvasNode`/`CanvasEdge` gain `props: Record<string,
  string | number | boolean>` (scalars only, strings capped — FR-11);
  `mergeIntoCanvas` fills it from the REST DTOs. Add `styleConfig` +
  `setStyleConfig(patch)` with defaults deep-merged on rehydrate (same pattern as
  `pathDraft`).
- `canvas/styleConfig.ts`: `StyleConfig` type + `DEFAULT_STYLE_CONFIG`
  (2D, force, label colors, fixed sizes, no images, labels on, arrows off).

## Phase 2 — pure style engine

- `canvas/styleEngine.ts`: `resolveStyles(nodes, edges, overlay, config)` →
  per-element `{ color, size, image, zIndex, dimmed }` / `{ color, width, zIndex }`.
  Contains: value→color hashing (generalized from `colorForLabel`), numeric-set
  detection + gradient, min-max scaling for sizes/widths, visible-degree counting,
  image value classification (url vs emoji/text vs none), overlay precedence (FR-9).
- `canvas/imageAssets.ts`: emoji/text → cached data-URL texture (DOM canvas; kept out
  of the pure engine so the engine stays unit-testable under jsdom).
- Unit tests `tests/style-engine.test.ts` pinning FR-1..FR-5 mapping rules, overlay
  precedence, missing-property defaults, pre-feature nodes without `props`.

## Phase 3 — 2D renderer upgrades

- `canvas/GraphCanvas.tsx` becomes the thin boundary: resolves styles, picks the
  renderer. Sigma code moves to `canvas/Canvas2D.tsx` and consumes resolved styles;
  registers the image node program; honors label/arrow toggles via Sigma settings;
  layouts force/circular/circlepack/grid/random (FR-6).

## Phase 4 — 3D renderer

- `canvas/Canvas3D.tsx` (`React.lazy`): `3d-force-graph` with the same props as
  Canvas2D — resolved styles, node/edge/background click → `onSelect`, path overlay,
  images/emoji as three.js sprites, dag layouts, arrow toggle. Node identity is kept
  across data refreshes so positions don't reset on merge.

## Phase 5 — UX

- `canvas/StylePanel.tsx`: sectioned panel (FR-8) fed by known property keys from the
  canvas snapshot (datalist + free text). `screens/CanvasScreen.tsx`: mount panel,
  move the layout picker into it, legend follows color mode (FR-10).
- `lib/fieldHelp.ts`: help entries for every new control; update `canvasLayout`.

## Phase 6 — docs & gates

- `README.md` in this feature dir: usage, including the image/emoji property contract.
- `npm run test`, `npm run build` green; e2e canvas scenario still passes.
