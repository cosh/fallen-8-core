# Schema-agnostic benchmark — spec

Status: **done** · Branch: `feature/schema-agnostic-benchmark` · Created: 2026-07-28

## Motivation

The Studio **Benchmark** tab reported **0 TPS / 0 edges per run** on any graph it did not
generate itself — a loaded sample, a save-game, or the user's own data. The traversal
counter (`ScaleFreeNetwork.CountAllEdgesParallelPartitioner`) filtered out-edges to the
single edge-property-id `"A"`:

```csharp
if (vertices[i].TryGetOutEdge(out outEdge, "A")) { ... }   // "A" = what /generate writes
```

Only `GET /generate` writes edge property `"A"`, so on every other schema the counter found
zero matching edges and the benchmark collapsed to nothing. This realizes the
"benchmarks over arbitrary loaded graphs" future-work item deferred in
[sample-graphs/spec.md](../../done/sample-graphs/spec.md) (§ Benchmark tab).

## Contract

The benchmark **follows every outgoing edge of every vertex, regardless of
edge-property-id**, on whatever graph is currently loaded:

- `ScaleFreeNetwork.TryBench` (behind `GET /benchmark`) walks all out-edge groups of each
  vertex via the public `VertexModel` surface (`GetOutgoingEdgeIds()` +
  `TryGetOutEdgesSpan(...)`), dereferencing each edge's `TargetVertex` so the pass does real
  pointer-following work, not a cached-degree read.
- `edgesTraversed` therefore equals the graph's total edge count (sum of out-degrees),
  independent of any schema. For a `/generate` graph the number is unchanged (all its edges
  carry `"A"`, so old and new agree); for any other graph it is now correct instead of 0.
- **Generation is unchanged**: `/generate` still writes edge property `"A"`. That label is a
  generation implementation detail, no longer a benchmark contract.
- No REST surface change: same routes, methods, and `BenchmarkResultREST` shape. Only the
  XML `<remarks>` wording changes (OpenAPI snapshot regenerated).

## Non-goals

- No per-label / directional / filtered benchmark modes — one number: raw edge-traversal
  throughput over the whole loaded graph. (A revisit trigger, not v1.)
- No in-edge traversal (summing out-degrees already counts every edge exactly once).
- No new engine API. The benchmark lives in the apiApp and uses only the existing public
  `VertexModel` accessors; it does not reach the engine-internal `GetRawOutEdges`.

## Impact on existing features (cross-feature sweep)

- **Engine**: no change. Uses existing public `VertexModel.GetOutgoingEdgeIds` /
  `TryGetOutEdgesSpan` (feature traversal-allocations). No new contract → no engine→REST→MCP
  propagation obligation.
- **REST contract / OpenAPI snapshot**: routes/methods/DTOs unchanged; `/generate` and
  `/benchmark` `<remarks>` reworded. `features/done/web-ui/openapi-v0.1.json` regenerated
  (description text only).
- **MCP**: `/benchmark` and `/generate` remain conscious deferrals ("development/benchmark
  tooling, not an agent surface") in `McpRestCoverageTest`. No route change → no MCP action.
- **Studio UI**: `BenchmarkScreen.tsx` (file comment + generation/throughput helper text) and
  `fieldHelp.ts` (`benchIterations`) reworded. `data-testid`s untouched, so the e2e specs
  (`studio.spec.ts`, `first-run.spec.ts`) are unaffected. Screenshot
  `docs/images/screen-benchmark.png` recreated (helper copy changed).
- **Docs**: `docs/studio.md` (Benchmark section + nav table row) rewritten;
  `features/done/sample-graphs/README.md` (living doc) corrected. `docs/samples.md` and
  `docs/rest-api.md` needed no change (they list the endpoint, not the old limitation).
  `features/done/sample-graphs/spec.md` is a historical record and is left as-is (it now
  points at realized future work).
- **NL-assist dataset/eval**: not affected (no new endpoint, filter grammar, or dataset).
- **Architecture diagrams**: no channel/layer/deployable change → no diagram update.
- **Persisted recipes / stored queries**: not affected.

## Tests

- `BenchmarkTest.Bench_FollowsEveryOutEdge_RegardlessOfSchema` (new): a graph with four edges
  under `"KNOWS"`/`"LIKES"` (never `"A"`) plus sink vertices; asserts `edgesTraversed == 4`.
  This fails (`== 0`) before the fix and passes after — the precise regression pin.
- `BenchmarkTest.ScaleFreeNetwork_ShouldCreateExpectedGraph` and
  `BenchmarkEndpointTest.*` continue to pass (a `/generate` graph is all-`"A"`, so the count
  is identical old vs new); the endpoint test's stale "property A pairing" comment is fixed.
