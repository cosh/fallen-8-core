# Studio canvas visualization

Data-driven styling, more layouts, and a 3D projection for the Studio canvas.
Everything is configured on the **Canvas screen → style panel** (right side), in four
sections; every control has hover help. The configuration is per instance and persists
in the browser. See [spec.md](spec.md) for requirements, [plan.md](plan.md) for the
implementation phases.

## Renderer & layout

Both projections are WebGL and render the same canvas, path overlays included:

| Renderer | Engine | Layouts | Labels |
| --- | --- | --- | --- |
| 2D (default) | Sigma.js v3 | force (FA2), circular, circle pack (by label), grid, random | on canvas |
| 3D | three.js (`3d-force-graph`) | force (d3-force-3d), dag · top-down, dag · radial | on hover |

DAG modes tolerate cycles (best effort). Switching renderer keeps the canvas contents;
2D positions and the 3D simulation are independent.

## Color, size, width

- **Node / edge color by property**: values hash onto the same stable palette as
  labels. If *every* present value is numeric, nodes shade along a cyan→pink min→max
  gradient instead, and the legend shows the ramp with min/max.
- **Node size**: a numeric property (min-max scaled), or in-/out-/total **degree
  counted over the edges currently on the canvas** — expand neighbors and sizes update.
- **Edge width**: a numeric property, min-max scaled; non-numeric values keep the
  default width.

Elements missing the chosen property render in the unlabeled color / default size.

## Node images & emoji

Set **image / emoji property** to a property id (e.g. `icon`). Per node, the value
decides what renders as the node, in 2D and 3D:

| Property value | Rendered as |
| --- | --- |
| `https://example.org/logo.png` (or `http://`, `data:`) | the image at that URL |
| `🦊` — or any other short text | the emoji/text itself, rasterized by the Studio |

Emoticons are built in: the Studio draws them onto a transparent texture locally, so
they need no hosting and work offline. Remote images must be publicly fetchable from
the *browser* (CORS applies). Nodes without the property keep their styled circle.
Long text values are capped at 8 characters.

Example: give vertices `PUT /vertex` properties like
`{ "propertyId": "icon", "propertyValue": "🦊", "fullQualifiedTypeName": "System.String" }`,
send them to the canvas, and set the image property to `icon`.

## Path & subgraph visualization

Unchanged, and identical in 2D and 3D: path results overlay the canvas (path members
highlighted, everything else dimmed — dimmed nodes also drop their images so the path
stands out), subgraph/query/browser results merge into the canvas as before.

## Notes

- Property snapshots on the canvas keep scalars only (strings capped at 200 chars);
  arrays/objects such as embeddings are never persisted to browser storage.
- Past 5 000 rendered elements, labels are dropped regardless of the toggles.
- The 3D engine loads lazily — instances that stay 2D never download three.js.
