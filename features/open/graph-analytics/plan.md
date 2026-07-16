# Graph Analytics — Plan

Companion to [spec.md](./spec.md). A third plugin-discovered algorithm family
(`IGraphAnalyticsAlgorithm`) with five built-in algorithms, bounded REST results, and opt-in
property write-back through the sanctioned plugin write path. Feature branch:
`feature/graph-analytics` (branch-only workflow — no GitHub issue/PR).

Ordering principle: land the **contract plus the simplest algorithm end-to-end first**
(degree centrality — single pass, no iteration machinery), so the plugin kind, engine facade,
cache, controller, and test harness all exist and are proven before any interesting algorithm
lands. Then the iterative machinery (budgets/convergence) arrives with PageRank and is reused
by everything after it. Write-back goes last among the behaviours because it depends on the
result shape being final. Every phase ends build-clean (`dotnet build fallen-8-core.sln`,
0 warnings introduced) and tests-green (`dotnet test fallen-8-core.sln`).

## Phase 0 — Contract + degree centrality end-to-end

Intent: a walking skeleton of the whole family — plugin kind, engine facade, REST, tests.

- [x] `fallen-8-core/Algorithms/Analytics/`: `IGraphAnalyticsAlgorithm`,
  `GraphAnalyticsDefinition`, `GraphAnalyticsResult` (MIT headers, XML docs, `Try*` pattern)
  per spec §3.1.
- [x] `PluginCache.Analytics` `IMemoryCache` + `AddAnalytics(…)` alongside
  `ShortestPath`/`SubGraph`; `Fallen8.TryRunAnalytics(out result, name, definition)` facade
  mirroring `TryCalculateShortestPath` (resolve via
  `PluginFactory.TryFindPlugin<IGraphAnalyticsAlgorithm>`, initialize, cache, invoke).
- [x] `DegreeCentralityAlgorithm` (`DEGREE`): snapshot from `GetAllVertices()`, dense
  id→index map, `GetRawOutEdges()`/`GetRawInEdges()` walks, `Direction` in/out/both,
  label + edge-property-id scoping, removed-element skipping, `Statistics` min/max/mean.
- [x] `AnalyticsController` skeleton: `GET /analytics/algorithms`,
  `POST /analytics/{algorithmName}` (200/400/404 only for now), `AnalyticsSpecification` /
  `AnalyticsResultREST` DTOs, top-K + `maxResults` ceiling (10 000), OpenAPI annotations per
  repo convention.
- [x] Tests: degree fixtures (star, parallel edges, self-loop), scoping, removed elements,
  empty graph, plugin discovery via the factory, REST 404/400/ordering/ceiling.

## Phase 1 — Iterative machinery + PageRank + WCC

Intent: budgets, convergence, and the two most-wanted results.

- [x] Budget plumbing shared by all algorithms: `MaxIterations` (ceiling 10 000), `Epsilon`,
  `TimeBudget` + `CancellationToken` checked every 4 096 vertices; `Converged` /
  `IterationsRun` / `Elapsed` / `BudgetExhausted` metadata; the §3.4 partial-result rules
  (iterative: last completed pass; single-pass: `false`).
- [x] `AnalyticsOptions` config section (default `TimeBudget` 30 s, `MaxConcurrentRuns` 1)
  bound in the apiApp; controller slot-holding + **429**; **408** mapping for
  no-usable-result exhaustion (problem+json per `api-error-contract`).
- [x] `PageRankAlgorithm` (`PAGERANK`): damping via `Parameters["DampingFactor"]` (0.85),
  L1-delta convergence, dangling-mass redistribution, parallel-edge/self-loop semantics per
  spec §3.3, `Direction` interpretation.
- [x] `WeaklyConnectedComponentsAlgorithm` (`WCC`): iterative union-find (path halving),
  smallest-id component ids, `Statistics["ComponentCount"]`.
- [x] Partition REST projection: partition summaries (size, largest first) +
  `POST /analytics/{name}/partition/{partitionId}` membership paging.
- [x] Tests: pinned 4-vertex PageRank values, cycle, dangling, damping-0 uniform,
  `MaxIterations=1` ⇒ `Converged=false`; WCC two-chains/singleton/direction-blind;
  near-zero `TimeBudget` ⇒ 408 path (suite-safe deadline); 429 with a held slot;
  cancellation honoured.

## Phase 2 — Label propagation + triangle counting

Intent: complete the v1 algorithm set on the now-proven machinery.

- [x] `LabelPropagationAlgorithm` (`LABELPROPAGATION`): synchronous rounds, ascending-id
  order, most-frequent-neighbour label with smallest-label tie-break, stop on no-change or
  `MaxIterations` (default 20), `Statistics["CommunityCount"]`.
- [x] `TriangleCountingAlgorithm` (`TRIANGLECOUNT`): undirected simple-graph reduction
  (dedupe parallel edges, drop self-loops), sorted-neighbour intersection, per-vertex counts
  + `Statistics["TriangleCount"]` (= Σ/3), budget-cooperative.
- [x] Tests: two-cliques-plus-bridge ⇒ 2 communities (determinism pinned by double run),
  one-round clique convergence; K4 ⇒ 4, 4-cycle ⇒ 0, parallel-edge dedupe, self-loop
  ignored; scoping + budget cases for both.

## Phase 3 — Property write-back

Intent: the full-result delivery vehicle, through the sanctioned write path only.

- [x] Write-back executor shared by all five algorithms: chunked `DelegateTransaction`
  bodies (50 000 `SetProperty` calls per chunk) via `IFallen8WriterContext`,
  `EnqueueTransaction` + `WaitUntilFinished` per chunk; property-key convention table from
  spec §3.5 + `writeBackPropertyKey` override validation (non-empty, ≤ 256 chars, 400
  otherwise).
- [x] REST: `writeBack` flag on `AnalyticsSpecification`; response reports vertices written,
  chunk count, and the mode-(a) durability note in the endpoint docs.
- [x] Tests: keys/types per convention; idempotent re-run overwrite; multi-chunk run applies
  all chunks; induced mid-chunk failure leaves earlier chunks applied (documented
  non-atomicity pinned); write-back survives `Save`→`Load` but is absent after WAL-only
  replay (mode-(a) pin, mirroring `PluginWriteTransactionsTest`); write-back works with
  `EnableDynamicCodeExecution=false`.

## Phase 4 — Surface polish + docs + gate

Intent: ship what exists, honestly documented.

- [x] Regenerate the pinned OpenAPI snapshot (`features/done/web-ui/openapi-v0.1.json`);
  verify the web-ui contract test still passes (new endpoints are additive).
- [x] `features/open/graph-analytics/README.md`: usage examples per algorithm (request/
  response), the consistency story (§3.4) and write-back durability note verbatim, the
  parked-items table with revisit triggers.
- [x] Root `README.md`: analytics section next to path/subgraph.
- [ ] Full `dotnet test` green (692 passed); build clean; council review per the repo merge
  gate; fix findings on the branch; `git merge --no-ff` to `main`; move
  `features/open/graph-analytics/` → `features/done/`.

## Progress

- [x] Phase 0 — contract + `DEGREE` end-to-end (engine facade, cache, controller, tests)
- [x] Phase 1 — budgets/convergence + `PAGERANK` + `WCC` + 408/429 semantics
- [x] Phase 2 — `LABELPROPAGATION` + `TRIANGLECOUNT`
- [x] Phase 3 — chunked property write-back (mode (a)) + durability pins
- [ ] Phase 4 — OpenAPI snapshot + READMEs done; council gate, merge + move to done/ pending

## Decision / revisit conditions

- **Betweenness/closeness parked** on the O(V·E) exact cost; revisit when a real use case
  needs path-based centrality and graph size (or an agreed sampling target) makes it fit a
  budget.
- **Synchronous-with-budget over job/queue**; revisit when a real workload outgrows the
  configurable request budget or scheduled/overlapping runs become routine.
- **Data-only scoping (no C# fragments)**; revisit when label/edge-property scoping proves
  insufficient — the `CodeGenerationHelper` pattern is the established route in.
- **Write-back durability is mode (a)** (snapshot-only); revisit when
  `plugin-write-transactions` mode (b) lands — analytics is its natural first customer.
- **Unweighted edges in v1**; revisit with the first weighted-analytics user, following the
  `weighted-shortest-paths` property-as-weight convention.
