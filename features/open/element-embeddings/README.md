# Element Embeddings — Usage

Named embeddings as durable element state (`AGraphElementModel.TryGetEmbedding`), consumed
by traversal (semantic filters/costs) and by the `VectorIndex` as a *bound*, derived
projection. Bring-your-own-vector throughout — generation is the
[embedding-provider](../embedding-provider/README.md) companion's job and is never
required.

## Writing embeddings

One current vector per (element, name); a write replaces, `DELETE` removes. WAL-durable
like every element datum.

```bash
curl -sf -X PUT "http://localhost:5000/graphelement/42/embedding/default?waitForCompletion=true" \
     -H "Content-Type: application/json" \
     -d '{ "vector": [0.12, -0.5, 0.33] }'

curl -sf http://localhost:5000/graphelement/42/embedding/default
# { "name": "default", "vector": [...], "model": null }

curl -sf -X DELETE "http://localhost:5000/graphelement/42/embedding/default?waitForCompletion=true"
```

- Names: `[A-Za-z0-9_-]{1,64}`; different names may have different dimensions.
- 400 with reason: invalid name, empty/oversized (> 4096) vector, non-finite components,
  a dimension conflicting with a *bound* index of that name, zero-norm while a bound
  Cosine index exists. 404: unknown element.
- Bulk import works too: the embedding **is** the reserved `float[]` property
  `$embedding:<name>` (v1 layout — an implementation detail behind the accessor, but the
  import surface may write it directly; the engine projects those writes as well).
- Embedded engine callers use `SetEmbeddingsTransaction` (batch, replace semantics).

## Bound vector indices (the projection)

Create a `VectorIndex` with `embeddingName` and it becomes a **pure derived cache** of
that embedding — no explicit adds (they answer 400), no separate durability story:

```bash
curl -sf -X POST http://localhost:5000/index \
     -H "Content-Type: application/json" \
     -d '{
           "uniqueId": "embeddings",
           "pluginType": "VectorIndex",
           "pluginOptions": {
             "dimension":     { "propertyValue": "384", "fullQualifiedTypeName": "System.Int32" },
             "metric":        { "propertyValue": "Cosine", "fullQualifiedTypeName": "System.String" },
             "embeddingName": { "propertyValue": "default", "fullQualifiedTypeName": "System.String" }
           }
         }'
```

- Membership = every live element carrying the named embedding with the index's
  dimension; maintained on the writer thread for every embedding surface (typed
  endpoint, raw reserved-key property writes, element creation, removals).
- Checkpoints persist only the index header; load rebuilds the slab from element state,
  and WAL replay re-projects replayed embedding writes — **the vector-index README's
  "store as property, re-add after crash replay" workaround is retired for bound
  indices.**
- Query exactly as before (`POST /scan/index/vector`) — a bound index is
  indistinguishable at query time.
- Unbound indices (no `embeddingName`) are unchanged: explicit adds, snapshot-persisted
  vectors, no element coupling — the right choice when you don't want the ~2× memory of
  element copy + slab copy (see below). An optional `model` creation option stores an
  opaque model-identity string for the embedding provider's consistency checks.

## Semantic traversal

The `semantic` block on `POST /path/{from}/to/{to}` and `PUT /subgraph` supplies a query
vector for the traversal — embedded **once, before the traversal starts** (client-side,
or via the provider's `queryText` once that feature lands). It is pure data: it runs with
`EnableDynamicCodeExecution=false`.

```bash
# Paths through semantically close vertices only (declarative filter):
curl -sf -X POST http://localhost:5000/path/1/to/9 \
     -H "Content-Type: application/json" \
     -d '{ "semantic": { "queryVector": [0.1, ...], "embeddingName": "default",
           "metric": "Cosine", "minScore": 0.7 } }'

# Weight a DIJKSTRA search by similarity (Cosine cost = 1 - score; L2 = distance;
# DotProduct is rejected - no honest non-negative mapping):
curl -sf -X POST http://localhost:5000/path/1/to/9 \
     -H "Content-Type: application/json" \
     -d '{ "pathAlgorithmName": "DIJKSTRA",
           "semantic": { "queryVector": [0.1, ...], "costBySimilarity": true } }'
```

- Elements without the named embedding are filtered by `minScore`/`costBySimilarity` —
  stated, not silent. Scores are bit-identical to `POST /scan/index/vector` for the same
  pair (`VectorMath` is the one implementation).
- With dynamic code **on**, C# fragments (and stored path queries) get the same vector
  through the `context` parameter:

```csharp
// vertexCost fragment: prefer semantically close vertices.
return (v) => context.TrySimilarity(v, out var s) ? 1.0 - s : 1.0;
```

- One owner per delegate slot: `minScore` plus a vertex-filter fragment (or
  `costBySimilarity` plus a vertex-cost fragment) is a 400.
- Subgraphs bind the vector at **registration**; recalculation reuses it (never any
  inference on the writer path), and the semantic block rides the persisted recipe, so a
  declarative semantic subgraph survives restart and WAL replay. A stored *subgraph*
  template invocation cannot carry `semantic` (400) — its delegates were materialized at
  registration.

## Memory math (the honest ~2×)

An element that is both embedded and in a bound index pays twice: `4·d` bytes on the
element (source of truth, WAL-covered) plus `4·d` in the slab (scan cache) plus ~64 B
bookkeeping each. d=384 → ~3.2 kB/element; 100 k ≈ 320 MB; 1 M ≈ 3.2 GB. Traversal-only
workloads (no index) and unbound-index workloads (no element copy) each pay once.

## Limits & guarantees

- Dimension ∈ [1, 4096] everywhere; every write and every semantic block validates
  finiteness; NaN never enters a ranking or a traversal decision.
- Embedding writes ride the single-writer transaction discipline; reads are lock-free
  (copy-on-write property store) — no new locks anywhere on the read path.
- The declarative semantic block carries no code and does not widen the dynamic-code
  gate; fragments and stored-query registration remain gated exactly as before.
- Pre-feature checkpoints and WALs load unchanged (new WAL entry type and index header
  are additive).
