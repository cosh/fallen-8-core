# Adjacency Flattening — Specification

> **Status:** Planned. This is `core-storage-representation` **Phase 4**, deferred there (and gestured
> at by `memory-footprint`) because it changes the **public** `VertexModel` surface and the tests +
> consumers bound to it — so it needs its own feature and a **public-API-version bump**. It is the
> single biggest *remaining* memory lever after the master-store change already landed.

## 1. Problem

Each vertex holds **two** `ImmutableDictionary<string, ImmutableList<EdgeModel>>` — `OutEdges` and
`InEdges` ([VertexModel.cs:45,50](../../fallen-8-core/Model/VertexModel.cs)). `ImmutableDictionary`
is a hash-array-mapped trie (~100 B container overhead) and each `ImmutableList` is an AVL tree
(~48 B/node on top of the payload). So each edge sits in a source-out list and a target-in list ≈
**~96 B/edge** of tree-node overhead, and the two per-vertex dictionaries add **~200–400 B/vertex**.
The master store already moved off immutable trees (core-storage); adjacency is the remaining
structural overhead the review flagged (~144 B/edge total was the headline; ~48 B/edge — the
master-store slot — landed; **~96 B/edge + the per-vertex dict overhead remain here**).

## 2. Why it needs its own feature (the deferral reason)

- `VertexModel.OutEdges`/`InEdges` are **public fields** typed `ImmutableDictionary<…>`; the public
  methods `TryGetOutEdge`/`TryGetInEdge` return `ImmutableList<EdgeModel>`. Changing the storage
  changes the **public API** → a **major/library-version bump**.
- Three rollback tests inject a poison edge through the immutable API to force a mid-removal fault:
  `v.InEdges = v.InEdges.SetItem("in", v.InEdges["in"].Add(poison))` (`CorrectnessFixesTest` and
  `EnginePerformanceTest`, which share this in-edge idiom) and
  `source.OutEdges = source.OutEdges.SetItem("knows", source.OutEdges["knows"].Add(null))`
  (`CorrectnessFixesFollowupsTest`). They must be **migrated** to a new fault-injection mechanism —
  the removal-rollback coverage must be preserved, not weakened.
- Internal consumers iterate the adjacency directly: `PathHelper`, `BidirectionalLevelSynchronousSSSP`,
  `WeightedDijkstraShortestPath`, `BreathFirstSearchSubgraphAlgorithm`, and the REST DTO
  `Controllers/Model/Vertex`.

## 3. Design

- **Storage:** replace each `ImmutableDictionary<string, ImmutableList<EdgeModel>>` with a
  copy-on-write **`Dictionary<string, EdgeModel[]>`** (grouped by edge-property-id, contiguous arrays
  per group) held behind a **`volatile`** reference. This sheds the AVL tree nodes (~48 B/node →
  ~8 B array slot) and the HAMT wrapper while KEEPING O(1) edge-property-id grouping (which the
  public accessors + path/subgraph consumers rely on). Empty adjacency stays `null` (no container),
  matching the property-store convention.
- **Lock-free reads (mandatory, unchanged semantics):** mutation is single-writer (transaction
  thread) and **copy-on-write** — a new `Dictionary`/array is built and published by ONE `volatile`
  reference assignment; the live structure is never mutated in place; readers capture the field into
  a local once. This is exactly the discipline the immutable dictionary provided and the property
  store (memory-footprint M1) now uses — so a reader always sees a fully-built, self-consistent
  adjacency (correct on weak-memory too, via `volatile`).
- **Public surface (the API break, kept minimal + read-only):** do NOT expose the mutable
  `Dictionary`/array. Replace the public `OutEdges`/`InEdges` fields and the `TryGet*` return type
  with a **read-only** shape — e.g. a read-only view (`IReadOnlyDictionary<string,
  IReadOnlyList<EdgeModel>>`) or accessor methods (`GetOutgoingEdges()`/`TryGetOutEdges(out
  IReadOnlyList<EdgeModel>, id)`). Preserve the semantics of every existing public method
  (`GetInDegree`/`GetOutDegree`/`GetAllNeighbors`/`GetIncoming|OutgoingEdgeIds`/`TryGetOut|InEdge`).
- **Version bump:** bump the `fallen-8-core` library/package version (a breaking change to the
  engine's public model surface). The **REST API** DTO shape (`Controllers/Model/Vertex`, the
  `/vertex` responses) must be **UNCHANGED** — only the internal mapping from the new `VertexModel`
  shape to the DTO changes, so REST clients are unaffected (no `api/v{version}` bump needed).

## 4. Goals / non-goals

**Goals:** shed the ~96 B/edge adjacency tree overhead + the per-vertex dict overhead; preserve
lock-free snapshot-stable reads, all public method semantics, path/subgraph results, and
removal/rollback behaviour; keep the REST contract unchanged; measure the win.

**Non-goals:** changing the transaction model or the master store; a columnar/CSR adjacency redesign
(possible future); changing edge-property-id grouping semantics.

## 5. Acceptance criteria

- Per-edge + per-vertex adjacency memory drops materially vs. the current `ImmutableDictionary`/
  `ImmutableList` (measured by an opt-in benchmark; target: the ~96 B/edge tree overhead + the
  per-vertex dict overhead largely removed).
- A concurrency test asserts readers never observe a torn/half-published adjacency under concurrent
  single-writer edge add/remove (mirrors the master-store guardrail).
- All existing tests pass, with the two poison-injection rollback tests **migrated** (not weakened)
  to the new mechanism and still asserting removal-rollback restoration; path-finding (BLS +
  DIJKSTRA), subgraph, and degree/neighbor/edge-id behaviour is identical.
- Save/load round-trip unaffected (the persistence layer already builds adjacency via
  `Dictionary<string, List<EdgeModel>>` on load — see the internal ctor).
- The REST `/vertex` response shape is byte-unchanged; the library version is bumped.

## 6. Risks

Concentrated in `VertexModel.cs`, the removal path in `Fallen8.TryRemoveGraphElement_private`
(which detaches via `RemoveOutGoingEdge`/`RemoveIncomingEdge` and replays them on rollback), the
path/subgraph consumers, and the two rollback tests. Main risks: (a) the copy-on-write publication
memory-ordering (build-then-`volatile`-swap; never mutate in place); (b) preserving the exact
removal-rollback semantics the correctness-fixes work fixed; (c) not weakening the migrated poison
tests. Land behind the full suite + a new adjacency concurrency test.
