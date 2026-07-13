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
  `RemoveOutGoingEdge`/`RemoveIncomingEdge` (both overloads), and the internal ctor to
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
  `CorrectnessFixesTest` + `CorrectnessFixesFollowupsTest` + `EnginePerformanceTest` (all three used
  the idiom) to use it — still forcing the mid-removal fault and asserting counts + adjacency are
  restored on rollback. Coverage preserved, not weakened.

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

Honest residual (**resolved in Phase 5 below**): the `Dictionary` container is itself heavier than a
compact map for the single-group common case; the small-map-inline layout added in Phase 5 removes
that fixed cost and makes the win monotonic across all degrees. The benchmark is degree-tunable via
`F8_ADJ_VERTICES` / `F8_ADJ_EDGES` so the crossover is reproducible.

## Phase 5 — Inline small-map (the monotonic-win optimization)

Phases 1-4 moved adjacency to `Dictionary<string, EdgeModel[]>`, which shed the AVL/HAMT overhead but
left every vertex carrying a per-direction `Dictionary` container whose fixed buckets+entries cost made
a sparse single-group (~degree-2) vertex a small **regression** vs. the 1-entry `ImmutableDictionary`
(the +6% row above). Phase 5 removes that fixed cost for the common case, making the win monotonic.

- New `Model/EdgeAdjacency.cs`: a sealed, **immutable-after-construction** type holding EITHER a single
  inline group (`_soleKey` + `EdgeModel[] _soleGroup`, **no dictionary**) OR a
  `Dictionary<string, EdgeModel[]>` fallback for a genuinely multi-group vertex. A vertex whose edges
  are all one edge-property-id (one group per direction — the overwhelmingly common shape) now carries
  no dictionary at all; it promotes to the map only when a second distinct edge-property-id appears.
  Add/remove/read logic (append, targeted + scan removal, group lookup, key/degree/count, a struct
  enumerator over the groups) lives in this type.
- `VertexModel` holds `private volatile EdgeAdjacency _outEdges/_inEdges` (null when empty). Every
  mutation returns a NEW `EdgeAdjacency` published by ONE volatile reference assignment — the same
  whole-reference copy-on-write swap as before, so **lock-free reads are preserved** (readers capture
  the reference once; the instance is immutable, so a captured view is snapshot-stable and correct on
  weak-memory hardware). `RemoveById` still dereferences each entry's `.Id`, so a poisoned null slot
  faults BEFORE the publish, leaving live adjacency intact and returning the affected edge-property-ids
  for rollback replay. The internal hot-path consumers `foreach` the groups allocation-free via the
  struct enumerator (unchanged bodies).
- Public surface, persistence round-trip, REST `/vertex` shape, and path/subgraph/degree/neighbor/
  edge-id semantics are all unchanged; the read-only view transparently presents the single-group case
  as a 1-entry dictionary. Version stays **0.1.0** (no further public break).

### Measurements (`AdjacencyMemoryBenchmark`, this machine, .NET 10, Server GC)

All three representations were measured on the same machine in one session; the `ImmutableDictionary`
and `Dictionary` figures reproduce the Phase 4 table exactly (an independent Dictionary re-run gave
315.0 / 152.6 / 128.8 B/edge), so the comparison is apples-to-apples.

| avg group degree | ImmutableDictionary (B/edge) | Dictionary (B/edge) | **inline (B/edge)** | inline Δ vs ImmutableDictionary |
|------------------|------------------------------|---------------------|---------------------|---------------------------------|
| 2  (200k V / 400k E)  | 298.1 | 314.9 | **162.8** | **−135.3 (≈ −45%)** |
| 10 (200k V / 2.0M E)  | 210.2 | 152.6 | **117.4** | **−92.8 (≈ −44%)** |
| 20 (50k V / 1.0M E)   | 197.6 | 128.8 | **111.2** | **−86.4 (≈ −44%)** |

Total retained (vertices + edges): degree 2 **129.5 → 77.9 MB** (−40%), degree 10 **416.6 → 239.6 MB**
(−42%), degree 20 **192.4 → 110.0 MB** (−43%). `bytes_per_vertex_no_edges` stays **82.6 B** (an
edge-free vertex holds no adjacency container in any representation — empty → `null`).

Reading: the degree-2 **regression is gone** — inline is a **−45%** win there, not a +6% loss — and the
win is now **monotonic**: ≈ **−44%** at every measured degree vs. the original `ImmutableDictionary`
baseline. Because the benchmark's edges all share one edge-property-id (`"friend"`), every vertex is
single-group, so inline drops the entire per-vertex, per-direction `Dictionary` container (~145 B of
buckets+entries) that even the flat-`Dictionary` version still allocated, leaving one small immutable
`EdgeAdjacency` object plus the `EdgeModel[]` that existed anyway. That removed cost is fixed per
vertex, so the per-edge saving is largest at low degree (fewest edges to amortise it over) — which is
also why inline beats the intermediate `Dictionary` representation at every degree (−48% / −23% / −14%
at degree 2 / 10 / 20).

Honest residual: the win assumes the common single-group topology. A genuinely **multi-group** vertex
(edges under several distinct edge-property-ids) falls back to the `Dictionary` and additionally carries
the small `EdgeAdjacency` wrapper object (~24–32 B/vertex/direction) on top of it, so such vertices are
marginally heavier than the plain-`Dictionary` representation — still far below `ImmutableDictionary`.
This benchmark is single-group by construction and does not exercise that case; it is the standard
small-map-inline trade-off and nets a large win for typical graphs.

## Status
- [x] Phase 0 — concurrency test + adjacency-memory benchmark (baseline)
- [x] Phase 1 — copy-on-write `Dictionary<string, EdgeModel[]>` storage + rewritten add/remove
- [x] Phase 2 — read-only public surface + consumer updates + version bump (0.0.14 → 0.1.0)
- [x] Phase 3 — migrate the poison-injection rollback tests (internal fault-injection hook)
- [x] Phase 4 — measure & document
- [x] Phase 5 — inline small-map (single-group inline vs multi-group fallback; monotonic win)

## Notes
- Lock-free reads are the crux: build-then-`volatile`-swap, never mutate a published structure;
  readers capture once. Same discipline as the property store (memory-footprint M1) and the
  segmented master store (core-storage).
- Do NOT change edge-property-id grouping semantics or the persistence format (load already
  reconstructs adjacency from `Dictionary<string, List<EdgeModel>>`).
