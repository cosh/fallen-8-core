# Schema-agnostic benchmark — plan

Status: **done** · Branch: `feature/schema-agnostic-benchmark`

## Phase 1 — Engine-agnostic traversal (apiApp)

- Rewrite `ScaleFreeNetwork.CountAllEdgesParallelPartitioner` to iterate every out-edge
  group per vertex (`GetOutgoingEdgeIds()` + `TryGetOutEdgesSpan(...)`), dereferencing each
  `TargetVertex`. Remove the `"A"`-only `TryGetOutEdge` filter.
- Keep `edgeProperty = "A"` for generation only; comment it as generation-only.
- Update XML docs on `CreateScaleFreeNetworkAsync`, `TryBench`/counter, and
  `BenchmarkController` `/generate` + `/benchmark` `<remarks>`.

## Phase 2 — Regression tests

- `BenchmarkTest.Bench_FollowsEveryOutEdge_RegardlessOfSchema`: build a non-`"A"`,
  multi-group graph with sinks; assert `edgesTraversed == 4`.
- Fix the stale "property A pairing" comment in `BenchmarkEndpointTest`.

## Phase 3 — UI + docs + snapshot

- Studio: `BenchmarkScreen.tsx` copy + `fieldHelp.ts`.
- Docs: `docs/studio.md`, `features/done/sample-graphs/README.md`.
- Regenerate the OpenAPI snapshot (`pwsh scripts/update-openapi-snapshot.ps1`).
- Recreate `docs/images/screen-benchmark.png`.

## Phase 4 — Verify

- `dotnet build` clean (warnings-as-errors).
- `dotnet test --filter "FullyQualifiedName~Benchmark"` green (new + existing).
- Full suite green (`OpenApiDocumentTest`, `McpContractTest`, `McpRestCoverageTest`,
  `CodeQualityTest` in particular).
- Adversarial review sweep for any remaining "edge property A / generated-graph-only"
  claim; move `features/open/schema-agnostic-benchmark/` → `features/done/`.
