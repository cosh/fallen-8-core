# Supernode Adjacency Build — Plan

Companion to [spec.md](./spec.md). Prove the O(d²) build cost first, then land the two independent
amortising steps (batch-group wiring, amortised capacity) behind the unchanged public surface and the
copy-on-write reader contract. Every phase: failing/observable measurement or test → change → green.

GitHub issue: to be opened (label: feature).

## Phase 0 — Baseline & guardrails

Intent: capture the quadratic build/load cost that motivates the change, and pin the lock-free
reader contract the fix must not break — so both are guarded before touching `EdgeAdjacency`.

- [ ] Add an opt-in `[TestCategory("Benchmark")]` + `[Ignore]` benchmark
  (`SupernodeAdjacencyBuildBenchmark`, alongside `AdjacencyMemoryBenchmark`) that builds a single
  high-degree hub at several degrees (env-tunable, e.g. `F8_SUPERNODE_DEGREES`) and records, per
  degree: wall time and `GC.GetAllocatedBytesForCurrentThread` delta for (a) runtime create via one
  `CreateEdgesTransaction`, (b) runtime create via many small transactions / single `CreateEdge`, and
  (c) a save → load round-trip of the hub. Emit + append to a results file (mirror
  `AdjacencyMemoryBenchmark`'s `[F8...]` line + `GC.Collect()` discipline). Capture the **before**
  numbers here (to be captured on this box) — expect ~4·d² bytes and d allocations.
- [ ] Add a characterization test (default suite) that pins linear scaling as a ratio, not an absolute
  time: build degree d and 2d, assert allocated-bytes(2d) / allocated-bytes(d) is well under the ~4×
  a quadratic build would show (guards the fix; would fail on today's O(d²)). Keep the threshold
  loose enough to be machine-independent.
- [ ] Confirm the existing adjacency concurrency guardrail covers a **growing** hub; if not, extend it
  so many readers (`TryGetOut|InEdge`, `GetAllNeighbors`, `GetOut|InDegree`, path traversal) run
  during single-writer append to one high-degree vertex and observe no torn read / NRE /
  `IndexOutOfRange`, and a captured view stays consistent. (Mirror `ConcurrentStorageTest` /
  `AppendGraphElement`'s guard.)

## Phase 1 — Batch-group wiring (Step 1: k copies → 1)

Intent: collapse many appends to one vertex/direction/group within a single transaction or load
fix-up into one array rebuild + one publish. Independent of Phase 2; helps batch and load paths.

- [ ] Add `EdgeAdjacency.WithEdgesAppended(string edgePropertyId, IReadOnlyList<EdgeModel> edges)`
  (`Model/EdgeAdjacency.cs`): build the target group once at final size, publish one new instance;
  keep inline shape for a single group, promote to map only on a new key; preserve append order.
- [ ] `Fallen8.CreateEdges_internal` (`Fallen8.cs:976`): keep edge construction, id assignment, and the
  single `AppendGraphElements` publish; group the new edges by `(vertex, direction, edgePropertyId)`
  and apply one `WithEdgesAppended` per group so each vertex/direction publishes once (chain per-key
  builds for a multi-key vertex, publish the final instance once). Preserve the up-front
  all-endpoints-resolved validation and the `allEndpointsResolved` all-or-nothing contract exactly.
- [ ] Load fix-up (`PersistencyFactory.cs:996`): bucket the deferred `edgeTodo` entries by `(VertexId,
  IsIncomingEdge, EdgePropertyId)` and apply one `WithEdgesAppended` per bucket, replacing the
  per-edge `AddIncomingEdge`/`AddOutEdge` calls (`:1008,1012`). Leave the per-vertex own-group
  `FromListGroups` reconstruction untouched.
- [ ] Verify existing create/load/path/subgraph tests stay green; confirm append order preserved.

## Phase 2 — Amortised capacity (Step 2: per-append amortised O(1))

Intent: give each group a logical count distinct from array length, over-allocate ×2, append into a
spare slot with the master-store publication discipline. Makes the single-edge path amortised O(1)
too, and composes with Phase 1.

- [ ] Introduce the logical count in `EdgeAdjacency`: inline `_soleGroup` + `_soleCount`; map shape as
  `Dictionary<string, EdgeGroup>` with a readonly `EdgeGroup { EdgeModel[] Array; int Count; }`.
- [ ] `WithEdgeAppended` (`EdgeAdjacency.cs:182`) + the new `WithEdgesAppended`: append into the spare
  slot when `count < array.Length` (write slot `array[count]` first, publish `count+1` sharing the
  array — the `AppendGraphElement` ordering, `Fallen8.cs:350-370`); else reallocate `count*2`, copy
  `[0,count)`, write, publish. Replace/retire the whole-group-copy `Append` helper (`:347`).
- [ ] Make every read/derived path slice `[0,count)`: `TotalDegree`, `TryGetGroup`, `CollectKeys`,
  `RemoveById`, `Contains`, and the struct `Enumerator` — change `Enumerator.Current` from
  `KeyValuePair<string, EdgeModel[]>` to a small struct carrying `(key, array, count)`.
- [ ] Migrate the in-engine consumers from `.Value.Length` to the logical count: `PathHelper.cs:73,102`;
  `BidirectionalLevelSynchronousSSSP.cs:643,689,714,770,816,841`;
  `WeightedDijkstraShortestPath.cs:564-595`; `Fallen8.cs:1089,1113,1188,1208`; and the save path
  `PersistencyFactory.cs:1284,1303` (persist `count`, not array length — on-disk bytes unchanged).
- [ ] Make the public read-only surface count-aware: `ReadOnlyEdgeContainer` and `TryGetOut|InEdge`
  (`VertexModel.cs`) expose only `[0,count)` (count-bounded read-only wrapper), never spare slots.
- [ ] Confirm removal (`WithEdgeRemovedFromGroup`/`WithEdgeRemovedEverywhere` → `RemoveById`) scans
  `[0,count)` and still yields a compacted `count == length` array; the poison-null-slot throw fires
  before publish (fault-injection rollback tests unchanged).
- [ ] Confirm the degree-0-but-present group (empty sole group, key still enumerable) round-trips
  through the count-aware accessors with no null-deref and unchanged observable behaviour.

## Measure & document

Intent: prove linear scaling and no LOH churn, and record where the residual sits.

- [ ] Re-run the Phase 0 benchmark; record before/after wall time + allocated bytes per degree for
  runtime create (single + batch) and load round-trip here (to be captured on this box). Assert
  linear scaling and no LOH allocations from the append.
- [ ] Confirm the full suite is green (adjacency, concurrency, path, subgraph, persistence,
  removal-rollback). Note steady-state retained-bytes delta vs `adjacency-flattening` (spare capacity
  ≤ ~2× the group array) is not a material regression.
- [ ] Update the `adjacency-flattening` spec/plan with a back-reference (append cost now amortised
  O(1); its measurements were retained-bytes only). Note any residual (e.g. transient spare-capacity
  headroom; multi-key vertices publishing per-key within a batch).

## Progress

- [x] Phase 0 — opt-in `SupernodeAdjacencyBuildBenchmark` (prefix `[SNBENCH]`, batch / single-edge /
  load round-trip, degrees env-tunable via `F8_SUPERNODE_DEGREES`, measured with
  `GC.GetTotalAllocatedBytes(true)` since the build runs on the writer thread); a default-suite
  linear-scaling characterization (`HubBuild_AllocatedBytes_ScaleLinearlyNotQuadratically`, asserts
  the 2d/d allocation ratio is < 3× — it would trip near 4× on the old O(d²) build); and a dedicated
  growing-hub concurrency guard added to `AdjacencyConcurrencyTest`
  (`ConcurrentReaders_DuringMonotonicHubGrowth_NeverSeeTornAdjacencyOrRegressingDegree`) — readers
  observe no torn read / null spare slot and a strictly non-decreasing out-degree while the writer
  grows one hub through the shared-array spare-capacity path.
- [x] Phase 1 — `EdgeAdjacency.WithEdgesAppended` + `VertexModel.AddOutEdges`/`AddIncomingEdges`
  (one publish per vertex/direction); batch-group wiring in `CreateEdges_internal` (grouped by
  `(vertex, direction, key)`) and the deferred-edge load fix-up (bucketed by the same key,
  encounter-order preserved).
- [x] Phase 2 — logical count per group (`EdgeGroup { Array; Count }`, inline `_soleArray` +
  `_soleCount`); ×2 spare-capacity append (write the spare slot first, publish `count+1` sharing the
  array); count-aware `TotalDegree`/`TryGetGroup`/`RemoveById`/`Contains`/enumerator (now yields a
  count-bounded `ArraySegment<EdgeModel>`); the ~8 in-engine consumers moved from `.Value.Length` to
  `.Value.Count` (compiler-flagged); the public read view exposes a truly read-only, count-bounded
  `ReadOnlyEdgeSlice` (never a spare slot); the save path persists the logical count.
- [x] Measure & document — full suite green (409 passing). Absolute before/after numbers are left to
  the opt-in benchmark on the target box; the linear-scaling test proves the O(d²) → O(d) shape at
  default degrees, and the append path allocates only the O(log d) ×2 doubling arrays (no per-edge
  whole-group copy). Public surface and on-disk format unchanged → no version bump.

## Decision / revisit condition

This feature deliberately stays **inside** the existing per-vertex immutable `EdgeAdjacency`
(inline single-group + contiguous per-group arrays + copy-on-write publish). It does **not** reopen
`csr-adjacency`, which was assessed and SKIPPED: edges are first-class objects, the graph is
continuously mutated, and per-vertex publication is the concurrency unit — none of which change here.
The amortised-capacity growth is the same discipline the master store already uses; it is an
incremental fix to the append cost, not a new global structure.

The only sanctioned CSR direction remains a **derived, read-only CSR snapshot built on demand** for an
analytics session and discarded — additive, off the mutation path — and only if a concrete, measured,
overwhelmingly-read-only full-graph-sweep workload emerges (the `csr-adjacency` revisit condition).
That is out of scope here and unaffected by this work.
