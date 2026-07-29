# Edge type vs label: one story, told everywhere

## Problem

Every graph element carries an optional free-form `label`
(`AGraphElementModel.Label`). Edges additionally carry `edgePropertyId`
(`EdgeModel.EdgePropertyId`): the name of the adjacency group the edge occupies on both
endpoints. `edgePropertyId` is structural - it is required at creation, keys every traversal
read (`GET /vertex/{id}/edges/out/{edgePropertyId}`), the path `edgePropertyFilter`, subgraph
pattern matching, and analytics scoping. `label` is a decorative category tag shared with
vertices, used by scans, bulk-export filters, and statistics.

The engine keeps the two cleanly apart. The product blurs them:

1. **`edgePropertyId` is write-only.** `PUT /edge` requires it, but the `Edge` read DTO
   returns only `label` - the one field that types an edge cannot be read back from the edge
   (only reconstructed via the vertex adjacency routes, which Studio does by hand in
   `neighborhood.ts`). Change-feed `edgeCreated` events drop it too, so a live-fed Studio
   canvas shows new edges untyped.
2. **The public contract contradicts itself.** The `PUT /edge` remarks sample swaps the two
   fields (`label: "knows", edgePropertyId: "friendship"`) against the `EdgeSpecification`
   schema example (`edgePropertyId: "knows", label: "friendship"`); the same remarks sample
   also shows `creationDate` as an ISO string (it is a Unix-seconds uint) and `properties` as
   a map (it is an array).
3. **Examples teach that the two are the same string.** `PatternSpecification` /
   `SubGraphSpecification` examples filter `p == "knows"` and `e.Label == "knows"` in one
   spec; the graph-model page's edge body sets both to `"trusts"`; the subgraphs walkthrough
   creates edges whose label duplicates the type.
4. **Studio renders three different label/type fallback orders** (store: `label ??
   edgePropertyId`; 2D canvas: `edgePropertyId` only; 3D canvas: `edgePropertyId ?? label`),
   and the store's `CanvasEdge.label` silently substitutes the type when the label is absent.

## Decision (terminology)

Both concepts stay - they do different jobs - and the product tells one story about them:

- **`edgePropertyId` is the edge's *type*.** Required. It names the adjacency group the edge
  occupies on its endpoints; traversal, filtering, and scoping key on it. Despite the name,
  it is not one of the edge's key/value properties.
- **`label` is an optional category tag,** the same field vertices have. On edges it is an
  orthogonal, human-facing grouping (type `suppliesTrojan`, label `supplies trojan`) - not a
  second copy of the type. Most graphs leave it unset.

The single home for this explanation is the **Graph model** docs page
(`docs/src/content/docs/graph-model.mdx`); every other site (XML docs, field help, tool
descriptions, feature docs) states its one line and points there. The wire name
`edgePropertyId` is kept everywhere (see rejected alternatives).

## Changes

### A. Read surface: `edgePropertyId` becomes readable wherever `label` is

- `Edge` REST DTO (`Controllers/Model/Edge.cs`) gains `edgePropertyId` (required, from
  `EdgeModel.EdgePropertyId`). Flows to `GET /edge/{id}`, `GET /graph`,
  `GET /graphelement/{id}`, and everything else that serializes an edge.
- Change feed: `ChangeDescriptor.Item`/`Builder.EdgeCreated` and `ChangeEvent` carry
  `EdgePropertyId` for `edgeCreated`; `ChangeEventREST` emits it as `edgePropertyId`
  (absent for other kinds, like `source`/`target`).
- MCP `get_element` projection (`Bridge/ElementProjection.cs`) copies `edgePropertyId`
  through next to `label`.
- Studio: `EdgeREST` gains the field (the `CanvasEdgeInput` bolt-on type collapses into it);
  `neighborhood.ts` prefers the server-sent value over its adjacency-map reconstruction;
  `liveFeed.ts` types live-created edges from the SSE event.

### B. Contract examples stop contradicting and conflating

- Fix the `PUT /edge` remarks sample (swapped fields, wrong `creationDate` shape, wrong
  `properties` shape) to match the `EdgeSpecification` example (`edgePropertyId: "knows"`,
  `label: "friendship"`).
- `PatternSpecification` / `SubGraphSpecification` examples use a label value distinct from
  the edge-property value (aligned with `PathFilterSpecification`, which already does).
- XML docs on `EdgeSpecification.EdgePropertyId` / `.Label` and `Edge` state the one-line
  distinction.

### C. Docs tell the story once

- `graph-model.mdx` gets a short "Edge type vs label" subsection (the one home); the edge
  create example drops the duplicated label.
- `subgraphs.mdx` walkthrough de-conflates its example graph and shows the new
  `edgePropertyId` response field; `change-feed.mdx` documents the new event field; pages
  showing edge response bodies are refreshed.
- `TestGraphGenerator` keeps its values (tests and docs examples pin them) but its comment
  explains the deliberate mix: `trusts` edges carry a label so label-based filter examples
  have something to match; the rest leave it null because labels are optional.

### D. Studio renders one rule

`CanvasEdge.label` becomes truthful (no fallback); every display site uses **`label ??
edgePropertyId`** (human-readable tag when present, the always-present type otherwise) -
store, 2D edge-label text, 3D hover name, and the color-by-label scale key. For graphs
without edge labels (all bundled samples except cyber-warfare) rendering is pixel-identical.

## Rejected alternatives

- **Renaming the wire field** (`edgeType` or similar): breaks every client, the JSONL bulk
  format, MCP tools, and stored samples for a purely cosmetic gain. Revisit only with a
  v2 API surface.
- **Deprecating edge `label`:** it serves scans, bulk-export filters, statistics, and the
  human-facing display case (cyber-warfare sample); removal is a model change with
  persistence impact and no user demand.
- **Defaulting `label` to `edgePropertyId` at creation:** would bake the conflation into the
  data instead of removing it from the product.

## Impact on existing features (mandatory sweep)

- **REST contract / OpenAPI snapshot:** `Edge` and `ChangeEventREST` schemas gain one
  optional-looking (additive) field each; the `PUT /edge` remarks sample changes. Snapshot
  regenerated via `scripts/update-openapi-snapshot.ps1`; additions only, no removals.
- **MCP server:** no new REST operations, so `McpRestCoverageTest`/`McpContractTest` need no
  new bridging; `get_element` output gains `edgePropertyId` (additive).
- **Studio UI:** additive DTO field; display unification is pixel-identical for label-less
  edges. Cyber-warfare (the one sample with distinct label/type) renders its human-readable
  edge label where the machine key showed before. Docs screenshots verified unaffected: the
  cyber-warfare canvas captures use the 3D renderer (edge names are hover-only tooltips,
  colors key on the same display name as before), and the only 2D-canvas capture
  (`screen-canvas-style`) contains no edges - so no recapture is required.
- **NL-assist dataset/eval:** fragments compile against the engine's delegate-visible types
  (`EdgeModel`), which are unchanged - no retrain entry needed.
- **Architecture diagrams:** no channel, layer, or deployable changes - untouched.
- **Persisted recipes/stored queries, persistence, WAL:** untouched (`EdgeModel` and its
  serialization are unchanged; the change feed is in-memory).
- **Feature docs:** `features/done/adjacency-preview` (a historical spec, not rewritten)
  describes the client-side `edgePropertyId` reconstruction this feature demotes to a
  fallback; the living explanation moves to the code site in `neighborhood.ts`.

## Tests

- REST: `GET /edge/{id}` and `GET /graph` return `edgePropertyId`; the value round-trips
  from `PUT /edge`.
- Change feed: `edgeCreated` events carry `EdgePropertyId` (engine) and `edgePropertyId`
  (SSE REST mapping); other kinds omit it.
- MCP: `get_element` projection surfaces `edgePropertyId` for edges.
- Existing suites (path, subgraph, bulk, statistics) pin that nothing else moved.
