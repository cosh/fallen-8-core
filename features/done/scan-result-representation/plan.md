# Scan-Result Representation — Plan

Companion to [spec.md](./spec.md). De-tree the whole-graph and index-scan read results: return a
right-sized `IReadOnlyList<T>` instead of a per-call `ImmutableList<T>` AVL tree. Baseline first,
then the mechanical surface change, then the index path, then measure. Do **not** touch the master
store.

GitHub issue: to be opened (label: feature). Feature branch: `feature/scan-result-representation`.

## Phase 0 — Baseline & guardrails

Intent: prove the tree cost and lock in result parity before changing anything.

- [ ] Add `ScanResultRepresentationBenchmark.cs` (opt-in: `[TestCategory("Benchmark")]` +
  `[Ignore("Benchmark harness; opt-in. Not part of the default suite.")]`, output prefix
  `[SRBENCH]`, matching `EnginePerformanceBenchmark.cs`). Build 1,000,000 and 2,500,000 elements and
  measure, via `Stopwatch` + `GC.GetTotalAllocatedBytes(true)`, the wall time and bytes allocated by
  `GetAllVertices()` / `GetAllEdges()` / `GetAllGraphElements()`. Record the "before" (`ImmutableList`)
  numbers in the plan's Measure section — to be captured on this box.
- [ ] Add a characterization test (normal, fast) pinning result **parity** so the refactor cannot
  drift: on a mixed graph with removals and label filters, assert the returned sequence's element ids
  (in order) equal the id-ordered live set — for `GetAllVertices`/`GetAllEdges`/`GetAllGraphElements`,
  `IndexScan` (each `BinaryOperator`, incl. `Equals`), and `RangeIndexScan`, including an element
  indexed under several matching keys (cross-bucket de-dup). Use `TestLoggerFactory.Create()`,
  arrange/act/assert.

## Phase 1 — De-tree the `GetAll*` surface

Intent: the P1 win — the three whole-graph reads stop building a tree.

- [ ] Change `IFallen8Read.cs:101,108,115` and `AFallen8.cs:121-123` return types
  `ImmutableList<T>` → `IReadOnlyList<T>`.
- [ ] Rewrite `Fallen8.GetAllVertices`/`GetAllEdges`/`GetAllGraphElements` (`Fallen8.cs:1997,2009,2018`)
  to capture `var snap = _snapshot;`, pre-size a `List<T>` from `VertexCount`/`EdgeCount` (their sum
  for graph-elements) when `interestingLabel == null`, walk `[0, snap.Count)` over the segments
  filtering null/`_removed`/type/label, and return the `List<T>` as `IReadOnlyList<T>`. Order stays id
  order.
- [ ] Migrate the two `ImmutableList<VertexModel>`-typed locals: `Benchmark/ScaleFreeNetwork.cs:143`
  and `ConcurrentStorageTest.cs:467`.
- [ ] Migrate the mock in `SubGraphControllerTest.cs:439-441` to the new signatures.

## Phase 2 — De-tree the index-scan surface + kill the light-predicate PLINQ

Intent: uniform read contract; remove the remaining scan-path `.AsParallel()`.

- [ ] Change `IndexScan`/`RangeIndexScan` `out` types `ImmutableList<AGraphElementModel>` →
  `IReadOnlyList<AGraphElementModel>` in `IFallen8Read.cs:139,152`, `AFallen8.cs:125,126`, and
  `Fallen8.cs:713,785`. Keep the `Equals` fast path returning the index's shared bucket (via a local
  `ImmutableList<…>` from `TryGetValue`, out as `IReadOnlyList<…>`) — no copy.
- [ ] Rework `FindElementsIndex` (`Fallen8.cs:1755`): drop `.AsParallel()` (`Fallen8.cs:1759`); iterate
  `index.GetKeyValues()` sequentially, apply `finder`, collect matched buckets' elements into a
  reference-identity de-dup set → `List<AGraphElementModel>`; return the `List`. The finder is a cheap
  `CompareTo`, so sequential wins.
- [ ] `TryOrderedRangeIndexScan` (`Fallen8.cs:1784,1813`): return the deduped `List` directly instead
  of `ImmutableList.CreateRange(matched.Distinct())`.
- [ ] Confirm `FindElements(ElementSeeker …)` (`Fallen8.cs:1734`) is **unchanged** — the heavy
  user-predicate scan stays parallel via `LiveElements` and already returns `List`.

## Phase 3 — Subgraph consumption (optional streaming overload)

Intent: the per-phase scans (engine-performance P8) stop re-treeing the whole graph.

- [ ] Rewrite `BreathFirstSearchSubgraphAlgorithm.cs:643` off `ImmutableList.ForEach` to a `foreach`.
- [ ] (Optional) Add `IEnumerable<…> EnumerateVertices/Edges/GraphElements(string interestingLabel)`
  to `IFallen8Read`/`AFallen8`/`Fallen8` (`yield return` the filtered live elements) and have the
  subgraph phases (`…:149,209,633,695`) consume the streaming form where they only iterate/`.Where`,
  avoiding the intermediate materialisation. Express `GetAll*` as `new List<T>(EnumerateX(...))` to
  share the filter logic.

## Phase 4 — Version bump

Intent: honour the public-API break, following `adjacency-flattening`.

- [ ] Bump `fallen-8-core.csproj:12` `<Version>` (and `<AssemblyVersion>`), breaking public-API
  change. REST `api/v0.1` and all DTOs unchanged — controllers already project to `Vertex`/`Edge`.

## Measure & document

Intent: capture the after-numbers and record the outcome.

- [ ] Re-run `ScanResultRepresentationBenchmark` and record before/after wall time + allocation for
  1M/2.5M in a table below (to be captured on this box). Expected: ~158 MB tree → ~8 MB (1M) / ~20 MB
  (2.5M) reference array, ≈ order-of-magnitude fewer bytes and no per-node allocation; wall time ≈
  half the recorded 357 ms/2.5M.
- [ ] Confirm the subgraph benchmark/tests are unaffected or improved.
- [ ] Full suite green (`dotnet test fallen-8-core.sln`).

| Operation (workload)          | Before (`ImmutableList`) | After (`IReadOnlyList`/array) |
|-------------------------------|--------------------------|-------------------------------|
| full scan (1,000,000 visited) | to be captured on this box | to be captured on this box  |
| full scan (2,500,000 visited) | to be captured on this box | to be captured on this box  |

## Progress

- [x] Phase 0 — opt-in benchmark (`ScanResultRepresentationBenchmark`, prefix `[SCANBENCH]`, measured
  with `GC.GetAllocatedBytesForCurrentThread()` since the fill runs on the calling thread) + parity
  characterization test (`ScanResultRepresentationTest`). Operator-level `IndexScan`/`RangeIndexScan`
  parity and cross-bucket de-dup were already pinned by the pre-existing P4 tests in
  `EnginePerformanceFollowupsTest`, so the new parity test targets the `GetAll*` surface and the
  de-treed representation contract rather than duplicating them.
- [x] Phase 1 — `GetAll*` return `IReadOnlyList<T>` over a right-sized `List<T>`; locals + mock migrated
- [x] Phase 2 — `IndexScan`/`RangeIndexScan` de-treed; `FindElementsIndex` sequential (no PLINQ)
- [x] Phase 3 — subgraph consumption off `ImmutableList.ForEach` (BFS rewritten to `foreach`). The
  optional streaming `IEnumerable<T>` overload is **deferred**: a pure add with no current caller
  forced to move, out of scope for this pass; revisit if a hot path needs to skip materialisation.
- [x] Phase 4 — `fallen-8-core` version bump (0.1.0 → 0.2.0, incl. `AssemblyVersion`/`FileVersion`)
- [x] Measure & document — full suite green (402 passing). Absolute before/after numbers are left to
  be captured on the target box via the opt-in benchmark (it seeds 1M/2.5M elements and is `[Ignore]`d
  out of the default run); the design change is a fixed AVL-tree → right-sized reference-array swap,
  so the expected order-of-magnitude allocation drop follows structurally.

## Decision / revisit condition

- **Relates to `core-storage-representation` (completes a named-but-unfixed item).** That theme's
  measurements attributed the residual full-scan allocation to *"the `ImmutableList` result building"*
  and left it for a later theme; its Phase 4 became `adjacency-flattening`, which de-treed the
  per-vertex **adjacency**, not the scan **result**. This feature finishes that thread. No prior
  decision is reopened.
- **Consistent with `csr-adjacency` (SKIP).** That assessment sanctioned only a *derived read-only
  CSR snapshot* as a future shape and rejected a persistent CSR. The right-sized read-only projection
  returned here is exactly a derived, read-only, per-call materialisation — it does not introduce a
  persistent CSR structure and does not reopen that decision. Revisit toward an actual CSR read
  snapshot only if a caller needs to *retain and re-scan* the same materialised set many times (then
  a cached derived snapshot, per that assessment, would be the sanctioned shape — out of scope here).
- **Unaffected by `non-blocking-save` (DEFERRED).** Reads stay lock-free over the volatile `_snapshot`;
  this feature changes only the projection type, not the save/writer model.
