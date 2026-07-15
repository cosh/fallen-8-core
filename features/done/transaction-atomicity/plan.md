# Transaction Atomicity — Plan

Companion to [spec.md](./spec.md). P0 correctness. The guiding invariant: a transaction whose terminal
state is `RolledBack` has ZERO observable effect. Characterization tests that expose the corruption
come first, then the create paths (id-space corruption is the worst blast radius), then remove/property,
then the empty rollbacks, then measure & document.

GitHub issue: to be opened (label: feature).

## Phase 0 — Baseline & guardrails
Intent: pin the violations with tests that FAIL today, and a benchmark that guards the construct-then-commit
cost, before changing engine code.
- [ ] Add `TransactionAtomicityTest` (MSTest, `TestLoggerFactory.Create()`) with characterization tests
  that currently FAIL, one per §1 violation:
  - [ ] `CreateVerticesBatch_WithNullDefinitionMidBatch_LeavesIdSpaceIntact` — build N vertices, enqueue
    a `CreateVerticesTransaction` whose definitions are `[valid, null, valid]`, then create one more
    vertex and assert its `.Id == snap.Count-1` and `TryGetVertex(id)` returns exactly it (fails today:
    `_currentId > Count`).
  - [ ] `CreateEdgesBatch_WithThrowMidWiring_WiresNoAdjacencyAndAppendsNothing` — assert no vertex has a
    dangling out/in edge and no `EdgeCount`/`_currentId` drift after a mid-batch failure.
  - [ ] `AddPropertiesBatch_WithConflictingUpdate_AppliesNothing` — batch `[set A=1 (new), set B=<conflict>]`
    on an element whose `B` already differs; assert `A` was NOT applied and state is unchanged.
  - [ ] `RemoveGraphElementsBatch_WithOutOfRangeIdMidBatch_RemovesNothing` — batch `[validId, hugeId]`;
    assert `validId` is still live afterwards.
  - [ ] Each also asserts terminal state `RolledBack` and that the worker still runs a subsequent
    transaction to completion.
- [ ] Add `TransactionAtomicityBenchmark` (`[TestCategory("Benchmark")]` + `[Ignore]`, `[TXABENCH]`
  console prefix, per repo convention) measuring batch create/remove/property throughput so the added
  pre-validation pass can be shown not to regress. Numbers to be captured on this box.

## Phase 1 — Batch creates: construct-then-commit
Intent: nothing is mutated until every model in the batch is built; a throw before the append leaves the
engine untouched.
- [ ] `Fallen8.CreateVertices_internal` (`Fallen8.cs:587`): validate all definitions first (a `null`/invalid
  entry → clean `false` + `FailureReason = InvalidInput`, no throw); build all `VertexModel`s against a
  LOCAL id counter seeded from `_currentId`; then `AppendGraphElements(list)` and only then
  `_currentId = nextId` / `VertexCount += n`.
- [ ] `CreateVerticesTransaction` (`CreateVerticesTransaction.cs`): remove the swallowing `try/catch`
  (`:50`) so a clean pre-validation `false` carries `InvalidInput` and a genuine OOM propagates to the
  worker as `InternalError`; keep `Rollback` as the safety net for the residual case (it already removes
  `_verticesCreated`).
- [ ] `Fallen8.CreateEdges_internal` (`Fallen8.cs:976`): keep the existing endpoint pre-validation
  (`:988`, `NotFound`); build all `EdgeModel`s against a local id counter WITHOUT touching store/adjacency;
  `AppendGraphElements(newEdges)` FIRST, then set counters, then wire adjacency (`AddOutEdge`/
  `AddIncomingEdge`) — store-then-adjacency, matching the single-edge order (`Fallen8.cs:958`→`964`).
- [ ] `CreateEdgesTransaction` (`CreateEdgesTransaction.cs`): assign the created-edge list as edges are
  appended (not only as the return value) so `Rollback` (`:42`) can remove them if adjacency wiring throws.

## Phase 2 — Batch remove/property: validate-then-apply with tracked undo
Intent: reject the whole batch up front where possible; where a later step can still throw, undo the
applied progress.
- [ ] `RemoveGraphElementsTransaction` (`RemoveGraphElementsTransaction.cs:50`): pre-validate every id is
  in range before removing any (out-of-range → clean `false`; keep in-range-null/already-removed as a
  no-op); track the ids actually transitioned live→removed and implement `Rollback` (`:45`) to restore
  them (clear the removed flag + restore adjacency + counters), mirroring the inverse operations in
  `TryRemoveGraphElement_private`'s restore block (`Fallen8.cs:1167`).
- [ ] Add a non-throwing property-conflict probe on `AGraphElementModel` (a `bool`-returning check that
  reuses the `ValueEquals` semantics at `AGraphElementModel.cs:361`) so a conflicting update is detected
  without the `ArgumentException` throw (`AGraphElementModel.cs:363`).
- [ ] `AddPropertiesTransaction` (`AddPropertiesTransaction.cs:44`): pre-validate the whole batch is
  conflict-free (→ `Conflict`/`InvalidInput` clean `false` if not); record each element's prior
  value/absence as it applies, and implement `Rollback` (`:39`) to restore them if a later set throws.
- [ ] Set the appropriate `FailureReason` on each pre-validation reject; a genuine post-validation fault
  stays `InternalError`.

## Phase 3 — Fill the empty single-transaction rollbacks
Intent: make the invariant uniform across every transaction, not only the batches.
- [ ] `CreateEdgeTransaction.Rollback` (`CreateEdgeTransaction.cs:53`): capture the created edge in
  `TryExecute` and remove it in `Rollback` (via `TryRemoveGraphElement_private`).
- [ ] `AddPropertyTransaction.Rollback` (`AddPropertyTransaction.cs:53`): record the element's prior
  value/absence in `TryExecute` and restore it in `Rollback`.
- [ ] Document the invariant on `ATransaction` (`ATransaction.cs:75`): `TryExecute` either mutates nothing
  and returns `false`/throws, or every mutation is undone by `Rollback`.

## Phase 4 — Verify atomicity end-to-end (incl. WAL crash+replay)
Intent: promote the Phase 0 characterization tests to passing and add the durability guarantees.
- [ ] The Phase 0 tests now PASS (state unchanged; `id == index` holds; correct `FailureReason`; worker
  survives).
- [ ] `WAL_RolledBackBatch_LogsNothing` — with a `WriteAheadLogOptions` engine, assert the failed batch
  appends no WAL entry (following `WriteAheadLogTest.cs`).
- [ ] `WAL_CrashReplayAfterRolledBackBatch_ReproducesPreTxState` — commit a baseline, run a failing batch,
  reload a fresh engine from snapshot + WAL, assert the recovered graph equals the pre-failing-tx state
  (counts, elements, ids, edges/adjacency, properties).
- [ ] Migrate any existing test that assumed partial-commit-on-throw for a remove/property batch to the
  new all-or-nothing contract; keep the out-of-range → 500 mapping (B6 / `transaction-failure-reasons`)
  unless the optional `InvalidInput` reclassification (spec §3) is deliberately taken.

## Measure & document
- [x] Run `TransactionAtomicityBenchmark` on this box and record batch throughput; confirm the
  pre-validation pass does not regress it. Numbers captured on this box (Debug build, 200k vertices,
  batch 10k): create-vertices ~493k/s, create-edges ~909k/s, add-properties ~1.09M/s,
  remove-elements ~1.61M/s. The added O(n) pre-validation pass is negligible next to model
  construction/interning; no observable regression.
- [x] Guardrails held: single-writer + lock-free reads over the volatile snapshot (no new locks),
  the WAL "log on success only" append point unchanged, and the `transaction-failure-reasons`
  channel reused (InvalidInput / NotFound / Conflict; out-of-range remove kept → InternalError/500).

## Outcome (what shipped)
- **Batch creates** (`CreateVertices_internal`, `CreateEdges_internal`) are construct-then-commit:
  validate the whole batch, build all models against a LOCAL id counter, atomic `AppendGraphElements`,
  then advance counters; edges wire adjacency AFTER the append (store-then-adjacency, matching the
  single-edge path). The appended edges are tracked on `CreateEdgesTransaction` so a residual wiring
  throw is rolled back. `CreateVerticesTransaction`'s swallowing `try/catch` was removed.
- **Batch remove/property** (`RemoveGraphElements_internal`, `SetProperties_internal`) are
  validate-then-apply with tracked undo: range/structure/conflict checks up front (incl. intra-batch
  conflicts), then apply while recording inverses; `Rollback` restores exactly the applied progress
  (`RestoreRemovedElements_private` inverts a completed removal cascade including self-loops without
  duplication; `RestoreProperties_internal` replays inverses in reverse).
- **Single-transaction rollbacks** filled (`CreateEdgeTransaction`, `AddPropertyTransaction`) and the
  atomicity invariant documented on `ATransaction`.
- **Note (out of scope, surfaced here):** bulk delete by absolute id interacts with auto-trim id
  renumbering — the `trim-reader-safety` feature owns that; the benchmark's end-state check is
  relaxed accordingly.

## Progress
- [x] Phase 0 — characterization tests (failing before the fix) + guarded benchmark
- [x] Phase 1 — batch creates construct-then-commit (vertices + edges; store-then-adjacency)
- [x] Phase 2 — batch remove/property validate-then-apply with tracked undo
- [x] Phase 3 — fill single-transaction rollbacks + document the invariant on `ATransaction`
- [x] Phase 4 — atomicity + WAL crash/replay tests green; no old partial-commit tests needed migration
- [x] Measure & document

## Decision / revisit condition
- **`transaction-failure-reasons/` (landed):** this feature reuses its `TransactionFailureReason` channel
  and its edge-endpoint pre-validation; it does NOT change the out-of-range remove/property → 500 mapping
  that feature deliberately kept. Reopen the status-code question only as the explicitly-optional
  `InvalidInput` reclassification in spec §3, and only if it can be done without regressing the
  load-bearing B6/500 tests.
- **`non-blocking-save/` (deferred, measured):** unaffected — this feature keeps all mutation on the
  single writer thread and adds no off-worker work. No revisit.
- **`persistence-hardening/` Stage D WAL + `wal-failure-hardening/`:** this feature is a prerequisite for
  their correctness (a rolled-back tx must leave nothing for the log to miss). No change to the WAL
  format or append point here; revisit only if the WAL later logs uncommitted effects (it must not).
