# Delegates

Fallen-8 has no query language, and that is the point. Instead of a Cypher/Gremlin/SQL dialect, a query in Fallen-8 *is* C#: a small delegate fragment that the engine compiles at runtime and runs directly against the in-memory graph. This page owns that design decision, the shape and contract of every fragment kind, how compilation and caching work, the `context` parameter, and the `POST /delegates/validate` compile-check.

## No query language, on purpose

Fallen-8 deliberately ships without a query language. There is no Cypher, no Gremlin, no SQL dialect — and none is planned. Owning a query language is a large, permanent commitment: a grammar, a parser, a planner, an optimizer, and a lifetime of compatibility promises. That responsibility was declined on purpose, not left as a to-do.

Queries are expressed as C# instead. You hand the engine a tiny delegate fragment — `return (v) => v.Label == "person";` — and it compiles that fragment to a real .NET delegate and invokes it inside the traversal at full in-memory speed. There is no query string to parse, no intermediate representation, no impedance layer between what you wrote and what runs. The precompiled flavor (stored queries, below) removes even the per-request compile.

This is a deliberate fit for the era of code-generating agents. An agent already writes C# fluently; asking it to emit a three-line lambda is easier and less error-prone than teaching it a bespoke query dialect, and the fragment runs against the graph with nothing lost in translation. The absence of a query language is a feature of Fallen-8, not a gap in it.

## Anatomy of a fragment

A fragment is the **body of a factory method** whose return type is one of the delegate types below and whose single parameter is a [`TraversalContext context`](#the-context-parameter). So a fragment must `return` a lambda of the matching shape:

```csharp
return (v) => v.Label == "person";
```

- The `return` prefix is required — the fragment is a method body, not a bare expression.
- End the statement with `;`.
- The lambda parameter name (`v`, `e`, `p`, …) is yours to choose; only its type is fixed by the delegate kind.
- For multi-step logic use a **block-body lambda** so the fragment stays a single `return` statement: `return (v) => { var n = v.GetOutDegree(); return n > 2; };`.
- A `null`/empty fragment means "match everything" (filters) or "no custom cost" (costs).

The generated method also receives `context`, so the returned lambda may close over it (see below).

## The delegate contract

Every fragment compiles against one of the delegate types in [`Delegates.cs`](../fallen-8-core/Algorithms/Delegates.cs). Filters return `bool` (return `false` to drop the element); costs return `double` (the step weight). The `context` parameter is always in scope.

| Delegate kind | Lambda receives | Returns | Accepted in |
|---|---|---|---|
| `VertexFilter` | `VertexModel` | `bool` | path `vertexFilter`; subgraph `vertexFilter` + Vertex pattern |
| `EdgeFilter` | `EdgeModel` | `bool` | path `edgeFilter`; subgraph `edgeFilter` + Edge pattern |
| `EdgePropertyFilter` | `string` (edge-property id) | `bool` | path `edgePropertyFilter`; subgraph Edge pattern |
| `VertexCost` | `VertexModel` | `double` | path `vertexCost` |
| `EdgeCost` | `EdgeModel` | `double` | path `edgeCost` |
| `GraphElementFilter` | `AGraphElementModel` | `bool` | `/delegates/validate` only — no live REST slot produces it today |
| `LabelFilter` | `string` (label) | `bool` | engine-internal; not exposed over REST |

### Accessor surface

The lambda parameter is a live engine model object. Read it through these members (properties are typed values behind `TryGetProperty<T>`, not C# fields).

`AGraphElementModel` — base of both `VertexModel` and `EdgeModel`:

| Member | Purpose |
|---|---|
| `int Id` | Element id |
| `string Label` | Element label (may be `null`) |
| `bool TryGetProperty<T>(out T value, string key)` | Typed property read; `false` if absent |
| `ImmutableDictionary<string,object> GetAllProperties()` | Snapshot of all properties |
| `int GetPropertyCount()` | Number of properties |
| `DateTime GetCreationDate()` / `GetModificationDate()` | Timestamps |
| `bool TryGetEmbedding(out ReadOnlySpan<float> vector, string name = "default")` | Named embedding, if present |

`VertexModel` adds:

| Member | Purpose |
|---|---|
| `uint GetOutDegree()` / `GetInDegree()` | Degree counts |
| `List<VertexModel> GetAllNeighbors()` | All adjacent vertices |
| `List<string> GetOutgoingEdgeIds()` / `GetIncomingEdgeIds()` | Edge-property-id groups |
| `IReadOnlyDictionary<string, IReadOnlyList<EdgeModel>> OutEdges` / `InEdges` | Adjacency grouped by edge-property id |
| `bool TryGetOutEdge(out IReadOnlyList<EdgeModel> edges, string edgePropertyId)` / `TryGetInEdge(…)` | One adjacency group |

`EdgeModel` adds: `VertexModel SourceVertex`, `VertexModel TargetVertex`, `string EdgePropertyId` (all read-only).

## The `context` parameter

`context` is a `TraversalContext` — the per-request semantic state, embedded once before the traversal starts. It is empty (`HasQueryVector == false`) unless the request carries a `semantic` block; that block is the code-free way to supply a query vector and is owned by [semantic-traversal.md](semantic-traversal.md). A fragment reads it to blend similarity into a filter or cost:

| Member | Purpose |
|---|---|
| `bool HasQueryVector` | Whether a query vector was supplied |
| `bool TrySimilarity(AGraphElementModel element, out float score)` | Score the element's embedding against the query vector; `false` if unavailable/non-finite |
| `ReadOnlySpan<float> QueryVector` | The raw query vector |
| `string EmbeddingName` / `VectorDistanceMetric Metric` | Which embedding and metric are scored |

Example — keep vertices whose embedding is close to the query vector:
`return (v) => context.TrySimilarity(v, out var s) && s > 0.8f;`

## Compilation and caching

Fragments are wrapped into a provider class and compiled with **Roslyn** (`CodeGenerationHelper`) into the strongly-typed `Delegates.*` types, then loaded into a collectible `AssemblyLoadContext` so the assembly can be unloaded once it is no longer referenced. The compile is the expensive step, so identical work is cached and never recompiled:

| Surface | Cache | Key | Size | Eviction |
|---|---|---|---|---|
| Path filters/costs | `GeneratedCodeCache` (process-wide, static) | the `(filter, cost)` pair, by value equality — numeric bounds (`maxDepth`/`maxResults`/`maxPathWeight`) and `pathAlgorithmName` are applied at traversal time and **excluded** from the key | 1024 entries | 60 s sliding; collectible context unloads on eviction |
| Subgraph filters | provider cache in `CodeGenerationHelper` | the generated provider source string | 256 entries | 60 s sliding; collectible context unloads on eviction |

So repeating the same fragment set reuses one compiled artifact, and two path requests that differ only in a numeric bound share the same cached traverser. Compile timings and cache hit/miss counters are exported as metrics — see [observability.md](observability.md).

There is no type/namespace allowlist and no sandbox: the compilation references the .NET core assemblies plus the engine assembly, and imports a fixed set of usings — `System`, `System.Linq`, `NoSQL.GraphDB.Core.Model`, `NoSQL.GraphDB.Core.Index.Vector` (subgraph fragments also get `NoSQL.GraphDB.Core.Algorithms`); `TraversalContext` resolves by simple name. Any reachable type may be used (fully qualified if not imported), and compiled code runs in-process with full trust. That is exactly why compiling arbitrary C# is gated — [security.md](security.md) owns the `EnableDynamicCodeExecution` flag. The only structural guards are size caps: a fragment over 100,000 characters, or a generated source over 1,000,000, is rejected before Roslyn runs. These runtime-compiled delegates are not [plugins](plugins.md) — that is a separate discovery mechanism.

## Where fragments are accepted

| Surface | Fragment slots | Doc |
|---|---|---|
| `POST /path/{from}/to/{to}` | `filter.vertexFilter` / `edgeFilter` / `edgePropertyFilter`, `cost.vertexCost` / `edgeCost` | [path-finding.md](path-finding.md) |
| `PUT /subgraph` | top-level `vertexFilter` / `edgeFilter` plus per-pattern filters | [subgraphs.md](subgraphs.md) |
| `POST /storedquery` | the same fragments, compiled once and invoked by name | [stored-queries.md](stored-queries.md) |
| `POST /delegates/validate` | compile-check a single fragment (below) | this doc |

## Validating a fragment: `POST /delegates/validate`

Compile-checks one fragment without running it — nothing is emitted, loaded, or executed. Use it to give an editor or agent instant feedback before submitting a query. It is gated by the same dynamic-code switch as the query endpoints ([security.md](security.md)): `401` without a credential, `403` when dynamic code is disabled, `429` when rate-limited.

Request: `{ "delegateKind": "<kind>", "fragment": "<C#>" }`, where `delegateKind` is one of the kinds above (case-insensitive). Response:

```json
{
  "valid": false,
  "diagnostics": [
    { "line": 1, "column": 19, "endLine": 1, "endColumn": 19,
      "id": "CS1002", "severity": "error", "message": "; expected" }
  ]
}
```

`valid` is `true` when the fragment compiles with no errors (warnings are reported but do not block). Diagnostic positions are 1-based and already mapped to fragment coordinates — line 1 / column 1 is the first character you sent. A `null`/empty fragment is valid with no diagnostics; an oversized one fails with id `F8LIMIT`.

```bash
curl -X POST http://localhost:8080/delegates/validate \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: $F8_API_KEY" \
  -d '{
        "delegateKind": "VertexFilter",
        "fragment": "return (v) => v.TryGetProperty(out int age, \"age\") && age > 30;"
      }'
```

```powershell
$body = @{
    delegateKind = "VertexFilter"
    fragment     = 'return (v) => v.TryGetProperty(out int age, "age") && age > 30;'
} | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri http://localhost:8080/delegates/validate `
    -Headers @{ "X-Api-Key" = $env:F8_API_KEY } -ContentType "application/json" -Body $body
```

## Authoring notes and common pitfalls

| Symptom | Cause | Fix |
|---|---|---|
| `CS1002: ; expected` | Missing trailing `;` | End the fragment with `;` |
| "not all code paths return a value" | Missing `return`, or logic placed outside the lambda | Prefix with `return`; put branching inside a block-body lambda |
| `CS1061: '…' does not contain '…'` | Using an `EdgeModel` member on a `VertexFilter` (or reading a property as a C# field) | Match the kind's parameter type; read properties via `TryGetProperty<T>` |
| `InvalidCastException` at query time (not compile time) | `TryGetProperty<T>` with `T` different from the stored value's CLR type | Request the exact stored type (e.g. `out int`, `out double`, `out string`) |

## See also

- [Path finding](path-finding.md) — the path request and its filter/cost slots
- [Subgraphs](subgraphs.md) — subgraph pattern filters
- [Stored queries](stored-queries.md) — named, precompiled fragment sets
- [Semantic traversal](semantic-traversal.md) — the code-free `semantic` block that populates `context`
- [Security](security.md) — the API key and the `EnableDynamicCodeExecution` gate
- [Observability](observability.md) — codegen compile and cache metrics
- [Studio](studio.md) — the browser delegate editor and NL-assist UI
- [Graph model](graph-model.md) — elements, properties, and transactions the fragments read
- [REST API](rest-api.md) — OpenAPI document and Scalar reference
