# Load-Path Integrity — Plan

Companion to [spec.md](./spec.md). P0 correctness: prove each defect with a guard first, then fix
the smallest thing that closes it, keeping the `persistence-hardening` envelope byte-for-byte intact.

GitHub issue: to be opened (label: feature). Feature branch: `feature/load-path-integrity`.

## Phase 0 — Baseline & guardrails

Intent: land tests that fail (or flake) on today's code and pass deterministically after the fix, so
the regression can never silently return. These are correctness guards (part of the default suite),
not perf benchmarks; only the large-scale stress variant is opt-in per repo convention.

- [ ] **L1 stress** — new `LoadPathIntegrityTest` (MSTest, arrange/act/assert,
  `TestLoggerFactory.Create()`): build a graph large enough to save into **many bunches** where
  edges **systematically span bunches** (e.g. `v[k] --e--> v[k + stride]` with `stride` chosen so
  source and target fall in different partitions), save, load, and assert adjacency is **exactly**
  correct — every out/in edge present exactly once, no duplicates, correct endpoints, counts match.
  Wrap the save/load in a loop (N iterations) so the race surfaces on today's code.
- [ ] **L1 heavy (opt-in)** — a higher-bunch-count / higher-iteration variant marked
  `[TestCategory("Benchmark")]` + `[Ignore("Opt-in load-race stress; not part of the default suite.")]`
  to hammer the race harder when investigating locally.
- [ ] **L2 overflow probe** — a `SerializationWriter` test that drives the public `UpdateHeader`
  through a stream reporting `Position - startPosition > Int32.MaxValue` (a small fake `Stream` that
  reports a huge `Position` without allocating), asserting today's code writes a wrapped/negative
  length (baseline) — this assertion flips to "throws" after Phase 2.
- [ ] **L3 crafted-manifest probe** — build an otherwise CRC-consistent header whose manifest `count`
  is absurd (e.g. `Int32.MaxValue`) and assert the load path's manifest read is the offender
  (baseline: large allocation / slow; post-fix: fast `InvalidDataException`). Reuse the
  `persistence-hardening` corrupt-header test helpers if present.

## Phase 1 — L1: lock-free cross-bunch edge fix-up

Intent: remove the race in the rehydration scratch structure without touching the format or the live
graph model.

- [ ] Change `edgeTodo` to `ConcurrentDictionary<Int32, ConcurrentQueue<EdgeOnVertexToDo>>` in
  `LoadGraphElementsCore` (`PersistencyFactory.cs:953`) and thread the new value type through
  `LoadAGraphElementBunch` (`:741`) and `LoadVertex` (`:1129`) signatures.
- [ ] Replace both `AddOrUpdate` sites (`:1190` out-edges, `:1237` in-edges) with
  `edgeTodo.GetOrAdd(edgeId, _ => new ConcurrentQueue<EdgeOnVertexToDo>()).Enqueue(aEdgeTodo);`.
- [ ] Leave the sequential consumer loop (`:996`–`1025`) as-is — `foreach (var aTodo in aKV.Value)`
  over a `ConcurrentQueue` is a safe FIFO snapshot; the fix-up effect is order-independent.
- [ ] Confirm the Phase 0 L1 stress now passes deterministically across all iterations.

## Phase 2 — L2: fail loud on an oversized checkpoint segment

Intent: a checkpoint that cannot be loaded must never be committed; the save fails instead.

- [ ] In `SerializationWriter.UpdateHeader` (`:2139`) compute the length as `long` and **throw**
  (`InvalidDataException`/`IOException`, clear message about the 2 GB per-file limit) when it exceeds
  `int.MaxValue`, instead of the unchecked `(int)` cast at `:2141`.
- [ ] Verify the throw propagates cleanly: `WriteSidecar` (`:673`–`698`) deletes the temp and
  rethrows; the header/rename commit (`:400`–`431`) never runs; `SaveTransaction.TryExecute`
  surfaces the failure → `RolledBack` + `Error` → REST 500. No committed checkpoint, previous
  savegame intact.
- [ ] Stop the default from capping partitions: treat `SaveTransaction.SavePartitions` (`:39`,
  default `5`) as opt-in — a non-positive/unset request lets `ComputePartitionCount`
  (`PersistencyFactory.cs:1040`) size by work+cores. Keep an explicit positive request honoured
  (the `persistence-hardening` "explicit SavePartitions = 1 honoured" test must still pass).
- [ ] Flip the Phase 0 L2 probe assertion to "throws"; add an integration assert that a failed save
  leaves no committed checkpoint.

## Phase 3 — L3: bound the manifest count before allocating

Intent: extend the existing length-guard discipline to the one spot that escaped it.

- [ ] In `ReadManifestList` (`PersistencyFactory.cs:204`) add, before `new List<…>(count)` (`:212`),
  a bound: reject `count > reader.BytesRemaining / MinManifestEntrySize` with `InvalidDataException`
  (keep the `count < 0` guard). Define `MinManifestEntrySize = 13` (`Int64` size + `UInt32` CRC +
  ≥ 1-byte name prefix) as a named constant next to `WriteManifestList` / `SidecarManifestEntry`, and
  handle the `BytesRemaining` unknown-length sentinel defensively.
- [ ] Correct the doc-comment at `:200`–`:202` to describe the actual bound (it currently overstates
  the safety).
- [ ] Confirm the Phase 0 L3 probe now throws fast, before the large allocation.

## Measure & document

- [ ] Run the full suite (`dotnet test fallen-8-core.sln`) plus the `persistence-hardening`,
  `wal-subgraph-support` and partitioning round-trip tests — all green; `formatVersion` unchanged.
- [ ] Capture the L1 stress iteration count / bunch count used and its runtime **on this box**, and
  note the partitioning-default change's effect on default-save bunch counts, in the PR description.
- [ ] Update `features/load-path-integrity/` with the outcome (a short "Outcome" note mirroring the
  reference features) and cross-link `persistence-hardening`.

## Progress

- [ ] Phase 0 — baseline & guardrails (L1 stress + opt-in heavy, L2 overflow probe, L3 crafted-manifest probe)
- [ ] Phase 1 — L1 lock-free `ConcurrentQueue` edge fix-up
- [ ] Phase 2 — L2 fail-loud `UpdateHeader` + `SavePartitions` default loosened
- [ ] Phase 3 — L3 manifest count bound + doc-comment fix
- [ ] Measure & document

## Decision / revisit condition

- **Int64 length header (long-term, deferred here).** The minimum L2 fix makes an oversized segment
  fail the save; it does **not** raise the 2 GB per-file limit. Widening the internal length header
  from `Int32` to `Int64` would change the on-disk layout and must ride a `persistence-hardening`
  `formatVersion` bump (do not re-propose the format itself — see that feature). **Revisit** only if
  a real workload legitimately needs a single bunch > 2 GB despite work+cores partitioning and
  by-bytes splitting — otherwise the loud-failure guard is sufficient and the layout stays put.
- **Off-worker / non-blocking save** stays as decided in `non-blocking-save` (measured, deferred);
  this theme does not move the write off the single worker. **CSR adjacency** stays SKIPPED per
  `csr-adjacency`. Neither is reopened by this work.
