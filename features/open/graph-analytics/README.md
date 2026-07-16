# Graph Analytics — Usage

Whole-graph analytics as a third plugin-discovered algorithm family
(`fallen-8-core/Algorithms/Analytics/`), next to path finding and subgraph extraction:
score or partition every in-scope vertex, over the flattened in-memory adjacency,
synchronously under a wall-clock budget. Requests are plain data — no dynamic code, so all
endpoints work with `EnableDynamicCodeExecution=false`.

## The algorithms

```bash
curl -sf http://localhost:5000/analytics/algorithms
# {"DEGREE":"Degree centrality (in/out/both) per vertex - ...","PAGERANK":"...","WCC":"...", ...}
```

| Plugin | Output | Semantics (pinned by tests) |
|---|---|---|
| `PAGERANK` | scores (sum to 1) | Damping `parameters.DampingFactor` (default 0.85), L1-delta convergence (`epsilon`, default 1e-6), `maxIterations` default 100. Directed over out-edges by default; dangling vertices redistribute uniformly; parallel edges carry rank multiply; self-loops feed back. |
| `WCC` | partitions | Union-find over all in-scope edges regardless of direction. Component id = smallest member vertex id. `statistics.ComponentCount`. |
| `LABELPROPAGATION` | partitions | Synchronous rounds; most frequent neighbour label, smallest label wins ties (deterministic); stop on no-change or `maxIterations` (default 20). `statistics.CommunityCount`. |
| `DEGREE` | scores | `direction` in/out/both (default both); parallel edges and self-loops count as they exist. `statistics.Min/Max/Mean`. |
| `TRIANGLECOUNT` | scores (per-vertex count) | Undirected simple-graph interpretation (parallel edges deduplicated, self-loops ignored). `statistics.TriangleCount` = Σ/3. |

## Running

```bash
curl -sf -X POST http://localhost:5000/analytics/PAGERANK \
     -H "Content-Type: application/json" \
     -d '{ "vertexLabel": "person", "maxResults": 10,
           "parameters": { "DampingFactor": 0.85 } }'
```

Response (score algorithms return top-K best-first, ascending-id tie-break):

```json
{
  "algorithm": "PAGERANK",
  "converged": true,
  "iterationsRun": 23,
  "elapsedMs": 184.2,
  "budgetExhausted": false,
  "vertexCount": 2500000,
  "statistics": {},
  "results": [ { "graphElementId": 7, "score": 0.0031 }, ... ]
}
```

Partition algorithms return `partitions` (partition id → size, largest first) instead;
one partition's members come from the paging endpoint, which re-runs the same specification
(runs are one-shot, no job store — deterministic for a quiescent graph). `writeBack` is
refused on this endpoint (400) — write back from the run endpoint instead:

```bash
curl -sf -X POST http://localhost:5000/analytics/WCC/partition/7 \
     -H "Content-Type: application/json" \
     -d '{ "offset": 0, "maxResults": 1000 }'
```

**Scoping** (`vertexLabel`, `edgePropertyId`) computes over the induced subgraph: only
in-scope vertices participate, only edges between two in-scope vertices are traversed.

**Responses are bounded**: `maxResults` default 100, ceiling 10 000. The full per-vertex
result's delivery vehicle is write-back, not pagination through millions of rows.

## Budgets & status codes

- `maxIterations` (ceiling 10 000): reaching the cap is a **normal 200** —
  `converged: false`, values usable.
- `timeBudgetSeconds` (default 30, ceiling 300; `Fallen8:Analytics`): checked cooperatively
  every 4 096 vertices. Exhaustion after ≥ 1 completed pass returns that pass's values with
  `budgetExhausted: true`; exhaustion before one full pass is a **408** (single-pass
  algorithms — DEGREE/WCC/TRIANGLECOUNT — always 408 on exhaustion: a partial single pass
  is meaningless).
- One run at a time (`MaxConcurrentRuns`, default 1): a second concurrent request gets
  **429** — a whole-graph pass saturates cores and memory bandwidth, overlapping runs gain
  nothing.
- **400** for out-of-range knobs, unknown `direction`, bad write-back key; **404** for an
  unknown algorithm name or partition id.

## Consistency (honest)

An analytics run is a lock-free read concurrent with the single writer — there is **no
global snapshot**. Every individual read is torn-free and self-consistent, but under
concurrent mutation the result is a best-effort mixture of states: vertices created after
the run started are absent, an edge created mid-run may be seen from one endpoint but not
the other, removals are skipped once their tombstone is visible. **The result is exact only
for a quiescent graph** — trivially arranged by a single operator. For iterative scores
(PageRank, label propagation) the fuzziness is usually indistinguishable from running a
moment later.

## Property write-back

`"writeBack": true` writes each in-scope vertex's value as a property — through the
sanctioned plugin write path (`DelegateTransaction` + `IFallen8WriterContext.SetProperty`
on the single writer thread) and nothing else:

| Algorithm | Property key | Value type |
|---|---|---|
| `PAGERANK` | `analytics.pagerank` | `Double` |
| `WCC` | `analytics.wcc` | `Int32` |
| `LABELPROPAGATION` | `analytics.community` | `Int32` |
| `DEGREE` | `analytics.degree.in` / `.out` / `.both` | `UInt32` |
| `TRIANGLECOUNT` | `analytics.triangles` | `Int64` |

`writeBackPropertyKey` overrides the key (non-empty, ≤ 256 chars); re-runs **overwrite**
(idempotent). Write-back is **chunked** — one `DelegateTransaction` per 50 000 vertices;
each chunk is atomic, the whole write-back is not: a mid-way failure leaves earlier chunks
applied, remedied by re-running.

**Durability is mode (a), snapshot-only** (the `plugin-write-transactions` contract):
a save-game captures the written properties, but the WAL does not log them — after a
WAL-only replay with no intervening save they are **gone**. Re-run the write-back to
restore them (pinned by test).

## Parked (non-goals with revisit triggers)

| Parked | Revisit when |
|---|---|
| Betweenness/closeness centrality | a real use case needs path-based centrality at a size (or agreed sampling target) that fits a budget — exact Brandes is O(V·E) |
| Predicate-scoped analytics (C# fragments) | label/edge-property scoping proves insufficient for a concrete case |
| Async job/queue machinery | a real workload outgrows the configurable request budget |
| Weighted analytics | the first weighted-graph user (follow the `weighted-shortest-paths` property-as-weight convention) |
| Incremental recomputation on change | periodically refreshed scores become a workflow and full re-runs measurably hurt |
| WAL-logged write-back | `plugin-write-transactions` mode (b) lands — analytics is its natural first customer |
