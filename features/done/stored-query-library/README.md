# Stored Query Library — Usage

Named, validated, **pre-compiled** query definitions: register once, invoke by name from the
existing path/subgraph endpoints. Companion docs: [spec.md](./spec.md) (contract) and
[plan.md](./plan.md) (phases).

## The operating model

1. **Provisioning window** — turn the dynamic-code switch on
   (`Fallen8:Security:EnableDynamicCodeExecution=true`) and register a vetted set of queries.
2. **Day-to-day** — run with the switch **off**: inline C# fragments are rejected (403), but
   invoking the registered set keeps working. The code surface shrinks from "arbitrary C# per
   request" to a closed, operator-approved library.

> **Honesty note (as everywhere in this repo):** an invoked stored query still runs
> in-process with **full trust**. The library narrows *who can introduce* code — a provenance
> control, not a sandbox. And recompilation at load/WAL-replay is not gated by the switch:
> the switch gates the REST *introduction* surface, not the engine rehydrating definitions
> the operator already approved.

## Registering (switch on)

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

## Invoking (switch on **or** off)

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
| `DELETE /storedquery/{name}` | authenticated | Never gated by the switch; unpins the compiled artifact so its load context can unload |

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

## Security matrix (authenticated caller)

| Request | switch on | switch off |
|---|---|---|
| Register stored query | 201 | **403** |
| `/path` / `/subgraph` with inline fragments | 2xx | **403** |
| `/path` / `/subgraph` via `storedQuery` | 2xx | 2xx |
| `/path` with no filter/cost at all | 200 | 200 |
| List / get / delete stored queries | 2xx | 2xx |

Pinned by `StoredQuerySecurityMatrixTest` through the real pipeline in both switch states.
