# Weighted Shortest Paths — Specification

> **Status:** Planned. This is the B8 item deferred from `correctness-fixes` (the path
> cost/weight surface exists but no algorithm honours it). The decision (recorded in
> `correctness-fixes/plan.md` Phase 4) is **(a) implement**, not remove the surface.

## 1. Problem

The path-finding API exposes edge/vertex **cost** and a **max path weight**, and the REST +
code-generation layers already compile and pass them through — but the only algorithm ignores
them. Concretely, verified in the current tree:

- `ShortestPathDefinition` carries `EdgeCost`, `VertexCost` (`Delegates.EdgeCost`/`VertexCost`,
  both `EdgeModel/VertexModel -> double`) and `MaxPathWeight`
  ([ShortestPathDefinition.cs](../../fallen-8-core/Algorithms/Path/ShortestPathDefinition.cs)).
- `PathElement` has `Weight` and `CalculateWeight(vertexCost, edgeCost)` (`= vertexCost(target) +
  edgeCost(edge)`); `Path` aggregates element weights into `Path.Weight`
  ([PathElement.cs](../../fallen-8-core/Algorithms/Path/PathElement.cs),
  [Path.cs](../../fallen-8-core/Algorithms/Path/Path.cs)).
- The REST DTO `PathSpecification` carries `pathAlgorithmName`, `maxPathWeight`, and a
  `cost { vertexCost, edgeCost }` block; `CodeGenerationHelper.GeneratePathTraverser` compiles those
  cost fragments into `EdgeCost`/`VertexCost` delegates; `GraphController.CalculateShortestPath`
  builds a `ShortestPathDefinition` including the costs and selects the algorithm **by name**; and
  `PathREST.TotalWeight` already surfaces `Path.Weight`.
- **The only shortest-path algorithm, `BidirectionalLevelSynchronousSSSP` (plugin name `"BLS"`), is
  a bidirectional _level/hop-count_ BFS.** It accepts `maxPathWeight`, `edgeCost`, `vertexCost` as
  parameters and **never uses them**: the frontier expansion applies only the filters, and
  `PathElement.CalculateWeight` is never called, so every path element keeps its default weight
  `0.0` and every `Path.Weight` (hence `PathREST.TotalWeight`) is `0`.

### False confidence in the current tests

`PathTest.PathWithWeightedEdges_ShouldFindShortestWeightedPath` *looks* like it proves weighted
routing, but it is a **false positive**. Its graph (A→C=2, C→E=3, A→B=5, B→D=1, C→D=1, D→E=3)
has the least-weight A→E path (A→C→E, weight 5) also be the **fewest-hop** path (2 hops), so BLS's
hop-count answer coincides with the weighted answer. The test would still pass if edge cost were
ignored — which it is. A genuine weighted test must use a graph where the least-weight path has
**more hops** than the fewest-hop path, so a hop-count BFS gives the wrong answer.

## 2. Decision & contract

Add a **new** shortest-path plugin implementing `IShortestPathAlgorithm` — a weighted
single-source shortest path (Dijkstra), plugin name **`"DIJKSTRA"`** — rather than retrofitting
weights into `BLS`. Rationale:

- `BLS` is a correct, useful *unweighted/hop-count* bidirectional search; retrofitting weights
  would turn it into a fundamentally different algorithm and change its established semantics
  (existing tests rely on hop-count behaviour).
- The plugin architecture already selects the algorithm by name, so a second algorithm is the
  idiomatic extension. **No REST/DTO/codegen changes are required** — a caller opts in with
  `"pathAlgorithmName": "DIJKSTRA"`, and the already-compiled `cost` block is finally honoured.

### Algorithm contract (`"DIJKSTRA"`)

- **Cost model.** The cost of extending a path to a neighbour `v` across edge `e` is
  `edgeCost(e) + vertexCost(v)`. Defaults when a delegate is null: `edgeCost → 1.0` per edge,
  `vertexCost → 0.0`. Thus **with no cost delegates the algorithm computes a fewest-hop shortest
  path** (each edge costs 1) — a sensible drop-in that agrees with `BLS` on path *length*.
- **Non-negative costs.** Dijkstra assumes non-negative step costs. Costs are expected `>= 0`;
  behaviour under negative costs is undefined. **Documented defensive rule:** if a computed step cost
  `edgeCost(e) + vertexCost(v)` is `< 0`, it is **clamped to `0`** — for both the priority ordering and
  the recorded element weight — and a warning is logged once per calculation. This keeps the search
  finite and consistently ordered (so `Path.Weight` still equals the sum of the element step costs) and
  never loops or silently misorders; it does not attempt to compute a correct negative-weight result
  (that is out of scope — see §4).
- **Traversal direction.** Matches `BLS`: both incoming and outgoing edges are traversable
  (undirected reachability over directed edges), and each `PathElement` records the traversal
  `Direction`. This keeps results comparable to `BLS` and honours the same filters.
- **Filters.** `EdgePropertyFilter`, `EdgeFilter`, `VertexFilter` are applied exactly as in `BLS`
  (an edge/vertex rejected by a filter is not traversable).
- **Bounds.**
  - `MaxDepth` — maximum number of edges (hops) in a returned path; a path may not exceed it. The
    search is keyed on `(vertexId, hops)`, so its state space is `O(V · MaxDepth)`. **Hop-cap
    correctness argument:** with non-negative (clamped) step costs the minimum-weight walk is
    achieved by a *simple* path, and the algorithm returns only loop-free paths; a loop-free/simple
    path visits at most `VertexCount` vertices, i.e. at most `VertexCount − 1` edges. The engine
    therefore caps the effective hop bound at `min(MaxDepth, VertexCount − 1)`. This is
    **result-invariant** — it cannot exclude any returned path (neither the single least-weight path
    nor Yen's K loop-free paths) and never *increases* the caller's `MaxDepth` — and it only stops a
    huge opt-in `MaxDepth` from enumerating redundant deeper states against an unreachable
    destination in a cyclic component (a resource guard on that opt-in request).
  - `MaxPathWeight` — a path whose cumulative weight exceeds it is never returned (pruned during
    search). The bound is **inclusive**: a path whose weight *equals* `MaxPathWeight` is allowed.
  - `MaxResults` — the number of paths to return (the `K` in K-shortest when `MaxResults > 1`).
- **Result semantics.**
  - `MaxResults == 1`: the single least-weight path within the bounds (ties broken deterministically,
    e.g. fewer hops then lower first-differing edge id).
  - `MaxResults > 1`: the **K least-weight loop-free paths** (Yen's algorithm over the Dijkstra
    shortest-path subroutine), returned in **non-decreasing weight order**.
  - No path within the bounds → `TryCalculateShortestPath` returns `false` with an empty list
    (mirrors `BLS`).
  - `source == destination`, non-existent/removed endpoints, `MaxDepth == 0`, `MaxResults <= 0`
    → no path (mirrors `BLS`'s guard clauses).
- **Weights populated.** Every returned `Path` has `Path.Weight` = sum of its element step costs,
  and each `PathElement.Weight` = its step cost, so `PathREST.TotalWeight` is correct and results are
  genuinely ordered by weight.

## 3. Acceptance criteria

- A `"DIJKSTRA"` query returns the correct **least-weight** path on a graph where that path has
  **more hops** than the fewest-hop path, and a side-by-side `"BLS"` query on the same graph returns
  the *different* fewest-hop path — proving weights are actually consumed (the discriminating test the
  current suite lacks).
- `Path.Weight` / `PathREST.TotalWeight` equal the summed costs for `"DIJKSTRA"` results (non-zero
  when costs are supplied).
- `MaxPathWeight` excludes over-weight paths (including the case where the only route exceeds it →
  empty). `MaxDepth` caps hop length even when a longer route is cheaper. `MaxResults = K` returns up
  to K distinct loop-free paths in non-decreasing weight order.
- Vertex cost contributes to the total; all three filters are respected; default (no-cost) behaviour
  yields a fewest-hop path of the same length `BLS` finds.
- Disconnected / non-existent / `source == destination` / `MaxDepth = 0` cases return an empty list,
  not null, and `false`.
- The plugin is discovered and resolvable by name (`"DIJKSTRA"`) alongside `"BLS"`, over both the
  engine API (`TryCalculateShortestPath(out, "DIJKSTRA", def)`) and the REST endpoint
  (`"pathAlgorithmName": "DIJKSTRA"`).
- The misleading `PathWithWeightedEdges_ShouldFindShortestWeightedPath` is corrected (renamed to
  reflect that `BLS` is hop-count, and a real weighted test added against `"DIJKSTRA"`).
- Build clean; full suite green.

## 4. Out of scope / follow-ups

- **Bidirectional / A\* optimisation.** A unidirectional Dijkstra (with Yen's for K-shortest) is the
  correctness-first target. Bidirectional Dijkstra or A\* with a heuristic are performance follow-ups,
  not required here.
- **Negative-weight support** (Bellman-Ford/Johnson) is out of scope; costs are assumed non-negative.
- **Retiring/aliasing `BLS`** — `BLS` stays as the hop-count algorithm; this feature does not change
  its behaviour or its default status (`PathSpecification.PathAlgorithmName` still defaults to `BLS`).
