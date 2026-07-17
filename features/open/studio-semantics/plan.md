# Studio semantics — Plan

Companion to [spec.md](./spec.md). Surfaces the element-embeddings + embedding-provider
server surfaces in F8 Studio (`fallen-8-web-ui`), plus one small server addition so the UI
can tell a bound vector index from a raw one. Umbrella: none — a standalone web-UI feature
branched `feature/studio-semantics` off `main`.

Ordering principle: server binding first (the one wire change), then the shared pure logic
and component, then each screen, then tests + docs. Every step ends typecheck-clean with the
vitest suite green.

## Phase 0 — Server: surface the index binding

- [x] `IndexDescriptionREST` gains optional `embeddingName`/`model`; `AdminController.Status`
  populates them from `IVectorIndex` via `GetNamedIndicesSnapshot` (read-locked, null-safe).
- [x] OpenAPI snapshot regenerated (additions-only: two fields); `JsonSourceGenParityTest`
  representative updated; `openapi.d.ts` regenerated.

## Phase 1 — API + state + shared logic

- [x] `types.ts`: `EmbeddingProviderStatsREST`, `EmbeddingWriteSpecification`,
  `EmbedElementSpecification`, `EmbeddingSearchSpecification`, `SemanticTraversalSpecification`;
  `embedding?` on `GraphStatisticsREST`; `embeddingName`/`model` on `IndexDescription`.
- [x] `endpoints.ts`: `putElementEmbedding`/`deleteElementEmbedding` (waitForCompletion=true,
  FR-21), `embedElement`, `embeddingSearch`. (No client GET helper — the studio reads a
  stored embedding off the element's folded reserved properties.)
- [x] `graphShape.ts`: `embeddingProvider(shape)`, `embeddingNames` in `shapeSuggestions`
  (reserved `$embedding:` keys folded out of `propertyKeys`).
- [x] `lib/semantic.ts`: `SemanticDraft`, `buildSemanticSpec` (mirrors server XOR/provider/
  cost/DotProduct/minScore rules), `semanticOwnsVertex{Filter,Cost}`.
- [x] `fieldHelp.ts` keys for every new labeled input.
- [x] api-contract test exercises all new helpers.

## Phase 2 — Components + screens

- [x] `SemanticBlockEditor` (shared) + `DelegateSlot` `disabled` prop.
- [x] Browser: Embeddings tab (set/replace/remove; text-in gated on provider), reserved
  folding + show-reserved toggle.
- [x] Query: bound create options, inventory strip + bound badges, add-vector guard for
  bound indices, semantic search by text.
- [x] Path + Subgraph: wire the semantic block; owned delegate slots go inert; the built
  spec OMITS a fragment the semantic block owns (no server 400); Subgraph stored mode
  disables the block and never builds/validates it; costBySimilarity disabled under BLS
  (path) and cleared when switching to DotProduct/BLS.
- [x] Dashboard: embedding-provider card (unknown / disabled / enabled).

## Phase 3 — Tests, docs, gate

- [x] Tests: `semantic.test.ts`, `semantic-block.test.tsx`, `embedding-browser.test.tsx`,
  `embedding-query.test.tsx`, `path-semantic.test.tsx`, `subgraph-semantic.test.tsx`,
  `dashboard-provider.test.tsx`, `stored-queries.test.ts` (slot-omission cases). Full suite
  265 green; typecheck + production build clean; server suite green.
- [x] README + CLAUDE.md notes; this spec+plan; move to `features/done/` at merge.
- [x] Council review (three prose reviewers: correctness/wiring, spec-fidelity/scope,
  contract/regression). Findings fixed with pinning tests: stale fragment riding along a
  semantic-owned slot (now omitted from the built spec), Subgraph validating the block in
  stored mode (now inline-only), costBySimilarity not gated under BLS (now disabled +
  auto-cleared), fire-and-forget embedding writes (now waitForCompletion=true), dead
  `getElementEmbedding` helper (removed), and the Dashboard card + Subgraph wiring test
  gaps (added). No contract or regression issues; snapshot additions-only.

## Decision / revisit conditions

- **Provider info from on-demand `/statistics`, not a poll** — the spec draft assumed a poll,
  but `/statistics` is a budgeted on-demand pass; text-in controls gate on it and prompt to
  Compute when unknown, consistent with every other shape-derived datalist. Revisit only if
  operators want provider state without a Graph-shape compute (a cheap field on `/status`).
- **Semantic block stays available in Path stored mode** — deliberately kept (the server
  composes `semantic` with a stored path query); only Subgraph disables it in stored mode
  (server 400s that combination).
- **NL assist emitting semantic blocks** — deferred (spec §5 non-goal).
- **Batch embed / client-side embedding / embedding visualisation** — deferred with triggers
  in spec §Non-goals.
