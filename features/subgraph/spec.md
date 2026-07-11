# Subgraph Feature — Specification

> **Status:** Retrofitted from the implementation on the `subgraph` branch (as of 2026-07).
> This document describes the intended behaviour of the subgraph feature. Where the
> current implementation deviates, it is called out explicitly under **Known
> deviations**. The companion [plan.md](./plan.md) tracks the work to close the gaps.

## 1. Overview

The subgraph feature extracts a **pattern-matched subset** of a Fallen-8 graph into a
new, standalone `Fallen8` instance. Given a source graph and a `SubGraphDefinition`,
the algorithm:

1. Copies the vertices that pass an optional vertex pre-filter into a fresh graph.
2. Copies the edges that pass an optional edge pre-filter, provided both endpoints were
   copied in step 1.
3. If an ordered list of patterns is supplied, finds every path in the copied graph that
   matches the pattern sequence, then prunes every vertex and edge that is not part of a
   matching path.

The result is returned as a `SubGraphResult` — a self-contained graph plus the metadata
required to recalculate it later (source graph reference, algorithm plugin name,
algorithm parameters, and the original definition).

A subgraph is itself a `Fallen8` instance, so subgraphs can be built from other
subgraphs. The `SubGraphFactory` tracks these dependencies and can recalculate a whole
dependency tree when a source graph changes.

## 2. Goals and non-goals

### Goals
- Deterministic, pattern-driven extraction of a subgraph from any `IFallen8` source.
- Filter graph elements by label, arbitrary property predicates, edge direction, and
  edge property id.
- Support fixed single-hop edges and variable-length paths (`MinLength`..`MaxLength`).
- Preserve vertex/edge properties, labels, creation dates, and edge property ids in the
  extracted subgraph.
- Leave the source graph completely unmodified (read-only with respect to the source).
- Register subgraphs by name and id, and support recalculation (single, all, and nested).
- Expose the feature over the REST API, consistent with the existing path-finding API.
- Round-trip subgraph definitions through persistence so registered subgraphs survive a
  save/load cycle.

### Non-goals
- Full graph query language (Cypher/Gremlin). Patterns are a linear vertex/edge sequence,
  not arbitrary graph patterns with branching or named-variable joins.
- Incremental / streaming recalculation. Recalculation re-runs the algorithm from scratch.
- Cross-process or distributed subgraph computation.

## 3. Domain model

All types live in `NoSQL.GraphDB.Core.Algorithms.SubGraph` unless noted.

### 3.1 Patterns

```
APattern (abstract)                         // Type: PatternType, PatternName: string
└── GraphElementPattern (abstract)          // GraphElement: Delegates.GraphElementFilter
    ├── VertexPattern                        // Vertex: Delegates.VertexFilter
    └── EdgePattern                          // Direction, EdgeProperty, Edge: Delegates.EdgeFilter
        └── VariableLengthEdgePattern        // MinLength, MaxLength (ushort)
```

- **`PatternType`** — `Vertex | Edge | VariableLengthEdge`. Exposed as a readonly
  `Type` property for allocation-free dispatch.
- **`GraphElementFilter`** applies to any element (vertex or edge); **`VertexFilter`** /
  **`EdgeFilter`** are the type-specific refinements; **`EdgePropertyFilter`** filters by
  edge property id (string) before the edge itself is inspected.
- **`Direction`** (`NoSQL.GraphDB.Core.Model`) — `OutgoingEdge | IncomingEdge |
  UndirectedEdge`.

### 3.2 Definition and result

- **`SubGraphDefinition`**
  - `Name: string` — unique registration key.
  - `AdditionalInformation: Dictionary<string,string>` — arbitrary metadata.
  - `VertexFilter: GraphElementPattern` — phase-1 pre-filter (null ⇒ copy all vertices).
  - `EdgeFilter: GraphElementPattern` — phase-2 pre-filter (null ⇒ copy all valid edges).
  - `Pattern: List<APattern>` — ordered pattern sequence (null/empty ⇒ no pruning).
- **`SubGraphResult`**
  - `SubGraph: IFallen8` — the extracted graph.
  - `SourceFallen8Id: Guid`, `SourceFallen8: IFallen8` — provenance for recalculation.
  - `AlgorithmPluginName: string`, `AlgorithmParameters: IDictionary<string,object>`.
  - `Definitions: SubGraphDefinition` — the definition used.

### 3.3 Algorithm plugin

- **`ISubGraphAlgorithm : IPlugin`** — `bool TryCreateSubgraph(out SubGraphResult, SubGraphDefinition)`.
- **`BreathFirstSearchSubgraphAlgorithm`** — the default implementation, plugin name
  `"Breadth First Search Subgraph Algorithm"`, registered type name
  `"BreathFirstSearchSubgraphAlgorithm"`.

### 3.4 Factory

`NoSQL.GraphDB.Core.SubGraph.SubGraphFactory` (exposed as `Fallen8.SubGraphFactory`):

- `TryCreateSubGraph(out result, name, definition, algorithmTypeName?, parameter?)`
- `TryCreateSubGraph<T>(out result, name, definition, parameter?)` — typed, no reflection.
- `TryGetSubGraph(out result, name | Guid)`, `TryRegisterSubGraph`, `TryDeregisterSubGraph`.
- `TryRecalculateSubGraph(name)`, `RecalculateAllSubGraphs()`, `CanRecalculateSubGraph(name)`.
- `GetAllSubGraphNames()`, `GetAvailableSubGraphPlugins()`, `DeleteAllSubGraphs()`.
- Dependency tracking so `RecalculateAllSubGraphs` recurses through subgraphs-of-subgraphs.

## 4. Behavioural semantics

### 4.1 Return-value contract for `TryCreateSubgraph`
- `definition == null` ⇒ returns `false`, `result == null`.
- Pre-filters produce zero vertices **and** a non-empty pattern list ⇒ `false`, `result == null`.
- Pre-filters produce zero vertices **and** no pattern list ⇒ `true` with an empty subgraph.
- Otherwise ⇒ `true` with the populated/pruned subgraph.

### 4.2 Pattern evaluation
- The pattern list must alternate vertex ↔ edge, starting with a vertex pattern (or, at
  level 0 only, an edge pattern that seeds paths from both endpoints). This is enforced by
  `ValidatePattern`.
- Path finding is breadth-first: level 0 seeds initial paths; each subsequent pattern
  extends or validates the working set. Paths that stop matching are marked invalid and
  dropped between levels.
- **Cycle prevention:** a path tracks the set of graph-element ids it already contains;
  re-adding an id invalidates the path. Each path must therefore carry its **own** element
  set (see Known deviation KD-1).
- **Pruning:** after path finding, the union of all element ids across valid paths is the
  keep-set; every other element in the copied graph is removed, followed by a trim.

### 4.3 Direction semantics
- `OutgoingEdge` — traverse source→target along outgoing edges.
- `IncomingEdge` — traverse target→source along incoming edges.
- `UndirectedEdge` — traverse both; level-0 seeding produces a path in each direction.

### 4.4 Variable-length paths
- `MinLength` mandatory hops are applied first; then hops up to `MaxLength` each contribute
  their intermediate paths to the valid set.
- The terminal `VertexPattern` (the pattern following the variable-length edge) constrains
  the path endpoint.

## 5. REST API (target)

The subgraph API mirrors the existing path-finding API: filter/predicate delegates are
supplied as C# code fragments (strings prefixed with `return`) and compiled at runtime via
Roslyn (`CodeGenerationHelper`). Routes are versioned under `api/v{version}` and live on a
dedicated `SubGraphController`.

| Method & route | Purpose | Success |
|---|---|---|
| `PUT /subgraph` | Create + register a subgraph from a `SubGraphSpecification` | `201 Created` (name) |
| `GET /subgraph` | List registered subgraph names | `200 OK` |
| `GET /subgraph/{name}` | Get a subgraph's summary (counts + metadata) | `200 OK` / `404` |
| `GET /subgraph/{name}/graph` | Get the extracted subgraph's vertices/edges | `200 OK` / `404` |
| `POST /subgraph/{name}/recalculate` | Recalculate against the current source | `200 OK` / `404` |
| `DELETE /subgraph/{name}` | Deregister a subgraph | `204 No Content` / `404` |

`SubGraphSpecification` (REST) mirrors `SubGraphDefinition` but expresses each filter as a
code fragment:

```jsonc
{
  "name": "friends-of-alice",
  "vertexFilter": "return (ge) => ge.Label == \"person\";",
  "edgeFilter":   "return (ge) => ge.Label == \"knows\";",
  "patterns": [
    { "type": "Vertex", "patternName": "start", "graphElementFilter": "return (ge) => ge.Label == \"person\";" },
    { "type": "Edge",   "patternName": "rel", "direction": "OutgoingEdge", "edgePropertyFilter": "return (p) => p == \"knows\";" },
    { "type": "Vertex", "patternName": "end", "graphElementFilter": "return (ge) => ge.Label == \"person\";" }
  ]
}
```

Compilation errors in a code fragment ⇒ `400 Bad Request` with the compiler diagnostics.

## 6. Persistence (target)

- Registered subgraphs are persisted as **definitions plus metadata**, not as materialised
  graphs. On load, each definition is recalculated against its restored source graph.
- Because filter/predicate delegates cannot be binary-serialised directly, they are
  persisted via `NoSQL.GraphDB.Core.Serializer.DelegateJson`, which encodes a delegate as
  its declaring type + method + parameter types (+ optional target factory).
- Persistence order guarantees the source graph is loaded before its dependent subgraphs
  are recalculated; nested subgraphs use the existing recursive recalculation.

## 7. Error handling
- The factory never throws to the caller; failures are logged and surfaced as `false`.
- The REST layer returns `400` for invalid specifications/compile errors, `404` for
  unknown subgraph names, and `2xx` on success.
- Subgraph creation must never mutate the source graph.

## 8. Testing requirements
- Algorithm: null/empty definitions, single vertex, vertex-edge-vertex, label/property/edge
  filters, incoming/undirected traversal, variable-length ranges, property preservation,
  **branching graphs** (diamonds / fan-out — see KD-1), read-only source invariant.
- Factory: create, recalculate (single/all/nested), can-recalculate, name listing.
- REST: create/list/get/recalculate/delete happy paths, compile-error handling, 404s.
- Persistence: create → save → load → assert equivalent subgraph after recalculation.
- Serializer: `DelegateJson` round-trips for each `Delegates.*` filter type.

## 9. Resolved deviations and current limitations

All deviations from the original retrofit, plus the issues surfaced by the
principal-architect review, and their disposition. See [plan.md](./plan.md) for the review
detail.

### Resolved
- **KD-1 — Shared path element set.** `PathInfo`'s copy constructor deep-copies the element
  set (fixed; branching regression tests).
- **KD-2 — No REST surface.** `SubGraphController` delivers the full CRUD + recalculate API.
- **KD-3 — No persistence.** Subgraphs persist as recipes and rehydrate on load.
- **KD-5 — Loose pattern validation.** Sequences ending in an edge (and a leading
  variable-length edge) are rejected.
- **Variable-length range dropped short paths (critical, review).** Fixed — each matched
  length is retained independently.
- **Factory selected the algorithm by the wrong name.** Fixed via a shared plugin-name
  constant.
- **Cached algorithm ignored the requested source.** Fixed — re-bind on cache hit;
  recalculation is sequential.
- **REST resource/robustness** — provider caching, `maxLength` cap, strict direction
  parsing, null-result-on-failed-registration, exception guard.

### Superseded
- **KD-4 — `DelegateJson` integration.** The review established `DelegateJson` cannot
  round-trip the feature's lambda/closure/compiled filter delegates. Persistence uses a
  **recipe** (spec text recompiled on load) instead. `DelegateJson` would only fit a future
  named-filter-registry model.

### Current limitations (documented)
- **KD-6 — Variable-length intermediate vertices unconstrained.** By design: only the
  terminal vertex pattern constrains a variable-length match; documented on
  `VariableLengthEdgePattern` and pinned by a test.
- ~~**Nested subgraph recalculation/persistence is not wired**~~ — resolved by the
  [nested-subgraph-recalculation](../nested-subgraph-recalculation/spec.md) feature:
  subgraphs sourced from other subgraphs now recalculate in dependency order and persist.
- **Runtime-compiled filter assemblies are not collectible** (same as the path API); the
  provider cache bounds repeated identical filter sets.
- **No ceiling on subgraph count / materialized size**; add quotas before exposing creation
  publicly.
