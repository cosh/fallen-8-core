# Traversal Allocations — Plan

Companion to [spec.md](./spec.md). Prove the allocations first with an opt-in benchmark + a
behaviour-pinning characterization test, then land the four independent changes (BLS expansion,
Dijkstra memoisation, public read shape, cleanups) in dependency order, then measure and document.

GitHub issue: to be opened (label: `feature`). Feature branch: `feature/traversal-allocations`.

## Phase 0 — Baseline & guardrails

Intent: capture the per-hop allocation and the public-read cost as they are today, and pin the
behaviour the changes must preserve, so the wins are measured and the results provably unchanged.

- [ ] Add `fallen-8-unittest/TraversalAllocationsBenchmark.cs`: `[TestClass]` with opt-in
  `[TestMethod]` + `[TestCategory("Benchmark")]` + `[Ignore]` methods (convention per
  `EnginePerformanceFollowupsBenchmark.cs`), output prefixed `[TRAVBENCH]`, built on a scale-free
  graph (reuse `ScaleFreeNetwork` shape / `TestLoggerFactory.Create()`).
- [ ] Benchmark: BLS `TryCalculateShortestPath` allocated-bytes per traversed edge
  (`GC.GetAllocatedBytesForCurrentThread` delta / edges expanded) — baseline "to be captured on this box".
- [ ] Benchmark: Dijkstra `GetNeighbours` invocation count vs. distinct expanded vertices for a
  multi-hop source (instrument via a counter behind the benchmark), and total search time — baseline
  "to be captured on this box".
- [ ] Benchmark: public read walk (`ScaleFreeNetwork.Bench`-style edges/second) through
  `TryGetOutEdge` (`IReadOnlyList`) vs. the internal `GetRawOutEdges` fast path — baseline gap "to be
  captured on this box".
- [ ] Characterization test (MSTest, arrange/act/assert): for a created graph, assert every edge's
  `EdgePropertyId` equals the group key it is stored under on BOTH the source out-adjacency and the
  target in-adjacency — pins the invariant the A4 field-drop relies on.
- [ ] Confirm the current path suites are green as the byte-identical baseline: `PathTest`,
  `PathTestEdgeCases`, `WeightedDijkstraPathTest`.

## Phase 1 — BLS: streaming, zero-alloc frontier expansion (A1–A4)

Intent: eliminate the per-edge objects and per-vertex intermediate lists without changing the frontier
dictionary the reconstruction consumes.

- [ ] Convert `FrontierElement` and `EdgeLocation` to `readonly struct`
  (`BidirectionalLevelSynchronousSSSP.cs:955-960,949-953`), modelled on `NeighbourStep`
  (`WeightedDijkstraShortestPath.cs:702-718`).
- [ ] Drop `EdgeLocation.EdgePropertyId` (guarded by the Phase 0 characterization test); reduce
  `EdgeLocation` to `{ EdgeModel Edge }` (or store the `EdgeModel` directly) and read
  `edge.EdgePropertyId` in reconstruction (`CreateAPath`, `CreatePaths`, `CreateToSourcePaths`,
  `CreatePathsRecusive`).
- [ ] Rewrite `GetGlobalFrontier` to iterate `GetRawInEdges()`/`GetRawOutEdges()` via the struct
  `EdgeAdjacency.Enumerator` directly and write straight into the
  `Dictionary<VertexModel, VertexPredecessor>`, applying the edge-property/edge/vertex filters and the
  `visitedVertices.Add` guard inline. Remove `GetLocalFrontier`, `GetValidIncomingEdges`,
  `GetValidOutgoingEdges` (`:876-887,622-738,749-865`) — no `List<FrontierElement>`, no `FrontierElement`.
- [ ] Make `VertexPredecessor` (`:943-947`) allocate its `Incoming`/`Outgoing` lists lazily (only on
  first edge in that direction).
- [ ] Keep `CreatePaths`/`CreateToSourcePaths`/`CreatePathsRecusive` and the `maxResults` bounding
  untouched (the frontier dict shape is preserved).

## Phase 2 — Dijkstra: memoise neighbours, carry the vertex (B1–B2)

Intent: compute each vertex's sorted neighbour list once per search and stop re-looking-up the vertex
per dequeue, with identical results.

- [ ] Add a per-`Search` memo (`Dictionary<int, List<NeighbourStep>>`) so `GetNeighbours`
  (`WeightedDijkstraShortestPath.cs:560-623`) materialises+filters+`Sort()`s a vertex once; later
  `(vertexId, hops)` expansions reuse the cached sorted list (`:272`).
- [ ] Thread the neighbour's `VertexModel` through the frontier record so the dequeue does not
  re-`TryGetVertex` by id (`:266-270`); keep `(VertexId, Hops)` as the `dist`/`pred` key.
- [ ] Verify the memo is scoped per `Search` (Yen's spur searches build their own `Search`), so
  banned-set variants never share a memo.
- [ ] Leave the priority ordering `(Weight, Hops, Sequence)`, stale-entry skip, `RemoveLoops`,
  `Signature`, and `CandidatePriorityComparer` untouched.

## Phase 3 — Public allocation-free adjacency read (C1–C2)

Intent: let library consumers reach the internal fast path without a per-lookup wrapper or slow
per-edge reads.

- [ ] Add `public bool TryGetOutEdgesSpan(out ReadOnlySpan<EdgeModel> edges, string edgePropertyId)`
  and `TryGetInEdgesSpan(...)` on `VertexModel` (single volatile snapshot read → `EdgeAdjacency.TryGetGroup`
  → `ReadOnlySpan<EdgeModel>` over the group array). XML `<summary>`/`<returns>`; keep the existing
  `TryGetOutEdge`/`TryGetInEdge` (`:401,420`) unchanged.
- [ ] (Optional, if the enumerator variant is chosen) add a public read-only struct enumerator over the
  whole adjacency, projecting the internal `EdgeAdjacency.Enumerator` read-only.
- [ ] Make the accessor count-ready for `supernode-adjacency-build`: slice by the logical group count,
  which today equals the array length — leave a comment tying it to that feature's capacity variant so
  it slices `[0, count)` when spare capacity lands.
- [ ] Reroute the `ScaleFreeNetwork` edges/second walk (`ScaleFreeNetwork.cs:200-207`) through
  `TryGetOutEdgesSpan` to demonstrate parity (benchmark path only; not a behaviour change).

## Phase 4 — Cleanups (D1–D2)

Intent: remove the dead anti-pattern and the one avoidable regrow.

- [ ] Delete `PathHelper.GetValidEdges` (`PathHelper.cs:50-124`) — zero production callers; update the
  stale doc reference in `path-filter-arity-fix/spec.md` if it dangles.
- [ ] Presize `GetAllNeighbors` (`VertexModel.cs:302-337`) to
  `new List<VertexModel>((int)(GetOutDegree() + GetInDegree()))`, behaviour verbatim.

## Measure & document

Intent: prove the wins and record them next to the deferrals this touches.

- [ ] Re-run the Phase 0 benchmarks; record BLS per-hop bytes → ~0, Dijkstra re-sort count, and the
  public-read parity percentage in this plan's Progress notes ("captured on this box").
- [ ] Run the full suite (`dotnet test`); confirm `PathTest`/`PathTestEdgeCases`/
  `WeightedDijkstraPathTest` are byte-identical and nothing else regresses.
- [ ] Update the feature branch PR (`Closes #<n>`) with the measured numbers; note the `Path`-rewrite
  deferral was respected.

## Progress

- [ ] Phase 0 — baseline benchmark + `EdgePropertyId`==group-key characterization test + green path suites
- [ ] Phase 1 — BLS struct `FrontierElement`/`EdgeLocation`, dropped `EdgePropertyId`, streamed
  `GetGlobalFrontier`, lazy `VertexPredecessor` lists
- [ ] Phase 2 — Dijkstra per-`Search` neighbour memo + `VertexModel` carried in state
- [ ] Phase 3 — public `TryGetOut|InEdgesSpan` (+ optional read-only struct enumerator), count-ready
- [ ] Phase 4 — delete dead `PathHelper.GetValidEdges`; presize `GetAllNeighbors`
- [ ] Measure & document — numbers captured; full suite green; PR updated

## Decision / revisit condition

- **`Path`/`PathElement` parent-pointer reconstruction (`engine-performance-followups` P6): stays
  deferred.** This feature deliberately stops at the frontier-expansion and read allocations and does
  not alter path materialisation or the public `Path` surface. Reopen the parent-pointer rewrite only
  under its own theme, and only if a measured benchmark shows reconstruction (not expansion) allocation
  is the dominant remaining cost and a byte-identical rewrite past the reversal seam is shown safe.
- **Public span accessor vs. `supernode-adjacency-build` capacity variant.** The `ReadOnlySpan` over a
  raw group array is correct while arrays are exact-sized. If `supernode-adjacency-build` (over-allocated
  arrays + logical `count`) lands first or concurrently, the accessor must slice `[0, count)`; sequence
  the two so the span never exposes spare slots.
</content>
