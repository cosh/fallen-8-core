# Engine-Performance Follow-ups — Plan

Companion to [spec.md](./spec.md). Each item is attempted under its hard guardrail; defer-with-
rationale rather than change observable results/semantics.

## Phase 1 — P4 (ordered IndexScan → RangeIndex) [lower risk, do first]
- In `Fallen8.IndexScan`/`FindElementsIndex`: when the resolved index is a `RangeIndex` and the
  operator is ordered (Greater/GreaterOrEqual/Lower/LowerOrEqual/Between/analogues), call the
  RangeIndex's O(log n + k) sorted methods; else keep the current generic O(n) scan. Apply the SAME
  cross-bucket `.Distinct()` dedup on the rerouted results so the set is identical.
- Test: parity — for a RangeIndex populated so at least one graph element is indexed under multiple
  key values (forcing the dedup), the rerouted result equals the old `FindElementsIndex` result for
  each ordered operator + selectivity (empty/all/partial/inverted); non-range index unaffected.
- Benchmark: ordered IndexScan on a large RangeIndex, O(log n + k) vs the old O(n) (opt-in).
- If the dedup/semantics can't be cleanly preserved, revert to the generic path and DEFER (rationale).

## Phase 2 — P6 (parent-pointer path reconstruction) [higher risk]
- Rework `BidirectionalLevelSynchronousSSSP` reconstruction to carry parent pointers and materialise
  each emitted `Path` once, instead of copy-on-extend. Keep the public `Path`/`PathElement` surface
  unchanged (internal build change). Preserve emission order so `Take(maxResults)` yields the same
  first-K paths.
- Test: BLS results byte-identical — reuse/extend the existing exact-path assertions; add an
  allocation benchmark (opt-in) showing the reduction. Confirm `maxResults`/`maxDepth` edge cases and
  the BLS-vs-DIJKSTRA discriminating test still hold.
- If BLS results shift or a public `Path` change is forced without clear justification, DEFER P6
  (rationale) — keep the current bounded reconstruction.

## Phase 3 — Measure & document
- Record P4 (and P6 if landed) benchmark numbers here; mark the engine-performance plan's deferred
  P4/P6 as landed or definitively-deferred-with-reason.

## Status
- [x] Phase 1 — P4 ordered IndexScan reroute — **LANDED**
- [x] Phase 2 — P6 parent-pointer reconstruction — **DEFERRED (rationale below)**
- [x] Phase 3 — measure & document

## Notes
- Preserve: BLS results (P6), IndexScan result set incl. dedup (P4), all public contracts. Single-
  writer + lock-free reads unaffected (these are read-path/algorithm changes).
- A deferral is a valid outcome for either item — the point is a definitive, documented decision.

### P4 — LANDED
`Fallen8.IndexScan` now resolves the index and, when it `is IRangeIndex` AND the operator is an
ordered one (`Greater`/`GreaterOrEquals`/`Lower`/`LowerOrEquals`), routes through the new private
`TryOrderedRangeIndexScan`, which calls the RangeIndex's O(log n + k) `GreaterThan`/`LowerThan`
(engine-performance P4) instead of the generic O(n) `FindElementsIndex` PLINQ scan. `Equals`/
`NotEquals` and every non-range index fall through to the unchanged generic `switch` — the branch is
purely additive.

- **Semantics preserved exactly.** The RangeIndex's sorted methods select the SAME key set the
  generic finder's per-key `CompareTo` predicate keeps (both order keys by `IComparable.CompareTo`;
  the binary-search bracket honours inclusive vs. exclusive identically: `GreaterOrEquals`/
  `LowerOrEquals` include the boundary key, `Greater`/`Lower` exclude it). Those methods concatenate
  the matched buckets WITHOUT deduping, whereas `FindElementsIndex` applies a cross-bucket
  `.Distinct()`, so `TryOrderedRangeIndexScan` reapplies the SAME `.Distinct()`. Result: a graph
  element indexed under several matching keys still appears exactly once — byte-identical output set.
  The `found` bool (`result.Count > 0`) and the empty→non-null-empty-list behaviour are unchanged.
  Order was already nondeterministic on the generic path (`.AsParallel()`), so no caller can depend
  on it; the reroute is asserted as SET equivalence.
- **Tests** (`EnginePerformanceFollowupsTest`, 4 methods): a RangeIndex and a DictionaryIndex are
  populated identically (v0 under keys 10 & 40, v1 under 20 & 50, a multi-value bucket at key 20 —
  forcing the cross-bucket dedup). Parity is asserted for every ordered operator × 13 literals
  (below-all / above-all / gaps / exactly-each-key) against the DictionaryIndex (the untouched
  generic path); hand-computed sets pin the headline cases incl. the dedup (`Greater(5)` = 5 distinct
  vertices, not 7 raw bucket entries); empty/full selectivity; and that the DictionaryIndex and the
  `Equals`/`NotEquals` operators are unaffected.
- **Benchmark** (`EnginePerformanceFollowupsBenchmark.P4_...`, opt-in): rerouted RangeIndex vs the
  generic O(n) path (same data in a DictionaryIndex), n=200,000, varying selectivity k — see
  Measured results.

### P6 — DEFERRED (keep the current bounded reconstruction)
The copy-on-extend → parent-pointer rewrite of `BidirectionalLevelSynchronousSSSP` is **deferred**,
confirming the engine-performance council's high-risk/low-reward flag with a concrete analysis:

- **High risk — the reconstruction has a reversal seam.** `CreatePaths` builds each middle→source
  path walking backward, calls `ReversePath()` (mutating the element list in place and repointing
  `LastPathElement` to the middle-adjacent element), and only then extends middle→target by
  appending. A partial path's `LastPathElement.SourceVertex`/`.TargetVertex` is read mid-flight to
  key the NEXT frontier lookup, and the two halves are materialised in OPPOSITE directions relative
  to a single parent chain. A parent-pointer scheme must reproduce this seam, the exact
  Incoming-then-Outgoing emission order at every level, the per-level `CapPaths` early termination,
  and the final `Take(maxResults)` — byte-identically, since `PathTest`/`PathTestEdgeCases`/
  `WeightedDijkstraPathTest` and `EnginePerformanceTest.Bls_BoundedReconstruction_*` pin the exact
  path set, element order, and first-k order. The risk of a subtle order/result shift is high and
  concentrated exactly where the guardrail forbids it.
- **Low reward — measured.** BLS reconstructs only ~(number of meeting/"middle" vertices) paths, NOT
  one per distinct route: the shared `visitedVertices` set gives every frontier vertex exactly ONE
  predecessor edge (`alreadyVisited.Add(...)` in `GetValid{In,Out}goingEdges`), so the predecessor
  structure is a spanning TREE. The opt-in `P6_BlsReconstruction_CurrentAllocationCost` benchmark
  confirms this: a layered graph with width^depth (256 … 4.3 billion) distinct equal-length routes
  yields exactly **2** reconstructed paths (= width). With so few paths, copy-on-extend cost is
  driven by path LENGTH (~O(L²) per path) and is a small slice of the whole calculate, which is
  dominated by frontier expansion the rewrite does not touch. The rewrite would turn a minor,
  already-bounded slice from ~O(L²) into ~O(L). Not worth the byte-identical-order risk.
- **Public surface unchanged.** `Path`/`PathElement` are untouched; no version bump needed. The
  current bounded reconstruction (engine-performance P6) is retained as-is.

## Measured results (real, captured in this environment)

Opt-in `EnginePerformanceFollowupsBenchmark` methods (`[TestCategory("Benchmark")]` + `[Ignore]`),
run once with `[Ignore]` temporarily removed on this dev box (net10.0, Debug build). Indicative
(Debug, single run) — not a formal BenchmarkDotNet study — but they demonstrate the intended shifts.
Output prefix `[EPFBENCH]`.

- **P4 — ordered IndexScan on a RangeIndex is O(log n + k), not O(n).** Same data in a RangeIndex
  (rerouted) and a DictionaryIndex (generic O(n) `FindElementsIndex`), n=200,000, `Greater` at varying
  selectivity k:
  - k=1 → range 0.0029 ms vs generic 33.9 ms (~11,559×)
  - k=100 → 0.035 ms vs 31.5 ms (~895×)
  - k=10,000 → 3.63 ms vs 17.0 ms (~5×)
  - k=100,000 → 22.0 ms vs 25.8 ms (~1×)

  The rerouted time tracks the result size k (plus the log n search); the generic scan tracks n
  regardless of k. Both return the identical (deduped) set — see parity tests.
- **P6 (deferred) — current copy-on-extend reconstruction cost.** Layered graph, width=2, whole
  `TryCalculateShortestPath` allocation (frontier expansion + reconstruction), 200 reps:
  - depth=8 (length 9), 256 routes → BLS reconstructs **2** paths; 26,152 bytes/call
  - depth=16 (length 17), 65,536 routes → **2** paths; 52,592 bytes/call
  - depth=32 (length 33), ~4.3 billion routes → **2** paths; 113,848 bytes/call

  BLS reconstructs a constant 2 paths (= width = # middle vertices) however many routes exist —
  spanning-tree predecessors. Total allocation grows with graph size (frontier expansion), while the
  copy-on-extend reconstruction of just 2 paths is a small, length-driven slice. This is the "low
  reward" backing the deferral.
