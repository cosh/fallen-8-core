# Non-Blocking Save (P3) — Specification

> **Status:** Landed as a **measurement-driven DEFERRAL**. The `persistence-hardening` theme deferred
> P3 ("move the file writing off the single writer thread") because the graph-element objects mutate
> in place, so a correct off-worker save needs a consistent point-in-time view — a non-trivial change.
> Rather than assume the blocking save is a problem or assume it is fine, this feature **measured the
> write-stall a blocking save actually causes** (opt-in `NonBlockingSaveBenchmark`) and let the number
> decide, exactly as P6 (parent-pointer BLS) was.
>
> **Outcome — DEFER, keep the blocking save.** The stall is sub-second up to a 2M-element graph
> (170 ms @ 100k, 433 ms @ 400k, 907 ms @ 2M elements) and a concurrent write was shown to stall by
> exactly the save duration (464 ms on a 1M-element graph). It is paid only on an explicit save, which
> the WAL (now covering subgraphs) lets you make infrequent; the bounded copy-on-save fix would remove
> only the disk-I/O share, and the full fix is a large hot-path rewrite. See the plan's Decision for
> the full rationale and the revisit condition (tens-of-millions-of-elements graphs saved frequently).

## 1. Problem / current state

`SaveTransaction` runs on the single transaction-writer thread. `PersistencyFactory.Save` fans the
element serialization across pooled tasks but the writer thread **blocks on their results**, then
writes the header (fsync + atomic rename). So the writer is held for the **entire** save —
serialize + disk I/O — and, because there is exactly one writer, every other mutating transaction
enqueued during a save waits until the save completes. For a large graph this is a write-latency
spike proportional to graph size. The code says so explicitly
([`PersistencyFactory.cs`](../../../fallen-8-core/Persistency/PersistencyFactory.cs) lines ~298–302:
"the file writing is NOT moved off the worker; see the P3 deferral note").

**Why a correct fix is non-trivial.** The master-store *snapshot* (the segmented array) is immutable
(copy-on-write publish), but the element *objects* (`AGraphElementModel` / `VertexModel` /
`EdgeModel`) mutate in place — `AddProperty`, `TryRemoveProperty`, and edge-adjacency additions
mutate the live object. `SaveBunch` reads each element's full state, so serializing off-thread
concurrently with the writer could capture a **torn** element (a half-updated property map or
adjacency). A correct off-worker save therefore needs a consistent point-in-time view of element
*contents*, not just of the element *set*.

## 2. The decision to make

Since the WAL (now including subgraph support — see `features/wal-subgraph-support/`) provides
durability **between** snapshots, snapshots can be less frequent, which reduces how often any
save-stall is paid. The question is whether the per-save writer-stall is large enough — at realistic
graph sizes — to justify the cost/risk of an off-worker save:

- **If the stall is small / acceptable** for the embedded single-writer model → **defer P3**, keep
  the simple, obviously-correct blocking save, and document the deferral with the measured numbers
  (the P6 pattern). This is the expected outcome unless the numbers are surprising.
- **If the stall is large** → recommend the bounded **copy-on-save** approach (capture an immutable
  point-in-time copy of element state on the writer, release the writer, then serialize + fsync +
  rename off-thread) as a follow-up, backed by the measurement.

## 3. What this feature delivers

- An **opt-in benchmark** (`[TestCategory("Benchmark")]` + `[Ignore]`, excluded from the default run,
  output prefix `[NBSBENCH]`) that measures:
  1. **Writer-hold time of a save** across graph sizes (the duration the single writer is held ≈ the
     worst-case stall any concurrent write pays).
  2. The **observed stall of a concurrent write**: enqueue a save, then immediately enqueue a tiny
     `AddVertex`, and time how long that write waits — demonstrating the stall is real and ≈ the save
     duration.
- **Real, captured numbers** recorded in the plan (run once on this box with `[Ignore]` temporarily
  removed; no fabricated figures).
- A **written decision** (defer or proceed) grounded in those numbers, in this spec + the plan, and
  the `persistence-hardening` P3 note updated to point here.

## 4. Acceptance criteria

- The benchmark builds and runs opt-in, is excluded from the default suite, and fabricates no numbers.
- The plan records measured writer-hold times across sizes and a demonstrated concurrent-write stall.
- A clear defer/proceed decision with rationale tied to the numbers; full suite still green; no engine
  behaviour change if the decision is to defer.

## 5. Non-goals

- Implementing the full immutable/copy-on-write element model (a large hot-path rewrite) — out of
  scope for this measurement feature; it would be a separate, benchmark-justified follow-up.
- Changing save correctness, the on-disk format, or the single-writer/lock-free-read invariants.
