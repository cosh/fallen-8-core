# Core Storage Representation — Plan

Companion to [spec.md](./spec.md). Staged so value lands incrementally and risk is bounded.
Do the `correctness-fixes` theme first (the immutable-return-value bugs share this root cause).

## Phase 0 — Baseline & guardrails
- Add microbenchmarks (BenchmarkDotNet or a bench harness) for: id lookup, single insert, bulk
  insert, full scan, edge wire-up. Capture current numbers.
- Add concurrency tests: many concurrent readers during single-writer appends/removes assert
  snapshot stability (no torn reads, no `IndexOutOfRange`).

## Phase 1 — Snapshot-stability & volatile (low risk, immediate)
- Capture `_graphElements` once per read method; mark the holder `volatile`. Fixes the
  double-read window (`Fallen8.cs:239/247`, `259/267`, `280/288`) independent of the storage swap.

## Phase 2 — Master store → array-backed (interim: ImmutableArray)
- Switch the master collection and the bulk/`Load` paths to `ImmutableArray<T>` + `Builder`
  (O(1) indexer, cache-friendly scans) as a low-risk first move; single-appends coalesced by the
  transaction batching from `engine-performance`.

## Phase 3 — Master store → segmented append-only array
- Replace with the segmented-array `Snapshot` holder + atomic publish. Verify id == index and
  `Trim` compaction still hold. Re-run Phase 0 benchmarks.

## Phase 4 — Adjacency flattening
- Replace `ImmutableDictionary<string, ImmutableList<EdgeModel>>` (`VertexModel.cs:45,50`) with
  per-direction `List<EdgeModel>`/arrays (+ optional lazy type grouping). Update `AddOutEdge`/
  `AddIncomingEdge` and the `Load` edge fix-up (`PersistencyFactory.cs:619,623`). This also
  eliminates the per-edge immutable-churn (`memory-footprint` #7).

## Phase 5 — Measure & document
- Re-run benchmarks + a large-graph memory measurement; record the before/after in this plan.
  Update `engine-performance` and `memory-footprint` notes that depend on this.

## Status
- [ ] Phase 0 — benchmarks + concurrency tests
- [ ] Phase 1 — snapshot capture + volatile
- [ ] Phase 2 — ImmutableArray interim
- [ ] Phase 3 — segmented array master store
- [ ] Phase 4 — adjacency flattening
- [ ] Phase 5 — measure & document
