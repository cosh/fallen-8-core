# Persistence / Checkpointing Hardening — Plan

Companion to [spec.md](./spec.md). Correctness/durability first, then performance, then the WAL.

## Phase 1 — Stop the outages / corruption (S–M, do first)
- **C3** wrap `TryExecute` in try/catch in the worker (shared with correctness-fixes B6).
- **C1** write an id-space size covering `max(live Id)+1` (or force Trim on Save, or remap to a
  dense range); Load already trims after, so correct sizing suffices. Test: remove a low id → save
  → load.
- **P1** drop `Constants.BufferSize` from 100 MB to 64–256 KB.

## Phase 2 — Durable, self-describing format (M–L)
- **C2** write to a temp dir, fsync, atomic rename/swap; write a completion manifest **last**
  (expected sidecar names + sizes/CRC).
- **C4** prepend magic + `formatVersion` to the header and each sidecar; reject unknown versions;
  per-file CRC/hash validated on load (model on `DelegateJson`).
- **C5** validate every length prefix against `BytesRemaining` before allocating; guard null
  bunches; try/catch the load with per-sidecar error reporting.
- **C6** replace the N recipe sidecars with a single manifest (or clear `prefix*` before writing);
  temp+rename; tie recipes to the save via the manifest, not a directory scan.
- **C7** symmetric `OtherType` framing (read the length-prefixed string, then deserialize it).
- **C8** UTC + monotonic/GUID version suffix.

## Phase 3 — Format efficiency (S–M, behind the C4 version gate)
- **P2** UTF-32 → UTF-8, bulk read/write instead of per-byte loops, route string values +
  `EdgePropertyId` through the tokenized path. This subsumes **memory-footprint finding M5**
  (tokenize `EdgePropertyId` on save/load to dedupe ~30–40 B/edge on load): it was **explicitly
  deferred out of `memory-footprint` to here** because it changes the on-disk format and must land
  behind the **C4** `formatVersion` gate so old (untokenized) save files still load. Owner of M5 is
  now this theme.
- **N1 (from `memory-footprint`)** add a serialization-path property accessor so `Save` reads the
  raw sorted compact property store directly instead of building a fresh `ImmutableDictionary` per
  element via `GetAllProperties()` (a minor per-call allocation on the save + DTO-read paths).
  Deferred out of `memory-footprint` to here because emitting the sorted store changes the on-disk
  property **byte order** (ordinal vs. the old dictionary hash order) - load-compatible but a format
  change, so it must land behind the **C4** `formatVersion` gate (with M5/P2).
- **P7** var-int (`WriteOptimized`) for counts and small ids.
- **C9** full spatial (R-Tree) index Save/Load serialization: persist the tree (nodes/leaves +
  MBRs + the container map) and its build config (metric, min/max node counts, space/dimensions)
  so a reloaded spatial index is functional, replacing today's skip-on-checkpoint (`RTree.Save`/
  `Load` throw and the persistency guards drop the index). Behind the C4 version gate. Deferred
  here from correctness-fixes-followups B7. As part of this, replace the implicit
  `NotSupportedException` "not persistable" signal with an explicit `IIndex` capability flag (e.g.
  `CanPersist`): `PersistencyFactory` then skips a non-persistable index silently (Information) and
  reserves Error-level logging for genuine serialization failures, rather than classifying intent by
  catching a specific exception type.

## Phase 4 — Non-blocking, right-sized save (M)
- **P3** capture the immutable snapshot inside the tx (O(1)); perform file writing off the worker.
- **P6** partition count = `min(cores, ceil(count/targetChunk))` with a minimum chunk; pooled
  tasks, not `LongRunning`.
- **P5** build via `ImmutableList.CreateBuilder`/array-backed store; release old graph + source
  array early (aligns with core-storage-representation). **P8** sequential filtered copy for
  `GetAllGraphElements`.

## Phase 5 — Write-ahead log / incremental checkpoints (L, highest long-term leverage)
- Append each committed transaction's serialized effect to a log (fsync on commit); periodic full
  snapshots (the hardened Save); replay the log after the newest snapshot on load. Composes with
  Phase 2's atomic snapshot + manifest.

## Status
- [x] Phase 1 — worker try/catch (C3, already landed in correctness-fixes B6), id-space sizing (C1), buffer size (P1)
- [x] Phase 2 — atomic + versioned + integrity-checked format (C2/C4/C5), recipe manifest (C6), OtherType framing (C7), UTC/monotonic version suffix (C8)
- [x] Phase 3 — UTF-8 + tokenized values (P2, subsumes memory-footprint **M5**) + var-int (P7) + serialization-path property accessor (N1); full spatial (R-Tree) index serialization + `IIndex.CanPersist` (C9)
- [~] Phase 4 — right-sized partitioning + pooled tasks (P6, done), load-memory release (P5, done), `GetAllGraphElements` copy (P8, already satisfied by engine-performance P7); **non-blocking save (P3) DEFERRED** (correctness — see Stage C outcome)
- [ ] Phase 5 — WAL / incremental checkpoints

### Stage A outcome (Phases 1-2)

On-disk format is now **v2** and self-describing. Decision: **clean-reject** — every binary
checkpoint file starts with an 8-byte magic (`F8SAVE\0\0`) + little-endian `formatVersion`; a file
without the magic (any pre-existing/unversioned save) or with an unknown version is rejected on load
with a clear `InvalidDataException`, never dual-read or misparsed. Old (v1) save files are
intentionally not loadable.

- **C1** — `Save` writes an id-space size of `max(surviving Id)+1` (0 when empty) instead of the
  live element count, so a save taken after removing a low id (without Trim) round-trips.
- **P1** — `Constants.BufferSize` 100 MB → **64 KB** (below the ~85 KB LOH threshold, so per-stream
  buffers leave the LOH entirely).
- **C2/C4** — each checkpoint file is written to a unique temp name, fsync'd (`Flush(true)`) and
  atomically renamed; the main header (written **last**, atomic rename = commit point) embeds a
  completion manifest (every sidecar's name + byte size + CRC-32) and is itself protected by a
  trailing CRC-32. Load validates the manifest before trusting the save; a mid-save crash leaves no
  loadable header. Per-file CRC + magic/version validated on load.
- **C5** — every length prefix on the load path is validated against the bytes remaining before
  allocating (no `new byte[untrusted]`); a foreign/garbage file is rejected at the 12-byte preamble
  before the file is read into memory; null bunches are guarded; the parallel load's
  `AggregateException` is flattened into a clear per-file error. A rejected load rolls back cleanly
  (state restored) and surfaces as `RolledBack`/500 — the single writer survives.
- **C6** — the N `_subgraph_N` recipe files + directory-scan load are replaced by ONE versioned
  `_subgraphs` manifest, written atomically and read as a whole: no stale rehydration.
- **C7** — `OtherType` read is now symmetric (read the length-prefixed string, then deserialize).
- **C8** — version suffix is UTC + monotonic (`DateTime.UtcNow.ToBinary()` bumped via interlocked
  guard), replacing local, tick-colliding `DateTime.Now`.

Payload byte encoding (UTF-32 strings, property layout) is unchanged — Stage B (Phase 3) increments
`formatVersion` behind this gate when it changes the encoding.

### Stage B outcome (Phase 3)

The efficient payload encoding, folded into the **same** `formatVersion = 2` (Stage A's v2 never
shipped, so the whole theme ships as one "hardened + efficient" format; clean-reject means only the
current version loads). Save→load round-trips a graph **exactly** — Unicode/BMP/surrogate + empty
strings, every property value type, and property byte order all reconstruct faithfully.

- **P2** — string encoding is **UTF-8** (was UTF-32: 1 byte/ASCII char instead of 4), written and
  read in bulk. String property **VALUES** and the edge **`EdgePropertyId`** now travel the string
  **token table** (`WriteOptimized`/`ReadOptimizedString`) instead of an untokenized per-occurrence
  copy: `WriteObject` routes strings through the token path and `ProcessObject` decodes the token
  buckets symmetrically. A value/label/id that repeats within a bunch is stored once and referenced
  by a 1–4 byte token — verified to dedupe (e.g. a shared 106-char value across 300 elements: an
  ~8 KB bunch vs a ~32 KB untokenized lower bound) and to reload verbatim.
- **M5** (from memory-footprint) — **subsumed** by P2: `EdgePropertyId` is now tokenized, so N edges
  sharing an id (or sharing it with the vertices' edge-property keys already tokenized in the bunch)
  store it once and dedupe on load.
- **N1** (from memory-footprint) — `Save` reads the raw, ordinal-key-sorted compact property store
  directly via the new internal `AGraphElementModel.GetPropertyStoreForSerialization()` instead of
  materialising a throwaway `ImmutableDictionary` per element. This changes the on-disk property byte
  order to ordinal key order; the loader rebuilds and re-sorts the store, so any order round-trips
  (the memory-footprint fidelity suite stays green).
- **P7** — element ids, partition boundaries and per-element counts are **var-int** (`WriteVarInt32`
  / `ReadOptimizedInt32`) instead of fixed 4-byte ints. A new full-range `WriteVarInt32` is used (not
  the guarded `WriteOptimized(int)`, which faults above ~268 M) so a legitimately large id never
  throws; a new guarded `ReadOptimizedInt32Checked` extends the C5 length-prefix guard to the var-int
  counts that size collections. Dates stay fixed-width; the header stays fixed-width.
- **C9** — the spatial **R-Tree** index is now **fully persistable and reloads functional**,
  replacing the B7 skip-and-recreate. `Save` writes the build **config** (metric + dimension types,
  min/max node counts) and the **entries** ((point | MBR, elementId) pairs from the container map);
  `Load` re-`Initialize`s an empty tree from the config and re-inserts every entry, rebuilding an
  equivalent, **queryable** tree (region/point/distance/kNN queries run on the reloaded index — the
  exact thing B7 could not do). An explicit **`IIndex.CanPersist`** capability flag replaces the
  implicit `NotSupportedException`-from-`Save` signal: `PersistencyFactory.SaveIndex` skips a
  `CanPersist == false` index silently (Information) and reserves Error-level logging + partial-file
  cleanup for a genuine serialization failure of an index that claims it can persist. Every built-in
  index returns `CanPersist == true`.

Hard guardrails preserved: single-writer and the load-publish ordering (dense array →
`PublishLoadedGraphElements` → index/service rehydration) are unchanged; the encoding change stays
inside the versioned/CRC'd envelope, so clean-reject + integrity still hold and a truncated/garbage
file is still rejected without a large allocation (the guards now cover the new var-int counts too).
Deferred to later stages: non-blocking save + right-sized partitioning (Phase 4) and the WAL
(Phase 5).

### Stage C outcome (Phase 4): P6 + P5 + P8 landed; P3 deferred

Right-sizing, load-memory and the `GetAll` copy landed; the non-blocking save (P3) is **deferred**
on correctness grounds (a torn checkpoint is unacceptable; a blocking-but-correct save is kept). The
on-disk format is unchanged, so this stage does **not** bump `formatVersion` — it is a save/load
runtime change only.

- **P6 — right-sized partitioning + pooled tasks (DONE).** The save partition count was a fixed
  number, which is degenerate (one file per element) for a tiny graph and under-partitioned for a
  huge one. It is now `ComputePartitionCount(count, requestedCap)` =
  `min(cores, ceil(count / Constants.SaveTargetPartitionSize))`, clamped to ≥ 1 and never above an
  explicit positive caller request (`SaveTransaction.SavePartitions`). `SaveTargetPartitionSize`
  (16384) is the minimum useful chunk: a graph smaller than it is one bunch (no one-file-per-element);
  a larger graph is split only up to the core count (each bunch is a CPU-bound serialization job, so
  more files than cores adds I/O + manifest overhead without adding parallelism). The fan-out now uses
  **pooled** `Task.Run` instead of `LongRunning` tasks (which dedicate a brand-new thread each). The
  fan-out/fan-in correctness is unchanged: `CreatePartitions` still produces contiguous ranges that
  cover `[0, count)` exactly once, each element is written exactly once, and the load reconstructs by
  each element's own id — the partition boundaries are only a work split, never an id authority. An
  empty graph writes **0** bunches, a tiny graph exactly **1**; verified for tiny/empty/large. The
  benchmark confirms adaptivity (e.g. 200 000 elements on 16 cores → 13 bunches = `ceil(200000/16384)`,
  under the 16-core cap).
- **P5 — load memory released earlier (DONE, behaviour-preserving).** The dense, id-ordered source
  array the load builds is now confined to a `LoadAndPublishGraphElements` helper, so it goes out of
  scope — and is collectable — the instant it has been copied into the segmented master store, i.e.
  BEFORE the indices and services rehydrate, instead of being pinned for the whole load. And on the
  committed-success path, `Fallen8.Load_internal` drops its last reference to the **old** graph
  snapshot before the closing `Trim_internal` rebuilds the store, so the old graph, the new store and
  the trim's temporaries are not all held at once. Both changes are behaviour-preserving (same
  operations, same order, same rollback semantics — the old snapshot is released only after the load
  has committed and neither restore path can run). The load-publish ordering is intact.
- **P8 — `GetAllGraphElements` copy (ALREADY SATISFIED; noted).** The spec flagged
  `GetAllGraphElements` using unordered PLINQ for a linear filtered copy. Engine-performance's **P7**
  already converted every `GetAll*`/count path to a sequential, id-ordered scan
  (`LiveElementsSequential`); `Fallen8.GetAllGraphElements` is already a sequential filtered copy, so
  P8 needed no change. (The genuinely heavy user-predicate scan, `FindElements`/`GraphScan`, stays
  parallel by design — that is not what P8 was about.)
- **P3 — non-blocking save (DEFERRED; correctness).** The goal was to capture an immutable
  point-in-time snapshot inside the save transaction (O(1) via the segmented store) and perform the
  file WRITING off the single worker thread, so a long save does not block concurrent reads/writes.
  It is **deferred** because the whole checkpoint **cannot** be proven a consistent point-in-time
  snapshot while the writer keeps running, given today's model:
  1. **Element ids are mutated in place.** `Trim`/auto-`Trim` reassigns ids via
     `survivors[i].SetId(i)` on the very element objects a captured snapshot references (`SetId`
     writes the public `Id` field). A save writes `graphElement.Id` and, for edges,
     `edge.SourceVertex.Id`/`edge.TargetVertex.Id`. A concurrent Trim during an off-thread write would
     reassign those ids mid-write → vertex ids and edge-endpoint ids drawn from two different id
     spaces → **unloadable/corrupt** checkpoint.
  2. **Other element fields are mutated in place.** Removal flips `_removed` and detaches adjacency;
     `SetProperty`/`RemoveProperty`/`SetLabel` change `_removed`/`ModificationDate`/`Label`. (Property
     stores and `OutEdges`/`InEdges` are copy-on-write, so a *reader* that captured a reference is
     internally safe — but the values still change between the snapshot instant and the off-thread
     write instant, so the written element is not point-in-time consistent with the rest of the
     checkpoint.)
  3. **The index/service set is not O(1)-snapshottable.** `IndexFactory.Indices` /
     `ServiceFactory.Services` are plain dictionaries guarded by `AThreadSafeElement`; membership is
     mutated by concurrent index/service transactions and **replaced wholesale** by `DeleteAllIndices`
     and by `Load` (which swaps the whole factory). Iterating them off-thread without the read lock
     races those swaps; and even capturing the list of index instances does not freeze their
     **contents** — an index saved at a later instant can reference element ids created after the
     element snapshot (`idSpaceSize`), which the load then cannot resolve → index inconsistent with the
     persisted elements. Making indices/services immutably snapshottable in O(1) is a large new
     contract across every plugin (Dictionary/Range/Fulltext/Spatial), out of Phase-4 scope.
  4. **A concurrent Load/Trim/second-save** would swap `_snapshot`/`IndexFactory`/`ServiceFactory` and
     tear down the old ones under an in-flight off-thread writer.

  The only ways to make P3 safe today are all either blocking (a read/write lock around the whole
  save — defeats the availability goal) or a large, risky change (element-level copy-on-write for
  ids/flags, or an O(n) deep-copy DTO per element — not O(1), and outside Phase 4). Per the
  stop-at-safe-boundary guardrail, the file writing therefore **stays on the single worker thread**
  (as before). Because the writer is the only mutator, the save already sees a fully consistent
  point-in-time image, and `WaitUntilFinished` / the `/save` REST result still report the true outcome:
  `TryExecute` runs the entire save synchronously and only returns `true` after all bytes are durably
  committed (temp + fsync + atomic rename); a write failure throws, the worker maps it to
  `RolledBack` + `Error`, and the REST layer maps that to 500. A regression test
  (`SingleWriter_SavesInterleavedWithMutations_YieldConsistentCheckpoints`) drives 4 threads
  interleaving vertex/edge creation with saves and asserts every produced checkpoint loads to a
  self-consistent graph (persisted counts match the rehydrated elements; every edge resolves both
  endpoints) — this pins the invariant we rely on and would fail if the write were later moved
  off-thread without first making the whole checkpoint immutable.

All prior-stage guarantees remain intact: **single-writer** (the fan-out tasks are pooled but the
worker still blocks on them, so the save fully completes on the worker before the transaction
returns), **atomicity/integrity/clean-reject** (Stage A — the header + manifest + CRC envelope and
the temp+fsync+rename commit are untouched), and the **encoding round-trip** (Stage B — the payload
bytes are unchanged). Save/Load/Trim stay correct and the worker still survives a faulting save.

**Remaining for Stage D (Phase 5 — WAL / incremental checkpoints):** append each committed
transaction's serialized effect to a write-ahead log (fsync on commit), take periodic full snapshots
(the hardened Save), and replay the log after the newest snapshot on load, so a crash between
snapshots recovers committed transactions. This composes with Phase 2's atomic snapshot + manifest.
(Genuine off-thread/non-blocking checkpointing would most naturally ride on the WAL — the log gives
durability while a background snapshot runs — or on a future element-level immutable-snapshot model;
either is a larger effort than Phase 4 and is explicitly left to a later theme.)
