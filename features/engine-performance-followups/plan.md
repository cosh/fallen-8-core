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
- [ ] Phase 1 — P4 ordered IndexScan reroute (or defer)
- [ ] Phase 2 — P6 parent-pointer reconstruction (or defer)
- [ ] Phase 3 — measure & document

## Notes
- Preserve: BLS results (P6), IndexScan result set incl. dedup (P4), all public contracts. Single-
  writer + lock-free reads unaffected (these are read-path/algorithm changes).
- A deferral is a valid outcome for either item — the point is a definitive, documented decision.
