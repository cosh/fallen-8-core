# Correctness Fixes — Plan

Companion to [spec.md](./spec.md). These are independent; do them in roughly this order
(cheapest / highest-blast-radius first). Every item = failing test → fix → green.

## Phase 1 — Index data loss (B1, B2, B3)
- Test: `DictionaryIndex`/`RegExIndex` keep multiple elements per key and honor removals.
- Fix: assign the `ImmutableList` return (`_idx[key] = values.Add(...)`, etc.).
- Test: `RangeIndex.Between(lower, upper)` returns in-range elements; fix the inverted predicate.
- Fix: `RangeIndex` had the same discarded-immutable-return data loss as B1/B2 —
  `AddOrUpdate` now writes back (`_idx[key] = values.Add(...)`) so it keeps every element per
  key (not just the first), and `RemoveValue` now reassigns `_idx[key] = _idx[key].Remove(...)`
  over a `_idx.Keys.ToList()` snapshot (removal was previously a no-op), dropping emptied keys
  after the loop — mirrors the fixed `DictionaryIndex`. Covered by two new regression tests in
  `CorrectnessFixesTest.cs`.
- (Consider `List<T>`/`HashSet<T>` buckets while here; the deeper container change is tracked
  in `engine-performance` / `core-storage-representation`.)

## Phase 2 — Core removal & null-safety (B4, B5)
- Test: a `CreateVerticesTransaction`/removal rollback restores counts and adjacency.
- Fix: use `InEdges` in the in-edge restore branch.
  - Note: the `_graphElements.Insert(...)` reassignment was intentionally NOT applied.
    Removal is a soft-delete (elements are flagged via `MarkAsRemoved` and stay in the
    list), so re-inserting duplicates the element and breaks the id==index invariant, and
    does not clear the removed flag. The correct rollback is `MarkAsNotRemoved`, which is
    what restores presence/counts.
  - Fix (follow-up applied): the in-edge restore now re-files the edge via `AddOutEdge`, not
    `AddIncomingEdge`. Removal detaches an in-edge from its source through `RemoveOutGoingEdge`,
    so the inverse is `AddOutEdge`; the old code mis-filed the edge into the source's incoming
    edges. The B4 test now asserts the restored in-edge is back in the source vertex's
    `OutEdges` (and not its `InEdges`).
  - Hardening: the restore block is wrapped so an exception thrown inside the restore loop no
    longer skips the counter recompute (`RecalculateGraphElementCounter` runs in a `finally`)
    nor masks the original removal failure (the restore exception is logged and swallowed; the
    original is rethrown).
- Test: `GetPropertyCount` on a property-less element returns 0; add the null guard.

## Phase 3 — Transaction worker resilience (B6)
- Test: enqueue a transaction whose `TryExecute` throws, then a normal one; assert the second
  completes (worker survived).
- Fix: wrap `TryExecute` in try/catch in `ProcessTransaction`; mark failed/rolled-back and continue.

## Phase 4 — Path cost/weight surface (B8)
- Decide option (a) implement or (b) remove (recommended (b)). Implement the decision; adjust
  `PathSpecification`/`ShortestPathDefinition` and tests accordingly.

## Phase 5 — Spatial index persistence (B7)
- Minimum: stop silently losing the R-Tree on checkpoint — either implement `Save`/`Load`
  (serialize points + element ids, rebuild on load like other indices) or rebuild-on-load with
  a logged warning. May be delegated to `persistence-hardening`.

## Follow-ups (tracked elsewhere)
- B6 transaction-observability: `WaitUntilFinished` does not surface a `RolledBack` outcome to
  the caller (the worker survives — Phase 3 — but the faulted result is not observable). Left as
  is; tracked separately.
- B7 spatial-index persistence / checkpoint: `SaveIndex` still silently loses the R-Tree on
  checkpoint (no `Save`/`Load` or rebuild-on-load guard). Deferred to Phase 5 /
  `persistence-hardening`.

## Status
- [x] Phase 1 — index data loss (B1, B2, B3)
- [x] Phase 2 — removal & null-safety (B4, B5)
- [x] Phase 3 — worker resilience (B6)
- [ ] Phase 4 — path cost/weight surface
- [ ] Phase 5 — spatial index persistence
