# Plugins

Fallen-8's indexes, path/subgraph/analytics algorithms, and services are all plugins: classes
implementing a family interface that derives from `IPlugin`. The engine finds them by scanning
assemblies, addresses each by its `PluginName`, and activates a fresh instance on demand. This
page is the contract for the plugin model and for writing your own; the docs that cover *using*
each built-in are linked in the family table.

> Runtime-compiled filter/cost fragments (`IPathTraverser`, the `Delegates.*` types) are **not**
> plugins — they are a separate, Roslyn-based mechanism owned by [delegates.md](delegates.md).

## The `IPlugin` contract

Every plugin implements [`IPlugin`](../fallen-8-core/Plugin/IPlugin.cs), which extends
`IDisposable`:

| Member | Purpose |
| --- | --- |
| `string PluginName { get; }` | Unique name used to address the plugin. Case-sensitive (ordinal). |
| `Type PluginCategory { get; }` | The family interface this plugin belongs to. |
| `string Description { get; }` | Human-readable description (surfaced in listings). |
| `string Manufacturer { get; }` | Author / vendor string. |
| `void Initialize(IFallen8 fallen8, IDictionary<string, object> parameter)` | Wires in the engine and per-instance options. |
| `void Dispose()` | From `IDisposable`; release instance state. |

## Plugin families

Each family is an interface deriving from `IPlugin`. Built-ins are listed by their `PluginName`.

| Family | Interface | Built-in `PluginName`s | Doc |
| --- | --- | --- | --- |
| Index | [`IIndex`](../fallen-8-core/Index/IIndex.cs) | `DictionaryIndex`, `RangeIndex`, `RegExIndex`, `SpatialIndex`, `SingleValueIndex`, `VectorIndex` | [indexes.md](indexes.md), [vector-search.md](vector-search.md) |
| Shortest path | [`IShortestPathAlgorithm`](../fallen-8-core/Algorithms/Path/IShortestPathAlgorithm.cs) | `DIJKSTRA`, `BLS` | [path-finding.md](path-finding.md) |
| Subgraph | [`ISubGraphAlgorithm`](../fallen-8-core/Algorithms/SubGraph/ISubGraphAlgorithm.cs) | `Breadth First Search Subgraph Algorithm` | [subgraphs.md](subgraphs.md) |
| Analytics | [`IGraphAnalyticsAlgorithm`](../fallen-8-core/Algorithms/Analytics/IGraphAnalyticsAlgorithm.cs) | `DEGREE`, `WCC`, `TRIANGLECOUNT`, `PAGERANK`, `LABELPROPAGATION` | [graph-analytics.md](graph-analytics.md) |
| Service | [`IService`](../fallen-8-core/Service/IService.cs) | *(none built in)* | — |

`IIndex` and `IService` also extend `IFallen8Serializable` (`Save`/`Load`), so their instances are
included in checkpoints; an index returns `false` from `CanPersist` to opt out. The index family
has refinement interfaces — `IRangeIndex`, `IFulltextIndex`, `ISpatialIndex`, `IVectorIndex` — that
each extend `IIndex` with query methods but do not form separate plugin families.

## Discovery and caching

[`PluginFactory`](../fallen-8-core/Plugin/PluginFactory.cs) is a static discovery service:

- **Scanning.** On first use it loads every `*.dll` in `AppContext.BaseDirectory` (plus any path
  registered via `AddPluginSearchDirectory`) and collects the eligible types. The result is
  memoized; `Assimilate(stream, path)` writes a new DLL and invalidates the cache so the next
  lookup rediscovers it.
- **Eligibility.** A candidate is a `public`, non-abstract class with a public parameterless
  constructor. Its family is decided by which family interface it implements — no attributes or
  manifest.
- **Addressing.** `TryFindPlugin<T>(out result, name)` resolves `PluginName` → type through a
  memoized map (ordinal, first match wins on duplicate names) and returns a freshly activated
  instance. `TryGetAvailablePlugins<T>` / `TryGetAvailablePluginsWithDescriptions<T>` enumerate a
  family.

Activated *algorithm* instances are reused via [`PluginCache`](../fallen-8-core/Cache/PluginCache.cs)
— three `MemoryCache`s (`ShortestPath`, `SubGraph`, `Analytics`), keyed by `PluginName` with a
60-second sliding expiration. Index and service instances are not cached here; they are held by
`Fallen8.IndexFactory.Indices` and `Fallen8.ServiceFactory.Services`.

## Initialization with options

Options always arrive through `Initialize` as an `IDictionary<string, object>`; each family owns
its keys.

| Family | Create / register | Options |
| --- | --- | --- |
| Index | `engine.IndexFactory.TryCreateIndex(out index, name, typeName, parameter)` | Passed straight to `Initialize` (e.g. `VectorIndex` reads dimension/metric/embedding). |
| Service | `engine.ServiceFactory.TryAddService(out svc, pluginName, instanceName, parameter)` | Passed straight to `Initialize`. |
| Path / subgraph / analytics | Resolved and cached by name at call time | `Initialize` only captures the engine; per-run parameters travel in the request definition object. |

## Writing a plugin

Implement a family interface (or subclass a provided base) with a public parameterless
constructor, a unique `PluginName`, and the file's MIT header. The example subclasses
[`AGraphAnalyticsAlgorithm`](../fallen-8-core/Algorithms/Analytics/AGraphAnalyticsAlgorithm.cs),
which supplies the whole `IPlugin` surface plus the workspace/budget scaffolding, leaving three
members to write:

```csharp
using System.Collections.Generic;
using System.Diagnostics;
using NoSQL.GraphDB.Core.Algorithms.Analytics;

public sealed class VertexCountAlgorithm : AGraphAnalyticsAlgorithm
{
    public override string PluginName => "VERTEXCOUNT";
    public override string Description => "Scores every in-scope vertex 1.";

    protected override bool TryRunCore(out GraphAnalyticsResult result,
        GraphAnalyticsDefinition definition, Workspace workspace, BudgetGuard budget,
        Stopwatch stopwatch)
    {
        var scores = new Dictionary<int, double>(workspace.Count);
        foreach (var vertex in workspace.Vertices)
            scores[vertex.Id] = 1d;

        result = new GraphAnalyticsResult(scores, new Dictionary<string, object>(),
            converged: true, iterationsRun: 1, stopwatch.Elapsed, budgetExhausted: false);
        return true;
    }
}
```

To deploy it, compile the class into any assembly on the probing path — into `fallen-8-core`, a
referenced assembly, or a DLL dropped in the base directory (or a registered plugin directory).
It is discovered on the next lookup and addressed by `PluginName` (here, `"algorithm":
"VERTEXCOUNT"` on `POST /analytics`). A family with no base class (e.g. `IIndex`) requires
implementing the full `IPlugin` contract above directly.

## REST exposure

There is no general plugin-management API. Two read-only listings and one upload endpoint exist:

| Endpoint | Purpose |
| --- | --- |
| `GET /status` | Lists available index / path / analytics / service plugin names alongside the live index inventory — see [observability.md](observability.md). |
| `GET /analytics/algorithms` | Lists analytics plugins with descriptions — see [graph-analytics.md](graph-analytics.md). |
| `PUT /plugin` | Uploads a DLL and assimilates it. It runs **in-process with full trust** (a trust boundary, not a sandbox), is process-global across every namespace, and is off by default — it requires authentication plus `Fallen8:Security:EnableDynamicPluginLoading=true`. See [security.md](security.md). |

## See also

- [indexes.md](indexes.md) — built-in index types and their creation options.
- [path-finding.md](path-finding.md) · [subgraphs.md](subgraphs.md) · [graph-analytics.md](graph-analytics.md) — the built-in algorithm plugins.
- [delegates.md](delegates.md) — runtime-compiled filter/cost fragments (a different mechanism).
- [security.md](security.md) — gating for uploaded, full-trust plugins.
