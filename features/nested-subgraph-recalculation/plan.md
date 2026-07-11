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
- [x] Phase 0 — reproduce (nested recalc + nested persistence tests)
- [x] Phase 1 — explicit source (`TryCreateSubGraphFromSource`; source threaded through create)
- [x] Phase 2 — dependency-ordered recalc with a cycle/revisit guard (`visited` set)
- [x] Phase 3 — nested persistence (recipe `SubGraphId`, topological rehydration, `fromSubGraph` create)
- [x] Phase 4 — verify & document

## Outcome

- `SubGraphFactory.TryCreateSubGraphFromSource` (typed + named) creates a subgraph from any
  source graph (the graph itself or another registered subgraph), registering it with the
  correct `SourceFallen8Id` and tracking the dependency.
- `RecalculateAllSubGraphs` now refreshes the full dependency tree in order (sources before
  dependents) with a `visited` guard that also protects against cycles.
- Persistence: recipes carry the subgraph's own id; `GetPersistableRecipes` includes nested
  subgraphs; `RehydrateFromRecipes` resolves each recipe's source by saved id and rebuilds
  in topological order. `CreateSubGraphTransaction` / `PUT /subgraph?fromSubGraph=<name>`
  create nested subgraphs over REST so they carry recipes and persist.
- Tests: `SubGraphNestedTest` (2- and 3-level recalculation) and
  `SubGraphPersistenceTest.SaveThenLoad_NestedSubgraph_IsRehydratedFromItsParent`.

## Note on cycles

Cycles cannot form through the creation API (each create produces a brand-new subgraph that
is not yet anyone's source), so there is no create-time rejection to test; the recursion's
`visited` guard is defensive should a cycle ever arise.
