# Canvas interact - plan

Studio-UI-only feature (no engine/REST/MCP/OpenAPI change), phased so each phase leaves the
web-ui gates green (`tsc -b`, vitest via the `cmd /v:on /c "... & echo EXIT=!ERRORLEVEL!"`
wrapper - the PowerShell wrapper's exit code lies). Pure logic lands before the panel; cheap
filters before costly ones.

Workflow: feature CODE on a `feature/canvas-interact` branch off `main`; review/council gate
before merge.

## Phase 1 - Pure logic + store + consolidation

**Goal:** every non-DOM decision implemented and unit-tested; the Detail panel's expand runs
through the new primitive with zero behaviour change.

- `src/lib/canvasInteract.ts` (new; MIT header):
  - Constants, one tuning home: `DEGREE_SWEEP_CAP = 1_000`, `EXPAND_SWEEP_CAP = 100`,
    `SEMANTIC_K = CANVAS_ELEMENT_CAP`, batch size reused or mirrored from Connect.
  - `CheapFilters` type + `matchCheap(nodes: CanvasNode[], edges: CanvasEdge[], filters):
    CanvasNode[]` - label exact, property key-present / contains-term (case-insensitive,
    stringified snapshot value), and on-canvas degree (delegating to the style engine's
    exported `visibleDegrees` - one home for that counting); stubs (no props) match no label or
    property filter, and an on-canvas degree filter reads their loaded edges like any other
    vertex's.
  - `applyDegree(scores: Map<number, number>, op, value): Set<number>` and
    `degreeSweep(instance, ids, { direction, signal, onProgress }): Map<number, number>` -
    the DATABASE source: in/out/total via `getInDegree`/`getOutDegree`, batched, abortable.
  - `applySemantic(result: VectorSearchResultREST, direction, threshold)` -> matched ids +
    unscored count, oriented by `higherIsBetter`; a vertex absent from the result never matches.
  - `expandVertices(instance, ids, { skip, signal, onProgress, onMerge, elementBudget })` -
    per-id `fetchVertexNeighborhood` (existing `EXPAND_EDGE_CAP`), batched, merges per batch,
    stops at the element budget and reports how far it got; cancel keeps what landed.
- `CanvasScreen.tsx`: the inline expand mutation becomes `expandVertices` over `[selected.id]`.
  No visible change; existing canvas tests must pass unmodified.
- `src/state/instanceStore.ts`: extend `CanvasToolsDraft` + `DEFAULT_CANVAS_TOOLS_DRAFT` with
  the interact fields (spec §6), merge-defaulted in persist `merge`, no migration.

**Tests** (`tests/canvas-interact.test.ts`, new; thorough per the quality bar): matchCheap
(label exact vs stub-null, key-present vs term-contains, 200-char snapshot cap boundary, AND
composition, empty filters = all, on-canvas degree per direction over the loaded edges with a
never-expanded vertex reading 0); applyDegree (over/under, boundary equality excluded per the
chosen comparator semantics); applySemantic (higherIsBetter both ways, unscored never matches
either direction, unscored count); degreeSweep (batching, abort, per-direction sums, failed
fetch handling); expandVertices (skip set honored, budget stop reports position, cancel keeps
merged batches, over-cap refusal is the caller's job and stated in the doc comment); store
draft merge-defaults.

## Phase 2 - Interact tab with cheap filters + both actions

**Goal:** the fourth tab works end to end for label/property filters, including expand-all.

- `src/canvas/InteractPanel.tsx` (new): filter rows (label datalist from graph shape + canvas
  labels, property key/term, degree with the source toggle - only the `on canvas` source is
  live in this phase, the `database` source arrives with Preview in phase 3), live match count
  line, `Remove from view (N)` and `Expand (N)`
  with the sweep caps, progress, cancel, canvas-cap early stop notice, view-only footnote.
  Selection cleared when a bulk remove takes the selected element.
- `CanvasScreen.tsx`: tab strip gains `interact` (`data-testid="canvas-tab-interact"`).
- Field help: new keys for the filter rows; reuse where identical.

**Tests** (`tests/canvas-interact-panel.test.tsx`, new): tab renders and persists; empty
filters match all (expand-all path); live count tracks store changes, including an on-canvas
degree filter recounting after a merge adds edges; remove drops matched
vertices + incident edges and clears a matched selection; expand merges neighborhoods and
respects `EXPAND_SWEEP_CAP` refusal + canvas-cap stop notice; cancel mid-sweep keeps landed
batches; draft persistence across remount.

## Phase 3 - Degree + semantic filters, preview lifecycle

**Goal:** costly filters with the evaluate-then-act contract.

- Preview button when a costly filter is active (database-source degree, semantic); evaluated
  match set with height-capped list, per-row eclipse hover + select-into-Detail; invalidation
  on any filter edit or canvas mutation (subscribe to store changes); degree sweep refusal over
  `DEGREE_SWEEP_CAP` naming the narrowing tools; semantic row gated on provider + bound index with the says-why row when
  it cannot run; unscored count in the preview summary.

**Tests**: preview gating (actions disabled until evaluated; invalidation on edit and on canvas
change); degree filter end to end with mocked degree routes (over/under, direction); over-cap
refusal message; semantic end to end with mocked `embeddingSearch` (metric orientation both
ways, unscored exclusion + count, gating rows); combined cheap+costly (degree evaluated over
cheap survivors only - assert request count).

## Phase 4 - Docs, screenshots, sweep

- `docs/src/content/docs/studio.md`: canvas section describes the Interact tab (one paragraph).
- Recapture the canvas screenshot(s) whose frame shows the tab strip (measure which, per the
  screenshot-capture practice: only recapture what actually changed).
- README canvas wording: augment only if it sharpens; no new bullet.
- Confirm the impact table: no OpenAPI/MCP/descriptor snapshot drift (nothing server-side
  changed), nl-assist no-retrain note stands.
- Docs build green (`npm --prefix docs ci && npm --prefix docs run build`).
