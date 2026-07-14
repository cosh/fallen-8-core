# Checkpoint I/O Efficiency — Plan

Companion to [spec.md](./spec.md). Byte-compatibility and the guardrail measurement first, then the
CRC swap, then save single-pass, then load single-pass + buffers, then re-measure and document.

GitHub issue: to be opened (label: feature). Feature branch: `feature/checkpoint-io-efficiency`.

## Phase 0 — Baseline & guardrails

Intent: prove the double-read and the CRC-swap safety before changing anything, so every later phase
has a number and a byte-for-byte equality it must not break.

- [ ] Add `CheckpointIoBenchmark` (`[TestClass]`, `[TestCategory("Benchmark")]` + `[Ignore]`, output
  prefix `[CKIOBENCH]`, per repo convention) that saves and loads graphs at a few sizes and reports,
  per side, the **bytes read/written** against the checkpoint directory (via a counting-stream shim
  or `FileStream` counters) and the wall time — capturing today's save read-back and load
  validate-then-parse double read. Numbers to be captured on this box.
- [ ] Add a CRC micro-measurement (in the same benchmark) hashing a large (≥ 256 MB) buffer with the
  old table loop vs `System.IO.Hashing.Crc32`, reporting GB/s for each. To be captured on this box.
- [ ] Add `Crc32EquivalenceTest` (real test, default suite): assert `System.IO.Hashing.Crc32`
  produces the **same** `uint` as the current `Crc32.Compute` for representative buffers (empty,
  short ASCII, arbitrary binary, > 64 KB, and a buffer straddling the 64 KB stream chunk). This is
  the byte-compatibility gate for §3.1.
- [ ] Add `Checkpoint_WrittenBeforeCrcSwap_StillLoads`: save a graph with the current code, keep the
  bytes as a fixture, and assert a load succeeds after the swap (locks "no `formatVersion` bump,
  existing checkpoints still validate").

## Phase 1 — SIMD CRC-32 (byte-compatible swap)

Intent: land the accelerated CRC behind the unchanged `Crc32` facade, gated by Phase 0's equality.

- [ ] Add `<PackageReference Include="System.IO.Hashing" Version="10.0.x" />` to
  `fallen-8-core/fallen-8-core.csproj` (match the 10.0.x line of the existing packages).
- [ ] Re-implement `Crc32` (`fallen-8-core/Persistency/Crc32.cs`) over `System.IO.Hashing.Crc32`:
  - `Compute(byte[] buffer, int offset, int count)` → `System.IO.Hashing.Crc32.HashToUInt32(span)`.
  - `Compute(Stream, out long length)` → bounded 64 KB reads `Append`ed to an instance
    `System.IO.Hashing.Crc32`, then `GetCurrentHashAsUInt32()`.
  - Add `Crc32Hasher` (thin wrapper: `Append(ReadOnlySpan<byte>)` + `GetCurrentHashAsUInt32()`) for
    the streaming/tee paths in Phases 2–3. Keep `Seed`/`Update`/`Finalize` only if a caller still
    needs them; otherwise delete the now-dead table loop (`Crc32.cs:42-58,61-69`).
- [ ] Confirm `Crc32EquivalenceTest` and the full suite are green (the header CRC at
  `PersistencyFactory.cs:421`/`:119` and every sidecar CRC now flow through the accelerated path).

## Phase 2 — Save single-pass (drop the read-back)

Intent: derive each sidecar's CRC from its finalized in-memory image; never re-read the file.

- [ ] Rework `WriteSidecar` (`PersistencyFactory.cs:673-699`): serialize preamble + body into a
  `MemoryStream` (seek-back `UpdateHeader` now happens in RAM), CRC the finalized buffer in one pass
  (`Crc32.Compute(buffer, 0, length)`), then write the buffer to the temp file once (`Flush(true)`)
  and atomic-rename. Return the same `SidecarManifestEntry`.
- [ ] Delete `PersistenceFormat.ComputeFileCrc` (`PersistenceFormat.cs:180-186`) — its only caller is
  gone.
- [ ] Keep the header commit (`PersistencyFactory.cs:407-431`) as-is (already single-pass, in-RAM
  CRC) — the sidecar path now matches it.
- [ ] Guard the transient-memory trade-off: keep the write bounded by the existing partitioning; do
  **not** buffer more than the in-flight bunches already imply. (If a future measurement demands it,
  cap in-flight buffered bunches — noted, not built now.)
- [ ] Round-trip test stays green; add `Save_DoesNotReReadSidecars` asserting (via the Phase 0
  counting shim) that save-side bytes read from the checkpoint dir is 0.

## Phase 3 — Load single-pass for bunches + right-sized parse opens

Intent: check the bunch CRC while parsing; stop the separate validate pass; buffer the parse opens.

- [ ] Add `Crc32ReadStream`: a read-only pass-through over a `Stream` that `Append`s every returned
  byte to a `Crc32Hasher` and exposes the running hash. Forward-only reads are sufficient (verified:
  `SerializationReader` reads to `endPosition` without seeking back).
- [ ] Rework `LoadAGraphElementBunch` (`PersistencyFactory.cs:741-788`): O(1) `FileInfo.Length ==
  entry.Size` check first; open with `Constants.BufferSize` + `FileOptions.SequentialScan`; wrap in
  `Crc32ReadStream`; `ReadAndValidatePreamble`; parse as now; at EOF compare the running CRC to
  `entry.Crc` and throw `InvalidDataException` on mismatch. Thread `SidecarManifestEntry` (or its
  size+CRC) down to this method.
- [ ] Remove the bunch validate loop at `PersistencyFactory.cs:154-157` (the CRC now happens
  in-parse). Keep `ValidateOptionalSidecars` for index/service (best-effort, plugin-driven parse).
- [ ] Right-size the index/service parse opens (`PersistencyFactory.cs:834,855`) to
  `Constants.BufferSize` + `FileOptions.SequentialScan`.
- [ ] Preserve C5: the up-front length check + `ReadOptimizedInt32Checked` guards still run before
  any large allocation; a mandatory-bunch failure still flattens through `LoadGraphElements`
  (`:939-948`) and rolls the load back.
- [ ] Tests: `Load_ReadsEachBunchOnce` (counting shim: load-side bunch bytes ≈ file size, not 2×);
  `Load_TruncatedBunch_RejectedByLengthCheck`; `Load_FlippedByteBunch_RejectedByEofCrc`; both assert
  clean rollback + surviving writer.

## Phase 4 — Measure & document

Intent: replace every "to be captured" with a real number and update the neighbouring feature notes.

- [ ] Re-run `CheckpointIoBenchmark`: record save/load bytes-moved (before → after) and the CRC GB/s
  before/after. Assert in the plan that save read-back is eliminated and bunch load I/O is ~halved.
- [ ] Re-run `NonBlockingSaveBenchmark` (`fallen-8-unittest/NonBlockingSaveBenchmark.cs`) and record
  the new save writer-hold numbers (the removed read-back was inside that window). Update
  `features/non-blocking-save/` with the new figures and state explicitly that the **P3 deferral is
  unaffected** — the stall is smaller, which reinforces the deferral (see Decision).
- [ ] Update the P3 note wording in `features/persistence-hardening/` only if it quotes the old stall
  magnitude; do not change the decision.
- [ ] `features/checkpoint-io-efficiency/README.md` (optional): one-paragraph "what changed and the
  measured before/after".

## Progress

- [ ] Phase 0 — baseline benchmark + CRC micro-measurement + `Crc32EquivalenceTest` + old-checkpoint
  round-trip fixture
- [ ] Phase 1 — `System.IO.Hashing` package + `Crc32` re-implemented over the accelerated primitive
  (byte-compatible), suite green
- [ ] Phase 2 — `WriteSidecar` single-pass (in-RAM CRC), `ComputeFileCrc` deleted, no save read-back
- [ ] Phase 3 — `Crc32ReadStream` + in-parse bunch CRC, bunch validate loop removed, parse opens
  right-sized, corruption/truncation tests
- [ ] Phase 4 — re-measured numbers recorded; `non-blocking-save` figures updated (deferral
  unaffected)

## Decision / revisit condition

This theme borders the **`non-blocking-save` (P3) deferral** and must not reopen it. The relationship:

- The save-side read-back removed in Phase 2 runs on the single writer thread, **inside** the save's
  writer-hold window. Removing it makes the retained **blocking** save cheaper; it does **not** move
  the write off the worker and does **not** require a consistent off-worker point-in-time snapshot
  (the reason P3 was deferred). The single-writer / lock-free-read model is untouched.
- Phase 4 re-measures the writer-hold and updates `non-blocking-save`'s recorded numbers. A *smaller*
  stall only strengthens "keep the blocking save"; the deferral's revisit condition is unchanged:
  reopen off-worker save only for **tens-of-millions-of-elements graphs saved frequently**, and then
  most naturally on the WAL or a future element-level immutable-snapshot model — not on this I/O
  change.

Revisit this theme itself if: (a) profiling shows the Phase 2 buffered-bunch memory is a problem on
large graphs (then add bounded in-flight-buffer concurrency, per §5); or (b) index/service sidecars
grow large enough that their retained validate-then-parse double read becomes significant (then fold
their CRC into the parse via a drain-to-EOF tee, the noted follow-on).
