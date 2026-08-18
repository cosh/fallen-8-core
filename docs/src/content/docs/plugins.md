---
title: "Plugins"
description: "The extension model behind indices, path/subgraph/analytics algorithms, graph functions, and services, all discovered plugins."
---

Fallen-8's indexes, path/subgraph/analytics algorithms, graph functions, and services are all
plugins: classes implementing a family interface that derives from `IPlugin`. The engine finds them
by scanning assemblies, addresses each by its `PluginName`, and activates a fresh instance on
demand. This page is the contract for the plugin model and for writing your own; the docs that cover
*using* each built-in are linked in the family table.

> Runtime-compiled filter/cost fragments (`IPathTraverser`, the `Delegates.*` types) are **not**
> plugins: they are a separate, Roslyn-based mechanism owned by [delegates.md](/delegates/).

## The `IPlugin` contract

Every plugin implements [`IPlugin`](https://github.com/cosh/fallen-8-core/blob/main/fallen-8-core/Plugin/IPlugin.cs), which extends
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
| Index | [`IIndex`](https://github.com/cosh/fallen-8-core/blob/main/fallen-8-core/Index/IIndex.cs) | `DictionaryIndex`, `RangeIndex`, `RegExIndex`, `SpatialIndex`, `SingleValueIndex`, `VectorIndex` | [indexes.md](/indexes/), [vector-search.md](/vector-search/) |
| Shortest path | [`IShortestPathAlgorithm`](https://github.com/cosh/fallen-8-core/blob/main/fallen-8-core/Algorithms/Path/IShortestPathAlgorithm.cs) | `DIJKSTRA`, `BLS` | [path-finding.md](/path-finding/) |
| Subgraph | [`ISubGraphAlgorithm`](https://github.com/cosh/fallen-8-core/blob/main/fallen-8-core/Algorithms/SubGraph/ISubGraphAlgorithm.cs) | `Breadth First Search Subgraph Algorithm` | [subgraphs.md](/subgraphs/) |
| Analytics | [`IGraphAnalyticsAlgorithm`](https://github.com/cosh/fallen-8-core/blob/main/fallen-8-core/Algorithms/Analytics/IGraphAnalyticsAlgorithm.cs) | `DEGREE`, `WCC`, `TRIANGLECOUNT`, `PAGERANK`, `LABELPROPAGATION` | [graph-analytics.md](/graph-analytics/) |
| Graph function | [`IGraphFunction`](https://github.com/cosh/fallen-8-core/blob/main/fallen-8-core/Plugins/IGraphFunction.cs) | *(none built in)* | [plugin-registration.md](/plugin-registration/) |
| Service | [`IService`](https://github.com/cosh/fallen-8-core/blob/main/fallen-8-core/Service/IService.cs) | *(none built in)* | *(this page)* |

`IIndex` and `IService` also extend `IFallen8Serializable` (`Save`/`Load`), so their instances are
included in checkpoints. On top of `IPlugin`, `IIndex` requires two capability declarations, both
without a default: `CanPersist` (return `false` to be skipped on save and recreated after load) and
`SupportsPointEqualityLookup` (whether an element stored under a key via `AddOrUpdate` is
retrievable by that same key via `TryGetValue`). The latter is the engine's single answer to "can
this index be addressed by an exact key": it is what the `equality` capability in `GET /status`
reports and what the ingestion layer checks before binding a link or dedup index. The index family
has refinement interfaces (`IRangeIndex`, `IFulltextIndex`, `ISpatialIndex`, `IVectorIndex`) that
each extend `IIndex` with query methods but do not form separate plugin families.

## Discovery and caching

[`PluginFactory`](https://github.com/cosh/fallen-8-core/blob/main/fallen-8-core/Plugin/PluginFactory.cs) is a static discovery service:

- **Scanning.** On first use it loads every `*.dll` in `AppContext.BaseDirectory` and collects the
  eligible types: this is how the **built-in** plugins compiled into the shipped assemblies are
  found. The result is memoized. (There is no runtime *external-assembly* loading: the DLL-upload
  path was removed, runtime plugins are now authored as C# source, see below.)
- **Eligibility.** A candidate is a `public`, non-abstract class with a public parameterless
  constructor. Its family is decided by which family interface it implements: no attributes or
  manifest.
- **Addressing.** `TryFindPlugin<T>(out result, name)` resolves `PluginName` → type through a
  memoized map (ordinal, first match wins on duplicate names) and returns a freshly activated
  instance. `TryGetAvailablePlugins<T>` / `TryGetAvailablePluginsWithDescriptions<T>` enumerate a
  family.

Activated *algorithm* instances are reused via [`PluginCache`](https://github.com/cosh/fallen-8-core/blob/main/fallen-8-core/Cache/PluginCache.cs):
three `MemoryCache`s (`ShortestPath`, `SubGraph`, `Analytics`), keyed by `PluginName` with a
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
constructor and a unique `PluginName`; an in-repo built-in also carries the file's MIT header (the
convention tests enforce it), submitted runtime source does not. The example subclasses
[`AGraphAnalyticsAlgorithm`](https://github.com/cosh/fallen-8-core/blob/main/fallen-8-core/Algorithms/Analytics/AGraphAnalyticsAlgorithm.cs),
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

The base fixes `PluginCategory` as a non-virtual member and supplies a default `Manufacturer`, which is
`virtual`: a third-party plugin overrides it to advertise its own vendor string in
`GET /analytics/algorithms` while still subclassing the base.
[`ABucketIndex`](https://github.com/cosh/fallen-8-core/blob/main/fallen-8-core/Index/ABucketIndex.cs)
is the equivalent base for bucket-style indexes (it fixes `Manufacturer`, `CanPersist` and
`SupportsPointEqualityLookup` and leaves `PluginName`/`Description` abstract); the path, subgraph,
graph-function and service families have no base class, so they implement the full `IPlugin`
contract above directly.

There are two ways to deploy it. As a **built-in**, compile the class into an assembly in the base
directory (`fallen-8-core` itself or a referenced assembly), and it is addressed by `PluginName`
(here, `POST /analytics/VERTEXCOUNT`). Discovery is memoized on first use and never invalidated at
runtime, so a new assembly is picked up on the **next process start**, not by dropping it next to a
running instance. As a **runtime plugin**, submit its C# source to the typed registration API and it
is compiled, contract-validated, and stored scoped to a namespace; see
[plugin-registration.md](/plugin-registration/).

The two paths do not admit the same families. Runtime registration is a closed set of two
categories covering four contracts: path, subgraph and analytics algorithms, plus graph functions.
Index and service plugins have no registrable category and can only ship as built-ins.

## REST exposure

Two read-only listings surface the built-ins unioned with the addressed namespace's
runtime-registered plugins, a typed registration API manages the latter, and the service family is
instantiated over REST:

| Endpoint | Purpose |
| --- | --- |
| `GET /status` | Lists available index / path / subgraph / analytics / service plugin names alongside the live index inventory, see [observability.md](/observability/). It is the only listing that carries the subgraph plugin names. |
| `GET /analytics/algorithms` | Lists analytics plugins with descriptions: see [graph-analytics.md](/graph-analytics/). |
| `POST /plugins/{algorithm,function}`, `POST /plugins/{algorithm,function}/validate`, `GET/DELETE /plugins[/{name}]`, `POST /plugins/function/{name}/invoke` | Register (from C# source), compile-check without registering, list, get, delete, and invoke **runtime** plugins, scoped per namespace, see [plugin-registration.md](/plugin-registration/). This replaces the former `PUT /plugin` DLL upload, which has been removed. |
| `POST /service`, `DELETE /service/{key}` | Instantiate a service plugin by `PluginName` (body below) and remove one by its instance key. Both return a bare boolean with **200**: `false` means the plugin type is unknown, the instance key is already taken, or the key was not found. It is never signalled as an HTTP error status. |

`POST /service` is the service family's only REST surface. It resolves `pluginType` against `IService`
implementations only, and Fallen-8 ships none, so it addresses a service plugin you compiled in
yourself. Its `pluginOptions` values are *typed* specifications, not raw JSON scalars: the map key is
the option name, and each value names a primitive from the allow-list and carries its value as a
string, converted with `InvariantCulture` before it reaches `Initialize`.

```json
{
  "uniqueId": "myService",
  "pluginType": "<PluginName of an IService implementation>",
  "pluginOptions": {
    "cacheSize": {
      "propertyId": "cacheSize",
      "fullQualifiedTypeName": "System.Int32",
      "propertyValue": "1000"
    }
  }
}
```

## See also

- [indexes.md](/indexes/): built-in index types and their creation options.
- [path-finding.md](/path-finding/) · [subgraphs.md](/subgraphs/) · [graph-analytics.md](/graph-analytics/): the built-in algorithm plugins.
- [plugin-registration.md](/plugin-registration/): registering runtime plugins from C# source.
- [delegates.md](/delegates/): runtime-compiled filter/cost fragments (a different mechanism).
- [security.md](/security/): gating for the full-trust dynamic-code and plugin-registration surfaces.
