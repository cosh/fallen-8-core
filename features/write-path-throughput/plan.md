# Write-Path Throughput — Plan

Companion to [spec.md](./spec.md). Prove the two bottlenecks first, then land them in dependency
order: persistent stream → group commit → awaitable completion → pooled encoding. Preserve the
`persistence-hardening` WAL contract and the single-writer model throughout.

GitHub issue: to be opened (label: feature). Cross-links: `persistence-hardening` (WAL Stage D — the
thing being amortised), `non-blocking-save` (deferred — NOT reopened), `crash-durability-hardening`
(co-owns the group-flush failure contract), `transaction-failure-reasons` (controller mapping kept).

## Phase 0 — Baseline & guardrails
Intent: measure the problem and pin the invariants the fix must not break, before touching code.
- [ ] Add `WalWritePathBenchmark` (`fallen-8-unittest`), mirroring `NonBlockingSaveBenchmark`:
      `[TestClass]`, `[TestCategory("Benchmark")]` + `[Ignore]`, `TestLoggerFactory.Create()`, output
      prefixed e.g. `[WPTBENCH]`. Build a WAL-enabled `Fallen8` (`WriteAheadLogOptions(tempPath)`) and
      report committed tx/s for (a) a single serial producer of single-element writes and (b) N
      concurrent producers of single-element writes. Capture the pre-change numbers *to be captured on
      this box*.
- [ ] Add a durable-before-ack characterization test (WAL on, default suite, NOT ignored): commit a
      write, `WaitUntilFinished()`, dispose the engine WITHOUT a Save, reopen against the same WAL
      path, assert the element is present after replay. This guards Phase 2's completion-after-fsync
      ordering.
- [ ] Add a request-thread characterization test that pins today's behaviour so Phase 3 can flip it:
      assert the mutation controllers currently block the calling thread inside `WaitUntilFinished()`
      (documents the starvation the fix removes).

## Phase 1 — Persistent append stream in `WriteAheadLog`
Intent: remove the per-commit open/close; keep per-commit fsync for now (correctness unchanged).
- [ ] In `Persistency/WriteAheadLog.cs`, replace the per-`Append` `using (new FileStream(FileMode.Append …))`
      (`:219`) with a single lazily-opened append `FileStream` field held for the log's lifetime; the
      per-commit `Flush(true)` stays for now.
- [ ] Close-before-rename / reopen-after in `ResetToSnapshot` (`:237`) so the persistent handle never
      races the temp+rename header rewrite; dispose the stream in `Dispose` (`:325`).
- [ ] Confirm callers unchanged: `Fallen8.LogCommittedTransaction` (`Fallen8.cs:1331`),
      `LogWriteAheadLogMarker`, and the `ResetToSnapshot` call sites (`Fallen8.cs:860`, `:1637`) still
      compile and behave identically; `ReadEntries` still opens its own read stream.
- [ ] Full suite + `WriteAheadLogTest` green (torn-tail, pairing, replay unaffected).

## Phase 2 — Group commit in `TransactionManager`
Intent: amortise one fsync across a drained batch, completing tasks only after the group fsync.
- [ ] Switch `ConsumeLoop` (`TransactionManager.cs:92`) from `GetConsumingEnumerable()` +
      `RunSynchronously()` to: `Take()` the first ready tx, then drain with `TryTake(out _, 0)` into an
      ordered batch; execute each body in commit order via the existing `ProcessTransaction` logic.
- [ ] Replace `new Task(ProcessTransaction, …)` + `RunSynchronously()` with explicit body execution +
      a `TaskCompletionSource` per transaction (held on `TransactionInformation`); the writer completes
      each TCS only in step 4.
- [ ] Add a buffered-append path to `WriteAheadLog` (write frame to the persistent stream WITHOUT
      fsync) + a single `Flush(true)` per batch; route `Fallen8.LogCommittedTransaction` /
      `LogWriteAheadLogMarker` through the buffered path so a batch fsyncs once. Preserve commit-order
      buffering of frames and any in-batch auto-`Trim` marker.
- [ ] Make `SaveTransaction`/`LoadTransaction` a hard batch boundary: flush + complete the pending
      group before processing them; the Save/Load commits as a group of one (so `ResetToSnapshot`
      truncation stays correct).
- [ ] Group-flush failure handling (co-owned with `crash-durability-hardening`): if the batch
      `Flush(true)` throws, record degraded durability on EVERY member's `TransactionInformation`
      (as `LogCommittedTransactionSafely` does today for one tx), keep them `Finished`, and keep the
      worker alive; only then complete their TCSs.
- [ ] Verify the durable-before-ack test still passes for both a lone commit and a drained-group
      member; verify auto-trim-inside-a-batch ordering with a removal-heavy test.

## Phase 3 — Awaitable completion + bounded wait
Intent: stop pinning request threads; add a timeout overload; keep every status code identical.
- [ ] `TransactionInformation` (`:31`): add `public Task Completion => _txTask ?? Task.CompletedTask;`
      (backed by the Phase-2 TCS) and `public bool WaitUntilFinished(TimeSpan timeout)` (optionally a
      `CancellationToken` overload); keep the parameterless `WaitUntilFinished()`.
- [ ] Convert controller wait paths to `await txInfo.Completion`, then read
      `TransactionState`/`FailureReason` (mapping unchanged):
  - [ ] `GraphController` `AddVertex` (`:147`), `AddEdge`, `AddProperty`, `TryRemoveProperty`,
        `TryRemoveGraphElement` — drop `Task.FromResult` wrapping, `await` (they already return
        `Task<IActionResult>`).
  - [ ] `SubGraphController.CreateSubGraph` (`:156`) & `DeleteSubGraph` — make `async Task<IActionResult>`.
  - [ ] `AdminController.Load` (`:182`) & `Save` (`:231`) — make `async Task<IActionResult>`. (Save
        still runs on the writer — `non-blocking-save` stays deferred; only the request thread is
        freed.)
- [ ] Test: many concurrent waited writes behind a slow transaction complete without pool exhaustion
      and a concurrent read stays responsive; `WaitUntilFinished(TimeSpan)` returns `false` on a
      deadline miss and `true` once finished; the fire-and-forget `202` path is unchanged.

## Phase 4 — Single-copy pooled entry encoding
Intent: remove the double copy per entry now that fsync no longer dominates.
- [ ] Add a serialize-into-buffer form to `WalTransactionCodec` (`:98`) that reserves the 4-byte
      length prefix and appends the 4-byte CRC into an `ArrayPool<byte>` buffer, so `WriteAheadLog.Append`
      writes it directly (no `mem.ToArray()`, no second `Buffer.BlockCopy` `WriteAheadLog.cs:213`) and
      returns the buffer to the pool.
- [ ] Confirm entry bytes are byte-identical to the previous framing (replay/CRC unaffected); re-run
      `WriteAheadLogTest`.

## Measure & document
Intent: quantify the win and record it against the deferral it relates to.
- [ ] Re-run `WalWritePathBenchmark`: capture post-change concurrent-producer tx/s (target ~10–100×)
      and confirm single-serial-producer latency is within noise; record numbers in this plan.
- [ ] Note the outcome in [spec.md](./spec.md) §4 and cross-reference `persistence-hardening`
      (WAL amortised) and `crash-durability-hardening` (flush-failure contract).

## Progress
- [ ] Phase 0 — benchmark + durable-before-ack + request-thread characterization
- [ ] Phase 1 — persistent append stream (no per-commit open/close)
- [ ] Phase 2 — group commit (drain → execute → single fsync → complete)
- [ ] Phase 3 — awaitable `Completion` + `WaitUntilFinished(TimeSpan)` + async controllers
- [ ] Phase 4 — single-copy pooled entry encoding
- [ ] Measure & document

## Decision / revisit condition
- **`non-blocking-save` stays deferred and is NOT reopened.** The `SaveTransaction` continues to run
  on the single writer thread (measured sub-second to 2M elements). This feature only (a) stops the
  *request* thread blocking on the save's outcome by awaiting it, and (b) amortises the *WAL* fsync
  across queued writes. It does not move save's serialization/IO off the writer. Reopen `non-blocking-save`
  only under its own stated condition (tens-of-millions of elements saved frequently), not because of
  this work.
- **Group-flush failure contract is co-owned with `crash-durability-hardening`.** This plan commits
  only to preserving today's "committed-in-memory, durability-degraded, worker-survives" behaviour for
  every member of a group. If `crash-durability-hardening` later defines a stronger contract (e.g. a
  distinct degraded-durability signal, or disabling the WAL on flush failure), the Phase-2 group-flush
  handler adopts it — that is the revisit condition, and it must keep durable-before-ack intact.
