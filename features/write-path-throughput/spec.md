# Write-Path Throughput — Specification

> **Status:** Implemented (group commit + awaitable completion). From the 2026-07 principal-architect
> & performance review. The WAL fsync is amortised across a drained commit group and request threads
> are freed by an awaitable completion. Measured on this box (WAL on, 20k single-element writes):
> **serial 788 writes/s → 32 concurrent producers 17,007 writes/s (~21×)**, with the serial per-commit
> latency floor unchanged (a group of one still fsyncs immediately). **Deferred within the feature:**
> (a) the persistent append handle (kept the per-group open+fsync+close so the D1 failure-fence stays
> externally testable and there is no handle lifecycle across `ResetToSnapshot`); (d) the single-copy
> pooled entry encoding; and the async conversion of `SubGraphController.CreateSubGraph`/`DeleteSubGraph`
> (large test-signature migration, lower-frequency endpoints) — `GraphController`'s five mutations and
> `AdminController.Load`/`Save` are converted. See the plan's Decision.

## 1. Problem / current state

Two independent bottlenecks throttle the write path. The first bites only with the WAL enabled
(`persistence-hardening` Stage D — opt-in, off by default); the second bites on *every* waited
write regardless of the WAL.

| # | Finding | Location | Effect |
|---|---------|----------|--------|
| W1 | The WAL does **one fsync per commit**, and it opens+writes+closes the file each time. `Append` opens a fresh `FileStream(FileMode.Append)`, writes one frame, `Flush(true)`, closes — on the single writer thread, before the transaction's task completes. | `Persistency/WriteAheadLog.cs:211` (open/write/fsync/close at `:219`–`:224`); called via `Fallen8.LogCommittedTransaction` `Fallen8.cs:1331` from `TransactionManager` `:187` | WAL-on write throughput is capped near `1 / (open + fsync + close)` commits/second regardless of engine speed, and the single writer is stalled for the whole fsync. A *batch* transaction amortises the one fsync across all its elements; a stream of single-element transactions each pays a full fsync. |
| W2 | `WaitUntilFinished()` is a blocking `Task.Wait()`, and every controller wait path calls it **synchronously on the request thread**. | `Transaction/TransactionInformation.cs:82`–`:88`; `GraphController.cs:147` (and the same pattern in `AddEdge`/`AddProperty`/`TryRemoveProperty`/`TryRemoveGraphElement`), `SubGraphController.cs:156`, `AdminController.cs:182` (Load) & `:231` (Save) | Each waited write pins an ASP.NET thread-pool thread for its full queue latency. Behind a long save (`non-blocking-save`: 170 ms @ 100k … 907 ms @ 2M, paid on the single writer) many concurrent waited writes exhaust the pool → thread-pool starvation that also throttles *readers*. There is no timeout or cancellation on the wait either. |

Structurally, the consumer runs each transaction one at a time: `ConsumeLoop` pulls a single task
from `GetConsumingEnumerable()` (`TransactionManager.cs:96`) and runs it inline via
`transactionTask.RunSynchronously()` (`:105`). The per-commit fsync therefore serialises the writer:
it cannot start the next transaction until the current one's frame is on disk. There is no path that
batches the queued work under one fsync even though the queue frequently holds several ready
transactions at once.

A secondary, latent cost sits in the entry encoding: `WalTransactionCodec.SerializeEntry`
(`WalTransactionCodec.cs:98`) allocates a fresh `MemoryStream` + `SerializationWriter` and returns
`mem.ToArray()` (`:204`) — one copy — and then `WriteAheadLog.Append` copies that payload **again**
into a `new byte[4 + payload.Length + 4]` frame (`WriteAheadLog.cs:213`–`:217`) — a second copy — per
commit. This is dominated by the fsync today, but becomes the next bottleneck once group commit
lands.

The `persistence-hardening` WAL is otherwise correct and complete: `F8WAL` magic + versioned,
CRC-protected header and per-entry `[Int32 length][payload][CRC-32]` framing, torn-tail-safe replay,
snapshot pairing/`ResetToSnapshot` truncation, and the *durable-before-ack* contract (a committed
transaction's entry is fsync'd before its `WaitUntilFinished()` returns). This work must keep every
one of those properties.

## 2. Goals / non-goals

**Goals.**
- Amortise the WAL fsync across all transactions that are ready at the same instant (group commit),
  so WAL-on throughput scales with concurrency instead of being pinned at one fsync per commit,
  while the durable-before-ack contract holds **exactly**.
- Remove the per-commit file open/close by holding one persistent append stream for the log's
  lifetime.
- Stop pinning request threads: expose an awaitable `Completion` on `TransactionInformation` and have
  the controllers `await` it instead of `Task.Wait()`, and add a bounded `WaitUntilFinished(TimeSpan)`
  overload.
- Cut the per-entry encoding to a single copy into a pooled, framing-reserving buffer.
- Leave single-serial-producer latency unchanged (a lone writer with an empty queue still commits and
  fsyncs immediately — a group of one).

**Non-goals.**
- **Moving the save/serialize off the writer thread — deferred in `non-blocking-save` (measured) and
  NOT reopened here.** The `SaveTransaction` still runs on the single writer; this feature only stops
  the *request* thread from blocking on its outcome and amortises the *WAL* fsync. Save remains a
  batch boundary, not a background job.
- Changing the transaction model: mutations still run on the single writer thread; reads stay
  lock-free over the volatile snapshot; the `Try*(out,…) : bool` contract is untouched.
- Changing the WAL on-disk format, the snapshot format, or the replay/pairing logic
  (`persistence-hardening` Stage A/D). Group commit changes only *when* bytes are fsync'd and when
  tasks complete, not what is written.
- Changing the WAL opt-in default (still off unless `WriteAheadLogOptions` is supplied).

## 3. Design sketch

### (a) Persistent append stream in `WriteAheadLog`
Replace the per-`Append` `using (new FileStream(FileMode.Append …))` with a single append-mode
`FileStream` opened once (lazily, on first append after construction/`ResetToSnapshot`) and held for
the log's lifetime. `Append` writes the frame into that stream; the fsync becomes a separate step
(see (b)). The stream is closed and reopened exactly at the two points that already rewrite the log
file — `ResetToSnapshot` (`Fallen8.cs:860` after Save, `:1637` after a non-pairing Load) rewrites the
header via temp+rename, so the persistent handle must be closed before the rename and reopened after
— and disposed in `WriteAheadLog.Dispose` (`Fallen8.cs:1686`). All of this stays on the single writer
thread, so no new locking is introduced.

### (b) Group commit in `TransactionManager`
Restructure the consumer from one-task-at-a-time `RunSynchronously` to a **drain-execute-flush-complete**
loop on the same single writer thread:
1. Block on `_transactions.Take()` for the first ready transaction, then greedily drain the rest of
   the currently-queued work with a `TryTake(out …, 0)` loop into an ordered batch.
2. Execute each transaction's body **in commit order** on the writer (the existing
   `ProcessTransaction` logic: `TryExecute`, set terminal state, release inputs, per-removal
   auto-trim), but instead of `wal.Append` fsyncing per commit, **buffer** each committed loggable
   transaction's frame (and any lifecycle marker such as an auto-`Trim`) into the persistent stream
   without fsyncing.
3. Issue **one** `Flush(true)` for the whole batch.
4. Only **then** complete every transaction's completion signal.

Because task completion moves to *after* the group fsync, no `WaitUntilFinished`/`Completion` can
return before that transaction's entry is durable — the durable-before-ack contract is preserved
identically, just amortised. This requires switching from `new Task(ProcessTransaction, …)` +
`RunSynchronously()` to explicit body execution plus a `TaskCompletionSource` per transaction that the
writer completes in step 4; `TransactionInformation.Completion` returns that TCS's `Task`.

**Batch boundaries.** A `SaveTransaction`/`LoadTransaction` cannot share a batch with buffered WAL
frames, because Save writes the snapshot and then `ResetToSnapshot` truncates/rewrites the log
(`Fallen8.cs:860`) and Load may replace state and re-anchor. So the drain flushes and completes any
pending group *before* processing a Save/Load, and the Save/Load commits as a group of one. This keeps
Save-then-truncate crash-safe exactly as `persistence-hardening` Stage D describes.

**Failure handling — coordinate with `crash-durability-hardening`.** Today a WAL append failure is
contained per transaction in `LogCommittedTransactionSafely` (`TransactionManager.cs:220`): the
transaction stays `Finished` with degraded durability recorded on its `TransactionInformation.Error`,
never faulting the worker. Under group commit the failure surface shifts to the single group flush: if
the group `Flush(true)` throws, **all** transactions in that group are already applied in memory but
not durable — each must have its degraded-durability outcome recorded before completion, and the
worker must survive. The exact contract (do we surface a distinct degraded-durability signal? does a
flush failure disable the WAL?) is owned jointly with `crash-durability-hardening`; this spec commits
only to *preserving today's "committed-in-memory, durability-degraded, worker-survives" behaviour for
every member of the group*.

### (c) Awaitable completion + bounded wait (`TransactionInformation`)
- Add `public Task Completion => _txTask ?? Task.CompletedTask;` (after (b), backed by the
  `TaskCompletionSource.Task`). Awaiting it registers a continuation instead of blocking, so a waiting
  request releases its pool thread.
- Add `public bool WaitUntilFinished(TimeSpan timeout)` (and optionally a `CancellationToken`
  overload) returning whether the transaction finished within the budget — the parameterless
  `WaitUntilFinished()` stays for existing callers/tests.
- Convert the controller wait paths to `await txInfo.Completion` and read the terminal
  state/`FailureReason` afterward (the `transaction-failure-reasons` mapping is unchanged). **These
  actions are not async today and must be made so:** the five `GraphController` mutations return
  `Task<IActionResult>` but have synchronous bodies (`Task.FromResult` + a blocking
  `WaitUntilFinished()`), so they drop the wrapping and `await`; `SubGraphController.CreateSubGraph`/
  `DeleteSubGraph` and `AdminController.Load`/`Save` are plain synchronous `IActionResult` methods and
  become `async Task<IActionResult>`. The fire-and-forget (`waitForCompletion=false`) path and every
  returned status code are unchanged.

### (d) Single-copy pooled entry encoding
Give `WalTransactionCodec` a serialize-into-buffer form that reserves the 4-byte length prefix at the
front and appends the 4-byte CRC at the tail, writing into an `ArrayPool<byte>` buffer that `Append`
writes directly to the stream and returns — removing both the `mem.ToArray()` copy and the second
`Buffer.BlockCopy` into a fresh frame. Land this only after group commit, when the fsync no longer
dominates.

## 4. Acceptance criteria

- **Throughput (measured).** An opt-in benchmark (mirroring `NonBlockingSaveBenchmark`: `[TestClass]`,
  `[TestCategory("Benchmark")]` + `[Ignore]`) runs N concurrent producers issuing single-element
  writes against a WAL-enabled engine and reports committed tx/s. With group commit, aggregate
  throughput under concurrent producers scales roughly **10–100×** over the pre-change one-fsync-per-
  commit baseline (both numbers *to be captured on this box*), while a **single serial producer's**
  per-commit latency is within noise of the baseline (a group of one still fsyncs immediately).
- **Durable-before-ack still holds.** A test that, with the WAL on, kills/abandons the engine
  immediately after `WaitUntilFinished()` (or `await Completion`) returns for a committed write, then
  reopens and replays the log, finds that write present — for both a lone commit and a member of a
  drained group.
- **Request threads are not pinned.** A test asserts the controller wait path `await`s (does not
  `Task.Wait()`): e.g. many concurrent waited writes queued behind a slow transaction complete
  without exhausting the thread pool, and a concurrent read stays responsive. The
  `WaitUntilFinished(TimeSpan)` overload returns `false` on a deadline miss and `true` once finished,
  without throwing.
- **Behaviour preserved.** Single-writer ordering, the `transaction-failure-reasons` status mapping,
  the fire-and-forget `202` path, WAL replay/pairing/torn-tail safety, and Save-then-truncate crash
  safety are all unchanged; the full suite stays green.

## 5. Risks

- **Durability contract regression (highest).** Completing a task before the group fsync would break
  durable-before-ack. The completion step MUST come strictly after the single `Flush(true)`; the
  durable-before-ack test guards this. Coordinate the flush-failure semantics with
  `crash-durability-hardening` so a partial group is never acked as durable.
- **Batching a Save/Load into a group** would corrupt the log across a `ResetToSnapshot` truncation.
  Save/Load must be a hard batch boundary (flush + complete the pending group first).
- **Auto-trim / lifecycle markers inside a batch.** An auto-`Trim` triggered by a removal within the
  batch appends a WAL marker; it must be buffered in commit order with the surrounding frames, and its
  id-space effect (id reassignment) must be applied on the writer before later frames in the same
  batch are serialised — the existing per-transaction ordering must be maintained exactly.
- **Persistent stream lifecycle.** A leaked/duplicated handle across `ResetToSnapshot` (which rewrites
  the file via temp+rename) would corrupt or lock the log; the close-before-rename / reopen-after must
  be exact, and remains single-writer so no lock is needed.
- **Latency vs. throughput trade-off.** Group commit trades a hair of best-case single-commit latency
  for throughput; the benchmark pins that the serial-producer case is not regressed.
- **Async controller conversion** must not change status codes, the fire-and-forget path, or exception
  handling (`SubGraphController.CreateSubGraph` catches infrastructure faults → 500).

## 6. Keep (do not regress)

- **Single writer.** Every transaction body, WAL append/flush, and `ResetToSnapshot` stays on the one
  writer thread; reads stay lock-free over the volatile snapshot. Group commit changes batching, not
  the thread model.
- **Durable-before-ack** (`persistence-hardening` Stage D / `crash-durability-hardening`): a committed
  transaction's WAL entry is durable before its wait returns.
- **WAL correctness** (`persistence-hardening` Stage D): `F8WAL` magic/version/CRC header, per-entry
  framing, torn/corrupt-tail-safe replay, snapshot pairing + `ResetToSnapshot` truncation ordering,
  opt-in-off-by-default, and the contained "WAL failure never faults the worker" behaviour.
- **The `transaction-failure-reasons` mapping and the B6 `Error`/state observability** on
  `TransactionInformation`, published under the same happens-before as completion.
- **WAL-off default path** stays exactly as before — no fsync, no extra stream, no behaviour change
  for engines constructed without `WriteAheadLogOptions`.
