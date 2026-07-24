# Subgraphs

A subgraph is a pattern-matched subset of a graph, extracted into a new, **standalone Fallen-8 graph** (its own engine instance) that you read, analyze, nest, and persist independently of its source. You give it optional vertex/edge pre-filters and an ordered, alternating vertex ↔ edge pattern; the engine keeps only the elements that lie on a matching path and prunes everything else. As everywhere in Fallen-8, there is no query language — every filter is a C# delegate ([delegates](delegates.md)), a code-free semantic threshold, or a stored template.

![A subgraph extracted from a source graph by matching a person-knows-person pattern; the company vertex and works_at edge are pruned.](../pics/subgraph-illustration.svg)

## How extraction works

The default algorithm is the `Breadth First Search Subgraph Algorithm` plugin ([plugins](plugins.md)). It runs in phases:

1. **Copy vertices** matching the top-level `vertexFilter` (or all vertices, if omitted) into a fresh graph.
2. **Copy edges** matching the top-level `edgeFilter` (or all) — but only where *both* endpoints were copied in phase 1.
3. **Prune to the pattern**: find every path matching the ordered `patterns`, then delete every vertex and edge not on at least one matching path. Paths are simple — revisiting an element closes a cycle and discards that path.

If `patterns` is empty, the copied elements (phases 1–2) are the result, unpruned. A syntactically valid pattern that matches nothing yields a registered **empty** subgraph (still `201`), identically whether the source is empty or populated. A *structurally* invalid pattern is rejected with `400` (see the pattern rules below). The extract holds deep copies with its own ids, so it never mutates the source and can itself be the source of a nested subgraph.

## REST surface

The bare paths below target the `default` namespace and also answer under `/ns/{ns}/…` ([namespaces](namespaces.md)).

| Route | Effect | Notable responses |
|---|---|---|
| `PUT /subgraph` | Create + register from a specification. `?fromSubGraph=<name>` sources it from an existing subgraph (nesting) instead of the whole graph. | `201` summary · `400` invalid spec/pattern or compile failure · `401`/`403` (gating) · `404` unknown `fromSubGraph`/stored query · `409` duplicate name or quota · `500` |
| `GET /subgraph` | List registered subgraph names | `200` array of strings |
| `GET /subgraph/{name}` | Summary: element counts, algorithm, source id, `canRecalculate`, metadata, bound `semantic` | `200` · `404` |
| `GET /subgraph/{name}/graph` | The extracted vertices + edges. `?maxElements=` caps each list (default 1000) | `200` `{ vertices, edges }` · `404` |
| `POST /subgraph/{name}/recalculate` | Re-extract against the current source | `200` summary · `404` · `409` not recalculable |
| `DELETE /subgraph/{name}` | Deregister | `204` · `404` · `500` |

**Gating:** a request carrying any inline fragment needs an authenticated caller and `EnableDynamicCodeExecution=true`, else `403`; a stored-query reference and the pure-data `semantic` block are never gated ([security](security.md)).

## The pattern model

`patterns` is an ordered list that must **start and end with a vertex** and **alternate** vertex ↔ edge; a variable-length edge may not lead. Each entry:

| Field | Applies to | Meaning |
|---|---|---|
| `type` | all | `Vertex`, `Edge`, or `VariableLengthEdge` (default `Vertex`) |
| `patternName` | all | Optional label for the step (e.g. reported in the `semantic` summary) |
| `vertexFilter` | `Vertex` | Fragment over the vertex |
| `semanticMinScore` | `Vertex` | Code-free alternative to `vertexFilter` — owns the same slot (setting both is `400`) |
| `direction` | edge | `OutgoingEdge` (default), `IncomingEdge`, or `UndirectedEdge` |
| `edgePropertyFilter` | edge | Fragment over the edge's property id (a string), checked before the edge itself |
| `edgeFilter` | edge | Fragment over the edge |
| `minLength` / `maxLength` | `VariableLengthEdge` | Hop range, inclusive (both default 1) |

The filter slots — plus the top-level `vertexFilter` / `edgeFilter` pre-filters — compile to these delegate types. Each fragment is a `return` statement over a single-parameter lambda; the full contract (parameter members, property access, the semantic `context`) lives in [delegates](delegates.md).

| Slot | Delegate | Fragment |
|---|---|---|
| `vertexFilter` (pattern + top-level) | `VertexFilter` | `return (v) => …;` — `v` is a `VertexModel` |
| `edgeFilter` (pattern + top-level) | `EdgeFilter` | `return (e) => …;` — `e` is an `EdgeModel` |
| `edgePropertyFilter` | `EdgePropertyFilter` | `return (p) => …;` — `p` is the edge property-id string |

An omitted or empty fragment matches everything.

**Variable-length semantics:** the edge filters apply to *every* hop, but the vertex pattern following a variable-length edge constrains only the **terminal** vertex of the matched path — intermediate vertices are unconstrained. Model an intermediate-vertex constraint with explicit fixed-length edge/vertex pairs instead.

**Code-free alternatives:** invoke a registered subgraph template with `"storedQuery": "<name>"` in place of all inline fragments ([stored queries](stored-queries.md)); or add a `semantic` block that scores vertices by embedding similarity — `minScore` is a code-free top-level vertex pre-filter and per-step `semanticMinScore` is its pattern-level twin ([semantic traversal](semantic-traversal.md)). Each is mutually exclusive with the fragments it replaces.

## Worked example

Match `person -knows-> person`, keeping only those people and their `knows` edges (the illustration's pruning):

```bash
curl -X PUT http://localhost:8080/subgraph \
  -H "Content-Type: application/json" \
  -d '{
    "name": "people-who-know-people",
    "vertexFilter": "return (v) => v.Label == \"person\";",
    "patterns": [
      { "type": "Vertex", "patternName": "p1", "vertexFilter": "return (v) => v.Label == \"person\";" },
      { "type": "Edge",   "patternName": "knows", "direction": "OutgoingEdge", "edgePropertyFilter": "return (p) => p == \"knows\";" },
      { "type": "Vertex", "patternName": "p2", "vertexFilter": "return (v) => v.Label == \"person\";" }
    ]
  }'
```

```powershell
$spec = @{
  name = "people-who-know-people"
  vertexFilter = 'return (v) => v.Label == "person";'
  patterns = @(
    @{ type = "Vertex"; patternName = "p1"; vertexFilter = 'return (v) => v.Label == "person";' }
    @{ type = "Edge";   patternName = "knows"; direction = "OutgoingEdge"; edgePropertyFilter = 'return (p) => p == "knows";' }
    @{ type = "Vertex"; patternName = "p2"; vertexFilter = 'return (v) => v.Label == "person";' }
  )
} | ConvertTo-Json -Depth 5
Invoke-RestMethod -Method Put -Uri http://localhost:8080/subgraph -ContentType "application/json" -Body $spec
```

The `201` body is a summary: `{ "name": "people-who-know-people", "vertexCount": 3, "edgeCount": 2, "algorithmPluginName": "Breadth First Search Subgraph Algorithm", "canRecalculate": true, … }`. Read the extracted elements back:

```bash
curl "http://localhost:8080/subgraph/people-who-know-people/graph?maxElements=500"
```

```powershell
Invoke-RestMethod -Uri "http://localhost:8080/subgraph/people-who-know-people/graph?maxElements=500"
```

```jsonc
{
  "vertices": [
    { "id": 1, "label": "person",
      "properties": [ { "propertyId": "name", "propertyValue": "Alice" } ],
      "outEdges": { "knows": [10] }, "inEdges": {} }
  ],
  "edges": [
    { "id": 10, "label": "knows", "sourceVertex": 1, "targetVertex": 2 }
  ]
}
```

Ids are the subgraph's own, not the source's.

## Recalculation, nesting, persistence

- **Recalculate** (`POST …/recalculate`) re-runs the extraction against the **current** state of the source and swaps in the fresh result; the subgraph's id and name are preserved, its contents fully replaced. Only a subgraph that retains its source and algorithm (`canRecalculate: true` — every subgraph created over REST) can be recalculated; a subgraph registered directly from delegates in the embedded engine cannot (`409`).
- **Nesting**: with `?fromSubGraph=<parent>` the source is another subgraph, so the pattern runs over the parent's extracted graph. The dependency is tracked so the whole tree rebuilds in order on load; recalculate each level you need refreshed.
- **Persistence**: every REST-created subgraph attaches a recipe (its materialized specification) that survives `PUT /save`/load and write-ahead-log crash recovery, rebuilt against the current graph in dependency order ([save games](save-games.md)). Subgraphs created directly from delegates have no recipe and are not persisted.

**Limits** (per namespace; `409` on breach): 1024 subgraphs, 10,000,000 elements per subgraph, and 25,000,000 elements summed across all subgraphs.

## See also

- [Delegates](delegates.md) — the no-query-language model, fragment shape, compilation, validation
- [Stored queries](stored-queries.md) — register a subgraph template once, invoke by name with no code
- [Semantic traversal](semantic-traversal.md) — the `semantic` block and score thresholds
- [Security](security.md) — API key and the `EnableDynamicCodeExecution` switch
- [Save games](save-games.md) — checkpoints, the write-ahead log, and rebuild-on-load
- [Namespaces](namespaces.md) — per-namespace isolation and the `/ns/{ns}/…` route twins
- [Graph model](graph-model.md) — vertices, edges, properties, and transactions
- [Plugins](plugins.md) — the subgraph algorithm plugin system
- [REST API](rest-api.md) — the OpenAPI document and Scalar reference
