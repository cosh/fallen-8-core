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
- [ ] Phase 1 — worker try/catch, id-space sizing, buffer size
- [ ] Phase 2 — atomic + versioned + integrity-checked format, recipe manifest, OtherType framing
- [ ] Phase 3 — UTF-8 + tokenized values + var-int; full spatial (R-Tree) index serialization (C9)
- [ ] Phase 4 — non-blocking save + partitioning + load memory
- [ ] Phase 5 — WAL / incremental checkpoints
