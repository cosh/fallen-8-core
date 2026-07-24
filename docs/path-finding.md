# Path finding

Fallen-8 finds shortest paths between two vertices with `POST /path/{from}/to/{to}`. Two algorithms ship in the box — an unweighted hop-count search (`BLS`, the default) and a weighted Dijkstra (`DIJKSTRA`) — and both accept optional C# filter and cost fragments that decide which vertices and edges are traversable and how much each step weighs. There is no query language: those fragments are runtime-compiled delegates ([delegates.md](delegates.md)). A path query is a read, so no transaction is involved; every route also answers under `/ns/{ns}/…` for a specific graph ([namespaces.md](namespaces.md)).

## The request

`POST /path/{from}/to/{to}` — `from` and `to` are vertex ids. The body is optional; `{}` uses every default.

| Field | Type | Default | Meaning |
|---|---|---|---|
| `pathAlgorithmName` | string | `BLS` | `BLS` or `DIJKSTRA` (below). An unknown name yields an empty result, not an error. |
| `maxDepth` | uint16 | `7` | Maximum number of edges on a returned path. `0` returns `[]` immediately. |
| `maxResults` | uint16 | `65535` | Maximum paths to return. For `DIJKSTRA` this is the `K` of K-shortest — set it to `1` for just the cheapest. |
| `maxPathWeight` | double | unbounded | Inclusive cumulative-weight cap; honoured by `DIJKSTRA`, ignored by `BLS`. |
| `filter` | object | — | `vertexFilter` / `edgeFilter` / `edgePropertyFilter` fragments (below). |
| `cost` | object | — | `vertexCost` / `edgeCost` fragments (below). |
| `storedQuery` | string | — | Invoke a registered `Path` query by name instead of inline fragments — mutually exclusive with `filter`/`cost` ([stored-queries.md](stored-queries.md)). |
| `semantic` | object | — | Code-free similarity filter/cost block ([semantic-traversal.md](semantic-traversal.md)). |

## Algorithms

Both traverse edges in **both directions** (undirected reachability over directed edges); each step records the direction it actually used. Both are discovered as plugins ([plugins.md](plugins.md)) — the shipped set is reported by `GET /status` → `availablePathPlugins`.

| | `BLS` (default) | `DIJKSTRA` |
|---|---|---|
| Optimises | fewest hops | least total weight |
| Cost block | ignored — every weight is `0` | consumed |
| `maxPathWeight` | ignored | enforced (inclusive) |
| Multiple results | all fewest-hop paths (≤ `maxResults`) | `K` least-weight **loop-free** paths, non-decreasing weight (Yen's algorithm) |

Pick **`BLS`** for reachability and degrees-of-separation questions where every edge counts the same. Pick **`DIJKSTRA`** when edges or vertices carry a cost to minimise — e.g. the air-routes sample's `km` for the shortest flight distance, or the attack-surface sample's `exploitCost` for the least-effort attack path ([samples.md](samples.md)).

## Filters and costs

Each slot is a one-statement C# fragment of the form `return (<param>) => <expr>;`. Filters return `bool` (return `false` to make the element non-traversable); costs return `double` (the step weight). The parameter name is yours; only its type is fixed by the slot.

| Slot | Receives | Returns | Example |
|---|---|---|---|
| `filter.vertexFilter` | `VertexModel` | `bool` | `return (v) => v.Label == "person";` |
| `filter.edgeFilter` | `EdgeModel` | `bool` | `return (e) => e.Label == "trusts";` |
| `filter.edgePropertyFilter` | `string` (edge-property id) | `bool` | `return (p) => p == "knows";` |
| `cost.vertexCost` | `VertexModel` | `double` | `return (v) => 0.0;` |
| `cost.edgeCost` | `EdgeModel` | `double` | `return (e) => e.TryGetProperty<double>(out var w, "weight") ? w : 1.0;` |

These fragments are compiled at runtime with Roslyn and run in-process; the full contract, the accessor surface, and the `POST /delegates/validate` compile-check live in [delegates.md](delegates.md). Because they introduce code, a request carrying **any** inline fragment requires dynamic code execution to be enabled, or it is rejected with `403` ([security.md](security.md)). Two code-free alternatives are **not** gated: a `storedQuery` reference ([stored-queries.md](stored-queries.md)) and the `semantic` block ([semantic-traversal.md](semantic-traversal.md)).

## Cost model (DIJKSTRA)

Reaching a neighbour `v` across edge `e` costs `edgeCost(e) + vertexCost(v)`, and `totalWeight` is the sum over the whole path. The defaults matter:

- **No `cost` block at all** → each edge costs `1` and each vertex `0`, so `DIJKSTRA` degenerates to a fewest-hop search whose `totalWeight` equals the hop count.
- **A `cost` block with only `edgeCost`** → `vertexCost` falls back to its own default `return (v) => 1.0;`, which adds `1` per step. For a pure edge-weight sum, set `vertexCost` to `return (v) => 0.0;` explicitly.

Costs must be non-negative; a negative step cost is clamped to `0` (logged once per query). `maxDepth` is enforced during the search, so a cheaper-but-longer route is correctly rejected in favour of a costlier one that fits the hop budget.

## Response

`200` with a JSON array of paths — empty `[]` when none exist, which also covers a missing `from`/`to` vertex and an unknown algorithm name.

| Path field | Meaning |
|---|---|
| `pathElements[]` | Ordered hops from `from` to `to` |
| `totalWeight` | Sum of the element weights (`0` for `BLS`) |

| Element field | Type | Meaning |
|---|---|---|
| `sourceVertexId` / `targetVertexId` | int | The hop's from / to vertex |
| `edgeId` | int | The edge traversed on this hop |
| `edgePropertyId` | string | The edge-property id the edge sits under |
| `direction` | int | `0` = traversed against the edge's stored direction, `1` = with it (`2` = undirected; path traversal emits only `0`/`1`) |
| `weight` | double | This step's cost (`0` for `BLS`) |

## Example: all shortest paths (BLS)

Create the built-in sample graph (in it `Trent` = 4, `Mallory` = 3), then find every fewest-hop path between them:

```bash
curl -X PUT http://localhost:8080/unittest
curl -X POST http://localhost:8080/path/4/to/3 \
  -H "Content-Type: application/json" -d '{}'
```
```powershell
Invoke-RestMethod -Method Put -Uri http://localhost:8080/unittest
Invoke-RestMethod -Method Post -Uri http://localhost:8080/path/4/to/3 -ContentType application/json -Body '{}'
```

Two two-hop paths come back — `Trent → Alice → Mallory` and `Trent → Bob → Mallory` — each with weight `0`, because `BLS` never consumes the cost block:

```json
[
  { "pathElements": [
      { "sourceVertexId": 4, "targetVertexId": 0, "edgeId": 6, "edgePropertyId": "trusts",  "direction": 0, "weight": 0 },
      { "sourceVertexId": 0, "targetVertexId": 3, "edgeId": 9, "edgePropertyId": "attacks", "direction": 0, "weight": 0 } ],
    "totalWeight": 0 },
  { "pathElements": [
      { "sourceVertexId": 4, "targetVertexId": 1, "edgeId": 7,  "edgePropertyId": "trusts",  "direction": 0, "weight": 0 },
      { "sourceVertexId": 1, "targetVertexId": 3, "edgeId": 10, "edgePropertyId": "attacks", "direction": 0, "weight": 0 } ],
    "totalWeight": 0 }
]
```

## Example: cheapest weighted path (DIJKSTRA)

Given three vertices where `A → B` weighs 10 while `A → C` and `C → B` each weigh 1, the cheapest `A → B` route is the two-hop `A → C → B` (total 2), not the direct weight-10 edge. Read the weight off each edge with `edgeCost`, zero out `vertexCost`, and ask for the single best path (`A` = 11, `B` = 12 here):

```bash
curl -X POST http://localhost:8080/path/11/to/12 \
  -H "Content-Type: application/json" \
  -d '{
        "pathAlgorithmName": "DIJKSTRA",
        "maxResults": 1,
        "cost": {
          "vertexCost": "return (v) => 0.0;",
          "edgeCost": "return (e) => e.TryGetProperty<double>(out var w, \"weight\") ? w : 1.0;"
        }
      }'
```
```powershell
$body = @{
  pathAlgorithmName = "DIJKSTRA"; maxResults = 1
  cost = @{
    vertexCost = "return (v) => 0.0;"
    edgeCost   = 'return (e) => e.TryGetProperty<double>(out var w, "weight") ? w : 1.0;'
  }
} | ConvertTo-Json -Depth 5
Invoke-RestMethod -Method Post -Uri http://localhost:8080/path/11/to/12 -ContentType application/json -Body $body
```

```json
[
  { "pathElements": [
      { "sourceVertexId": 11, "targetVertexId": 13, "edgeId": 15, "edgePropertyId": "road", "direction": 1, "weight": 1 },
      { "sourceVertexId": 13, "targetVertexId": 12, "edgeId": 16, "edgePropertyId": "road", "direction": 1, "weight": 1 } ],
    "totalWeight": 2 }
]
```

Leaving `maxResults` at its default would instead return every loop-free `A → B` route in non-decreasing weight order (here the weight-2 detour, then the weight-10 direct edge).

## Status codes

| Code | When |
|---|---|
| `200` | Paths found — or none (`[]`); a missing `from`/`to` vertex or an unknown algorithm also returns `[]` |
| `400` | Malformed body, a fragment that fails to compile (Roslyn diagnostics in the body), or `storedQuery` mixed with inline fragments |
| `401` / `403` | No credential supplied / inline code sent while dynamic code execution is disabled ([security.md](security.md)) |
| `404` | The referenced `storedQuery` name does not exist |
| `409` | The referenced `storedQuery` is not invocable (its recompile-on-load failed) |
| `413` / `429` | Body over 1 MiB / sensitive-endpoint rate limit exceeded |

## See also

- [Delegates](delegates.md) — the no-query-language philosophy, the fragment contract, compilation, and `/delegates/validate`
- [Stored queries](stored-queries.md) — precompiled `Path` queries invoked by name, no dynamic code required
- [Semantic traversal](semantic-traversal.md) — the code-free `semantic` similarity block carried on `/path`
- [Security](security.md) — the API key and the `EnableDynamicCodeExecution` gate
- [Graph model](graph-model.md) — vertices, edges, properties, and the transactions that build a graph
- [Samples](samples.md) — weighted datasets to traverse (air-routes `km`, attack-surface `exploitCost`)
- [Plugins](plugins.md) — how path algorithms are discovered
- [Namespaces](namespaces.md) — per-namespace routing (`/ns/{ns}/…`)
- Source: [`Algorithms/Path/`](../fallen-8-core/Algorithms/Path/), [`Delegates.cs`](../fallen-8-core/Algorithms/Delegates.cs)
