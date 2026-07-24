# Fallen-8 documentation

Fallen-8 is an in-memory graph database written in C# (.NET 10), built for raw in-memory
speed and for AI agents that produce code. It has **no query language** — queries are C#
[delegates](delegates.md).

New here? Start with [Running](running.md), open [F8 Studio](studio.md), and load a
[sample graph](samples.md).

## Start here

| Doc | What it covers |
|---|---|
| [Running](running.md) | Every way to launch it — the one-command default, bare `dotnet run`, config, GPU |
| [Architecture](architecture.md) | How the engine, REST app, Studio, and model sidecar fit together |
| [F8 Studio](studio.md) | The browser UI — screens, the delegate editor, the NL assist |
| [Sample gallery](samples.md) | The one-click demo graphs, with screenshots and example queries |

## Core model

| Doc | What it covers |
|---|---|
| [Graph model](graph-model.md) | Vertices, edges, typed properties, the transaction queue, direct reads, property scans |
| [Delegates](delegates.md) | Why there is no query language; runtime-compiled C# fragments and their contracts |
| [Indexes](indexes.md) | Dictionary, range, fulltext, spatial R-Tree, and vector index types and their scans |
| [Namespaces](namespaces.md) | Many isolated graphs in one Fallen-8, addressable under `/ns/{name}/…` |
| [Security](security.md) | The all-or-nothing API key and the dynamic-code-execution switch |
| [Plugins](plugins.md) | The extension model behind indices, algorithms, and services |

## Query and traverse

| Doc | What it covers |
|---|---|
| [Path finding](path-finding.md) | Shortest/weighted paths with delegate filters and cost functions |
| [Subgraphs](subgraphs.md) | Pattern-matched extraction into a standalone, recalculable graph |
| [Graph analytics](graph-analytics.md) | PageRank, components, communities, degree, triangles, with write-back |
| [Stored queries](stored-queries.md) | Register a compiled query once, invoke it by name without dynamic code |
| [Vector search](vector-search.md) | Exact k-nearest-neighbour over `float[]` embeddings |
| [Semantic traversal](semantic-traversal.md) | Embeddings as element state; the code-free `semantic` block |

## Data and operations

| Doc | What it covers |
|---|---|
| [Bulk import/export](bulk-import-export.md) | Stream whole graphs as newline-delimited JSON |
| [Live change feed](change-feed.md) | Committed mutations as Server-Sent Events, with resync |
| [Save games](save-games.md) | Checkpoints, the registry that drives startup, and the write-ahead log |
| [Observability](observability.md) | Opt-in Prometheus/OTLP metrics and traces, statistics, health probes |
| [REST API](rest-api.md) | The OpenAPI document, Scalar reference, versioning, and the endpoint map |

## Help

| Doc | What it covers |
|---|---|
| [Troubleshooting](troubleshooting.md) | The common snags and their fixes |
