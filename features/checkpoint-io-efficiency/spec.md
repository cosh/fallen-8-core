# Checkpoint I/O Efficiency — Specification

> **Status:** Planned — **P2 performance** — from the 2026-07 principal-architect & performance
> review. Make checkpoint I/O single-pass and SIMD: stop re-reading every sidecar just to CRC it on
> save, fold the CRC check into the single load parse instead of a separate whole-file pass, and
> replace the byte-at-a-time CRC-32 with a hardware-accelerated one — halving the bytes moved on both
> save and load while keeping the exact same on-disk envelope, manifest and CRC coverage.

## 1. Problem / current state

The hardened checkpoint (`persistence-hardening`: atomic temp+fsync+rename, completion manifest,
per-file CRC-32, magic+`formatVersion`, clean-reject) is correct and durable, but it moves each
sidecar's bytes **through the CPU twice** on both the save and the load side, with a CRC that runs
one byte per loop iteration.

- **Save re-reads every sidecar it just wrote.** `WriteSidecar` streams the preamble + content to a
  temp `FileStream`, `Flush(true)`s it, and then calls
  `PersistenceFormat.ComputeFileCrc(temp, out var size)` (`PersistencyFactory.cs:690`), which
  **re-opens the just-written file and reads it end-to-end again** to compute the CRC-32 for the
  manifest entry (`PersistenceFormat.cs:180-186`). Every byte of every bunch, index and service
  sidecar is therefore written once and read back once. That read-back runs on the single
  transaction-writer thread, **inside** the save's writer-hold window (see §6 and
  `non-blocking-save`).

- **Load reads every sidecar twice: once to CRC, once to parse.** On load, each mandatory bunch is
  first fully streamed to compute its CRC in `ValidateSidecar` (`PersistenceFormat.cs:142-175`, the
  whole-file `Crc32.Compute(stream, …)` at `:166`), invoked at `PersistencyFactory.cs:154-157`; the
  bunch is **then re-opened and parsed** in `LoadAGraphElementBunch`
  (`PersistencyFactory.cs:754`). Index and service sidecars take the same validate-then-parse
  double read (`ValidateOptionalSidecars` → `LoadAService`/`LoadAnIndex` at
  `PersistencyFactory.cs:834,855`).

- **The parse opens use the default 4 KB `FileStream` buffer and no sequential hint.** The three
  parse opens — `PersistencyFactory.cs:754` (bunch), `:834` (service), `:855` (index) — call
  `File.Open(path, FileMode.Open, FileAccess.Read)`, which uses the framework-default 4 KB buffer
  and `FileOptions.None`. The integrity passes (`ValidateSidecar`, `ComputeFileCrc`) already open
  with `Constants.BufferSize` (64 KB) + `FileOptions.SequentialScan`
  (`PersistenceFormat.cs:159,182`), so only the parse opens are under-buffered.

- **The CRC-32 is a byte-at-a-time table loop.** `Crc32.Update` walks the buffer one byte per
  iteration (`Crc32.cs:61-69`), and the streaming helper feeds it 64 KB at a time
  (`Crc32.cs:87-100`). A scalar table CRC runs at roughly 0.3–0.5 GB/s; the reflected-IEEE CRC has
  a hardware-accelerated implementation an order of magnitude faster. Because the CRC is computed
  over every sidecar on both save (the re-read) and load (the validate pass), it is on the critical
  path of both.

### Already addressed by `persistence-hardening` — NOT part of this theme

Verified against the current tree; the review brief listed these as residuals but the code already
fixes them, so this spec does **not** re-propose them:

- The 100 MB `FileStream` buffer is **gone**: `Constants.BufferSize` is **64 KB**
  (`Constants.cs:43`), below the LOH threshold. (finding P1, landed)
- Partitioning is **already work+cores adaptive**, not a fixed count:
  `ComputePartitionCount = min(cores, ceil(count / SaveTargetPartitionSize))`
  (`PersistencyFactory.cs:1040-1058`, `Constants.SaveTargetPartitionSize = 16384`,
  `Constants.cs:97`). The old one-file-per-element degeneracy is gone. (finding P6, landed)
- Ids, counts and partition boundaries are **already var-int** (`WriteVarInt32` /
  `ReadOptimizedInt32`/`…Checked`, e.g. `PersistencyFactory.cs:882,1104,1115,1287,1388`); the
  brief's fixed-4-byte-int line references (~683/847/956) are stale. (finding P7, landed)

The one partitioning item that remains — stopping the default `SavePartitions = 5` from acting as a
hard ceiling and guarding a single bunch against exceeding 2 GB — is owned by **`load-path-integrity`
(L2)**, not this theme. This spec does not reopen it and only needs its per-bunch size to stay
bounded (see §5).

## 2. Goals / non-goals

**Goals**

- **Save is single-pass.** A sidecar's bytes are produced once and its CRC-32 is computed from those
  same bytes without re-reading the file. Remove the `ComputeFileCrc` read-back from `WriteSidecar`.
- **Load is single-pass for the mandatory bunches.** The per-bunch CRC-32 is accumulated **during
  the parse** and checked at end-of-file, instead of a separate whole-file validate pass followed by
  a re-open. The cheap O(1) length check and the preamble/version gate stay **before** any large
  allocation.
- **CRC-32 is hardware-accelerated.** Replace the byte-at-a-time table loop with
  `System.IO.Hashing.Crc32` (same reflected-IEEE polynomial, so the produced value is **byte-for-byte
  identical** and existing checkpoints still validate — no `formatVersion` bump).
- **Parse opens are right-sized.** The bunch/index/service parse opens use `Constants.BufferSize` +
  `FileOptions.SequentialScan`, matching the integrity opens.
- **The win is measured, not assumed.** Re-run `NonBlockingSaveBenchmark` and record the (smaller)
  save writer-hold numbers; add a load-side + bytes-moved measurement showing I/O roughly halved and
  CRC throughput up an order of magnitude.

**Non-goals**

- Changing the on-disk layout, the envelope (magic + `formatVersion` + trailing/per-file CRC), the
  completion manifest, or the CRC's coverage (still the **whole** file, preamble included). No
  `formatVersion` bump.
- Changing the transaction/concurrency model. Single writer; mutations through transactions; reads
  lock-free over the volatile snapshot; the file writing stays **on** the worker thread — the
  off-worker save is MEASURED and DEFERRED in `non-blocking-save` and is **not** reopened here (this
  theme only *shrinks* that same stall — see §6).
- The `load-path-integrity` correctness fixes (L1 edge-todo race, L2 >2 GB bunch guard + partition
  default, L3 manifest preallocation) — a separate, higher-priority theme this one composes with.
- A derived read-only CSR adjacency snapshot — ASSESSED and SKIPPED in `csr-adjacency`; not reopened.
- Widening the string-table/tokenization scheme or any payload encoding (that shipped in
  `persistence-hardening` Stage B).

## 3. Design sketch

### 3.1 SIMD CRC-32 (`System.IO.Hashing.Crc32`), byte-compatible

Add the `System.IO.Hashing` package (10.0.x, matching the repo's other package versions) and back
`Crc32` with `System.IO.Hashing.Crc32`, which computes CRC-32/ISO-HDLC — reflected input/output,
polynomial `0xEDB88320`, init `0xFFFFFFFF`, final XOR `0xFFFFFFFF` — **exactly** the parameters the
hand-rolled table uses (`Crc32.cs:40-58,72-74`). So the emitted `uint` is identical and every
existing checkpoint keeps validating; this is a pure throughput swap, not a format change.

Keep the `Crc32` facade (its call sites in `PersistencyFactory`/`PersistenceFormat` are stable) and
re-implement it over the accelerated primitive:

- One-shot over an in-memory buffer: `System.IO.Hashing.Crc32.HashToUInt32(ReadOnlySpan<byte>)`
  (replaces `Compute(byte[], offset, count)` — used by the header at `PersistencyFactory.cs:421`
  and the load header check at `:119`).
- Incremental: a small `Crc32Hasher` wrapping an instance `System.IO.Hashing.Crc32` with
  `Append(ReadOnlySpan<byte>)` + `GetCurrentHashAsUInt32()`, for the streaming/tee paths below.
- The stream helper `Compute(Stream, out long length)` stays (it is the safe bounded-buffer reader),
  but now `Append`s each 64 KB chunk to the accelerated hasher.

A **characterization test** (Phase 0) pins the equivalence over representative buffers (empty, short
ASCII, binary, > 64 KB) and confirms a checkpoint written before the swap still loads after it, so
the swap cannot silently change a checksum.

### 3.2 Save: CRC from the finalized image, no read-back

**Code-reality adaptation (differs from the brief's "forward-only CRC-tee").** The brief pictured a
thin wrapper stream that CRCs each byte as it is written sequentially. That is not directly viable
here: `SerializationWriter` requires a **seekable** stream and, in `UpdateHeader`
(`SerializationWriter.cs:2139-2158`), **seeks back** to `startPosition` to patch its 12-byte
length/token-count header, then seeks forward again (the writer even writes a placeholder header in
its constructor, `:233-259`). A forward-only running CRC would checksum the placeholder bytes and
then double-count the patched region — wrong CRC. CRC-32 is order-dependent, so the patched prefix
cannot be spliced in afterwards without a `crc_combine` primitive `System.IO.Hashing` does not
expose.

Instead, mirror what the **header commit already does** and which is exemplary: `Save` builds the
main header in a `MemoryStream`, patches it (`UpdateHeader` in RAM), CRCs `GetBuffer()` in one
in-memory pass and writes it once via `WriteAllBytesDurably` (`PersistencyFactory.cs:407-431`, CRC
at `:421`). Extend that shape to `WriteSidecar`:

1. Serialize the sidecar image (preamble + `SerializationWriter` body) into a `MemoryStream`; the
   seek-back header patch happens **in RAM** (free).
2. Compute the CRC-32 in **one** pass over the finalized buffer
   (`Crc32.Compute(buffer, 0, length)` → `System.IO.Hashing.Crc32.HashToUInt32`). No file read-back.
3. Write the buffer to the temp file once, `Flush(true)`, atomic-rename — the existing durability
   sequence, unchanged. Return the same `SidecarManifestEntry(name, size, crc)`.

`ComputeFileCrc` (`PersistenceFormat.cs:180-186`) loses its only caller and is deleted.

**Trade-off (see §5):** the sidecar buffer is now held in RAM while it is CRC'd and written, so save
peak memory rises by ~one serialized bunch per in-flight partition instead of a 64 KB streaming
buffer. It is bounded by the existing work+cores partitioning and by `load-path-integrity`'s <2 GB
per-bunch guard, freed immediately after the durable write, and far below the old
100 MB×writers LOH that finding P1 removed.

### 3.3 Load: validate the bunch CRC during the single parse pass

Introduce a `Crc32ReadStream` — a **read** pass-through over the bunch `FileStream` that `Append`s
every byte it hands out to a running `Crc32Hasher`. Reads on this path are strictly forward
(confirmed: `SerializationReader` reads the header then consumes the payload sequentially to
`endPosition`; string/object tokens are inlined on first use, `SerializationReader.cs:88-109,1823-1828`
— it never seeks backward), so the running CRC sees exactly the file bytes, in order, once.

`LoadAGraphElementBunch` (`PersistencyFactory.cs:741-788`) becomes:

1. Cheap O(1) truncation check up front: `new FileInfo(path).Length == entry.Size` (kept from
   `ValidateSidecar`, still before any allocation).
2. Open with `Constants.BufferSize` + `FileOptions.SequentialScan`; wrap in `Crc32ReadStream`.
3. `ReadAndValidatePreamble` (rejects foreign/old/wrong-version, and the running CRC covers the
   preamble too — same coverage as today), then parse the payload through `SerializationReader` as
   now. The existing `ReadOptimizedInt32Checked` guards still bound every length prefix against the
   bytes remaining, so a corrupt count still cannot drive a huge allocation *mid-parse* (finding C5
   preserved).
4. At end-of-parse, compare the accumulated CRC to `entry.Crc`; on mismatch throw
   `InvalidDataException`. A mandatory-bunch failure is already fatal — `LoadGraphElements` flattens
   it and the worker rolls the load back (`PersistencyFactory.cs:939-948`; C3/C5) — so the semantics
   are unchanged; only the redundant validate pass is gone.

The separate bunch loop in `PersistenceFormat.ValidateSidecar` at `PersistencyFactory.cs:154-157`
is removed for bunches (the CRC now happens in-parse).

**Index/service sidecars stay validate-then-parse, but faster.** They are best-effort and typically
small, and their parse is driven by a plugin (`OpenIndex`/`OpenService`) that may not read to EOF, so
an in-parse tee cannot guarantee whole-file coverage. Scope keeps `ValidateOptionalSidecars` for
them (now on the SIMD CRC) and only right-sizes their parse opens (`:834,:855`) to
`Constants.BufferSize` + `FileOptions.SequentialScan`. Folding their CRC into the parse (drain to EOF
through the tee after the plugin returns) is a noted follow-on; the mandatory bunches dominate the
byte volume, so halving their I/O meets the acceptance target.

## 4. Acceptance criteria

- **Save single-pass:** `WriteSidecar` no longer re-opens or re-reads the file it just wrote; the
  manifest entry's size/CRC are derived from the in-memory image. Verified by the round-trip suite
  (save→load still validates and reconstructs exactly) plus a save-side bytes-moved measurement
  showing the sidecar bytes are read back **zero** times (was once).
- **Load single-pass for bunches:** a bunch is opened and read **once** on load; its CRC is checked
  at EOF; a corrupted bunch (flipped byte, truncation) is still rejected with `InvalidDataException`
  and the load still rolls back cleanly, the single writer surviving. Measured load bytes-moved for
  the bunches is roughly halved.
- **CRC throughput up an order of magnitude:** a micro-measurement over a large buffer shows the SIMD
  CRC materially faster than the old table loop; a characterization test pins the two produce
  byte-identical values, and a checkpoint written with the old CRC still loads (byte-compatibility).
- **Buffers right-sized:** the bunch/index/service parse opens use `Constants.BufferSize` +
  `FileOptions.SequentialScan`.
- **Save stall re-measured (not assumed):** `NonBlockingSaveBenchmark` is re-run on this box and the
  recorded save writer-hold numbers shrink; the `non-blocking-save` figures are updated to the new
  measurements and the P3 deferral is explicitly noted as **unaffected** (still deferred — the stall
  is now smaller, which only reinforces it).
- **Envelope intact:** magic + `formatVersion` (unbumped), completion manifest (name+size+CRC),
  per-file and header CRC-32, temp+fsync+rename, clean-reject — all unchanged. Full suite plus the
  `persistence-hardening` / `load-path-integrity` / WAL round-trip tests stay green.

## 5. Risks

- **Save peak memory (§3.2).** Buffering a whole bunch in RAM to CRC it in one pass raises transient
  save memory versus today's 64 KB streaming write. Mitigation: it is bounded by the work+cores
  partitioning and `load-path-integrity`'s <2 GB per-bunch guard, released right after the durable
  write, and matches the shape the header commit already uses. If a future measurement shows it
  matters for very large graphs, cap the number of in-flight buffered bunches (bounded concurrency)
  rather than reverting to the read-back. This is a deliberate memory-for-I/O trade and is called out
  as such.
- **CRC byte-compatibility.** The whole no-format-bump argument rests on `System.IO.Hashing.Crc32`
  matching the hand-rolled polynomial/parameters exactly. Locked by the Phase 0 characterization test
  (identical values over representative buffers + an old-checkpoint-still-loads round-trip) landing
  **before** the swap.
- **In-parse CRC coverage.** Folding the CRC into the parse means the check now depends on the reader
  consuming the file to EOF. Verified forward-only/read-to-`endPosition` today
  (`SerializationReader.cs:88-109,1823-1828`); guard it with a test that a **truncated** bunch (fewer
  bytes than the manifest size) is caught by the up-front O(1) length check, and a **flipped-byte**
  bunch (same length) is caught by the EOF CRC.
- **New dependency.** `System.IO.Hashing` is an additional package. It is a Microsoft-published,
  BCL-adjacent library on the same 10.0.x line as the existing references; low risk.
- **Interaction with `load-path-integrity`.** Both themes touch the load path. L1 changes the
  `edgeTodo` scratch type and L2/L3 touch save/manifest guards; none of them conflicts with moving
  the CRC into the parse. Land order is flexible, but the L2 per-bunch size guard should be in place
  so §3.2's buffered bunch cannot grow unbounded — coordinate, don't duplicate.

## 6. Keep (do not regress)

- **The whole hardened envelope** from `persistence-hardening`: 8-byte magic + `formatVersion`,
  per-file CRC-32 + trailing header CRC, the completion manifest (name+size+CRC), temp+fsync+atomic
  rename with the header written **last** as the single commit point, clean-reject of foreign/old
  files, and CRC coverage over the **entire** file (preamble included). This theme changes only *when*
  and *how fast* those bytes are checksummed, never *what* is checksummed.
- **The C5 allocation discipline:** the O(1) length/size check and preamble/version gate run before
  any large allocation, and every length prefix stays guarded by `ReadOptimizedInt32Checked` /
  `EnsureAvailable` during the parse — a truncated/garbage file is still rejected without a huge
  allocation and without killing the worker.
- **Mandatory-vs-best-effort load resilience:** a bunch failure aborts the load cleanly (state
  restored, single writer survives); a bad index/service is logged and skipped
  (`ValidateOptionalSidecars`, `LoadIndices`/`LoadServices`).
- **The single-writer, blocking-but-correct save** (`non-blocking-save`). The `ComputeFileCrc`
  read-back this theme removes sits **inside** that save's writer-hold window (it runs on the worker
  in `WriteSidecar`), so deleting it shrinks the very stall `non-blocking-save` measured
  (170 ms @ 100k / 433 ms @ 400k / 907 ms @ 2M) — **without** moving the write off the worker. The P3
  deferral stands; this theme only makes the retained blocking save cheaper and re-measures it.
