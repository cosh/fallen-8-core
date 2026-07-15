# Structured Transaction Failure Reasons — Plan

Companion to [spec.md](./spec.md). Correctness/behaviour-preservation first; every mapping pinned by
a test.

## Phase 1 — The reason channel
- Add `TransactionFailureReason` enum (`None`, `InternalError`, `InvalidInput`, `NotFound`,
  `QuotaExceeded`, `Conflict`) and expose it on `TransactionInformation` (settable in place like
  `Error`/`TransactionState`, published under the same happens-before). Default `None`; an escaped
  exception implies `InternalError` (Error != null).
- Add a way for a transaction/engine op to record its reason on the shared `TransactionInformation`
  when it rolls back cleanly (mirror how `Error` is threaded in `TransactionManager.ProcessTransaction`).

## Phase 2 — Engine mutations set reasons (no throw for client causes)
- `CreateEdge(s)`: BEFORE wiring, check both referenced vertices exist and are not removed; if not,
  roll back cleanly with `NotFound` (do NOT let the master-store indexer throw). Preserve the
  successful path exactly.
- `CreateSubGraph`: `QuotaExceeded` on the post-materialization per-subgraph/total element breach;
  `InvalidInput` on a structurally-invalid pattern; keep the up-front count-ceiling path but route it
  through the SAME `QuotaExceeded` status. `RemoveSubGraph`/other removals: `NotFound`/`InvalidInput`
  as appropriate; a post-existence-check internal failure stays `InternalError`.
- Genuine unexpected exceptions remain `Error`/`InternalError` (unchanged B6 behaviour).

## Phase 3 — Controller mapping
- `GraphController.RolledBackResult` and the `SubGraphController` create/delete paths map the reason:
  `InvalidInput→400`, `NotFound→404`, `QuotaExceeded→409`, `Conflict→409`, else (`InternalError`/
  Error/None) `→500`, each with a clear message. Keep the response body shape.
- **202/204:** switch the five GraphController mutations' `[ProducesResponseType(204)]` + `<response>`
  to `202` (matches `Accepted()`); update remarks.
- **Subgraph no-match:** implement the chosen consistent contract (prefer valid-empty-match → 201
  empty subgraph in both empty-graph and populated cases; fallback both → 400) and document it.

## Phase 4 — Tests
- New tests pinning each mapping: edge→missing vertex → 4xx (not 500); each quota breach → 409;
  invalid pattern → 400; genuine fault → 500; duplicate name → 409; empty-graph vs populated-no-match
  identical.
- MIGRATE the existing tests that asserted the old codes (subgraph quota was 400 → now 409; any
  AddEdge/missing-vertex expectation; the empty-graph/no-match tests) to the new contract — preserve
  the coverage intent, update the expected status.

## Status
- [x] Phase 1 — reason channel on TransactionInformation (`TransactionFailureReason` enum;
  `TransactionInformation.FailureReason` + `ATransaction.FailureReason`; recorded in
  `TransactionManager.ProcessTransaction` under the same happens-before as `Error`/state — exception
  path ⇒ `InternalError`, clean false ⇒ the tx's recorded reason, default `None`).
- [x] Phase 2 — engine mutations set reasons. `CreateEdge(s)` resolve endpoints via a new
  `Fallen8.TryResolveLiveVertexForEdge` (null for out-of-range/empty/non-vertex/removed) instead of
  the throwing `GetGraphElementForMutation`, so a missing/removed endpoint ⇒ clean `NotFound`
  (batch is all-or-nothing); the removal/property paths keep the throwing resolver (B6 unaffected).
  `CreateSubGraph` ⇒ `QuotaExceeded` (count + per-subgraph + total element ceilings, unified),
  `InvalidInput` (structurally-invalid pattern / bad spec), `Conflict` (name race), `InternalError`
  (fault). `RemoveSubGraph` ⇒ `NotFound`/`InvalidInput`/`InternalError`.
- [x] Phase 3 — controller mapping (`InvalidInput→400`, `NotFound→404`, `QuotaExceeded`/`Conflict→409`,
  else `→500`) in `GraphController.RolledBackResult(reason)` and `SubGraphController`
  create/delete; five GraphController mutations' docs `204→202`; no-match consistency implemented
  (see spec decision below).
- [x] Phase 4 — tests: new `TransactionFailureReasonTest` (engine reason channel + edge/subgraph
  reasons + controller mappings + fire-and-forget); migrated the old-code tests to the new contract.

## Notes
- Preserve single-writer, `WaitUntilFinished`, B6 Error/state observability, memory-footprint M3
  input release, and the WAL append hook. The reason is set BEFORE the task completes so a waited-on
  caller sees it (same as `Error`).
- Do not regress the fire-and-forget (`waitForCompletion=false`) path — it still returns `202`
  immediately regardless of eventual outcome.
