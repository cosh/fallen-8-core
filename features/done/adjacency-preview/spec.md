# Adjacency preview

## Problem

The Browser screen's element detail is a dead end visually and navigationally:

- **Vertex adjacency is numbers-only.** The adjacency panel shows degrees and
  per-property edge-id chips. There is no way to *see* the 1-hop neighborhood without
  round-tripping through "Send to canvas" and the Canvas screen.
- **An edge gets no adjacency surface at all.** The right column is empty; the only
  structural information is the `#source → #target` text line in the detail header.
  Parallel edges between the same two vertices are invisible.
- **Element-to-element navigation is broken.** `InspectLink` navigates to
  `/browser?id=N`, but nothing consumes that search param — clicking an endpoint or an
  edge-id chip changes the URL and nothing else. (The bulk table's `onInspect` callback is
  the only navigation that actually works.)
- **Parallel edges overlap on the canvas.** Sigma renders multi-edges as coincident
  straight lines, so only one of N parallel edges is ever visible — relevant here because
  the edge preview's entire point is showing the parallel bundle.

## Contract

### Vertex: adjacency panel gains a rendered view

The adjacency panel gets a two-way view toggle, **graph** (default) | **stats**:

- **stats** keeps the per-property out/in chips and the edge-id expansion; the degree
  line moved above the toggle and stays visible in both views.
- **graph** renders the vertex's 1-hop neighborhood as a small 2D force-layout preview —
  the same renderer, styling rules, and force layout as the Canvas screen (label-hashed
  colors, arrows on, labels on). The focus vertex is emphasized (path-overlay size bump)
  without dimming the neighbors.
- Clicking a node or an edge in the preview navigates the Browser screen to that element
  (same in-screen lookup as the bulk table). Clicking the stage does nothing.
- The neighborhood fetch caps BOTH edge and endpoint-vertex hydration at
  `PREVIEW_EDGE_CAP` (60); hitting either cap shows the truncation badge ("capped at
  60") — endpoints beyond the cap still render, as label-less stubs. Failed element
  hydrations are skipped, not errors.

### Edge: a neighborhood panel appears

When the looked-up element is an edge, the right column renders a preview showing the
source vertex, the target vertex, **all** edges between them (both directions, found by
intersecting the endpoints' per-property edge-id lists — never by hydrating a
supernode's full edge set), with the current edge highlighted in the path-overlay accent.
Arrows show each edge's direction; parallel edges render curved so the bundle is
readable. A caption states the parallel-edge count. Clicking either endpoint vertex or a
sibling edge navigates to it.

### Hops are seamless

Traversing element to element must feel like moving through the graph, not like page
loads:

- The shown element is screen state set on lookup success, NOT the mutation's `data`
  (which resets while pending) — so the detail and adjacency panels stay mounted while
  the next element loads; nothing below the lookup form flickers or shifts.
- The neighborhood query keeps the previous graph as placeholder data
  (`keepPreviousData`): the canvas instance persists across hops and morphs to the new
  neighborhood when it lands; the caption shows `…` while the placeholder is up. One
  `AdjacencyPanel` serves both kinds so vertex↔edge hops reuse the same preview instance.
- Clicking the element already on screen (the focus vertex, the current edge) is a
  no-op — the guard is id equality in the screen's single `inspect(id)`. A FAILED hop
  keeps the previous element on screen with the error box above it (previously the
  detail blanked while an error showed).

### Navigation: one mechanism

`InspectLink` drops the dead router navigation and becomes a pure callback
(`onInspect(id)`). The Browser screen owns the single `inspect(id)` implementation
(`getGraphElement` lookup, same as the bulk table path) and threads it into
`ElementDetail` (endpoint links), `AdjacencyPanel` (edge-id chips + preview), and the
edge neighborhood panel. Endpoint/edge-id links therefore *work* now.

### Rendering: shared, not forked

- The preview reuses `GraphCanvas`/`Canvas2D` — no second Sigma setup.
- `PathOverlaySets` gains a `dim` flag: the Canvas screen's path overlay passes
  `dim: true` (behavior unchanged); the preview's emphasis passes `dim: false` so the
  focus element gets the overlay visuals (size/width bump, accent edge color, z-index)
  without greying out its context. `GraphCanvas` gains an optional `emphasis` prop.
- `Canvas2D` indexes parallel edges (`@sigma/edge-curve`) and renders them curved with
  spread curvatures. This applies to the main canvas too — a strict visual improvement
  (previously coincident lines).

### Data: shared, not forked

- The Canvas screen's expand-neighbors fetch (per-property edge-id lists → hydrate edges
  with `edgePropertyId` attribution → hydrate endpoint vertices) moves to
  `lib/neighborhood.ts` as `fetchVertexNeighborhood`; the expand mutation and the vertex
  preview call the same function (expand keeps its cap of 200, the preview passes 60).
- `fetchEdgeNeighborhood` finds the parallel bundle by id-list intersection:
  `out(source) ∩ in(target)` plus `in(source) ∩ out(target)`, then hydrates only that
  intersection. The focus edge is always included even if its own hydration fails.
- The REST-elements → canvas-model conversion (`snapshotProps`, endpoint stubs, label
  fallback to `edgePropertyId`) is extracted from the store's `mergeIntoCanvas` into a
  pure `buildCanvasModel`; the store and the preview both use it.

## Non-goals

- No new server endpoints; the preview composes the existing per-property adjacency
  routes. A dedicated neighborhood endpoint is a separate feature if the request
  amplification ever hurts.
- No 3D preview, no style/layout controls on the preview — it is a fixed-config canvas
  teaser; the Canvas screen remains the workbench.
- No deep-linkable `/browser?id=` route param. The dead search-param write is removed
  with `InspectLink`'s router coupling; URL-addressable element lookup would be its own
  small feature.

## Impact on existing features

- **studio-canvas-viz**: `PathOverlaySets` gains `dim` (path overlay passes `true`,
  pinned visuals unchanged); `GraphCanvas` gains the optional `emphasis` prop;
  `Canvas2D` renders parallel edges curved (main-canvas visual improvement, arrows and
  labels unchanged); the expand-neighbors mutation now delegates to
  `fetchVertexNeighborhood` (behavior-preserving refactor, same caps). Two latent
  canvas bugs are fixed in passing: Sigma v3 ships with edge events DISABLED, so the
  canvas's `clickEdge` handler (edge selection → detail panel) had never fired —
  `enableEdgeEvents: true` turns it on; and `minEdgeThickness: 2.5` floors the rendered
  (= clickable) edge geometry so width-1 edges are an actually hittable target. Resolved
  style widths are untouched (width-by-property contrast is visually compressed below
  2.5px until zoomed — accepted for clickability). Both renderers' mount-once click
  handlers now read the live `onSelect` through a ref; the frozen-closure was latent on
  the Canvas screen (its handler is a stable setState) and manifest in the preview,
  where the same-id guard would compare against the mount-time element (council
  finding, pinned by canvas2d-select.test.tsx).
- **web-ui (Browser screen)**: `InspectLink` changes contract from router navigation to
  callback — its two call sites are both on this screen; endpoint and edge-id links go
  from dead to functional.
- **Engine / REST / OpenAPI**: untouched — client-only feature, no new routes used
  (`api-contract.test.ts` unaffected).
- **NL-assist dataset, stored queries, recipes**: untouched.
