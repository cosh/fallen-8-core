# Engine Performance — Plan

Companion to [spec.md](./spec.md). Findings P1–P10 defined there. Ordered by value ÷ effort.

## Phase 1 — Quick, high-impact wins (all S)
- **P1** path compile cache → DI singleton / static backing `MemoryCache`; test cross-request reuse.
- **P2** transaction dispatcher: `RunSynchronously()` + `Channel`/`BlockingCollection` consumer.
- **P3** incremental vertex/edge counts on removal; drop the global recount.
- **P9** reclaim `RolledBack` transaction-state in `Trim`; bound retention.
- **P10** drop `Interlocked` on `_currentId` (single-writer); `volatile` if reader visibility needed.

## Phase 2 — Index & query complexity (M)
- **P4** ordered range index (`SortedList`/balanced tree) → O(log n + k); coordinate with
  `correctness-fixes` B3 (Between inversion) and route ordered `IndexScan` operators to it.
- **P5** memoize `PluginFactory` discovery (invalidate on `Assimilate`); `FrozenDictionary` map.

## Phase 3 — Path & subgraph algorithms (M)
- **P6** bounded/lazy path reconstruction honoring `maxResults`; predecessors written directly to
  the frontier dict; paths built from parent pointers. (Weighting decision lives in correctness-fixes B8.)
- **P8** materialize subgraph collections once; drop redundant `.ToList()`/`Where` layers;
  sequential for small inputs.

## Phase 4 — Scan strategy (after core-storage-representation)
- **P7** with an array-backed store, keep `.AsParallel()` only for heavy predicates; sequential
  counting; use `ParallelHelper.GetOptimalNumberOfTasks()` where parallelism is kept.

## Status
- [x] Phase 1 — quick wins (P1, P2, P3, P9, P10) — done
- [ ] Phase 2 — index/query complexity (P4, P5)
- [ ] Phase 3 — algorithms (P6, P8)
- [ ] Phase 4 — scan strategy (P7)

### Phase 1 notes
- **P1** done. `GeneratedCodeCache`'s backing `MemoryCache` is now `static` (process-wide),
  mirroring the subgraph plugin cache. Chosen over a DI singleton to avoid churning the 17
  `new GraphController(logger, fallen8)` call sites; a compiled traverser depends only on the
  value-equality `PathSpecification`, never on a graph instance, so sharing it process-wide is
  safe. New test `EnginePerformanceTest.PathCompileCache_IsSharedAcrossControllerInstances_CompilesOnce`
  proves a second controller reuses the first's compiled traverser (reference-equal via a
  separate cache handle).
- **P2** done. `TransactionManager` now uses ONE background consumer thread that blocks on a
  `BlockingCollection<Task>` (no `Thread.Sleep(1)` idle spin) and runs each transaction inline
  via `Task.RunSynchronously()` (single writer; the body can never be inlined onto an
  enqueuer's `Wait()` because the task is never scheduled to a `TaskScheduler`). `WaitUntilFinished`
  still blocks the enqueuer via `Task.Wait()` and the task-completion happens-before still
  publishes the volatile master-store snapshot + terminal `TransactionState`/`Error`. The
  `TransactionInformation` is now registered in the state dictionary BEFORE the task is enqueued,
  so the eager consumer's `SetTransactionState` updates the caller's exact instance (fixes a race
  the old lazy poller masked; keeps B6 `Error` observable). `Dispose()` completes the queue and
  joins the worker; wired into `Fallen8.Dispose`.
- **P3** done. Vertex removal maintains counts incrementally (decrement by 1 vertex + the count
  of DISTINCT cascaded edges that transition live->removed), replacing the O(n)
  `RecalculateGraphElementCounter`. The rollback path keeps the full recount in `finally`, so a
  rolled-back removal restores exact counts. Self-loops counted once. Tests:
  committed-cascade, self-loop, edge-only, and rolled-back (counts intact).
- **P9** done (over M3). M3 already released each transaction's heavy INPUT at its terminal
  state; the remaining leak was that `Trim` only reclaimed `Finished` dictionary entries.
  `Trim` now reclaims `RolledBack` entries too, bounding retention. B6 observability via a held
  `TransactionInformation` reference is unaffected.
- **P10** done. `Interlocked.Increment(ref _currentId)` replaced with a plain `_currentId++`
  (single-writer field; no non-writer thread reads it, so no `volatile` needed).
