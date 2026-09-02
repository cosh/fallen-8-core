# Canvas interact - spec

## Problem

The canvas is the Studio's curation surface, but its two curation verbs only exist one element
at a time. "Expand neighbors" and "Remove from view" live on the Detail panel and apply to the
single selected element; the expand logic is an inline mutation in `CanvasScreen.tsx`. There is
no way to say:

- "expand every vertex on this canvas" (or every `pdu`, or everything matching a property);
- "remove every vertex whose degree is over 50" (the hub that just exploded the layout);
- "remove everything that is not semantically close to 'turbine vibration'" (the canvas as a
  relevance lens over what a query dumped there).

Every one of those is today a loop of click, expand or remove, click the next one - or a round
trip through the Query screen. The building blocks all exist: the canvas store is a client-side
working set carrying label and scalar-property snapshots per node, the per-vertex degree routes
answer true degree, `POST /embedding/search` scores elements against a text, and
`fetchVertexNeighborhood` is the one expand primitive. What is missing is a place to compose
them: **a match set built from filters, and the two verbs applied to it**.

This is a **Studio-UI-only feature**. Every operation reuses REST routes that already exist
(`/vertex/{id}/edges/indegree|outdegree`, `POST /embedding/search`, the per-property adjacency
routes behind `fetchVertexNeighborhood`). No engine method, no REST route, no MCP tool, no
OpenAPI change - the engine -> REST -> MCP propagation rule does not fire.

## Decisions

| Decision | Choice | Note |
|---|---|---|
| Placement | Fourth tab in the canvas tool strip: `Style \| Find \| Connect \| Interact` | Requested position, right of Connect. Style stays the default tab. |
| What it operates on | **Vertices only**, always the current canvas working set | Removing a vertex drops its incident edges (existing store semantics). Edge-targeted operations are a non-goal below. |
| The model | **Filters build a match set; two actions apply to it** | No scope toggle: with no filter active the match set is ALL canvas vertices, which covers "expand all". Filters are AND-composed; each is active exactly when its input is non-empty. |
| Filters | Label (exact), property (key + optional contains-term), degree (in/out/total, over/under x, with a **source toggle: database or on canvas**), semantic (text vs a bound vector index, closer/farther than a raw-metric threshold) | The four asked for. The two degree sources answer different questions - the database's is the graph fact ("remove real hubs"), the canvas's counts only loaded edges and is instant ("prune the leaves of what I see") - so the row carries a toggle and says which is active. Decided with the requester. |
| Single element in focus | **Stays on the Detail panel**, same buttons as today | Consolidation, not duplication: the Detail panel's Expand/Remove become calls into the same new primitives the tab uses. The tab needs no "selected only" scope; the Detail panel IS that scope, one click, selection-driven. |
| Where expand logic lives | One home: `src/lib/canvasInteract.ts` | Today it is an inline mutation in `CanvasScreen.tsx`. (For the record: nothing expand-shaped lives in the Style tab; Style's degree-based node sizing is untouched.) |
| Costly filters are two-step | Degree and semantic need server round trips, so actions run off an explicit **Preview** whose result invalidates on any edit or canvas change | Cheap filters (label, property) evaluate live client-side; with only cheap filters active there is no Preview step. A stale match set must never be acted on. |
| Unscored is unmatched | A vertex the semantic search returned no score for **never matches** the semantic filter, in either direction | "I could not look" must never become "it is far". The panel counts and reports the unscored. |
| Caps | Refuse-over-cap with an honest message, never a silent partial sweep | The Connect tab's precedent. Named constants in one home (values below are starting points, tuned in one place). |

## Behaviour after the change

### 1. The tab

`CanvasToolsDraft.tab` gains `"interact"`; the strip renders `Style | Find | Connect |
Interact`. The Detail panel below stays selection-driven and independent of the active tab,
exactly as today.

### 2. Filters

Four rows, each optional, active when filled, AND-composed over the canvas vertices:

- **label** - exact match on the canvas snapshot's label, suggested from the graph-shape
  datalist plus the labels actually on the canvas. A stub vertex (merged as an edge endpoint,
  never hydrated: label `null`, no props) matches no label filter.
- **property** - a key (activates the row) and an optional term. With a term: case-insensitive
  contains over the stringified snapshot value; without: "the key is present". Honest caveat,
  stated in the field help: the canvas snapshot is the styling snapshot - scalars only, strings
  capped at `CANVAS_PROP_MAX_STRING` (200) characters - so a term that only occurs past the cap
  does not match. The Find tab remains the server-side search.
- **degree** - direction (`in | out | total`, default total), comparator (`over | under`), an
  integer, and a **source**: `database` (default) or `on canvas`.
  - `database` is the vertex's true degree, fetched per candidate via the existing
    `/vertex/{id}/edges/indegree` and `/outdegree` routes, batched with visible progress and a
    Cancel. Evaluated AFTER the cheap filters, over their survivors only, and refused above
    `DEGREE_SWEEP_CAP` (1,000 candidates; two requests each) with "narrow by label or property
    first".
  - `on canvas` counts only the edges currently loaded, via the style engine's existing
    `visibleDegrees` (one home for that counting - it is what degree-based node sizing already
    reads). Instant and client-side, so it is a **cheap** filter with a live count and no
    Preview step. The field help states the blind spot plainly: this is the view's number, not
    the graph's - a vertex you never expanded can read 0 here while the database knows
    hundreds.
- **semantic** - a query text, a bound vector index (discovered from the status inventory the
  same way Find similar does), `closer than | farther than`, and a threshold **in the metric's
  raw units** (the same raw-score contract as the vector search screen and the traversal
  semantic block: the response says `metric` and `higherIsBetter`, the client never re-derives).
  One `POST /embedding/search` call with `kind: "vertex"` and `k = MAX_K`; canvas vertices found
  in the result carry their score, the rest are
  **unscored and never match**, and the preview reports "N of M matched vertices had no score".
  The row is offered only when the instance has an embedding provider and at least one bound
  vector index exists; when it cannot run, the row says why instead of greying out silently
  (the `findSimilar` principle).

A status line always shows what is active and what matches: `filtered · 37 of 240 vertices
match`. With a costly filter active the count reads `evaluate to match` until Preview runs, and a
row that is half filled in (a value term with no key, a semantic text with no threshold) reads what
is missing and **disables both verbs** rather than falling back to matching everything - the
keystroke that empties a degree box would otherwise move the panel from its safest state to its
most destructive one.

> Shipped note: `k` is `MAX_K` (1024, `lib/vectorSearch.ts`), the engine's own kNN ceiling, NOT a
> number derived from the canvas. An earlier draft of this spec said 20,000 (the canvas element
> cap); the engine refuses any `k` above 1024 with a 400, after the provider has already embedded
> the text, so that would have made this filter unusable on every real instance. The window it
> leaves is disclosed in the panel and in the docs instead of being hidden.

### 3. Preview

- **Cheap filters only** (label, property, on-canvas degree): the match set is live
  (recomputed from the store on every render); no Preview button; actions are enabled whenever
  the match set is non-empty.
- **Database degree or semantic active**: a **Preview** button runs the fetches (batched, progress as
  `vertex X of Y` for degree, one request for semantic, Cancel aborts and keeps nothing). The
  result is the evaluated match set: a count plus a height-capped list (id + label, list-caps
  policy). Hovering a row spotlights that vertex with the existing single-element eclipse (the
  Find tab's affordance); clicking selects it into the Detail panel.
- Any filter edit, threshold change, or canvas mutation (merge, remove, clear) **invalidates**
  the evaluated set: actions disable and the panel says to preview again. Acting on yesterday's
  match set against today's canvas is the bug this rule exists to prevent.

### 4. Actions

Both act on the current match set, are **view-only** (the database is never touched - the
standing canvas rule, restated in the panel), and show the count on the button:

- **Remove from view (N)** - `removeManyFromCanvas`, ONE store write for the whole set; incident
  edges go with their endpoints (existing semantics). A per-vertex loop was the first shape and is
  not viable: the canvas is persisted, so it re-serializes the whole workspace once per vertex
  (~10ms each, measured), which freezes the tab on a real match set. If the Detail panel's
  selected element is among them - or is an edge leaving with its endpoint - the selection clears
  (the single-remove precedent).
- **Expand (N)** - per matched vertex, `fetchVertexNeighborhood` with the existing
  `EXPAND_EDGE_CAP` (200) per vertex and `skipNeighborIds` = the live canvas, merged as each
  batch lands (`CONNECT_BATCH_SIZE`-style concurrency), progress `vertex X of Y`, Cancel offered
  whenever a run is in flight (filter or no filter) and stopping the requests themselves rather
  than only their answers (the signal reaches the fetches). Cancel works in whole BATCHES: completed
  ones are kept and counted, the in-flight one is dropped and counted as nothing, because the
  neighborhood primitive answers an aborted fetch with empty arrays and crediting those reported
  "expanded 8 of 20" over an unchanged canvas.
  Refused above `EXPAND_SWEEP_CAP` (100 matched vertices) - a single expand can
  cost hundreds of requests, so the filters are the narrowing tool and the refusal says so.
  The sweep also **stops early** when the canvas reaches `CANVAS_EXPAND_CEILING` (40,000
  elements, the SUM of both kinds), reporting it - the same honesty as the whole-graph truncation
  notice. Deliberately not `CANVAS_ELEMENT_CAP`: that bounds each KIND of a fetch, so "Show whole
  graph" can leave 20,000 vertices AND 20,000 edges, and a sweep refusing to grow a canvas the app
  itself just filled would enforce a ceiling nothing else in the product has. The report states
  what it EXPANDED (not what it attempted, so a failure cannot be counted twice) and names how
  many neighbourhoods the per-vertex edge cap cut short.

### 5. Consolidation

The expand mutation moves out of `CanvasScreen.tsx` into `src/lib/canvasInteract.ts` as
`expandVertices(instance, ids, opts)` (batching, caps, progress, cancel, merge callback). The
Detail panel's "Expand neighbors" becomes `expandVertices` over one id; the Interact tab's
Expand is the same function over the match set. "Remove from view" already delegates to the
store; the Detail button stays as is. One implementation, two call sites, no behaviour change
for the single-element path.

### 6. State, help, and shared pieces

- `CanvasToolsDraft` extends (merge-defaulted, no migration): `tab` gains `"interact"`;
  `interactLabel` "", `interactPropKey` "", `interactPropTerm` "", `interactDegreeDirection`
  "total", `interactDegreeOp` "over", `interactDegreeValue` "" (empty string = inactive, so
  inactivity is representable), `interactDegreeSource` "database", `interactSemanticText` "",
  `interactSemanticIndexId` "",
  `interactSemanticDirection` "closer", `interactSemanticThreshold` "". Evaluated scores,
  match previews, and progress are ephemeral component state, like every result in the Studio.
- Pure logic in `src/lib/canvasInteract.ts` (the `connectPaths.ts` idiom): the constants
  (`DEGREE_SWEEP_CAP`, `EXPAND_SWEEP_CAP`), cheap-filter matching over
  `CanvasNode`s plus the canvas edges (on-canvas degree delegates to the style engine's
  `visibleDegrees`), threshold application over scored results (metric-direction aware), and
  the degree/expand sweeps - all unit-testable without a DOM.
- New `src/canvas/InteractPanel.tsx` beside the other three panels; `CanvasScreen` passes the
  same handles down (`onSelect`, `onHover` like FindPanel).
- Field help: reuse `semanticQueryText`-family keys and the vector-search threshold phrasing
  where the concept is identical; new keys only for the genuinely new concepts (degree filter,
  the unscored rule).

## Impact on existing features

| Feature / layer | Impact | Handling |
|---|---|---|
| Engine / REST / MCP / OpenAPI | **None** - existing routes only | No snapshot regeneration, no MCP coverage change |
| Studio REST wrappers (`api/endpoints.ts`) | `getInDegree`, `getOutDegree`, `embeddingSearch` and the four adjacency wrappers gain an optional trailing `signal?: AbortSignal` (the `findPaths` precedent), which is what makes a batched sweep cancellable rather than merely ignored; `fetchVertexNeighborhood` takes one too and threads it | Additive, no caller changes. Recorded here because the pre-implementation table said "existing routes only" and this is the one place that was not literally true |
| Canvas store (`instanceStore.ts`) | Adds `removeManyFromCanvas` and a `CANVAS_TABS` / `isCanvasTab` guard, and normalizes a persisted `tab` on rehydration (the `isTraverseTab` precedent) | An unknown persisted tab id would otherwise render a strip with nothing selected and an empty area under it |
| `lib/vectorSearch.ts` (new) | ONE home for the engine's kNN ceiling `MAX_K`, which `QueryScreen` had as a local literal | Both callers now share it; a k from any other quantity is a 400, not a degradation |
| Canvas Detail panel ([canvas-view-controls](../../done/canvas-view-controls/spec.md)) | Expand implementation moves to the shared lib; buttons and behaviour unchanged | Existing canvas tests must stay green unmodified |
| [canvas-find-connect](../../done/canvas-find-connect/spec.md) | Fourth tab in the strip; FindPanel/ConnectPanel untouched; the eclipse hover-spotlight is reused | Tab-strip tests extend, none change meaning |
| Style tab / `styleEngine` | Degree-based node sizing untouched; its exported `visibleDegrees` gains a second caller (the on-canvas degree source), so there is ONE home for counting canvas degree | No behaviour change in styling; the filter's field help says which source reads what |
| Canvas store (`instanceStore.ts`) | `CanvasToolsDraft` extended | Merge-defaults, no migration |
| [element-embeddings](../../done/element-embeddings/) / vector search | Semantic filter consumes `POST /embedding/search` and the raw-score contract verbatim | Gated on provider + bound index, like Find similar |
| [studio-list-caps policy](../../done/index-workspace/README.md) | Match preview list | `SCROLL_ROWS`/`scrollRows`, `capList` + `ListCapNote` |
| docs-site `studio.md` | Canvas section describes three tabs | Describe Interact (one paragraph, the docs page owns the story); **recapture the affected canvas screenshot(s)** - the tab strip is visible in them |
| README "Key features" | No new bullet, no new docs page | studio.md is the home; augment the canvas wording only if it sharpens |
| nl-assist-finetune | No delegate/NL surface change | Reviewed: no retrain, no `RETRAIN-LOG.md` entry |
| Architecture diagrams | No new channel or deployable | No change |

## Non-goals (with revisit triggers)

- **Edge-targeted operations** (remove every edge of one type). Vertices carry the workflow;
  removing vertices already removes their edges. Revisit if a real session needs edge pruning
  that vertex removal cannot express.
- **OR-composition, filter groups, saved filters.** AND over four rows covers the asks;
  composition UI is where panels go to die. Revisit with a concrete query that AND cannot say.
- **Set-wide visual emphasis of the match set** (dimming non-matches like the path overlay).
  The overlay is single-path today; generalizing it is renderer work in both canvases. Preview
  is count + list + per-row eclipse in v1. Revisit if previews prove hard to trust without it.
- **Multi-hop expand.** One hop per sweep, run it again to go deeper; the Traverse screen is
  the home for real walks. Revisit only alongside a server-side neighborhood route.
- **A server-side bulk expand/degree route.** The client sweeps are capped and honest; if
  canvases outgrow them, the fix is a REST route (which then fires the MCP propagation rule),
  not a bigger client cap.
- **Selection gestures (lasso, ctrl-click) as a match source.** Same non-goal as
  canvas-find-connect; the filters are the v1 selection mechanism.
