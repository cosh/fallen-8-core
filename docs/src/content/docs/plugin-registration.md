---
title: "Plugin registration"
description: "Add runtime algorithm and graph-function plugins by authoring C# source, compiled, contract-validated, and namespace-scoped."
---

Fallen-8 lets you add a plugin at runtime by **authoring it as C# source** and submitting it to a
typed registration endpoint. The server compiles the source with Roslyn, validates that it
satisfies a known plugin contract, and stores it **scoped to the namespace** it was registered
under. This replaces the former DLL-upload endpoint (`PUT /plugin`), which loaded an opaque,
unvalidated assembly in-process and has been removed.

> **Honesty note.** A registered plugin still runs **in-process with full trust** when invoked,
> exactly as much authority as the DLL it replaces. What registration buys is that the server now
> *sees and validates the source and the contract it satisfies* instead of loading an opaque
> binary, and every registration is typed, gated, logged, and per-namespace. It is **not** a
> sandbox. Registering a plugin is a trust decision equivalent to deploying code.

This is the same idea as [stored queries](/fallen-8-core/stored-queries/), lifted from a *fragment* (a
filter/cost body the server wraps in a fixed class) to a **whole plugin type** you author in full,
because a plugin's contract carries real logic a fragment can't express.

## Two categories

The set of plugin categories is closed and defined by maintainers: a new category means a new
typed endpoint and contract in the codebase, never a widened catch-all. Two ship today:

| Category | Endpoint | Contract you implement | How it is invoked |
| --- | --- | --- | --- |
| **algorithm** | `POST /plugins/algorithm` | `IShortestPathAlgorithm` (`Path`), `ISubGraphAlgorithm` (`SubGraph`), or `IGraphAnalyticsAlgorithm` (`Analytics`), chosen by the `contract` field | **By name** through an existing algorithm endpoint, no new call (what is reachable differs per contract, see below) |
| **function** | `POST /plugins/function` | `IGraphFunction`: a stored graph procedure | `POST /plugins/function/{name}/invoke` |

Built-in algorithms (`BLS`, `DIJKSTRA`, `PAGERANK`, …) are unaffected; only *runtime* plugins are
authored this way. Registering a name that collides with a built-in of the same **contract** is
rejected, so a registered plugin never silently shadows a built-in. The check is per contract, not
per category: a `Path` name is compared only against the `Path` built-ins, and graph functions have
no built-ins so they never collide.

**Reach, per contract.** A registered `Path` plugin is selected by `pathAlgorithmName` on
`POST /path/{from}/to/{to}`. Every compiled *algorithm* entry is *discoverable* whether or not it is
reachable: `GET /status` unions the registry into `availablePathPlugins`, `availableSubGraphPlugins`
and `availableAnalyticsPlugins`, and `GET /analytics/algorithms` lists each `Analytics` entry with
its description. `Failed`/`SourceOnly` entries are deliberately not advertised. Every advertised name
is now also reachable: an `Analytics` entry runs through `POST /analytics/{name}` and
`POST /analytics/{name}/partition/{partitionId}`, and a `SubGraph` entry is selected by the
`algorithm` field on `PUT /subgraph` (omit it for the built-in breadth-first algorithm; an unknown
name is a `400` that lists what is available). Name matching is ordinal, so the case must match what
you registered.

### The `function` category, stored graph functions

A graph function is a user-authored read procedure: it receives the graph (via the `IFallen8` it
captures in `Initialize`), does a full scan or an index query, and returns a **view of existing
vertices and edges** (projected with the same DTOs as `GET /vertex/{id}` and `GET /edge/{id}`).
It is read-only. Example:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Plugin;
using NoSQL.GraphDB.Core.Plugins;

public sealed class NeighboursOfLabel : IGraphFunction
{
    private IFallen8 _graph;
    public string PluginName    => "NeighboursOfLabel";
    public Type   PluginCategory => typeof(IGraphFunction);
    public string Description   => "All vertices carrying a given label.";
    public string Manufacturer  => "acme";
    public void   Initialize(IFallen8 fallen8, IDictionary<string, object> parameter) => _graph = fallen8;
    public void   Dispose() { }

    public bool TryInvoke(out GraphFunctionResult result, IDictionary<string, object> parameters)
    {
        var label = parameters != null && parameters.TryGetValue("label", out var l) ? l as string : null;
        result = GraphFunctionResult.FromElements(_graph.GetAllVertices(label), edges: null);
        return true;
    }
}
```

Register and invoke it:

```
POST /plugins/function
{ "name": "NeighboursOfLabel", "description": "...", "sourceCode": "using System; ..." }

POST /plugins/function/NeighboursOfLabel/invoke
{ "parameters": { "label": "person" } }
→ 200 { "vertices": [ … ], "edges": [ … ] }
```

Invocation parameters are string-valued in v1 (a function parses what it needs).

A registered plugin is activated **fresh for every invocation** (it is never held in the
`PluginCache` a built-in is reused from), a graph-function instance is disposed right after its call,
and `Initialize` is handed a `null` `parameter` bag by every REST-reachable invocation. So state you
cache in a field does not survive between calls: do the work inside `TryInvoke`.

## The contract validation

Your source must contain **exactly one** public, non-abstract class with a public parameterless
constructor that implements the category's interface, whose `PluginName` equals the registration
`name`. A compile error, a missing/ambiguous implementor, a missing constructor, or a name mismatch
makes the registration a `400` whose problem-body `detail` carries the errors-only compiler/contract
text. The side-effect-free `POST /plugins/{algorithm,function}/validate` endpoints, which F8 Studio
drives for compile-as-you-type, do not fail on bad source: they answer `200` with
`{ "valid": false, "error": "<the same text>" }`. That is a flat string, not the positioned
`diagnostics` array `/delegates/validate` returns for fragments.

## The gate

Registration and the two compile-check endpoints require the dynamic-plugin capability plus
authentication. The capability is **on by default**: `Fallen8:Security:EnableDynamicPluginLoading`
(default `true`) is the per-instance global default, and any namespace can **override** it (enable or disable) with
`PATCH /ns/{name}` `{ "pluginRegistration": "enabled" | "disabled" | "inherit" }`. The gate resolves
the addressed namespace's override first, then the global default. Everything else (invoking a
registered plugin, listing, getting, and deleting) requires only the standard authentication, never
the capability. So you can leave registration on everywhere, or disable it on a specific
(e.g. shared/untrusted) namespace while others keep authoring. See [security.md](/fallen-8-core/security/).

Default-on is deliberate and consistent with the always-on dynamic-code model (inline path/subgraph
C# fragments already compile unconditionally); it is **not** a sandbox: a registered plugin runs
full-trust, so an internet-facing instance must set an API key.

## Namespace scoping

Registered plugins live in the **per-namespace** registry (each namespace is one isolated graph).
A plugin registered under one namespace is invisible and unresolvable in another; register it in
each namespace that needs it. Both bare routes (the reserved `default` namespace) and
`/ns/{ns}/plugins/…` are served. There is no global/shared runtime registration, built-ins are the
only cross-namespace plugins.

## Durability

The registry is namespace **checkpoint state**: plugin definitions (source + metadata, never
compiled bytes) are written to a manifest sidecar on save and recompiled on load, and
registration/removal are written to the write-ahead log and replayed on crash recovery: the same
mechanism [stored queries](/fallen-8-core/stored-queries/) and subgraph recipes use. If a plugin's source fails
to recompile after an engine upgrade, the entry is kept in a `Failed` state with its diagnostics
(visible via `GET /plugins/{name}`) rather than silently dropped; delete and re-register to
recover. `GET /plugins/{name}` returns the full source, which also covers manual migration between
instances. Plugins are **not** part of the element-level bulk export.

`compileState` on `GET /plugins` and `GET /plugins/{name}` is `Compiled`, `Failed`, or `SourceOnly`
(the last one for a definition loaded into an embedded engine with no compiler registered). Only a
`Compiled` entry runs: invoking a non-`Compiled` graph function answers `409`, and a non-`Compiled`
algorithm name does not resolve from the registry at all, so the lookup falls through to the
built-ins.

## REST surface

| Method & route | Gate | Purpose |
| --- | --- | --- |
| `POST /plugins/algorithm` | dynamic-plugin | Register an algorithm plugin (`contract`: `Path`/`SubGraph`/`Analytics`). |
| `POST /plugins/function` | dynamic-plugin | Register a graph function. |
| `POST /plugins/{algorithm,function}/validate` | dynamic-plugin | Compile-check source without registering (editor aid). Two concrete routes, not an open `{category}` template. |
| `POST /plugins/function/{name}/invoke` | auth | Run a registered graph function; returns projected vertices/edges. |
| `GET /plugins` | auth | List registered plugins in the namespace. |
| `GET /plugins/{name}` | auth | Full definition including source (and recompile diagnostics if `Failed`). |
| `DELETE /plugins/{name}` | auth | Deregister a plugin. |

Entries are **immutable** and there is no update route: editing a plugin means
`DELETE /plugins/{name}` and re-registering. Re-registering a name that already exists is a `409`.

| Limit | Value | On breach |
| --- | --- | --- |
| Plugin name | `^[A-Za-z0-9_-]{1,128}$`, compared ordinally (so case-sensitive), unique per namespace | `400`; a duplicate or a built-in collision is `409` |
| Source length | 200,000 characters, checked before Roslyn runs | `400` |
| Registry size | 64 per namespace, `Fallen8:Plugins:MaxCount` | `409` |

The 1 MiB body cap (`413`) and the sensitive-endpoint rate limit (`429`) are the ones shared by every
code endpoint: see [security.md](/fallen-8-core/security/).

## AI agents (MCP)

The MCP server bridges the registry as the `f8_plugins` tool: `list`/`get`/`invoke` are on the read
tier; `delete` needs the write capability; `register_algorithm`/`register_function` need the `code`
capability (the same gate as inline C# fragments). A registered graph function is run with
`f8_plugins` `invoke`. A registered *algorithm* is selectable from the traversal tools too: `f8_paths`,
`f8_subgraph` and `f8_analytics` each take a free-form `algorithm` name and reach the addressed
namespace's registry, so an agent has the same reach as REST.
See [mcp-server.md](/fallen-8-core/mcp-server/).

## See also

- [plugins.md](/fallen-8-core/plugins/): the plugin model, families, and the built-ins.
- [stored-queries.md](/fallen-8-core/stored-queries/): the fragment-shaped sibling this generalizes.
- [security.md](/fallen-8-core/security/): the dynamic-code / plugin-registration trust boundary.
