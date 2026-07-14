# Load-Path Integrity — Specification

> **Status:** Planned — **P0 correctness** — from the 2026-07 principal-architect & performance
> review. Close two silent-corruption cliffs and one DoS in the checkpoint read/write path that
> predate or slipped past `persistence-hardening`.

The hardened checkpoint format (`persistence-hardening`: atomic temp+fsync+rename, completion
manifest, per-file CRC-32, magic+`formatVersion`, clean-reject) makes a checkpoint that *loads*
trustworthy. This theme fixes three defects the envelope does **not** catch: a data race while
rehydrating cross-bunch edges, a length header that silently overflows `Int32` on a >2 GB bunch,
and one manifest count that escaped the load-path allocation discipline. All three sit *inside* a
save/load that otherwise CRC-validates everything, so each fails silently or fails only at the
worst time (load, after the save reported success).

## 1. Problem / current state

| # | Issue | Location | Effect |
|---|-------|----------|--------|
| L1 | Cross-bunch `edgeTodo` fix-up uses `ConcurrentDictionary.AddOrUpdate` whose update factory mutates a shared `List<T>` **outside any lock** and may run more than once on CAS retry | `PersistencyFactory.cs:1190,1237` (writers) · `:957` (`Parallel.For`) · `:953` (shared dict) · `:996`–`1025` (consumer) | Timing-dependent silently-inconsistent adjacency on load (torn `List`, lost or duplicated edge fix-ups) |
| L2 | `UpdateHeader` writes an unchecked `(int)(Position - startPosition)` length; a bunch > 2 GB wraps to a negative/short length that is CRC'd, manifested and committed | `SerializationWriter.cs:2141,2150` · `SerializationReader.cs:53`–`54`,`:94` · `SaveTransaction.cs:39` · `PersistencyFactory.cs:1050` | Save "succeeds", checkpoint is permanently **unloadable** (throws at load); because bunches are mandatory the WHOLE savegame is lost |
| L3 | `ReadManifestList` does `new List<SidecarManifestEntry>(count)` from a raw `ReadInt32()` guarded only against `count < 0` | `PersistencyFactory.cs:206,207,212` | A crafted (CRC-self-consistent) header drives a multi-GB preallocation before the first entry read — DoS |

### L1 — Load-path data race on `edgeTodo`

`LoadGraphElementsCore` (`PersistencyFactory.cs:951`) declares one shared
`ConcurrentDictionary<Int32, List<EdgeOnVertexToDo>> edgeTodo` (`:953`) and fans bunch files out
across a `Parallel.For` (`:957`), each task running `LoadAGraphElementBunch` → `LoadVertex`
concurrently. When a vertex references an edge that is not yet materialised, `LoadVertex` records a
fix-up under the **edge id** as key:

```csharp
edgeTodo.AddOrUpdate(edgeId,
     new List<EdgeOnVertexToDo> { aEdgeTodo },
     (id, list) => { list.Add(aEdgeTodo); return list; });   // :1190 (out) and :1237 (in)
```

The two endpoints of one not-yet-materialised edge live in different bunches — the source records
an out-edge fix-up, the target records an incoming-edge fix-up — **under the same `edgeId` key**,
on two threads at once. `ConcurrentDictionary.AddOrUpdate` runs its update delegate **outside** the
bucket lock and can invoke it more than once on a CAS retry. So two threads can call `List.Add` on
the same list simultaneously (torn backing array / lost element) or a retried factory double-adds
(duplicate `AddOutEdge`/`AddIncomingEdge` in the sequential fix-up at `:996`–`1025`). The result is
a timing-dependent, silently inconsistent rehydrated graph — on the one path where every byte was
CRC-validated. The sequential consumer loop only *reads* `aKV.Value`, so the container type is free
to change.

### L2 — >2 GB bunch silently overflows the `Int32` length header

Every sidecar is written through `WriteSidecar` (`PersistencyFactory.cs:673`), which calls
`writer.UpdateHeader()` (`:685`) to back-patch the payload length into the file's leading 4 bytes:

```csharp
var result = BaseStream.CanSeek ? (int)BaseStream.Position - startPosition : 0;   // :2141
...
Write(result);                                                                     // :2150
```

The cast is **unchecked**. Once a single bunch's serialized payload exceeds `Int32.MaxValue`, the
length wraps to a negative/short value, which is then CRC'd, recorded in the completion manifest and
committed by the atomic rename — the save reports success. The reader stores start/end as `int`
(`SerializationReader.cs:53`–`54`) and computes `endPosition = startPosition + ReadInt32()`
(`:94`); the wrapped length makes `endPosition < startPosition`, so `EnsureAvailable`/`BytesRemaining`
throw a "negative remaining length" `InvalidDataException` at **load**. Because graph-element
bunches are mandatory, one oversized bunch makes the whole checkpoint unloadable.

This is reachable by default: `SaveTransaction.SavePartitions` defaults to **5**
(`SaveTransaction.cs:39`) and `ComputePartitionCount` additionally caps at `Environment.ProcessorCount`
(`PersistencyFactory.cs:1050`). A large graph split into only a handful of bunches pushes one bunch
past 2 GB long before the id space runs out.

### L3 — unbounded manifest preallocation

`ReadManifestList` (`PersistencyFactory.cs:204`) reads the entry count with a raw
`reader.ReadInt32()` (`:206`), rejects only a negative count (`:207`), then preallocates
`new List<SidecarManifestEntry>(count)` (`:212`) **before** the first guarded entry read. A crafted
header (a CRC is an integrity check, not a signature — an attacker recomputes it) with
`count = Int32.MaxValue` drives a multi-GB allocation, i.e. a DoS. This is the one spot that escaped
the `ReadOptimizedInt32Checked` / `EnsureAvailable` discipline the rest of the load path already
follows. (The method's own doc-comment at `:200`–`:202` asserts the count is "already bounded by the
header's own validated length and the reader's per-prefix guards" — that claim is **inaccurate** for
the capacity preallocation and must be corrected along with the code.)

## 2. Goals / non-goals

**Goals**

- L1: rehydrate cross-bunch edges with a container that is safe under concurrent `Parallel.For`
  producers and one sequential consumer, with **no** possibility of a torn list or a duplicated
  fix-up. Adjacency after load is exactly what was saved, every time.
- L2: a save that would produce an unloadable checkpoint must **fail loudly at save time** and leave
  **no committed checkpoint** — never a silently-broken savegame discovered only at load.
- L3: reject an absurd manifest count **before** allocating, consistent with the existing length-guard
  discipline.

**Non-goals**

- Changing the transaction/concurrency model (single writer; lock-free reads over a volatile
  snapshot). L1's fix is confined to the load-time rehydration scratch structure, which is not part
  of the live graph.
- Changing the on-disk layout / bumping `formatVersion`. All three fixes are load/save **runtime**
  changes over the existing bytes. Widening the internal length header to `Int64` (which *would*
  change the layout) is explicitly a **long-term** item behind a `persistence-hardening`
  `formatVersion` bump — see §5 and the plan's revisit note; it is out of scope here.
- Making the file writing non-blocking / off-worker — MEASURED and DEFERRED in `non-blocking-save`;
  not reopened.
- A derived read-only CSR adjacency snapshot — ASSESSED and SKIPPED in `csr-adjacency`; not
  reopened.

## 3. Design sketch

**L1 — lock-free enqueue instead of a shared mutable list.** Change the scratch structure from
`ConcurrentDictionary<Int32, List<EdgeOnVertexToDo>>` to
`ConcurrentDictionary<Int32, ConcurrentQueue<EdgeOnVertexToDo>>` and replace both `AddOrUpdate`
call sites with:

```csharp
edgeTodo.GetOrAdd(edgeId, _ => new ConcurrentQueue<EdgeOnVertexToDo>()).Enqueue(todo);
```

`GetOrAdd`'s value factory may still run more than once under contention, but only the winning queue
is published and returned, and `ConcurrentQueue.Enqueue` is itself thread-safe and called exactly
once per fix-up — so no torn backing array and no double-add. The signatures of `LoadGraphElementsCore`,
`LoadAGraphElementBunch` and `LoadVertex` change to the new value type; the sequential consumer loop
at `:996`–`1025` iterates `aKV.Value` unchanged (a `foreach` over a `ConcurrentQueue` is a safe FIFO
snapshot). No format change, no behavioural change on a correct file — only the race is removed.

**L2 — fail the save loudly (minimum fix).** In `SerializationWriter.UpdateHeader`, compute the
length as a `long` and throw (e.g. `InvalidDataException`/`IOException` with a clear "checkpoint
segment exceeds the 2 GB per-file limit" message) when `Position - startPosition > int.MaxValue`,
instead of casting unchecked. Because `WriteSidecar` writes to a temp file inside a
`try { … } catch { TryDeleteFile(temp); throw; }` (`PersistencyFactory.cs:673`–`698`) and the main
header (the commit point) is only written/renamed *after* every sidecar succeeds (`:400`–`431`), a
throw here deletes the partial temp and aborts the save before anything is committed — no torn
checkpoint, and `TryExecute` surfaces the failure so the worker maps it to `RolledBack`+`Error` and
REST to 500. **Risk-reduction (fold in):** stop the default `SavePartitions = 5` from acting as a
hard ceiling — treat a non-positive/unset request as "let `ComputePartitionCount` decide" so a large
graph is split by work+cores rather than always into ≤ 5 bunches. This lowers the chance of hitting
the limit but is not a substitute for the guard (the per-file cap is still `Environment.ProcessorCount`,
so a big enough graph can still exceed 2 GB/bunch — hence the loud throw is the real fix). Optional
follow-on: size partitions by *estimated bytes*, not just element count.

**L3 — bound the count before allocating.** In `ReadManifestList`, before the `new List<…>(count)`,
reject a count that cannot possibly be backed by the bytes left in the header: an entry is at least
`sizeof(Int64) + sizeof(UInt32) + 1` = **13 bytes** (name length-prefix ≥ 1, size 8, CRC 4), so
`count > reader.BytesRemaining / minEntrySize` (with `BytesRemaining` treated defensively when it
reports the unknown-length sentinel) throws `InvalidDataException`. Keep the existing `count < 0`
guard. Fix the misleading doc-comment to describe the actual bound. This matches the
`EnsureAvailable` pattern the rest of the load already uses.

## 4. Acceptance criteria

- **L1:** a stress test loads a **many-bunch** graph whose edges **systematically span bunches** (so
  the two endpoints of most edges land in different bunches) and asserts adjacency is **exactly**
  correct — every out/in edge present exactly once, no duplicates, correct endpoints — **repeated**
  across many iterations. It flakes/fails on today's code and passes deterministically after the fix.
- **L2:** a save whose bunch would exceed `Int32.MaxValue` **fails the Save loudly** (the transaction
  rolls back with an `Error`; REST → 500) and leaves **no committed checkpoint** — only discarded temp
  files, the previous savegame (if any) intact, and no header the loader would accept. Because a real
  2 GB payload is impractical in a unit test, the guard is pinned by driving `UpdateHeader` over a
  stream that reports a `Position` past `Int32.MaxValue` (a seam / fake stream), asserting it throws
  rather than writing a wrapped length; an integration-level assert confirms no committed checkpoint
  is left behind on a save failure.
- **L3:** a header whose manifest declares an absurd `count` is rejected with `InvalidDataException`
  **before** the large allocation (the throw happens at the count check, not after materialising the
  list), on an otherwise CRC-consistent header.
- The existing checkpoint envelope — temp+fsync+rename, completion manifest, per-file CRC-32,
  magic+`formatVersion`, clean-reject — is **unchanged**; `formatVersion` is not bumped; the full
  suite plus the `persistence-hardening` / `wal-subgraph-support` round-trip tests stay green.

## 5. Risks

- **L1 ordering.** A `ConcurrentQueue` preserves per-producer FIFO but the interleave of two
  producers is nondeterministic — acceptable because a vertex's edge set is a set, and the fix-up
  loop's effect (add each edge once to the right adjacency bucket) is order-independent. The stress
  test asserts set-equality, not order.
- **L2 test fidelity.** The overflow cannot be exercised with a genuine 2 GB write in CI, so the guard
  is tested through a seam over `UpdateHeader`; the risk is the seam drifting from the real call path.
  Mitigate by driving the *public* `UpdateHeader` on a stream that reports the large `Position`, not a
  private hook, so it exercises the exact production line.
- **L2 partitioning change.** Loosening the `SavePartitions = 5` default alters the number of bunches
  a default save produces; `persistence-hardening`'s partitioning tests pin the fan-out/fan-in
  correctness (contiguous ranges covering `[0,count)` exactly once) — re-run them and adjust only the
  count expectations, never the coverage invariant.
- **L3 min-entry-size assumption.** The 13-byte floor depends on the manifest entry encoding; if the
  entry framing changes, the divisor must track it. Keep it a named constant next to
  `WriteManifestList`/`SidecarManifestEntry` so the two stay in sync.

## 6. Keep (do not regress)

- The hardened envelope from `persistence-hardening`: atomic temp+fsync+rename, header-written-last
  commit point, completion manifest (name+size+CRC), per-file CRC-32, 8-byte magic + `formatVersion`,
  clean-reject of foreign/old files, and the `EnsureAvailable`/`ReadOptimizedInt32Checked` length
  guards on every other length-prefixed read.
- Load resilience: a mandatory graph-element bunch failure aborts the load cleanly (state restored,
  single writer survives) while a best-effort index/service sidecar failure is logged and skipped
  (`ValidateOptionalSidecars`). The parallel load's fan-out/fan-in and per-element `id == index`
  reconstruction (each element rebuilt by its own id, partitions are only a work split) are correct
  and must stay so.
- Single writer, lock-free reads over the volatile snapshot, and `Try*(out, …) : bool` for
  expected not-found/invalid cases.
