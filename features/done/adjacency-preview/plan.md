# Adjacency preview — plan

Phases are ordered so each lands green (vitest + `tsc -b`) on its own.

## Phase 1 — shared data layer

- `src/lib/neighborhood.ts`:
  - `fetchVertexNeighborhood(instance, vertexId, { cap, skipNeighborIds })` — extracted
    from `CanvasScreen`'s expand mutation verbatim (per-property id lists,
    `edgePropertyId` attribution map, edge + neighbor hydration, per-request
    `catch(() => null)`), returning `{ vertices, edges, truncated }`.
  - `fetchEdgeNeighborhood(instance, edge, { cap })` — endpoint vertices +
    id-list intersection both directions + hydration of the intersection only; the focus
    edge always present in the result.
  - `PREVIEW_EDGE_CAP = 60`, expand keeps 200.
- `src/state/instanceStore.ts`: extract pure `buildCanvasModel(vertices, edges, base?)`;
  `mergeIntoCanvas` delegates to it.
- `src/screens/CanvasScreen.tsx`: expand mutation calls `fetchVertexNeighborhood`.
- Tests: `tests/neighborhood.test.ts` (attribution, self-loop dedup, cap/truncated,
  failed-hydration skip, intersection correctness incl. reverse direction, focus-edge
  guarantee, canvas-model stubs/labels/prop snapshots).

## Phase 2 — render layer

- `src/canvas/styleEngine.ts`: `PathOverlaySets.dim`; `dimmed = active && dim && !inPath`.
- `src/canvas/GraphCanvas.tsx`: overlay memo builds `dim: true` from `pathOverlay`,
  `dim: false` from the new optional `emphasis` prop.
- `src/canvas/Canvas2D.tsx`: register `EdgeCurveProgram`/`EdgeCurvedArrowProgram`;
  after edge sync, `indexParallelEdgesIndex` + curvature assignment so parallel edges
  fan out; non-parallel edges keep the straight arrow/line programs.
- New dependency: `@sigma/edge-curve`.
- Tests: extend `tests/style-engine.test.ts` for the `dim` flag (emphasis keeps colors,
  still bumps size/z-index; `dim: true` reproduces the pinned overlay behavior).

## Phase 3 — preview UI + navigation

- `src/components/NeighborhoodPreview.tsx`: `useQuery` over the phase-1 fetchers, canvas
  model via `buildCanvasModel` (focus vertex seeded from the already-loaded element),
  fixed preview `StyleConfig` (2D force, arrows + labels on), emphasis on the focus
  element, truncation badge, edge-count caption (edge mode), loading/error states,
  `onSelect` → `onInspect(id)`.
- `src/components/InspectLink.tsx`: callback contract, router import removed.
- `src/components/AdjacencyPanel.tsx`: graph|stats toggle (graph default), threads
  `onInspect`.
- `src/components/ElementDetail.tsx`: `onInspect` prop for endpoint links.
- `src/screens/BrowserScreen.tsx`: single `inspect(id)`; edge lookups render the
  neighborhood panel in the right column.
- Tests: `tests/neighborhood-preview.test.tsx` (mocked `GraphCanvas`: model contents,
  emphasis, click-to-inspect, truncation badge, edge caption); update
  `tests/browser-element.test.tsx` (toggle default + stats assertions behind a click,
  endpoint links now trigger the in-screen lookup, edge lookup shows the panel).

## Phase 4 — docs + verification

- `npm run test:ui`, `npm run build` (tsc) green; `dotnet test` untouched by design but
  run once for the convention gates (file headers).
- Move `features/open/adjacency-preview/` → `features/done/adjacency-preview/`.
