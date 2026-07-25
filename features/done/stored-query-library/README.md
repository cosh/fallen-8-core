# Stored Query Library — Usage

Named, validated, **pre-compiled** query definitions: register once, invoke by name from the
existing path/subgraph endpoints. Companion docs: [spec.md](./spec.md) (contract) and
[plan.md](./plan.md) (phases).

## The operating model

Register a vetted set of queries once, then reference them by name from the path/subgraph
endpoints. The payoff is **reuse and curation**, not a security lockdown:

1. **Compile once, invoke many** — each fragment is validated and compiled a single time at
   registration; every invocation reuses the pinned artifact, so a hot query pays no
   per-request Roslyn cost and callers send only a name plus bounds.
2. **A curated catalog** — an operator registers named queries that agents/clients reference
   instead of re-sending raw C#; `GET /storedquery` lists them for discovery, and the stored
   specification JSON (`GET /storedquery/{name}`) is your migration path between instances.

> **Update — dynamic code is always on.** An earlier revision let an operator lock the engine
> down to stored-queries-only by turning `EnableDynamicCodeExecution` off. That flag has been
> **removed**: inline C# fragments are always accepted (auth permitting), so the library is no
> longer a way to shrink the code surface. It remains a reuse/curation convenience.

> **Honesty note (as everywhere in this repo):** an invoked stored query still runs
> in-process with **full trust**. The library is not a sandbox. Registration carries the same
> authentication as the inline code endpoints (the API key when one is configured);
> recompilation at load/WAL-replay needs no flag — the engine simply rehydrates definitions the
> operator already registered.

## Registering

A `Path` query stores the `filter`/`cost` blocks of a path specification; the numeric bounds
(`maxDepth`, `maxResults`, `maxPathWeight`) and algorithm name stay per-request:

```jsonc
POST /storedquery
{
  "name": "adults-shortest",                    // ^[A-Za-z0-9_-]{1,128}$, case-sensitive
  "kind": "Path",
  "description": "age>30 vertices, weight-by-distance",
  "path": {
    "filter": { "vertexFilter": "return (v) => v.TryGetProperty(out int age, \"age\") && age > 30;" },
    "cost":   { "edgeCost": "return (e) => 1.0;" }
  }
}
```

A `SubGraph` query stores a pattern template; the subgraph *instance* name (and optional
`additionalInformation`) stays per-request:

```jsonc
POST /storedquery
{
  "name": "person-network",
  "kind": "SubGraph",
  "subGraph": {
    "vertexFilter": "return (v) => v.Label == \"person\";",
    "patterns": [
      { "type": "Vertex", "patternName": "p1", "vertexFilter": "return (v) => v.Label == \"person\";" },
      { "type": "Edge",   "patternName": "knows", "direction": "OutgoingEdge", "edgePropertyFilter": "return (p) => p == \"knows\";" },
      { "type": "Vertex", "patternName": "p2", "vertexFilter": "return (v) => v.Label == \"person\";" }
    ]
  }
}
```

The fragments are compiled **at registration** through the same Roslyn paths (and the same
compile bounds) the inline endpoints use; a compile failure rejects the registration with a
400 carrying the compiler diagnostics. `201` pins the compiled artifact for the query's
registered lifetime. `409` = duplicate name or library quota
(`Fallen8:StoredQueries:MaxCount`, default 256; applies PER NAMESPACE — each namespace owns its
own library, see [graph-namespaces](../graph-namespaces/)). A SubGraph template block cannot carry a
pattern step's `semanticMinScore` (400): a template has no semantic query to bind — see the
[element-embeddings README](../element-embeddings/README.md), "Semantic traversal".

## Invoking

```jsonc
POST /path/1/to/5
{ "storedQuery": "adults-shortest", "maxDepth": 5, "pathAlgorithmName": "DIJKSTRA" }

PUT /subgraph
{ "name": "my-network-today", "storedQuery": "person-network" }
```

`storedQuery` is mutually exclusive with the inline fragment fields (400 when mixed).
Resolution errors: unknown name → 404 (the message names the stored query), wrong kind → 400,
not invocable (see below) → 409.

A subgraph created from a stored template is **self-contained**: its persisted recipe is the
materialized specification, so deleting the stored query later never orphans the subgraph.

## Managing

| Call | Gate | Notes |
|---|---|---|
| `GET /storedquery` | authenticated | Summaries incl. `compileState` |
| `GET /storedquery/{name}` | authenticated | Full source + (if `Failed`) recompile diagnostics — also covers manual migration |
| `DELETE /storedquery/{name}` | authenticated | Unpins the compiled artifact so its load context can unload |

Entries are immutable: to change one, delete and re-register.

## Durability

Stored queries survive **Save/Load** (a manifest sidecar next to the save point, source only,
recompiled eagerly on load) and **crash recovery** (WAL entries, replayed in commit order) —
a query survives a crash+replay exactly when it survives a Save+Load.

If a recompile fails on load (e.g. an engine upgrade changed the model API), the entry is
**kept** as `compileState: "Failed"` with its diagnostics — visible in list/get, 409 on
invoke, recoverable by delete + re-register. Operator-registered state is never silently
dropped. An engine embedded without a hosting layer (no compiler registered) loads entries
as `SourceOnly`.

## Security matrix

Dynamic code execution is always on, so the only gate on these endpoints is authentication —
required when an API key is configured, open otherwise.

| Request | key set, authenticated | key set, anonymous | no key |
|---|---|---|---|
| Register stored query | 201 | **401** | 201 |
| `/path` / `/subgraph` with inline fragments | 2xx | **401** | 2xx |
| `/path` / `/subgraph` via `storedQuery` | 2xx | **401** | 2xx |
| `/path` with no filter/cost at all | 200 | **401** | 200 |
| List / get / delete stored queries | 2xx | **401** | 2xx |

Pinned by `StoredQuerySecurityMatrixTest` through the real pipeline.
