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
- [ ] Phase 4 — non-blocking save + partitioning + load memory
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
