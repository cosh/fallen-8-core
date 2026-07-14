# Supernode Adjacency Build — Specification

> **Status:** Implemented (P1 performance/scale) — from the 2026-07 principal-architect & performance
> review. Adjacency append was O(d) per edge, so building or LOADING a high-degree hub was O(d²) with
> large-object-heap churn; per-edge append is now amortised O(1) with no change to the public surface
> or the copy-on-write reader contract.
>
> Delivered on branch `feature/supernode-adjacency-build`, both composing steps: **Step 1
> (batch-group wiring)** — `EdgeAdjacency.WithEdgesAppended` plus `VertexModel.AddOutEdges/
> AddIncomingEdges` collapse k edges to one vertex/direction into one array build + one volatile
> publish, applied in `CreateEdges_internal` and the deferred-edge load fix-up; **Step 2 (amortised
> capacity)** — each group carries a logical count distinct from its ×2-over-allocated backing array,
> and an append writes a reader-invisible spare slot then publishes `count + 1` sharing the array
> (the master store's `AppendGraphElement` discipline). The enumerator hands out a count-bounded
> `ArraySegment<EdgeModel>` (never the raw array); every read/derived path and the save path slice
> `[0, count)`, so the on-disk bytes are unchanged. The public `VertexModel` surface and the REST
> `/vertex` DTO are byte-identical — **no version bump**. Guarded by `SupernodeAdjacencyBuildTest`
> (batch/single-edge correctness + order, load round-trip, a machine-independent linear-scaling
> allocation ratio), `AdjacencyConcurrencyTest.ConcurrentReaders_DuringMonotonicHubGrowth_*` (the
> shared growing-array race), and the pre-existing adjacency/path/persistence/removal suites; an
> opt-in `SupernodeAdjacencyBuildBenchmark` captures the wall-time/allocation numbers.

## 1. Problem / current state

`adjacency-flattening` (landed) replaced the per-vertex `ImmutableDictionary<string,
ImmutableList<EdgeModel>>` with an immutable `EdgeAdjacency` holding a contiguous `EdgeModel[]` per
edge-property-id group (single-group vertices stored inline, no dictionary). That was a large memory
win, but it measured **retained bytes only** — it never measured the cost of the *append itself* at
high degree, and it traded away the old `ImmutableList`'s O(log d) insert for a full-array copy.

Every edge added grows its group array by one via a whole-group copy:

- `EdgeAdjacency.Append` (`EdgeAdjacency.cs:347`) allocates `new EdgeModel[existing.Length + 1]` and
  `Array.Copy`s the entire existing group on **every** edge add. It is reached from
  `WithEdgeAppended` (`EdgeAdjacency.cs:182`), which `VertexModel.AddOutEdge` /
  `AddIncomingEdge` (`VertexModel.cs:115,128`) call once per edge.
- **Runtime single create:** `CreateEdge_internal` wires one edge via `sourceVertex.AddOutEdge`
  (`Fallen8.cs:964`) + `targetVertex.AddIncomingEdge` (`Fallen8.cs:967`).
- **Runtime batch create:** `CreateEdges_internal` batches the master-store append
  (`AppendGraphElements`, one publish) but **still wires adjacency one edge at a time inside the
  loop** — `sourceVertex.AddOutEdge` (`Fallen8.cs:1015`) + `targetVertex.AddIncomingEdge`
  (`Fallen8.cs:1018`). So k edges landing on one vertex in a single transaction cost k separate
  group copies.
- **Load:** the deferred-edge fix-up loop (`PersistencyFactory.cs:996`) calls
  `interestingVertex.AddIncomingEdge` (`:1008`) / `AddOutEdge` (`:1012`) once per edge that was not
  yet materialised when its owning vertex was loaded. (A vertex's *own* groups are reconstructed in
  one shot through the internal ctor → `EdgeAdjacency.FromListGroups`, which is already O(d) per
  vertex; the O(d²) on load is confined to these deferred edges, and in a parallel bunch load a large
  fraction of a hub's edges are deferred.)

Cost of building one degree-d vertex one edge at a time: **d** array allocations and
`Σ 8·i ≈ 4·d²` bytes copied (8 bytes/reference on 64-bit).

| degree d | array allocations | bytes copied | LOH allocations (array > ~85 KB) |
|----------|-------------------|--------------|-----------------------------------|
| 10 000   | 10 000            | ~400 MB      | ~0 (crosses LOH near d ≈ 10 600)  |
| 100 000  | 100 000           | ~40 GB       | ~89 000 (every array past ≈ 10 600 refs) |

An `EdgeModel[]` crosses the ~85 KB LOH threshold at length > ~10 600 (8 × 10 625 ≈ 85 000). The LOH
is non-compacting, so a supernode build churns tens of thousands of large, short-lived arrays through
it. The project's own `ScaleFreeNetwork` benchmark (`Controllers/Benchmark/ScaleFreeNetwork.cs`)
generates exactly these hubs: all edges share one edge-property-id (`"A"`, `:48`), so every hub is a
**single inline group** with high in-degree, and its target vertices accumulate degree across many
parallel `CreateEdgesTransaction`s. The old `ImmutableList` did this in O(log d) per add; flattening
made it O(d).

This is within the existing per-vertex immutable adjacency. It is **not** a CSR proposal —
`csr-adjacency` was assessed and SKIPPED (edges are first-class objects; live mutation; per-vertex
publication is the concurrency unit), and this work does not reopen it.

## 2. Goals / non-goals

**Goals**

- Make per-edge adjacency append **amortised O(1)**, so building or loading a degree-d hub is O(d),
  not O(d²), with no LOH allocations attributable to the append.
- Two independent, composing changes, **no public-surface change** and the copy-on-write /
  lock-free-reader contract preserved exactly:
  1. **Batch-group wiring** — collapse k appends to one vertex/direction/group in a single
     transaction (or load fix-up) into ONE array rebuild + one volatile publish.
  2. **Amortised capacity** — over-allocate the group array (×2 growth) and append into a spare slot,
     publishing `count + 1`, using the same discipline `AppendGraphElement` already uses for the
     master store.

**Non-goals**

- Any change to the public `VertexModel` surface (`OutEdges`/`InEdges`, `TryGetOut|InEdge`,
  `GetOut|InDegree`, `GetAllNeighbors`, `GetIncoming|OutgoingEdgeIds`), the REST `/vertex` DTO, or the
  on-disk save format. This is an internal representation change; no library/API version bump.
- A CSR / columnar adjacency (see `csr-adjacency` — SKIPPED; do not reopen). A derived read-only CSR
  snapshot remains the only sanctioned future shape, and is out of scope here.
- Changing the transaction model, edge-property-id grouping semantics, or the removal path's cost
  (removal stays a compacting O(d) rebuild — it is not the hot path this feature targets).
- Shrinking/reclaiming spare capacity, or right-sizing arrays on save (the save path slices to the
  logical count; the on-disk bytes are unchanged).

## 3. Design sketch

Two independent steps on the internal `EdgeAdjacency` (`Model/EdgeAdjacency.cs`) and its two callers
(runtime wiring in `Fallen8.cs`, load fix-up in `PersistencyFactory.cs`). Either can land alone;
together they cover both "many edges per transaction" and "one edge per transaction".

### Step 1 — Batch-group wiring (k copies → 1)

Add `EdgeAdjacency.WithEdgesAppended(string edgePropertyId, IReadOnlyList<EdgeModel> edges)`: build
the target group once at final size and publish a single new `EdgeAdjacency` (inline shape kept for a
single group; promote to the map only when a genuinely new key appears — same rule as
`WithEdgeAppended`). Append order within a group is preserved (encounter order), matching the current
enumerator contract.

- **`CreateEdges_internal` (`Fallen8.cs:976`):** after creating the `EdgeModel`s and assigning ids
  (unchanged), group the new edges by `(vertex, direction, edgePropertyId)` and apply one
  `WithEdgesAppended` per group, publishing each vertex/direction adjacency **once**. Keep the master-
  store `AppendGraphElements` single-publish and the up-front all-endpoints-resolved validation
  exactly as they are. A vertex touched under several keys in one batch chains the per-key builds and
  publishes the final instance once (intermediate instances are never published, so no reader sees
  them).
- **Load fix-up (`PersistencyFactory.cs:996`):** `edgeTodo` is keyed by edge id, so the current outer
  loop is per-edge. Restructure the fix-up to first bucket the deferred todos by `(VertexId,
  IsIncomingEdge, EdgePropertyId)`, then apply one `WithEdgesAppended` per bucket. This turns the
  hub's deferred-edge wiring from O(d²) into O(d). The per-vertex own-group reconstruction via
  `FromListGroups` is already batched and stays as-is.
- `CreateEdge_internal` (single edge) keeps calling `AddOutEdge`/`AddIncomingEdge`; Step 2 makes that
  single-edge path amortised O(1) on its own.

### Step 2 — Amortised capacity (per-append amortised O(1))

Give each group an explicit **logical count** distinct from its backing array length, and over-
allocate (×2 growth) so an append usually writes a spare slot instead of copying:

- Represent a group as `(EdgeModel[] array, int count)` — for the inline shape, `_soleGroup` +
  `_soleCount`; for the map shape, a small readonly `EdgeGroup { EdgeModel[] Array; int Count; }`
  value (the map is `Dictionary<string, EdgeGroup>`). `count` is the vertex-visible degree of the
  group; `array.Length >= count` is capacity.
- **Append with spare capacity (`count < array.Length`):** write `array[count] = edge` — a slot no
  reader can observe, because every reader slices `[0, count)` — **then** publish a new `EdgeAdjacency`
  with `count + 1` sharing the **same** array. This is exactly `AppendGraphElement`'s ordering
  (`Fallen8.cs:350-370`): write the spare slot first, publish the incremented count last via the
  releasing volatile store of `_outEdges`/`_inEdges`; an acquiring reader that sees the new count is
  guaranteed to see the fully written slot, and a reader holding the old instance sees the old count
  and never touches the new slot. Single-writer means the slot written is always `>=` any published
  count, so no torn/null read is possible.
- **Append at capacity (`count == array.Length`):** allocate `new EdgeModel[max(count*2, 1)]`, copy
  `[0, count)`, write the new slot, publish `count + 1`. Amortised O(1); ~⌈log₂ d⌉ reallocations
  building a degree-d group (≈17 for d = 100 000) and ~2·d total bytes copied instead of ~4·d².
- **Every read/derived path becomes count-aware (slice `[0, count)`):** `TotalDegree`, `TryGetGroup`,
  `CollectKeys`, the struct `Enumerator` (its `Current` must carry the count, not hand out the raw
  array — see the consumer note below), `RemoveById`, and `Contains`. The public read-only view
  (`ReadOnlyEdgeContainer`) and `TryGetOut|InEdge` must expose only the first `count` elements (an
  `ArraySegment`/count-bounded read-only list wrapper), never the spare slots.
- **In-engine hot-path consumers** iterate `edgeContainer.Value.Length` over the enumerator's raw
  `EdgeModel[]` today: `PathHelper.cs:73,102`, `BidirectionalLevelSynchronousSSSP.cs` (`:643,689,714,
  770,816,841`), `WeightedDijkstraShortestPath.cs` (`:564-595`), the removal cascade / neighbour count
  in `Fallen8.cs` (`:1089,1113,1188,1208`), and the save path
  `PersistencyFactory.cs:1284,1303` (writes `Value.Length` as the persisted group count). With spare
  capacity these must iterate/emit the logical **count**, so the enumerator element changes from
  `KeyValuePair<string, EdgeModel[]>` to a small struct carrying `(key, array, count)` and these call
  sites move from `.Length` to `.Count`. This is a bounded, enumerated set of internal call sites — no
  public type changes.
- **Removal** (`WithEdgeRemovedFromGroup`/`WithEdgeRemovedEverywhere` → `RemoveById`) still produces a
  fresh **compacted** array with `count == length` (no spare). It must scan `[0, count)` only (not
  `array.Length`, which now includes uninitialised spare slots), preserving the poison-null-slot throw
  that the removal-rollback tests rely on (the poison lives within `[0, count)`).

### Interaction notes (called out in the brief)

- **Single-group inline optimisation:** the amortised path applies directly to the inline
  `_soleGroup` — which is the ScaleFreeNetwork hub shape (all edges one key) — so the win lands
  without ever allocating the map. Promotion to the map on a second key is unchanged.
- **Empty-group / 0-length sole group:** removal can already leave an inline shape whose sole group
  has length 0 while its key remains enumerable (`CollectKeys` still yields it, `GetOutgoingEdgeIds`
  still returns it, `TotalDegree` returns 0). With counts, this is `count == 0` over a possibly
  non-empty backing array — the count-aware accessors must treat it as an empty slice (no null-deref),
  and this observable "empty-but-present group" semantics must be **preserved, not silently changed**.

## 4. Acceptance criteria

- An opt-in `[TestCategory("Benchmark")]` + `[Ignore]` benchmark measures **high-degree build** both
  ways — runtime create (single + batch transactions) and load round-trip — across a range of
  degrees, and shows **linear (not quadratic) scaling in degree** for append time and allocated bytes.
  Numbers to be captured on this box.
- The append contributes **no LOH allocations** during a supernode build (verified via an allocation/
  GC probe, e.g. `GC.GetAllocatedBytesForCurrentThread` growth per degree is roughly linear and no
  `EdgeModel[]` on the append path exceeds ~85 KB except the O(log d) doubling reallocations, which
  are the whole point).
- Building a degree-d hub performs **O(log d)** group reallocations, not d (asserted by counting
  reallocations or by the linear-bytes assertion), and k edges to one vertex in a single transaction
  perform **one** publish per vertex/direction, not k.
- **Load round-trip of a supernode is correct and fast:** save a graph containing a high-degree hub,
  load it, and assert the rehydrated hub has the exact same degree, edge set, edge order, and
  endpoints — and that the load-time deferred fix-up is O(d), not O(d²) (linear-scaling assertion).
- All existing adjacency, concurrency, path (BLS + Dijkstra), subgraph, persistence, and removal-
  rollback tests pass unchanged. In particular the lock-free concurrency guardrail (readers never
  observe a torn/half-published adjacency under single-writer add/remove) still holds with the shared-
  array + growing-count publication.

## 5. Risks

- **Shared mutable backing array (the crux).** Over-allocation means multiple immutable
  `EdgeAdjacency` instances share one array while `count` grows. Correctness depends on the exact
  `AppendGraphElement` ordering — write the spare slot (index `>=` every published count) *before* the
  releasing volatile publish of the count — and on it being single-writer. Mitigate by mirroring the
  master-store code and its concurrency test, and by an adjacency concurrency test driving readers
  against a growing hub.
- **Count/length confusion across the ~7 consumer call sites.** Any path that still reads
  `array.Length` after over-allocation would iterate into uninitialised spare slots (null-deref or
  over-count) — including the save path emitting a wrong persisted count. Mitigate by changing the
  enumerator element type so the raw array is no longer handed out bare (the compiler flags the call
  sites), and by the load round-trip + path/subgraph tests.
- **Preserving the poison-null-slot throw.** `RemoveById` must scan `[0, count)`; scanning
  `array.Length` would trip on spare nulls (wrong throw) or miss the poison. The migrated
  fault-injection rollback tests (`InjectRawOut|InEdgeForTesting`) pin this.
- **Empty-group semantics drift.** The count-aware accessors must keep a degree-0-but-present group
  enumerable exactly as today (see §3); a subtle change here would alter `GetOutgoingEdgeIds` /
  degree behaviour.
- **Save-format stability.** The save path must slice to `count`, so the on-disk bytes are identical
  to today — no `formatVersion` bump (see `persistence-hardening`). A benchmark artefact (spare
  slots) leaking into the file would be a silent format change.

## 6. Keep (do not regress)

- **Lock-free reads / single-writer copy-on-write.** Every mutation still publishes one immutable
  `EdgeAdjacency` by a single volatile assignment; readers capture the field once and see a fully
  built, self-consistent adjacency (correct on weak-memory hardware). The shared-array growth must not
  weaken this — it is the same contract as `AppendGraphElement`.
- **The `adjacency-flattening` memory win.** Inline single-group storage, contiguous per-group arrays,
  empty → `null`, and the map fallback only for genuinely multi-group vertices all stay. Amortised
  capacity trades a little transient headroom for build speed; steady-state retained bytes must not
  regress materially (spare capacity is at most ~2× the group array, which is small next to the
  `EdgeModel` bodies).
- **Public surface and REST contract.** `OutEdges`/`InEdges`, `TryGetOut|InEdge`, degree/neighbour/
  edge-id semantics, and the `/vertex` DTO are byte-unchanged; no version bump.
- **Removal-rollback behaviour.** The affected-key return lists and the pre-publish poison throw that
  `Fallen8.TryRemoveGraphElement_private` and the correctness-fixes tests depend on are preserved.
- **Enumeration order.** Groups in stable order; within a group, append order (a poison appended last
  is enumerated last).
