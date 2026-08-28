# Plan: studio-traverse-merge

Branch: `feature/studio-traverse-merge`. Studio-only; no engine, REST, OpenAPI, or MCP
changes, so the .NET gates are untouched by construction (run the full suite once
before merge anyway). All paths below are relative to `fallen-8-web-ui/` unless they
start with `docs/` or `features/`.

Phase order is chosen so the suite is green after every phase.

## Phase 1: Traverse screen and tab shell

Goal: the merged screen exists and is routable; the two old screens become its panels.

- `src/screens/TraverseScreen.tsx` (new): owns the tab strip (existing Studio tab
  idiom; test ids `traverse-tab-path`, `traverse-tab-subgraph`, `traverse-tab-stored`)
  and renders all three panels, hiding inactive ones (keep-mounted; use `hidden` on
  the panel wrappers so component state survives). Tab resolution: `?tab=` search
  param wins, else the `traverseTab` store field, else `path`. Tab clicks navigate
  with `replace: true` and write the store field.
- `src/screens/PathScreen.tsx`, `src/screens/SubgraphScreen.tsx`: drop the trailing
  `<StoredQueriesPanel …/>` (and its `onUse`); everything else stays byte-identical.
  Keep file and export names to avoid churn; they are now tab panels.
- `src/state/instanceStore.ts`: add `traverseTab: "path" | "subgraph" | "stored"`
  (default `"path"`) + setter. New field with a default: no migration branch needed
  in the custom `merge`; verify rehydration of an old persisted blob in a test.
- `src/app/routes.tsx`:
  - `traverseRoute` under `namespaceRoute`, path `"traverse"`, `validateSearch` for
    `tab`, component `TraverseScreen`.
  - Replace `pathRoute`/`subgraphRoute` with redirect routes to
    `/q/$ns/traverse` + the matching `tab` (pattern: the `documents` -> `knowledge`
    redirect).
  - `LEGACY_SCOPED_PATHS`: retarget `/path` and `/subgraphs` directly at the traverse
    URL with the right tab (one hop); add `/traverse`.
- `src/app/nav.ts`: replace the `path` and `subgraphs` entries with
  `{ leaf: "traverse", label: "Traverse", icon: "↝", scoped: true }` at position 7.
- `src/lib/sectionHelp.ts`: replace the `path` and `subgraphs` entries with one
  `traverse` entry (heading "How traversal works"; slugs `path-finding`, `subgraphs`,
  `stored-queries`; max three enforced by test).
- Namespace/instance switching: `sameScopedScreen` keeps the `traverse` leaf but drops
  the search param; that is fine because the store remembers the tab. Pin this with a
  test rather than threading search through the switchers.

Existing tests to update in this phase (they fail loudly until then):

- `tests/app-shell.test.tsx`: `GATED` list (`nav-path`/`nav-subgraph` ->
  `nav-traverse`), the namespace-keeps-screen case (`/q/default/subgraphs` ->
  `/q/default/traverse`), deep-link guard.
- `tests/section-help.test.tsx`: leaf coverage picks up `traverse` automatically;
  slug existence keeps passing (all three pages exist).
- `tests/namespaces.test.tsx`: `/q/ghost/subgraphs` -> `/q/ghost/traverse` in the
  recover-state case.
- `tests/path-semantic.test.tsx`, `tests/subgraph-pattern-builder.test.tsx`,
  `tests/subgraph-semantic.test.tsx`: keep rendering the panel components directly;
  only assertions that relied on the stored panel being on-screen move to Phase 2.

Gate: `npm test` green, `npm run build` green (tsc). On this machine confirm exit
codes with `cmd /v:on /c "npm test & echo EXIT=!ERRORLEVEL!"` (the PowerShell wrapper
can lie about nonzero exits).

## Phase 2: unified Stored queries tab

Goal: one library table over both kinds, wired by kind.

- `src/components/StoredQueriesPanel.tsx`: make `kind` optional. Without it: no kind
  filter, a **kind** column between name and state, title "stored queries", an
  empty state naming both kinds, `data-testid="stored-queries-all"`, and
  `onUse(entry)` receiving the full summary (name + kind) instead of the name.
  With `kind` set, behaviour stays exactly today's (the two scenario-scoped call
  sites are gone, but the prop keeps the component honest and the tests simple).
  List capping, Source expansion, typed-confirm Delete: unchanged code paths.
- `src/screens/TraverseScreen.tsx`: third tab renders the panel without `kind`;
  `onUse` routes by `entry.kind`: set the matching draft to
  `{ filterSource: "stored", storedQuery: name }`, then activate that scenario tab.
  Tab label count from the shared `[instance.id, "storedqueries"]` query cache
  (render nothing while loading; no layout shift).
- Deduplicate the four identical `onSaved`/`onUse` draft-flip closures into one small
  helper (e.g. in `src/lib/storedQueries.ts`) used by both panels and the tab.

Tests:

- `tests/stored-queries-panel.test.tsx`: keep the kind-scoped cases; add unkinded
  cases: both kinds listed with kind column; Use hands back the entry; Failed
  disabled; Delete confirm unchanged.
- New `tests/traverse-screen.test.tsx`: tab switching preserves the path draft, the
  advanced-tier open state, and transient results (mock a run, switch tabs, switch
  back); Use on a `SubGraph` entry lands on the Subgraph builder tab with the picker
  set; `?tab=stored` deep link; remembered tab restored without a param; tab label
  count.

Gate: `npm test` green.

## Phase 3: e2e and embed

- `e2e/studio.spec.ts`: scenarios entering via `goto("/path")` / `goto("/subgraphs")`
  keep working through the redirects; update URL assertions (now
  `/q/{ns}/traverse?tab=…`), the section-help scenario (tooltip heading and link
  targets), and any locator that assumed the stored panel on the scenario screens.
- `e2e-embed/embed-smoke.spec.ts`: `nav-path` -> `nav-traverse`; the Monaco boot path
  is then `traverse` tab 1, unchanged otherwise.
- `e2e/screenshot-stored-queries.spec.ts`: rewrite around the merge; it now produces
  `screen-path.png` (Path finding tab), `screen-subgraph-builder.png` (Subgraph
  builder tab), and the Query-screen negative assertion moves to "the Traverse
  Stored queries tab lists all four seeded entries" (this is also the natural frame
  for the unified table if `studio.md` wants one).
- `e2e/screenshot-worked-examples.spec.ts`, `e2e/screenshot-delegate-editor.spec.ts`,
  `e2e/screenshot-nl-assist.spec.ts`: entry route updates only.

Gate: `npm run e2e` and the embed config green locally.

## Phase 4: docs and screenshots

- `docs/src/content/docs/studio.md`: rail/screen table gets one `Traverse` row;
  `## Path` and `## Subgraph` merge into `## Traverse` describing the three tabs and
  the unified library; update the Query note ("stored queries live on the Traverse
  screen"), the Canvas source list, the delegate editor prose ("fragment slots on the
  Traverse screen"), and the capability table rows.
- `docs/src/content/docs/stored-queries.mdx`: the "Stored path queries / Stored
  subgraph queries panel" sentence becomes the Stored queries tab.
- Sweep `docs/src/content/docs/samples.md` and `semantic-traversal.mdx` for
  screen-name mentions; reword. Sweep root `README.md`; the key-features links point
  at concept pages and should not need changes.
- `features/done/stored-query-scenario-scoped-ux/`: add a one-line pointer to this
  feature (library view unified on the Traverse screen; pickers still kind-scoped).
- Recapture screenshots: full pass over the `e2e/screenshot-*.spec.ts` suite
  (`F8_SCREENSHOT=1 npm run e2e -- <spec>` per spec), since every rail-bearing frame
  is one entry stale. Use an isolated apiApp + `F8_UI_URL` against the dev server to
  avoid the :5000 webServer race; do not pipe the app launch through anything that
  truncates its output.
- Gate: `npm --prefix docs ci && npm --prefix docs run build` (link-checked).

## Phase 5: final verification

- Full `npm test`, `npm run e2e`, embed smoke, docs build.
- `dotnet test fallen-8-core.sln` once (expected untouched; belt and braces).
- Manual pass with `npm run dev`: 13-entry rail, deep links from old bookmarks, tab
  memory across a namespace switch, Use-routing from the library tab, overlay to
  canvas from a found path.
- Move `features/open/studio-traverse-merge/` to `features/done/` in the landing PR.

## Risks / working notes

- The keep-mounted tabs mean both panels' queries (subgraph list, stored-query list)
  run while either tab is open; they already share the query cache and are cheap.
  If jsdom tests assert visibility, prefer `toBeVisible()` over presence, since
  hidden panels keep their DOM.
- `StoredQueriesPanel` derives title and empty-state prose from `kind`; the unkinded
  branch must not silently regress the kind-scoped strings (both branches tested).
- The persisted-store `merge` has bespoke branches for `pathDraft.semantic` and a
  pre-restructure `subgraphDraft`; do not touch those slices. Only `traverseTab` is
  added, with a default.
- Route-leaf collisions: `traverse` has no API-route sibling, but re-check against
  `Program.cs` route prefixes when adding the flat `/traverse` legacy entry (the SPA
  fallback serves any unmatched GET).
- When implementing in a worktree, junction `node_modules` from the main checkout
  before running the JS gates.
