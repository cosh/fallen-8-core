---
title: "Plugin registration"
description: "Add runtime algorithm and graph-function plugins by authoring C# source, compiled, contract-validated, and namespace-scoped."
---

Fallen-8 lets you add a plugin at runtime by **authoring it as C# source** and submitting it to a
typed registration endpoint. The server compiles the source with Roslyn, validates that it
satisfies a known plugin contract, and stores it **scoped to the namespace** it was registered
under. This replaces the former DLL-upload endpoint (`PUT /plugin`), which loaded an opaque,
unvalidated assembly in-process and has been removed.

> **Honesty note.** A registered plugin still runs **in-process with full trust** when invoked —
> exactly as much authority as the DLL it replaces. What registration buys is that the server now
> *sees and validates the source and the contract it satisfies* instead of loading an opaque
> binary, and every registration is typed, gated, logged, and per-namespace. It is **not** a
> sandbox. Registering a plugin is a trust decision equivalent to deploying code.

This is the same idea as [stored queries](/fallen-8-core/stored-queries/), lifted from a *fragment* (a
filter/cost body the server wraps in a fixed class) to a **whole plugin type** you author in full —
because a plugin's contract carries real logic a fragment can't express.

## Two categories

The set of plugin categories is closed and defined by maintainers — a new category means a new
typed endpoint and contract in the codebase, never a widened catch-all. Two ship today:

| Category | Endpoint | Contract you implement | How it is invoked |
| --- | --- | --- | --- |
| **algorithm** | `POST /plugins/algorithm` | `IShortestPathAlgorithm` (`Path`), `ISubGraphAlgorithm` (`SubGraph`), or `IGraphAnalyticsAlgorithm` (`Analytics`) — chosen by the `contract` field | **Transparently by name** through the existing `/path`, `/subgraph`, `/analytics` endpoints — no new call |
| **function** | `POST /plugins/function` | `IGraphFunction` — a stored graph procedure | `POST /plugins/function/{name}/invoke` |

Built-in algorithms (`BLS`, `DIJKSTRA`, `PAGERANK`, …) are unaffected; only *runtime* plugins are
authored this way. Registering a name that collides with a built-in of the same category is
rejected, so a registered plugin never silently shadows a built-in.

### The `function` category — stored graph functions

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

## The contract validation

Your source must contain **exactly one** public, non-abstract class with a public parameterless
constructor that implements the category's interface, whose `PluginName` equals the registration
`name`. A compile error, a missing/ambiguous implementor, a missing constructor, or a name mismatch
is returned as `400` with the compiler/contract diagnostics — the same shape the
`/delegates/validate` editor check uses. F8 Studio drives the side-effect-free
`POST /plugins/{algorithm,function}/validate` endpoints for compile-as-you-type.

## The gate

Registration requires the dynamic-plugin capability plus authentication. The capability is
**on by default** — `Fallen8:Security:EnableDynamicPluginLoading` (default `true`) is the
per-instance global default, and any namespace can **override** it (enable or disable) with
`PATCH /ns/{name}` `{ "pluginRegistration": "enabled" | "disabled" | "inherit" }`. The gate resolves
the addressed namespace's override first, then the global default. Everything else — invoking a
registered plugin, listing, getting, and deleting — requires only the standard authentication, never
the capability. So you can leave registration on everywhere, or disable it on a specific
(e.g. shared/untrusted) namespace while others keep authoring. See [security.md](/fallen-8-core/security/).

Default-on is deliberate and consistent with the always-on dynamic-code model (inline path/subgraph
C# fragments already compile unconditionally); it is **not** a sandbox — a registered plugin runs
full-trust, so an internet-facing instance must set an API key.

## Namespace scoping

Registered plugins live in the **per-namespace** registry (each namespace is one isolated graph).
A plugin registered under one namespace is invisible and unresolvable in another; register it in
each namespace that needs it. Both bare routes (the reserved `default` namespace) and
`/ns/{ns}/plugins/…` are served. There is no global/shared runtime registration — built-ins are the
only cross-namespace plugins.

## Durability

The registry is namespace **checkpoint state**: plugin definitions (source + metadata, never
compiled bytes) are written to a manifest sidecar on save and recompiled on load, and
registration/removal are written to the write-ahead log and replayed on crash recovery — the same
mechanism [stored queries](/fallen-8-core/stored-queries/) and subgraph recipes use. If a plugin's source fails
to recompile after an engine upgrade, the entry is kept in a `Failed` state with its diagnostics
(visible via `GET /plugins/{name}`) rather than silently dropped; delete and re-register to
recover. `GET /plugins/{name}` returns the full source, which also covers manual migration between
instances. Plugins are **not** part of the element-level bulk export.

## REST surface

| Method & route | Gate | Purpose |
| --- | --- | --- |
| `POST /plugins/algorithm` | dynamic-plugin | Register an algorithm plugin (`contract`: `Path`/`SubGraph`/`Analytics`). |
| `POST /plugins/function` | dynamic-plugin | Register a graph function. |
| `POST /plugins/{category}/validate` | dynamic-plugin | Compile-check source without registering (editor aid). |
| `POST /plugins/function/{name}/invoke` | auth | Run a registered graph function; returns projected vertices/edges. |
| `GET /plugins` | auth | List registered plugins in the namespace. |
| `GET /plugins/{name}` | auth | Full definition including source (and recompile diagnostics if `Failed`). |
| `DELETE /plugins/{name}` | auth | Deregister a plugin. |

The per-namespace registration ceiling is `Fallen8:Plugins:MaxCount` (default 64).

## AI agents (MCP)

The MCP server bridges the registry as the `f8_plugins` tool: `list`/`get`/`invoke` are on the read
tier; `delete` needs the write capability; `register_algorithm`/`register_function` need the `code`
capability (the same gate as inline C# fragments). A registered *algorithm* needs no MCP invoke — an
agent calls it by name through `f8_paths`/`f8_analytics`/`f8_subgraph`. See
[mcp-server.md](/fallen-8-core/mcp-server/).

## See also

- [plugins.md](/fallen-8-core/plugins/) — the plugin model, families, and the built-ins.
- [stored-queries.md](/fallen-8-core/stored-queries/) — the fragment-shaped sibling this generalizes.
- [security.md](/fallen-8-core/security/) — the dynamic-code / plugin-registration trust boundary.
