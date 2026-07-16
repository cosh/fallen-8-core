# Vector Index — Specification

> **Status:** Implemented and merged (branch `feature/vector-index`, council-approved
> 2026-07-16; see [plan.md](./plan.md) for the phase record and council outcome).
>
> **Companion features:** [mcp-server](../../open/mcp-server/spec.md) and
> [skill-library](../../open/skill-library/spec.md) give agents the tools and the know-how to use
> Fallen-8; this feature gives them the *entry point*: similarity search over embeddings that
> lands on graph elements, from which the existing traversal/path/subgraph surface takes over
> (the GraphRAG story). Neither blocks the other.

## 1. Overview & requirements

Fallen-8 has three query-index families today — exact/ordered lookup
(`DictionaryIndex`/`RangeIndex` in `fallen-8-core/Index/` and `Index/Range/`), fulltext
(`Index/Fulltext/RegExIndex`), and spatial (`Index/Spatial/` R-tree). What it cannot answer
is the question every embedding-based workload starts with: *"which elements are most similar
to this vector?"*

This feature adds a fourth family, **`Index/Vector`**, as a straight sibling of the existing
three:

- A **`VectorIndex`** plugin implementing a new **`IVectorIndex : IIndex`** interface,
  discovered by `PluginFactory` and created through the existing surface
  (`IndexFactory.TryCreateIndex` / `POST /index`) — no new registration machinery.
- **k-nearest-neighbour** search over `float[]` embedding vectors with a metric fixed per
  index at creation: **cosine similarity**, **dot product**, or **L2 (Euclidean) distance**.
- v1 algorithm: **exact brute-force scan with SIMD**, using `TensorPrimitives`
  (`System.Numerics.Tensors`, net10.0) — `CosineSimilarity`, `Dot`, `Distance` over
  contiguous `ReadOnlySpan<float>` slices. No approximate structures (§2 non-goals).
- A REST kNN endpoint (`POST /scan/index/vector`) returning **element ids + scores**, plus a
  typed add endpoint, following the fulltext/spatial controller pattern.
- Full participation in the landed **index-lifecycle** rules (removed elements never
  returned, O(1) `RemoveValue` for the engine's write-end purge) and the existing
  **checkpoint persistence** pattern (`CanPersist == true`, id + payload sidecar
  serialization like `RangeIndex`).

### Why exact brute force (and not HNSW) in v1

Fallen-8 is a single-process, self-hosted, single-operator database. At that scale an exact
SIMD scan is simple, always correct (no recall parameter to tune), has zero build/maintain
cost on writes, and is fast enough — the scan is memory-bandwidth bound at roughly
`n · d · 4` bytes per query:

| corpus | dimension | scanned bytes | expected latency (≈30 GB/s effective) |
|---|---|---|---|
| 10 k | 768 | ~31 MB | ~1–3 ms |
| 100 k | 768 | ~307 MB | ~10–30 ms |
| 1 M | 768 | ~3.1 GB | ~100–300 ms |

Measure before adding ANN complexity: the plan ships an opt-in benchmark test that produces
these numbers on the operator's box, so the revisit trigger below is checked against data,
not vibes.

## 2. Goals / non-goals

**Goals**

- `IVectorIndex : IIndex` + `VectorIndex` plugin in `fallen-8-core/Index/Vector/`, MIT
  headers, `Try*` conventions, created via `POST /index` with plugin options
  (`dimension`, `metric`) — exactly how the other families are parameterized.
- kNN with the three metrics, deterministic ordering (best score first, ties broken by
  ascending element id), scores returned raw (no normalization games).
- **One vector per element** (add-again = replace), single contiguous copy of each vector
  inside the index (structure-of-arrays layout, §3.3), honest memory math documented.
- Dimension validated on every write and every query; k bounded; NaN/Infinity components
  rejected on add and query; zero-norm vectors rejected for cosine; non-finite *scores*
  (cosine squared-norm underflow, dot-product overflow — reachable from finite inputs) are
  skipped during selection. NaN never enters a ranking.
- Removed elements never surface from a kNN result (engine purge + defense-in-depth filter).
- Save/load round-trip via the existing per-index sidecar mechanism; scores identical after
  reload.
- REST: `POST /scan/index/vector` (query) and `PUT /index/vector/{indexId}` (typed add) on
  the versioned controller surface with full OpenAPI annotations.
- Optional query constraints: element kind (vertex/edge/any) and exact label match.

**Non-goals** (each with its revisit trigger)

- **ANN structures (HNSW/IVF/quantized graphs).** Revisit when a real dataset exceeds
  ~1 M vectors **or** the benchmark's measured p99 kNN latency at the operator's real
  dimension exceeds ~100 ms. Until then exact scan wins on simplicity and correctness.
- **Vector quantization (int8/binary/PQ).** Revisit when the vector slab's measured share of
  the process heap exceeds ~25 % on a real workload (the memory math in §3.3 makes this easy
  to check).
- **Embedding generation inside the database** (calling embedding models). Never in-engine;
  clients supply vectors. If a convenience wrapper is ever wanted it belongs in the MCP
  server / skill library, not in `fallen-8-core` — revisit there when agent usage shows the
  extra round-trip actually hurts.
- **Multiple vectors per element** (e.g. chunked-document embeddings). Revisit when a real
  workload needs chunk-level retrieval; the v1 workaround is one element per chunk, which is
  also the better graph model.
- **Per-query metric override.** The metric is an index-creation parameter; revisit when a
  real caller needs two metrics over one corpus (workaround: two indices — the memory cost is
  the honest price, stated).
- **Predicate/hybrid-filtered kNN** beyond the kind/label constraint (property predicates,
  fulltext+vector fusion). Revisit when GraphRAG usage shows "top-k, then traverse/filter"
  is insufficient — traversal *is* this database's filter language.
- **A combined kNN+traversal endpoint.** v1 composes two calls (kNN → `/path`, `/subgraph`,
  property gets). Revisit when the skill-library/MCP usage shows a demand pattern worth a
  server-side join.
- **WAL-logging vector index writes.** Index durability is snapshot-only across the whole
  family (index-lifecycle 3.5/3.6, deferred and owned jointly with
  `crash-durability-hardening`); this index inherits that posture and migrates with the
  family when it lands. §5 states the practical consequence.
- **Auto-indexing on property write.** Index membership stays an explicit caller action, the
  index-lifecycle non-goal, unchanged.

## 3. Design sketch

### 3.1 Family shape

```
fallen-8-core/Index/Vector/
  IVectorIndex.cs            IVectorIndex : IIndex — TryNearestNeighbors(...)
  VectorDistanceMetric.cs    enum: Cosine | DotProduct | L2
  VectorIndex.cs             the plugin (sealed, AThreadSafeElement like the family)
  VectorSearchResult.cs      result: elements + scores + metric (mirrors FulltextSearchResult)
```

`VectorIndex` is discovered by `PluginFactory`'s assembly scan like every other `IIndex`
implementer; `IndexFactory.TryCreateIndex(out idx, "myEmbeddings", "VectorIndex", options)`
creates it. `Initialize` parses plugin parameters:

| parameter | type | required | validation |
|---|---|---|---|
| `dimension` | int | yes | `1 ≤ d ≤ 4096` |
| `metric` | string | no (default `Cosine`) | one of `Cosine`, `DotProduct`, `L2` |

Invalid parameters throw from `Initialize`; `TryCreateIndex` already catches and returns
`false` (existing contract; the failure is logged). `MaxDimension = 4096` and
`MaxK = 1024` are `public const` on `VectorIndex` — deliberately constants, not a new config
section (right-sizing: one operator, one box; revisit if a real embedding model beyond
4096 dims or a real caller needing k > 1024 shows up).

### 3.2 Contract & validation

```csharp
public interface IVectorIndex : IIndex
{
    Int32 Dimension { get; }
    VectorDistanceMetric Metric { get; }

    /// k best-scoring live elements for the query vector; false on invalid input
    /// (wrong dimension, k out of [1, MaxK], zero-norm query under Cosine).
    Boolean TryNearestNeighbors(out VectorSearchResult result, ReadOnlySpan<Single> query,
                                Int32 k, VectorSearchConstraint constraint = null);
}
```

- **`AddOrUpdate(key, element)`** (the `IIndex` member): `key` must be a `float[]` of
  exactly `Dimension`; anything else is logged and ignored — the same silent-skip contract
  `IndexHelper.CheckObject` gives the dictionary family, but the *typed REST endpoint*
  (§3.5) validates first and returns 400, so over REST the error is never silent. A
  zero-norm vector under `Cosine` is rejected the same way. **Add-again replaces**: an
  element already in the index gets its vector overwritten (one vector per element — kNN
  over stale duplicates is a wrong answer, so the multi-bucket semantics of the dictionary
  family deliberately do not apply here; documented on the interface).
- **`RemoveValue(element)`** is **O(1)** via the slot map (§3.3). This is the member the
  engine's write-end purge (index-lifecycle 3.3) calls on the single writer thread for every
  committed removal — it must never scan.
- **`TryRemoveKey(key)`**: removes every element whose stored vector is bitwise-equal to
  `key` (linear scan; diagnostic-grade, like the endpoint that calls it).
- **`TryGetValue(out bucket, key)`**: exact-match linear scan returning every element whose
  stored vector is bitwise-equal to `key` (normally 0 or 1 element, but distinct elements
  may share a vector). `GetKeys`/`GetKeyValues` enumerate per-slot copies — diagnostic-only,
  O(n·d), documented as such. Because `float[]` is not `IComparable`, the generic
  `Fallen8.IndexScan`/`RangeIndexScan` paths simply never match a vector index — the
  dedicated endpoint is the query surface, exactly as with fulltext and spatial.
- **`CountOfKeys() == CountOfValues() == `** number of indexed elements (one key per entry).
- **Vector provenance ("designated property"):** the recommended convention is that the
  element also carries its embedding as a `float[]` property (the serializer already
  round-trips `Single[]` — `SerializedType.SingleArrayType`), and the typed add endpoint
  supports *property mode* (§3.5): "index element X by its property `embedding`". Missing
  property, non-`float[]` value, or wrong dimension in property mode → 400 with the reason.
  The index itself does not crawl properties and never auto-updates when a property changes
  (family-wide behaviour; the caller re-adds — index-lifecycle non-goal kept).

### 3.3 Storage layout & memory math

Structure-of-arrays, owned by the index, sized for SIMD scanning:

- `float[] _vectors` — one flat slab; slot *i* occupies `[i·d, (i+1)·d)`. Grows by doubling.
- `AGraphElementModel[] _elements` — parallel array (slot → element).
- `Dictionary<AGraphElementModel, int> _slotByElement` — reverse map, **reference-keyed**
  like `RangeIndex._reverse`, so it survives a Trim id-renumber and makes
  `RemoveValue`/replace O(1).
- Removal swaps the last slot into the hole (both arrays + map fix-up) — no tombstones, the
  scan range is always dense.

**Memory per element ≈ `4·d` bytes (vector) + ~64 bytes bookkeeping** (element reference,
parallel-array entry, dictionary entry). Concretely: d=384 → ~1.6 kB/element (100 k ≈
160 MB); d=768 → ~3.1 kB/element (100 k ≈ 310 MB, 1 M ≈ 3.1 GB). Vectors dominate
everything else in this feature; the README states this table so the operator can budget.
If the caller *also* stores the embedding as an element property that is a second full copy
— stated, caller's choice (see §5 for why WAL users may want it anyway).

### 3.4 kNN query

Single pass over the dense slot range under the read lock:

1. Optional constraint check (element kind, exact `Label` match) — applied **before**
   scoring, so the returned k are k *matching* elements, not k results minus casualties.
2. Defense-in-depth liveness check: a slot whose element is `_removed` is skipped (the
   engine purge should already have removed it; this mirrors the read-end `FilterLive`
   floor so the two ends can never disagree).
3. Score via `TensorPrimitives.CosineSimilarity` / `.Dot` / `.Distance` on the slot's span —
   SIMD within each candidate, no per-candidate allocation. A non-finite score is skipped:
   NaN is not totally ordered (it would freeze the heap root), and neither NaN nor Infinity
   survives JSON serialization — and both are reachable from finite inputs (cosine
   squared-norm underflow, dot-product overflow).
4. Bounded top-k selection (fixed-size binary heap of `(score, id)`); ordering: best first
   (`Cosine`/`DotProduct`: higher is better; `L2`: lower is better), ties by ascending id —
   fully deterministic.

`VectorSearchResult` carries the metric and score semantics alongside
`(elementId, score)` pairs so a caller can never misread an L2 distance as a similarity.

### 3.5 REST surface (GraphController, versioned, OpenAPI-annotated)

The generic `PUT /index/{indexId}` add path converts keys through `PropertySpecification`
(string value + primitive-type allow-list from `dynamic-code-resource-limits` R3) and cannot
express a `float[]` — so the vector family gets typed endpoints, the same way fulltext and
spatial got theirs:

- **`PUT /index/vector/{indexId}`** — body `VectorIndexAddSpecification`:
  `{ "graphElementId": 42, "vector": [0.12, …] }` (explicit mode) **or**
  `{ "graphElementId": 42, "propertyId": "embedding" }` (property mode — reads the
  element's `float[]` property). 200 `true` on success; 400 with reason for wrong dimension,
  zero-norm-under-cosine, missing/non-vector property, not-a-vector-index; existing
  not-found mapping for unknown index/element.
- **`POST /scan/index/vector`** — body `VectorIndexScanSpecification`:
  `{ "indexId": "myEmbeddings", "query": [ … ], "k": 10,
     "kind": "vertex|edge|any"?, "label": "person"? }`.
  Returns `VectorSearchResultREST` — `{ "metric": "Cosine", "higherIsBetter": true,
  "results": [ { "graphElementId": 7, "score": 0.93 }, … ] }` (mirrors
  `FulltextSearchResultREST`'s elements-plus-relevance shape). 400 for wrong query
  dimension, `k` outside `[1, MaxK]`, or a non-vector index.

Engine plumbing mirrors fulltext: a `Fallen8.VectorIndexScan(out VectorSearchResult, indexId,
query, k, constraint)` read helper resolves the index, type-checks `IVectorIndex`, and
delegates — the `IFallen8Read` surface for embedded callers. The controller resolves the
index itself instead of going through the helper, because the REST contract needs the
granular status split (404 unknown index vs. 400 not-a-vector-index) that the helper's
single `bool` collapses. Both actions carry `[ProducesResponseType]` / `[Consumes]` /
`[Produces]` + XML `<summary>`/`<remarks>` with request samples, per repo convention; the
pinned OpenAPI snapshot is regenerated (plan Phase 3).

**The GraphRAG story** (documented in the feature README, aligned with the skill library):
kNN returns element ids → feed them straight into the existing surface —
`POST /path/{from}/to/{to}` between hits, `PUT /subgraph` seeded by hit labels/properties,
or plain property reads. The database's value is that similarity search *lands on a graph*;
v1 deliberately keeps the join client-side (non-goal + trigger in §2).

### 3.6 Lifecycle (index-lifecycle rules, followed exactly)

- **Removed elements are never returned.** Both ends hold, as landed for the family:
  the engine's write-end purge calls `RemoveValue` on every registered index on the single
  writer thread when a removal commits (rolled-back removals never purge) — O(1) here via
  the slot map; and the read end skips `_removed` slots (§3.4 step 2), the vector analogue
  of `FilterLive`.
- **Writes** (`AddOrUpdate` via the typed endpoint) run on the request thread under the
  family's `AThreadSafeElement` read/write lock — the *current* landed state
  (index-lifecycle 3.5 writer-routing is deferred, documented). The SoA layout is already
  shaped for that migration: when 3.5 lands, the slab + parallel arrays publish by a single
  volatile store like the rest of the family.
- **Property update/removal** does not touch the index (no auto-indexing, family-wide);
  replacing an element's vector is an explicit re-add; removing the element purges it.
- **Trim safety:** the reverse map keys on element reference, never id.
- **`Wipe`/`Dispose`** clear slab, arrays, and map.

### 3.7 Persistence

`CanPersist => true`. `Save` (under the read lock, like `RangeIndex`): dimension, metric,
count, then per slot `elementId` + `Write(float[])` (the serializer's native
`SingleArrayType`). `Load`: read header, validate dimension and metric against the limits
(a corrupt header throws, so the per-index catch in `LoadIndices` skips exactly this
sidecar instead of registering a half-initialized index), resolve each element via
`fallen8.TryGetGraphElement` (missing id → logged and skipped, the family's existing
posture; the logger is wired inside `Load` because `OpenIndex` activates the plugin without
calling `Initialize`), rebuild slab/arrays/slot map. Loaded through the unchanged
`IndexFactory.OpenIndex` path from the per-index sidecar; nothing in
`PersistencyFactory` changes.

### 3.8 Concurrency & resource limits

- Reads (kNN, counts, enumerations) take the shared read lock and run concurrently with each
  other; writes are exclusive — the family's model. A kNN scan holds the read lock for its
  duration, so a write waits behind the longest concurrent scan; at the corpus sizes in §1's
  table that is milliseconds, and the benchmark test makes the real number visible. (This is
  the same trade every index in the family makes today; the fix is the family-wide 3.5
  migration, not something bespoke here.)
- Bounds, all validated before any work: `k ∈ [1, 1024]`, `dimension ∈ [1, 4096]`,
  query/stored vectors exactly `Dimension` long with finite components, zero-norm rejected
  under cosine. No unbounded allocation is reachable from user input (top-k heap is
  `k`-sized; the slab grows only with successful adds, slab-first so a failed resize is
  retryable, and growth past `Int32.MaxValue` slab floats throws instead of overflowing).

### 3.9 Tests (MSTest, `fallen-8-unittest`, the repo bar)

- **Metric correctness:** each of the three metrics against hand-computed values on small
  vectors (including negative components and non-unit norms).
- **Ordering & ties:** equal scores → ascending element id; best-first per metric direction.
- **Bounds:** k > element count returns all; k = 0 / k > MaxK → false / 400; wrong-dimension
  add and query rejected; zero-norm cosine add and query rejected; dimension/metric plugin
  parameter validation (creation fails).
- **Replace semantics:** re-adding an element with a new vector — old vector unfindable,
  counts unchanged.
- **Lifecycle:** transactionally removed element absent from kNN results (write-end purge)
  and skipped even when artificially left in a slot (read-end defense); `RemoveValue`,
  `TryRemoveKey`, `Wipe`, `CountOfKeys`/`CountOfValues` semantics.
- **Constraints:** kind and label filtering return k matching elements.
- **Persistence:** save/load round-trip — same query returns identical ids *and scores*;
  missing-element-on-load skipped and logged.
- **REST:** both endpoints via `WebApplicationFactory` — happy paths, each 400 reason,
  non-vector-index 400, OpenAPI document still generates.
- **Perf smoke:** opt-in `[TestCategory("Benchmark")]` + `[Ignore]` (repo pattern) — e.g.
  100 k × 384-dim corpus, assert a generous latency bound and print the measured number
  (the §2 ANN-trigger evidence).

## 4. Acceptance criteria

- `POST /index` with `pluginType: "VectorIndex"` + options creates a working index;
  `GetAvailableIndexPlugins` lists it; invalid options fail creation (logged).
- kNN returns the exact top-k by the configured metric, deterministically ordered, with raw
  scores and correct `higherIsBetter` semantics — pinned by hand-computed-value tests.
- A removed element never appears in a kNN result, matching `GraphScan` liveness semantics —
  pinned by a purge test and a defense-in-depth test.
- Save/load round-trips the index through the standard checkpoint (scores identical after
  reload); `CanPersist == true`; a checkpoint written before this feature still loads.
- Every input bound (k, dimension, zero-norm, non-vector index) is enforced with a 400 and a
  reason over REST; nothing user-supplied can allocate unboundedly.
- `AddOrUpdate` replaces (one vector per element); `RemoveValue` is O(1) (no scan —
  code-review-visible via the slot map, exercised by the purge test).
- Both endpoints appear correctly in the OpenAPI document; the pinned snapshot is
  regenerated; full suite green, build clean.

## 5. Risks

- **Scan latency under the read lock at unplanned scale.** A silently grown corpus makes
  kNN (and therefore write-wait) slow before anyone measures. Mitigation: the benchmark test
  plus the §1 table give the operator the math; the ANN non-goal has a concrete trigger
  bound to that measurement.
- **Index-only vectors and the WAL gap.** Index durability is snapshot-only (family-wide,
  index-lifecycle 3.5/3.6 deferred): with the WAL on, vectors added since the last
  checkpoint do not survive crash-replay — same as every index entry today, but here the
  vector may exist *nowhere else*. Mitigation, documented in the README: WAL users should
  also store the embedding as a `float[]` element property (WAL-covered, serializer-native)
  and re-add from property mode after a replay; resolved properly when 3.5/3.6 land.
- **`TensorPrimitives` dependency.** New package reference (`System.Numerics.Tensors`,
  exact 10.0.x version pinned at implementation) in `fallen-8-core` — first new engine
  dependency in a while. It is a Microsoft-maintained platform library with no transitive
  tail; the metric tests pin numerical behaviour so a package bump that changes results
  fails loudly.
- **Float non-determinism across machines.** SIMD width can change summation order, so
  scores may differ in the last ulps between boxes. Tests therefore compare with an epsilon,
  and the save/load "identical scores" criterion is same-process. Cross-machine ranking
  stability at meaningful score gaps is unaffected.
- **Silent `AddOrUpdate` skip via the generic engine API.** An embedded caller using
  `IIndex.AddOrUpdate` directly with a bad key gets a log line, not an exception (family
  contract). The typed REST path validates loudly; the interface docs state the difference.

## 6. Keep (do not regress)

- The `IIndex` contract, `CanPersist` capability flag, `PluginFactory`/`PluginCache`
  discovery, and `IndexFactory` registration/locking — this feature only *adds* an
  implementer and one narrow sub-interface.
- The landed index-lifecycle guarantees: write-end purge on the single writer for every
  registered index (this index makes it O(1)), read-end liveness filtering, reference-keyed
  reverse maps surviving Trim.
- The existing checkpoint format and per-index sidecar mechanism (`OpenIndex` path
  unchanged); checkpoints from before this feature load unchanged.
- The REST conventions: versioned controllers, `ProducesResponseType`/XML docs, the
  `dynamic-code-resource-limits` primitive allow-list on the *generic* index endpoints
  (untouched — the vector endpoints are typed precisely so that allow-list does not grow).
- The security posture: the new endpoints are plain data endpoints — no dynamic code, no new
  capability flags, covered by the existing API-key/rate-limit middleware like every other
  scan.
- The repo test bar: every behaviour above lands with MSTest coverage in
  `fallen-8-unittest`, arrange/act/assert, `TestLoggerFactory.Create()`.
