# Engine-Performance Follow-ups — Specification

> **Status:** Planned. The two items the `engine-performance` council deferred (documented in
> `features/engine-performance/plan.md`): **P6** parent-pointer path reconstruction and **P4** routing
> ordered `IndexScan` operators onto the sorted RangeIndex. Both are refinements with real
> risk/reward trade-offs — each is attempted here under a hard guardrail, and **deferred-with-rationale
> if it cannot be done without changing observable results / semantics** (a definitive outcome, not a
> silent skip).

## 1. P6 — parent-pointer BLS path reconstruction

**Today:** the bounded reconstruction (honour `maxResults`, early-terminate) already landed in
engine-performance. But paths are still built **copy-on-extend** — extending a partial path copies its
element list — so building a length-`L` path is O(L²) allocation across the walk
(`BidirectionalLevelSynchronousSSSP` + `Path`/`PathElement`).

**Goal:** build paths from **parent pointers** (each partial path references its predecessor;
materialise the element list once, at emit) to cut the per-extension copying to O(1), materialising
each final path once.

**HARD guardrail:** BLS's observable results — the exact set of paths, their element order, and the
count for every `maxResults`/`maxDepth` — MUST be byte-identical (pinned by `PathTest` /
`PathTestEdgeCases`, incl. the exact-path assertions and the DIJKSTRA-vs-BLS discriminating test).
Prefer an INTERNAL reconstruction change that leaves the public `Path`/`PathElement` surface
unchanged. If a public `Path` shape change is unavoidable, it needs a version bump + justification. If
results cannot be preserved (or the rewrite is disproportionately risky for the allocation payoff),
**DEFER P6 with a written rationale** and leave the current bounded reconstruction.

## 2. P4 — ordered IndexScan onto the sorted RangeIndex

**Today:** `RangeIndexScan → RangeIndex.Between` is already O(log n + k) (engine-performance). But the
GENERIC `Fallen8.IndexScan` ordered operators (Greater/GreaterOrEqual/Lower/LowerOrEqual/Between) go
through `FindElementsIndex`, which does an **O(n) scan** over `index.GetKeyValues()` applying the
operator and a cross-bucket `.Distinct()`, for ALL index kinds.

**Goal:** when the target index is a `RangeIndex` AND the operator is an ordered one, route through
the RangeIndex's O(log n + k) sorted methods instead of the O(n) scan; keep the generic path for
non-range indices and non-ordered operators.

**HARD guardrail:** the result SET must be identical to the current `FindElementsIndex` output,
including the **cross-bucket `.Distinct()` dedup** (a graph element that appears under multiple key
values must appear once) and any ordering expectation callers have. So the rerouted path must apply
the same dedup. Only reroute when it is provably semantics-preserving; otherwise keep the current path
and **DEFER P4 with a written rationale**.

## 3. Acceptance criteria

- **P6 (if landed):** a benchmark shows reduced path-reconstruction allocation; ALL `PathTest`/
  `PathTestEdgeCases`/`WeightedDijkstraPathTest` pass unchanged (BLS results byte-identical); public
  `Path` surface unchanged (or version-bumped + justified). **If deferred:** a clear rationale in the
  plan + this spec, current behaviour retained.
- **P4 (if landed):** ordered `IndexScan` on a RangeIndex is O(log n + k) (benchmarked across
  selectivities) and returns the IDENTICAL (deduped) result set as before; non-range indices and
  non-ordered operators are unaffected; RangeIndex/IndexScan tests pass. **If deferred:** rationale +
  current behaviour retained.
- Full suite green; benchmarks opt-in (`[TestCategory("Benchmark")]`+`[Ignore]`); no fabricated
  numbers.

## 4. Non-goals

- Changing the path/index public contracts beyond what P6/P4 strictly require (and only with a version
  bump if so).
- Bidirectional/A* path optimisation; a new index structure. These stay as the engine-performance
  spec's non-goals.
