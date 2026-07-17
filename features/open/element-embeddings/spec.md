# Element Embeddings — Specification

> **Status:** Draft, spec only (no implementation yet). Follow the feature workflow in the
> repository root `CLAUDE.md`. Umbrella branch: `feature/scan-v2`; feature branch:
> `feature/element-embeddings` (GitHub issue-tracked; per-phase commits; no merge without
> explicit approval).
>
> **Companion feature:** [embedding-provider](../embedding-provider/spec.md) is the
> *generation* half — the component that turns text into embeddings behind a swappable
> backend, and the intended producer of element embeddings. This feature is the *storage +
> dual-consumption* half and **does not depend on a provider being present**: raw,
> bring-your-own-vector workflows are first-class throughout. Neither blocks the other,
> but the provider consumes this feature's write surface.

## 1. Overview & requirements

The [vector-index](../../done/vector-index/spec.md) feature gave Fallen-8 exact kNN over
`float[]` embeddings (`fallen-8-core/Index/Vector/VectorIndex.cs`), but left the vector
homeless: the index slab is the only place a vector lives unless the caller *also* stores it
as an element property by convention (vector-index §3.2 "designated property"), and the two
copies drift apart across the WAL gap (vector-index §5). Traversal cannot see vectors at
all — a path cost function or subgraph filter has no way to ask "how similar is this vertex
to my query?".

This feature makes the embedding **element state**:

- **The element is the source of truth for its embedding.** An embedding is named element
  data on `AGraphElementModel` (covering `VertexModel` and `EdgeModel`), persisted and
  WAL-durable like every other element datum. The `VectorIndex` becomes a **derived,
  materialized projection** that copies the vector into its slab for SIMD scanning — never
  a second source of truth. *(Load-bearing decision — not to be relitigated.)*
- **One typed accessor** — `AGraphElementModel.TryGetEmbedding(out ReadOnlySpan<float>
  vector, string name = "default")` — is the **only** coupling point to the physical
  representation. v1 backs it with a reserved `float[]` element property; the layout can be
  promoted to a dedicated field later without touching a single caller. *(Load-bearing.)*
- **Named embeddings**: the API carries a name everywhere (`name → vector`); v1 ships the
  full named surface (name, dimension can differ per name; metric is a consumer concern).
  *(Load-bearing: the name is part of the API even where "default" dominates.)*
- **One shared similarity primitive** over the same `System.Numerics.Tensors.
  TensorPrimitives` operations the index uses, callable from runtime-compiled path/subgraph
  delegates and stored queries — index kNN and in-traversal similarity are bit-identical by
  construction.
- **Traversal integration**: path cost functions, path/subgraph filters, and stored queries
  read an element's vector via the accessor and score it against a query vector supplied
  for the traversal. The query vector is embedded **once, before the traversal starts**
  (client-side, or via the companion provider) and threaded into the delegate context —
  never model inference during traversal or during commit. *(Load-bearing.)*
- A **declarative semantic filter/cost option** so the common case needs no arbitrary C#
  and runs with `EnableDynamicCodeExecution` **off**.

### FR summary

- FR-1 **Accessor.** `public Boolean TryGetEmbedding(out ReadOnlySpan<Single> vector,
  String name = "default")` on `AGraphElementModel` (`fallen-8-core/Model/
  AGraphElementModel.cs`), inherited by `VertexModel` and `EdgeModel`. Returns `false` when
  the element has no embedding of that name. Nothing outside `AGraphElementModel` touches
  the physical representation.
- FR-2 **Storage v1.** The embedding is a reserved element property, key
  `"$embedding:" + name`, value `Single[]` — stored through the existing copy-on-write
  property store (`_properties`, single-writer/lock-free-reader discipline), serialized
  natively (`SerializedType.SingleArrayType`), and WAL-covered like every property.
- FR-3 **Named embeddings.** Name grammar `^[A-Za-z0-9_-]{1,64}$`; different names on one
  element may have different dimensions. No global schema registry (right-sizing): a
  *bound* vector index (FR-9) declares the dimension it expects for its name; everything
  else validates per write.
- FR-4 **Write surface.** A `SetEmbeddingsTransaction` (batch: element id → name → vector
  or `null` for remove) executes on the single writer thread, is WAL-logged via
  `WalTransactionCodec`, and is the only mutation path. Typed REST endpoints:
  `PUT /element/{id}/embedding/{name}` (body `{ "vector": [...] }`),
  `DELETE /element/{id}/embedding/{name}`, `GET /element/{id}/embedding/{name}` — 400 with
  reason for bad name, non-finite components, or a dimension conflicting with a bound
  index of the same name; existing not-found mapping for unknown elements.
- FR-5 **Similarity primitive.** `VectorMath` (in `fallen-8-core/Index/Vector/`) exposes
  the one scoring implementation (`Cosine`/`DotProduct`/`L2` over
  `TensorPrimitives.CosineSimilarity`/`.Dot`/`.Distance`); `VectorIndex.
  TryNearestNeighbors` switches to it, so index scores and traversal scores are
  bit-identical. It reuses the index's non-finite and zero-norm rejection
  (`VectorIndex.HasNonFiniteComponent`/`IsZeroNorm`).
- FR-6 **Traversal context.** A `TraversalContext` (query vector, embedding name, metric)
  is built once per request and passed as a **parameter** to the delegate factory methods —
  `IPathTraverser`'s five methods gain a `TraversalContext` parameter, and the generated
  subgraph provider methods likewise. Compiled fragments may reference `context` (e.g.
  `context.TrySimilarity(v, out var s)`); fragments that ignore it compile unchanged.
- FR-7 **Declarative semantic block.** `PathSpecification` and `SubGraphSpecification` gain
  an optional `semantic` block (query vector, embedding name, metric, optional `minScore`
  filter threshold, optional `costBySimilarity`). It compiles **no C#** (native closures),
  so it passes the dynamic-code gate (`GraphController.CarriesInlineCode` stays false) and
  works with `EnableDynamicCodeExecution=false`.
- FR-8 **Stored queries.** A stored path query's fragments may reference `context`; the
  invocation payload supplies the query vector via the same `semantic` block. Registration
  stays gated (`POST /storedquery`, `DynamicCodePolicy`); invocation by name stays ungated.
- FR-9 **Bound index projection.** A `VectorIndex` may be created with an optional
  `embeddingName` plugin option. A *bound* index is a pure derived cache: the writer thread
  projects committed embedding writes into the slab, element removal purges as today, the
  checkpoint persists only the header, and load rebuilds the slab from element embeddings —
  retiring the vector-index README's "store as property, re-add after crash replay"
  workaround for bound indices.
- FR-10 **Raw vectors stay first-class.** An *unbound* index behaves exactly as today
  (explicit adds, snapshot persistence, vectors that exist nowhere else). An element with
  no stored embedding is simply not semantic-capable in traversal; pure kNN retrieval is
  never forced to store a vector on every element.

## 2. Goals / non-goals

**Goals** — FR-1 … FR-10 above, plus: every existing vector-index guarantee (exact SIMD
brute force, `MaxDimension = 4096`, `MaxK = 1024`, dimension/metric fixed at creation, one
vector per element, finite/zero-norm guards, deterministic ordering) is **unchanged**.

**Non-goals** (each with its revisit trigger)

- **No model runtime in `fallen-8-core`, no inference on the commit or traversal path, and
  the default deployment stays model-free.** *(Cross-cutting with the companion spec.)*
  Embedding *generation* is the companion feature's job and lives in the apiApp; this
  feature only stores and consumes vectors. Never revisit in-engine.
- **No approximate/ANN index.** Exact SIMD brute-force kNN stays; the vector-index §2
  trigger (~1 M vectors or p99 > ~100 ms) is unchanged. *(Cross-cutting.)*
- **No re-embedding orchestration in the engine.** A model change is an external re-index:
  write new embeddings (new name or new elements) into a new index. The model-identity
  stamp (companion spec) makes drift detectable; fixing it is the operator's loop.
  *(Cross-cutting.)*
- **No dedicated embedding field on the element in v1.** The reserved property is the
  layout; the accessor hides it. Revisit (promote to a typed field) when profiling shows
  the property binary-search or the `Single[]` boxing indirection measurably hurts a real
  semantic-traversal workload — the accessor makes that a model-file-only change.
- **No auto-projection for unbound indices.** Explicit index membership stays the family
  rule (index-lifecycle non-goal). The *bound* index is the deliberate, declared exception:
  binding at creation **is** the explicit caller action, stated once instead of per
  element. Revisit only if a real workload needs per-element opt-out under a binding
  (workaround today: a second unbound index).
- **No semantic block on stored *subgraph* template invocation.** A stored subgraph
  template's artifact is a pinned `SubGraphDefinition` whose delegates were materialized at
  registration (`StoredQueryEntry.Artifact`); rebinding them per invocation is deeper
  surgery than v1 needs. `semantic` + a `SubGraph`-kind `storedQuery` → 400. Revisit when a
  real caller needs parameterized stored subgraph templates.
- **No per-query metric override, no multiple vectors per name per element, no hybrid
  predicate kNN** — vector-index §2 non-goals, all unchanged.
- **No cross-slot composition in the declarative block** (e.g. `semantic` filter AND an
  inline C# vertex filter on the same slot). One owner per delegate slot; conflicts → 400.
  Revisit when real usage shows AND-composition demand.

## 3. Design sketch

### 3.1 Storage: the reserved property behind the accessor

`AGraphElementModel` gains (all in `fallen-8-core/Model/AGraphElementModel.cs`):

```csharp
public const String EmbeddingPropertyPrefix = "$embedding:";

/// The ONLY read coupling point to the physical embedding representation.
public Boolean TryGetEmbedding(out ReadOnlySpan<Single> vector, String name = "default");

internal void SetEmbedding(String name, Single[] vector);   // remove+set, writer thread
internal Boolean RemoveEmbedding(String name);
public static Boolean IsValidEmbeddingName(String name);    // ^[A-Za-z0-9_-]{1,64}$
```

- `TryGetEmbedding` is `TryGetProperty<Single[]>` on the reserved key plus an `AsSpan()` —
  it inherits the store's lock-free-reader guarantee (the published `Single[]` is never
  mutated in place; replacement publishes a fresh array via `SetEmbedding`'s
  remove+set, the same pattern as `RestoreProperty`).
- The reserved prefix is honest, not hidden: the embedding **is** a property, so it rides
  the existing checkpoint format, the WAL property coverage, bulk import/export, and
  `GetAllProperties()` diagnostics for free. Generic property writes to `$embedding:*` keys
  are not blocked (bulk import deliberately uses this), but the typed endpoints and the
  provider are the validated paths.
- **Memory budget, stated honestly:** an element that is both embedded and in a vector
  index pays ~2× — `4·d` bytes in the property (source of truth) plus `4·d` in the slab
  (scan cache) plus ~64 B bookkeeping each. d=384: ~3.2 kB/element both-copies; 100 k
  elements ≈ 320 MB; 1 M ≈ 3.2 GB. That is the price of "source of truth + fast scan";
  workloads that cannot pay it use an unbound index (one copy, weaker durability — FR-10)
  or no index (traversal-only similarity, one copy).

### 3.2 The similarity primitive (`VectorMath`)

`fallen-8-core/Index/Vector/VectorMath.cs`:

```csharp
public static class VectorMath
{
    /// The single scoring implementation shared by index kNN and traversal.
    public static Single Score(ReadOnlySpan<Single> a, ReadOnlySpan<Single> b,
                               VectorDistanceMetric metric);

    /// Guarded variant: false on length mismatch or a non-finite score
    /// (cosine underflow 0/0, dot overflow) - NaN never escapes into a ranking
    /// or a traversal decision.
    public static Boolean TryScore(out Single score, ReadOnlySpan<Single> a,
                                   ReadOnlySpan<Single> b, VectorDistanceMetric metric);
}
```

`VectorIndex.TryNearestNeighbors` replaces its inline metric `switch` with
`VectorMath.Score` — same `TensorPrimitives` calls, now with one home, so bit-identity
between a kNN score and an in-traversal score for the same pair is by construction, and the
metric tests that pin numerical behaviour pin both consumers at once. The add-time guards
(`HasNonFiniteComponent`, `IsZeroNorm`) stay where they are and are reused by the FR-4
write validation.

### 3.3 The traversal context

`fallen-8-core/Algorithms/TraversalContext.cs`:

```csharp
public sealed class TraversalContext
{
    public static readonly TraversalContext Empty;

    public ReadOnlyMemory<Single> QueryVector { get; }
    public String EmbeddingName { get; }          // default "default"
    public VectorDistanceMetric Metric { get; }   // default Cosine
    public Boolean HasQueryVector { get; }

    /// Accessor + VectorMath in one call: false when the element lacks the named
    /// embedding, the dimensions differ, or the score is non-finite.
    public Boolean TrySimilarity(AGraphElementModel element, out Single score);
}
```

**Why a parameter, not a closure field or ambient state** (resolving the open question):
the compiled path traverser is cached **process-wide** keyed on the filter/cost fragments
only (`GeneratedCodeCache.KeyFor` — `fallen-8-core-apiApp/Controllers/Cache/
GeneratedCodeCache.cs`), so one instance serves concurrent requests with different query
vectors: per-request state on the instance is a race, and thread-ambient state leaks
through the algorithms' internal parallelism. Passing the context to the delegate
*factories* keeps the cache key unchanged (same fragments → same compiled assembly) while
each request's delegates close over that request's context.

Concretely, `IPathTraverser` (`fallen-8-core/Algorithms/Path/IPathTraverser.cs`) becomes:

```csharp
public interface IPathTraverser
{
    Delegates.EdgePropertyFilter EdgePropertyFilter(TraversalContext context);
    Delegates.VertexFilter VertexFilter(TraversalContext context);
    Delegates.EdgeFilter EdgeFilter(TraversalContext context);
    Delegates.EdgeCost EdgeCost(TraversalContext context);
    Delegates.VertexCost VertexCost(TraversalContext context);
}
```

The `Delegates.*` types (`fallen-8-core/Algorithms/Delegates.cs`) are **unchanged** — the
context is bound at delegate-materialization time, so `WeightedDijkstraShortestPath` and
`BidirectionalLevelSynchronousSSSP` (which receive the delegates via
`ShortestPathDefinition`) need no changes at all. `CodeGenerationHelper.CreateSource`
(`fallen-8-core-apiApp/Helper/CodeGenerationHelper.cs`) emits the parameter on every
generated method and adds `NoSQL.GraphDB.Core.Algorithms` /
`NoSQL.GraphDB.Core.Index.Vector` to the generated usings; a fragment that never mentions
`context` compiles exactly as before. The subgraph provider methods
(`BuildProviderSource`) gain the same parameter; `CompileDelegates` invokes them with the
context built at **registration** time, so the delegates a `SubGraphRecipe` pins close over
a fixed vector and recalculation never embeds anything (the load-bearing "no inference
during commit" rule — recalculation runs on the writer path).

A cost fragment using the context:

```csharp
// vertexCost: prefer semantically close vertices; strangers cost full price.
return (v) => context.TrySimilarity(v, out var s) ? 1.0 - s : 1.0;
```

`IPathTraverser` is a public-surface breaking change; its only implementers are generated
code and stored-query artifacts, which are **recompiled from source on rehydrate**
(`Fallen8.RehydrateStoredQueries` → `IStoredQueryCompiler.TryCompile`), so no persisted
artifact breaks — persisted stored queries carry source only (`StoredQueryDefinition.
SpecificationJson`).

### 3.4 The declarative semantic block

`PathSpecification` and `SubGraphSpecification` gain an optional `semantic` member
(`SemanticTraversalSpecification`, one DTO shared by both):

```jsonc
"semantic": {
  "queryVector":   [0.12, -0.5, ...],  // required in this feature (companion adds queryText)
  "embeddingName": "default",          // optional
  "metric":        "Cosine",           // optional: Cosine | DotProduct | L2
  "minScore":      0.7,                // optional: declarative vertex filter
  "costBySimilarity": true             // optional: declarative vertex cost (path only)
}
```

- The block alone carries **no C#**: `CarriesInlineCode` (`GraphController.cs`,
  `SubGraphController.cs`) does not consider it, so it runs with the dynamic-code switch
  off — the same posture as the vector kNN endpoint's declarative kind/label constraint.
- `minScore` builds a native `Delegates.VertexFilter` closing over the context: an element
  lacking the named embedding, or scoring worse than the threshold
  (direction-aware: `>= minScore` for `Cosine`/`DotProduct`, `<= minScore` for `L2`), is
  filtered.
- `costBySimilarity` builds a native `Delegates.VertexCost` with a **non-negative** mapping
  — `Cosine`: `1 − score` (∈ [0, 2]); `L2`: the distance itself. `DotProduct` is rejected
  (400): unbounded and sign-indefinite, it has no honest non-negative cost mapping
  (`WeightedDijkstraShortestPath` would clamp negatives to 0 and silently distort the
  ranking). Elements lacking the embedding are filtered (a cost is only defined over
  embedded elements — stated, not silent).
- The context also feeds any compiled fragments in the same request (a stored path query's
  fragments read the same `semantic.queryVector` — FR-8). Conflicts are 400s: `minScore`
  plus an inline/stored vertex-filter fragment, or `costBySimilarity` plus a vertex-cost
  fragment (one owner per slot, §2 non-goal); `semantic` on a `SubGraph`-kind stored-query
  invocation (§2 non-goal); a `queryVector` whose components are non-finite; `minScore`/
  `costBySimilarity` without a query vector.
- Validation order in the controllers mirrors today's: shape/gate checks first, then
  resolution, then execution; the `semantic` DTO is registered in `AppJsonContext` with a
  `JsonSourceGenParityTest` representative like every DTO.

### 3.5 Bound vector indices: the materialized projection

`VectorIndex.Initialize` accepts one new optional plugin option:

| parameter | type | required | validation |
|---|---|---|---|
| `embeddingName` | string | no | `IsValidEmbeddingName`; presence makes the index *bound* |

**Bound index rules** (unbound = exactly today's behaviour, FR-10):

- **Membership = every live element carrying the named embedding with the index's
  dimension.** The explicit-add REST modes (`PUT /index/vector/{indexId}` explicit and
  property mode) answer 400 on a bound index — membership is declared once, at creation.
- **Projection on commit:** `SetEmbeddingsTransaction.TryExecute` runs on the single writer
  thread; after mutating the element it projects into every registered bound index whose
  name matches (`AddOrUpdate`-equivalent internal path; wrong dimension → logged and
  skipped, the family's silent-skip contract — the REST write endpoint already answered 400
  up front, FR-4). Embedding removal and element removal purge the slot (`RemoveValue`
  stays O(1) via the reference-keyed slot map, Trim-safe).
- **Persistence:** `Save` writes the header only (dimension, metric, embedding name).
  `Load` rebuilds the slab by scanning elements for the named embedding — elements are
  already resolvable during index load (the existing `Load` resolves via
  `fallen8.TryGetGraphElement`). After **WAL replay**, replayed `SetEmbeddingsTransaction`s
  re-project naturally (they execute like any replayed transaction), so a bound index is
  correct after crash recovery with **no operator action** — the vector-index README's
  WAL-gap workaround is retired for bound indices. Rollback of an aborted batch restores
  prior embeddings and re-projects them.
- Score behaviour, ordering, constraints, `TryNearestNeighbors` — unchanged; a bound index
  is indistinguishable at query time.

### 3.6 REST surface (versioned, OpenAPI-annotated, snapshot regenerated)

- `PUT /element/{id}/embedding/{name}` — body `{ "vector": [ ... ] }`; 200 `true`; 400
  (name, non-finite, zero-norm only when a bound cosine index of that name exists,
  dimension conflict with a bound index); 404 unknown element. `waitForCompletion`
  semantics as on the other mutation endpoints.
- `DELETE /element/{id}/embedding/{name}` — 200/404.
- `GET /element/{id}/embedding/{name}` — the stored vector (+ the model-identity stamp once
  the companion feature lands); 404 when absent.
- `POST /path/{from}/to/{to}` and `PUT /subgraph` accept the `semantic` block (§3.4).
- All actions carry `[ProducesResponseType]`/`[Consumes]`/`[Produces]` + XML docs;
  the pinned snapshot (`features/done/web-ui/openapi-v0.1.json`) is regenerated via
  `pwsh scripts/update-openapi-snapshot.ps1`.

### 3.7 Concurrency & limits

- Reads (`TryGetEmbedding`, `TrySimilarity`) are lock-free property-store reads — safe
  during traversal by the store's copy-on-write publication discipline; no new locks on the
  read path.
- Writes are single-writer (transaction), like every element mutation; bound-index
  projection happens inside the same commit on the same thread — the index write lock is
  taken per projected element exactly as `RemoveValue` does today for purges.
- Bounds: embedding dimension ∈ [1, `VectorIndex.MaxDimension`] (4096) at the typed write
  endpoints; name ≤ 64 chars; the `semantic` block's vector obeys the same bounds; batch
  `SetEmbeddingsTransaction` size bounded by the existing request-size limit (1 MiB) at the
  REST boundary. Nothing user-supplied allocates unboundedly.

### 3.8 Tests (MSTest, `fallen-8-unittest`, the repo bar)

- Accessor: present/absent/wrong-name/wrong-type property; span content equality; name
  validation table; `VertexModel` and `EdgeModel` both.
- Write path: set/replace/remove round-trip through a real transaction; WAL replay
  restores embeddings; rollback restores the prior vector; reserved-key visibility in
  `GetAllProperties`.
- `VectorMath`: hand-computed values per metric — **asserted equal to
  `VectorIndex.TryNearestNeighbors` scores for the same pairs** (the bit-identity pin);
  non-finite/length-mismatch rejection.
- Traversal: path cost and filter via `context.TrySimilarity` (compiled fragment) and via
  the declarative block — both produce the same path set on a fixture graph; declarative
  block works with `EnableDynamicCodeExecution=false` (WebApplicationFactory,
  `builder.UseSetting`); every 400 conflict in §3.4; missing-embedding filtering semantics;
  DotProduct-cost rejection; subgraph registration-time binding + recalculation without
  re-embedding.
- Stored queries: register a context-referencing path query, invoke with `semantic`
  vector, dynamic code off at invocation; rehydrate-recompile still works (interface
  change).
- Bound index: projection on set/replace/remove; element-removal purge; header-only
  save → load rebuild (same query, identical ids and scores); WAL-replay correctness
  (crash between checkpoint and embedding write); explicit adds → 400; unbound regression
  suite unchanged (`VectorIndexTest`, `VectorIndexEndpointTest` stay green).
- Convention gates: MIT headers, no `Console.Write*`, exact package pins, OpenAPI snapshot
  test, `JsonSourceGenParityTest` representatives for every new DTO.

## 4. Acceptance criteria

- `TryGetEmbedding` is the only site (outside `AGraphElementModel`) that knows the
  representation — enforced by review: no other product file mentions
  `EmbeddingPropertyPrefix`.
- A path query with a `semantic` block (declarative) returns the expected semantically
  filtered/weighted paths **with dynamic code execution disabled**.
- A compiled fragment using `context.TrySimilarity` scores bit-identically to
  `POST /scan/index/vector` for the same element/query pair.
- A bound index survives kill-and-replay with vectors intact and **zero operator action**;
  a pre-feature checkpoint (unbound indices, conventional embedding properties) loads
  unchanged.
- Explicit-add endpoints on a bound index answer 400 with a reason; unbound indices are
  byte-for-byte today's behaviour.
- Full suite green; build 0 warnings/0 errors; OpenAPI snapshot regenerated with additions
  only.

## 5. Risks

- **`IPathTraverser` is a breaking public-interface change.** Contained: implementers are
  generated code and recompiled-on-load stored queries; no persisted artifact carries the
  old shape. The risk is third-party embedded users of `fallen-8-core` — accepted and
  release-noted (the engine is pre-1.0).
- **Writer-thread projection cost.** A bound-index projection adds an O(d) copy per
  embedding write to the commit path (no inference, no allocation beyond the slab). A bulk
  embedding import projects per element; the batch transaction amortizes lock traffic.
  Mitigated by measurement in the plan's benchmark phase.
- **Startup rebuild cost for bound indices.** Load scans all elements once per bound name
  set (O(n) + O(d) copy per embedded element) instead of reading vectors from the sidecar.
  At vector-index README scale (100 k × 384-d ≈ 147 MiB slab) this is IO-free memory work;
  measured in the plan. Revisit (persist vectors redundantly) only if startup time regresses
  measurably on a real corpus.
- **Reserved-key collisions.** A user property that happens to start with `$embedding:`
  now has semantics. Accepted deliberately (it is what makes bulk import work); documented
  in the README; the typed endpoints validate, raw property writes stay caveat-emptor —
  the same posture as the vector-index property mode.
- **Two copies of every indexed embedding.** ~2× memory on embedded-and-bound-indexed
  elements (§3.1 math). Stated in the README table; the unbound path remains for
  memory-constrained pure-kNN uses.

## 6. Keep (do not regress)

- Every vector-index §6 keep: `IIndex` contract, plugin discovery, checkpoint sidecar
  mechanism, index-lifecycle purge/liveness/Trim guarantees, kNN determinism, and the
  typed-endpoint validation posture.
- The property store's copy-on-write single-writer/lock-free-reader discipline — the
  accessor adds no locks and no new mutation sites.
- The dynamic-code security posture: nothing in this feature widens what runs without
  `EnableDynamicCodeExecution`; the declarative block is data, not code, and the
  request-shape gate (`CarriesInlineCode`) still fires for every fragment.
- The stored-query contract: source-only persistence, compile-at-registration, ungated
  invocation, quota, rehydration semantics.
- The WAL/checkpoint formats stay load-compatible: pre-feature checkpoints and WALs load
  unchanged (new transaction type = new codec entry, additive).
