# Indexes

An index is an optional acceleration structure that maps keys to graph elements so a lookup
does not have to walk the graph. Indexes are a complement to Fallen-8's primary query path —
C# delegate predicates ([delegates.md](delegates.md)) and full-graph scans
([graph-model.md](graph-model.md)) — not a replacement for it. Each index is a plugin keyed
by a `uniqueId`; the built-in types are discovered through the plugin system
([plugins.md](plugins.md)), and third-party index plugins are picked up the same way. Every
route below also answers under `/ns/{ns}/…` for a specific namespace
([namespaces.md](namespaces.md)); each namespace owns its own set of indexes.

## Built-in index types

The `pluginType` is the exact string `POST /index` expects. The query capability is derived
from the interfaces the index implements and is reported per index by `GET /status`.

| `pluginType` | Indexes | Query capability | Scan endpoint(s) |
|---|---|---|---|
| `DictionaryIndex` | any comparable key → elements (multi-value buckets) | `equality` | `POST /scan/index/all` |
| `RangeIndex` | a single totally-ordered comparable key type | `equality`, `range` | `POST /scan/index/all`, `POST /scan/index/range` |
| `RegExIndex` | string keys, matched by regular expression | `equality`, `fulltext` | `POST /scan/index/all`, `POST /scan/index/fulltext` |
| `SpatialIndex` | geometry (R-Tree / R*-tree) | `spatial` | `POST /scan/index/spatial` |
| `VectorIndex` | `float[]` embedding vectors (exact kNN) | `vector` | `POST /scan/index/vector` |

Notes:

- `RangeIndex` requires its keys to form a total order (one comparable type; mixed types or
  `Double.NaN` have undefined ordering and can throw while sorting).
- `RegExIndex` matching is case-insensitive and treats the query as a .NET regular expression.
- `SpatialIndex` and `VectorIndex` do **not** report `equality`: their keys (geometry,
  `float[]`) cannot travel as a scan endpoint's typed literal.

## Creation options

`POST /index` takes `pluginOptions` as a map of option name → `{ "propertyValue", "fullQualifiedTypeName" }`,
where the value is parsed via a closed primitive allow-list (string, the integer/float types,
`bool`, `DateTime`, `Guid`, …).

- **`DictionaryIndex`, `RangeIndex`, `RegExIndex`** take **no** options — they ignore
  `pluginOptions` entirely and are populated per element (see below).
- **`VectorIndex`** takes `dimension` (required), `metric`, `embeddingName`, `model` — see
  [vector-search.md](vector-search.md).
- **`SpatialIndex` is not creatable over REST**: it needs .NET-object options (a metric, a
  dimension list) that the primitive allow-list cannot express, so `POST /index` returns
  `false` for it. It is created in-process against the engine.

## REST surface

| Action | Route | Body | Returns |
|---|---|---|---|
| Create an index | `POST /index` | `PluginSpecification` (`uniqueId`, `pluginType`, `pluginOptions`) | `true`/`false` |
| Add an element under a key | `PUT /index/{indexId}` | `IndexAddToSpecification` (`graphElementId`, `key`) | `true`/`false` |
| Add a vector (vector family) | `PUT /index/vector/{indexId}` | see [vector-search.md](vector-search.md) | `true`/`false` |
| Remove one element from an index | `DELETE /index/{indexId}/{graphElementId}` | — | `true`/`false` |
| Remove a key | `DELETE /index/{indexId}/propertyValue` | `PropertySpecification` (the key) | `true`/`false` |
| Delete an index | `DELETE /index/{indexId}` | — | `true`/`false` |
| List indexes | `GET /status` | — | inventory (id, type, capabilities, key/value counts) |

Listing and per-index counts are part of the status surface ([observability.md](observability.md)).
Index definitions are immutable — there is no update; delete and recreate. Populating a
bucket or fulltext index is explicit: created empty, then filled with `PUT /index/{indexId}`,
one element and key at a time. Removing a graph element (through a transaction) purges it from
every index automatically. A `VectorIndex` bound to an embedding name maintains itself and
rejects explicit adds ([semantic-traversal.md](semantic-traversal.md)).

Watch the two key shapes: an index **add** key uses `propertyValue` (a `PropertySpecification`),
while a **scan** literal uses `value` (a `LiteralSpecification`); both carry
`fullQualifiedTypeName`.

## Worked example: a dictionary index

Create a dictionary index, index element `42` under the key `"Alice"`, then look it up.

```bash
# 1. Create (bucket indexes take no options)
curl -X POST http://localhost:8080/index \
  -H "Content-Type: application/json" \
  -d '{ "uniqueId": "nameIndex", "pluginType": "DictionaryIndex" }'

# 2. Index element 42 under key "Alice"
curl -X PUT http://localhost:8080/index/nameIndex \
  -H "Content-Type: application/json" \
  -d '{ "graphElementId": 42,
        "key": { "propertyValue": "Alice", "fullQualifiedTypeName": "System.String" } }'

# 3. Equality scan -> [42]
curl -X POST http://localhost:8080/scan/index/all \
  -H "Content-Type: application/json" \
  -d '{ "indexId": "nameIndex",
        "operator": 0,
        "literal": { "value": "Alice", "fullQualifiedTypeName": "System.String" },
        "resultType": "Vertices" }'
```

```powershell
# 1. Create
Invoke-RestMethod -Method Post -Uri http://localhost:8080/index -ContentType "application/json" `
  -Body (@{ uniqueId = "nameIndex"; pluginType = "DictionaryIndex" } | ConvertTo-Json)

# 2. Index element 42 under key "Alice"
$add = @{ graphElementId = 42
          key = @{ propertyValue = "Alice"; fullQualifiedTypeName = "System.String" } } | ConvertTo-Json
Invoke-RestMethod -Method Put -Uri http://localhost:8080/index/nameIndex -ContentType "application/json" -Body $add

# 3. Equality scan -> @(42)
$scan = @{ indexId = "nameIndex"
           operator = 0
           literal = @{ value = "Alice"; fullQualifiedTypeName = "System.String" }
           resultType = "Vertices" } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri http://localhost:8080/scan/index/all -ContentType "application/json" -Body $scan
```

## Scanning an index

All scan endpoints return the matching graph-element ids (the vector and fulltext scans
return a richer object). `resultType` is a string enum — `Vertices`, `Edges`, or `Both`.

| Endpoint | Spec fields | Use |
|---|---|---|
| `POST /scan/index/all` | `indexId`, `operator`, `literal` (`value`, `fullQualifiedTypeName`), `resultType` | Equality (any index) and ordered comparisons; a `RangeIndex` answers ordered ops in `O(log n + k)`, other indexes fall back to an `O(n)` key scan |
| `POST /scan/index/range` | `indexId`, `leftLimit`, `rightLimit`, `fullQualifiedTypeName`, `includeLeft`, `includeRight`, `resultType` | Bounded range on a `RangeIndex` |
| `POST /scan/index/fulltext` | `indexId`, `requestString` | Regex search on a `RegExIndex`; returns matched elements with highlights and a score |
| `POST /scan/index/spatial` | `indexId`, `graphElementId`, `distance` | Elements within `distance` of a reference element in a `SpatialIndex` |
| `POST /scan/index/vector` | `indexId`, `query`, `k`, `kind`, `label` | Exact kNN — see [vector-search.md](vector-search.md) |

`operator` on `POST /scan/index/all` is the integer-valued `BinaryOperator` (it is **not** a
string on the wire):

| Value | Operator | | Value | Operator |
|---|---|---|---|---|
| `0` | `Equals` | | `3` | `Lower` |
| `1` | `Greater` | | `4` | `LowerOrEquals` |
| `2` | `GreaterOrEquals` | | `5` | `NotEquals` |

An index scan hits only the keys you added to that index. To scan every element's live
properties without an index, use the full-graph scan `POST /scan/graph/property/{propertyId}`
([graph-model.md](graph-model.md)) or a compiled delegate ([delegates.md](delegates.md)).

## Vector indexes

`VectorIndex` is an exact, SIMD brute-force k-nearest-neighbour index over `float[]` vectors,
and its hits are graph elements, so similarity search is a graph entry point. It shares the
index surface above (create with `pluginType: "VectorIndex"`, delete, list) but has its own
typed add (`PUT /index/vector/{indexId}`) and query (`POST /scan/index/vector`). The full
contract — metrics, ordering, options, bound embeddings, memory — lives in
[vector-search.md](vector-search.md); embedding-driven population is covered in
[semantic-traversal.md](semantic-traversal.md).

## Persistence

Every built-in index is persistable (`CanPersist == true`): its contents are written into
checkpoints and restored on load, where the factory recreates each index and rehydrates its
entries by graph-element id — indexes are **not** rebuilt from a property sweep. Index writes
are not WAL-logged, so entries added after the last checkpoint do not survive crash replay
(a bound vector index is the exception — it rebuilds from WAL-covered element embeddings). See
[save-games.md](save-games.md).

## See also

- [vector-search.md](vector-search.md) — the `VectorIndex` kNN deep dive
- [semantic-traversal.md](semantic-traversal.md) — element embeddings and bound vector indexes
- [graph-model.md](graph-model.md) — vertices, edges, properties, transactions, full-graph scans
- [delegates.md](delegates.md) — why there is no query language: queries are compiled C# delegates
- [plugins.md](plugins.md) — the plugin system indexes are discovered through
- [observability.md](observability.md) — `GET /status` index inventory and counts
- [studio.md](studio.md) — the F8 Studio Indexes screen
- [save-games.md](save-games.md) — checkpoints and durability
- [namespaces.md](namespaces.md) — per-namespace routing (`/ns/{ns}/…`)
- [rest-api.md](rest-api.md) — OpenAPI document and Scalar UI
