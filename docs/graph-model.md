# Graph model

Fallen-8 is a directed property graph held entirely in memory. A graph is a set of
**vertices** and **edges**; both are *graph elements* carrying an engine-assigned integer id, an
optional label, timestamps, and typed key/value properties. Every change goes through a single
transaction queue (one writer thread; readers never block); reads and full-graph property scans
run straight against the live graph. This page covers the model, the REST CRUD surface, and
`POST /scan/graph/property/...`. Index-backed scans live in [Indexes](indexes.md); C# query
delegates in [Delegates](delegates.md).

## Vertices, edges, and properties

| Concept | Detail |
| --- | --- |
| **Vertex** | A node. Has an id, optional `label`, and properties. |
| **Edge** | A **directed** relationship from a source vertex to a target vertex. Edges are graph elements too: an edge has its own id, `label`, and properties. |
| **`edgePropertyId`** | The relationship "slot" an edge occupies on its endpoints (e.g. `knows`, `trusts`). Adjacency is grouped by `edgePropertyId`; traversal reads are keyed by it. It is set at edge-creation time and is distinct from `label`. |
| **`label`** | A free-form category string on any element (e.g. `person`, `friendship`). Optional. |
| **Properties** | A per-element map of `string` key -> typed value. Empty by default; keys are unique per element. |

Every element also carries system fields: `id` (assigned by the engine on commit — you do not
choose it), `creationDate`, and `modificationDate`. Over REST, create requests take
`creationDate` as a Unix-seconds `uint` (`0` is fine); reads return both timestamps as ISO-8601.
Property keys beginning with `$embedding:` / `$embeddingModel:` are reserved for engine-managed
vector state — see [Semantic traversal](semantic-traversal.md).

### Typed property values

A property value crosses REST as a JSON **string** plus a `fullQualifiedTypeName` that names the
target .NET type. The string is parsed to that type with `InvariantCulture` (so `"0.8"` is always
0.8, never 8). Only this closed allow-list of primitive types is accepted:

`String`, `Boolean`, `Byte`, `SByte`, `Int16`, `UInt16`, `Int32`, `UInt32`, `Int64`, `UInt64`,
`Single`, `Double`, `Decimal`, `Char`, `DateTime`, `DateTimeOffset`, `TimeSpan`, `Guid`.

The name is case-insensitive and accepts the full name (`System.Int32`), the short name (`Int32`),
or the C# alias (`int`); an unknown name is a `400`.

## Mutation goes through the transaction queue

All writes (create, property add/remove, delete) are enqueued and applied by one writer thread —
serialized, while lock-free readers keep running. Over REST this is the `waitForCompletion` query
parameter on every write endpoint:

| `waitForCompletion` | Behaviour |
| --- | --- |
| `false` (default) | The write is enqueued and the call returns `202 Accepted` immediately. |
| `true` | The call awaits the outcome. A committed write is `202`; a rolled-back write maps to `400`/`404`/`409`/`500` by cause. |

A create returns no body — the new id is not echoed back; locate a just-created element with a
property scan (below) or `GET /graph`. Writer/commit internals are in [Architecture](architecture.md).

## REST CRUD

Base URL `http://localhost:8080`. These are the bare (default-namespace) paths; every route also
answers under `/ns/{name}/...` — see [Namespaces](namespaces.md).

| Method & path | Purpose |
| --- | --- |
| `PUT /vertex` | Create a vertex. Body: `VertexSpecification`. |
| `PUT /edge` | Create an edge (`404` if a referenced endpoint is missing and `waitForCompletion=true`). Body: `EdgeSpecification`. |
| `GET /vertex/{id}` · `GET /edge/{id}` | Read one element (`200`, or `204` when absent). |
| `GET /graphelement/{id}` | Read a vertex or edge by id. |
| `GET /graph?maxElements=1000` | A bounded page of vertices and edges (clamped to 100000 each). |
| `PUT /graphelement/{id}/{propertyId}` | Add or update one property. Body: `PropertySpecification`. |
| `DELETE /graphelement/{id}/{propertyId}` | Remove one property. |
| `DELETE /graphelement/{id}` | Remove a vertex or edge (removing a vertex detaches its edges). |
| `HEAD /tabularasa` | Erase all data in the addressed namespace (it stays registered, empty). |
| `GET /vertex/count` · `GET /edge/count` | Element counts. |

Vertex-scoped traversal reads (all return `204` when the vertex has nothing to return, `404`
when the vertex is missing on the degree routes):

| Method & path | Returns |
| --- | --- |
| `GET /vertex/{id}/edges/out` · `.../edges/in` | The `edgePropertyId` groups present. |
| `GET /vertex/{id}/edges/out/{edgePropertyId}` · `.../in/{edgePropertyId}` | Edge ids in that group. |
| `GET /vertex/{id}/edges/outdegree` · `.../indegree` | Total out/in degree. |
| `GET /vertex/{id}/edges/out/{edgePropertyId}/degree` · `.../in/.../degree` | Degree within a group. |
| `GET /edge/{id}/source` · `GET /edge/{id}/target` | The endpoint vertex ids. |

Creating a vertex, in both shells:

```bash
curl -X PUT http://localhost:8080/vertex \
  -H 'Content-Type: application/json' \
  -d '{
        "creationDate": 0,
        "label": "person",
        "properties": [
          { "propertyId": "name", "propertyValue": "Trent", "fullQualifiedTypeName": "System.String" },
          { "propertyId": "age",  "propertyValue": "35",    "fullQualifiedTypeName": "System.Int32"  }
        ]
      }'
```

```powershell
$body = @'
{
  "creationDate": 0,
  "label": "person",
  "properties": [
    { "propertyId": "name", "propertyValue": "Trent", "fullQualifiedTypeName": "System.String" },
    { "propertyId": "age",  "propertyValue": "35",    "fullQualifiedTypeName": "System.Int32"  }
  ]
}
'@
Invoke-RestMethod http://localhost:8080/vertex -Method Put -ContentType 'application/json' -Body $body
```

An edge is created the same way against `PUT /edge`, with this body shape:

```json
{
  "sourceVertex": 0,
  "targetVertex": 4,
  "edgePropertyId": "trusts",
  "label": "trusts",
  "creationDate": 0
}
```

Batch creation and file-based loading are covered in [Bulk import/export](bulk-import-export.md).

## Full-graph property scans

`POST /scan/graph/property/{propertyId}` walks **every** element and returns the ids of those
whose `{propertyId}` value satisfies the comparison. It is a linear O(n) scan with no index — for
indexed lookups use [Indexes](indexes.md). Request body:

| Field | Value |
| --- | --- |
| `operator` | The comparison, as an integer: `0` Equals, `1` Greater, `2` GreaterOrEquals, `3` Lower, `4` LowerOrEquals, `5` NotEquals. |
| `literal` | `{ "value": "<string>", "fullQualifiedTypeName": "<type>" }` — the value to compare against, typed as above. |
| `resultType` | `"Vertices"`, `"Edges"`, or `"Both"`. |

The response is a `200` with a JSON array of matching ids (empty when nothing matches); a missing
`literal` or an unknown/unconvertible type is a `400`.

Worked example against the built-in sample graph (create it with `PUT /unittest`; see
[Samples](samples.md)), which stores each person's name under `name` — find "Trent":

```bash
curl -X POST http://localhost:8080/scan/graph/property/name \
  -H 'Content-Type: application/json' \
  -d '{ "operator": 0,
        "literal": { "value": "Trent", "fullQualifiedTypeName": "System.String" },
        "resultType": "Vertices" }'
# => [4]
```

```powershell
$body = @'
{ "operator": 0,
  "literal": { "value": "Trent", "fullQualifiedTypeName": "System.String" },
  "resultType": "Vertices" }
'@
Invoke-RestMethod http://localhost:8080/scan/graph/property/name -Method Post -ContentType 'application/json' -Body $body
# => 4
```

## Using the engine as a library

Embedded in-process, the same model is reached without REST. Build a transaction, enqueue it, and
wait; read with the `Try*(out result, ...) : bool` pattern.

```csharp
var f8 = new Fallen8(loggerFactory);

// Write: enqueue, then wait for the writer thread to commit.
var tx = new CreateVerticesTransaction();
tx.AddVertex(creationDate: 0, label: "person",
             properties: new Dictionary<string, object> { { "name", "Trent" } });
f8.EnqueueTransaction(tx).WaitUntilFinished();

// Read: no queue, never blocks.
if (f8.TryGetVertex(out var v, 4) && v.TryGetProperty<string>(out var name, "name"))
{
    // name == "Trent"; v.GetOutDegree(), v.OutEdges, ...
}

// Full-graph scan.
f8.GraphScan(out var hits, "name", "Trent", BinaryOperator.Equals);
```

Reads (`TryGetVertex`, `TryGetEdge`, `TryGetGraphElement`, `GetAllVertices`, `GetAllEdges`,
`GetAllGraphElements`, `GraphScan`) live on `IFallen8Read`; `EnqueueTransaction` on
`IFallen8Write`. It returns a `TransactionInformation` whose `Completion` task and
`WaitUntilFinished()` await the outcome and whose `TransactionState` / `FailureReason` report a
rollback.

## See also

- [Indexes](indexes.md) — index types and `POST /scan/index/...`.
- [Delegates](delegates.md) — why there is no query language; C# predicates over the graph.
- [Path finding](path-finding.md) — traversals between vertices.
- [Namespaces](namespaces.md) — isolated graphs and `/ns/{name}` routing.
- [Semantic traversal](semantic-traversal.md) — element embeddings and reserved property keys.
- [Bulk import/export](bulk-import-export.md) — creating and dumping graphs in bulk.
- [REST API](rest-api.md) — versioning, OpenAPI, and shared conventions.
