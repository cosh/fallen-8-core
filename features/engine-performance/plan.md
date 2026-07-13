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
- [x] Phase 2 — index/query complexity (P4 done; ordered IndexScan reroute LANDED in
  engine-performance-followups; P5 done)
- [x] Phase 3 — algorithms (P6 bounded reconstruction done; parent-pointer rewrite definitively
  DEFERRED in engine-performance-followups; P8 delivered via P7 + review)
- [x] Phase 4 — scan strategy (P7) — done

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

### Phase 2 notes
- **P4** done. `RangeIndex` keeps its `Dictionary` for point ops (so multi-value buckets, key
  identity by `Equals`, and the merged B3 fixes are preserved byte-for-byte) and gains a lazily
  built, cached ascending-sorted key array (`_sortedKeys`, `volatile`). `LowerThan`/`GreaterThan`/
  `Between` now binary-search that array (`LowerBound`/`UpperBound`) and gather the k matched
  buckets -> O(log n + k) instead of the old O(n) parallel scan. The cache is invalidated only on
  KEY-SET changes (new key, removed key, emptied key, wipe, load); adding a value under an
  existing key does not invalidate it and range queries always read current values from the
  dictionary. Built under the read lock / invalidated under the write lock (mutually exclusive),
  so it never sees a torn dictionary. Result order becomes sorted (previously nondeterministic
  parallel order); no caller/test depends on order. New test exercises value-only updates, new/
  removed keys, and Greater/Lower bracketing. `RangeIndexScan` already routes to `Between`.
  Deliberately did NOT reroute `Fallen8.IndexScan`'s ordered operators (Greater/Lower/etc.) onto
  the range structure: that path (`FindElementsIndex`) applies `.Distinct()` across buckets and
  serves ALL index kinds, so rerouting risked a semantic change for little gain; the range-scan
  endpoint (the primary range surface) is fully covered.
  **Follow-up:** the reroute was subsequently LANDED in `engine-performance-followups` (P4).
  `IndexScan` now routes an `IRangeIndex` + ordered operator through `GreaterThan`/`LowerThan` and
  reapplies the cross-bucket `.Distinct()`, so the deduped result set is byte-identical to the
  generic path (proven by a RangeIndex-vs-DictionaryIndex parity suite); non-range indices and
  `Equals`/`NotEquals` keep the generic path.
- **P5** done. `PluginFactory` memoizes the expensive discovery (enumerate DLLs + `Assembly.Load`
  + `GetExportedTypes` + structural filter) ONCE into `_candidateTypes` (was repeated on every
  op); `GetAllTypes<T>` now applies only the cheap interface/category filters over the cached list,
  preserving the exact set and order. `TryFindPlugin` resolves via a per-category memoized
  `FrozenDictionary<string,Type>` name->type map (built by activating each candidate once; first
  wins on a duplicate name; activation failures skipped defensively). Both caches are invalidated
  on `Assimilate`. New tests prove BLS + DIJKSTRA resolve by name and that the index enumeration
  still finds DictionaryIndex/RangeIndex/RegExIndex and the test-only ThrowingOnLoadTestIndex.

  Memoization trade-off (intended): plugins are discovered ONCE and cached until `Assimilate`
  invalidates the caches, so a DLL that lands in the base directory by a path OTHER than
  `Assimilate` (e.g. copied in out-of-band) is not picked up until the next assimilation — unlike
  the old rescan-on-every-call. `Assimilate` is the supported way to introduce a plugin at runtime,
  so this is an accepted staleness window, not a correctness regression.

  Concurrency (M1 review fix): `GetNameMap<T>` now BUILDS AND STORES each per-category name map
  under the same `_discoveryLock` that guards candidate discovery and `InvalidateDiscoveryCache`,
  capturing the candidate set once and passing it into the build (so the build never re-enters the
  lock). This closes a race where a name map built from the pre-`Assimilate` candidate set could be
  stored AFTER a concurrent invalidation, leaving a just-assimilated plugin unresolvable-by-name for
  that category until the next invalidation. The fast path stays a lock-free `ConcurrentDictionary`
  read. New test `PluginFactory_AfterDiscoveryInvalidation_RebuildsFreshNameMapAndStillResolves`
  pins that an invalidation clears the map and the next lookup rebuilds a fresh, full set.

  Degenerate/unsupported cases (note only): `TryFindPlugin(name: null)` returns `false` (the
  name-map lookup is guarded by a non-null name); and a `RangeIndex` populated with
  heterogeneous-typed keys throws at sort time when the sorted-key cache is (re)built — both are
  out-of-contract inputs, called out here so the behaviour is documented rather than surprising.

### Phase 3 notes
- **P6** done (bounded reconstruction); parent-pointer rewrite deferred. BLS
  (`BidirectionalLevelSynchronousSSSP`) reconstruction now threads `maxResults` through
  `CreatePaths` -> `CreateToSourcePaths` -> `CreatePathsRecusive` and stops early / caps every
  intermediate list at `maxResults` (`CapPaths`), instead of building the full (potentially
  exponential) path set and only `Take(maxResults)` at the end. This is result-preserving: paths
  are built in a fixed order and each intermediate path yields >= 1 final path when extended, so
  the first `maxResults` intermediates already cover the first `maxResults` finals; truncating the
  tail never changes the first `maxResults` paths or their order. Predecessors are already written
  into the frontier dict (existing design), satisfying that part of the finding. The
  copy-on-extend -> parent-pointer path-object rewrite is DEFERRED: it would reshape the `Path`
  class for an allocation micro-optimisation and materially risks changing BLS's observable results
  (the `PathTest` suite, incl. exact-path assertions at `maxResults=1`), which the guardrail says to
  protect. New test proves `maxResults=2` returns exactly the first two paths of an unbounded
  (`maxResults=100`) run, element-for-element. DIJKSTRA untouched.
  **Follow-up:** re-evaluated in `engine-performance-followups` (P6) and DEFINITIVELY DEFERRED. The
  reconstruction's reversal seam (build middle->source, `ReversePath`, then extend middle->target)
  makes a byte-identical parent-pointer rewrite high-risk, and an opt-in allocation benchmark
  quantified the low reward: BLS reconstructs only ~(# middle vertices) paths (the shared
  `visitedVertices` set makes predecessors a spanning tree — a graph with 4.3 billion equal-length
  routes still yields 2 paths), so copy-on-extend is a small, length-driven slice of a cost dominated
  by frontier expansion. `Path`/`PathElement` stay unchanged.
- **P8** delivered via P7 + review. The subgraph algorithm (`BreathFirstSearchSubgraphAlgorithm`)
  was re-read against its CURRENT shape (prior themes, esp. memory-footprint M3, already tidied
  it): each phase's whole-graph scan goes through `GetAllVertices`/`GetAllEdges`/
  `GetAllGraphElements`, which P7 turned into cheap SEQUENTIAL id-ordered walks (the dominant
  "re-materializes the whole graph across phases" cost). The algorithm already materializes each
  collection exactly once and uses plain sequential LINQ (no parallelism to strip); the pattern
  phases work over in-memory path lists. No further redundant `.ToList()`/`.Where()` layer or
  multi-enumeration remained that could be removed safely without an engine-API change or risking
  the `SubGraphTest`/`SubGraphControllerTest` suites, so per stop-at-safe-boundary no additional
  algorithm-level change was forced. Both subgraph suites remain green.

### Phase 4 notes
- **P7** done. The light-predicate scans over the segmented store no longer pay PLINQ overhead:
  `GetCountOf<T>` is now a direct sequential count loop, and `GetAllVertices`/`GetAllEdges`/
  `GetAllGraphElements` iterate sequentially in id order via a new `LiveElementsSequential` helper
  (the old parallel scan was unordered, so no caller relied on order). The one genuinely heavy scan
  - `FindElements` (the `/scan/graph` full-graph scan with a user predicate) - KEEPS `.AsParallel()`,
  now bounded by `ParallelHelper.GetOptimalNumberOfTasks()` (clamped to >= 1, since it computes 0 on
  a single-core host) via `WithDegreeOfParallelism` inside `LiveElements`. Behaviour-preserving (same
  result sets; counts identical). Verified against Core/GraphController/Concurrent-storage/subgraph.

## Measured results (real, captured in this environment)

Opt-in `EnginePerformanceBenchmark` methods (`[TestCategory("Benchmark")]` + `[Ignore]`), run once
with `[Ignore]` temporarily removed on this dev box (net10.0, Debug build). Numbers are indicative
(Debug, single run) - not a formal BenchmarkDotNet study - but they demonstrate the intended
complexity shifts:

- **P1 - /path compiles once under repeated identical requests.** First call (Roslyn compile) =
  ~605 ms; each of 40 subsequent calls, each through a FRESH `GraphController`, = ~0.008 ms
  (process-wide cache hit). The ~78,000x drop confirms the traverser is compiled once and reused
  across controller instances (the pre-fix per-instance cache recompiled every call).
- **P3 - vertex delete is O(degree), not O(n).** Deleting a fixed degree-10 vertex: n=100,000 ->
  ~0.088 ms, n=500,000 -> ~0.234 ms (the n=20,000 -> ~1.8 ms figure is first-run JIT warmup). The
  time does not grow with n, confirming O(degree) (the old double full O(n) recount scaled with n).
- **P4 - range query scales O(log n + k).** Fixed n=500,000, varying selectivity k: k=10 ->
  ~0.002 ms, k=100 -> ~0.015 ms, k=1,000 -> ~0.198 ms, k=10,000 -> ~2.67 ms, k=100,000 ->
  ~16.8 ms. Time tracks the result size k (plus the log n search), not n; a tiny query over a
  500k-key index is microseconds, whereas the old O(n) scan cost the same regardless of k.

## Final suite status
Full `dotnet test`: 277 passing / 5 skipped (baseline 268 passing / 2 skipped; +9 new behaviour
tests, +3 new opt-in benchmarks). Build: 0 warnings / 0 errors.
