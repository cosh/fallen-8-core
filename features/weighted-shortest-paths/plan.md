# Weighted Shortest Paths — Plan

Companion to [spec.md](./spec.md). Correctness first (single least-weight path), then K-shortest,
then wiring/docs. Every phase: failing test → implementation → green.

## Phase 1 — Weighted single-source shortest path (Dijkstra), `MaxResults == 1`
- New plugin `WeightedDijkstraShortestPath : IShortestPathAlgorithm` in
  `fallen-8-core/Algorithms/Path/` (MIT header; mirror `BLS`'s `IPlugin` members —
  `PluginName = "DIJKSTRA"`, `PluginCategory = typeof(IShortestPathAlgorithm)`, description,
  `Initialize`, `Dispose`).
- Dijkstra with a `PriorityQueue<VertexModel, double>` (min-heap by cumulative weight). Relaxation:
  reaching neighbour `v` via edge `e` costs `stepCost(e, v) = (edgeCost?(e) ?? 1.0) + (vertexCost?(v)
  ?? 0.0)`. Track best-known distance and predecessor `(edge, edgePropertyId, direction)` per vertex.
- Reuse `BLS`'s traversal shape: expand **both** incoming and outgoing edges, applying
  `EdgePropertyFilter` / `EdgeFilter` / `VertexFilter` identically (factor the "valid neighbours of a
  vertex" logic so both algorithms stay consistent; do not regress `BLS`).
- Honour bounds during search: stop expanding a vertex once its path length would exceed `MaxDepth`;
  never settle/return a path whose cumulative weight exceeds `MaxPathWeight`.
- Guard clauses identical to `BLS`: null/removed/nonexistent endpoints, `source == destination`,
  `MaxDepth == 0`, `MaxResults <= 0` → return empty list + `false`.
- Reconstruct the path from predecessors into `Path`/`PathElement`, setting each element's step cost
  as its `Weight` (so `Path.Weight` aggregates correctly). Order of elements source→destination.
- **Defensive negative-cost handling:** if a computed `stepCost < 0`, log a warning once and treat it
  per a documented rule (clamp to 0 for ordering, or bail to empty) — do not infinite-loop or silently
  misorder. Record the chosen rule in spec §2.
- Tests (the discriminating ones the suite lacks):
  - **Weight beats hops:** graph with A→B (edge cost 10) and A→C→B (1 + 1 = 2). `"DIJKSTRA"` returns
    A→C→B (weight 2, 2 hops); a side-by-side `"BLS"` returns A→B (1 hop). Assert they differ and each
    is correct for its algorithm.
  - `Path.Weight` / `PathREST.TotalWeight` equals the summed cost (non-zero).
  - Vertex cost contributes (non-null `VertexCost` changes the chosen route/total).
  - `MaxPathWeight` prunes: the cheap route is excluded when below threshold; empty when the only
    route exceeds it.
  - `MaxDepth` caps: a cheaper long route is rejected when it exceeds `MaxDepth`, a costlier short one
    returned.
  - Default (no cost delegates) → fewest-hop path of the same length `BLS` returns.
  - Disconnected / nonexistent endpoint / `source == destination` / `MaxDepth == 0` → empty + `false`.
  - Filters (`EdgeFilter` label restriction, `VertexFilter`) respected.

## Phase 2 — K-shortest loop-free paths (`MaxResults > 1`)
- Implement **Yen's algorithm** on top of the Phase 1 Dijkstra subroutine: seed with the shortest
  path, then generate candidate deviations (spur paths) at each node, forbidding the edges/nodes that
  would reproduce an already-found path, using a candidate min-heap keyed by total weight. Emit up to
  `MaxResults` paths in non-decreasing weight order, each loop-free and within `MaxDepth`/
  `MaxPathWeight`.
- Tests:
  - K distinct paths returned in non-decreasing weight order; exactly `min(K, available)` when fewer
    than K exist.
  - Ties (equal-weight alternatives) returned deterministically.
  - K-shortest still respects `MaxDepth` / `MaxPathWeight` / filters.
  - `MaxResults` larger than the number of loop-free paths → all of them, no duplicates.

## Phase 3 — Wiring, docs, and test hygiene
- Confirm the plugin is auto-discovered and resolvable by name over both entry points:
  `IFallen8.TryCalculateShortestPath(out, "DIJKSTRA", def)` and the REST endpoint
  `POST /path/{from}/{to}` with `"pathAlgorithmName": "DIJKSTRA"` (add an end-to-end controller test
  through `GraphController.CalculateShortestPath` with a compiled `cost` block, exercising
  `CodeGenerationHelper`). No controller/DTO/codegen changes expected — assert that.
- **Fix the false-positive test:** rename `PathWithWeightedEdges_ShouldFindShortestWeightedPath` to
  make clear it exercises `BLS`'s hop-count behaviour (its assertion is fine for `BLS`), and add the
  genuine weighted equivalent under `"DIJKSTRA"` (covered in Phase 1). Leave other `BLS` tests intact.
- Docs: mention the `"DIJKSTRA"` option and that it honours the `cost` block in the
  `PathSpecification` XML docs / endpoint remarks (OpenAPI surfaces it); a short note in the feature
  README is optional. No on-disk/format changes.

## Status
- [ ] Phase 1 — weighted Dijkstra (single least-weight path) + discriminating tests
- [ ] Phase 2 — Yen's K-shortest loop-free paths
- [ ] Phase 3 — name resolution + end-to-end REST test + fix the misleading test + docs

## Notes
- Keep `BLS` untouched (behaviour + default). Factor shared neighbour-enumeration carefully so a
  refactor does not regress `BLS`'s existing tests.
- `PriorityQueue<,>` is available on net10; no external dependency.
- Bidirectional/A\* and negative-weight algorithms are explicit non-goals (spec §4).
