# Plan - canvas-view-controls

Everything lives in `fallen-8-web-ui` plus one docs-site page. No new dependencies,
no server changes. No feature README: the user-facing home is the Studio docs page,
and spec + plan are the record here.

## Phase 1 - one home for the canvas element cap

- New `lib/canvasCap.ts`: exports `CANVAS_ELEMENT_CAP = 20_000` with a doc comment
  stating what it bounds (elements fetched into the canvas working set per kind,
  since `GET /graph?maxElements=` caps vertices and edges independently).
- `lib/sampleLoader.ts`: delete its private `CANVAS_ELEMENT_CAP`, import the shared one.
- `components/SampleGraphsPanel.tsx`: replace the hardcoded `20_000` literal in its
  `getGraph` call with the shared constant.

## Phase 2 - complete clear (FR-1)

- `screens/CanvasScreen.tsx`: the "Clear view" onClick becomes
  `clearCanvas(); setSelected(null);` - the same pairing "Remove from view" already
  does. The store's `clearCanvas` (nodes, edges, path overlay) is already right;
  style config, result sets, and drafts are untouched by construction. No store change.

## Phase 3 - Show whole graph (FR-2, FR-3, FR-5)

- `screens/CanvasScreen.tsx`: a `useMutation` that awaits `getGraph(instance,
  CANVAS_ELEMENT_CAP)` and `getStatus(instance)` together, merges via the existing
  `mergeIntoCanvas`, and returns `{fetched vertex/edge counts, status
  vertexCount/edgeCount}` as the mutation result.
- Button "Show whole graph" in the top-left toolbar strip (before "Clear view"),
  disabled and labeled "Loading..." while pending. Merge-only: no replace mode, no
  cancellation; a clear racing an in-flight load is resolved by clicking clear again.
- Truncation notice: when status counts exceed the fetched counts, render "showing the
  first X of Y vertices" (and/or edges) next to the element count. It stays until the
  next load or a clear. Amended in council review: the record lives in the persisted
  workspace store (`wholeGraphTruncation`, cleared by `clearCanvas`), not in mutation
  state as first planned - the canvas working set is persisted, so an ephemeral notice
  would silently vanish on revisit while the truncated canvas remained, exactly the
  dishonesty FR-3 forbids.
- Fetch failure: `ErrorBox` rendered under the toolbar strip; canvas contents are
  untouched because the merge only happens after a successful fetch.

## Phase 4 - empty-state discoverability (FR-4)

- Detail panel empty hint becomes "Send elements here from the browser, query, path,
  or subgraph screens, or show the whole graph." No unittest-endpoint mention.
- `lib/fieldHelp.ts`: hover help entry for the new button if the strip buttons carry
  fieldHelp; otherwise a `title` tooltip stating the cap and merge semantics.

## Phase 5 - tests

- New `tests/canvas-view-controls.test.tsx` (vitest, testing-library, mocked
  `api/endpoints` and a stubbed `GraphCanvas` that exposes `onSelect`): clear resets
  nodes/edges/overlay AND selection (detail panel back to the empty hint) while style
  config and result sets survive; whole-graph load merges without dropping existing
  elements; busy/disabled states; truncation notice appears exactly when status
  counts exceed fetched counts and is absent otherwise; failed fetch leaves the
  canvas intact and shows the error.
- `tests/sample-loader.test.ts`: keep green after the cap import switch.

## Phase 6 - docs, screenshots & gates

- `docs/src/content/docs/studio.md` Canvas section: reword "it never auto-loads the
  whole graph" to keep the promise while naming the explicit button; document both
  toolbar actions (clear semantics: view-only, wipes selection too).
- Recapture canvas screenshots showing the toolbar strip (at least
  `screen-canvas-style.png`; check the cyber-sample shot) via the isolated-app
  screenshot flow.
- Gates: `npm run test` and `npm run build` in `fallen-8-web-ui`;
  `npm --prefix docs ci && npm --prefix docs run build` (link check) for the docs
  edit; no dotnet-side changes, so no OpenAPI/MCP regeneration.
- On merge: move `features/open/canvas-view-controls/` to `features/done/`.
