# Graph analytics

Fallen-8 runs whole-graph algorithms — PageRank, weakly connected components, label
propagation, degree centrality, triangle counting — synchronously over the in-memory
adjacency, under a wall-clock budget, via `POST /analytics/{algorithm}`. Requests are plain
data (labels, numbers, flags): no [delegates](delegates.md) and no dynamic code, so
analytics works with `EnableDynamicCodeExecution=false` (see [security.md](security.md)).
Analytics algorithms are the third plugin family next to path traversers and subgraph
algorithms, discovered the same way (see [plugins.md](plugins.md)).

## Endpoints

| Route | What it does |
|---|---|
| `GET /analytics/algorithms` | Lists discovered algorithm plugins (name → description) |
| `POST /analytics/{algorithm}` | Runs one algorithm; returns metadata + a bounded result projection |
| `POST /analytics/{algorithm}/partition/{partitionId}` | Pages one partition's member vertex ids (WCC / LABELPROPAGATION) |

All three also answer under `/ns/{ns}/…` for a specific namespace (see
[namespaces.md](namespaces.md)).

## Algorithms

| Name | Kind | Computes | Notes |
|---|---|---|---|
| `PAGERANK` | score | PageRank via power iteration; scores sum to 1 over in-scope vertices | Out-edges by default; dangling vertices redistribute rank uniformly; parallel edges carry rank multiply; self-loops feed back |
| `WCC` | partition | Weakly connected components (union-find, ignores direction) | Component id = smallest member vertex id |
| `LABELPROPAGATION` | partition | Communities via synchronous label propagation | Most frequent neighbour label wins; smallest label breaks ties |
| `DEGREE` | score | Per-vertex degree (in / out / both) | Parallel edges and self-loops count as they exist |
| `TRIANGLECOUNT` | score | Per-vertex triangle count | Undirected simple-graph view: parallel edges deduplicated, self-loops ignored |

Third-party `IGraphAnalyticsAlgorithm` plugins appear alongside the built-ins.

## Request options

All fields are optional; an empty body `{}` runs over the whole graph with defaults.

| Field | Applies to | Default | Meaning |
|---|---|---|---|
| `vertexLabel` | all | whole graph | Only vertices with exactly this label participate; the run computes over the induced subgraph (only edges between two in-scope vertices are traversed) |
| `edgePropertyId` | all | all edges | Only edges in this adjacency group are traversed |
| `direction` | `PAGERANK`, `DEGREE`, `LABELPROPAGATION` | `out` / `both` / `both` | `"in"`, `"out"` or `"both"`; `WCC` and `TRIANGLECOUNT` ignore it |
| `maxIterations` | `PAGERANK`, `LABELPROPAGATION` | 100 / 20 | Iteration cap, ceiling 10 000; 0 = algorithm default. Reaching the cap is a normal 200 (`converged: false`, values usable) |
| `epsilon` | `PAGERANK` | 1e-6 | Convergence threshold (L1 delta between iterations); 0 = default |
| `parameters` | `PAGERANK` | `{"DampingFactor": 0.85}` | Algorithm-specific numeric knobs; `DampingFactor` must be within [0, 1] |
| `timeBudgetSeconds` | all | 30 (config) | Wall-clock budget; values above the configured ceiling (default 300) are a 400 |
| `maxResults` | all | 100 | Response row bound (top-K scores or partition summaries), ceiling 10 000 |
| `offset` | partition endpoint | 0 | Page offset into the ascending-id member list |
| `writeBack` | run endpoint | `false` | Write every in-scope vertex's value as a property (below) |
| `writeBackPropertyKey` | run endpoint | convention key | Overrides the property key; non-empty, at most 256 characters |

## Response

| Field | Meaning |
|---|---|
| `algorithm` | The plugin that ran |
| `converged` | `false` when the iteration cap stopped an iterative run (values still usable); single-pass algorithms always `true` |
| `iterationsRun`, `elapsedMs` | Completed iterations; wall-clock duration |
| `budgetExhausted` | `true` when the budget stopped the run and the values are the last completed iteration's |
| `vertexCount` | In-scope vertices |
| `statistics` | Run-level aggregates: `ComponentCount` (WCC), `CommunityCount` (LABELPROPAGATION), `TriangleCount` (global count = Σ per-vertex / 3), `Min`/`Max`/`Mean` (DEGREE) |
| `results` | Score algorithms: top-K vertices, score descending, ascending id tie-break |
| `partitions` | Partition algorithms: `{partitionId, size}` summaries, size descending, ascending id tie-break |
| `writeBack` | `{propertyKey, verticesWritten, chunks}` when the request opted in |

Responses are bounded by design: `maxResults` caps the rows, and the full per-vertex
result's delivery vehicle is write-back — not pagination through millions of rows.

### Partition membership

`POST /analytics/{algorithm}/partition/{partitionId}` returns one partition's member vertex
ids (ascending), paged with `offset` + `maxResults`. Runs are one-shot (no job store), so
the page comes from a fresh run with the same specification — deterministic for a quiescent
graph. `writeBack` is refused here (400); score algorithms are refused too.

## Budget contract and status codes

The budget is checked cooperatively every 4 096 vertices. On exhaustion, iterative
algorithms return the last completed iteration's values with `budgetExhausted: true` if at
least one full pass finished; otherwise — and always for the single-pass `DEGREE`, `WCC`
and `TRIANGLECOUNT`, where a partial pass is meaningless — the run is a 408.

| Code | When |
|---|---|
| 200 | Success — including `converged: false` (iteration cap) and `budgetExhausted: true` (partial but usable) |
| 400 | Out-of-range `maxResults`/`maxIterations`/`timeBudgetSeconds`/`epsilon`/`DampingFactor`, unknown `direction`, bad write-back key, `writeBack` on the partition endpoint |
| 404 | Unknown algorithm name, or a partition id the run did not produce |
| 408 | Budget exhausted with no usable result |
| 429 | All concurrent-run slots taken (default 1 — a whole-graph pass saturates cores; retry when the running computation finishes) |

Configuration (`Fallen8:Analytics`): `DefaultTimeBudgetSeconds` (30),
`MaxTimeBudgetSeconds` (300), `MaxConcurrentRuns` (1).

## Consistency and determinism

A run is a lock-free read concurrent with the single writer — there is no global snapshot.
Under concurrent mutation the result is a best-effort mixture of states; **it is exact only
for a quiescent graph**. On a quiescent graph, results are fully deterministic: WCC
component ids are the smallest member vertex id, label propagation uses synchronous rounds
with a smallest-label tie-break, all orderings have ascending-id tie-breaks, and write-back
applies values in ascending vertex-id order.

## Property write-back

`"writeBack": true` writes each in-scope vertex's value as a vertex property, applied on
the single writer thread in chunks of 50 000 vertices. Each chunk is atomic; the whole
write-back is not — a mid-way failure (500) leaves earlier chunks applied, and re-running
overwrites idempotently. Durability is **snapshot-only**: a save captures the properties,
but the WAL does not log them — after a WAL-only replay with no intervening save they are
gone (re-run to restore). See [save-games.md](save-games.md).

Convention property keys (overridable via `writeBackPropertyKey`):

| Algorithm | Property key | Value type |
|---|---|---|
| `PAGERANK` | `analytics.pagerank` | `Double` |
| `WCC` | `analytics.wcc` | `Int32` |
| `LABELPROPAGATION` | `analytics.community` | `Int32` |
| `DEGREE` | `analytics.degree.in` / `.out` / `.both` (per `direction`) | `UInt32` |
| `TRIANGLECOUNT` | `analytics.triangles` | `Int64` |

Third-party plugins get `analytics.<lowercased plugin name>` (`Double` for scores).

## Worked example

Run PageRank over `person` vertices with write-back, then read the property back.

```bash
curl -s -X POST http://localhost:8080/analytics/PAGERANK \
     -H "Content-Type: application/json" \
     -d '{ "vertexLabel": "person", "maxResults": 5,
           "parameters": { "DampingFactor": 0.85 }, "writeBack": true }'
# => { "algorithm": "PAGERANK", "converged": true, ..., "results": [ { "graphElementId": 7, "score": 0.0031 }, ... ],
#      "writeBack": { "propertyKey": "analytics.pagerank", "verticesWritten": 2500, "chunks": 1 } }

curl -s http://localhost:8080/vertex/7
# => "properties" now contains { "propertyId": "analytics.pagerank", "propertyValue": 0.0031 }
```

```powershell
$body = @{ vertexLabel = "person"; maxResults = 5
           parameters = @{ DampingFactor = 0.85 }; writeBack = $true } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri http://localhost:8080/analytics/PAGERANK `
                  -ContentType "application/json" -Body $body

(Invoke-RestMethod http://localhost:8080/vertex/7).properties |
    Where-Object { $_.propertyId -eq "analytics.pagerank" }
```

Paging a WCC partition from the run's `partitions` summaries:

```bash
curl -s -X POST http://localhost:8080/analytics/WCC/partition/0 \
     -H "Content-Type: application/json" -d '{ "offset": 0, "maxResults": 1000 }'
```

```powershell
Invoke-RestMethod -Method Post -Uri http://localhost:8080/analytics/WCC/partition/0 `
                  -ContentType "application/json" -Body '{ "offset": 0, "maxResults": 1000 }'
```

## See also

- [delegates.md](delegates.md) — why queries are C# delegates and analytics deliberately are not
- [plugins.md](plugins.md) — the plugin system that discovers analytics algorithms
- [graph-model.md](graph-model.md) — vertices, properties and transactions
- [path-finding.md](path-finding.md), [subgraphs.md](subgraphs.md) — the sibling algorithm families
- [save-games.md](save-games.md) — snapshots, WAL and why write-back is snapshot-durable only
- [observability.md](observability.md) — graph statistics beyond per-run aggregates
