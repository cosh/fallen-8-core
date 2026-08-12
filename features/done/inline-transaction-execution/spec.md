# Inline transaction execution

**Status:** implemented on `feature/inline-transaction-execution`. The engine can now be constructed and
written to on a host that cannot start a thread (single-threaded browser WebAssembly). The threaded
server design is unchanged and stays the default wherever a thread can be started.

## Why

`new Fallen8(...)` threw `System.PlatformNotSupportedException` on the single-threaded browser-wasm
runtime, before any API call:

- `Fallen8.cs` — `_txManager = new TransactionManager(this)` in the primary constructor; every other
  constructor chains to it.
- `Transaction/TransactionManager.cs` — the constructor unconditionally created and started the writer
  thread (`_worker.Start()`), and `Thread.Start` is unsupported on that runtime
  (`Thread.ThrowIfNoThreadStart` → `Thread.Start`).

There was no bypass: `IFallen8Write` declares only `EnqueueTransaction(ATransaction)`, the real mutators
are `internal`, and the assembly declares no `InternalsVisibleTo`.

This is an improvement, not a prerequisite: `WasmEnableThreads=true` on the host already made the engine
work in a browser unmodified. Inline mode is worth having because it drops the threads dependency, and
with it cross-origin isolation on every embedding page, a larger payload, the experimental wasm workload,
and poor Safari/mobile odds.

## Contract

`TransactionExecutionMode` (public, in `NoSQL.GraphDB.Core.Transaction`) is the ONE home for the
explanation. Both modes uphold the same single-writer invariant and share one execution path; they differ
only in which thread runs the body, and therefore in what a commit group can hold.

| Mode        | Meaning                                                                                       |
| ----------- | --------------------------------------------------------------------------------------------- |
| `Automatic` | Default. Threaded where a writer thread can be started, inline where it cannot                 |
| `Threaded`  | Require the writer thread; the `PlatformNotSupportedException` propagates on a host without it |
| `Inline`    | Require inline execution on the calling thread, even where a thread could be started           |

Selected per engine: `new Fallen8(loggerFactory, transactionExecutionMode: ...)` and the widest
(write-ahead-log) constructor. `Fallen8.TransactionExecution` reports the RESOLVED mode, never
`Automatic`.

### Detection is a runtime capability probe, not `#if BROWSER`

One assembly ships to every host via the `Fallen-8` package, so the check is a runtime one: the manager
attempts the threaded design and treats a `PlatformNotSupportedException` out of the queue/thread
allocation as "this host is single-threaded", falling back to inline. Nothing else is caught, and an
explicit `Threaded` request skips the fallback filter so such a host fails loudly instead of being
silently downgraded.

### What inline mode does

`EnqueueTransaction` runs `ExecuteTransactionBody` + `FlushAndCompleteGroup` — the same pair the writer
thread runs — as a commit group of one, on the calling thread, and returns a `TransactionInformation` that
is already terminal with its completion source already set. `WaitUntilFinished()`, `WaitUntilFinished(0)`
and `await Completion` therefore never wait. No consumer thread is started and no queue (and no wait
handle) is allocated at all.

Because everything runs through the existing path, these are preserved rather than reimplemented:
ordering, rollback (clean and faulted, with `Error`/`FailureReason` containment), `GetTransactionState`,
the terminal-transaction FIFO and its retention bound, `Trim`, change-feed descriptors, and the
durability/WAL hooks (`LogCommittedTransaction`, `FlushWal`) the commit path fires.

**Nothing is refused.** The write-ahead log works: each inline transaction is its own commit group, so it
fsyncs before the call returns — durable-before-ack holds, at the pre-group-commit cost of one fsync per
transaction. The change feed works: publication happens on the calling thread, after the flush, in commit
order. Delivery to subscribers stays the dispatcher's asynchronous job, which on a single-threaded host
progresses when the app yields to the event loop. What IS given up is group commit — the only thing the
queue was buying — plus the decoupling of the caller from the write.

### Two invariants that needed explicit care

- **Concurrent callers.** Inline mode exists for a host with one thread, but a forced-inline host (or a
  test) may have several. Inline execution is therefore serialized on a private gate, so the single-writer
  invariant holds by construction rather than by assuming the host. On the single-threaded host the lock
  is never contended and never blocks; the threaded path never takes it.
- **Reentrancy.** A transaction body CAN reach the engine through a captured reference (a
  `DelegateTransaction` body is the reachable case). Such a reentrant enqueue is not run nested — which
  would invert commit order and let a nested WAL frame flush inside the outer transaction — but queued and
  drained after the current transaction. Enqueue order is preserved and every commit group stays one
  transaction wide. Consequence, documented on the method: the nested `TransactionInformation` is not yet
  complete when that inner call returns (it completes before the outer call returns), and a body that
  WAITED on it would hang — exactly as it would on the threaded path, where waiting on the writer from the
  writer hangs too.

## The threaded path is untouched

The threaded branch allocates the same queue and starts the same thread, and `ConsumeLoop`,
`ExecuteTransactionBody`, `FlushAndCompleteGroup` and group commit are unmodified. The blocking-consumer
design that replaced the old `Thread.Sleep(1)` spin (and its ~1 ms per-transaction latency floor) is
retained as-is. The only additions on that path are the `try` around allocation-plus-`Start` and one
`?.`/null check each in `QueueDepth` and `Dispose`.

## Verification

Beyond the unit tests (`fallen-8-unittest/InlineTransactionExecutionTest.cs`, 20 tests), the claim was
exercised on the real single-threaded browser-wasm runtime with a throwaway `wasmconsole` project
referencing the engine (scratchpad, not committed):

- `Thread.Start` throws `PlatformNotSupportedException` there — the blocker reproduces.
- Construction succeeds and resolves to `Inline` by detection, with no argument passed.
- Vertices and edges commit inline, are read back (counts, properties, adjacency), and a clean rollback
  reports `NotFound`.
- The returned transaction is already complete; `WaitUntilFinished(TimeSpan.Zero)` succeeds.
- Path finding runs: `TryCalculateShortestPath<BidirectionalLevelSynchronousSSSP>` finds the expected
  3-hop path.

## Known limit found while verifying (NOT part of this feature)

Plugin lookup **by name** does not resolve on browser-wasm: `PluginFactory.DiscoverCandidateTypes`
enumerates `*.dll` under `AppContext.BaseDirectory`, which is `/` with zero dll files there (assemblies
come from the app bundle, not the virtual filesystem), so `TryCalculateShortestPath(out paths, "BLS", …)`
returns `false`. The typed overload works, so this is a discovery limitation, not a path-finding or
transaction one. Fixing it (for example: fall back to the already-loaded assemblies when the directory
scan yields nothing) is a separate, engine-wide change and was deliberately left out of this feature. It
is recorded on the library docs page as a browser limitation.

## Impact on existing features

| Area                                  | Impact                                                                                    |
| ------------------------------------- | ----------------------------------------------------------------------------------------- |
| Engine transactions / write path      | Additive: one new mode; the threaded design and group commit unchanged                     |
| REST contract, OpenAPI snapshot       | None — no controller, route or XML doc changed; the mode is not exposed over HTTP          |
| MCP server (engine → REST → MCP)      | None — no new REST operation, so nothing to bridge or defer                                |
| Studio UI, NL-assist dataset/eval     | None — no contract change                                                                  |
| Observability                         | Queue-depth gauge stays readable and reports 0 inline (no queue); spans/metrics unchanged   |
| Write-ahead log, change feed          | Work inline (group of one); no code change to either                                       |
| Subgraphs                             | A derived subgraph engine resolves its own mode via `Automatic`, so it is inline exactly where threads are unavailable |
| Persistence / save games              | Unchanged in-process. The claim that a browser cannot persist was retired on 2026-08-12: the checkpoint fan-out now runs inline where no thread can be started, so a save and load complete on a single-threaded host (verified into the Emscripten virtual filesystem). What a browser host still has to solve is getting those bytes out of the VFS, which is its own business |
| Docs site, README, architecture       | `library.mdx` gains a section; the README library entry gains a clause. No new channel or deployable, so both architecture diagrams stay correct |

## Out of scope

REST or HTTP (the browser playground calls the engine in-process, by decision), Roslyn fragment
compilation in the browser, browser persistence, payload size, trimming, AOT.
