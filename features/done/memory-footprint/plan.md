# Memory Footprint — Plan

Companion to [spec.md](./spec.md). Findings M1–M6 defined there.

## Phase 1 — Quick wins (S, low risk)
- **M2 (done)** intern labels/property-keys/`EdgePropertyId` on the create + `SetProperty` paths.
  A `ConcurrentDictionary<string,string>` intern table on `Fallen8`; keys of incoming property maps
  are interned when the element is built.
- **M3 (done)** release each transaction's heavy **input** definition at the terminal state
  (`Finished` **or** `RolledBack`) via a new `ATransaction.ReleaseAfterCompletion()`, instead of
  waiting for `Trim`. The `TransactionInformation` entry, its terminal state and its captured
  `Error` stay in place (B6 observability intact), and the captured **created-models**
  (`GetCreatedVertices()`/`VertexCreated`/`GetCreatedEdges()`) are preserved for a waited-on caller
  and dropped only at `Trim`. The BFS subgraph algorithm was fixed to capture its edge count before
  waiting (it used to read the now-released input list post-commit). **Usage:** read the
  created-models promptly after `WaitUntilFinished()` — they are dropped at the next `Trim`,
  **including M4's auto-trim**, after which the handles read empty.
- **M5 (DEFERRED to `persistence-hardening`)** — tokenizing `EdgePropertyId` in the serializer is
  an on-disk **format** change; done unversioned it would break loading old save files. Moved to
  `persistence-hardening` to land behind that theme's format versioning. Recorded in
  `features/persistence-hardening/plan.md`.
- **M6 (done)** default `SubGraphQuota` is now generous-but-bounded (1024 subgraphs / 10M elements
  per subgraph / 25M total) instead of `Int32.MaxValue`.

## Phase 2 — Removed-element reclamation (S–M)
- **M4 (done)** auto-**Trim** past a tombstone threshold, triggered only **after** a committed
  element-removal transaction, on the single writer thread — never inside `TryExecute`, so the
  removal rollback path (which restores adjacency/counts) is untouched, and it reuses the existing
  rollback-agnostic, reader-safe `Trim`. Heavy fields are **not** freed on the removal path itself
  (that would break rollback and race lock-free readers). Threshold defaults to a conservative
  100k reclaimable slots (no ordinary workload or existing test triggers it). `MemoryFootprintTest`
  is the soak/churn test asserting bounded growth.

## Phase 3 — Property storage compaction (M)
- **M1 (done)** replaced per-element `ImmutableDictionary<string,object>` with a compact, key-sorted
  `KeyValuePair<string,object>[]` (binary-searched), and de-boxed common value types (shared boxes
  for booleans and small integers). Public surface preserved: `GetAllProperties()` still returns an
  `ImmutableDictionary` snapshot (built on demand), `TryGetProperty<T>`/`GetPropertyCount()`
  unchanged. **Public-surface check:** the property field is private-behind-accessors (unlike the
  public `VertexModel.OutEdges`), so M1 proceeded; the one test that reflected the field's concrete
  type now reads through the public accessor. Guarded by `PropertyStoreFidelityTest`.

## Measurement

Harness: `fallen-8-unittest/MemoryFootprintBenchmark.cs` (`[TestCategory("Benchmark")]` **and**
`[Ignore]`, so it is excluded from the default `dotnet test`; run it explicitly, like
`StorageBenchmark`). The memory metric is **managed-heap retained** via `GC.GetTotalMemory(true)`
after a forced blocking GC (NOT process RSS / working set); allocations via
`GC.GetTotalAllocatedBytes(true)`. A full 1M/10M run is impractical in the test environment, so the
harness uses a **200k-vertex / 200k-edge** property graph (mixed-typed properties, repeated
labels/keys) — the per-element numbers scale linearly. Numbers are real, captured before
(merge-base) and after (M1+M2+M3+M4+M6) on this machine (net10.0):

| Metric (200k V + 200k E) | Before | After | Δ |
|---|---|---|---|
| Per-vertex, 4 properties (retained) | 877.1 B | 244.6 B | **−72%** |
| Per-vertex property-container overhead | 744.1 B | 162.0 B | **−78%** |
| Per-edge, 1 property (retained) | 760.4 B | 388.3 B | **−49%** |
| Whole graph retained (managed heap, not RSS) | 312.3 MB | 120.7 MB | **−61%** |
| Bulk-insert alloc, vertices (transient) | 214.8 MB | 228.1 MB | +6% |
| Bulk-insert alloc, edges (transient) | 252.7 MB | 270.0 MB | +7% |

The retained drop is the M1 (compact store + de-boxing) and M2 (key/label interning) win. The
small rise in *transient* allocation is M2's interned-key copy of each incoming property map — an
acceptable trade for a footprint theme (it does not affect retained/steady-state memory). M3's
effect is on peak retention during bulk inserts (input released at commit rather than at `Trim`),
and M4's is the bounded churn behaviour asserted by `MemoryFootprintTest`; both are lifecycle wins
not captured by the single-snapshot retained numbers above.

Reproduce:
```
F8_MEM_LABEL=before dotnet test --filter "FullyQualifiedName~MemoryFootprintBenchmark"   # at merge-base
F8_MEM_LABEL=after  dotnet test --filter "FullyQualifiedName~MemoryFootprintBenchmark"   # on this branch
# results appended to <temp>/fallen8-memory-footprint.txt
```

## Status
- [x] Phase 1 — interning (M2), tx input release (M3), default quota (M6). **M5 deferred** to
  `persistence-hardening` (format-versioned).
- [x] Phase 2 — removed-element reclamation via post-commit auto-trim (M4) + soak test.
- [x] Phase 3 — property storage compaction + de-boxing (M1) + fidelity tests.
- Build: 0 warnings / 0 errors. Tests: default suite green — 268 passed, 2 benchmark tests skipped
  (now `[Ignore]`; opt-in, run explicitly).
