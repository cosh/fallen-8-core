# Architecture

Fallen-8 is an in-memory graph engine with a thin REST app wrapped around it. The engine
holds the graph in RAM and runs the algorithms; the app exposes it over HTTP and serves the
browser UI. Three kinds of client reach it: **AI agents** through the [MCP server](mcp-server.md),
and **F8 Studio** (the browser UI) plus **your own services** straight over the REST API. This
doc is the map of how the pieces fit; each piece's contract lives in its own doc, linked below.

```mermaid
%%{init: {'theme':'base','themeVariables':{'fontFamily':'ui-monospace, SFMono-Regular, Menlo, Consolas, monospace','lineColor':'#666666'}}}%%
flowchart TB
    agents["AI agents"]:::client
    studio["F8 Studio<br/>(React SPA, browser)"]:::client
    svc["Your services / code"]:::client

    mcp["MCP server — fallen-8-mcp<br/>separate deployable · bridges MCP → REST"]:::mcp

    subgraph app["fallen-8-core-apiApp (ASP.NET Core · thin layer)"]
        direction TB
        rest["REST controllers + OpenAPI"]:::sys
        roslyn["Roslyn delegate compiler<br/>+ code cache"]:::sys
        semantic["Semantic gateway<br/>(embeddings + chat, optional)"]:::sys
        wwwroot["wwwroot (serves F8 Studio)"]:::sys
    end
    subgraph engine["fallen-8-core (in-memory engine)"]
        direction TB
        ns["Namespaces<br/>(collection of graphs)"]:::sys
        writer["Single writer thread<br/>← transaction queue"]:::sys
        model["Graph model<br/>(vertices, edges, properties, embeddings)"]:::sys
        plugins["Plugins<br/>(indices · path · subgraph · analytics · services)"]:::sys
        durab["Durability<br/>(WAL + checkpoints + registry)"]:::sys
    end
    sidecar["Model sidecar (Ollama)<br/>embeddings + delegate assist"]:::ext

    subgraph obs["Fleet observability · one Grafana pane"]
        direction TB
        collector["OTel Collector<br/>ingest + spanmetrics"]:::sys
        promstore["Prometheus<br/>metrics"]:::sys
        tempo["Tempo<br/>traces"]:::sys
        loki["Loki<br/>logs"]:::sys
        grafana["Grafana<br/>dashboards"]:::sys
        collector --> promstore
        collector --> tempo
        collector --> loki
        promstore --> grafana
        tempo --> grafana
        loki --> grafana
    end

    agents -->|MCP| mcp
    mcp -->|HTTP| rest
    studio -->|HTTP| rest
    svc -->|HTTP| rest
    studio -.->|custom model endpoint (optional, browser-direct)| sidecar
    wwwroot -.- studio
    rest --> ns
    rest --> roslyn
    rest --> semantic
    semantic -.->|embeddings + chat| sidecar
    rest -.->|OTLP metrics/traces/logs| collector
    mcp -.->|OTLP| collector
    ns --> writer --> model
    model --- plugins
    model --- durab

    classDef client fill:#45494D,stroke:#666666,color:#FEFEFE
    classDef mcp fill:#E2001A,stroke:#FC0606,color:#FEFEFE
    classDef sys fill:#141516,stroke:#45494D,color:#FEFEFE
    classDef ext fill:#141516,stroke:#666666,color:#C6C7C8,stroke-dasharray:5 4
    style app fill:#000000,stroke:#E2001A,stroke-width:1.5px,color:#C6C7C8
    style engine fill:#000000,stroke:#E2001A,stroke-width:1.5px,color:#C6C7C8
    style obs fill:#000000,stroke:#E2001A,stroke-width:1.5px,color:#C6C7C8
```

## The engine (`fallen-8-core`)

Everything the database *is* lives here; it has no dependency on ASP.NET and can be embedded
as a library (see the `Try*` API in [Graph model](graph-model.md)).

- **A Fallen-8 is a collection of [namespaces](namespaces.md).** Each namespace is one
  isolated graph backed by its own engine instance, with its own vertices, edges, indices,
  subgraphs, and stored queries. The reserved `default` namespace is what bare routes address.
- **The [graph model](graph-model.md)** is a directed property graph: vertices and edges are
  both first-class elements carrying typed properties and, optionally, named
  [embeddings](semantic-traversal.md).
- **Mutation goes through a transaction queue.** Callers enqueue a transaction; a **single
  writer thread** applies them one at a time, so writes are serialized and readers never lock.
  This is why the REST mutation calls take `waitForCompletion` — it waits for the writer to
  finish the enqueued transaction. Reads go straight to the in-memory structures.
- **Algorithms and indices are [plugins](plugins.md).** Path traversers, subgraph algorithms,
  whole-graph analytics, index types, and services are discovered by a plugin factory and
  addressed by name. The built-ins are just the plugins that ship in the box.
- **Durability** is a write-ahead log plus full-graph checkpoints, tracked by a registry that
  decides what loads on startup. The whole story — including volatile mode — is in
  [Save games](save-games.md).

## The REST app (`fallen-8-core-apiApp`)

A thin ASP.NET Core layer. It owns three things the engine deliberately does not:

- **The HTTP surface** — versioned controllers, an OpenAPI document, and the Scalar reference
  ([REST API](rest-api.md)), plus the [security](security.md) boundary (the API key; dynamic
  code execution is always on).
- **Runtime delegate compilation.** Fallen-8 has [no query language](delegates.md): path and
  subgraph filters arrive as C# fragments, and the app compiles them with Roslyn into typed
  delegates and caches the result. This is the app's job, not the engine's, and it is gated
  by a capability switch that is off by default.
- **The optional [embedding provider](semantic-traversal.md).** Text-in embedding lives only
  in the app so the engine stays model-free; a bare run has it off, and the compose
  environment wires it to the model sidecar.

The app also serves [F8 Studio](studio.md) as static files from its `wwwroot`.

## AI agents and the MCP server

AI agents do not call the REST API directly. They go through **`fallen-8-mcp`**, a separate
deployable that bridges the [Model Context Protocol](https://modelcontextprotocol.io) to the
REST surface over HTTP — it references neither the engine nor the app. It is a small,
token-frugal tool surface, read-only by default, with opt-in write/admin tiers and three auth
modes. The full story is in [MCP server](mcp-server.md).

## F8 Studio and the model sidecar

[F8 Studio](studio.md) is a React single-page app. It talks to the REST API like any other
client — it has no privileged channel, including for models: the app is the **semantic
gateway**. Both embeddings and the natural-language assist default to going **through the
instance** — the browser calls `POST /embedding/text` / `POST /chat`, and the app proxies to
its model backend. In the compose environment that backend is the Ollama sidecar, which
serves both the embedding model and the chat model. F8 itself bundles no model weights or
runtime. The one path that stays off the instance is a **custom** NL-assist endpoint: there
the browser calls the model backend directly and any API key is held only in the browser
(the earlier browser-only default was retired in favour of the gateway — see
[studio.md](studio.md)).

## Fleet observability

The engine and app emit metrics, traces, and logs through BCL instruments; the app and the
[MCP server](mcp-server.md) push them over OTLP to a small consumer stack that ships with the
environment: an OpenTelemetry Collector ingests the push and derives per-action metrics from
spans, Prometheus stores metrics, Tempo stores traces, Loki stores logs, and Grafana is the
single pane. Each process stamps a tenant/instance/namespace identity on every signal so one
Grafana can separate many instances. This is a **push** relationship and a separate set of
containers, always on with `npm run env:up`. The full story, including what isolation is and is
not guaranteed, is in [Fleet observability](fleet-observability.md).

## One deployable unit

The [compose environment](running.md) builds the SPA, publishes the app, and ships them in
one container (the app serves the UI from `wwwroot`), alongside the Ollama sidecar. Managed as
a whole, it is the "just works" default. The container is durable by default — checkpoints and
the WAL live on a mounted volume.

## See also

- [Running](running.md) — how to launch each of these
- [Graph model](graph-model.md) — the data model and the transaction/read contract
- [Delegates](delegates.md) — why there is no query language and how fragments compile
- [Plugins](plugins.md) — the extension model for indices and algorithms
- [Save games](save-games.md) — the durability subsystem
- [Namespaces](namespaces.md) — the graph-collection model
- [Security](security.md) — the API-app boundary
- [MCP server](mcp-server.md) — how AI agents reach Fallen-8
- [Fleet observability](fleet-observability.md): the multi-tenant metrics/traces/logs consumer
