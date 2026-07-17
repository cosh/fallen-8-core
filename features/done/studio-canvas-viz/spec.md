# Studio canvas visualization — data-driven styling, more layouts, 3D renderer

## Problem

The Studio canvas renders every vertex as a same-sized dot colored by label and every
edge as a hairline. Real graphs carry meaning in their properties (scores, categories,
icon URLs, degrees) that the canvas cannot show. There is also exactly one placement
family (ForceAtlas2 or a circle) and exactly one projection (2D).

## Decisions

- **2D and 3D, not 2D or 3D.** The existing Sigma.js v3 canvas is already WebGL and
  stays the default. A 3D renderer (three.js via `3d-force-graph`, also WebGL) is added
  as a per-instance choice. Both live behind the existing `<GraphCanvas>` boundary
  (web-ui design §4), so every producer — browser, query, path, subgraph — keeps
  working unchanged in both projections: they all talk to the canvas through
  `mergeIntoCanvas` and `pathOverlay` only.
- **Styling is resolved once, rendered twice.** A pure style engine maps
  (canvas model, style config, path overlay) → resolved per-element visuals
  (color, size, image, width). Both renderers consume the resolved visuals; neither
  contains mapping logic. This is the "one home" for the styling rules.
- **Degree means visible degree.** Size-by-degree counts edges currently on the canvas
  (the view is a working set, not the whole graph); it updates live as neighborhoods
  are expanded. True store-side degree stays on the browser screen's degree readout.

## Functional requirements

- **FR-1 Node color by property.** Color mode `label` (today's behavior, default) or
  `property`: the user names a property id; values hash onto the existing stable
  palette. When every present value is numeric, a min→max two-color gradient is used
  instead. Elements missing the property render in the unlabeled color.
- **FR-2 Edge color by property.** Same modes as FR-1, applied to edges
  (default remains label-hash with the muted fallback).
- **FR-3 Node size.** Modes: `fixed` (default), `property` (numeric values min-max
  scaled into the node size range; non-numeric → default size), `in-degree`,
  `out-degree`, `total degree` (visible degree, scaled the same way).
- **FR-4 Edge width by property.** `fixed` (default) or `property` with numeric
  min-max scaling into the edge width range.
- **FR-5 Node images.** The user names an image property. Per node: an `http(s)://` or
  `data:` URL value renders that image as the node; any other non-empty scalar (an
  emoji like 🦊, or short text) is rasterized to a texture by the Studio itself —
  emoticons are built in, no hosting needed. Nodes without the property keep their
  styled circle. Works in 2D (Sigma image node program) and 3D (three.js sprites).
- **FR-6 Layout choice per renderer.** 2D: `force` (FA2, default), `circular`,
  `circlepack` (grouped by label), `grid`, `random`. 3D: `force` (d3-force-3d,
  default), `dag top-down`, `dag radial`. The layout control only offers layouts
  valid for the active renderer.
- **FR-7 More render options.** Toggles for node labels, edge labels, and directed
  arrowheads, honored by both renderers (3D shows labels on hover — that is the 3D
  labeling idiom). The existing degrade threshold (drop labels past 5 000 elements)
  still wins over the toggles.
- **FR-8 Style panel in sections.** All of the above is configured on the canvas
  screen in a collapsible style panel with sections — Renderer & layout / Nodes /
  Edges / Labels & effects. Every control has hover help (`fieldHelp`). Config is
  per-instance and persisted (same store/persistence as the canvas itself); defaults
  reproduce today's rendering exactly.
- **FR-9 Path & subgraph viz parity.** The path overlay (dim non-path elements,
  highlight path edges) and subgraph/query/browser "send to canvas" flows work
  identically in 2D and 3D. Overlay highlighting takes precedence over FR-1..FR-4
  styling for dimmed elements; path members keep their styled color with a size boost.
- **FR-10 Legend follows the color mode.** Label mode keeps the label legend;
  property mode shows value swatches, or a min→max gradient ramp for numeric.
- **FR-11 Properties reach the canvas.** Canvas nodes/edges carry a snapshot of their
  scalar properties (strings capped, arrays/objects skipped — embeddings must not
  bloat local storage). Elements persisted before this feature simply have no
  snapshot and render with defaults until re-merged.

## Non-goals

- No server/API changes; this is entirely `fallen-8-web-ui`.
- No per-element manual style overrides ("make this one node red").
- No store-side degree queries for sizing (visible degree only, see Decisions).
- No VR/AR modes, no edge images, no curved-edge program.

## Revisit triggers

- Users ask to size by true store degree → add a degree hydration pass.
- Property snapshots blow local-storage quota despite the caps → move canvas state
  to IndexedDB.
