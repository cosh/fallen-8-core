# Nested Subgraph Recalculation & Persistence — Plan

Companion to [spec.md](./spec.md).

## Phase 0 — Reproduce
- Add failing tests: (a) `RecalculateAllSubGraphs` does not refresh a subgraph-of-a-subgraph
  after the root changes; (b) a nested chain is lost across save/load.

## Phase 1 — Explicit source
- Change the subgraph creation path so the source graph is passed explicitly to the
  algorithm rather than read from the cached instance's `_fallen8`. Update
  `SubGraphFactory.TryGetOrLoadAlgorithm` / `TryCreateSubgraph` accordingly.
- Retire the "re-initialize on cache hit" workaround once the source is explicit.

## Phase 2 — Dependency registry & ordered recalculation
- Introduce a dependency registry reachable from the root graph (replace the unused
  `_subGraphDependencies`). Register `subGraphId → sourceId` on create; remove on deregister.
- Rewrite `RecalculateAllSubGraphs` to compute a topological order over the registry and
  recalculate sources before dependents. Detect and reject cycles.

## Phase 3 — Nested persistence
- Persist nested recipes (they already carry `SourceFallen8Id`). On load, rehydrate roots
  first, then subgraphs whose source id resolves to an already-rehydrated subgraph.
- Extend `GetPersistableRecipes()` to include nested subgraphs and
  `RehydrateFromRecipes(...)` to resolve non-root sources.

## Phase 4 — Verify & document
- Full test pass; update [../subgraph/spec.md](../subgraph/spec.md) §9 to mark the nested
  limitation resolved.

## Status
- [ ] Phase 0 — reproduce
- [ ] Phase 1 — explicit source
- [ ] Phase 2 — dependency registry & ordered recalc
- [ ] Phase 3 — nested persistence
- [ ] Phase 4 — verify & document
