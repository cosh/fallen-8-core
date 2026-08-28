# Studio: merge Path + Subgraph into one Traverse screen

Status: spec and plan only; not implemented. Implementation goes to
`feature/studio-traverse-merge` (branch + review gate), never directly to `main`.

Source: the Studio navigation redesign exploration, direction **1d** ("the Traverse
merge"). Of the merges considered there, this is the only one recommended;
Indexes + Plugins and Query + Indexes were considered and rejected and stay separate
screens. The rail *rendering* directions from the same exploration (zone labels, wide
collapsible rail) are deliberately **not** part of this feature; see "Out of scope".

## Why

The Path screen (`fallen-8-web-ui/src/screens/PathScreen.tsx`) and the Subgraph screen
(`fallen-8-web-ui/src/screens/SubgraphScreen.tsx`) are structural near-twins. Both wire
the same shared components in the same order: `FilterSourceToggle` +
`StoredQueryPicker` + `SaveAsStoredQuery` (`src/components/StoredQueryControls.tsx`),
`DelegateSlot` opening the shared `DelegateEditor` with its NL-assist panel, the same
semantic helpers and graph-shape suggestions, and a per-instance-and-namespace draft in
the workspace store. They differ in the query model on top (route finding vs subgraph
lifecycle + pattern builder), not in the interaction pattern.

The seam that actually hurts is the stored-query library: it is ONE library per
namespace (`GET/POST/DELETE /storedquery`, kinds `Path` | `SubGraph`), but the Studio
renders it as two kind-scoped `StoredQueriesPanel` instances, one at the bottom of each
screen. The `onUse`/`onSaved` wiring (`set…Draft({ filterSource: "stored",
storedQuery: name })`) is repeated four times across the two screens.

Merging the two screens into one **Traverse** screen with three tabs removes a rail
entry (14 to 13), reunites the library in one table, and keeps every capability exactly
where its sibling already is.

## Behaviour

### One screen, three tabs

`/q/{ns}/traverse` renders the new Traverse screen: a tab strip with three tabs,
following the existing Studio tab idiom.

| Tab | Label | Content |
|---|---|---|
| 1 | Path finding | Everything on today's Path screen except its stored-queries panel: the Path query panel (from/to, algorithm picker incl. registered plugins, maxDepth/maxResults/maxPathWeight), filter source toggle, semantic scoring block, stored-query picker (stored mode), advanced delegate slots + Save as stored query (inline mode), results with Overlay on canvas. |
| 2 | Subgraph builder | Everything on today's Subgraph screen except its stored-queries panel: the Subgraphs lifecycle table (To canvas / Recalculate / Delete), the Create subgraph form with semantic query section, top-level slots, pattern sequence builder, Save as stored query, create-error mapping. |
| 3 | Stored queries | The unified library: one table over both kinds (see below). The tab label carries the entry count, e.g. "Stored queries (4)". |

The two scenario tabs keep their content and behaviour 1:1, including all existing
test ids (`path-*`, `sg-*`, `pt-*`, …). This feature moves furniture; it does not
redesign the forms.

**Tab panels stay mounted.** Switching tabs hides the inactive panels instead of
unmounting them, so transient component state (path results, the subgraph message
line) survives a tab switch exactly as form drafts already do. The existing remount
key on instance/namespace switch (`AppShell.tsx`, `key={id/ns}`) still resets
everything, as today.

### The unified Stored queries tab

- One table, columns: **name | kind | state | registered | actions**. Kind renders
  the wire values `Path` / `SubGraph`. Everything else matches today's panel: name
  truncation, compile-state warn colouring, locale timestamp, Source/Hide expansion
  with `describeStoredSpecification` + recompile diagnostics, Delete behind the typed
  confirm dialog, list capping (scrolls after the default row threshold, hard cap
  per the Studio list policy).
- **Use** switches to the entry's scenario tab and selects it there: flips that tab's
  draft to `{ filterSource: "stored", storedQuery: name }`. Disabled while
  `compileState === "Failed"`, as today.
- Registration is unchanged: **Save as stored query…** stays inside each scenario
  tab's inline advanced tier, next to the fragments it captures. On success it flips
  that tab to stored mode (today's behaviour); it does not navigate to the library tab.
- Empty state names both kinds and points at the two Save as stored query buttons.

Relation to `features/done/stored-query-scenario-scoped-ux/`: that feature removed the
mixed both-kinds table from the **Query** screen because it sat on an unrelated screen
and its cross-links navigated away. This tab is a different animal: it lives on the
scenario surface itself, and Use switches a tab in place instead of leaving the screen.
The kind-scoped pickers inside the scenario tabs are untouched. The kind-scoped
*panels* at the bottom of each screen are replaced by the one tab; that feature's
README gets a one-line pointer here.

### Routing

- New scoped route `/q/{ns}/traverse` (leaf `traverse` collides with no API route, so
  the SPA fallback is safe). The active tab is a validated search param:
  `?tab=path | subgraph | stored`.
- Tab resolution: an explicit `?tab=` wins; otherwise the remembered last tab for this
  instance+namespace (new `traverseTab` field in the workspace store, defaulting to
  `path`). Switching tabs updates the search param with `replace: true` and the store.
- **Old URLs stay live and deep-linkable.** Following the retiring pattern from the
  dashboard and documents removals (`routes.tsx`):
  - `/q/{ns}/path` redirects to `/q/{ns}/traverse?tab=path`
  - `/q/{ns}/subgraphs` redirects to `/q/{ns}/traverse?tab=subgraph`
  - the flat legacy `/path` and `/subgraphs` entries in `LEGACY_SCOPED_PATHS` point
    directly at the traverse URL with the right tab (one hop, not two)
  - `/traverse` joins the flat legacy list.
- Nothing else in the app deep-links to the two old leaves (verified: the only
  `navigate()` targets are the rail links; both screens' send-to-canvas actions
  navigate to `/canvas`).

### Rail

- One entry replaces two: `{ leaf: "traverse", label: "Traverse", icon: "↝",
  scoped: true }` at today's Path position (rail goes 14 to 13 entries).
  Subgraph's ◫ glyph retires with its entry.
- Rendering, lock-when-disconnected, and the integrations capability-hide are
  untouched. Active-entry matching keeps working unchanged: `navTarget` compares the
  pathname only, and all three tabs share `/q/{ns}/traverse`.
- Test id becomes `nav-traverse` (derived from the label, as today).

### Section help

One `traverse` entry in `src/lib/sectionHelp.ts` replaces the `path` and `subgraphs`
entries (the leaf-coverage test enforces exactly this). Heading: "How traversal
works". Doc slugs (the mapping allows at most three): `path-finding`, `subgraphs`,
`stored-queries`. The delegates and semantic-traversal pages stay one link away from
those pages; that is accepted.

### What does not change

- Engine, REST contract, OpenAPI snapshot, MCP tools: nothing. This is Studio-only.
- Both forms' fields, validation gates, semantic ownership rules, pattern sequence
  validation, error mapping, and the send-to-canvas flows (`pathOverlay`,
  `mergeIntoCanvas`).
- The delegate editor, NL assist, and the plugin authoring editor.
- The persisted `pathDraft` / `subgraphDraft` slices: no reshaping, no renaming, so
  the store's rehydration/migration logic is untouched. `traverseTab` is a new field
  with a default; absent values deep-default like any other.
- The embeddable library artifact's public surface (`F8GraphCanvas`, overlay props).

## Impact on existing features

- **Engine / REST / OpenAPI snapshot:** none. No controller or model changes;
  `McpRestCoverageTest` and `McpContractTest` unaffected.
- **stored-query-scenario-scoped-ux (done):** partially superseded on the library
  *view* (one table again, now kind-columned and scenario-local); the kind-scoped
  pickers and the "registration lives next to the fragments" decision survive. Add a
  one-line pointer in that feature's docs to this spec.
- **studio-section-help (done):** mapping shrinks by one leaf; the coverage test
  forces the update.
- **studio-dashboard-removal (done):** its redirect pattern is reused; no change there.
- **studio-embeddable (done):** the embed smoke e2e clicks `nav-path`; update to
  `nav-traverse`. No artifact API change.
- **Docs site:** `studio.md` is the main hit (rail/screen table, the `## Path` and
  `## Subgraph` sections merge into `## Traverse` with the three tabs, the Query
  screen's stored-query note, the Canvas source list, the delegate editor prose, the
  capability table). One line in `stored-queries.mdx` (panels renamed to the tab).
  `samples.md` and `semantic-traversal.mdx` mention the screens by name; sweep and
  reword. No new docs page: `studio.md` remains the screens' home, and
  `path-finding.mdx` / `subgraphs.mdx` are concept pages that keep their names (help
  targets). Root `README.md`: check for stale screen-name mentions; the key-features
  list links concept pages, which do not change.
- **Screenshots:** the rail loses an entry, so every `screen-*.png` showing the rail
  is stale, plus the directly affected frames (`screen-path.png`,
  `screen-subgraph-builder.png`, `path-result.png`, `subgraph-result.png`, and the
  two captured through the Path route: `screen-delegate-editor.png`,
  `screen-nl-assist.png`). Full recapture pass via the per-spec Playwright
  screenshot harness.
- **NL-assist dataset / eval:** none. The dataset, eval fixture, and prompts
  reference the REST surface only, never Studio routes. No RETRAIN-LOG entry needed.
- **First-run walkthrough:** no change; its beats caption REST endpoints and its only
  navigations are Samples and Save games.
- **Architecture diagrams:** none; no channel or deployable changes.

## Out of scope

- The rail rendering directions from the same exploration (zone labels, wide
  collapsible rail, collapsed icon mode). If pursued, that is its own feature on top
  of the 13-entry rail this feature produces.
- An icon redesign.
- Cleaning up the two screens' `navigate({ to: "/canvas" })` flat-route quirk (works
  today via the legacy redirect; unchanged here).

## Acceptance criteria

1. The rail shows 13 entries; `nav-traverse` sits where `nav-path` was; `nav-path`
   and `nav-subgraph` are gone.
2. `/q/{ns}/traverse` renders the three tabs; `?tab=` selects one; without it the
   last-used tab for that instance+namespace is restored (default Path finding).
3. `/q/{ns}/path`, `/q/{ns}/subgraphs`, `/path`, `/subgraphs` all land on the right
   tab with the namespace preserved.
4. Filling the path form, switching to Subgraph builder and back loses nothing:
   drafts, transient results, and the advanced-tier open state survive.
5. The Stored queries tab lists both kinds with a kind column and live count in the
   tab label; Use lands on the right scenario tab with the entry selected in stored
   mode; Failed entries cannot be used; Delete keeps the typed confirm.
6. Both scenario tabs behave exactly as the two screens did (existing component tests
   pass against the panels with only mounting-related adjustments).
7. Section help on the Traverse screen resolves; the help coverage test passes.
8. Vitest suite, Playwright e2e (including the embed smoke), and the docs build
   (`npm --prefix docs run build`, link-checked) are green; screenshots recaptured.
