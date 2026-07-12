# Core Storage Representation — Specification

> **Status:** Planned. The headline finding of the repository review, flagged independently by
> four of six teams. This is the single biggest lever for both **performance** and **memory**,
> and it underpins the `engine-performance` and `memory-footprint` themes.

## 1. Problem

The engine stores everything in `System.Collections.Immutable` collections, which are
**balanced trees, not arrays**:

- Master store: `ImmutableList<AGraphElementModel> _graphElements` (`Fallen8.cs:54`).
- Per-vertex adjacency: **two** `ImmutableDictionary<string, ImmutableList<EdgeModel>>`
  (`VertexModel.cs:45,50`).
- Per-element properties: `ImmutableDictionary<string, object>` (`AGraphElementModel.cs:64`).

Consequences:

- **Performance.** Element ids are dense and equal to the list index, and lookups are
  `_graphElements[id]` (`Fallen8.cs:247,267,288,560,595`) — but `ImmutableList`'s indexer and
  `Add` are **O(log n)** with pointer chasing. Every `TryGetVertex` (the hottest REST op),
  every edge wire-up (two index lookups + an add), every scan, and PLINQ partitioning over the
  tree pay a tree cost where an array is O(1).
- **Memory.** Each `ImmutableList` node is ~48 B of tree pointers on top of the payload; each
  edge sits in three such lists (source out, target in, master) ≈ **~144 B/edge** of pure
  overhead, and the two per-vertex dictionaries add **~200–400 B/vertex**. Estimated
  ~1.4 GB → ~0.3 GB structural overhead at 1M vertices / 10M edges.
- **GC.** A single `Add` allocates ~log n tree nodes; wiring a degree-d vertex is O(d log d)
  allocations.

## 2. Why it's safe to change

Every mutation goes through the **single-writer** `TransactionManager` thread; readers capture
the current collection reference and get a consistent snapshot without locks. This lets us
replace the immutable trees with **mutable structures published by an atomic reference swap**
(copy-on-write at the snapshot boundary), preserving today's lock-free read semantics exactly.

## 3. Goals / non-goals

**Goals**
- O(1) id→element access and cheap append for the master store.
- Adjacency storage with array-like per-edge cost (~8 B/slot) instead of ~48 B tree nodes, and
  without the per-direction dictionary wrapper when a vertex has one edge type (the common case).
- Preserve: dense id == index, snapshot-stable lock-free reads, `Trim` compaction, and the
  persistence contract (`PersistencyFactory.Load` already produces a flat array).
- Keep public interfaces (`IFallen8*`, `VertexModel`/`EdgeModel` surface) unchanged.

**Non-goals**
- Changing the single-writer model or the transaction API.
- Struct-of-arrays / columnar redesign (possible future; out of scope here).

> **Scope note (adjacency vs. unchanged surface).** The second goal (array-like adjacency without
> the per-direction dictionary wrapper) and the last goal (keep the `VertexModel`/`EdgeModel`
> surface unchanged) are in direct tension: the adjacency collections **are** part of the public
> surface — `VertexModel.OutEdges`/`InEdges` are public fields and `TryGetOutEdge`/`TryGetInEdge`
> return `ImmutableList<EdgeModel>` — so flattening them necessarily changes that surface. This is
> resolved by scoping: the master-store change (first goal) landed under the unchanged-surface
> constraint, while adjacency-flattening is **out of this theme's "unchanged surface" scope** and
> deferred to **Phase 4** as a future, API-versioned change (it requires a public-API-version
> bump). See the plan's "Phase 4 — deferred" section.

## 4. Design sketch

- **Master store:** an append-only **segmented array** behind a single `volatile` holder:
  `sealed class Snapshot { AGraphElementModel[][] segments; int count; }`. Reader: capture holder
  → `segments[id >> S][id & MASK]` (O(1), cache-friendly). Writer (single thread): write the
  spare slot, then publish a new holder with `count+1` (publish `count` last so readers never
  see a half-written slot); allocate a new segment only when the last fills (no whole-array copy,
  no LOH churn). Interim cheaper step: `ImmutableArray<T>` + `Builder` on the bulk/`Load` paths
  (O(1) indexer, cache-friendly scans) while single-appends are coalesced by the tx batching in
  `engine-performance`.
- **Adjacency:** one `List<EdgeModel>`/`EdgeModel[]` per direction; group by edge-property-id
  lazily at query time (the edge already carries `EdgePropertyId`/`Label`), or keep a single
  small `Dictionary<string,List<EdgeModel>>` if O(1) type grouping must stay. Published by
  reference swap on the tx thread.
- **Snapshot-stability:** capture `_graphElements` once per read method (`Fallen8.cs:239/247`
  reads it twice today); mark the holder `volatile` so the snapshot contract is explicit and
  correct on weak-memory (ARM) hardware, not just x86.

## 5. Acceptance criteria

- `TryGetVertex/Edge/GraphElement` are O(1); a microbenchmark shows id-lookup and bulk-insert
  improvements over the tree.
- Measured per-element memory drops materially (target: the ~144 B/edge + ~200–400 B/vertex
  structural overhead largely removed).
- All existing tests pass unchanged; a new concurrency test asserts readers never observe a
  torn/half-published element under concurrent single-writer appends.
- Save/load round-trip unaffected (format unchanged).

## 6. Risks

Concentrated in `Fallen8.cs`, `VertexModel.cs`, `EdgeModel.cs`. Main risk is the publication
memory-ordering (write slot before publishing count/holder) and the `Trim` id-renumbering
interacting with in-flight readers (see `engine-performance` for the `Trim` hazard). Land behind
the existing suite plus new concurrency tests.
