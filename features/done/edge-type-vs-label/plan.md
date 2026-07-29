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
3. `liveFeed.ts`: merge the fetched Edge DTO whole (it now carries the type; the SSE
   field exists for non-hydrating consumers).
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
   vector/semantic requests; end-to-end tests through the apiApp for the index and
   property modes (vector/semantic reuse the pre-existing REST `Label` plumbing and
   stay covered by the REST-side vector tests).
3. REST fix: `POST /scan/index/all` honours the previously ignored `label` field.

## Phase 6 - docs + snapshot

1. `graph-model.mdx`: "Edge type vs label" subsection (the one home); fix the edge example.
2. `subgraphs.mdx`, `change-feed.mdx`, and any page showing an edge response body.
3. `TestGraphGenerator` comment (deliberate label mix).
4. Regenerate the OpenAPI snapshot; review the diff (additions only).
5. `dotnet build` + `dotnet test` + `npm --prefix docs run build` green; move this feature
   dir to `features/done/` in the final commit.

## Council outcome (2026-07-29)

Three parallel reviewers (correctness/concurrency; regressions/contracts/invariants;
scope/docs/one-home) over the full branch diff. **No blockers from any lens.** All
should-fixes were applied on the branch:

- SSE wire coverage: `ChangeFeedEndpointTest.EdgeCreated_CarriesTheEdgeType_OnTheWire`
  pins the `edgePropertyId` field on the `edgeCreated` frame (the one line in
  `ChangeEventREST.FromEvent` nothing previously exercised).
- Living docs brought in line: change-feed README event schema + payload sentence, bulk
  README filter example (which itself conflated type as label), studio.md save-games and
  benchmark wording, and the payload headline sentences in `ChangeEvent`/`ChangeDescriptor`/
  `ChangeEventREST`/`ATransaction`/change-feed.mdx now name the edge type.
- Leftover conflations the sweep missed: `StoredSubGraphQueryBlock` example (+ its wrong
  lambda-parameter claim - it receives an `EdgeModel`, not `AGraphElementModel`),
  `SubGraphDefinition` engine example, the seeded `knows-hops` stored query
  (now an `edgePropertyFilter`; name/description unchanged so `screen-path.png` is
  unaffected), Studio's `analyticsEdgeProperty` help ("edge container"), and
  `ScanSpecification.Label`'s doc which called labels "element types".
- Studio: the canvas edge detail panel now shows the edge's type row (the headline
  asymmetry was still visible there); `changefeed.ts`'s SSE mirror type gained the field.
- Spec/plan honesty: liveFeed mechanism described as implemented (hydrate via GET /edge,
  SSE field for non-hydrating consumers); vector/semantic f8_search label coverage stated
  as pass-through, not end-to-end.

Accepted without change: `[Required]` + nullable-typed `edgePropertyId` on the Edge schema
(null reachable only via the embedded-engine library path; schema types it honestly),
`ChangeDescriptor.Builder.EdgeCreated`'s source-breaking signature (compile-time-safe, no
out-of-tree callers in repo), the pre-existing PathScreen stub-edge label overwrite
(follow-up candidate), and the behavior change for external `/scan/index/all` callers who
sent `label` expecting it ignored (deliberate: converges on the documented contract).
