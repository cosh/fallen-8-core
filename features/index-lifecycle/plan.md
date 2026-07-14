# Index Lifecycle — Plan

Companion to [spec.md](./spec.md). Correctness floor first (read-end), then the durable fix
(write-end + efficiency), then single-writer + concurrency, then WAL. Each behaviour is pinned by a
test; the perf claims by an opt-in benchmark.

GitHub issue: to be opened (label: feature).

## Phase 0 — Baseline & guardrails
Intent: prove the bug and the cost before touching anything, so the fixes are guarded against
regression.
- [ ] Characterization test (`IndexLifecycleTest`): index N vertices under a property, remove a
  subset via `RemoveGraphElementTransaction`, then assert TODAY's behaviour — `IndexScan`
  (Equals + an ordered op + `NotEquals`), `RangeIndexScan`, and `FindElementsIndex` still return the
  removed elements while `GraphScan` for the same predicate does not. This test documents the
  divergence and flips to the fixed assertion in Phase 1.
- [ ] Opt-in benchmark (`IndexLifecycleBenchmark`, `[TestClass]` with `[TestCategory("Benchmark")]`
  + `[Ignore]` per repo convention): (a) `DictionaryIndex.RemoveValue` cost across a growing key set
  (shows the O(keys)×O(n) curve); (b) concurrent `TryGetValue` throughput across threads (shows the
  `AThreadSafeElement` cache-line ceiling). Record "to be captured on this box".

## Phase 1 — Stop the stale reads (read-end correctness floor)
Intent: make every index-serving read agree with `GraphScan` immediately, independent of the refactor.
- [ ] Add a single `FilterLive` helper in `Fallen8.cs` and apply it to `IndexScan` Equals
  (`Fallen8.cs:748`), `FindElementsIndex` (`Fallen8.cs:1755`), `TryOrderedRangeIndexScan`
  (`Fallen8.cs:1784`), and `RangeIndexScan`/`Between` (`Fallen8.cs:812`) so all four filter
  `_ != null && !_._removed` exactly as `FindElements` (`Fallen8.cs:1737`) does.
- [ ] Flip the Phase 0 characterization test: index-served results now equal `GraphScan` results.

## Phase 2 — Efficient dictionary-backed indices (reverse map + writer-owned buckets)
Intent: make removal O(affected keys) so the write-end purge (Phase 3) is affordable.
- [ ] Add a reverse map `Dictionary<Int32, HashSet<IComparable>>` to `DictionaryIndex`
  (`Index/DictionaryIndex.cs`), maintained in `AddOrUpdate` (`:117`) and `RemoveValue` (`:176`);
  `RemoveValue` touches only the element's own keys — no `_idx.Keys.ToList()` full scan, no per-key
  allocation.
- [ ] Mirror the change in `RangeIndex` (`Index/Range/RangeIndex.cs`, `AddOrUpdate :245`,
  `RemoveValue :302`), preserving the `_sortedKeys` invalidation on key-set shrink; apply the same to
  `SingleValueIndex` and `RegExIndex`.
- [ ] Tests: `RemoveValue` removes from all and only the element's buckets; multi-value buckets,
  cross-bucket elements, and emptied-key drop still behave (reuse the existing `IndexTest`/
  `CorrectnessFixesTest` cases); reverse map has no residual entry for a removed element.
- [ ] Benchmark: confirm `RemoveValue` is now flat in the key-set size (Phase 0 curve gone).

## Phase 3 — Element removal maintains registered indices (write-end purge)
Intent: an element leaving the live set leaves every registered index — the durable correctness fix.
- [ ] In `TryRemoveGraphElement_private` (`Fallen8.cs:1054`) and the batch remove, after the
  live→removed transition on the commit path, call `index.RemoveValue(element)` for each registered
  `IndexFactory.Indices` entry (edges too, when a cascaded edge transitions). Runs on the single
  writer; only on commit, so rollback is unaffected.
- [ ] Test: after removal, the element is unreachable from every index (buckets AND reverse map) with
  NO read-end filter needed — temporarily disable the Phase 1 filter in the test to prove the write
  end alone is now sufficient (keep the filter in production as defence in depth).

## Phase 4 — Index mutations as single-writer transactions
Intent: bring index writes under the single-writer invariant (prerequisite for WAL + lock-free reads).
- [ ] Add `AddToIndexTransaction`, `RemoveFromIndexTransaction`, `RemoveIndexKeyTransaction` (and
  create/delete/wipe as needed) under `Transaction/`, each `TryExecute` calling the index op on the
  writer; enqueue via the existing pipeline (`TransactionManager`).
- [ ] Repoint the REST actions `AddToIndex` (`GraphController.cs:1132`), `RemoveKeyFromIndex`
  (`:1173`), `RemoveGraphElementFromIndex` (`:1201`) to build a transaction + `WaitUntilFinished`,
  mapping outcomes through the `transaction-failure-reasons` channel (index/element not found →
  `NotFound` → 404; invalid key → `InvalidInput` → 400).
- [ ] Publish each index's bucket map as an immutable/`FrozenDictionary` swapped by a single
  `volatile` store; make `TryGetValue`/`GetKeyValues`/`Between` read the field with **no lock**.
  Retire `AThreadSafeElement` for the indices and `IndexFactory`; delete the now-dead
  `CollisionException` paths.
  - [ ] (Fallback if writer-routing is deferred: replace `AThreadSafeElement`
    (`Helper/AThreadSafeElement.cs`) with `ReaderWriterLockSlim` in the indices/`IndexFactory` and
    stop here — fixes concurrency (d) but not durability (c).)
- [ ] Tests: index writes observe single-writer ordering; concurrent reads scale (benchmark);
  not-found/invalid map to 4xx; existing index REST tests migrated to the transactional semantics.

## Phase 5 — WAL-logging index writes (coordinate with crash-durability-hardening)
Intent: close the between-snapshots durability gap for index writes.
- [ ] Jointly with `crash-durability-hardening`, reserve the next free `WalEntryType`
  (`Persistency/WalEntryType.cs`; 1–13 in use) ordinals for the index transactions and extend
  `WalTransactionCodec` (`Persistency/WalTransactionCodec.cs`) to classify/serialize/replay them —
  same extension pattern as `wal-subgraph-support`. Ordinals are fixed once shipped.
- [ ] Replay re-executes the reconstructed index transaction against the loaded snapshot, in commit
  order, after element replay — so index entries land against the correct (replayed) ids.
- [ ] Test (in the `WriteAheadLogTest` family): index elements, remove some, crash before a snapshot;
  load replays graph + index entries to a consistent state (scan returns exactly the live, indexed
  set). WAL-off default path unchanged.

## Measure & document
- [ ] Capture the benchmark deltas (`RemoveValue` cost, concurrent-read throughput) on this box and
  record them here.
- [ ] Update `features/index-lifecycle/README.md` (optional) with the index lifecycle contract:
  membership is derived state, valid only while the element is live; mutations are single-writer
  transactions; durability follows the WAL "as far as they are logged" rule.
- [ ] Confirm `IIndex`/`CanPersist` and all index save/load tests are green.

## Progress
- [ ] Phase 0 — characterization test + opt-in benchmark proving the stale read and the cost curves
- [ ] Phase 1 — `FilterLive` on all four index-serving read paths; characterization flipped
- [ ] Phase 2 — reverse map + writer-owned buckets across the dictionary-backed indices; O(affected keys)
- [ ] Phase 3 — removal purges registered indices on the single writer
- [ ] Phase 4 — index mutations as single-writer transactions; snapshot-publish reads; `AThreadSafeElement` retired (or RWLS fallback)
- [ ] Phase 5 — WAL entry types + codec for index writes; crash+replay consistency
- [ ] Measure & document

## Decision / revisit condition
This theme reopens a specific prior deferral and reuses one prior decision; it does not relitigate
either.

- **persistence-hardening Stage D (WAL)** explicitly deferred index logging: "indices are not logged;
  they rehydrate from the snapshot." Phase 5 narrows that deferral to *durable* index writes — but
  only after Phase 4 makes index mutations single-writer transactions (the precondition the WAL
  assumes for everything it logs). If Phase 4 is not taken (RWLS fallback), Phase 5 stays deferred and
  index durability remains snapshot-only, exactly as today. Reopen fully only once index writes are on
  the writer.
- **csr-adjacency (SKIPPED)** sanctioned exactly one future shape: a derived read-only snapshot. The
  snapshot-publish reads in Phase 4 apply that sanctioned shape to index buckets (immutable map + one
  volatile publish, the per-unit publication `csr-adjacency` calls the concurrency unit). This is a
  use of that decision, not a reopening of CSR adjacency.
- **non-blocking-save (DEFERRED, measured)** keeps the checkpoint write on the single writer. Routing
  index writes onto the writer (Phase 4) must not depend on moving the save off-worker and does not
  change the save-stall numbers; if a future large-graph theme revisits off-worker checkpointing, the
  index snapshot-publish here is compatible with it but does not require it.
