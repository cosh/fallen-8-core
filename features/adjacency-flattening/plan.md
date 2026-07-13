# Adjacency Flattening — Plan

Companion to [spec.md](./spec.md). Correctness + lock-free semantics dominate the memory win.
Every phase: failing/observable test → change → green.

## Phase 0 — Guardrail
- Add an adjacency concurrency test: many concurrent readers (`TryGetOut/InEdge`, `GetAllNeighbors`,
  `GetOut/InDegree`, path traversal) during single-writer edge add/remove assert no torn read, no
  NRE, no `IndexOutOfRange`, and a captured view stays consistent. (Mirror `ConcurrentStorageTest`.)
- Add an opt-in `[TestCategory("Benchmark")]`+`[Ignore]` adjacency-memory benchmark (retained bytes
  per edge / per vertex) capturing the current `ImmutableDictionary`/`ImmutableList` baseline.

## Phase 1 — Storage swap (internal)
- Replace `OutEdges`/`InEdges` internal storage with copy-on-write `Dictionary<string, EdgeModel[]>`
  behind `volatile` references (empty → `null`). Rewrite `AddOutEdge`/`AddIncomingEdge`,
  `RemoveOutGoingEdge`/`RemoveIncomingEdge` (both overloads), and `SetOutEdges`/the internal ctor to
  build-new-and-swap (never mutate a published array/dict in place). Keep grouping by
  edge-property-id.
- Preserve the removal contract exactly: `RemoveOutGoingEdge(edge)`/`RemoveIncomingEdge(edge)` still
  return the affected edge-property-ids (so `Fallen8.TryRemoveGraphElement_private` can replay them
  on rollback), and detach semantics are unchanged.

## Phase 2 — Public surface (the API break, read-only)
- Replace the public `OutEdges`/`InEdges` fields + `TryGetOutEdge`/`TryGetInEdge` return type with a
  read-only shape (read-only view or accessor methods returning `IReadOnlyList<EdgeModel>` /
  `IReadOnlyDictionary<string, IReadOnlyList<EdgeModel>>`). Keep `GetInDegree`/`GetOutDegree`/
  `GetAllNeighbors`/`GetIncomingEdgeIds`/`GetOutgoingEdgeIds`/`TryGetOut|InEdge` semantics identical.
- Update the internal consumers to the new shape: `PathHelper`, `BidirectionalLevelSynchronousSSSP`,
  `WeightedDijkstraShortestPath`, `BreathFirstSearchSubgraphAlgorithm`, and
  `Controllers/Model/Vertex` (REST DTO — its output shape must not change).
- Bump the `fallen-8-core` package/library version (breaking public-API change). REST `api/v{version}`
  unchanged.

## Phase 3 — Migrate the poison-injection rollback tests
- Provide an internal fault-injection mechanism (e.g. an `internal` test hook to set a raw/poison
  adjacency state) replacing the removed `v.InEdges = v.InEdges.SetItem(...)` writes, and update
  `CorrectnessFixesTest` + `CorrectnessFixesFollowupsTest` to use it — still forcing the mid-removal
  fault and asserting counts + adjacency are restored on rollback. Coverage preserved, not weakened.

## Phase 4 — Measure & document
- Re-run the Phase 0 benchmark; record the before/after adjacency memory here. Update the
  `core-storage-representation` Phase 4 status and the `memory-footprint` §1 note (adjacency overhead
  now removed). Note any residual (e.g. the `Dictionary` wrapper cost vs a future CSR layout).

### Measurements (`AdjacencyMemoryBenchmark`, this machine, .NET 10, Server GC)

`bytes_per_edge_incl_adjacency` is the retained-memory delta of adding the edges divided by the
edge count; the `EdgeModel` body is identical before/after, so the before→after delta is purely the
adjacency representation. The win is **degree-dependent**: the per-group array-vs-AVL-tree saving
scales with the group size, while the per-vertex `Dictionary<string, EdgeModel[]>` container has a
fixed cost (its buckets + entries arrays), so a low-degree vertex with a single tiny group does not
recover that fixed cost.

| avg group degree | before (B/edge) | after (B/edge) | Δ B/edge | total retained before→after |
|------------------|-----------------|----------------|----------|-----------------------------|
| 2  (200k V / 400k E)  | 298.1 | 314.9 | **+16.8 (≈ +6%)** | 129.5 → 135.9 MB |
| 10 (200k V / 2.0M E)  | 210.2 | 152.6 | **−57.6 (≈ −27%)** | 416.6 → 306.8 MB (−110 MB) |
| 20 (50k V / 1.0M E)   | 197.6 | 128.8 | **−68.8 (≈ −35%)** | 192.4 → 126.8 MB (−66 MB) |

Reading: at **degree ≈ 2** (one edge-property group of ~2 edges per direction) the change is a
small **regression** — a `Dictionary<K,V>` instance's fixed buckets+entries overhead exceeds a
1-entry `ImmutableDictionary`, and the 2-node `ImmutableList` saves too little to cover it. The
**crossover is ≈ degree 3–4**; from there the tree-node overhead the old representation carried
(~48 B/AVL-node × 2 lists per edge) is shed for ~8 B array slots, so per-edge adjacency drops
~27–35% and the old cost's floor (~48 B/edge of AVL nodes that never goes away) is what the new
array representation removes. `bytes_per_vertex_no_edges` is unchanged (82.6 B) — an edge-free
vertex holds no adjacency container in either representation (empty → `null`).

Honest residual: the `Dictionary` container is itself heavier than a compact map for the
single-group common case; a future CSR / small-map-inline layout (a non-goal here) would remove that
fixed cost and make the win monotonic across all degrees. The benchmark is degree-tunable via
`F8_ADJ_VERTICES` / `F8_ADJ_EDGES` so the crossover is reproducible.

## Status
- [x] Phase 0 — concurrency test + adjacency-memory benchmark (baseline)
- [x] Phase 1 — copy-on-write `Dictionary<string, EdgeModel[]>` storage + rewritten add/remove
- [x] Phase 2 — read-only public surface + consumer updates + version bump (0.0.14 → 0.1.0)
- [x] Phase 3 — migrate the poison-injection rollback tests (internal fault-injection hook)
- [x] Phase 4 — measure & document

## Notes
- Lock-free reads are the crux: build-then-`volatile`-swap, never mutate a published structure;
  readers capture once. Same discipline as the property store (memory-footprint M1) and the
  segmented master store (core-storage).
- Do NOT change edge-property-id grouping semantics or the persistence format (load already
  reconstructs adjacency from `Dictionary<string, List<EdgeModel>>`).
