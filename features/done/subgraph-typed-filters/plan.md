# Subgraph typed filters — plan

Phases; each leaves the build green.

## Phase 1 — engine

- `SubGraphDefinition.VertexFilter` → `Delegates.VertexFilter`,
  `EdgeFilter` → `Delegates.EdgeFilter`.
- Delete `GraphElementPattern`; `VertexPattern`/`EdgePattern` extend `APattern` directly
  (drop the `GraphElement` property everywhere).
- `BreadthFirstSearchSubgraphAlgorithm`: copy phases take the delegates directly
  (`MatchesAGraphElementPattern` deleted); `MatchesVertexPattern`/`MatchesEdgePattern`
  lose the GE check.
- `Delegates.GraphElementFilter` itself stays (see spec non-goals).

## Phase 2 — REST

- `PatternSpecification`: remove `graphElementFilter`.
- `SubGraphSpecification` / `SubGraphController`: docs + examples move to `(v)`/`(e)`
  fragments; `CarriesInlineCode` drops the GE check.
- `CodeGenerationHelper.TryGenerateSubGraphDefinition`: top-level slots compile as
  `Delegates.VertexFilter`/`Delegates.EdgeFilter`; pattern GE slots removed; semantic
  pre-filter assigns `semantic.VertexFilter`.
- `SemanticTraversalHelper`: drop the `GraphElementFilter` field (subgraph uses the
  existing `VertexFilter`).
- Stored queries flow through `PatternSpecification`/`TryGenerateSubGraphDefinition`
  unchanged; `/delegates/validate` keeps the GraphElementFilter kind.

## Phase 3 — tests

- Engine tests: `VertexPattern { GraphElement = … }` → `{ Vertex = … }`,
  `EdgePattern { GraphElement = … }` → `{ Edge = … }`.
- REST tests: pattern `GraphElementFilter = "(ge) …"` → `VertexFilter = "(v) …"`.
- New pins: top-level `vertexFilter` receives `VertexModel` (vertex-only member compiles
  and filters); legacy `(ge)`-named fragment still compiles in the typed slot.

## Phase 4 — Studio UI

- Top-level slots pass `delegateKind="VertexFilter"` / `"EdgeFilter"`.
- Step editors lose the graphElementFilter slot; `PatternSpecification` TS type and
  `normalizePatterns`/`subGraphBlock` drop the field.
- Kind table keeps `GraphElementFilter` (validate endpoint still accepts it); stale
  comments corrected. Rebuild the apiApp bundle (`npm run build:apiapp`).

## Phase 5 — docs & snapshot

- Root `README.md`, `features/done/subgraph/README.md`,
  `features/done/stored-query-library/README.md`: examples use typed fragments.
- Regenerate the OpenAPI snapshot (`pwsh scripts/update-openapi-snapshot.ps1`); expected:
  `graphElementFilter` removal + doc-text changes.
