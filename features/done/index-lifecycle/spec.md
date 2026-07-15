# Index Lifecycle — Specification

> **Status:** Partially implemented (P1 architecture) — from the 2026-07 principal-architect &
> performance review. Index membership is decoupled from element lifecycle, the single-writer
> invariant, and the WAL; this theme makes index mutations first-class derived state with an explicit
> lifecycle.
>
> **Landed on branch `feature/index-lifecycle`** (the removal-lifecycle core):
> - **3.2 read-end filter** — a shared `FilterLive` helper drops `null`/`_removed` at all four
>   index-serving paths (`IndexScan` Equals, the generic `FindElementsIndex`, `TryOrderedRangeIndexScan`,
>   and `RangeIndexScan`/`Between`), so an index scan returns exactly what `GraphScan` does for the same
>   logical query. `FilterLive` returns the same shared bucket when nothing is dead, preserving the
>   `scan-result-representation` Equals fast path.
> - **3.4 reverse map** — `DictionaryIndex` and `RangeIndex` gained a reference-keyed `element → keys`
>   map so `RemoveValue` is O(affected keys) with no full-key scan or per-key allocation (rebuilt on
>   load; `RangeIndex` still invalidates `_sortedKeys` exactly on key-set shrink).
> - **3.3 write-end purge** — a committed removal (and, for a vertex, its cascaded edges) is dropped
>   from every registered index on the single writer, only on the commit path (a rolled-back removal
>   never purges), so a removed element's body is no longer pinned by a bucket.
>
> **Deferred (documented):** 3.5 (routing index writes through the single writer as transactions and
> retiring `AThreadSafeElement`) — a large surface that changes the REST failure mapping; and 3.6
> (WAL-logging index writes) — whose entry-type ordinals are owned jointly with
> `crash-durability-hardening`. Until 3.5 lands, index writes stay on the request thread under
> `AThreadSafeElement`, and index durability stays snapshot-only (defect (c)/(d) unchanged). The
> `SingleValueIndex`/`RegExIndex` reverse-map mirror is also deferred (the two named heavy indices,
> `DictionaryIndex`/`RangeIndex`, are done).

## 1. Problem / current state

Indices in Fallen-8 are entirely **caller-managed** and live outside the engine's core guarantees.
The engine never touches an index when an element is created or removed; the ONLY code that mutates
index contents is three REST actions (`AddToIndex` `GraphController.cs:1132`, `RemoveKeyFromIndex`
`GraphController.cs:1173`, `RemoveGraphElementFromIndex` `GraphController.cs:1201` → `idx.RemoveValue`
`GraphController.cs:1209`), each running on the ASP.NET request thread. Four coupled defects follow.

**(a) Stale reads — a removed element surfaces from an index forever.**
Element removal is a soft-delete: `TryRemoveGraphElement_private` (`Fallen8.cs:1054`) flips `_removed`
via `MarkAsRemoved`, detaches adjacency and maintains the counters, but **never touches any index**
(verified: the full commit and restore paths, `Fallen8.cs:1067`–`1274`, contain no index call). The
index-serving read paths then return the removed element because **none of them filter `_removed`**:

| Read path | Location | Filters `_removed`? |
|-----------|----------|--------------------|
| `IndexScan` Equals → `index.TryGetValue` | `Fallen8.cs:748` | No |
| `IndexScan` ordered/`NotEquals` → `FindElementsIndex` | `Fallen8.cs:1755` | No |
| `IndexScan` ordered range → `TryOrderedRangeIndexScan` (P4) | `Fallen8.cs:1784` | No |
| `RangeIndexScan` → `IRangeIndex.Between` | `Fallen8.cs:812` | No |
| `GraphScan` → `FindElements(ElementSeeker)` | `Fallen8.cs:1737` | **Yes** (`!_._removed`) |

So the *same logical query* ("elements whose property == X") returns the removed element through
`IndexScan` but hides it through `GraphScan` — inconsistent semantics for one question. The
brief flagged two stale paths; there are in fact **four** (the P4 ordered-range path and
`RangeIndexScan.Between` inherit the same gap), so any fix must cover all of them.

**(b) Memory + cost — removed bodies stay pinned, and removal is O(keys)×O(n).**
The dictionary-backed indices hold **strong references**: `DictionaryIndex._idx` is
`Dictionary<IComparable, ImmutableList<AGraphElementModel>>` (`DictionaryIndex.cs:53`), so a bucket
entry (`DictionaryIndex.cs:133`) keeps a removed element's whole object graph alive even after an
auto-trim compacts the master store — undercutting the churn-bounding work for any indexed workload.
`RemoveValue` (`DictionaryIndex.cs:176`) walks **every key** (`_idx.Keys.ToList()`) and does an
`ImmutableList.Remove` per key — O(keys)×O(n) with a fresh list allocation per key — because there is
no reverse `elementId → keys` map. `AddOrUpdate` (`DictionaryIndex.cs:117`) allocates a new
`ImmutableList` on every insert. `RangeIndex` carries the **identical** structure and the identical
`AddOrUpdate`/`RemoveValue` (`Range/RangeIndex.cs:245,302`); `SingleValueIndex`/`RegExIndex` are the
same family. This is a whole-family defect, not a `DictionaryIndex` one.

**(c) Concurrency + durability — index writes bypass the single writer AND the WAL.**
Every graph mutation goes through the single transaction-writer thread (`TransactionManager.cs:58,96`)
and, when the opt-in WAL is on, is appended and fsync'd there (`TransactionManager.cs:187`;
`Fallen8.LogCommittedTransaction`). Index mutations do **neither**: `idx.AddOrUpdate` /
`idx.RemoveValue` run on the request thread (`GraphController.cs:1140,1209`), the only writers in the
engine outside the single-writer thread, and they are invisible to the WAL — which by design does not
log indices ("they rehydrate from the snapshot"; `WalEntryType.cs`, `WalTransactionCodec.cs`). The
durability hole: after a crash between snapshots, replaying the WAL faithfully rebuilds the *elements*,
but every index entry added since the last snapshot is **gone** — the index is silently inconsistent
with the recovered graph.

**(d) `AThreadSafeElement` is a scalability ceiling and has a dead exception cliff.**
Every index (and `IndexFactory`) derives from `AThreadSafeElement` (`Helper/AThreadSafeElement.cs:34`).
Its `ReadResource` (`AThreadSafeElement.cs:49`) does an unconditional `Interlocked.Increment` on one
shared `_usingResource` field on acquire and an `Interlocked.Decrement` on release — so **every**
`TryGetValue` pays two interlocked read-modify-writes on a single shared cache line, and concurrent
readers serialise on cache-line ownership (the exact contention the graph read side already eliminated
via lock-free snapshot reads). It is a `Thread.Yield()` spin, unfair to writers, and its exhaustion
path (after `int.MaxValue` iterations) returns `false`, on which every caller throws
`CollisionException` — a 500 under contention that is otherwise effectively dead code.

## 2. Goals / non-goals

**Goals**

- Make index membership **derived state with an explicit lifecycle**: an element leaving the live set
  removes it from every registered index, so `IndexScan`/`RangeIndexScan`/`FindElementsIndex` agree
  with `GraphScan` for the same logical query.
- Bring index mutations under the **single-writer invariant** by routing them through the transaction
  pipeline, and make them **WAL-loggable** so index state survives crash + replay.
- Make removal **O(affected keys)**, not O(all keys)×O(n), and stop pinning removed element bodies.
- Remove `AThreadSafeElement` as the index concurrency primitive (or, if index writes stay off the
  writer, replace it with a fair, diagnosable lock).

**Non-goals**

- Changing the `IIndex` plugin contract's shape or the `CanPersist` flag (persistence-hardening C9) —
  both are kept.
- Automatically indexing elements on creation (auto-maintained secondary indices keyed by property).
  Indexing *which* property stays an explicit caller action; this theme only makes the *lifecycle*
  (removal, durability, concurrency) correct for whatever the caller indexed.
- A general query planner or new index types.
- Reintroducing CSR adjacency (see `csr-adjacency/assessment.md` — SKIPPED); this work reuses that
  assessment's one sanctioned shape, a derived read-only snapshot, for index buckets only.
- Moving the checkpoint write off the single writer (`non-blocking-save/` — DEFERRED with numbers);
  routing index writes onto the writer must not depend on or reopen that.

## 3. Design sketch

### 3.1 Index membership is derived state with an explicit lifecycle
Document (and enforce in code) the contract: **an index entry is valid only while its element is
live.** Two ends must hold it: the *write* end (removal purges the element from registered indices)
and the *read* end (a scan never returns a `_removed` element). We implement both — the read-end
filter as a cheap, immediate correctness floor, the write-end purge as the durable fix.

### 3.2 Stop the stale reads now (cheap interim, correctness floor)
Filter `_removed` (and null) at every index-serving read path so the semantics match `GraphScan`
*immediately*, independent of the larger refactor:
- `IndexScan` Equals (`Fallen8.cs:748`), `FindElementsIndex` (`Fallen8.cs:1755`),
  `TryOrderedRangeIndexScan` (`Fallen8.cs:1784`), and `RangeIndexScan`/`Between` (`Fallen8.cs:812`).
Do it in one shared helper (`FilterLive(ImmutableList<AGraphElementModel>)`) applied after the index
returns its bucket, so there is a single definition of "live" and the four paths cannot drift again.
Optionally scavenge lazily: when a read observes a `_removed` element in a bucket, it may be dropped
from the index under the writer (opportunistic, best-effort) — but the authoritative purge is 3.3.

### 3.3 Element removal maintains registered indices (write-end purge)
Have the removal path purge the element from every registered index. The engine owns the
`IndexFactory`, so `TryRemoveGraphElement_private` (and the batch remove) can, after the element
transitions live→removed, call `index.RemoveValue(element)` for each registered index — but only once
`RemoveValue` is cheap (3.4), else a single vertex delete becomes O(indices × keys × n). Sequencing:
land 3.4 first, then wire removal → purge. The purge runs **on the single writer** (removal already
does), so it composes with the existing rollback: if removal rolls back, the element is un-removed and
was never purged (purge happens only on the commit path, after the live→removed transition succeeds).

### 3.4 Efficient dictionary-backed indices (reverse map + writer-owned buckets)
Give `DictionaryIndex` and `RangeIndex` a reverse map `Dictionary<Int32, HashSet<IComparable>>`
(`elementId → keys it appears under`), maintained in `AddOrUpdate`/`RemoveValue`:
- `RemoveValue(element)` looks up the element's key set and touches **only those buckets** →
  O(affected keys), no full-key-set scan, no per-key allocation.
- Buckets become mutable `List<AGraphElementModel>` **owned by the writer** (once index writes are
  single-writer, 3.5), so `AddOrUpdate` appends in place instead of allocating a new `ImmutableList`
  per insert. Readers get an immutable/frozen view via the publish in 3.5.
- The reverse map keys on `Id`, so it also lets a scan (or a future scavenge) resolve staleness in
  O(1). `RangeIndex`'s `_sortedKeys` invalidation rule (P4) is preserved: the reverse-map removal
  still nulls `_sortedKeys` exactly when the key set shrinks.

### 3.5 Index mutations as single-writer transactions; retire `AThreadSafeElement`
Two coherent shapes; pick per whether index writes move onto the writer.

- **Preferred — single-writer + snapshot-publish (mirrors the graph read side and the
  `csr-adjacency` sanctioned snapshot).** Add index transactions (`AddToIndexTransaction`,
  `RemoveFromIndexTransaction`, `RemoveIndexKeyTransaction`, and create/delete/wipe) that
  `EnqueueTransaction` like every other mutation, so all index writes run on the single writer.
  The controller actions build a transaction and `WaitUntilFinished()` (same pattern as the graph
  mutations, with the `TransactionFailureReason` channel for not-found/invalid → 4xx). Each index then
  holds its bucket map as an **immutable/`FrozenDictionary`** published by **one `volatile` store**;
  readers read the field with no lock (lock-free, no interlocked per read). `AThreadSafeElement` is
  **retired** for indices and `IndexFactory` — its `CollisionException` cliff goes with it.
- **Fallback — keep index writes on the request thread but fix the lock.** If moving writes onto the
  writer is deferred, replace `AThreadSafeElement` with `ReaderWriterLockSlim` (fair, diagnosable, no
  exception cliff, no per-read interlocked on a shared line). This still fixes (d) and is a smaller
  change, but does NOT fix (c) durability — it is the interim, not the destination.

### 3.6 WAL-logging index writes (coordinate with crash-durability-hardening)
Once index writes are single-writer transactions (3.5 preferred), they become WAL-loggable by the
existing mechanism: extend `WalEntryType` (`Persistency/WalEntryType.cs`, values 1–13 in use;
subgraphs took 12/13) with the new index entry types at the next free ordinals, and teach
`WalTransactionCodec` (`Persistency/WalTransactionCodec.cs`) to classify/serialize/replay them —
exactly the extension pattern `wal-subgraph-support` used. **The entry-type numbering and the
serialized definition format are owned jointly with `crash-durability-hardening`** (the sibling theme
whose scope is WAL-logging index writes); this spec defines the transaction and lifecycle shape, that
theme lands the on-disk codec so the two do not double-allocate ordinals. Index rehydration from the
snapshot (persistence-hardening) is unchanged; the WAL only closes the *between-snapshots* gap, and
only for the index writes that are logged — matching how the WAL already treats the graph.

## 4. Acceptance criteria

- A removed element no longer appears in `IndexScan` (Equals, ordered, `NotEquals`),
  `RangeIndexScan`, or `FindElementsIndex`, and the result is **identical** to `GraphScan` for the
  same logical query — pinned by a test that indexes N elements, removes some, and asserts every
  index-serving path and `GraphScan` return the same live set.
- After the write-end purge, a removed element is not reachable from any registered index and its body
  is eligible for collection (no strong reference remains in a bucket or the reverse map).
- With the WAL on: create elements, index them, remove some, crash before a snapshot; on load the
  replayed graph AND its index entries reconstruct consistently (a scan returns exactly the live,
  still-indexed elements) — pinned by a WAL test in the `WriteAheadLogTest` family.
- A benchmark shows `RemoveValue` cost scales with **affected keys**, not all-keys×n, and that
  concurrent index reads scale (no per-read contention on one cache line) — opt-in
  `[TestCategory("Benchmark")]` + `[Ignore]`, numbers to be captured on this box.
- The `IIndex` plugin contract and `CanPersist` (persistence-hardening C9) are unchanged; every
  built-in index still saves/loads and still reports `CanPersist == true`. Full suite green.

## 5. Risks

- **Removal fan-out.** Purging on removal (3.3) multiplies a delete by the number of registered
  indices; it is only safe after `RemoveValue` is O(affected keys) (3.4). Land the efficiency fix
  first; gate the purge behind it.
- **Throughput of routing index writes onto the writer.** Bulk index loads that today run
  concurrently on request threads would serialise on the writer. Mitigate with a batch index
  transaction (one publish for many `AddOrUpdate`s) and keep reads lock-free so only writes serialise.
- **WAL ordinal collision.** Adding `WalEntryType` values without coordinating with
  `crash-durability-hardening`/`wal-subgraph-support` could clash on disk. Reserve ordinals jointly;
  values are fixed once shipped (they are the on-disk encoding).
- **Snapshot-publish correctness under the writer.** The FrozenDictionary swap must be a single
  `volatile` store with the reverse map published consistently; a torn publish would expose a bucket
  without its reverse-map entry. Build/validate the new map fully, then publish with one store (the
  same discipline as the adjacency publication `csr-adjacency` calls the concurrency unit).
- **Behaviour change for existing index REST callers.** Making the actions transactional changes their
  failure mapping (not-found → 4xx via `transaction-failure-reasons`) and their timing (waited vs
  fire-and-forget). Keep the returned body shape; migrate any test that asserted the old inline
  semantics.

## 6. Keep (do not regress)

- The `IIndex` plugin contract, `PluginFactory`/`PluginCache` discovery, and the persistence-hardening
  **C9** `CanPersist` capability flag + full R-Tree/Dictionary/Range/Fulltext serialization.
- The **P4** ordered range-scan routing (`TryOrderedRangeIndexScan`) and `RangeIndex._sortedKeys`
  invalidation semantics — the reverse-map removal must invalidate `_sortedKeys` on the same
  key-set-shrink condition it does today.
- The single-writer invariant and lock-free graph reads over the volatile snapshot: index reads must
  become *at least* as concurrent as graph reads, never less.
- The opt-in, off-by-default WAL and its crash/torn-tail guarantees (persistence-hardening Stage D);
  index logging is additive and must not change the WAL-off default path.
