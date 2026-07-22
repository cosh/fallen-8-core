# Subgraph feature

Extract a pattern-matched subset of a Fallen-8 graph into a new, standalone graph.

- **[spec.md](./spec.md)** — what the feature does and its contract (retrofitted).
- **[plan.md](./plan.md)** — implementation plan and phase status.

## Concepts

A `SubGraphDefinition` has three parts, applied in order:

1. **`VertexFilter`** / **`EdgeFilter`** — optional pre-filters that decide which vertices
   and edges are copied into the new subgraph (null ⇒ copy everything valid).
2. **`Pattern`** — an ordered, alternating sequence of vertex/edge patterns
   (`VertexPattern` → `EdgePattern`/`VariableLengthEdgePattern` → `VertexPattern` → …).
   The algorithm finds every path matching the sequence and prunes everything not on a
   matching path. An empty pattern list means "keep the copied elements".

The default algorithm is the breadth-first `BreathFirstSearchSubgraphAlgorithm`. Results
are registered in `Fallen8.SubGraphFactory` by name and can be recalculated against their
source graph (including nested subgraphs-of-subgraphs).

## Using it from C#

```csharp
var definition = new SubGraphDefinition
{
    Name = "people-who-know-people",
    Pattern = new List<APattern>
    {
        new VertexPattern { PatternName = "p1", GraphElement = ge => ge.Label == "person" },
        new EdgePattern    { PatternName = "knows", Direction = Direction.OutgoingEdge,
                             EdgeProperty = p => p == "knows" },
        new VertexPattern { PatternName = "p2", GraphElement = ge => ge.Label == "person" }
    }
};

if (fallen8.SubGraphFactory.TryCreateSubGraph<BreathFirstSearchSubgraphAlgorithm>(
        out var result, definition.Name, definition))
{
    // result.SubGraph is a standalone IFallen8 containing the matched subgraph.
}
```

## Using it over REST

Filters are C# code fragments (prefixed with `return`) compiled at runtime, mirroring the
path-finding API. A null/empty fragment matches everything.

| Method & route | Purpose |
|---|---|
| `PUT /subgraph` | Create + register from a `SubGraphSpecification` (201 / 400 / 409) |
| `GET /subgraph` | List registered subgraph names |
| `GET /subgraph/{name}` | Summary (metadata + counts) (200 / 404) |
| `GET /subgraph/{name}/graph` | The extracted vertices and edges (200 / 404) |
| `POST /subgraph/{name}/recalculate` | Recalculate against the current source (200 / 404 / 409) |
| `DELETE /subgraph/{name}` | Deregister (204 / 404) |

```jsonc
PUT /subgraph
{
  "name": "friends-of-alice",
  "vertexFilter": "return (v) => v.Label == \"person\";",
  "patterns": [
    { "type": "Vertex", "patternName": "start", "vertexFilter": "return (v) => v.Label == \"person\";" },
    { "type": "Edge",   "patternName": "rel", "direction": "OutgoingEdge", "edgePropertyFilter": "return (p) => p == \"knows\";" },
    { "type": "Vertex", "patternName": "end", "vertexFilter": "return (v) => v.Label == \"person\";" }
  ]
}
```

The endpoints are described in the OpenAPI document (`/openapi/v0.1.json`) and the Scalar
reference (`/scalar/v0.1`) in Development.

## Tests

- `fallen-8-unittest/SubGraphTest.cs` — algorithm behaviour, including branching graphs and
  pattern validation.
- `fallen-8-unittest/SubGraphCodeGenerationTest.cs` — REST spec → definition compilation.
- `fallen-8-unittest/SubGraphControllerTest.cs` — end-to-end controller behaviour.
