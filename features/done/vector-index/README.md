# Vector Index — Usage

Exact k-nearest-neighbour search over `float[]` embeddings, as a fourth index family
(`fallen-8-core/Index/Vector/`) next to dictionary/range, fulltext, and spatial. Brute-force
SIMD scan via `TensorPrimitives` — no approximate structures, no recall parameter, always the
exact top-k (see [spec.md](./spec.md) §1 for why, and §2 for the ANN revisit trigger).

## Creating an index

A vector index is created through the existing plugin surface. `dimension` is required
(1–4096); `metric` is optional (`Cosine` default, `DotProduct`, `L2`):

```bash
curl -sf -X POST http://localhost:5000/index \
     -H "Content-Type: application/json" \
     -d '{
           "uniqueId": "embeddings",
           "pluginType": "VectorIndex",
           "pluginOptions": {
             "dimension": { "propertyValue": "384", "fullQualifiedTypeName": "System.Int32" },
             "metric":    { "propertyValue": "Cosine", "fullQualifiedTypeName": "System.String" }
           }
         }'
```

Invalid options (dimension out of range, unknown metric) fail creation; the reason is logged.

## Adding vectors

The generic `PUT /index/{indexId}` add path cannot express a `float[]`, so the family has a
typed endpoint with two modes. **One vector per element** — adding again replaces.

```bash
# Explicit mode: the vector is in the request.
curl -sf -X PUT http://localhost:5000/index/vector/embeddings \
     -H "Content-Type: application/json" \
     -d '{ "graphElementId": 42, "vector": [0.12, -0.5, 0.33, ...] }'

# Property mode: the element already carries its embedding as a float[] property.
curl -sf -X PUT http://localhost:5000/index/vector/embeddings \
     -H "Content-Type: application/json" \
     -d '{ "graphElementId": 42, "propertyId": "embedding" }'
```

Exactly one mode per request. 400 with a reason for: wrong dimension, NaN/Infinity
components, zero-norm vector under cosine, both/neither mode, missing or non-`float[]`
property, not a vector index. 404 for an unknown index or element.

## Querying (kNN)

```bash
curl -sf -X POST http://localhost:5000/scan/index/vector \
     -H "Content-Type: application/json" \
     -d '{ "indexId": "embeddings", "query": [0.1, 0.2, ...], "k": 10,
           "kind": "vertex", "label": "person" }'
```

`kind` (`vertex`/`edge`/`any`) and `label` (exact, case-sensitive) are optional constraints,
applied **before** top-k selection — you get k *matching* elements, not k minus casualties.
Removed elements never appear. `k` is bounded to `[1, 1024]`.

Response:

```json
{
  "metric": "Cosine",
  "higherIsBetter": true,
  "results": [
    { "graphElementId": 7,  "score": 0.93 },
    { "graphElementId": 12, "score": 0.87 }
  ]
}
```

### Metric semantics

Scores are raw — no normalization. `metric` + `higherIsBetter` say how to read them:

| metric | score | higherIsBetter | zero-norm vectors |
|---|---|---|---|
| `Cosine` | cosine similarity, [-1, 1] | true | rejected (add and query) |
| `DotProduct` | inner product, unbounded | true | allowed |
| `L2` | Euclidean distance, ≥ 0 | **false** | allowed |

Results are best-first; equal scores are broken by ascending element id, so ordering is
fully deterministic.

## The GraphRAG recipe

The point of vectors in a graph database: similarity search *lands on graph elements*, and
the existing traversal surface takes over.

1. Embed the user's question client-side (the database never calls embedding models).
2. `POST /scan/index/vector` → top-k element ids.
3. Expand from the hits with the existing surface:
   - `POST /path/{from}/to/{to}` — how are two hits connected?
   - `PUT /subgraph` — register a recipe seeded by hit labels/properties.
   - plain `GET /vertex/{id}` property reads and adjacency lookups.
4. Feed the retrieved neighbourhood, not just the isolated hits, to the model.

The join is deliberately client-side in v1 (spec §2 non-goal with trigger).

## Memory math

The index stores one contiguous copy of each vector (structure-of-arrays slab). Budget
roughly `4·d` bytes per element for the vector plus ~64 bytes bookkeeping:

| dimension | per element | 100 k elements | 1 M elements |
|---|---|---|---|
| 384 | ~1.6 kB | ~160 MB | ~1.6 GB |
| 768 | ~3.1 kB | ~310 MB | ~3.1 GB |
| 1536 | ~6.2 kB | ~620 MB | ~6.2 GB |

If you *also* store the embedding as an element property (see below), that is a second full
copy — your choice, stated honestly.

## Measured latency (the ANN trigger evidence)

Opt-in benchmark (`fallen-8-unittest/VectorIndexBenchmark.cs`, remove `[Ignore]` and run
`dotnet test --filter "TestCategory=Benchmark"`): brute-force kNN over **100,000 × 384-dim**
vectors (Cosine, k=10) measured **~21 ms/query mean, ~75 ms worst of 50** (vector slab
~147 MiB). The spec's revisit trigger for ANN structures is ~1 M vectors or p99 > ~100 ms at
your real dimension — the benchmark prints mean and worst per-query time, so run it on your
box before adding complexity.

Note the family's concurrency model: a kNN scan holds the index's shared read lock, so a
concurrent write to the *same index* waits behind the longest running scan — milliseconds at
these sizes, but part of the same math.

## Durability: the WAL gap (solved for bound indices)

For an **unbound** (raw) index, contents are **snapshot-only** across the whole index
family: a save-game persists the index (dimension, metric, vectors — scores are identical
after reload), but the WAL does not log index writes. With the WAL on, vectors added since
the last save-game do **not** survive crash replay — and unlike other index entries, the
vector may exist nowhere else.

**The clean fix is a *bound* index** (feature
[element-embeddings](../../open/element-embeddings/README.md)): create the index with an
`embeddingName` option and store vectors as element embeddings
(`PUT /graphelement/{id}/embedding/{name}`). The element is then the WAL-durable source of
truth and the index a derived projection that rebuilds on load and re-projects on WAL
replay — zero operator action after a crash.

For raw indices that must stay unbound, the historical workaround still applies:

1. Store the embedding as a `float[]` **property** on the element as well (properties are
   WAL-covered; the serializer round-trips `Single[]` natively).
2. Add to the index in **property mode** (`"propertyId": "embedding"`).
3. After a crash replay, re-add the affected elements in property mode — the vectors come
   back from the WAL-recovered properties.

## Limits & guarantees

- `dimension` ∈ [1, 4096], `k` ∈ [1, 1024] (`VectorIndex.MaxDimension`/`MaxK` constants).
- Every write and every query validates the dimension and rejects NaN/Infinity components;
  zero-norm is rejected under cosine. A candidate whose *score* comes out non-finite anyway
  (cosine squared-norm underflow, dot-product overflow — possible from finite inputs) is
  skipped during selection. NaN never enters a ranking.
- One vector per element; re-add replaces; element removal purges the vector O(1).
- No dynamic code, no new capability flags — plain data endpoints under the existing
  API-key/rate-limit posture.
