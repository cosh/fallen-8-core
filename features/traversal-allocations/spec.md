# Traversal Allocations — Specification

> **Status:** Planned (P2 performance) — from the 2026-07 principal-architect & performance review.
> BFS (BLS) and Dijkstra allocate heavily per traversed edge, and the public traversal read surface
> allocates a wrapper per lookup; cut per-hop allocations to ~0 and give library consumers the
> allocation-free read the in-engine hot paths already enjoy — without touching the deferred
> `Path`/`PathElement` rewrite.

## 1. Problem / current state

The two shortest-path algorithms and the public vertex adjacency surface are correct and reasonably
fast, but they churn short-lived heap objects on the hottest loop — one per traversed edge — plus a
handful of intermediate lists per expanded vertex. None of it is retained; it is pure GC pressure
that scales with the frontier size. The line references below are current in this tree.

| # | Issue | Location | Effect |
|---|-------|----------|--------|
| A1 | BLS frontier expansion allocates **two heap objects per traversed edge**: a `FrontierElement` (class, ~40 B) and an `EdgeLocation` (class, ~32 B) | `BidirectionalLevelSynchronousSSSP.cs:955-960,949-953` | ~72 B/edge of garbage on every level; a 1M-edge frontier level ≈ 80 MB short-lived before any path is built |
| A2 | Each expanded vertex builds **three intermediate `List<FrontierElement>`**: `GetValidIncomingEdges` and `GetValidOutgoingEdges` each allocate one, and `GetLocalFrontier` allocates a third and `AddRange`-copies both into it — only to be folded into the frontier dict and dropped | `BidirectionalLevelSynchronousSSSP.cs:630,757,881-884` | 3 lists + a full copy per expanded vertex, on top of A1 |
| A3 | `VertexPredecessor` **eagerly allocates two `List<EdgeLocation>`** (`Incoming`/`Outgoing`) for every frontier vertex, even though most frontier vertices are reached from a single direction | `BidirectionalLevelSynchronousSSSP.cs:943-947` | 2 list objects per frontier vertex, one usually empty |
| A4 | `EdgeLocation` carries a redundant `EdgePropertyId` string that `EdgeModel` already holds | `BidirectionalLevelSynchronousSSSP.cs:951-952` vs `EdgeModel.cs:97` | a redundant field on the hottest object |
| B1 | Dijkstra `GetNeighbours` **re-materialises + re-sorts** all in+out edges into a fresh `List<NeighbourStep>` and `Sort()`s it once per `(vertexId, hops)` state — so the SAME vertex is re-expanded (materialise + filter + sort) up to `MaxDepth` times | `WeightedDijkstraShortestPath.cs:560-623,272` | O(degree · log degree) redundant work + a list per dequeue, repeated per hop level for the same vertex |
| B2 | The Dijkstra search state stores only the vertex **id**, forcing a `TryGetVertex` map lookup on every dequeue to recover the `VertexModel` | `WeightedDijkstraShortestPath.cs:266-270` | a dictionary lookup per dequeue that the expansion already had in hand |
| C1 | `TryGetOutEdge`/`TryGetInEdge` return `Array.AsReadOnly(group)` — a fresh `ReadOnlyCollection<EdgeModel>` **per call** | `VertexModel.cs:406,425` | one wrapper alloc per lookup for a library consumer / the `ScaleFreeNetwork` "edges/second" benchmark (`ScaleFreeNetwork.cs:200-207`) |
| C2 | `OutEdges`/`InEdges` allocate a `ReadOnlyEdgeContainer` per get, and each enumeration yields a fresh `ReadOnlyCollection<EdgeModel>` per group; reading an edge through `IReadOnlyList<EdgeModel>` costs **two virtual indirections** (interface dispatch → `ReadOnlyCollection` indexer → array) | `VertexModel.cs:373-393,518-589` | per-get + per-group allocation, and slow per-edge reads for consumers |
| D1 | `PathHelper.GetValidEdges` is **dead** (zero production callers) yet carries the worst allocation pattern of all: `List<Tuple<string, IEnumerable<EdgeModel>>>` + a `Tuple` + a `List<EdgeModel>` per group | `PathHelper.cs:50-124` | dead code that models the anti-pattern; only referenced by a doc note |
| D2 | `GetAllNeighbors` starts from an empty `List<VertexModel>` and grows by reallocation though the exact final size is `OutDegree + InDegree` | `VertexModel.cs:302-337` | avoidable list regrowth on a public read |

**Why the in-engine paths are already fast (and consumers are not).** `adjacency-flattening` (landed)
gave `VertexModel` an immutable `EdgeAdjacency` with a **struct** `Enumerator`
(`EdgeAdjacency.cs:302-340`) that `foreach`es the groups allocation-free, and the algorithms read it
through the internal `GetRawOutEdges`/`GetRawInEdges` (`VertexModel.cs:238,247`). So the raw walk is
already zero-alloc — but the algorithms then re-wrap every edge into `FrontierElement`/`EdgeLocation`/
`NeighbourStep` and intermediate lists (A1–A3, B1), and the **public** surface (C1/C2) still forces a
wrapper per lookup because it only exposes `IReadOnlyList`, so a library consumer cannot reach the
fast path at all.

**Relationship to the prior BLS work.** `engine-performance` P6 landed the *bounded* reconstruction
(honour `maxResults`, early-terminate, `CapPaths`) and does store predecessors directly in the
frontier `Dictionary<VertexModel, VertexPredecessor>` — that part of P6's intent is real. What did
**not** get removed is the per-edge object + three-list materialisation on the *expansion* path
(A1–A3) that feeds that dictionary. This feature finishes the expansion-allocation half of P6's
intent. It does **not** touch the `Path`/`PathElement` **copy-on-extend → parent-pointer**
reconstruction rewrite, which `engine-performance-followups` (P6) measured and **definitively
deferred**; that decision stands and is out of scope here (see §2, §6).

## 2. Goals / non-goals

**Goals**

- Cut BLS frontier-expansion allocations to **~0 per traversed edge** (A1–A4): no `FrontierElement`
  class, no `EdgeLocation` class, no per-vertex intermediate lists — stream the `EdgeAdjacency`
  enumerator straight into the frontier dictionary.
- Eliminate Dijkstra's per-expansion re-materialise-and-re-sort of the same vertex (B1) and the
  redundant per-dequeue vertex lookup (B2), with the search results (single path and Yen's K paths,
  including ordering/determinism) **byte-identical**.
- Give library consumers an **allocation-free public read shape** for adjacency that reaches parity
  with the internal fast path (C1/C2), without exposing the mutable backing array for mutation.
- Remove the dead anti-pattern (D1) and presize the one public read that has a known size (D2).

**Non-goals**

- **Not** the `Path`/`PathElement` copy-on-extend → parent-pointer reconstruction rewrite. That is
  `engine-performance-followups` P6, **definitively deferred** (measured low reward, high risk at the
  reversal seam). This feature must leave path *reconstruction* and the public `Path` surface exactly
  as they are. Reference it; do not reopen it.
- No change to path/traversal **results**, ordering, determinism, or filter semantics (BLS and
  Dijkstra), and no change to the transaction/read model.
- No CSR/columnar adjacency (`csr-adjacency` — assessed and SKIPPED). This works within the existing
  per-vertex immutable `EdgeAdjacency`.
- No new algorithm and no public `Path` version bump.

## 3. Design sketch

### 3.1 BLS: stream the expansion, no per-edge objects (A1–A4)

- **`FrontierElement` and `EdgeLocation` → `readonly struct`.** Dijkstra's `NeighbourStep`
  (`WeightedDijkstraShortestPath.cs:702-718`) already proves the pattern in the sibling file: a small
  `readonly struct` passed by value, never heap-allocated. Convert both. `VertexPredecessor` then holds
  `List<EdgeLocation>` of **values** (no per-element boxing / heap object).
- **Drop `EdgeLocation.EdgePropertyId` (A4).** The edge-property-id an edge is stored under equals
  `edge.EdgePropertyId`: an edge is created with the same `edgePropertyId` local that is then passed to
  `AddOutEdge`/`AddIncomingEdge` on both endpoints (`Fallen8.cs:955,964,967` single; `1006,1015,1018`
  batch), so the group key and `EdgeModel.EdgePropertyId` (`EdgeModel.cs:97`) are the same value on
  both the source's out-group and the target's in-group. `EdgeLocation` becomes just `{ EdgeModel Edge }`
  (or is dropped entirely in favour of storing the `EdgeModel` directly); reconstruction reads
  `edgeLocation.Edge.EdgePropertyId` where it used the stored copy. **Verify** with an equality
  assertion in the guardrail test before deleting the field.
- **Fold `GetValidIncomingEdges`/`GetValidOutgoingEdges`/`GetLocalFrontier` into `GetGlobalFrontier`
  (A2).** Instead of each helper returning a `List<FrontierElement>` that is `AddRange`d together and
  re-iterated, iterate `vertex.GetRawInEdges()`/`GetRawOutEdges()` via the struct `EdgeAdjacency.Enumerator`
  directly inside `GetGlobalFrontier` and, for each edge that passes the (unchanged) edge-property /
  edge / vertex filters and the `visitedVertices.Add(...)` guard, write straight into the frontier
  `Dictionary<VertexModel, VertexPredecessor>`. No `FrontierElement` is ever constructed; the direction
  is a local, not a stored field. Filter order and the `alreadyVisited.Add` semantics stay identical.
- **Lazily allocate `VertexPredecessor` direction lists (A3).** Allocate `Incoming`/`Outgoing` only
  when the first edge in that direction is recorded (or collapse to a single list tagged by direction).
  Most frontier vertices are reached from one direction, so this removes one empty `List` per vertex.

The output of `GetGlobalFrontier` (the `Dictionary<VertexModel, VertexPredecessor>` the reconstruction
consumes) is structurally unchanged, so `CreatePaths`/`CreateToSourcePaths`/`CreatePathsRecusive` and
the landed `maxResults` bounding are untouched.

### 3.2 Dijkstra: memoise neighbours per vertex, carry the `VertexModel` (B1–B2)

- **Memoise `GetNeighbours` per vertex within one `Search` (B1).** A `Search` already exists per call
  and holds no cross-call state (`WeightedDijkstraShortestPath.cs:184-206`). Add a
  `Dictionary<int, List<NeighbourStep>>` (or `Dictionary<VertexModel, ...>`) memo on the `Search`
  instance so the materialise+filter+`Sort()` for a given vertex happens **once** and every later
  `(vertexId, hops)` expansion of that vertex reuses the sorted list. The list is read-only after
  build, so sharing it across states is safe. Because filters/costs are fixed for the whole `Search`,
  the neighbour set for a vertex is invariant across hop levels — the memo changes nothing about the
  result, only how many times it is computed. (Yen's spur searches construct their own `Search`, so
  the memo is naturally scoped and never mixes banned-set variants.)
- **Carry the `VertexModel` in the search state (B2).** The expansion already holds the current
  `VertexModel`; store the neighbour's `VertexModel` on the frontier record so the dequeue does not
  re-`TryGetVertex` by id (`:266-270`). Keep the `(VertexId, Hops)` tuple as the `dist`/`pred`
  dictionary **key** (ids are the stable identity used for banning and reconstruction); only avoid the
  *lookup* by threading the reference. The priority ordering `(Weight, Hops, Sequence)` and the stale-
  entry skip are unchanged, so determinism holds.

### 3.3 Public allocation-free adjacency read (C1–C2)

- Add a public, allocation-free group accessor to `VertexModel`:
  `bool TryGetOutEdgesSpan(out ReadOnlySpan<EdgeModel> edges, string edgePropertyId)` and the incoming
  counterpart. It reads the live `EdgeAdjacency` snapshot once (single volatile read), calls the
  internal `TryGetGroup`, and returns a `ReadOnlySpan<EdgeModel>` over the group array — no
  `ReadOnlyCollection` wrapper, and edge access is a direct indexer, not two virtual calls.
- Optionally add a public **read-only struct enumerator** over the whole adjacency (a public,
  read-only projection of the internal `EdgeAdjacency.Enumerator`) so a consumer can walk *all* groups
  allocation-free, mirroring what the in-engine paths get from `GetRawOutEdges`. The span accessor
  covers the single-group hot path (the `ScaleFreeNetwork` benchmark shape); the enumerator covers
  full traversal.
- **Safety.** The backing `EdgeModel[]` is immutable-after-publish (copy-on-write, single writer;
  `EdgeAdjacency.cs` class doc), so handing out a `ReadOnlySpan`/read-only view over it cannot expose
  mutation: a `ReadOnlySpan` is read-only, and the writer never mutates a published array in place — it
  publishes a new instance. The existing `IReadOnlyList`-returning members stay (source-compatible);
  the span accessor is additive.
- **Coordinate with `supernode-adjacency-build` (capacity variant).** Today an `EdgeAdjacency` group
  array is exact-sized (`Append`/`RemoveById` allocate exactly `length` entries), so a span over the
  whole array is correct. `supernode-adjacency-build` (Planned) proposes over-allocated group arrays
  with a separate logical `count`; when that lands, `TryGetOut|InEdgesSpan` **must slice `[0, count)`**
  and the public enumerator must surface the count, never the spare slots. This is called out there
  (its §3 lists the count-aware consumers); land the span accessor count-ready, or sequence it after
  that change. Do not let a benchmark span leak spare slots.

### 3.4 Cleanups (D1–D2)

- **Delete `PathHelper.GetValidEdges`** (`PathHelper.cs:50-124`) — it has zero production callers
  (only a documentation reference in `path-filter-arity-fix/spec.md`). If a shared valid-edge walker is
  ever wanted, reshape it around the struct `EdgeAdjacency.Enumerator` rather than the
  `Tuple`/`List`-per-group pattern; but the honest move now is deletion.
- **Presize `GetAllNeighbors`** (`VertexModel.cs:302-337`): `new List<VertexModel>((int)(GetOutDegree() + GetInDegree()))`.
  Behaviour (both directions projected through `edge.TargetVertex`, verbatim) is preserved.

## 4. Acceptance criteria

- An opt-in `[TestCategory("Benchmark")]` + `[Ignore]` benchmark (in `fallen-8-unittest`, matching the
  existing convention, e.g. output prefixed `[TRAVBENCH]`) measures, on a scale-free graph:
  - **BLS per-hop allocation → ~0.** Allocated-bytes growth (via `GC.GetAllocatedBytesForCurrentThread`)
    per traversed edge during frontier expansion drops to approximately zero (no `FrontierElement`/
    `EdgeLocation` objects; no per-vertex intermediate lists) versus the pre-change baseline. Numbers to
    be captured on this box.
  - **Dijkstra re-sorts eliminated.** For a source whose reachable vertices are expanded at multiple
    hop levels, `GetNeighbours` (materialise+filter+`Sort`) executes once per distinct vertex, not once
    per `(vertexId, hops)` state — asserted by a call counter or by allocation/time scaling.
  - **Public-API traversal parity.** The public read microbenchmark (the `ScaleFreeNetwork`
    edges/second walk, rerouted through `TryGetOutEdgesSpan`) gains a **double-digit percent** and
    reaches parity with the internal `GetRawOutEdges` fast path. Numbers to be captured on this box.
- **All existing path tests pass unchanged** — `PathTest`, `PathTestEdgeCases`, and
  `WeightedDijkstraPathTest` — with BLS results byte-identical and Dijkstra's single + K-shortest
  results (weights, ordering, determinism) identical.
- A characterization test asserts `edge.EdgePropertyId` equals the group key an edge is stored under
  (both directions), pinning the invariant the A4 field-drop relies on.
- The public `Path` surface, the transaction/read model, and the on-disk format are unchanged (no
  version bump). Existing `VertexModel.OutEdges`/`InEdges`/`TryGetOut|InEdge` members still behave as
  before (the span accessor is additive).

## 5. Risks

- **A4 correctness (dropping `EdgePropertyId`).** If any edge could be stored under a group key
  different from its own `EdgePropertyId`, reconstruction would emit the wrong property id. Verified
  equal today at the two creation sites; guard it with the characterization test before deleting the
  field, and keep the field if the test ever fails.
- **Determinism drift in Dijkstra.** Memoising and threading the `VertexModel` must not change
  neighbour enumeration order or the `(Weight, Hops, Sequence)` priority — the K-shortest tie-breaks
  are locale-sensitive by design (`CandidatePriorityComparer`). Build the memo from the exact same
  sorted `GetNeighbours` output; pin with the unchanged `WeightedDijkstraPathTest`.
- **Span safety under a future capacity variant.** A `ReadOnlySpan` over the raw group array is correct
  only while arrays are exact-sized. Coordinate with `supernode-adjacency-build` so the accessor slices
  `[0, count)` the moment spare capacity exists; otherwise a consumer would read uninitialised slots.
- **`ReadOnlySpan` ergonomics.** A `ref struct` cannot cross `await`/`yield` or live in a field;
  document that the span accessor is for synchronous, in-scope iteration, and keep the enumerator/
  `IReadOnlyList` members for other uses.
- **Struct-size regression.** Over-large `readonly struct`s copied by value can cost more than a
  pointer; `FrontierElement`/`EdgeLocation` are 1–3 fields, so passing by value is a win — keep them
  small.

## 6. Keep (do not regress)

- **The deferred `Path` rewrite stays deferred.** Do not convert `Path`/`PathElement` reconstruction to
  parent pointers, and do not change the public `Path` surface (`engine-performance-followups` P6).
  This feature is strictly about the *expansion/read* allocations, not path *materialisation*.
- **Path/traversal results, ordering, determinism, and filter semantics** — BLS `maxResults` bounding
  and `CapPaths` early termination (landed in `engine-performance` P6), Dijkstra's single/K-shortest
  results and locale-invariant tie-breaks (`weighted-shortest-paths`), and the edge-property/edge/
  vertex filter order — all byte-identical.
- **Lock-free reads / single-writer copy-on-write.** Adjacency is read via one volatile capture of an
  immutable `EdgeAdjacency`; the span/enumerator accessors capture once and never expose the array for
  mutation (`adjacency-flattening`). No public read may hand out a mutable `EdgeModel[]`.
- **The internal fast path.** `GetRawOutEdges`/`GetRawInEdges` + the struct `EdgeAdjacency.Enumerator`
  stay the zero-alloc in-engine walk; the public span accessor reaches parity with it, it does not
  replace it.
- **The existing public read members.** `OutEdges`/`InEdges`/`TryGetOut|InEdge`/`GetAllNeighbors`/
  `GetIncoming|OutgoingEdgeIds` keep their current contracts and return types (the span accessor is
  additive; `GetAllNeighbors` only gains a presized backing list).
</content>
</invoke>
