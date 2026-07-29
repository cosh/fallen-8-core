# Plan: edge-type-vs-label

Phases are ordered so the contract lands first and every dependent surface follows in the
same branch. Spec: [spec.md](spec.md).

## Phase 1 - REST read surface (spec A, B)

1. `Edge` DTO: add `edgePropertyId` (from `EdgeModel.EdgePropertyId`), one-line XML doc
   pointing at the graph-model page; update the class example.
2. Fix the `PUT /edge` remarks sample in `GraphController.Edge.cs` (field swap, `creationDate`
   shape, `properties` shape) to match the `EdgeSpecification` class example.
3. Sharpen XML docs on `EdgeSpecification.EdgePropertyId` / `.Label`; de-conflate the
   `PatternSpecification` / `SubGraphSpecification` examples.
4. Unit tests: `GET /edge/{id}` / `GET /graph` carry the created `edgePropertyId`.

## Phase 2 - change feed (spec A)

1. `ChangeDescriptor.Item` + `Builder.EdgeCreated(id, label, edgePropertyId, source, target)`
   (two callers), `ChangeEvent.EdgePropertyId`, dispatcher pass-through.
2. `ChangeEventREST.EdgePropertyId` (`edgeCreated` only, absent otherwise).
3. Tests: engine event carries the type; REST mapping emits/omits it by kind.

## Phase 3 - MCP projection (spec A)

1. `ElementProjection.Compact`: copy `edgePropertyId` when present.
2. Test alongside the existing projection tests.

## Phase 4 - Studio (spec A, D)

1. `types.ts`: `EdgeREST.edgePropertyId?`; collapse `CanvasEdgeInput` into it.
2. `neighborhood.ts`: prefer server value, keep adjacency-map attribution as fallback.
3. `liveFeed.ts`: take `edgePropertyId` from the SSE event.
4. Display rule `label ?? edgePropertyId` in `instanceStore` (truthful `label` field),
   `Canvas2D`, `Canvas3D`, `styleEngine` color key.
5. Check docs screenshots: if any captured scene shows edge-label text from a graph with
   distinct label/type (cyber-warfare), recapture per the documented flow; otherwise none
   change (label-less samples render identically).

## Phase 5 - scoping follow-ups (spec D)

1. `GET /bulk/export`: `edgePropertyId` query param (controller-side exact-match filter,
   ANDs with `edgeLabel`); Studio export form field + `exportBulk` client; docs; test
   covering the single filter, the AND composition, and the import round-trip.
2. MCP `f8_search`: `label` param wired to the index/property scan DTOs and the
   vector/semantic requests; end-to-end tests through the apiApp.
3. REST fix: `POST /scan/index/all` honours the previously ignored `label` field.

## Phase 6 - docs + snapshot

1. `graph-model.mdx`: "Edge type vs label" subsection (the one home); fix the edge example.
2. `subgraphs.mdx`, `change-feed.mdx`, and any page showing an edge response body.
3. `TestGraphGenerator` comment (deliberate label mix).
4. Regenerate the OpenAPI snapshot; review the diff (additions only).
5. `dotnet build` + `dotnet test` + `npm --prefix docs run build` green; move this feature
   dir to `features/done/` in the final commit.
