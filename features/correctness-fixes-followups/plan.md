# Correctness Fixes — Follow-ups — Plan

Companion to [spec.md](./spec.md). Three independent items; each is failing test → fix → green
where the behaviour is observable.

## Phase 1 — B6: surface rolled-back transactions
- Confirm `TransactionInformation.TransactionState` is readable after `WaitUntilFinished()`
  returns. It is (public property, mutated in place by the manager, made visible by `Task.Wait()`)
  — no change to `TransactionInformation`.
- `GraphController` (`AddVertex`, `AddEdge`, `AddProperty`, `TryRemoveProperty`,
  `TryRemoveGraphElement`): when `waitForCompletion` is true and the transaction ended
  `RolledBack`, return `500` instead of `Accepted()`. Leave `waitForCompletion=false` unchanged.
- `AdminController.Save`/`Load` are the "similar" methods (they always wait then report success):
  return `500` on `RolledBack`; `Save` becomes `IActionResult` (`Ok(path)` on success), `Load`
  becomes `IActionResult` (`NoContent()` on success).
- Test: a `waitForCompletion=true` mutation that rolls back returns `500`; a normal mutation
  returns success; the fire-and-forget path still returns `Accepted()`.

## Phase 2 — B7: a spatial index must not crash the checkpoint
- `RTree.Save`: graceful no-op that logs a warning (do **not** throw).
- `RTree.Load`: no-op that logs the warning and comes up as a valid **empty** index (initialize
  the container map so it is queryable/countable), do **not** throw.
- `PersistencyFactory.SaveIndex`: try/catch — on failure log and return `null`; the `Save` index
  manifest drops `null` entries so one bad index is skipped, not fatal.
- `PersistencyFactory.LoadIndices`: try/catch around each `LoadAnIndex` — log and skip a failing
  index rather than aborting the load.
- Test: a graph with an R-Tree index + a non-spatial index saves and loads without throwing;
  graph elements and the non-spatial index round-trip; the spatial index is empty after load.

## Phase 3 — edge-removal rollback regression test
- Test-only. Drive `Fallen8.TryRemoveGraphElement_private`'s edge (`else`) branch to fault after
  the target-side detach, then assert rollback restored the edge and both endpoints' adjacency.
- Note (not a fix): the `outEdgeRemovals` replay branch is unreachable with today's removal
  structure — the reachable rollback replays `inEdgeRemovals` and leaves the source untouched.

## Status
- [x] Phase 1 — surface rolled-back transactions (B6 follow-up)
- [x] Phase 2 — spatial index must not crash the checkpoint (B7 follow-up)
- [x] Phase 3 — edge-removal rollback regression test
