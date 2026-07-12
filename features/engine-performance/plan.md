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
- [ ] Phase 1 — quick wins (P1, P2, P3, P9, P10)
- [ ] Phase 2 — index/query complexity (P4, P5)
- [ ] Phase 3 — algorithms (P6, P8)
- [ ] Phase 4 — scan strategy (P7)
