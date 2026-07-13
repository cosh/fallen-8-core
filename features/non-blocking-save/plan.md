# Non-Blocking Save (P3) — Plan

Companion to [spec.md](./spec.md). Measure the blocking save's writer-stall → decide defer-vs-proceed
from the number.

## Phase 1 — Benchmark harness
- Add `NonBlockingSaveBenchmark` (`[TestCategory("Benchmark")]` + `[Ignore]`, prefix `[NBSBENCH]`):
  - `SaveWriterHoldTime_ScalesWithGraphSize`: build graphs of increasing size (vertices with a couple
    of properties + edges), time `Save` (= the writer-hold ≈ worst-case concurrent-write stall), for
    each size. Save to a temp dir; clean up.
  - `ConcurrentWriteStall_DuringSave_IsAboutSaveDuration`: build one graph; enqueue a `SaveTransaction`
    (do NOT wait); immediately enqueue a tiny `AddVertex`; time that write's `WaitUntilFinished` (the
    stall a concurrent writer observes, since the single writer runs the save first). Report the stall
    alongside the pure save time.

## Phase 2 — Capture numbers
- Temporarily remove `[Ignore]` on this box, run, record the real `[NBSBENCH]` output in the Measured
  section below. Restore `[Ignore]`.

## Phase 3 — Decide & document
- Compare the stall against a plain-English bar for the embedded single-writer model (given the WAL
  covers between-snapshot durability). Record the defer/proceed decision + rationale here and in the
  spec; update `features/persistence-hardening/` P3 note to reference this outcome.

## Status
- [x] Phase 1 — benchmark harness (`NonBlockingSaveBenchmark`, `[NBSBENCH]`).
- [x] Phase 2 — captured numbers (below).
- [x] Phase 3 — decided: **DEFER P3**, keep the blocking save (rationale below).

## Measured results (real, captured in this environment)

Release build (net10.0), single run on this dev box — indicative, not a formal BenchmarkDotNet
study. `[Ignore]` was temporarily removed to run, then restored. Output prefix `[NBSBENCH]`.
Vertices carry two properties; edges chain the vertices.

- **Save writer-hold (≈ worst-case concurrent-write stall), by graph size:**
  - 100,000 elements (50k vertices + 50k edges) → **170 ms**
  - 400,000 elements (200k vertices + 200k edges) → **433 ms**
  - 2,000,000 elements (1M vertices + 1M edges) → **907 ms**
- **Concurrent-write stall, demonstrated directly:** on a 1,000,000-element graph (500k vertices +
  500k edges), a save took **464 ms** and a one-vertex write enqueued right behind it observed a
  stall of **464 ms** — i.e. the stall a concurrent writer pays equals the save duration, as the
  single-writer model predicts.

## Decision — DEFER (keep the blocking save)

The measured writer-stall is **sub-second up to a 2-million-element graph** and is paid ONLY during
an explicit `Save`. With the write-ahead log now providing durability *between* snapshots — including
subgraphs (see `features/wal-subgraph-support/`) — snapshots can be infrequent, so this stall is
rarely paid. Weighed against that:

- The bounded **copy-on-save** alternative (spec §2) would move only the **disk-I/O** portion of the
  save off the writer; the serialization (a substantial share of the 170–907 ms) would still run on
  the writer. So even implementing it would leave a large fraction of the stall in place — a partial
  win for a real increase in complexity and a transient ~2× memory copy. (We did not instrument the
  exact serialize-vs-I/O split; that it is only a *partial* win is enough to decide against starting.)
- The **full immutable/copy-on-write element model** would remove the stall entirely but is a large,
  risky rewrite of the hot mutation path — clearly disproportionate to eliminating a rare, sub-second,
  explicit-save pause.

So the simple, obviously-correct blocking save is retained. **Revisit if** a workload needs frequent
snapshots of very large graphs (tens of millions of elements), where the stall would grow to several
seconds — then copy-on-save (partial) or the immutable model (full) becomes worth its cost. The
opt-in benchmark stays in the tree so that decision can be re-measured, not re-guessed.

## Notes
- The headline metric is the total writer-hold (the stall). Under the copy-on-save alternative only
  the disk-I/O portion would move off-worker, so that approach's win would be a fraction of the total;
  the total stall is what decides whether the work is worth starting at all.
- Defer is a valid, documented outcome (as with P6) — the point is a decision grounded in real numbers.
