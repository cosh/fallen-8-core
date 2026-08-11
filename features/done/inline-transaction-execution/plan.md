# Plan — inline transaction execution

Phases as executed. See [spec.md](spec.md) for the contract.

## Phase 1 — the mode, and its one home

- [x] `fallen-8-core/Transaction/TransactionExecutionMode.cs`: public enum `Automatic` / `Threaded` /
      `Inline`, carrying THE explanation of the two designs and of why detection is a runtime probe.

## Phase 2 — TransactionManager: a second mode behind the same surface

- [x] Constructor takes the requested mode. Threaded branch (unchanged design) allocates the queue and
      starts the writer inside a `try`; `PlatformNotSupportedException` under `Automatic` falls back to
      inline, releasing the unused queue. `Threaded` lets it propagate.
- [x] `Mode` (internal) exposes the RESOLVED mode.
- [x] `AddTransaction`: unchanged bookkeeping, then either `_transactions.Add(item)` (threaded) or
      `ExecuteInline(item)`.
- [x] `ExecuteInline`: gate-serialized; reentrant enqueues deferred and drained after the current
      transaction; leftovers completed in a `finally` so no waiter can hang.
- [x] `RunAsGroupOfOne`: `ExecuteTransactionBody` + `FlushAndCompleteGroup` through a reusable
      single-member group list, so an inline commit allocates no per-transaction list.
- [x] `QueueDepth` reports 0 inline; `Dispose` returns early (no thread to join, no queue to complete).
- [x] `ConsumeLoop`, `ExecuteTransactionBody`, `FlushAndCompleteGroup`, `SetTransactionState`, `Trim`:
      untouched, so both modes share one execution path.

## Phase 3 — Fallen8 surface

- [x] `transactionExecutionMode` optional argument on the primary constructor (every other constructor
      chains through it) and on the widest write-ahead-log constructor, so a host can combine inline mode
      with the WAL and the change feed.
- [x] `Fallen8.TransactionExecution` reports the resolved mode.
- [x] Stale doc comments corrected where they promised "the writer THREAD": `DelegateTransaction`,
      `IFallen8WriterContext`, and the terminal-FIFO note in `TransactionManager` — each now points at
      `TransactionExecutionMode` instead of re-explaining it.

## Phase 4 — Tests

- [x] `fallen-8-unittest/InlineTransactionExecutionTest.cs` (20 tests): no writer thread and no queue
      allocated; `Automatic` resolves to threaded on a threads-capable host (the no-regression control);
      an enqueued transaction is already complete and a zero-timeout wait succeeds; vertices, edges,
      properties and adjacency read back; a traversal runs; enqueue order preserved; clean rollback with
      `NotFound`; a faulting body contained with `Error`/`InternalError` and the engine still usable;
      `GetTransactionState` for terminal, unknown and trimmed ids; the retention FIFO bound; 64 concurrent
      callers serialized without a lost or duplicated write; a reentrant enqueue deferred and ordered;
      WAL durable-plus-replay; change feed in commit order; save/load round trip.
- [x] Full suite green (`dotnet test fallen-8-core.sln`), including the convention, OpenAPI-snapshot and
      MCP-coverage gates.

## Phase 5 — Verify on the real runtime, and document

- [x] Throwaway `wasmconsole` project (scratchpad, not committed) referencing the engine on the
      single-threaded browser-wasm runtime: construction, inline detection, writes, reads, traversal and
      rollback all pass; the plugin-lookup-by-name limitation surfaced and was recorded rather than fixed.
- [x] `docs/src/content/docs/library.mdx`: a "Single-threaded hosts" section, plus the constructor table
      and the writing paragraph corrected. README library entry extended.
