# Consolidation audit: plan

Eight slices, one branch each, merged to `main` in order after the review gate. No GitHub
issues, no PRs. Spec: [spec.md](spec.md); findings: [report.md](report.md).

**Model choreography:** this spec/plan and each slice's review gate run on Claude Fable;
the implementation of the slices runs on Claude Opus. Switch models at the handoffs.

**Per-slice gate (every slice, no exceptions):**

1. `dotnet build fallen-8-core.sln` clean (warnings-as-errors) and
   `dotnet test fallen-8-core.sln` fully green.
2. Web-ui `tsc -b` + `vitest run` green when the slice touches `fallen-8-web-ui`.
3. OpenAPI snapshot regenerated (`pwsh scripts/update-openapi-snapshot.ps1`) only in the
   slices spec section 5 names; anywhere else, a snapshot diff is a stop signal.
4. Pure-consolidation slices assert behavior identity: if a change alters observable
   behavior beyond spec section 4, stop and surface it.
5. Docs build green when docs/ is touched.
6. Review gate (Fable) before merge to main.

## Slice 1: `feature/index-equality-capability` (CA-1 + CA-12) - bug fix, test-first

The only behavior-changing correctness slice; lands first, alone.

1. Reproducing test first: a spatial R-Tree index in a document-link allowlist currently
   passes `ValidateLinkRequest` and silently yields zero links; pin the current wrong
   behavior, then flip the assertion with the fix.
2. Engine: add the equality-capability to the index contract in `fallen-8-core` (positive
   capability, owned by `IIndex`; built-ins implement it: dictionary/range/single/fulltext
   true, vector/spatial false for point-equality linking per `IndexCapabilities`'
   existing derivation). Minor engine version bump + csproj comment.
3. apiApp: derive `IndexCapabilities.Describe` from the engine capability (delete the
   local type-tests); replace the five `is IVectorIndex || is IFulltextIndex` sites in
   `DocumentIngestionService` with the single predicate.
4. CA-12: make the Role builders (VectorRole/FulltextRole/EntityRole) the one
   shape-decision home; `Validate*`/create paths throw off their result.
5. Snapshot regen (400 response docs on the link path); docs: correct the allowlist rule
   in `unstructured-ingestion.md`; one-line pointer in the semantic-layer feature record.

## Slice 2: `feature/error-mapping-single-home` (CA-2, CA-3, CA-7, CA-8) - pure consolidation + spec 4.2

1. CA-2: one reason-to-status mapper (`TransactionFailureReason` extension or
   `ProblemResults.FromFailureReason`); convert the five switches; Bulk keeps its
   documented NotFound-to-400 override; route Embedding's post-precheck rollback and
   `DocumentIngestionService.Enqueue` through it (spec 4.2, snapshot regen).
2. CA-3: `BoundIndexContract.FindConflictForIndex(...)`; call from
   `EmbeddingController.SemanticSearch`, `DocumentSearchService.TryDenseSide`, and the
   by-name loop.
3. CA-7: `EmbeddingProviderProblem.Map(ex)` consumed by EmbeddingController,
   SemanticTraversalHelper, DocumentSearchService.
4. CA-8: `NamespaceProblems.NotFound(name)` consumed by the three 404 sites; extend
   NamespaceEndpointTest to assert the detail string once, through the factory.

## Slice 3: `feature/codegen-env-parity` (CA-4 + CA-24)

1. CA-4: single usings/namespace/signature definition in `CodeGenerationHelper` consumed
   by `BuildValidationSource`; parity test asserting a fragment referencing each supported
   namespace (incl. `Index.Vector` and `context`) both validates and compiles through the
   real path and subgraph generators.
2. CA-24: one `FormatDiagnostics(diags, header, errorsOnly)`; path site aligned to
   errors-only (spec 4.4).

## Slice 4: `feature/contract-guards` (CA-14, CA-21, CA-22, CA-16) - guards + one small fix

1. CA-14: reflection parity test pinning effective JSON field names of REST spec DTOs vs
   MCP write DTOs (vertex/edge/property).
2. CA-21: bulk round-trip test derives from `AllowedLiteralTypes.AllowedNames`.
3. CA-22: api-contract.test.ts fails when an exported route-bearing endpoint is never
   recorded; add the ~17 missing newest-feature calls.
4. CA-16: `_logger ??=` guard in the three legacy index `Load`s (mirror VectorIndex);
   OpenIndex-path load test with a dangling element reference.

## Slice 5: `feature/analytics-direction-helper` (CA-6 + CA-23) - hot path, benchmark-gated

1. Benchmark first: capture analytics timings (existing perf/benchmark harness) on a
   representative graph.
2. CA-6: `VisitByDirection`/`CountByDirection` on `AnalyticsAdjacency` (same ref-struct
   visitor monomorphization); convert the four blocks; TriangleCount/WCC untouched.
3. CA-23: `BudgetGuard.IsExhaustedAt(counter)` (AggressiveInlining) as the one documented
   home of the power-of-two assumption; convert the eight sites; the bare per-vertex check
   at TriangleCounting:90 stays.
4. Benchmark after; any regression: revert and document the duplication as deliberate.

## Slice 6: `feature/vector-rankability` (CA-5) - hot path, benchmark-gated

1. Benchmark first (embedding set/projection throughput path).
2. `VectorRankability Classify(ReadOnlySpan<float>, dimension, metric)` on `VectorIndex`;
   writer projection and RebuildProjection test `== Ok`; AddOrUpdate and the REST validate
   switch on the reason for their distinct messages.
3. Benchmark after; regression = revert.

## Slice 7: `feature/discovery-consolidation` (CA-9, CA-13, CA-10, CA-11)

1. CA-9: `PluginRegistry.EntriesForContract` (name + description); AnalyticsController
   consumes it.
2. CA-13: one contract-to-interface home (interface `Type` on the contract definition or
   `PluginFactory.AvailableBuiltInNames(PluginContract)`); PluginCompiler,
   CollidesWithBuiltIn, AdminController `/status`, SubGraphFactory all consume it.
3. CA-10: `SidecarHttpClient`/`SidecarHealthProbe` under `Ingestion/`; Docling and NLP
   clients supply endpoint, timeout, log label.
4. CA-11: bidirectional kind map (`TryParseKind` beside `KindName`); parser consumes it;
   round-trip test over every `ChangeEventKind` member.

## Slice 8: `feature/consolidation-hygiene` (CA-15, CA-17, CA-18, CA-19, CA-20, comment fix)

1. CA-15: index-family `String.Format` log calls to message templates (exception-message
   `String.Format`s stay).
2. CA-17: delete `BuildSummary`; drop `LinksCreated` from `DocumentSummaryREST`,
   AppJsonContext, JsonSourceGenParityTest; snapshot regen (spec 4.3).
3. CA-18: mcp-server.md f8_documents row lists binding/entities/bind;
   unstructured-ingestion.md op list becomes a pointer to it.
4. CA-19: merge the two `<summary>` blocks on `UpdateProperty` into one.
5. CA-20: delete `Property.cs` + its AppJsonContext registration.
6. Correct the over-general reverse-map comment at `Fallen8.Storage.cs:744-746` (name the
   two documented index-lifecycle 3.4 exceptions).

## Follow-up features (own records, after slice 8)

- `feature/webui-lint-gate` (CA-27): ESLint flat config, `lint` script, CI wiring.
- `feature/nullable-everywhere` (CA-26): `<Nullable>enable` apiApp then engine; its own
  spec/plan; expect real effort.

## Landing

After slice 8 merges: move `features/open/consolidation-audit/` to `features/done/`, mark
each report.md finding fixed/guarded/left, and verify the spec section 6 acceptance list.
