# Studio mutations UX — plan

Phases; each leaves the build green (`npm run build`, `npm test` in `fallen-8-web-ui`).

## Phase 1 — field-help foundation

- `src/lib/fieldHelp.ts`: the single home for field help copy. Typed
  `FIELD_HELP: Record<FieldHelpKey, string>` plus a `help(key)` accessor that fails the
  type-check on unknown keys.
- `src/components/FieldLabel.tsx`: `<FieldLabel htmlFor helpKey>text</FieldLabel>` —
  renders the existing `.label` styling plus `title` + `cursor-help` + dotted underline.
  Exports `fieldTitle(helpKey)` for putting the same text on the input element.
- `TypedLiteralEditor` gains an optional `helpKey` (defaults to the generic
  `typedValue` explanation) and uses `FieldLabel`.
- Unit test: dictionary has no empty/missing entries; `FieldLabel` renders `title`.

## Phase 2 — tabbed, full-spectrum MutationsPanel

- `src/components/PropertyListEditor.tsx`: 0..n rows of (property id, typed value) with
  add/remove-row buttons; reports rows + validity. Reused by Vertex and Edge tabs.
- `src/lib/creationDate.ts` (or into `literals.ts` if it stays tiny): parse
  empty → 0, integer → seconds, ISO date → seconds, else error.
- Rewrite `MutationsPanel.tsx`: four tabs (Vertex / Edge / Property / Remove) using the
  segmented-control styling already used for the vector-add mode toggle; per-tab forms
  send the full `VertexSpecification` / `EdgeSpecification`. All fields use `FieldLabel`.
- Unit tests (`tests/mutations-panel.test.tsx`): payload assembly, tab state
  preservation, validation gates, creation-date parsing table, duplicate property ids.

## Phase 3 — portal-wide help sweep

- Adopt `FieldLabel` + dictionary keys on every labeled input across: BrowserScreen,
  QueryScreen (scan + index management), PathScreen, SubgraphScreen, AnalyticsScreen,
  DashboardScreen, ConnectScreen, SaveGamesScreen, CanvasScreen, StoredQueriesPanel,
  StoredQueryControls, DelegateEditor/DelegateSlot inputs.
- Grep gate: no remaining `className="label"` outside `FieldLabel` itself.

## Phase 4 — verify + council

- `npm run build`, `npm test`; run the API app + studio and exercise each mutation tab
  end-to-end against a live instance.
- Council review (parallel agents: correctness/API-contract, UX/regression, scope), fix
  findings on the branch, then `git merge --no-ff` to `main`, push branch + main, move
  `features/open/studio-mutations-ux/` → `features/done/`.

## Status

- [ ] Phase 1 — field-help foundation
- [ ] Phase 2 — tabbed MutationsPanel
- [ ] Phase 3 — portal-wide help sweep
- [ ] Phase 4 — verify + council
