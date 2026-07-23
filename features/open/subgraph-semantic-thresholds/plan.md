# Subgraph semantic thresholds — plan

Phased implementation of [spec.md](spec.md). Each phase ends with a clean build
(warnings-as-errors) and a green `dotnet test`; the OpenAPI snapshot is regenerated in the
phase that changes the contract.

## Phase 1 — contract + compiler (apiApp)

- `PatternSpecification` gains `SemanticMinScore` (`Double?`) with XML docs (spec FR-1).
- `SemanticTraversalHelper` exposes the threshold-closure builder (context + minScore →
  `Delegates.VertexFilter`) so the existing top-level `minScore` filter and the new
  pattern thresholds share ONE implementation — no second scoring path.
- `CodeGenerationHelper.TryGenerateSubGraphDefinition` threads the built
  `SemanticTraversal` into `BuildPattern`; vertex steps install the closure, with the
  FR-2/FR-3 validations (400 texts per spec §5). Edge-type steps reject the field.
- `StoredQueryCompiler` rejects SubGraph template blocks containing thresholds (FR-6).
- Tests (`fallen-8-unittest`):
  - Parity: fragment `context.TrySimilarity(v, out s) && s >= t` vs `semanticMinScore: t`
    produce the same subgraph on a seeded fixture.
  - End-to-end with `EnableDynamicCodeExecution=false` (WebApplicationFactory,
    `builder.UseSetting`): top-level `minScore` + pattern threshold, no fragments → 201.
  - Every §5 error → 400 with the specified reason.
  - Recalculation after data mutation reuses the bound vector with pattern thresholds
    (no provider registered — proves nothing embeds on the write path).
  - Missing-embedding semantics and the L2 direction (≤) on a pattern threshold.
  - Mixed ownership across steps (step 1 fragment, step 3 threshold) is legal.

## Phase 2 — summary echo (apiApp)

- New `SubGraphSemanticSummary` DTO; `SubGraphSummary.Semantic` (nullable), projected
  from the persisted recipe where the resolved block lives (spec FR-7). Verify during
  implementation that `queryText` survives in the recipe alongside the resolved vector
  (the resolver sets `QueryVector` without clearing `QueryText`); if it does not, echo
  without it rather than persisting more.
- Tests: echo present/absent; `queryText` echoed when used; `patternThresholds` naming
  (patternName vs index); the raw vector never serialized.
- Regenerate the OpenAPI snapshot; review the diff (additions only).

## Phase 3 — Studio (fallen-8-web-ui)

- New shared slot-mode component (*match everything / C# fragment / semantic threshold*)
  used by the top-level VERTEXFILTER slot and each Vertex pattern step; the semantic mode
  carries only the threshold — the query lives in the request-level section (spec FR-8).
- Request-level SEMANTIC QUERY section (query source / embedding name / metric + the
  binding notice), active when ≥ 1 slot is in semantic mode; per-screen subheader copy;
  the Path screen keeps its combined block with its own text.
- Draft model: subgraph draft moves from block-local flags to slot modes. Persisted drafts
  from the old shape are RESET (session convenience, not data) — with a one-line release
  note in the commit message rather than a migration.
- "Save as stored query…" blocked with a reason while any semantic mode is active;
  create-button gating mirrors the server rules; subgraph list/detail badge from the echo.
- `fieldHelp` entries updated (membership wording, binding, per-step thresholds); stale
  "filter/cost" copy removed.
- UI tests following the existing component-test patterns (slot ownership structural,
  inert state unrepresentable, save-as-stored guard, badge rendering).

## Phase 4 — docs + sweep closure

- element-embeddings README: "Subgraphs" section gains pattern thresholds + echo; errors
  table gains the §5 rows. It remains the one home for semantic-traversal rules.
- stored-query-library README: one-line registration-rejection note.
- Root README / CLAUDE.md: no changes expected (pointers already go through the feature
  READMEs); verify.
- Check Studio samples for the old block shape; update if referenced.
- Move `features/open/subgraph-semantic-thresholds/` → `features/done/` on merge.
