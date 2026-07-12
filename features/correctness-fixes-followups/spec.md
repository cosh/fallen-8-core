# Correctness Fixes — Follow-ups — Specification

> **Status:** Planned. Three deferred follow-ups from the merged `correctness-fixes` work (see
> [../correctness-fixes/plan.md](../correctness-fixes/plan.md) "Follow-ups"). Localized fixes,
> each with a regression test, independent of the larger structural themes.

## 1. Problem

The `correctness-fixes` work fixed the *engine* symptoms of two defects but left the *observable*
behaviour untouched, and one rollback branch remained untested.

| # | Defect | Effect |
|---|--------|--------|
| B6-followup | The worker now rolls a faulting transaction back and marks it `RolledBack`, but the mutation controllers return `202 Accepted` regardless of outcome | A rolled-back write is reported to the client as success |
| B7-followup | `RTree.Save`/`Load` throw `NotImplementedException` and `PersistencyFactory.SaveIndex` has no guard (the caller dereferences the faulted task's `.Result`) | A single spatial index present aborts the **entire** checkpoint — all graph elements and every other index are lost |
| edge-rollback | The `else` branch of `Fallen8.TryRemoveGraphElement_private` (edge removal, replaying `inEdgeRemovals`/`outEdgeRemovals` on rollback) has no test | A regression there would go unnoticed |

## 2. Decisions

- **Error status for a rolled-back mutation:** use `500 Internal Server Error`, consistent with
  the existing `SubGraphController.CreateSubGraph` catch-all. A rolled-back transaction is an
  internal failure the client did not ask to distinguish; `500` matches the house style.
- **`TransactionInformation` visibility:** confirmed *no gap*. `TransactionState` is a public
  property that the manager mutates on the very instance the controller holds (`AddOrUpdate`
  updates the existing entry), and `Task.Wait()` inside `WaitUntilFinished()` establishes the
  happens-before that makes the terminal state visible. No change needed there.
- **Spatial index persistence:** minimal only, and the R-Tree is *not* persisted. `RTree.Save`/
  `Load` throw a clear `NotSupportedException` ("the spatial (R-Tree) index is not yet
  persistable; recreate it after load"), which the per-index guards in `PersistencyFactory` catch:
  `SaveIndex` logs it and drops the index from the checkpoint manifest, and `LoadIndices` skips it
  (`OpenIndex` calls `Load` *before* registering, so a throwing `Load` means the index is never
  registered). Net effect: after a load a spatial index is simply **absent and must be recreated**
  — never present-but-half-initialised. (A reloaded R-Tree with only its container map set has a
  null `_root`/`Metric`/`Space` and would NPE on the first spatial query or add; that landmine is
  what this avoids.) Full R-Tree serialization stays deferred to the `persistence-hardening` theme.
  The same per-index guards make any single throwing index skipped, not fatal (defense in depth for
  any future index), and `SaveIndex` now deletes the partial sidecar it had already created before
  the failure.

## 3. Acceptance criteria

- A `waitForCompletion=true` mutation that rolls back returns `500`, not `202`; a normal mutation
  still returns success. The fire-and-forget (`waitForCompletion=false`) path is unchanged.
- A graph containing a spatial (R-Tree) index saves and loads without throwing; the checkpoint
  succeeds and graph elements and non-spatial indices round-trip; the spatial index is absent after
  load (not persisted) and can be recreated, after which a spatial add/query works (documented in
  the test). A separate deliberately-throwing index is likewise skipped, not fatal, and leaves no
  orphaned sidecar.
- The edge-removal rollback path has a regression test asserting the edge and its endpoint
  adjacency are restored.
- Full suite stays green.

## 4. Notes

The `outEdgeRemovals` replay in the edge-removal restore block is effectively unreachable with the
current removal structure (nothing throws after both removal lists are populated); this is noted,
not "fixed" — widening the fault window is out of scope. See plan.md.

## 5. Known limitations / follow-ups

- **`SubGraphController.CreateSubGraph` fault-vs-clean split.** `TransactionManager` maps *both* a
  clean `TryExecute() == false` and a thrown exception to `TransactionState.RolledBack`, so the
  state alone cannot distinguish them. `TransactionInformation.Error` now exposes the caught
  exception (null on a clean rollback), and `CreateSubGraph` uses it: `Error != null` → `500`
  (genuine internal fault), otherwise a null result (empty match / structurally-invalid pattern /
  post-materialization quota) → `400`. This corrected an earlier round that returned `500` for all
  rolled-back creates, mis-classifying valid-but-empty and invalid-pattern requests. `DeleteSubGraph`
  keeps `RolledBack → 500` (its only failure paths are internal).

- **Client-caused rollbacks on `GraphController` mutations still surface as `500`.** e.g. `AddEdge`
  referencing a non-existent vertex faults inside the worker, so the new faulted flag (`Error`) is
  *not* a reliable client-vs-internal proxy there — a client error legitimately manifests as a
  thrown exception. We therefore did **not** rewire `GraphController` here. The proper fix is
  structured, per-transaction failure reasons/categories (a transaction declaring "invalid input"
  vs "internal fault") so client-caused rollbacks can return the right `4xx`. Until then the
  `GraphController` mutations keep their approved `RolledBack → 500` behaviour.

- **`GraphController` mutation success returns `202 Accepted` but docs/attributes still declare
  `204`.** Pre-existing mismatch between the `Accepted()` result and the `[ProducesResponseType(204)]`
  / `<response code="204">` annotations on `AddVertex`/`AddEdge`/`AddProperty`/`TryRemoveProperty`/
  `TryRemoveGraphElement`. Not touched here; align the attributes and XML docs with the actual `202`
  in a follow-up.

- **No-match semantics inconsistency (subgraph create).** An *empty graph* with a pattern defined
  returns `400` (the vertex-copy stage copies zero vertices → the algorithm short-circuits to a clean
  `false`), whereas a *populated graph whose pattern matches nothing* returns `201` with an empty
  subgraph (all vertices are copied, so the short-circuit is skipped, then the pattern filters them
  all out and the algorithm returns `true`). Both are pinned by tests
  (`Create_OnEmptyGraph_Returns400`, `Create_WhenPatternMatchesNothingOnPopulatedGraph_Returns201`).
  Decide and document a single consistent contract — either both `201` with an empty subgraph, or
  both `400` — and align the algorithm's early-return.

- **Quota status inconsistency (subgraph create).** The subgraph **count** ceiling is rejected
  up-front by the controller and returns `409 Conflict`, while the per-subgraph and total **element**
  ceilings are enforced post-materialization inside the factory as a clean `false`, so they surface
  as `400` (clean rollback). Unify quota breaches under the structured-failure-reasons follow-up (see
  the `GraphController` bullet above) so all quota rejections share one status/shape.

- **`TransactionInformation.Error` visibility for pollers.** The worker sets
  `TransactionState = RolledBack` and `.Error` as two separate steps, so only a caller that
  `WaitUntilFinished()` (which establishes the happens-before via `Task.Wait()`) is guaranteed to
  observe `Error`; a caller that merely polls `GetState()` may see `RolledBack` before `.Error` is
  set. Either assign `.Error` before publishing the terminal `RolledBack` state, or document the
  wait-first precondition more prominently than the current XML remark on `TransactionInformation.Error`.
