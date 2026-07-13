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

## Status
- [ ] Phase 0 — concurrency test + adjacency-memory benchmark (baseline)
- [ ] Phase 1 — copy-on-write `Dictionary<string, EdgeModel[]>` storage + rewritten add/remove
- [ ] Phase 2 — read-only public surface + consumer updates + version bump
- [ ] Phase 3 — migrate the two poison-injection rollback tests
- [ ] Phase 4 — measure & document

## Notes
- Lock-free reads are the crux: build-then-`volatile`-swap, never mutate a published structure;
  readers capture once. Same discipline as the property store (memory-footprint M1) and the
  segmented master store (core-storage).
- Do NOT change edge-property-id grouping semantics or the persistence format (load already
  reconstructs adjacency from `Dictionary<string, List<EdgeModel>>`).
