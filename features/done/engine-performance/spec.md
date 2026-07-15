# Engine Performance — Specification

> **Status:** Planned. Performance improvements surfaced by the review that are independent of
> (though amplified by) the `core-storage-representation` change.

## 1. Scope

Reduce CPU cost and latency on the hot read/write and query paths, excluding the storage-
representation change (its own theme) which this theme benefits from.

## 2. Findings & targets

| # | Finding | Location | Impact | Effort |
|---|---------|----------|--------|--------|
| P1 | Path-finding recompiles Roslyn on **every** `/path` call — `GeneratedCodeCache` is created per controller instance (per request), so the cache always misses. The subgraph cache is correctly `static`. | `GraphController.cs:81`; `Cache/GeneratedCodeCache.cs:42` | High | S |
| P2 | Transaction dispatcher hands each tx to the thread pool via `Start()`+`Wait()` (two threads per serial unit) and idle-polls with `Thread.Sleep(1)` (adds ≤1 ms latency + a spinning thread) | `Transaction/TransactionManager.cs:62,66` | High | S |
| P3 | Deleting one vertex runs two full O(n) parallel recounts (`RecalculateGraphElementCounter`); subgraph pruning makes removal O(K·n) | `Fallen8.cs:715,1075`; `Algorithms/SubGraph/...:674` | High | S |
| P4 | `RangeIndex` answers every range query with a full O(n) scan of an unordered dictionary | `Index/Range/RangeIndex.cs:367` | High | M |
| P5 | `PluginFactory.GetAllTypes` re-enumerates + `Assembly.Load`s every DLL in the base dir on every index/service/save/load op | `Plugin/PluginFactory.cs:176` | Med-High | M |
| P6 | BLS builds the full (potentially exponential) path set, then `Take(maxResults)` — no early termination; heavy per-frontier/per-path allocations; cost delegates unused (see correctness-fixes B8) | `Algorithms/Path/BidirectionalLevelSynchronousSSSP.cs:292,436,567` | High | M |
| P7 | `.AsParallel()` over the immutable tree for scans/counts — cache-hostile, and PLINQ overhead often exceeds the tiny per-element predicate | `Fallen8.cs:939,1081,1088` | Med | S–M |
| P8 | Subgraph algorithm re-materializes the whole graph across phases and layers redundant `.ToList()`/`Where` | `Algorithms/SubGraph/...:141,201,620,682` | Med | S–M |
| P9 | `transactionState` grows unbounded; `Trim` reclaims only `Finished`, never `RolledBack` | `Transaction/TransactionManager.cs:42,146` | Low-Med | S |
| P10 | Cargo-cult `Interlocked.Increment(ref _currentId)` on a single-writer field (inconsistent with the plain `++` counters beside it) | `Fallen8.cs:202,224,572,607` | Low | S |

## 3. Approach (per finding)

- **P1:** register `GeneratedCodeCache` as a DI singleton (or make its backing `MemoryCache`
  `static`, like the subgraph cache). `PathSpecification` already has correct value-equality, so
  cross-request hits are safe.
- **P2:** run the tx inline (`RunSynchronously()`); replace `ConcurrentQueue`+`Sleep(1)` with a
  `Channel`/`BlockingCollection` consumer (one consumer thread preserves single-writer). Optional:
  drain a batch and apply under one publish (cheap once storage is array-backed).
- **P3:** maintain counts incrementally — the vertex-removal branch already walks cascaded edges,
  so decrement there; drop the global recount (and the recount after restore).
- **P4:** back the range index with an ordered structure (`SortedList`/balanced tree/B+-tree) →
  O(log n + k) range queries. (Pairs with correctness-fixes B3.)
- **P5:** memoize plugin discovery once (invalidate on `Assimilate`); a `FrozenDictionary` for the
  name→type map (see `dotnet10-modernization`).
- **P6:** thread `maxResults` into path reconstruction and stop expanding at the cap (lazy/bounded);
  write predecessors directly into the frontier dict instead of intermediate lists; build paths
  from parent pointers instead of copy-on-extend.
- **P7/P8:** after the storage change, scans run over a real array — keep `.AsParallel()` only for
  genuinely heavy predicates; make counting sequential (or incremental per P3); materialize each
  subgraph collection once and drop redundant `.ToList()` layers; prefer sequential for small inputs.
- **P9:** reclaim `RolledBack` entries in `Trim`; bound retention.
- **P10:** drop `Interlocked` for `_currentId` (single-writer); use `volatile` if reader visibility
  is desired.

## 4. Acceptance criteria

- `/path` under repeated identical requests compiles once, not per call (measured).
- Write latency and idle CPU drop after P2 (no 1 ms floor, no idle spin).
- Vertex delete is O(degree), not O(n) (benchmark on a large graph).
- Range queries scale O(log n + k) (benchmark across selectivities).
- Full suite green; add tests where behavior is observable (P1 cache reuse, P3 counts, P4 ranges).
