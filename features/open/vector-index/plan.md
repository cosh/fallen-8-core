# Vector Index — Plan

Companion to [spec.md](./spec.md). A new `Index/Vector` family: exact SIMD brute-force kNN
over `float[]` embeddings, plugged into the existing index creation, lifecycle, persistence,
and REST surfaces. Feature branch: `feature/vector-index` (branch-only workflow — no GitHub
issue/PR).

Ordering principle: land the in-engine index with its lifecycle guarantees first (the part
with correctness stakes), then the query math, then persistence, then the REST surface, then
the measurement + docs that back the ANN non-goal trigger. Every phase ends build-clean with
the full suite green.

## Phase 0 — Family skeleton & write-side contract

Intent: a registered, creatable, lifecycle-correct index that cannot query yet.

- [ ] `fallen-8-core/Index/Vector/`: `VectorDistanceMetric` enum, `IVectorIndex : IIndex`
  (with `Dimension`, `Metric`; `TryNearestNeighbors` may throw `NotImplementedException`
  until Phase 1), `VectorIndex` sealed plugin (MIT headers, `AThreadSafeElement` like the
  family).
- [ ] `Initialize`: parse + validate `dimension` (1–4096) and `metric` plugin options;
  invalid → throw (surfaces as `TryCreateIndex == false`, logged). `MaxDimension`/`MaxK`
  constants.
- [ ] SoA storage: flat `float[]` slab (doubling growth), parallel `AGraphElementModel[]`,
  reference-keyed `Dictionary<AGraphElementModel, int>` slot map; swap-last removal.
- [ ] `IIndex` members: `AddOrUpdate` (dimension check, zero-norm-under-cosine check,
  replace-on-re-add), `RemoveValue` O(1), `TryRemoveKey`/`TryGetValue` exact-match scans,
  `GetKeys`/`GetKeyValues` diagnostic enumerations, counts, `Wipe`, `Dispose`.
  `CanPersist => false` until Phase 2 delivers `Save`/`Load` (the C9 capability flag keeps a
  mid-feature checkpoint honest: the index is skipped, never half-serialized).
- [ ] Tests: plugin discovered (`GetAvailableIndexPlugins`), creation via
  `IndexFactory.TryCreateIndex` with good/bad options, add/replace/remove/wipe/count
  semantics, wrong-dimension + zero-norm adds ignored-and-logged, slot-map integrity after
  swap-last removals (add/remove churn).

## Phase 1 — kNN core (TensorPrimitives)

Intent: the actual feature — exact, deterministic, bounded top-k.

- [ ] `System.Numerics.Tensors` package reference in `fallen-8-core` (exact 10.0.x pinned).
- [ ] `TryNearestNeighbors`: input validation (k ∈ [1, MaxK], query dimension, zero-norm
  under cosine), single dense-range scan under the read lock, constraint check → liveness
  check (`_removed` skipped, the `FilterLive` analogue) → `TensorPrimitives`
  `CosineSimilarity`/`Dot`/`Distance` per slot span → k-sized binary heap; best-first
  ordering, ties by ascending id.
- [ ] `VectorSearchResult` (+ `VectorSearchConstraint`: kind, exact label) carrying metric +
  `higherIsBetter`.
- [ ] Tests: hand-computed values per metric (negative components, non-unit norms; epsilon
  compare), ordering + tie determinism, k > count, bound rejections, constraint filtering
  returns k *matching* elements, removed-element skip (both the engine-purge path via a real
  `RemoveGraphElementsTransaction` and an artificially stale slot).

## Phase 2 — Persistence

Intent: full citizen of the checkpoint.

- [ ] `Save` (read lock): dimension, metric, count, per-slot elementId + `Write(float[])`.
  `Load`: validate header against limits, resolve via `TryGetGraphElement` (missing →
  logged + skipped), rebuild slab/arrays/slot map. Flip `CanPersist => true`.
- [ ] Tests: save/load round-trip through the real checkpoint path (same kNN query →
  identical ids and scores in-process), removed-then-saved element absent after load,
  missing-element skip logged, pre-feature checkpoint still loads (no format change).

## Phase 3 — REST surface

Intent: the operator/agent-facing contract, typed end to end.

- [ ] Engine read helper `Fallen8.VectorIndexScan(out VectorSearchResult, indexId, query, k,
  constraint)` mirroring `FulltextIndexScan` (resolve, type-check `IVectorIndex`, delegate).
- [ ] DTOs: `VectorIndexAddSpecification` (explicit `vector` **or** `propertyId` mode),
  `VectorIndexScanSpecification`, `VectorSearchResultREST` (metric, `higherIsBetter`,
  id+score pairs).
- [ ] `PUT /index/vector/{indexId}` and `POST /scan/index/vector` on `GraphController`:
  versioned route, `[ProducesResponseType]`/`[Consumes]`/`[Produces]`, XML
  summary/remarks with request samples; 400-with-reason for every spec'd rejection
  (wrong dimension, zero-norm, k bounds, non-vector index, missing/non-vector property in
  property mode).
- [ ] Regenerate the pinned OpenAPI snapshot (`features/done/web-ui/openapi-v0.1.json`
  process) so the web-UI/MCP contract source of truth includes the new endpoints.
- [ ] Tests (`WebApplicationFactory`): add-explicit → scan round-trip, add-by-property mode
  (including missing/wrong-type property → 400), each 400 reason, `OpenApiDocumentTest`
  family still green.

## Phase 4 — Benchmark, memory math & docs

Intent: the numbers that police the non-goals, and the GraphRAG story.

- [ ] Opt-in perf smoke test (`[TestCategory("Benchmark")]` + `[Ignore]`, repo pattern):
  ~100 k × 384-dim corpus — asserts a generous bound, prints measured latency and slab bytes
  (the evidence for the ANN/quantization revisit triggers).
- [ ] `features/open/vector-index/README.md`: creation + add + scan examples (curl), the
  memory table (bytes/element at d = 384/768/1536), metric semantics, the GraphRAG recipe
  (kNN → `/path` / `/subgraph`), and the WAL-gap guidance (store the embedding as a
  `float[]` element property too if the WAL is on).
- [ ] Skill-library/MCP touchpoint: leave a one-line pointer in each companion feature's
  spec follow-up notes (vector scan is a natural `read`-tier MCP tool later; no
  implementation here).

## Phase 5 — Gate

- [ ] Full `dotnet test` green; build 0 warnings/0 errors on the touched projects.
- [ ] Council review per the repo merge gate; fix findings on the branch;
  `git merge --no-ff` to `main`; move `features/open/vector-index/` → `features/done/`.

## Progress

- [ ] Phase 0 — skeleton, storage, write-side lifecycle
- [ ] Phase 1 — kNN + metrics + determinism + liveness
- [ ] Phase 2 — checkpoint save/load
- [ ] Phase 3 — REST endpoints + OpenAPI snapshot
- [ ] Phase 4 — benchmark numbers + README/GraphRAG docs
- [ ] Phase 5 — council gate, merge + move to done/

## Decision / revisit conditions

- **Exact SIMD scan over ANN** — revisit when a real dataset exceeds ~1 M vectors or the
  Phase 4 benchmark's p99 exceeds ~100 ms at the operator's real dimension.
- **Constants over config** (`MaxDimension = 4096`, `MaxK = 1024`) — revisit when a real
  model or caller hits them; a config section is a five-line change then.
- **One vector per element / metric fixed at creation / client-side GraphRAG join /
  snapshot-only durability** — triggers as named in spec §2; none are blocked by this
  design (the SoA layout publishes cleanly when index-lifecycle 3.5 lands).
