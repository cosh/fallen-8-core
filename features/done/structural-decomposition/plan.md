# Structural decomposition — Plan

Companion to [spec.md](./spec.md). Feature branch: `feature/structural-decomposition`
(branch-only workflow — no GitHub issue/PR). Every phase is independently shippable, lands as
its own commit(s), and holds the gates: full C# + web suites green, 0 warnings, OpenAPI
snapshot byte-identical (after Phase 0's one-time reorder).

Status 2026-07-22: Phases 0–5 are implemented on the branch (one commit each; Phase 3 landed
as separate web and C# commits). Remaining items are the deliberately deferred,
trigger-gated ones listed at the bottom.

Ordering rationale: determinism first (Phase 0 makes the snapshot criterion honest), then the
compiler-proven splits (Phases 1–2, no new tests needed — the compiler is the net), then the
pinning tests (Phase 3, before any code *moves*), then the moves (Phases 4–5). Deferred work
stays deferred until its trigger fires.

## Phase 0 — OpenAPI determinism

1. Add a document transformer to `AddOpenApi("v0.1", …)` in `fallen-8-core-apiApp/Program.cs`
   that sorts the `paths` object alphabetically (schemas and doc-level tags are already
   sorted by the generator; only `paths` is discovery-ordered).
2. Extend `fallen-8-unittest/OpenApiDocumentTest.cs` with an inventory test asserting the
   exact (path, method, tag) set of the served document — the executable, order-independent
   contract guard.
3. Regenerate the snapshot (`pwsh scripts/update-openapi-snapshot.ps1`); review the diff —
   pure reorder, no operation added/removed/changed. Own commit.

## Phase 1 — `Fallen8.cs` partial-class split

Core-part rule from the spec: `Fallen8.cs` keeps **all** fields, the nested `Snapshot` type,
segment constants, constructors, and `Dispose`; parts contain only methods/properties.

| Part | Contents |
|---|---|
| `Fallen8.cs` (core) | fields + initializers, nested `Snapshot`, constants, ctors, `Dispose`, reset/`TabulaRasa`, the lock-free discipline doc comment |
| `Fallen8.Storage.cs` | create/remove vertex/edge, property mutation, the transaction-apply entry points |
| `Fallen8.Scan.cs` | `GraphScan`, `IndexScan`, `FulltextIndexScan`, `SpatialIndexScan`, `FindElements`, `GetAll*`, `GetCountOf`, scan helper methods |
| `Fallen8.Persistence.cs` | `Save`/`Load` orchestration, WAL wiring, checkpoint |
| `Fallen8.Embeddings.cs` | `ProjectEmbeddingToBoundIndices`, `ProjectAllEmbeddingsOf`, projection purge helpers |
| `Fallen8.Trim.cs` | `Trim`/`Trim_internal`, auto-trim logic (fields stay in core) |
| `Fallen8.Metrics.cs` | metrics gauge accessors, `MeasureCheckpointBytes` |
| `Fallen8.ChangeFeed.cs` | `TryDescribeElement`, `DescribeRemovedElement`, descriptor helpers |

Exact member placement is decided at the diff, but the core-part rule is not negotiable.
Verification beyond the gates: `git diff --stat` shows only moves (no member edited), and the
public API surface is unchanged (same members, same signatures — the compiler enforces it).

## Phase 2 — `GraphController.cs` partial-class split

Core part `GraphController.cs` keeps: class-level attributes (`[ApiController]`,
`[ApiVersion]`, `[Route]`, the trimming suppression), constructor + DI fields, and the shared
private helpers (`AwaitAndAccept`, `RolledBackResult`, `TryResolveType`, `TryConvertLiteral`,
`CreateResult`, `CarriesInlineCode`, `MaxPageSize`).

| Part | Actions |
|---|---|
| `GraphController.Vertex.cs` | `/vertex` CRUD, degrees, adjacency |
| `GraphController.Edge.cs` | `/edge` CRUD, source/target |
| `GraphController.GraphElement.cs` | `/graphelement` properties + element embeddings, `/graph` |
| `GraphController.Scan.cs` | the six `/scan/*` families |
| `GraphController.Index.cs` | `/index` management incl. vector |
| `GraphController.Path.cs` | `/path` + code-generation plumbing it owns |

No test edits, no attribute duplication, no tag/route/logger change. Snapshot byte-identical
(Phase 0 made ordering deterministic).

## Phase 3 — pinning tests (before any code moves)

The three groups from the spec's Verification section, as MSTest/vitest additions:

1. **Engine** (`fallen-8-unittest`): `FulltextIndexScan` (unknown index → false, non-fulltext
   index → false, whitespace query), `GetCountOf<T>`, `GraphScan`/`IndexScan` remaining
   operator arms + invalid-operator default branches, `IndexCountForMetrics`/
   `IndexEntriesForMetrics` against live index create/delete, `MeasureCheckpointBytes` second
   save to the same base path, change-feed self-loop removal dedup.
2. **REST, hosted pipeline** (`WebApplicationFactory`): routing smokes for `PUT /vertex`,
   `GET /vertex/{id}`, `PUT /edge`, `GET /graph`, `POST /scan/graph/property/{id}`,
   `POST /scan/index/{all|range|fulltext|spatial}`; route-precedence pins for
   `DELETE /index/{indexId}/propertyValue` vs `DELETE /index/{indexId}/{graphElementId}` and
   `PUT /graphelement/{id}/{propertyIdString}` vs `PUT /graphelement/{id}/embedding/{name}`.
3. **Web** (`fallen-8-web-ui/tests`): `AnalyticsScreen` runner happy path +
   `describeRunError` mapping + `GraphShapePanel` render; QueryScreen property-scan →
   hydration → result-table flow, fulltext result rendering, range/spatial request shapes;
   BrowserScreen `ElementDetail`/`AdjacencyPanel`/`PropertiesTab`; SubgraphScreen
   pattern-builder draft→request flow.

## Phase 4 — engine extractions (narrow, rescoped)

1. `ScanHelpers` static class: move `FilterLive`, `FindElementsIndex`,
   `TryOrderedRangeIndexScan`, `Binary*Method`, `CheckLabel` — already static and
   snapshot-free; call sites update mechanically. Own commit.
2. `EmbeddingProjection` — **only if its trigger fires** (spec target 1). Guards: re-read
   `IndexFactory` per call, writer-thread affinity documented on the type.

## Phase 5 — web mechanical extractions

Move the delineated inner components out of `BrowserScreen` (`AdjacencyPanel`,
`EmbeddingsTab`, `PropertiesTab`, `ElementDetail`) and `AnalyticsScreen` (`AnalyticsRunner`,
`GraphShapePanel`) into `components/`, honoring the two constraints (store via
`useInstanceStore()`/props; no new direct `localStorage` reads). `SubgraphScreen` proved to
have no self-contained inner components — its pattern builder is inline JSX over screen
state, deferred with QueryScreen (see spec target 3). Screen tests from Phase 3 are the
guard; screens keep composing the extracted units so coverage transfers.

## Deferred (triggers in spec)

- **QueryScreen designed decomposition** — after studio-embeddable's shell seams land.
- **Real per-resource controllers** — on a concrete per-resource policy/filter need.
- **Phase 6, concurrency spike** — evidence-gated; executes index-lifecycle defect (d) with
  its benchmark criteria; standard-lock swap is the only in-scope replacement shape.
