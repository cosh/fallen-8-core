# Core Storage Representation — Plan

Companion to [spec.md](./spec.md). Staged so value lands incrementally and risk is bounded.
Do the `correctness-fixes` theme first (the immutable-return-value bugs share this root cause).

## Phase 0 — Baseline & guardrails
- Add microbenchmarks (BenchmarkDotNet or a bench harness) for: id lookup, single insert, bulk
  insert, full scan, edge wire-up. Capture current numbers.
- Add concurrency tests: many concurrent readers during single-writer appends/removes assert
  snapshot stability (no torn reads, no `IndexOutOfRange`).

## Phase 1 — Snapshot-stability & volatile (low risk, immediate)
- Capture `_graphElements` once per read method; mark the holder `volatile`. Fixes the
  double-read window (`Fallen8.cs:239/247`, `259/267`, `280/288`) independent of the storage swap.

## Phase 2 — Master store → array-backed (interim: ImmutableArray)
- Switch the master collection and the bulk/`Load` paths to `ImmutableArray<T>` + `Builder`
  (O(1) indexer, cache-friendly scans) as a low-risk first move; single-appends coalesced by the
  transaction batching from `engine-performance`.

## Phase 3 — Master store → segmented append-only array
- Replace with the segmented-array `Snapshot` holder + atomic publish. Verify id == index and
  `Trim` compaction still hold. Re-run Phase 0 benchmarks.

## Phase 4 — Adjacency flattening
- Replace `ImmutableDictionary<string, ImmutableList<EdgeModel>>` (`VertexModel.cs:45,50`) with
  per-direction `List<EdgeModel>`/arrays (+ optional lazy type grouping). Update `AddOutEdge`/
  `AddIncomingEdge` and the `Load` edge fix-up (`PersistencyFactory.cs:619,623`). This also
  eliminates the per-edge immutable-churn (`memory-footprint` #7).

## Phase 5 — Measure & document
- Re-run benchmarks + a large-graph memory measurement; record the before/after in this plan.
  Update `engine-performance` and `memory-footprint` notes that depend on this.

## Status
- [x] Phase 0 — benchmarks + concurrency tests
- [x] Phase 1 — snapshot capture + volatile
- [x] Phase 2 — array-backed interim (copy-on-write `AGraphElementModel[]`, see note below)
- [x] Phase 3 — segmented array master store
- [x] Phase 4 — adjacency flattening — **landed as its own feature** `features/adjacency-flattening/`
  (it broke the public `VertexModel` surface, so it needed the public-API version bump this theme
  could not make; see "Phase 4 — deferred" below for the original rationale)
- [~] Phase 5 — measure & document — **partial**: benchmarks captured (below) and the
  `memory-footprint` sibling-doc baseline reconciled to the master-store-only win (it no longer
  inherits the full adjacency+master tree overhead as realised). Still outstanding: the
  large-graph (multi-GB) RSS measurement was **not** run in this environment (too heavy for the
  in-suite harness — see the Measurements note), and the `engine-performance` note is only
  partially updated.

> Phase 2 note: the plan called for `ImmutableArray<T> + Builder`, but `ImmutableArray<T>` is a
> struct and cannot be marked `volatile`, which Phase 1 requires for the holder. A copy-on-write
> `AGraphElementModel[]` behind a `volatile` reference gives the identical immutable-after-publish
> guarantee with correct release/acquire memory ordering, so that was used instead.

## Measurements (real, captured in this environment)

Method: the in-suite `StorageBenchmark` harness (`fallen-8-unittest/StorageBenchmark.cs`),
`[TestCategory("Benchmark")]`, Stopwatch + `GC.GetTotalAllocatedBytes(true)`, **Debug** build,
Server GC, single run each (`F8_BENCH_SCALE=1`: 50k vertices, 50k edges, 1M id lookups, 50 full
scans, 5k single-tx inserts). These are indicative Debug numbers from one machine, not rigorous
Release BenchmarkDotNet figures; they are intended for the relative before/after comparison the
theme asks for, not as absolute throughput claims.

| Operation (workload)          | Phase 0 `ImmutableList` | Phase 2 flat `T[]` | Phase 3 segmented |
|-------------------------------|-------------------------|--------------------|-------------------|
| id lookup (1,000,000)         | 173.7 ms (5.8 M/s)      | 14.6 ms (68.6 M/s) | **20.0 ms (50.0 M/s)** |
| bulk insert vertices (50,000) | 26.6 ms / 14.0 MB       | 37.3 ms / 12.1 MB  | **23.8 ms / 12.1 MB** |
| bulk insert edges (50,000)    | 217.0 ms / 42.1 MB      | 172.2 ms / 40.6 MB | **200.4 ms / 40.2 MB** |
| full scan (2,500,000 visited) | 392.8 ms / 158.2 MB     | 307.3 ms / 158.2 MB| **357.2 ms / 158.3 MB** |
| single-tx insert (5,000)      | 104.2 ms / 29.2 MB      | 80.3 ms / **121.9 MB** | **63.3 ms / 26.6 MB** |

Takeaways:
- **id lookup ~8.7x faster** (O(log n) tree indexer → O(1) segmented index). Phase 2's flat array
  is marginally faster on lookup (one fewer indirection) but pays O(n) per single append.
- **Segmented append removes the copy churn**: Phase 2's flat copy-on-write allocated 121.9 MB for
  5k single-tx inserts (O(n²)); Phase 3 is back to 26.6 MB (O(1) amortised append, one 32 KB
  segment allocated per 4096 elements, no whole-store copy, no LOH churn).
- **Per-element master-store overhead drops** from a ~48 B `ImmutableList` tree node to an 8 B array
  slot: bulk-insert-vertices allocation fell 14.0 MB → 12.1 MB for 50k vertices (~38 B/element),
  consistent with removing the tree-node overhead. A full multi-GB large-graph RSS measurement was
  **not** run in this environment (too heavy for the in-suite harness); the allocation deltas above
  are the captured evidence.
- Edge wire-up and full scan are dominated by the (unchanged) per-vertex adjacency
  `ImmutableDictionary`/`ImmutableList` and the `ImmutableList` result building, so they move little
  — exactly what Phase 4 would address.

## Phase 4 — deferred (adjacency flattening) → since LANDED

> **Update (LANDED):** this was carried out as the standalone `features/adjacency-flattening/`
> feature (with the public-API version bump 0.0.14 → 0.1.0 this theme could not make).
> `VertexModel.OutEdges`/`InEdges` are now read-only views and the two per-vertex `ImmutableList`
> AVL trees are gone. A single-edge-group vertex (the common case) is stored **inline with no
> `Dictionary` at all**; only a genuinely multi-group vertex falls back to a
> `Dictionary<string, EdgeModel[]>` (plus a small wrapper). The poison-injection rollback tests were
> migrated (not weakened) to an internal fault-injection hook. Because the common vertex sheds the
> per-vertex dictionary entirely, the memory win is now **monotonic** — ≈ **−44%/edge across degrees**
> (2/10/20), and the former degree-2 regression is **gone**; a genuinely multi-group vertex is
> marginally heavier than a plain dict but still far below the old `ImmutableDictionary`. See that
> feature's `plan.md` Measurements (Phase 5). The original deferral rationale is kept below for context.

Phase 4 would replace the two per-vertex `ImmutableDictionary<string, ImmutableList<EdgeModel>>`
(`VertexModel.OutEdges`/`InEdges`) with a flatter per-direction representation to shed the
~48 B/edge tree-node and ~200–400 B/vertex dictionary overhead. It is **deferred** because its
memory win is only realisable by changing types that are part of the **public surface this theme
must keep unchanged**, and doing so would break existing tests (which must not be weakened):

- `VertexModel.OutEdges`/`InEdges` are **public fields** typed
  `ImmutableDictionary<string, ImmutableList<EdgeModel>>`, and `TryGetOutEdge`/`TryGetInEdge` are
  **public methods returning `ImmutableList<EdgeModel>`**. Flattening changes these types.
- Two existing tests manipulate them through the immutable API and would fail to compile after a
  type change (the test assembly has no `InternalsVisibleTo`, so the types cannot be hidden):
  - `CorrectnessFixesTest.RemoveGraphElement_WhenRemovalFaultsMidway…`:
    `v.InEdges = v.InEdges.SetItem("in", v.InEdges["in"].Add(poison));`
  - `CorrectnessFixesFollowupsTest.RemoveEdge_WhenDetachFaultsMidway…`:
    `source.OutEdges = source.OutEdges.SetItem("knows", source.OutEdges["knows"].Add(null));`
- Internal consumers (`PathHelper`, `WeightedDijkstraShortestPath`,
  `BidirectionalLevelSynchronousSSSP`, `BreathFirstSearchSubgraphAlgorithm`) and the API
  `Controllers/Model/Vertex` iterate these as `ImmutableDictionary`/`ImmutableList`.

Keeping the types = no memory win, so the phase's goal is fundamentally incompatible with the
"public surface unchanged" + "do not weaken existing tests" constraints. Per the theme's
stop-at-safe-boundary rule, work stopped at the last fully-green phase (Phase 3).

Safe to defer: adjacency already uses the same lock-free pattern as the (now hardened) master
store — the tx thread swaps whole immutable containers by reference and readers capture the
reference once. Because those containers are fully immutable and a reference read/write is atomic,
readers never see a torn or crashing adjacency even though the fields are not `volatile`; the only
weak-memory effect is benign staleness (a reader may momentarily miss a just-added edge), which is
within snapshot semantics.

A future, properly-scoped change could land the flattening by bumping the public API version,
migrating the two tests' poison-injection mechanism, and updating the path/subgraph/API consumers
to the new accessor shape.
