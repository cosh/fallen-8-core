# Persistence / Checkpointing Hardening — Specification

> **Status:** Planned. Durability, robustness, and efficiency improvements to the checkpoint
> layer, from the persistence review. Correctness/durability items take priority over the
> performance items.

## 1. How it works today (verified)

`PersistencyFactory.Save` writes a header file (Guid + element count + sidecar filename lists) and
fans graph elements into `savePartitions` sidecar "bunch" files, one file per index and per
service, via parallel `LongRunning` tasks; subgraph recipes are separate `_subgraph_N` JSON
sidecars. `Load` reads the header, allocates `AGraphElementModel[currentId]`, reloads bunches in
parallel by each element's `.Id`, resolves cross-bunch edges, then `ToImmutableList()`s. Save,
Load, and Trim are separate transactions on the single worker; `/save` does **not** trim first.

## 2. Correctness / robustness (priority)

| # | Issue | Location | Effect |
|---|-------|----------|--------|
| C1 | Save writes the **live element count** as the id-space size, but removal leaves id gaps; a surviving `.Id >= liveCount` makes Load's `graphElements[id] = …` throw | `PersistencyFactory.cs:182,96,828` | Save-after-remove-without-Trim → unloadable/corrupt savegame (data-dependent) |
| C2 | No save atomicity: header + sidecars written in place, no temp+rename, no completion manifest | `PersistencyFactory.cs:166` | Crash mid-save → header referencing missing/truncated sidecars, or silent empty load |
| C3 | Worker has no try/catch around `TryExecute`; a Save/Load throw faults the task and **kills the single worker** | `Transaction/TransactionManager.cs:87` | One bad save bricks the DB (shared with correctness-fixes B6) |
| C4 | No magic number / format version / checksum in the core binary format (contrast: `DelegateJson` has version + optional SHA-256) | `SerializationWriter.cs:234`; `PersistencyFactory.cs:178` | Format changes / foreign files / bit-rot misparse silently |
| C5 | Corrupt input not handled: `new byte[counter]` with an unvalidated length; null bunch → NRE; loader surfaces `AggregateException` | `SerializationReader.cs:215`; `PersistencyFactory.cs:580,574` | Corrupt/malicious file → huge allocation / crash (DoS) |
| C6 | Recipe sidecars: counter restarts at 0 each save, so a save with fewer recipes leaves stale higher-numbered files that the directory-scan load rehydrates; non-atomic `WriteAllText`; prefix-glob discovery is unscoped | `PersistencyFactory.cs:277,303` | Stale/rehydrated or silently-dropped subgraphs |
| C7 | `OtherType` JSON fallback: writer emits a length-prefixed UTF-32 string, reader hands the raw stream to `JsonSerializer.Deserialize<object>` | `SerializationWriter.cs:606` vs `SerializationReader.cs:1582` | Complex (non-primitive) property values not round-trippable |
| C8 | Version suffix uses `DateTime.Now` (local, DST-sensitive) and collides within a tick | `PersistencyFactory.cs:169` | Fragile save versioning |
| C9 | Spatial (R-Tree) index is not serialized: `RTree.Save`/`Load` throw `NotSupportedException`, so the checkpoint guards deliberately skip it (deferred here from correctness-fixes-followups B7) | `Index/Spatial/Implementation/RTree/RTree.cs` (`Save`/`Load`) | A spatial index does not survive a save/load — it is absent after load and must be recreated by the caller |

> **C9 follow-up (capability flag):** today `PersistencyFactory.SaveIndex` distinguishes "not yet
> persistable" from a genuine serialization failure by catching `NotSupportedException` specifically
> (Information-level skip) vs any other exception (Error-level skip). That is an implicit,
> exception-typed signal. Replace it with an explicit `IIndex` capability flag (e.g. `CanPersist`):
> the factory can then skip a non-persistable index *silently* and reserve Error-level logging (and
> partial-sidecar cleanup) for real failures, without relying on a thrown exception type to classify
> intent. Fold this in when C9's full R-Tree serialization lands.

## 3. Performance / memory / design

| # | Issue | Location | Impact |
|---|-------|----------|--------|
| P1 | 100 MB FileStream buffer per file × parallel writers ≈ 0.7–0.9 GB LOH during a save | `Helper/Constants.cs:36`; `PersistencyFactory.cs:175,364,391,527` | High — drop to 64–256 KB |
| P2 | Strings encoded UTF-32, byte-at-a-time; string **values** and `EdgePropertyId` not tokenized | `SerializationWriter.cs:342`; `SerializationReader.cs:213`; `PersistencyFactory.cs:955` | High — UTF-8 + bulk I/O + tokenization |
| P3 | Full serialization runs **inside** the single worker → whole DB blocked for the save duration, though `_graphElements` is already an O(1) snapshot | `TransactionManager.cs:55`; `SaveTransaction.cs:65` | Med — capture snapshot in-tx, write outside the worker |
| P4 | Full-snapshot-only; no WAL; a crash between saves loses everything since the last snapshot | — | Med — biggest long-term lever |
| P5 | Load holds old graph + dense array + new immutable tree simultaneously | `PersistencyFactory.cs:98,115`; `Fallen8.cs:852` | Med — release earlier / array-backed store |
| P6 | Partitioning is fixed-count: degenerate (one file/element) for tiny graphs, under-partitioned for huge ones; `LongRunning` tasks | `PersistencyFactory.cs:645,185` | Med — size partitions to work + cores |
| P7 | Fixed-width 4-byte ints for counts / ids where var-int (`WriteOptimized`, already present) would win | `PersistencyFactory.cs:683,847,956` | Low-Med |
| P8 | `GetAllGraphElements` uses unordered PLINQ for a linear filtered copy | `Fallen8.cs:1108` | Low |

## 4. Goals / non-goals

**Goals:** a checkpoint that is atomic (all-or-nothing), self-describing (magic + version +
integrity), defensive against corrupt/truncated input, non-blocking during the write, and
efficient (right-sized buffers, UTF-8 + tokenized strings, sane partitioning). Introduce a
write-ahead log for durability between snapshots.

**Non-goals:** distributed/replicated persistence; changing the transaction model.

## 5. Acceptance criteria

- Save-after-remove (no Trim) round-trips correctly (C1). A crash injected mid-save never yields a
  loadable-but-wrong graph; load validates a completion manifest + per-file integrity and fails
  loudly on mismatch (C2, C4). A truncated/garbage file is rejected without a huge allocation or
  worker death (C3, C5). Recipe sidecars can't rehydrate stale entries (C6). Complex property
  values round-trip (C7).
- Save peak memory drops (P1); save no longer stalls concurrent reads/writes (P3); large-graph
  save/load throughput improves (P2, P6) — measured.
- WAL: after a crash between snapshots, load replays the log and recovers committed transactions.
- On-disk format changes are gated behind the new version field; existing tests + new
  crash/corruption tests pass.

## 6. Keep (solid today)

The var-int core (`Read/Write7BitEncoded*`), `BitVector32` date/time packing, the string token
table + duplicate-run compression, consistent little-endian encoding, and `DelegateJson`'s
version/CRC discipline (copy it to the core format for C4).
