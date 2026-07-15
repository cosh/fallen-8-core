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
> **Superseded by persistence-hardening Stage B (C9):** `RTree.Save`/`Load` are now fully
> implemented — the R-Tree is serialized (build config + entries) and reloads as a functional,
> queryable index, and the `NotSupportedException` "not persistable" signal is replaced by an
> explicit `IIndex.CanPersist` flag. The B7 design below (skip-and-recreate) was the interim state;
> the per-index guards for a genuinely failing index still stand. See
> [../persistence-hardening/](../persistence-hardening/).

- `RTree.Save`/`Load`: throw a clear `NotSupportedException` ("the spatial (R-Tree) index is not
  yet persistable; recreate it after load"). The R-Tree is **not** persisted — coming up as an
  "empty" index is not viable because `Load` lacks the config to build a valid tree (a container
  map alone leaves `_root`/`Metric`/`Space` null and NPEs on the first query/add).
- `PersistencyFactory.SaveIndex`: try/catch — on failure log, **delete the partial sidecar** that
  `File.Create` already made, and return `null`; the `Save` index manifest drops `null` entries so
  the spatial (and any throwing) index is skipped, not fatal, and not referenced by the manifest.
- `PersistencyFactory.LoadIndices`: try/catch around each `LoadAnIndex` — log and skip a failing
  index rather than aborting the load. `IndexFactory.OpenIndex` calls `Load` before registering,
  so a throwing `Load` (e.g. an older save point that still lists the spatial index) leaves the
  index **unregistered** (absent) rather than half-initialised.
- Test: a graph with an R-Tree index + a non-spatial index saves and loads without throwing;
  the checkpoint succeeds and graph elements + the non-spatial index round-trip; the spatial index
  is absent after load and can be recreated (add/query then work). A separate test registers a
  deliberately-throwing stub index alongside a good one and asserts the checkpoint still finishes,
  the good index round-trips, and the throwing one is skipped with no orphaned sidecar.

## Phase 3 — edge-removal rollback regression test
- Test-only. Drive `Fallen8.TryRemoveGraphElement_private`'s edge (`else`) branch to fault after
  the target-side detach, then assert rollback restored the edge and both endpoints' adjacency.
- Note (not a fix): the `outEdgeRemovals` replay branch is unreachable with today's removal
  structure — the reachable rollback replays `inEdgeRemovals` and leaves the source untouched.

## Status
- [x] Phase 1 — surface rolled-back transactions (B6 follow-up)
- [x] Phase 2 — spatial index must not crash the checkpoint (B7 follow-up)
- [x] Phase 3 — edge-removal rollback regression test
