# Graph Analytics — Specification

> **Status:** Implemented on branch `feature/graph-analytics` (branch-only workflow — no
> GitHub issue/PR); council gate pending. See [plan.md](./plan.md) for the phase record.

## 1. Overview & requirements

Fallen-8 has two algorithm families today, both plugin-discovered: **path finding**
(`IShortestPathAlgorithm` / `IPathTraverser`, `Algorithms/Path/`) answers "how do I get from A
to B", and **subgraph extraction** (`ISubGraphAlgorithm`, `Algorithms/SubGraph/`) answers "give
me the part of the graph matching this pattern". What is missing is the third classic family:
**whole-graph analytics** — "score or partition every vertex" (PageRank, connected components,
communities, centrality, triangles). Users currently have to export the graph and run these
elsewhere, which throws away the one thing Fallen-8 is good at: the whole graph is already in
RAM, in a flattened, cache-friendly adjacency representation (`EdgeAdjacency`, features
`adjacency-flattening` + `supernode-adjacency-build`) built for exactly these hot loops.

This feature adds a **graph analytics algorithm family** as a third plugin kind alongside the
two existing ones, plus a versioned REST controller:

1. **A new plugin contract** — `IGraphAnalyticsAlgorithm : IPlugin`, discovered via
   `PluginFactory`, cached via `PluginCache`, invoked through a `Fallen8.TryRunAnalytics(…)`
   facade mirroring `TryCalculateShortestPath(out result, "BLS", definition)`.
2. **Five built-in v1 algorithms:** PageRank, weakly connected components (WCC), label
   propagation community detection, degree centrality (in/out/both), triangle counting.
3. **Two result modes:** (a) bounded results over REST (element id → score / partition id,
   top-K + paging), and (b) opt-in **write-back** of the full per-vertex result as properties
   via the sanctioned plugin write path (`DelegateTransaction` + `IFallen8WriterContext`,
   feature `plugin-write-transactions`).
4. **Resource budgets** — max iterations, wall-clock budget, cooperative cancellation —
   consistent with the `dynamic-code-resource-limits` philosophy, and an honest consistency
   story for runs concurrent with writes.

No dynamic C# fragments are involved anywhere in v1 (see Non-goals) — analytics requests are
plain data, so the endpoints do **not** need the `EnableDynamicCodeExecution` gate.

## 2. Goals / non-goals

**Goals**

- `IGraphAnalyticsAlgorithm` (public, third-party-implementable, like `IShortestPathAlgorithm`
  and `ISubGraphAlgorithm`), with a typed `GraphAnalyticsDefinition` in +
  `GraphAnalyticsResult` out, `Try*(out result, …) : bool` per repo convention.
- The five v1 algorithms as built-in plugins in `fallen-8-core/Algorithms/Analytics/`, their
  hot loops running over the flattened adjacency (`VertexModel.GetRawOutEdges()` /
  `GetRawInEdges()` — the internal, allocation-free accessors the path/subgraph algorithms
  already use).
- Optional **scoping**: restrict a run to vertices with a given label and/or edges in a given
  edge-property-id group — data-only scoping, no compiled predicates.
- A new versioned `AnalyticsController` (`api/v{version}/…`, default `0.1`), synchronous
  execution with a budget (see §3.6 for why that is right-sized here).
- Write-back mode using **only** the sanctioned plugin write path (mode (a)
  `DelegateTransaction`), with a documented property-key convention (§3.5).
- Budgets and honest failure semantics: iteration cap, wall-clock budget, `CancellationToken`,
  and a concurrent-run cap; exhaustion maps to documented status codes.

**Non-goals** (each with its revisit trigger)

- **Betweenness and closeness centrality.** Exact Brandes betweenness is O(V·E) (closeness is
  the same order via all-sources BFS) — on a 1M-vertex/10M-edge in-memory graph that is
  ~10¹³ edge relaxations, far past any per-request budget, and approximate/sampled variants
  bring estimation-quality machinery this single-operator product has no demonstrated need
  for. *Revisit when a real use case needs path-based centrality and the graphs involved are
  small enough (or a sampling accuracy target is agreed) that the computation fits a budget.*
- **Predicate-scoped analytics** (C# filter fragments selecting the vertex/edge subset, like
  the path/subgraph APIs). v1 scoping is label + edge-property-id only, which keeps the
  analytics surface entirely outside the dynamic-code trust boundary. *Revisit when
  label/edge-property scoping proves insufficient for a concrete analytics use case; the
  `CodeGenerationHelper` → `Delegates.*` pattern is the established route.*
- **Async job/queue machinery** (submit → poll → fetch, job persistence, progress streaming).
  v1 is synchronous-with-budget (§3.6). *Revisit when a real workload needs runs longer than
  the operator is willing to hold a request open (default budget 30 s), or when overlapping
  scheduled runs become a routine need.*
- **Weighted analytics** (edge-weight-aware PageRank, weighted degree). v1 treats every edge
  as weight 1. *Revisit alongside the first user with weighted-graph analytics; the
  `weighted-shortest-paths` property-as-weight convention is the template.*
- **Incremental/streaming recomputation** on graph change (the subgraph feature's
  recalculation registry is deliberately not mirrored). Analytics runs are one-shot; re-run to
  refresh. *Revisit if periodically-refreshed scores become a workflow and re-running from
  scratch measurably hurts.*
- **WAL-logged write-back** (mode (b) plugin transactions). Write-back durability is
  snapshot-only, exactly the documented mode-(a) contract. *Revisit if/when
  `plugin-write-transactions` mode (b) lands; analytics write-back is a natural first
  customer since a (algorithm, definition) descriptor is trivially serialisable and replayable.*
- **Distributed / out-of-process execution.** Single process, single operator, graph fits in
  RAM — the product's stated reality. *Revisit never, short of the product changing shape.*

## 3. Design sketch

### 3.1 Contract

New types in `fallen-8-core/Algorithms/Analytics/` (`NoSQL.GraphDB.Core.Algorithms.Analytics`):

```csharp
public interface IGraphAnalyticsAlgorithm : IPlugin
{
    /// Runs the analytics computation over the graph the plugin was Initialize()d with.
    /// Returns false on invalid definition or a run that produced no usable result
    /// (e.g. wall-clock budget exhausted before one full pass) — the Try* convention.
    bool TryRunAnalytics(out GraphAnalyticsResult result, GraphAnalyticsDefinition definition);
}

public sealed class GraphAnalyticsDefinition
{
    // Scoping (all optional; null => whole graph)
    public String VertexLabel { get; set; }        // only vertices with this label participate
    public String EdgePropertyId { get; set; }     // only edges in this adjacency group are traversed
    public Direction Direction { get; set; }       // per-algorithm meaning; see §3.3

    // Budgets (§3.4)
    public Int32 MaxIterations { get; set; }       // iterative algorithms; per-algorithm default
    public Double Epsilon { get; set; }            // convergence threshold (PageRank; L1 delta)
    public TimeSpan TimeBudget { get; set; }       // wall-clock; default from AnalyticsOptions
    public CancellationToken CancellationToken { get; set; }

    // Algorithm-specific knobs, the plugin parameter convention (e.g. "DampingFactor" -> 0.85)
    public IDictionary<String, Object> Parameters { get; set; }
}

public sealed class GraphAnalyticsResult
{
    // Exactly one of the two vertex maps is populated, per algorithm (§3.3):
    public IReadOnlyDictionary<Int32, Double> VertexScores { get; }      // PageRank, degree, triangles
    public IReadOnlyDictionary<Int32, Int32>  VertexPartitions { get; }  // WCC, label propagation

    public IReadOnlyDictionary<String, Object> Statistics { get; }  // e.g. "TriangleCount", "ComponentCount"
    public Boolean Converged { get; }        // iterative algorithms; degree/WCC/triangles => true
    public Int32 IterationsRun { get; }
    public TimeSpan Elapsed { get; }
    public Boolean BudgetExhausted { get; }  // stopped by TimeBudget/cancellation, values are partial
}
```

Plugins are discovered by `PluginFactory.TryFindPlugin<IGraphAnalyticsAlgorithm>(out algo, name)`
(zero `PluginFactory` changes — the memoized per-category name map is generic already), receive
the engine via `IPlugin.Initialize(IFallen8, parameters)`, and are cached in a new
`PluginCache.Analytics` `IMemoryCache` alongside `ShortestPath` and `SubGraph` (same sliding
expiration/size options). A `Fallen8.TryRunAnalytics(out result, String algorithmName,
GraphAnalyticsDefinition definition)` facade mirrors `TryCalculateShortestPath`'s
resolve-initialize-cache-invoke flow.

### 3.2 Execution substrate

The built-in algorithms iterate the vertex snapshot from `GetAllVertices()` (array-backed
`IReadOnlyList<VertexModel>` since `scan-result-representation` — one cheap materialisation per
run) and walk neighbours through `VertexModel.GetRawOutEdges()` / `GetRawInEdges()`: a single
volatile read per vertex handing back the immutable `EdgeAdjacency`, whose enumerator yields
count-bounded `ArraySegment<EdgeModel>` groups allocation-free. That is the same substrate the
BLS/Dijkstra/subgraph hot loops use, and it is what `adjacency-flattening` +
`supernode-adjacency-build` were built for.

Per-run working state is **dense arrays**, not per-vertex dictionaries: build one
`Dictionary<Int32, Int32>` id→dense-index map from the snapshot (element ids are not assumed
contiguous — `Trim`/removals can leave gaps), then keep scores/partitions in `Double[]`/`Int32[]`
indexed densely. O(V) extra memory per run (~12–16 B/vertex), freed when the run ends.

Vertices whose `_removed` tombstone is set, and edges to removed endpoints, are skipped —
matching what every other read path does since `index-lifecycle`.

Third-party analytics plugins cannot see the internal raw accessors (the engine declares no
`InternalsVisibleTo`); they implement the same interface over the public read-only views
(`VertexModel.OutEdges`/`InEdges`, `TryGetOutEdge`) — correct, just paying the wrapper cost.
This is the identical situation third-party path algorithms are in today; documented, not
worked around.

### 3.3 The v1 algorithms

All plugin names follow the existing all-caps convention (`"BLS"`, `"DIJKSTRA"`). Every
algorithm skips removed elements, honours `VertexLabel`/`EdgePropertyId` scoping (a scoped run
computes over the induced subgraph: only in-scope vertices participate, only edges between two
in-scope vertices are traversed), and is deterministic for a quiescent graph.

| Algorithm | Plugin name | Output | Semantics decided up front |
|---|---|---|---|
| PageRank | `PAGERANK` | `VertexScores` (sums to 1 over in-scope vertices) | Damping factor `d` via `Parameters["DampingFactor"]` (default **0.85**); iterate until L1 delta < `Epsilon` (default **1e-6**) or `MaxIterations` (default **100**, ceiling 10 000). Directed over out-edges by default (`Direction` selects out/in/undirected interpretation). **Dangling vertices** (no in-scope out-edges) redistribute their rank uniformly. **Parallel edges** count multiply (each edge carries rank); **self-loops** feed rank back to their vertex. Iteration-cap stop without convergence is a normal outcome: `Converged=false`, scores returned. |
| Weakly connected components | `WCC` | `VertexPartitions`; `Statistics["ComponentCount"]` | Union-find (iterative, path-halving — no recursion) over all in-scope edges regardless of direction. Deterministic component id = **smallest vertex id in the component**. Not iterative: always `Converged=true`. |
| Label propagation | `LABELPROPAGATION` | `VertexPartitions`; `Statistics["CommunityCount"]` | Every vertex seeded with its own id; synchronous rounds in ascending-id order; each vertex adopts the **most frequent neighbour label, smallest label winning ties** (deterministic — LP is normally order/tie sensitive; pinning the rule makes results hand-computable and reproducible). Stop when a round changes nothing or `MaxIterations` (default **20**). Neighbours over both directions unless `Direction` narrows. |
| Degree centrality | `DEGREE` | `VertexScores`; `Statistics["Min"/"Max"/"Mean"]` | `Direction` selects in / out / both (both = in+out, default). Counts parallel edges and self-loops as they exist in the adjacency (a self-loop contributes 1 to out and 1 to in). Single pass, no iteration knobs. |
| Triangle counting | `TRIANGLECOUNT` | `VertexScores` (per-vertex triangle count); `Statistics["TriangleCount"]` (global; = Σ/3) | **Undirected simple-graph interpretation**: adjacency is deduplicated per vertex pair and self-loops ignored before counting (the standard definition; documented). Neighbour-intersection over sorted dense neighbour lists, O(Σ d²) worst case bounded by the time budget. |

**Betweenness/closeness stay out** — the honest call after looking at the substrate: nothing in
the codebase changes the O(V·E) arithmetic, and none of the five in-set algorithms needs the
all-pairs machinery those two would drag in. Parked in §2 with the concrete trigger.

### 3.4 Resource budgets & consistency

Budget philosophy follows `dynamic-code-resource-limits`: bounded by default, configurable,
generous for legitimate use. One important difference makes this feature *simpler*: analytics
runs execute **no user-supplied code** — the loops are entirely engine-owned — so cooperative
cancellation is genuinely sufficient. There is no hostile-delegate residual, no task-abandon
backstop, no leaked threads (the honest gap that deferred R1 there does not exist here).

- **`MaxIterations`** — per-algorithm defaults (§3.3), hard ceiling 10 000. Reaching the cap on
  an iterative algorithm is a *normal* result (`Converged=false`, values usable).
- **`TimeBudget`** — wall-clock, default **30 s**, operator-configurable ceiling via an
  `AnalyticsOptions` section (mirroring the `SubGraphQuota` options shape). The engine checks
  the deadline/token cooperatively every N vertices (N = 4 096) inside every pass. Exhaustion
  mid-run: iterative algorithms return the last **completed** iteration's values with
  `BudgetExhausted=true` if at least one full pass finished, else `TryRunAnalytics` returns
  `false`; single-pass algorithms (WCC/degree/triangles) return `false` (partial single-pass
  values are meaningless).
- **Concurrent runs** — `AnalyticsOptions.MaxConcurrentRuns` (default **1**): a run holds a
  slot; the controller returns **429** when none is free. A whole-graph pass saturates cores
  and memory bandwidth; a single-operator instance gains nothing from overlapping runs.

**Consistency (honest story).** Analytics runs are lock-free reads concurrent with the single
writer, and there is **no global snapshot**: `GetAllVertices()` is a point-in-time snapshot of
the element *list*, and each vertex's adjacency is a per-vertex copy-on-write snapshot captured
when that vertex is visited. Concretely, during a run concurrent with writes: vertices created
after the run started are absent; an edge created mid-run may be seen from one endpoint's
adjacency but not the other's (each endpoint is read at a different moment); elements removed
mid-run are skipped once their tombstone is visible. Every individual read is torn-free and
self-consistent (the volatile-publish discipline), so the run never crashes or sees corrupt
adjacency — but the **result is only exact for the graph as of the run if the graph was
quiescent**; under concurrent mutation it is a best-effort mixture of states, which for
iterative scores (PageRank, LP) is usually indistinguishable from running a moment later. This
is documented on the API, not hidden. Runs that need exactness run against a quiet graph — the
single-operator reality makes that trivial to arrange.

### 3.5 Result modes

**Mode 1 — REST return (all five algorithms).** The full per-vertex map stays in the engine
result; the REST layer returns a **bounded** projection:

- Score algorithms (`PAGERANK`, `DEGREE`, `TRIANGLECOUNT`): top-K vertices by score,
  descending (id ascending as tie-break), `maxResults` default **100**, ceiling **10 000**
  per request, plus the `Statistics` map and run metadata.
- Partition algorithms (`WCC`, `LABELPROPAGATION`): partition summaries (partition id → size,
  largest first, same `maxResults` bound) and a separate membership page per partition id
  (`offset` + `maxResults`, same ceiling).

The ceiling is deliberate: a 10 000-row JSON body is ~hundreds of KB — fine; a 2.5M-row body is
a multi-hundred-MB allocation on the request path, exactly the class of problem
`scan-result-representation` and `maxElements` bounds exist to prevent. **The full result set's
delivery vehicle is write-back**, not pagination-through-millions.

**Mode 2 — property write-back (all five algorithms; opt-in per request).** The run writes each
in-scope vertex's value as a property, through the sanctioned plugin write path and nothing
else: `DelegateTransaction` bodies calling `IFallen8WriterContext.SetProperty` on the single
writer thread. Property-key convention:

| Algorithm | Property key | Value type |
|---|---|---|
| `PAGERANK` | `analytics.pagerank` | `Double` |
| `WCC` | `analytics.wcc` | `Int32` (component id) |
| `LABELPROPAGATION` | `analytics.community` | `Int32` |
| `DEGREE` | `analytics.degree.in` / `.out` / `.both` (per `Direction`) | `UInt32` |
| `TRIANGLECOUNT` | `analytics.triangles` | `Int64` |

A request may override the key (`writeBackPropertyKey`, non-empty, ≤ 256 chars); the
`analytics.` prefix is the documented namespace and re-runs **overwrite** (idempotent).
Write-back is **chunked**: one `DelegateTransaction` per 50 000 vertices, each chunk atomic
(the undo journal covers `SetProperty`), the whole write-back **not** atomic across chunks — a
mid-way failure leaves earlier chunks applied, remedied by re-running (idempotent overwrite).
Chunking is the deliberate trade against the documented `plugin-write-transactions` risk of a
single multi-second delegate body stalling every other write. Durability is **mode (a)**:
snapshot-durable, not WAL-logged — a WAL-only replay with no intervening save loses the
written properties (pinned by test, stated in the API docs).

### 3.6 REST surface

New `AnalyticsController` in `fallen-8-core-apiApp`, repo conventions throughout
(`[Route("api/v{version:apiVersion}/[controller]")]`, `[ApiVersion("0.1")]`,
`[ProducesResponseType]`/`[Consumes]`/`[Produces]`, XML `<summary>`/`<remarks>`):

| Route | Verb | Behaviour |
|---|---|---|
| `/analytics/algorithms` | GET | Discovered `IGraphAnalyticsAlgorithm` plugins with descriptions (`PluginFactory.TryGetAvailablePluginsWithDescriptions`) — mirrors how path plugins are enumerable. |
| `/analytics/{algorithmName}` | POST | Body: `AnalyticsSpecification` (scoping, budgets, algorithm parameters, `writeBack` flag + key override, `maxResults`). Runs synchronously under the budget; returns `AnalyticsResultREST`. |
| `/analytics/{algorithmName}/partition/{partitionId}` | POST | Membership page for one partition of a fresh run (same body + `offset`) — partition algorithms only. |

Status mapping (aligned with `api-error-contract` problem+json and the
`dynamic-code-resource-limits` precedent): **200** result (including `converged=false` /
`budgetExhausted=true` partials that carry usable values); **400** unknown parameter values,
out-of-ceiling `maxIterations`/`maxResults`, bad property key; **404** unknown algorithm name;
**408** wall-clock budget exhausted with no usable result; **429** concurrent-run slots
exhausted; **500** never for expected cases (`Try*` pattern end to end).

**Why synchronous-with-budget is right-sized:** the graphs this product holds fit in one
process's RAM; a PageRank pass touches each edge once per iteration, so even ~10M edges ×
100 iterations is tens of seconds on one core — inside a configurable request budget. A
job/queue layer would add persistence, ids, polling, and cleanup for a single operator who can
simply re-run with a bigger budget. Parked in §2 with a named trigger. The write-back flag does
not change this: chunked write-back of 2.5M properties is seconds on the writer thread.

The pinned OpenAPI snapshot (`features/done/web-ui/openapi-v0.1.json`) is regenerated when the
controller lands — it is the REST-contract source of truth the web-ui and mcp-server contract
tests read; the new endpoints are additive.

### 3.7 Tests (MSTest, `fallen-8-unittest`)

Hand-computable fixtures, pinned expected values, arrange/act/assert,
`TestLoggerFactory.Create()`:

- **PageRank:** a 4-vertex directed graph with published rank values (assert within 1e-4);
  the two-vertex cycle (0.5/0.5); a dangling-vertex graph (ranks still sum to 1); convergence
  flag true under generous iterations, false at `MaxIterations=1`; damping 0 ⇒ uniform 1/V.
- **WCC:** two disjoint chains ⇒ 2 components, ids = smallest member; direction ignored;
  singleton vertex is its own component.
- **Label propagation:** two cliques joined by one bridge edge ⇒ 2 communities under the
  pinned tie-break; a clique converges in one round; determinism pinned by running twice.
- **Degree:** a star (hub in/out/both counts vs leaves); parallel edges counted; self-loop
  contributes to both in and out.
- **Triangles:** K4 ⇒ 4 triangles (per-vertex 3); a 4-cycle ⇒ 0; parallel edges deduplicated
  (a doubled triangle edge still counts 1 triangle); self-loops ignored.
- **Cross-cutting edge cases:** empty graph (`Try*` semantics pinned per algorithm); label
  scoping (out-of-scope neighbours invisible — induced-subgraph semantics); edge-property-id
  scoping; removed vertices/edges skipped; budget exhaustion (a `TimeBudget` of ~0 on a
  seeded graph ⇒ 408-path / `false`, suite-safe with its own deadline); cancellation token
  honoured.
- **Write-back:** properties appear with the convention keys and correct types; re-run
  overwrites; a chunked run over > 1 chunk applies all chunks; write-back survives
  `Save`→`Load` but is **absent after a WAL-only replay** (the mode-(a) pin, mirroring
  `PluginWriteTransactionsTest`); a mid-chunk induced failure leaves earlier chunks applied
  and later ones absent (the documented non-atomicity).
- **REST:** unknown algorithm ⇒ 404; bad parameter ⇒ 400; top-K ordering + tie-break; result
  ceiling enforced; partition paging; 429 when a slot is held; OpenAPI doc contains the new
  operations.

## 4. Acceptance criteria

- All five algorithms return pinned, hand-verified results on the fixture graphs, over the
  flattened adjacency, honouring label/edge-property scoping and skipping removed elements.
- `PluginFactory` discovers a third-party `IGraphAnalyticsAlgorithm` from an assimilated
  assembly with **zero** factory changes; `PluginCache.Analytics` caches instances like the
  other two families.
- Budgets behave as specified: iteration cap ⇒ usable partial (`Converged=false`); wall-clock
  exhaustion ⇒ partial-with-flag or `false`/408 per §3.4; a second concurrent run ⇒ 429.
- Write-back lands through `DelegateTransaction` only (no other mutation path), chunk-atomic,
  idempotent on re-run, snapshot-durable and provably not WAL-replayable (mode (a) pin).
- REST responses are bounded by the documented ceilings; the full suite is green; the OpenAPI
  snapshot is regenerated and the web-ui contract test still passes.
- No new dynamic-code surface: the analytics endpoints work with
  `EnableDynamicCodeExecution=false`.

## 5. Risks

- **Result-map memory for large graphs.** A run holds O(V) working arrays plus the result map
  (~40–60 B/vertex with the dictionary); ~150 MB transient for 2.5M vertices. Acceptable for
  an in-memory database whose graph already dwarfs it; bounded to one run by
  `MaxConcurrentRuns=1`. Mitigation if it ever bites: return dense arrays keyed by the
  snapshot order instead of a dictionary.
- **Fuzzy results under concurrent writes** (§3.4) could surprise users expecting snapshot
  isolation. Mitigation: documented prominently on the endpoints and in the result metadata;
  exactness = quiescent graph, trivially arranged by a single operator.
- **Label propagation instability.** LP famously oscillates under synchronous update.
  Mitigation: the pinned ascending-id order + smallest-label tie-break + `MaxIterations`
  ceiling; the tests pin determinism. If oscillation on real graphs is observed, switch to
  asynchronous update *as a documented behaviour change*, not silently.
- **Writer stalls from write-back.** A delegate body blocks the single writer for its
  duration (documented `plugin-write-transactions` risk). Mitigation: 50 000-vertex chunks
  keep each stall in the tens-of-milliseconds range; the non-atomicity trade is documented
  and idempotent-by-design.
- **Triangle counting blow-up on supernodes** (O(d²) intersection at a hub). Mitigation: the
  wall-clock budget bounds it cooperatively; the sorted-intersection implementation keeps the
  constant small. No sampling machinery in v1.
- **Third-party plugins on the slow public views** could be mistaken for engine slowness.
  Mitigation: documented; if a real third-party analytics ecosystem appears, a public
  read-only fast adjacency enumerator is the follow-on (same trigger as widening
  `traversal-allocations`' span accessor).

## 6. Keep (do not regress)

- **The plugin architecture stays open and uniform:** `IPlugin` untouched; discovery through
  `PluginFactory`'s existing memoized machinery; the new family mirrors, not forks, the
  path/subgraph registration + caching pattern.
- **Single-writer / lock-free reads:** analytics never mutates outside `DelegateTransaction`;
  the copy-on-write adjacency discipline and `EdgeAdjacency`'s count-bounded enumerator
  contract are consumed as-is, never bypassed.
- **`plugin-write-transactions` boundaries:** `ATransaction` stays closed; write-back uses the
  public context surface only; the mode-(a) durability contract is restated, not weakened.
- **The security posture:** no new `Type.GetType` on user strings (scoping values are plain
  strings compared against labels/keys); no dynamic code; endpoints usable with the dynamic-
  code kill switch engaged; body-size/rate limits from `api-security-boundary` apply as to
  any controller.
- **The pinned OpenAPI snapshot workflow:** regenerate, don't fork; additive endpoints only.
- **The repo test bar:** every behaviour above lands with MSTest coverage in
  `fallen-8-unittest`, edge cases included, suite-safe deadlines on any budget test.
