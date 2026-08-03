# Canvas find & connect - spec

## Problem

The Canvas is the studio's curation surface: a working set of elements you assemble from the
other screens, style, and explore. But once you are ON the canvas, the workflow dead-ends in
two ways:

1. **You cannot look anything up without leaving.** Growing the view means a round trip to the
   Browser or Query screen and back. The [all-property search](../../done/all-property-search/spec.md)
   just built the perfect discovery primitive ("does `acme` appear anywhere?"), but reaching it
   from the canvas costs a context switch that loses your visual train of thought.
2. **The canvas shows islands and cannot answer "how are these connected?".** Elements arrive
   from independent result sets, so related vertices sit next to each other with no edges
   between them. Fallen-8's path search (`POST /path/{from}/to/{to}`) answers exactly that
   question, but only one pair at a time, on another screen, with no way to add or retract the
   found connections selectively.

The right-hand panel currently holds exactly one tool (Style). This feature makes it a small
tool strip: **Style | Find | Connect**.

This is a **Studio-UI-only feature**. Both tabs reuse REST operations that already exist
(`POST /scan/graph/properties`, `POST /path/{from}/to/{to}`). No engine method, no REST route,
no MCP tool, no OpenAPI change - the engine -> REST -> MCP propagation rule does not fire.

## Decisions (locked with the requester)

| Decision | Choice | Note |
|---|---|---|
| Placement | Tab strip in the Canvas right panel: `Style \| Find \| Connect` | Style stays the default tab; the selection-driven Detail panel stays below, independent of the active tab. |
| "Connect" naming | The requested "Enrich" tab ships as **Connect** | The requester flagged "Enrich" as a placeholder name; Connect says what it does. |
| Find scope | **All-property contains search only** | Term + optional label + result type, reusing `POST /scan/graph/properties`. No named-key or index forms in the panel (the Query screen remains their home). |
| Find output | Compact result list + the existing Detail panel | Rows show kind, id, label, an on-canvas indicator, and a per-row add action; clicking a row selects it into the Detail panel below ("more details" costs zero new UI). "Send all" adds everything. Works for edges too. |
| Connect endpoints | **All canvas vertices by default, optionally a picked subset** | The pick list lives in the panel (checkboxes over the canvas vertices); no canvas multi-select gesture in v1. |
| Connect power | **Lean: shortest path + max hops** | BLS, one path per pair (`maxResults: 1`), a max-hops input (default 3). The Path screen remains the home for algorithms, filters, costs, and semantic traversal. |
| Pairing | **One query per unordered pair** | BLS expands frontiers over incoming AND outgoing edges (`GetLocalFrontier`), so a->b finds the same connections as b->a; querying both would double the cost for nothing. Lower id is the source, deterministically. |
| Pair explosion | **Hard pair cap; refuse to run above it** | Partial pairwise coverage would be a lie ("no path" for pairs never tried). Over the cap the run button is disabled with an honest count and the pick list as the narrowing tool. |
| Path bookkeeping | **Selective add/remove with baseline + reference counts** | Removing a path removes only what that path introduced and no other kept path still claims. |

## Behaviour after the change

### 1. The tool strip

The Canvas aside's first panel becomes tabbed: `Style | Find | Connect`. Style renders the
existing `StylePanel` unchanged. The active tab persists in the per-instance store, so
returning to the canvas restores it. The Detail panel below is unaffected by tabs: selecting
any element (canvas click or Find row click) shows it there, exactly as today.

### 2. Find tab

A vertical form in the 320px panel:

- **search term** (required; run disabled while blank - a blank term is a 400 server-side),
  **label** (optional exact-match restrictor, suggested from the graph-shape datalist),
  **result type** (`Vertices | Edges | Both`, default `Both`).
- Run calls `scanProperties(instance, { searchTerm, label?, resultType })` - the same endpoint
  wrapper the Query screen's "any property" scope uses - then hydrates the ids via
  `hydrateElements` (existing 500 cap, visible progress, honest "(first 500 hydrated)" note
  when capped).
- **Result list** (compact, height-capped per the list-caps policy): one row per element with
  a kind marker (vertex/edge), the id, the label, an **on-canvas indicator** (live: derived
  from `canvasNodes`/`canvasEdges`, so adding flips it immediately), and a per-row **+ canvas**
  action. The row's id is a button: clicking selects the element into the Detail panel below,
  which fetches and shows its full properties whether or not it is on the canvas (the detail
  query already works server-side). This is the "request more details" affordance.
- **Send all to canvas** merges every hydrated element (vertices and edges split, the same
  `mergeIntoCanvas` call shape as the Query screen). A lone edge brings stub endpoint nodes,
  as everywhere else (`buildCanvasModel`). Re-adding an element already on canvas is a
  harmless merge that refreshes its property snapshot.
- Results are ephemeral (component state, like Query results); the term/label/result-type
  inputs persist in the draft (below).

Deliberately NOT a second Query screen: no operator, no typed literal, no index forms, no
result-set recording (`addResultSet` is Query-screen bookkeeping; Find is canvas curation).

### 3. Connect tab

Answers "which of the vertices on this canvas are connected, and how?" by running the existing
path search over vertex pairs:

- **Scope**: `all vertices | pick vertices` toggle. `all` uses every canvas vertex (stub nodes
  included - they are canvas vertices). `pick` shows a height-capped checkbox list of the
  canvas vertices (id + label) with a small client-side filter input (id or label substring,
  the Browser bulk-filter precedent). Picked ids are ephemeral session state; the scope choice
  persists.
- **max hops**: number input, default 3, min 1 (maps to `maxDepth`).
- **Pair budget**: the panel always shows `N vertices -> P pairs`. Above `CONNECT_PAIR_CAP`
  (500 pairs, one named constant) the run button is disabled with the honest message and the
  pick list as the way to narrow. Below 2 endpoints the run is disabled too.
- Run issues one `findPaths(instance, lowId, highId, { pathAlgorithmName: "BLS", maxDepth,
  maxResults: 1, maxPathWeight: Number.MAX_VALUE })` per unordered pair, in small concurrent
  batches (`CONNECT_BATCH_SIZE = 8`) with visible progress (`pair X/Y`) and a **Cancel** button
  (an `AbortSignal` threaded through `findPaths`, which gains an optional trailing `signal`
  parameter - the `getStatus` precedent). Cancelling keeps the rows found so far and says
  "cancelled after X of Y pairs". A failed pair request is counted and reported ("E pair
  searches failed"), never silently swallowed.
- **Results**: a summary line (`F connections found - U pairs unreachable within H hops`) and
  one row per FOUND path: `a -> b`, hop count, how many new elements it would introduce, and
  an **Add / Remove** toggle; plus **Add all**. Unreachable pairs are counted, not listed
  (with mostly-disconnected canvases the misses are noise). Results are ephemeral; a new run
  replaces them.

**Add/remove bookkeeping** (the honest part):

- When a run starts, the canvas element ids are snapshotted as the **baseline**.
- Each found path's **introduced set** = its path vertex ids minus baseline vertices, plus its
  path edge ids minus baseline edges (the pair's endpoints are on the canvas by construction,
  so a direct edge introduces just the edge).
- **Add** hydrates the introduced vertices (`getGraphElement`, so labels and property snapshots
  are real), synthesizes the introduced edges from the path elements (id, endpoints, type -
  the Path screen's overlay precedent; an edge label/props arrive only if the element is later
  re-merged from a hydrating flow), and merges ONLY the introduced elements - a baseline
  element's property snapshot is never clobbered by a synthesized stand-in.
- **Remove** removes this path's introduced elements EXCEPT those claimed by another
  currently-added path (a shared intermediate stays until its last claiming path is removed).
  Claimed edges always have claimed endpoints (an edge's endpoints are on every path the edge
  is on), so removal is consistent; removing is per-element `removeFromCanvas`, and removing an
  id the user already removed manually is a clean no-op. The bookkeeping is honest against the
  run-time baseline; manual canvas edits between add and remove are tolerated best-effort.
- The single-path `pathOverlay` (Path screen dim-highlight) is not used or touched: Connect
  merges elements, it does not overlay.

### 4. State, help, and shared pieces

- One new persisted draft in the per-instance store (`instanceStore.ts`), merge-defaulted like
  every draft (no migration):

  ```ts
  canvasToolsDraft: {
    tab: "style" | "find" | "connect";      // default "style"
    findTerm: string;                        // default ""
    findLabel: string;                       // default ""
    findResultType: "Vertices" | "Edges" | "Both"; // default "Both"
    connectMaxDepth: number;                 // default 3
    connectScope: "all" | "pick";            // default "all"
  }
  ```

- Pure logic lives in a new `src/lib/connectPaths.ts` (the `neighborhood.ts`/`hydrate.ts`
  idiom): pair building (unordered, dedup, cap), introduced-set derivation, and the removal
  claim computation - all unit-testable without a DOM.
- Field help reuses existing keys where the concept is identical (`searchTerm`, `searchLabel`,
  `scanResultType`, `pathMaxDepth` - one home per explanation); new keys only where the concept
  is new (`connectScope`, and a `canvasTools` entry for the tab strip if a labeled control
  needs it).
- New panel components `src/canvas/FindPanel.tsx` and `src/canvas/ConnectPanel.tsx` beside
  `StylePanel.tsx`; `CanvasScreen` owns the tab strip and passes the store handles down.

## Impact on existing features

| Feature / layer | Impact | Handling |
|---|---|---|
| Engine / REST / MCP / OpenAPI | **None** - both tabs consume existing operations | No snapshot regeneration, no MCP coverage change, no `AppJsonContext` change |
| [all-property-search](../../done/all-property-search/spec.md) | Find reuses `scanProperties` + the search semantics verbatim | Sequencing: this feature branches from `main` AFTER `feature/all-property-search` merges (it needs `scanProperties`) |
| Path search / [stored-query-library](../../done/stored-query-library/) | Connect reuses `findPaths` with a fixed lean spec; `findPaths` gains an optional trailing `signal?: AbortSignal` (additive) | No stored-query / filter / semantic surface in the panel; the Path screen remains their home |
| Canvas store (`instanceStore.ts`) | New `canvasToolsDraft` + setter/reset | Merge-defaults in the persist `merge`, same pattern as `queryDraft`; old persisted state needs no migration |
| `pathOverlay` / `styleEngine` | Untouched | Connect merges; it does not overlay. Extending the overlay to multiple paths is a non-goal below |
| `ElementTable` | Untouched | The Find list is a purpose-built compact list for the 320px panel; the wide table stays the wide screens' component |
| Canvas tests (`canvas-view-controls.test.tsx`, `canvas2d-select.test.tsx`, `style-panel.test.tsx`) | Must stay green | Style is the default tab, so the aside renders `StylePanel` on mount exactly as before |
| e2e `studio.spec.ts` | Canvas scenarios must stay green | Default-tab behaviour preserved; the e2e is not extended for the new tabs (vitest pins them) |
| [studio-list-caps policy](../../done/index-workspace/README.md) | Find list, pick list, and results list | Height-capped via `SCROLL_ROWS`/`scrollRows`; hard ceiling via `capList` + `ListCapNote` where a list can exceed it |
| docs-site `studio.md` | Canvas section describes one tool (Style) | Rewrite the Canvas section around the three tabs; recapture the affected canvas screenshot(s) - the style-panel shot now shows the tab strip - and add one shot of Connect with found paths |
| README "Key features" | Canvas/Studio bullet wording | Augment if it sharpens the story; no new bullet, no new docs page (studio.md is the home) |
| nl-assist-finetune | No delegate/NL surface change | Reviewed: **no retrain**, no `RETRAIN-LOG.md` entry |
| Architecture diagrams | No new channel or deployable | No change |

## Non-goals (with revisit triggers)

- **Named-key / index query forms in the Find tab.** The Query screen owns them. Revisit if
  users demonstrably run operator scans from the canvas workflow.
- **Canvas multi-select gestures (ctrl-click, lasso) as the Connect pick mechanism.** That is
  renderer work in both Sigma and three.js; the panel checkbox list is the v1 pick. Revisit
  when a selection feature is wanted for more than Connect.
- **Algorithm choice, K paths per pair, filters, costs, semantic blocks in Connect.** Lean by
  decision; the Path screen has the full instrument panel. Revisit if a real session needs
  weighted or filtered connecting.
- **Direction-sensitive pairing (a->b vs b->a as distinct questions).** BLS traverses both
  edge directions, so undirected pairing is complete for "are these connected". Revisit only
  with a directed-reachability use case (which would also want a different algorithm surface).
- **Partial pairwise runs above the cap.** Refusing is honest; running "the first N pairs"
  silently is not. Revisit with server-side batch path search if canvases outgrow the cap.
- **Multi-path dim-overlay highlighting.** `pathOverlay` stays single-path (Path screen).
  Revisit if Connect users ask to SEE the found connections emphasized rather than merged;
  that lands in `styleEngine` as a union overlay.
- **Recording Find results as result sets (`addResultSet`).** Find is canvas curation, not
  query bookkeeping. Revisit if result-set reuse from the canvas is requested.
- **Persisting found paths / added-path bookkeeping across sessions.** Results and picks are
  session-ephemeral like every other result in the studio; only input drafts persist.
