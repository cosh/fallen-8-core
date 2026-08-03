# Canvas find & connect - plan

Studio-UI-only feature (no engine/REST/MCP/OpenAPI change), phased so each phase leaves the
web-ui gates green (`tsc`, vitest via the `cmd /c "... & echo EXIT=%ERRORLEVEL%"` wrapper -
the PowerShell wrapper's exit code lies). Pure logic lands before panels, panels land one tab
at a time.

Workflow: feature CODE on a `feature/canvas-find-connect` branch. **Sequencing: branch from
`main` only after `feature/all-property-search` has merged** - the Find tab imports
`scanProperties` from that branch. Run the review/council gate before merge.

## Phase 1 - Pure logic + store + endpoint touch

**Goal:** every non-DOM decision is implemented and unit-tested before any panel exists.

- `src/lib/connectPaths.ts` (new; MIT header):
  - `CONNECT_PAIR_CAP = 500`, `CONNECT_BATCH_SIZE = 8` (the one tuning home).
  - `buildPairs(vertexIds: number[]): [number, number][]` - unordered pairs, lower id first,
    self-pairs excluded, deterministic order.
  - `introducedSets(path: PathREST, baseline: { nodes: Set<number>; edges: Set<number> })`
    -> `{ nodeIds: Set<number>; edgeIds: Set<number> }` - path elements minus baseline.
  - `removalSet(target: IntroducedSets, others: IntroducedSets[])` -> the target's elements
    not claimed by any other currently-added path.
  - `synthesizeEdges(path: PathREST): EdgeREST[]` - the Path screen's overlay synthesis,
    factored here so both call sites share one shape (id, endpoints, edgePropertyId).
- `src/api/endpoints.ts`: `findPaths` gains an optional trailing `signal?: AbortSignal`,
  passed to `apiRequest` (the `getStatus` precedent). Additive; no caller changes.
- `src/state/instanceStore.ts`: add `canvasToolsDraft` (`tab` "style" default, `findTerm`,
  `findLabel`, `findResultType` "Both" default, `connectMaxDepth` 3, `connectScope` "all"),
  `DEFAULT_CANVAS_TOOLS_DRAFT`, `setCanvasToolsDraft`, `resetCanvasToolsDraft`, and the
  merge-default line in the persist `merge` (old persisted workspaces pick up the defaults,
  no migration).

**Tests** (`tests/connect-paths.test.ts`, new; thorough per the quality bar):
- `buildPairs`: dedup, ordering, self-exclusion, empty/one-vertex input, count formula.
- `introducedSets`: direct-edge path (endpoints in baseline -> only the edge introduced),
  multi-hop path (intermediates + edges introduced), path fully inside baseline (empty sets).
- `removalSet`: shared intermediate claimed by another added path is kept; last claimant
  releases it; disjoint paths remove fully; claim by two others.
- `synthesizeEdges`: field mapping from `pathElements`, including direction-agnostic endpoint
  ids taken verbatim from `sourceVertexId`/`targetVertexId`.
- Store: draft persists and rehydrates; an old persisted workspace (no `canvasToolsDraft`)
  merges to defaults (`instance-isolation.test.ts` pattern).

## Phase 2 - Tab strip + Find tab

**Goal:** the aside is tabbed, Style is untouched under the default tab, Find works end to end.

- `CanvasScreen.tsx`: replace the single style panel header with the tab strip
  (`data-testid="canvas-tab-style|find|connect"`), active tab from `canvasToolsDraft.tab`.
  Detail panel below stays as is. Pass `setSelected` down so a Find row can select into it.
- `src/canvas/FindPanel.tsx` (new): term/label/result-type inputs (label suggested via a
  graph-shape datalist scoped to this screen), run gated on a non-blank term, `scanProperties`
  -> `hydrateElements` (500 cap + progress + capped note), compact result rows (kind marker,
  id-as-button -> `onInspect`, truncated label, live on-canvas indicator from
  `canvasNodes`/`canvasEdges`, per-row `+ canvas`), and `Send all to canvas`
  (vertices/edges split, `mergeIntoCanvas`). Height-capped list (`SCROLL_ROWS`), `capList` +
  `ListCapNote` on the rare overflow. Errors via `ErrorBox`.
- `src/lib/fieldHelp.ts`: reuse `searchTerm`/`searchLabel`/`scanResultType`; add nothing that
  duplicates an existing explanation.

**Tests** (`tests/canvas-find.test.tsx`, new):
- Tab strip: Style renders by default (existing `style-panel` testid present on mount); tab
  switch persists across unmount/remount; Detail panel visible under every tab.
- Find: run disabled on blank term; run sends `scanProperties` with `{ searchTerm, label?,
  resultType }` (label omitted when empty); rows render hydrated kind/id/label; the on-canvas
  indicator reflects the store and flips after a per-row add; per-row add merges exactly that
  element (edge add brings stub endpoints); send-all merges the split sets; row id click calls
  the inspect path (element selected into detail); hydration-cap note at >500 ids; draft
  term/label/result-type persist across remount and `resetCanvasToolsDraft` clears them.
- Existing `canvas-view-controls.test.tsx`, `canvas2d-select.test.tsx`, `style-panel.test.tsx`
  stay green unchanged (Style is the default tab).

## Phase 3 - Connect tab

**Goal:** pairwise shortest-path discovery over canvas vertices with honest caps, cancel, and
selective add/remove.

- `src/canvas/ConnectPanel.tsx` (new):
  - Scope toggle (`all | pick`); pick mode renders the checkbox list of canvas vertices
    (id + label, client-side substring filter, height-capped, `capList` ceiling); picked ids
    are component state.
  - Max-hops input (min 1, default from draft, `pathMaxDepth` help key).
  - Always-visible pair arithmetic (`N vertices -> P pairs`); run disabled under 2 endpoints
    or over `CONNECT_PAIR_CAP` (with the honest over-cap message pointing at pick mode).
  - Run: snapshot baseline; `buildPairs`; batched `findPaths` (`CONNECT_BATCH_SIZE`, one
    `AbortController` for the run) with `pair X/Y` progress and a Cancel button; per-pair
    failures counted and reported; found rows (`a -> b`, hops, `n new`), summary line with
    found/unreachable/failed/cancelled counts.
  - Add/Remove per row + Add all: `introducedSets` at run time; Add hydrates introduced
    vertices via `getGraphElement`, merges them + `synthesizeEdges` output filtered to the
    introduced edge ids; Remove applies `removalSet` against the OTHER added rows, edges then
    nodes, via `removeFromCanvas`.
- `pathOverlay` untouched.

**Tests** (`tests/canvas-connect.test.tsx`, new):
- Pair gating: <2 endpoints disabled; over-cap disabled with message; pick mode narrows the
  count; the pick filter narrows the list without dropping state.
- Run: one `findPaths` call per unordered pair with the lean spec (`BLS`, draft `maxDepth`,
  `maxResults: 1`); found/unreachable summary; a rejected pair increments the failure count
  without aborting the run.
- Add: merges exactly the introduced elements (intermediate vertex hydrated with real label;
  baseline elements' props untouched); direct-edge path introduces only the edge.
- Remove: shared intermediate survives while another added path claims it and goes when the
  last claimant is removed; re-add after remove works; remove after a manual canvas removal
  is a no-op (no throw).
- Cancel: abort keeps rows found so far and shows the cancelled-after note.
- Draft: `connectMaxDepth`/`connectScope` persist; picked ids do NOT persist (documented
  ephemerality).

## Phase 4 - Docs, screenshots, sweep

- `docs/src/content/docs/studio.md`: rewrite the Canvas section around the three tabs (Style
  table stays; add Find and Connect paragraphs with the caps stated honestly: 500-row
  hydration, 500-pair budget, introduced-only add/remove).
- Screenshots per the capture pipeline (isolated app + `F8_UI_URL`): recapture
  `screen-canvas-style.png` (the aside now shows the tab strip); add one Connect shot with
  found paths (added/removable rows visible). Any other canvas screenshot that shows the
  aside gets recaptured too.
- `README.md`: augment the Canvas/Studio key-features wording if it sharpens ("find and
  connect without leaving the canvas"); no new bullet or page.
- Docs build: `npm --prefix docs ci && npm --prefix docs run build` (link-checked).
- Cross-feature sweep per the spec's impact table; confirm the dotnet suite is untouched by
  running it once before merge (it must be trivially green - no server code changed).
- e2e `studio.spec.ts`: run, confirm canvas scenarios green; do not extend it (vitest pins
  the new tabs).

## Definition of done

- All four phases on the feature branch; `tsc` + full vitest suite green (exit code confirmed
  via the cmd wrapper); dotnet suite green (untouched); docs build link-clean.
- Canvas screenshots recaptured; studio.md updated; README wording reviewed.
- The feature directory moves from `features/open/canvas-find-connect/` to `features/done/`.
