[![.NET](https://github.com/cosh/fallen-8-core/actions/workflows/buildAndTest.yml/badge.svg?branch=main)](https://github.com/cosh/fallen-8-core/actions/workflows/buildAndTest.yml)

## Welcome to Fallen-8

![Fallen-8 logo.](https://raw.githubusercontent.com/cosh/fallen-8-core/main/pics/F8White.svg)

Fallen-8 is an in-memory [graph database](http://en.wikipedia.org/wiki/Graph_database) implemented in C# (.NET 10). Its focus is to provide raw speed for heavy graph algorithms.

This is the .NET Core version of the original [fallen-8](https://github.com/cosh/fallen-8). The core of fallen-8 stays unchanged, and the web services expose a modern OpenAPI description rendered with the [Scalar](https://github.com/scalar/scalar) API reference.

### Key features

- **Properties** on vertices and edges
- **Indexes** on vertices and edges (dictionary, range, fulltext, spatial R-Tree, vector kNN)
- **Path finding** with runtime-compiled filter and cost functions
- **Graph analytics** — PageRank, connected components, communities, degree centrality, triangle counting, with optional property write-back (see [Graph analytics](#graph-analytics))
- **Subgraphs** — extract a pattern-matched subset of the graph as a standalone graph, recalculate it when the source changes, and persist it (see [Subgraphs](#subgraphs))
- **Plugins** for indexes, algorithms and services
- Checkpoint **persistency**, with a **save-game registry** that records every checkpoint and drives startup (see [Save games](#save-games-checkpoints))
- **Observability** — opt-in Prometheus/OTLP metrics + traces, a `/statistics` graph-shape snapshot, health endpoints (see [Observability](#observability))
- **F8 Studio** — a browser UI for browsing, querying, visualizing, and authoring the C# delegate fragments, with an optional local **natural-language assist**
- Ships as **one Docker unit** (engine + API + UI) via `docker compose`

### Sweet spots

- **Enterprise Search** (semantic ad-hoc queries on multi-dimensional graphs)
- **Lawful Interception** (mass analysis)
- **E-Commerce** (bid- and portfolio-management)

## Architecture

The REST API app (`fallen-8-core-apiApp`) is a thin layer over the in-memory engine
(`fallen-8-core`). All mutation flows through transactions; indices, algorithms and services
are plugins; and the engine checkpoints its state to disk. The API also serves **F8 Studio**
(`fallen-8-web-ui`, a React SPA) as static files and exposes `POST /delegates/validate` so
the delegate editor can compile-check fragments; the natural-language assist calls a
**user-run model backend** (Ollama / llama.cpp) directly from the browser. A **save-game
registry** (`metadata/savegames.json`) records every checkpoint and is the authority for
what loads on startup. The whole stack ships as **one Docker unit** managed via
`docker compose`.

![Fallen-8 architecture: F8 Studio (browser SPA) and a user-run NL-assist model sit above the ASP.NET Core REST API, which is a thin layer over the in-memory engine (model and transactions, algorithms, indices, persistence, plugin system); a save-game registry records checkpoints and drives startup, and the whole stack ships as one Docker unit.](./pics/architecture.svg)

## Running it

```bash
dotnet run --project fallen-8-core-apiApp
```

With the F8 Studio web UI (built into the apiApp's wwwroot):

```bash
npm run install:ui
npm run build:apiapp
dotnet run --project fallen-8-core-apiApp
# open http://localhost:5000
```

To debug the whole stack (engine + API + UI) in VS Code, see [DEBUGGING.md](./DEBUGGING.md).

Or the complete environment in Docker - engine, REST API, F8 Studio, and the NL-assist
model backend (Ollama + the MIT default model, pulled on first start). The environment is
managed **as one unit** via compose - do not start/stop individual containers:

```bash
npm run env:up      # Start everything (auto-detects an NVIDIA GPU; CPU otherwise)
npm run env:down    # Stop everything; data volumes persist
npm run env:logs    # Follow all logs
npm run env:status  # Health of the whole environment

# F8 Studio:        http://localhost:8080
# NL-assist model:  http://localhost:11434 (configure in the delegate editor)
```

> **PowerShell (Windows):** the plain `npm run …` commands work as-is. Where a command below
> is prefixed with an environment variable (`NAME=value …`, the bash form), set it the
> PowerShell way instead — `$env:NAME = "value"; …` (it stays set for the session;
> `Remove-Item Env:NAME` clears it) — and chain commands with `;`, not `&&`. A few `.sh`
> helpers are bash; run them with `bash script.sh` (Git for Windows ships `bash`).

**On first start** the Ollama container pulls its two models — `phi4-mini` (base) and
`phi4-f8-mini` (the fine-tune, the UI default) — straight into the `f8-ollama-models` volume.
That is a few GB, so it takes a few minutes on the first `env:up`; watch it with `npm run
env:logs`. The F8 API and Studio are up immediately; NL assist starts answering once the
pull finishes. Later starts reuse the cached models, so they are instant and need no network.

The pull needs internet to `registry.ollama.ai` **the first time**. If this machine is
offline (or you want to skip the wait), pre-seed the volume once from a machine that has
internet — `scripts/ensure-models.sh` needs only Docker, no host Ollama (on Windows run it as
`bash scripts/ensure-models.sh`) — then `env:up` starts with the models already cached. If a
pull fails, the Ollama endpoint still comes up
with whatever is present (it does not crash-loop); fix the network and re-run `env:up`, or
run `scripts/ensure-models.sh`.

A larger, GPU-only fine-tune `phi4-f8` (full Phi-4, 14B) is available as an **opt-in** — this
also pulls and serves it (~9 GB), and the delegate editor then offers it as a preset:

```bash
F8_PULL_PHI4F8=1 npm run env:up
```
```powershell
$env:F8_PULL_PHI4F8 = "1"; npm run env:up
```

See [features/delegate-model-variants](features/done/delegate-model-variants/README.md).

The delegate editor's compile validation and NL assist run C# fragments through the
server. That surface is gated by a single capability flag that is **off by default**;
turn it on to use the editor:

Bash

```bash
F8_ENABLE_DYNAMIC_CODE=true docker compose up --build
```

PowerShell (`$env:` variables stay set for the session; `Remove-Item Env:F8_ENABLE_DYNAMIC_CODE` to unset)

```powershell
$env:F8_ENABLE_DYNAMIC_CODE = "true"; docker compose up --build
```

Authentication is independent and all-or-nothing: set `F8_API_KEY` and the *entire*
service requires that key (register the instance in Studio with it); leave it unset and
the whole service — reads, mutations, and the code endpoints alike — is open, for a
trusted network. The dynamic-code flag gates code execution either way.

Bash

```bash
F8_API_KEY=change-me F8_ENABLE_DYNAMIC_CODE=true docker compose up --build   # secured + editor on
```

PowerShell

```powershell
$env:F8_API_KEY = "change-me"; $env:F8_ENABLE_DYNAMIC_CODE = "true"; docker compose up --build   # secured + editor on
```

### Save games (checkpoints)

Every `PUT /save` (and the clean-shutdown save) is recorded in a persistent registry at
`<deployment>/metadata/savegames.json` with KPIs and file facts; the "Save games" screen
in F8 Studio lists, loads, and deletes them. **Startup is registry-driven:** on boot the
engine loads the newest registered save game; when the registry is empty it does not load
any checkpoint (a file sitting in the storage directory is no longer auto-loaded just
because it is there) — it keeps whatever the write-ahead log replayed at construction,
which for a fresh deployment is an empty graph. To adopt a pre-existing checkpoint after upgrading, load it once via
`PUT /load` (or the Save games screen); it is then registered permanently. See
[features/save-games/spec.md](features/done/save-games/spec.md).

In the Development environment the API description and interactive reference are available at:

- **OpenAPI document:** `https://localhost:5001/openapi/v0.1.json`
- **Scalar API reference:** `https://localhost:5001/scalar/v0.1`

![The Scalar API reference for fallen-8-core, listing the Admin, Graph, SubGraph and other endpoint groups.](./pics/scalarApiReference.png)

## Samples

Start `fallen-8-core-apiApp` and have fun. The following walkthrough uses the built-in
sample graph.

### Create a sample graph

HTTP example

```http
PUT /unittest HTTP/1.1
Host: localhost:5001
```

cURL example

```bash
curl -L -X PUT 'https://localhost:5001/unittest'
```

PowerShell example

```powershell
Invoke-RestMethod 'https://localhost:5001/unittest' -Method Put
```

### Scan for Trent and Mallory

HTTP example (Trent)

```http
POST /scan/graph/property/0 HTTP/1.1
Host: localhost:5001
Content-Type: application/json
Content-Length: 148

{
    "operator": 0,
    "literal": {
        "value": "Trent",
        "fullQualifiedTypeName": "System.String"
    },
    "resultType": 0
}
```

PowerShell example (Trent)

```powershell
$body = @'
{
    "operator": 0,
    "literal": {
        "value": "Trent",
        "fullQualifiedTypeName": "System.String"
    },
    "resultType": 0
}
'@
Invoke-RestMethod 'https://localhost:5001/scan/graph/property/0' -Method Post -ContentType 'application/json' -Body $body
```

cURL example (Mallory)

```bash
curl -L -X POST 'https://localhost:5001/scan/graph/property/0' -H 'Content-Type: application/json' --data-raw '{
    "operator": 0,
    "literal": {
        "value": "Mallory",
        "fullQualifiedTypeName": "System.String"
    },
    "resultType": 0
}'
```

Response

```json
[4]
```

### Calculate the paths between Trent and Mallory

Trent = 4

Mallory = 3

HTTP example

```http
POST /path/4/to/3 HTTP/1.1
Host: localhost:5001
Content-Type: application/json
Content-Length: 2

{}
```

PowerShell example

```powershell
Invoke-RestMethod 'https://localhost:5001/path/4/to/3' -Method Post -ContentType 'application/json' -Body '{}'
```

cURL example

```bash
curl -L -X POST 'https://localhost:5001/path/4/to/3' -H 'Content-Type: application/json' --data-raw '{}'
```

Response

```json
[
  {
    "pathElements": [
      {
        "sourceVertexId": 4,
        "targetVertexId": 0,
        "edgeId": 7,
        "edgePropertyId": 11,
        "direction": 0,
        "weight": 0
      },
      {
        "sourceVertexId": 0,
        "targetVertexId": 3,
        "edgeId": 10,
        "edgePropertyId": 12,
        "direction": 0,
        "weight": 0
      }
    ],
    "totalWeight": 0
  },
  {
    "pathElements": [
      {
        "sourceVertexId": 4,
        "targetVertexId": 1,
        "edgeId": 8,
        "edgePropertyId": 11,
        "direction": 0,
        "weight": 0
      },
      {
        "sourceVertexId": 1,
        "targetVertexId": 3,
        "edgeId": 11,
        "edgePropertyId": 12,
        "direction": 0,
        "weight": 0
      }
    ],
    "totalWeight": 0
  }
]
```

## Subgraphs

A **subgraph** is a pattern-matched subset of the graph, extracted into a new, standalone
Fallen-8 instance. You give it optional vertex/edge pre-filters and an ordered pattern
(alternating vertex/edge), and the engine keeps only the elements that lie on a matching
path. The example below matches `person -knows-> person` and prunes everything else (the
company vertex and its `works_at` edge):

![A subgraph extracted from a source graph by matching a person-knows-person pattern; the company vertex and works_at edge are pruned.](./pics/subgraph-illustration.svg)

Over REST, filters are C# code fragments compiled at runtime (just like the path API):

```jsonc
PUT /subgraph
{
  "name": "people-who-know-people",
  "patterns": [
    { "type": "Vertex", "patternName": "p1", "vertexFilter": "return (v) => v.Label == \"person\";" },
    { "type": "Edge",   "patternName": "knows", "direction": "OutgoingEdge", "edgePropertyFilter": "return (p) => p == \"knows\";" },
    { "type": "Vertex", "patternName": "p2", "vertexFilter": "return (v) => v.Label == \"person\";" }
  ]
}
```

Subgraphs can be listed, read, recalculated against their (possibly changed) source, deleted,
and nested (a subgraph of a subgraph). See [features/subgraph/](features/done/subgraph/) for the
full specification and REST reference.

## Stored queries

Instead of shipping C# fragments on every request, you can register a **stored query** once —
compiled and validated at registration — and reference it by name from the path and subgraph
endpoints afterwards:

```jsonc
POST /storedquery
{
  "name": "adults-shortest",
  "kind": "Path",
  "path": { "filter": { "vertexFilter": "return (v) => v.TryGetProperty(out int age, \"age\") && age > 30;" } }
}

POST /path/1/to/5
{ "storedQuery": "adults-shortest", "maxDepth": 5 }
```

The security payoff: **registration requires the dynamic-code switch, invocation does not.**
Register a vetted set while `Fallen8:Security:EnableDynamicCodeExecution=true` (a provisioning
window), then run day-to-day with the switch off — inline fragments are rejected while stored
queries keep working, so the code surface shrinks from "arbitrary C# per request" to a closed,
operator-approved set. (Honesty note: a stored query still runs in-process with full trust;
this narrows who can *introduce* code, it is not a sandbox.) Stored queries survive save/load
and crash-recovery via the write-ahead log. See
[features/stored-query-library/](features/done/stored-query-library/) for the full
specification.

## Bulk export/import (JSONL)

Move whole graphs as plain, `grep`-able data — `GET /bulk/export` streams the graph as
newline-delimited JSON (typed property values, so everything round-trips exactly), and
`POST /bulk/import` streams it back into an empty instance with fresh ids:

Bash

```bash
curl -sf http://localhost:5000/bulk/export -o graph.jsonl
curl -sf -X POST http://localhost:5000/bulk/import -H "Content-Type: application/x-ndjson" --data-binary @graph.jsonl
```

PowerShell

```powershell
Invoke-RestMethod 'http://localhost:5000/bulk/export' -OutFile graph.jsonl
Invoke-RestMethod 'http://localhost:5000/bulk/import' -Method Post -ContentType 'application/x-ndjson' -InFile graph.jsonl
```

See [features/bulk-import-export/](features/done/bulk-import-export/) for the line schema,
consistency contract, and error semantics.

## Live change feed

`GET /changefeed` streams committed mutations as **Server-Sent Events** — in commit order,
with declarative server-side filters (no code fragments, works with the dynamic-code switch
off) and an in-memory catch-up buffer:

Bash

```bash
curl -N "http://localhost:5000/changefeed?kinds=vertexCreated,vertexRemoved&labels=person"
```

PowerShell (`curl.exe`, shipped with Windows — the bare `curl` alias buffers instead of streaming)

```powershell
curl.exe -N "http://localhost:5000/changefeed?kinds=vertexCreated,vertexRemoved&labels=person"
```

Events carry ids, labels and property keys — never property values. Whenever continuity is
lost (slow consumer, restart, trim/load), the stream says so in-band with a `resync` event;
the client recipe is always "fetch, then stream; on resync, re-fetch". See
[features/change-feed/](features/done/change-feed/) for the event schema, filter grammar,
and measured write-throughput non-regression.

## Graph analytics

A third plugin-discovered algorithm family runs whole-graph analytics over the in-memory
adjacency: **PageRank**, **weakly connected components**, **label propagation** communities,
**degree centrality** and **triangle counting** — synchronously under a wall-clock budget,
with deterministic, hand-verifiable semantics and no dynamic code:

Bash

```bash
curl -sf -X POST http://localhost:5000/analytics/PAGERANK \
     -H "Content-Type: application/json" \
     -d '{ "vertexLabel": "person", "maxResults": 10 }'
```

PowerShell

```powershell
Invoke-RestMethod 'http://localhost:5000/analytics/PAGERANK' -Method Post -ContentType 'application/json' `
                  -Body '{ "vertexLabel": "person", "maxResults": 10 }'
```

Responses are bounded (top-K / partition summaries); the full per-vertex result lands as
vertex properties via the opt-in write-back (`"writeBack": true`, snapshot-durable). See
[features/graph-analytics/](features/done/graph-analytics/) for all five algorithms, the
budget/status-code contract, and the consistency story.

## Vector search (kNN over embeddings)

A `VectorIndex` gives exact k-nearest-neighbour search over `float[]` embeddings — cosine,
dot product, or L2, SIMD brute-force, deterministic ordering. Create it through the normal
index surface, add vectors (explicitly or from a `float[]` element property), then query:

Bash

```bash
curl -sf -X POST http://localhost:5000/scan/index/vector \
     -H "Content-Type: application/json" \
     -d '{ "indexId": "embeddings", "query": [0.1, 0.2, 0.3], "k": 10, "label": "person" }'
```

PowerShell

```powershell
Invoke-RestMethod 'http://localhost:5000/scan/index/vector' -Method Post -ContentType 'application/json' `
                  -Body '{ "indexId": "embeddings", "query": [0.1, 0.2, 0.3], "k": 10, "label": "person" }'
```

Hits are graph elements, so the traversal surface takes over from there — the GraphRAG
recipe (kNN → paths/subgraphs/properties), memory math, and measured latency live in
[features/vector-index/](features/done/vector-index/).

## Semantic traversal (embeddings as element state)

Embeddings can live **on the elements themselves** (named, WAL-durable, one typed
accessor), and the traversal surface can then make decisions by similarity: a `semantic`
block on `POST /path/{from}/to/{to}` and `PUT /subgraph` carries a query vector — embedded
once, before the traversal starts — and installs code-free similarity filters/costs, so
the common case runs with dynamic code execution disabled:

Bash

```bash
curl -sf -X PUT "http://localhost:5000/graphelement/42/embedding/default?waitForCompletion=true" \
     -H "Content-Type: application/json" -d '{ "vector": [0.12, -0.5, 0.33] }'

curl -sf -X POST http://localhost:5000/path/1/to/9 \
     -H "Content-Type: application/json" \
     -d '{ "semantic": { "queryVector": [0.1, 0.2, 0.3], "minScore": 0.7 } }'
```

PowerShell

```powershell
Invoke-RestMethod 'http://localhost:5000/graphelement/42/embedding/default?waitForCompletion=true' -Method Put `
                  -ContentType 'application/json' -Body '{ "vector": [0.12, -0.5, 0.33] }'
Invoke-RestMethod 'http://localhost:5000/path/1/to/9' -Method Post -ContentType 'application/json' `
                  -Body '{ "semantic": { "queryVector": [0.1, 0.2, 0.3], "minScore": 0.7 } }'
```

A `VectorIndex` bound to an embedding name (`embeddingName` creation option) maintains
itself as a derived projection of element state — no explicit adds, rebuilt on load,
correct after WAL replay. An optional, capability-gated **embedding provider** in the
server (ONNX / LLamaSharp / Ollama behind `Microsoft.Extensions.AI`, off by default — the
deployment stays model-free) adds text-in workflows: `POST /embedding/element`,
`POST /embedding/search`, and `semantic.queryText`. The full story — the traversal rules
and diagram, metric/cost semantics, memory math, backend matrix, the model-identity
contract — lives in [features/element-embeddings/](features/done/element-embeddings/) and
[features/embedding-provider/](features/done/embedding-provider/).

**In F8 Studio**, none of this needs a curl: the Browser's element inspector has an
**Embeddings** tab (set/replace/remove a named embedding by pasted vector, or by text when
the provider is on); the Query screen creates **bound** vector indices, badges them in the
inventory, and offers **semantic search** by text; and the Path and Subgraph screens carry
a **semantic block** editor — the code-free similarity filter/cost that works with dynamic
code execution off — that mirrors the server's one-owner-per-slot rules in the UI before a
request is ever sent. See [features/studio-semantics/](features/done/studio-semantics/).

## Observability

The engine emits metrics and traces through the BCL instruments (no engine dependency);
the server wires OpenTelemetry **opt-in** — a default configuration runs zero OTel code:

```jsonc
"Fallen8": { "Observability": { "Prometheus": { "Enabled": true } } }
```

`GET /metrics` then serves commit latency, queue depth, WAL degradation, checkpoint
durations, element/index gauges and codegen cache stats in Prometheus format (plus the
built-in HTTP/runtime meters); an OTLP endpoint adds traces whose transaction spans parent
to the enqueuing HTTP request across the writer thread. `GET /statistics` returns a budgeted
graph-shape snapshot (label/property cardinalities, degree percentiles, index inventory),
and `/healthz` + `/readyz` cover probes. See
[features/observability/](features/done/observability/) for the metric reference, scrape
config, and the measured (noise-level) overhead.

## Troubleshooting

### Delegate model fails to load (HTTP 404 in UI)

**Symptom**: Using the "built-in (Local Ollama)" backend in F8 Studio's delegate editor
returns "Model endpoint returned HTTP 404."

**Cause**: the `phi4-f8-mini` model is not in the Ollama container's volume yet — usually
because the first-start pull has not finished, or it failed (no internet to
`registry.ollama.ai`). The container reuses the `f8-ollama-models` volume, **not** any Ollama
you have installed on the host; the two caches are separate, so pulling on the host does not
help the container.

**Fix**:

```bash
npm run env:logs                 # is the pull still running, or did it error?
```

- Still pulling → wait; the UI works as soon as it finishes (a few GB on first start).
- Errored (no internet in the container) → pre-seed the volume from a machine with internet,
  then restart. This needs only Docker (no host Ollama):

  ```bash
  scripts/ensure-models.sh       # pulls phi4-mini + phi4-f8-mini INTO volume f8-ollama-models
  npm run env:down && npm run env:up
  ```
  ```powershell
  bash scripts/ensure-models.sh  # the seed script is bash (Git for Windows ships it)
  npm run env:down; npm run env:up
  ```

- Meanwhile you can switch the delegate editor's backend to the **"Ollama (stock phi4-mini)"**
  preset if `phi4-mini` pulled but `phi4-f8-mini` did not.

To seed a different published fine-tune, set `F8_DELEGATE_REPO` (it is tagged locally as
`phi4-f8-mini` either way):

```bash
F8_DELEGATE_REPO=you/your-finetune scripts/ensure-models.sh
```
```powershell
$env:F8_DELEGATE_REPO = "you/your-finetune"; bash scripts/ensure-models.sh
```

### GPU acceleration (and the NVIDIA + WSL box)

GPU is auto-detected (`nvidia-smi` present → Ollama gets the GPU via `docker-compose.gpu.yml`):

```bash
npm run env:up          # auto-detect (recommended)
F8_GPU=0 npm run env:up # force CPU-only even if a GPU is present
F8_GPU=1 npm run env:up # force GPU (fails to create the container if it is unavailable)
```
```powershell
npm run env:up                          # auto-detect (recommended)
$env:F8_GPU = "0"; npm run env:up       # force CPU-only even if a GPU is present
$env:F8_GPU = "1"; npm run env:up       # force GPU (fails to create the container if unavailable)
Remove-Item Env:F8_GPU                  # clear the override for later auto-detect runs
```

**On Windows + NVIDIA GPU + WSL2 (Ubuntu 24.04):** the GPU reaches the container through the
NVIDIA Container Toolkit — install it *in the place the Docker engine runs*:

- **Docker Desktop (WSL2 backend):** install the current NVIDIA driver on Windows and confirm
  `nvidia-smi` works inside your WSL distro. Docker Desktop's WSL2 backend then exposes the GPU
  automatically — there is no separate toggle. `npm run env:up` from Windows or WSL detects it.
- **Docker running natively inside the Ubuntu 24.04 distro:** install
  [`nvidia-container-toolkit`](https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/latest/install-guide.html)
  in that distro and `sudo nvidia-ctk runtime configure --runtime=docker && sudo systemctl restart docker`.

Verify the GPU actually reaches a container before `env:up` (note `--entrypoint nvidia-smi`:
the `ollama/ollama` image's entrypoint is `ollama`, so a bare trailing `nvidia-smi` would be
parsed as an ollama subcommand and fail):

```bash
docker run --rm --gpus all --entrypoint nvidia-smi ollama/ollama:latest   # should list your GPU
```

If this fails, the GPU is not exposed to Docker (missing/misconfigured toolkit); the stack
still runs CPU-only with `F8_GPU=0`. AMD GPUs need the `ollama/ollama:rocm` image and are not
covered by this compose.

## Additional information

[Graph databases - Henning Rauch](http://www.slideshare.net/HenningRauch/graphdatabases)

[Graphendatenbanken - Henning Rauch (visiting lecture)](http://www.slideshare.net/HenningRauch/vorlesung-graphendatenbanken-an-der-universitt-hof)

[Issues on GitHub](https://github.com/cosh/fallen-8/issues)

[Wiki on GitHub](https://github.com/cosh/fallen-8/wiki)

[Google Group](https://groups.google.com/d/forum/fallen-8)

## MIT-License

Copyright (c) 2025 Henning Rauch

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,

FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
