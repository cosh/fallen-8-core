# Transaction Retention & Completion Contract — Plan

Companion to [spec.md](./spec.md). Correctness/contract fixes first (cheap, no format/model change),
then the structural retention bound, then the created-model change, then measure. Every behaviour
change is pinned by a test.

GitHub issue: to be opened (label: `feature`). Feature branch: `feature/transaction-retention`.

## Phase 0 — Baseline & guardrails

Prove the leak and the throw, and lay down the guards the fixes must keep green.

- [ ] Add `TransactionRetentionBenchmark` (`fallen-8-unittest/`), `[TestClass]` with a
  `[TestMethod] [TestCategory("Benchmark")] [Ignore(...)]` method (mirror `MemoryFootprintBenchmark`:
  env-scaled workload, `GC.GetTotalMemory(true)` retained-heap reading, results printed + appended to
  a temp file). Drive an **insert-only, no-removal** workload of N transactions and record
  `transactionState` growth / retained bytes vs N. Baseline "before" numbers: to be captured on this
  box.
- [ ] Add a characterization test in `TransactionTest.cs` (normal suite) that pins **today's** buggy
  behaviour so the fix visibly flips it: `GetCreatedVertices()` after a `Trim` throws
  `ArgumentNullException` today (assert the throw, then Phase 2/3 changes it to "returns a list").
  Use `TestLoggerFactory.Create()`.
- [ ] Add a characterization test that a WAL-append failure currently sets `Error` on a `Finished`
  transaction (the R3 overload), so Phase 1 can flip it to `DurabilityDegraded`.

## Phase 1 — Completion-contract fixes (cheap, correctness)

Intent: recover the single meanings of `Error` and the created-model handles, and shave the hot path,
with no retention/model change yet.

- [ ] **R2 (cheap):** make `GetCreatedVertices()` (`CreateVerticesTransaction.cs:80`) and
  `GetCreatedEdges()` (`CreateEdgesTransaction.cs:92`) null-safe — return `ImmutableList<…>.Empty`
  when the backing list is `null`, else `CreateRange(...)`.
- [ ] **R3:** add `TransactionInformation.DurabilityDegraded` (bool, default `false`) with an XML
  `<summary>` per spec §3.3; change `LogCommittedTransactionSafely` (`TransactionManager.cs:220-237`)
  to set `DurabilityDegraded = true` instead of `Error = logEx` (keep the loud `LogError`). Restore
  `Error`'s doc meaning ("execution faulted") — no doc edit needed beyond confirming it now holds.
- [ ] **F14 logging:** demote the three per-tx `LogInformation` calls
  (`TransactionManager.cs:135,178,285`) to `LogDebug` or wrap in `IsEnabled(LogLevel.Information)`.
  Leave the error/warning failure logs untouched.
- [ ] **F14 id:** hold the transaction id as a `Guid` on `ATransaction` (`ATransaction.cs:34`) and
  stringify lazily; log with the `Guid`; resolve `GetTransactionState(String)` via `Guid.TryParse`
  (unparseable/unknown → `NotExist`). Pick the least-disruptive public shape for `TransactionId`
  (property computed on access vs. type change + version bump) and pin the chosen behaviour.

## Phase 2 — Bounded terminal-entry retention (structural, R1)

Intent: schedule reclamation so no-removal workloads stay bounded, entirely on the single writer.

- [ ] Add `MaxRetainedTerminalTransactions` (configurable; sane default in the tens of thousands) and
  an in-memory terminal-id FIFO to `TransactionManager`.
- [ ] In `ProcessTransaction`, when a transaction reaches `Finished` or `RolledBack`, push its id onto
  the FIFO; while `FIFO.Count > MaxRetainedTerminalTransactions`, pop the oldest and `TryRemove` it
  from `transactionState`. O(1) amortised, worker-thread-only (no lock).
- [ ] Reconcile with `Trim()` (`TransactionManager.cs:321-347`): after a full `Trim` clear the FIFO
  (its ids are gone). Leave the WAL/load `Trim` call sites (`Fallen8.cs:1640,1962`) and `Trim_internal`
  unchanged in behaviour.
- [ ] Confirm the eviction path runs the same `Cleanup()`-equivalent teardown as `Trim` for the
  evicted entry (until Phase 3 changes what `Cleanup` does).
- [ ] Test (`TransactionTest.cs`): insert-only workload past the bound with **no** removals →
  `transactionState` entry count stays ≤ bound and does not grow with N; a recent id still resolves
  via `GetTransactionState`; a long-superseded id → `NotExist`; a held `TransactionInformation` still
  reads its terminal state/`Error`/`FailureReason`/`DurabilityDegraded`.

## Phase 3 — Created-models survive an unrelated trim (structural, R2)

Intent: a waited-on caller reads the actual created models, immune to a foreign auto-trim.

- [ ] Stop nulling the created-model lists in `Cleanup()`
  (`CreateVerticesTransaction.cs:94`, `CreateEdgesTransaction.cs:106`) — `Cleanup` may still drop the
  input definition (already released at commit by `ReleaseAfterCompletion`). Rely on Phase 2's entry
  eviction + GC to reclaim the whole `ATransaction`.
- [ ] Update the `ATransaction.Cleanup` XML contract and the `memory-footprint/spec.md:122-125`
  "reads empty after Trim" note to the new contract (a prompt read returns the actual models; the
  null-safe accessors still guarantee no throw).
- [ ] Test: create → `WaitUntilFinished()` → fire an **unrelated** removal that triggers auto-trim →
  `GetCreatedVertices()`/`GetCreatedEdges()` returns the created models (or empty), never throws.
  Interleave to exercise the race.

## Measure & document

- [ ] Re-run `TransactionRetentionBenchmark` "after"; record retained-bytes / entry-count vs the
  Phase 0 baseline (numbers: to be captured on this box). Confirm the F14 hot-path reduction.
- [ ] Update `features/transaction-retention/spec.md` status and the cross-links
  (`engine-performance` P9, `memory-footprint` M3/M4, `transaction-failure-reasons`,
  `crash-durability-hardening`).
- [ ] `dotnet build` clean; `dotnet test` green (full suite). Mark the PR ready for review.

## Progress

- [x] Phase 0 — opt-in `TransactionRetentionBenchmark` (insert-only workload, `GC.GetTotalAllocatedBytes`
  + retained entry count vs N, prefix `[TXBENCH]`) + `TransactionRetentionTest`. Note: the tests assert
  the FIXED behaviour directly (the fixes landed in the same pass) rather than first pinning the buggy
  state; the `Error`-overload was **already** resolved before this branch (the WAL path records
  degradation via the `Durable` flag, not `Error`).
- [x] Phase 1 — null-safe `GetCreatedVertices()`/`GetCreatedEdges()`; `DurabilityDegraded => !Durable`
  convenience added (the `Error` overload was already gone via `Durable`); F14: the two per-tx info
  logs demoted to `Debug` and `IsEnabled`-guarded, `SetTransactionState` already `Debug`.
- [x] Phase 2 — bounded terminal-entry retention: a writer-thread-only `Queue<Guid>` FIFO in
  `TransactionManager`, evicting past `MaxRetainedTerminalTransactions` (default 100 000) in
  `SetTransactionState`; `Trim` clears the FIFO. Dictionary rekeyed by `Guid` (F14). Tests cover the
  bound, recent-resolves/old-NotExist, and id round-trip.
- [x] Phase 3 — `Cleanup()` on both create transactions stops nulling the created-model lists (drops
  only the input); race test: create → foreign `TrimTransaction` → `GetCreated*` returns the actual
  models, never throws; plus a null-guard test.
- [x] Measure & document — full suite green (422 passing); absolute before/after numbers left to the
  opt-in benchmark on the target box. `memory-footprint` M3's "reads empty after Trim" is now the
  strict improvement "reads the actual models" (null-safe accessors still guarantee no throw).

## Decision / revisit condition

- **Relation to `engine-performance` P9 (not a deferral — landed):** P9 widened *what* `Trim`
  reclaims (`Finished` + `RolledBack`); this feature adds the missing *scheduling* so no-removal
  workloads reclaim without an explicit `/trim`. It does not narrow P9.
- **Relation to `memory-footprint` M3 (documented contract change):** M3 documents the created-model
  handles as "reads empty after Trim". Phase 3 changes a *prompt* read to return the actual models
  (strict improvement); the null-safe accessors preserve "never throws". If the structural change is
  judged out of scope at implementation time, ship Phase 1's null-safe accessors alone — that already
  honours the original "reads empty" contract and removes the throw. What would reopen Phase 3: a
  caller relying on the old "reads empty" behaviour surfacing (none known).
- **Retention bound default:** the count bound is a policy knob. Revisit (raise the default, or add
  the TTL variant from spec §3.1) if a real deployment needs longer poll-by-id windows or a
  time-based eviction. A workload doing tens of millions of *removals*-heavy transactions is already
  covered by auto-trim and is out of scope here.
