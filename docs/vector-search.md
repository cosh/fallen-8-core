# Vector search

`VectorIndex` is an exact k-nearest-neighbour index over `float[]` embedding vectors: a SIMD
brute-force scan (`TensorPrimitives`) over one contiguous vector slab — no approximate
structures, no recall parameter, always the true top-k. Because every hit is a graph element,
similarity search is a graph entry point, not a detached vector store. All routes below also
answer under `/ns/{ns}/…` for a specific namespace ([namespaces.md](namespaces.md)).

## Metrics and ordering

| Metric | Score | `higherIsBetter` | Zero-norm vectors |
|---|---|---|---|
| `Cosine` (default) | cosine similarity, [-1, 1] | `true` | rejected on add and query |
| `DotProduct` | inner product, unbounded | `true` | allowed |
| `L2` | Euclidean distance, ≥ 0 | `false` | allowed |

Scores are raw — interpret them via the `metric` and `higherIsBetter` fields the response
carries. Ordering is deterministic: best score first, ties broken by ascending element id.
Candidates whose score comes out non-finite (possible from finite inputs, e.g. dot-product
overflow) are skipped; NaN never enters a ranking.

## Creating a vector index

A vector index is created through the normal index surface ([indexes.md](indexes.md)):
`POST /index` with `pluginType: "VectorIndex"`. Options:

| Option | Required | Meaning |
|---|---|---|
| `dimension` | yes | Fixed vector dimension, 1–4096 |
| `metric` | no | `Cosine` (default), `DotProduct`, or `L2` |
| `embeddingName` | no | Binds the index to a named element embedding — the index then maintains itself and rejects explicit adds ([semantic-traversal.md](semantic-traversal.md)) |
| `model` | no | Opaque model-identity string, stored and persisted; enforced by the embedding provider ([semantic-traversal.md](semantic-traversal.md)) |

The endpoint returns `true`/`false`; invalid options (dimension out of range, unknown metric)
fail creation with the reason logged.

```bash
curl -X POST http://localhost:8080/index \
  -H "Content-Type: application/json" \
  -d '{
        "uniqueId": "docEmbeddings",
        "pluginType": "VectorIndex",
        "pluginOptions": {
          "dimension": { "propertyValue": "3", "fullQualifiedTypeName": "System.Int32" },
          "metric":    { "propertyValue": "Cosine", "fullQualifiedTypeName": "System.String" }
        }
      }'
```

```powershell
$body = @{
    uniqueId      = "docEmbeddings"
    pluginType    = "VectorIndex"
    pluginOptions = @{
        dimension = @{ propertyValue = "3"; fullQualifiedTypeName = "System.Int32" }
        metric    = @{ propertyValue = "Cosine"; fullQualifiedTypeName = "System.String" }
    }
} | ConvertTo-Json -Depth 5
Invoke-RestMethod -Method Post -Uri http://localhost:8080/index -ContentType "application/json" -Body $body
```

## Adding vectors

`PUT /index/vector/{indexId}` adds (or replaces — one vector per element) with exactly one of
two modes:

| Mode | Body | Use when |
|---|---|---|
| Explicit | `{ "graphElementId": 42, "vector": [0.1, 0.2, 0.3] }` | The vector lives only in the index |
| Property | `{ "graphElementId": 42, "propertyId": "embedding" }` | The element carries the vector as a `float[]` property |

Responses: `200 true` on success; `400` with a reason for wrong dimension, NaN/Infinity
components, a zero-norm vector under `Cosine`, both/neither mode, a missing or non-`float[]`
property, not a vector index, or an index bound to an embedding (bound indices maintain
themselves — write the element embedding instead, see
[semantic-traversal.md](semantic-traversal.md)); `404` for an unknown index or element.
Removing an element purges its vector automatically.

```bash
curl -X PUT http://localhost:8080/index/vector/docEmbeddings \
  -H "Content-Type: application/json" \
  -d '{ "graphElementId": 42, "vector": [0.12, -0.5, 0.33] }'
```

```powershell
$body = @{ graphElementId = 42; vector = @(0.12, -0.5, 0.33) } | ConvertTo-Json
Invoke-RestMethod -Method Put -Uri http://localhost:8080/index/vector/docEmbeddings -ContentType "application/json" -Body $body
```

## Querying: `POST /scan/index/vector`

| Field | Required | Meaning |
|---|---|---|
| `indexId` | yes | The vector index to query |
| `query` | yes | Query vector; must match the index dimension, finite components, non-zero-norm under `Cosine` |
| `k` | yes | Number of neighbours, 1–1024 |
| `kind` | no | `vertex`, `edge`, or `any` (default); lowercase |
| `label` | no | Exact, case-sensitive label match; unlabeled elements never match |

Constraints are applied before top-k selection, so you get k *matching* elements (fewer only
when the matching corpus is smaller). Removed elements never appear. `400` covers invalid
queries and non-vector indices; `404` an unknown index.

```bash
curl -X POST http://localhost:8080/scan/index/vector \
  -H "Content-Type: application/json" \
  -d '{ "indexId": "docEmbeddings", "query": [0.1, 0.2, 0.3], "k": 10, "kind": "vertex", "label": "person" }'
```

```powershell
$body = @{ indexId = "docEmbeddings"; query = @(0.1, 0.2, 0.3); k = 10; kind = "vertex"; label = "person" } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri http://localhost:8080/scan/index/vector -ContentType "application/json" -Body $body
```

Response — hits are graph element ids with raw scores, best first:

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

## From hits to graph (GraphRAG)

kNN hits are graph elements, so retrieval does not stop at a ranked list: connect two hits
with a path query ([path-finding.md](path-finding.md)), expand a hit's neighbourhood as a
subgraph ([subgraphs.md](subgraphs.md)), or read its properties and adjacency directly
([graph-model.md](graph-model.md)) — then feed the retrieved neighbourhood, not isolated
snippets, to the model. To rank or filter *during* a traversal instead of before it, use
semantic traversal ([semantic-traversal.md](semantic-traversal.md)).

## Memory and durability

Budget roughly `4·d` bytes per indexed element for the vector plus ~64 bytes bookkeeping —
the vectors dominate (d=768: ~3.1 kB/element, 1 M elements ≈ 3.1 GB). Storing the embedding
additionally as an element property or embedding is a second full copy.

Index contents persist in checkpoints ([save-games.md](save-games.md)) but index writes are
not WAL-logged: vectors added to an *unbound* index after the last checkpoint do not survive
crash replay. A *bound* index rebuilds itself from WAL-covered element embeddings
([semantic-traversal.md](semantic-traversal.md)); for unbound indices, property-mode adds
let you re-add from WAL-recovered properties after a replay.

## See also

- [indexes.md](indexes.md) — the index surface all families share
- [semantic-traversal.md](semantic-traversal.md) — element embeddings, bound indices, the embedding provider, semantic path/subgraph blocks
- [path-finding.md](path-finding.md) / [subgraphs.md](subgraphs.md) — the traversal surface kNN hits feed into
- [namespaces.md](namespaces.md) — per-namespace routing (`/ns/{ns}/…`)
- [rest-api.md](rest-api.md) — OpenAPI document and Scalar UI
