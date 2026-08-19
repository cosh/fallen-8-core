---
title: "Embed scenarios"
description: "The staged path for building Fallen-8 into your own product: an in-browser WASM graph, the Studio canvas rendering it, and the full embedded Studio against a hosted instance."
---

Suppose your product - a SaaS portal, an internal tool, a desktop shell - wants Fallen-8 inside
its own environment. Not a link to somewhere else: a graph that lives where your users already
are. There is a staged path for that, and each stage builds on the one before it. This page
walks the whole journey and says plainly where its boundary is; the contract of each piece
lives in its own deep-dive, linked as you go.

1. **A graph in the page**: run the engine itself in the browser, no server at all.
2. **See it**: render that graph with Studio's canvas component.
3. **Work on it**: graduate to the full embedded Studio against a hosted instance.

## Stage 1: a graph in the page (the engine on WebAssembly)

The engine ships as the `Fallen-8` NuGet package and runs on a single-threaded browser-wasm
runtime: transactions apply inline on the calling thread, checkpoints save into and load from
the Emscripten virtual filesystem, and a host that registers its plugin types gets indexes and
vector search even though a browser has no assemblies to scan. All of that is the
[library page's](/library/#single-threaded-hosts-such-as-browser-webassembly)
story, and it is not aspirational: a trimmed browser-wasm probe
([`tools/browser-probe`](https://github.com/cosh/fallen-8-core/tree/main/tools/browser-probe))
runs the engine on that runtime in CI on every push.

What the library page leaves to you is the bridge from your JavaScript application into that
.NET module. The standard .NET mechanism is `[JSExport]` (a `wasmbrowser`-style project
referencing the `Fallen-8` package):

```csharp
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using System.Text.Json.Serialization;
using NoSQL.GraphDB.Core;

public static partial class GraphBridge
{
    private static readonly Fallen8 Engine = new(loggerFactory);

    [JSExport]
    internal static string Snapshot()
    {
        var snapshot = new CanvasSnapshot(
            Engine.GetAllVertices()
                .Select(v => new CanvasNode(v.Id, v.Label)).ToArray(),
            Engine.GetAllEdges()
                .Select(e => new CanvasEdge(
                    e.Id, e.SourceVertex.Id, e.TargetVertex.Id, e.EdgePropertyId, e.Label))
                .ToArray());
        return JsonSerializer.Serialize(snapshot, BridgeJson.Default.CanvasSnapshot);
    }
}

// Source-generated serialization, so the trimmed publish keeps working; camelCase, so the
// JSON is already in the canvas component's prop shape.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CanvasSnapshot))]
internal sealed partial class BridgeJson : JsonSerializerContext;

internal sealed record CanvasSnapshot(CanvasNode[] Nodes, CanvasEdge[] Edges);
internal sealed record CanvasNode(int Id, string Label);
internal sealed record CanvasEdge(int Id, int Source, int Target, string EdgePropertyId, string Label);
```

Your page boots the module and calls it like any other export:

```js
import { dotnet } from "./_framework/dotnet.js";

const { getAssemblyExports, getConfig } = await dotnet.create();
const exports = await getAssemblyExports(getConfig().mainAssemblyName);
const { nodes, edges } = JSON.parse(exports.GraphBridge.Snapshot());
```

Writes go the same way: expose a method that builds a `CreateVerticesTransaction` /
`CreateEdgesTransaction` and enqueues it - on this host the write has already happened when
the call returns (inline execution, see the library page). Persistence is the host's job at
the edges: the engine saves into the virtual filesystem; getting those bytes into IndexedDB,
a download, or your own API is yours.

## Stage 2: see it (the canvas component over your data)

Studio's graph canvas ships as a standalone component on its own
[package subpath](/embed-studio/), so this step costs the graph renderer and not the app shell
around it - data in,
selection callbacks out, no app shell, no server dependency. The snapshot from stage 1 is
already in its prop shape:

```tsx
import { F8GraphCanvas } from "@fallen-8/studio/canvas";
import "@fallen-8/studio/styles.css";

<F8GraphCanvas
  nodes={Object.fromEntries(nodes.map((n) => [n.id, n]))}
  edges={Object.fromEntries(edges.map((e) => [e.id, e]))}
  onSelect={(ref) => showDetails(ref)}
  theme={{ accent: "#e2001a" }}
/>;
```

Re-call `Snapshot()` (or expose finer-grained reads) whenever your bridge reports a write, and
feed the new objects in; the component re-renders from props. It styles only its own subtree
and takes your theme tokens, so it sits inside your design system rather than beside it. How
the artifact is built and consumed - exports map, peer dependencies, the scoped stylesheet -
is on [Embed F8 Studio](/embed-studio/).

At this point you have a real graph database and an interactive visualization running entirely
inside your page. No container, no network, no credential.

## Stage 3: work on it (the full Studio against a hosted instance)

When browsing and querying outgrow a canvas - delegates, path finding, indexes, save games,
the whole [Studio feature set](/studio/) - mount the full embedded Studio. It
needs one thing the in-page engine does not have: a **REST origin**. Run one
([all-in-one image or compose](/running/), or any Fallen-8 deployment your
product operates), allow your product's origin in
[`AllowedCorsOrigins`](/security/), and hand Studio the instance:

```ts
import { mountStudio } from "@fallen-8/studio";
import "@fallen-8/studio/styles.css";

mountStudio(document.getElementById("studio")!, {
  instances: [{
    id: "tenant-graph",
    name: "Tenant graph",
    baseUrl: "https://f8.example.internal",
    auth: { kind: "bearer", getToken: () => yourAuth.freshToken() },
  }],
  lockInstances: true,
  history: "memory",
  storageNamespace: "tenant-42.",
  nlAssist: "instance-only",
});
```

The full config contract - locks, namespace pinning, theme tokens, the NL-assist policy - is
on [Embed F8 Studio](/embed-studio/).

**Bringing the stage 1 graph along.** The wire format is the documented
[`fallen8-jsonl`](/bulk-import-export/) interchange: walk `GetAllVertices()` /
`GetAllEdges()` in your bridge, emit one JSON line per element in that shape (the meta line is
optional - drop it), and `POST` the lines to the hosted instance's `/bulk/import` (into an
empty namespace). From then on the hosted instance is the system of record, and the in-page
engine remains what it is best at: an instant, zero-infrastructure scratchpad.

## The boundary, stated plainly

The full Studio **cannot run against the in-page WASM engine**, and that is a real boundary,
not a missing paragraph. Studio speaks HTTP to the REST app (`fallen-8-core-apiApp`), and
several of the things that make it Studio live in that app rather than in the engine: the
delegate and plugin editors compile C# server-side with Roslyn, the live event feed is an SSE
stream, bulk import/export is an HTTP body, and the semantic features proxy through the
instance. An engine in your page has none of that surface, so the graduation path is the one
above: canvas over the in-page engine, full Studio over a hosted instance. If your product
needs the full Studio against an in-process engine, that is a feature conversation (a
transport seam with declared capability degradation), not a configuration you can reach today.

## The map

| Stage | You run | You import | Deep dive |
| --- | --- | --- | --- |
| Graph in the page | the `Fallen-8` NuGet package on browser-wasm | your own `[JSExport]` bridge | [Use as a library](/library/) |
| See it | nothing new | `F8GraphCanvas` + `styles.css` | [Embed F8 Studio](/embed-studio/) |
| Work on it | a Fallen-8 REST deployment | `mountStudio` | [Embed F8 Studio](/embed-studio/), [Running](/running/), [Security](/security/) |
