# Element Embeddings — Plan

Companion to [spec.md](./spec.md). Embeddings become named element state behind one typed
accessor, consumed by traversal (context + declarative semantic block) and by the
`VectorIndex` as a derived projection. Umbrella branch `feature/scan-v2`; feature branch
`feature/element-embeddings`; GitHub issue-tracked; per-phase commits; no merge without
explicit approval.

Ordering principle: the model change and its durability story first (the correctness
stakes), then the shared math, then traversal, then the index projection, then REST + docs.
Every phase ends build-clean with the full suite green.

## Phase 0 — Accessor, storage, write path (FR-1…FR-4)

Intent: an embedding is durable element state, readable through the one coupling point.

- [x] `AGraphElementModel`: `EmbeddingPropertyPrefix`, `TryGetEmbedding` (span over the
  copy-on-write store, plus a hot-path by-property-id variant), `IsValidEmbeddingName`,
  `GetEmbeddingPropertyId`/`TryGetEmbeddingName` helpers. (The mutation primitive turned
  out to already exist: `RestoreProperty` IS "set to exactly this state", so
  `Fallen8.SetEmbeddings_internal` reuses it — no new model mutation member.)
- [x] `SetEmbeddingsTransaction` (batch: element id → name → vector/null, replace
  semantics, batch-first validation), rollback via the property-undo machinery, WAL codec
  entry `SetEmbeddings = 16` (additive; old WALs replay unchanged), change-feed
  `PropertySet` events.
- [x] Tests (`ElementEmbeddingTest`, 15): accessor semantics (vertex + edge,
  absent/wrong-type/name table), replace/remove/intra-batch-last-wins, batch atomicity on
  invalid input, bounds table, WAL replay incl. removals, change-feed event, reserved key
  visible in `GetAllProperties`. (Checkpoint round-trip lands with Phase 3's bound-index
  save/load tests — the property store's persistence is already pinned by existing suites.)

## Phase 1 — Shared similarity primitive (FR-5)

Intent: one scoring implementation, bit-identical everywhere.

- [x] `VectorMath.Score`/`TryScore` in `Index/Vector`; `VectorIndex.TryNearestNeighbors`
  switches its metric dispatch to `VectorMath.Score` (behaviour-preserving refactor).
  `TryScore`'s out is `default` whenever it returns false (no NaN escape, ever).
- [x] Tests (`VectorMathTest`, 5): hand-computed values per metric,
  non-finite/length-mismatch/empty rejection, and the bit-identity pin — `VectorMath` vs.
  index kNN scores for identical pairs under all three metrics; existing `VectorIndexTest`
  suite green untouched.

## Phase 2 — Traversal integration (FR-6…FR-8)

Intent: the query vector reaches every delegate, with and without dynamic code.

- [x] `TraversalContext` (+ `TrySimilarity`); `IPathTraverser` methods gain the context
  parameter; `CodeGenerationHelper` emits it (path + subgraph providers, new usings);
  `DelegateValidationHelper` wrapper matched; `GraphController` call sites updated;
  `GeneratedCodeCache` keying untouched.
- [x] `SemanticTraversalSpecification` DTO (+ `AppJsonContext` + parity representative);
  declarative `minScore` filter and `costBySimilarity` cost as native closures; every
  400 conflict from spec §3.4. Better than planned: the semantic block is handled INSIDE
  `TryGenerateSubGraphDefinition`, so the persisted-recipe compiler binds it identically
  on load/WAL replay — a declarative semantic subgraph is fully durable, not
  delegate-only.
- [x] Tests (`SemanticTraversalTest`, 10): fragment-vs-declarative equivalence on a
  diamond fixture, dynamic-code-off declarative path + subgraph, the 400 table
  (DotProduct cost, zero-norm, bad name/metric, slot conflicts, semantic-on-stored-
  subgraph), stored path query reading the invocation context (and the empty-context
  no-vector case), subgraph recalculation picking up new embedded vertices without
  re-embedding. Full suite green (762).

## Phase 3 — Bound index projection (FR-9, FR-10)

Intent: the index becomes a pure derived cache when bound; unbound stays byte-identical.

- [x] `embeddingName` (+ `model` identity storage for the companion feature) plugin
  options; bound indices: reject explicit REST adds (400), materialize on creation over
  existing data, project on every embedding surface — `SetEmbeddingsTransaction`, raw
  reserved-key property set/remove/restore, element creation with embedding properties
  (the bulk-import path) — all on the writer thread, silent-skip + summarized log on
  invalid vectors; purge on embedding/element removal; header-only `Save` (extended
  header behind a `-1` sentinel, legacy sidecars load unchanged); rebuild-by-scan `Load`.
- [x] Post-WAL-replay correctness (replayed embedding transactions re-project; the
  checkpoint+tail scenario retires the manual re-add workaround).
- [x] Tests (`BoundVectorIndexTest`, 13): projection lifecycle across all write surfaces,
  rolled-back batch untouched, unbound index untouched, kill-and-replay with zero
  operator action (unanchored and checkpoint+tail), header-only save → load rebuild
  (identical ids and scores), model-identity round-trip, invalid-binding creation
  failure, dimension-mismatch skip. Full suite green (775).

## Phase 4 — REST surface, OpenAPI, docs

Intent: the operator/agent contract, typed end to end.

- [ ] `PUT/GET/DELETE /element/{id}/embedding/{name}` with spec §3.6 status codes;
  `semantic` accepted on `/path` and `/subgraph`; full annotations + XML docs.
- [ ] OpenAPI snapshot regenerated (`pwsh scripts/update-openapi-snapshot.ps1`), additions
  only.
- [ ] `features/open/element-embeddings/README.md` (usage: raw-vector and traversal
  recipes, memory table with the ~2× note); update the LIVING vector-index README's
  WAL-gap guidance to point bound-index users at the retired workaround.
- [ ] Tests: endpoint happy paths + every 400/404, `WebApplicationFactory` with
  dynamic code off, OpenAPI document test green.

## Phase 5 — Gate

- [ ] Full `dotnet test` green; build 0 warnings/0 errors; convention tests green.
- [ ] Council review; fixes with pinning tests; merge to `feature/scan-v2` only after
  explicit approval; `features/open/element-embeddings/` → `features/done/` at merge
  to `main`.

## Progress

- [ ] Phase 0 — accessor, storage, transaction, WAL
- [ ] Phase 1 — VectorMath bit-identity
- [ ] Phase 2 — traversal context + declarative block
- [ ] Phase 3 — bound index projection
- [ ] Phase 4 — REST + snapshot + docs
- [ ] Phase 5 — gate

## Decision / revisit conditions

- **Reserved property over dedicated field** — revisit when profiling shows the property
  lookup hurting a real semantic-traversal workload; the accessor confines the change.
- **Context as factory parameter** — chosen for the process-wide fragment-keyed traverser
  cache; revisit only if a future per-request-compiled surface appears.
- **Bound = auto-projected, unbound = explicit** — the declared exception to the
  no-auto-indexing family rule; revisit per-element opt-out only on real demand.
- **No semantic block on stored subgraph invocation** — revisit with parameterized
  stored templates.
