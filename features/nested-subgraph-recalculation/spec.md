# Nested Subgraph Recalculation & Persistence — Specification

> **Status:** Planned. Originates from limitations documented in
> [../subgraph/spec.md](../subgraph/spec.md) (§9) and the principal-architect review
> (findings S3/S4). Tracked by its GitHub feature issue.

## 1. Problem

A subgraph is itself a `Fallen8` instance with its own `SubGraphFactory`, so a
"subgraph of a subgraph" is created on the **child** subgraph's factory. Two defects make
nested subgraphs second-class today:

- **Recalculation never reaches them.** Every subgraph a factory creates is stamped with
  `SourceFallen8Id == that factory's own graph id`, and nested subgraphs register on the
  child's factory. The root factory's `RecalculateSubGraphsRecursive` scans its own
  registry for entries whose source is a subgraph id and always finds none, so
  `RecalculateAllSubGraphs` only refreshes root-level subgraphs. The `_subGraphDependencies`
  map is written and cleared but never read — evidence the cross-factory model was never
  finished.
- **Persistence skips them.** `GetPersistableRecipes()` returns only root-sourced recipes,
  and recipes carry a `SourceFallen8Id` but there is no mechanism to rehydrate a subgraph
  whose source is *another subgraph* in dependency order.

The engine already guards correctness for the single-level case (the algorithm rebinds to
its source on each call, recalculation is sequential), so this feature is about making the
nested topology first-class, not about fixing single-level bugs.

## 2. Goals / non-goals

**Goals**
- `RecalculateAllSubGraphs` refreshes the full dependency tree (root → nested → …) in an
  order where each subgraph is recomputed only after its source.
- Nested subgraphs persist and rehydrate, sourced from their parent subgraph (also
  restored), not the root.
- A single, discoverable dependency registry replaces the currently-unused
  `_subGraphDependencies` map.

**Non-goals**
- Distributed or incremental recalculation.
- Cycles in the dependency graph (a subgraph cannot source itself, directly or
  transitively); these are rejected.

## 3. Design sketch

- **Dependency registry.** Track `subGraphId → sourceId` for every registered subgraph in a
  registry reachable from the root graph (either a shared registry injected into child
  factories, or the root factory aggregating child registrations). Reads drive recalculation
  order and persistence.
- **Explicit source.** Give `ISubGraphAlgorithm.TryCreateSubgraph` (or the factory call) an
  explicit source graph instead of relying on the cached instance's bound `_fallen8`, so a
  nested subgraph is unambiguously recomputed against its parent. This supersedes the
  current "rebind on cache hit" workaround.
- **Topological recalculation.** Recompute in dependency order (sources before dependents),
  detecting and rejecting cycles.
- **Persistence.** Extend the recipe with its `SourceFallen8Id` (already present) and, on
  load, rehydrate roots first, then subgraphs whose source is an already-rehydrated
  subgraph, resolving the source by id.

## 4. Acceptance criteria

- Creating B from A (a subgraph) then changing the root and calling
  `RecalculateAllSubGraphs` refreshes both A and B, with B reflecting the recomputed A.
- A save/load round-trip restores a two-level subgraph chain with correct contents.
- A dependency cycle is rejected with a clear error, not a stack overflow or hang.
- Existing single-level behaviour and tests are unchanged.

## 5. Testing

- Two-level and three-level recalculation reflecting source changes at each level.
- Save → load round-trip of a nested chain.
- Cycle rejection.
- Regression: all existing subgraph tests still pass.
