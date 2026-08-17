---
title: "Benchmark"
description: "Measure raw edge-traversal throughput over whatever graph is loaded: generate a graph or point it at a sample, then run timed passes that follow every out-edge."
---

The **Benchmark** section of [F8 Studio](/fallen-8-core/studio/) measures one thing: how fast
Fallen-8 traverses edges in memory. It follows every outgoing edge of every vertex in the
currently loaded graph, regardless of edge type
([edge type vs label](/fallen-8-core/graph-model/#edge-type-vs-label)), and reports edges traversed
per second (TPS). This is raw traversal throughput, not query latency and not analytics timing.

The screen (route `/q/{namespace}/benchmarks`) is **namespace-scoped**: it generates into, and
measures, the namespace the switcher is on, and the namespace is in the URL so a pasted link
restores it. It never aggregates across namespaces. The older flat `/benchmarks` link still works
and redirects to the active namespace's screen.

## Running a benchmark

1. Pick the [namespace](/fallen-8-core/namespaces/) you want to measure in the top bar, then open
   **Benchmark** in the left rail. The screen header names the instance and that namespace.
2. (Optional) Give it a graph to measure. You can point the benchmark at anything already
   loaded, a [sample](/fallen-8-core/samples/), a restored [save game](/fallen-8-core/save-games/),
   or your own data, so this step is only needed when the graph is empty. To conjure one, use the
   **Graph generation** panel: set `vertices`, `edges / vertex`, and a `distribution`, or click a
   preset, then **Generate**. The result reports what was created and, as **into namespace**, the
   graph the server actually wrote.
3. Set **iterations** (default 1000) in the **Edge-traversal throughput** panel and click
   **Run benchmark**.

Results appear as edges per run, average TPS, median TPS, and standard-deviation TPS, plus a
per-session **run history** table (in memory, not persisted) so you can compare runs as you
change the graph or the iteration count. More iterations tighten the median and standard
deviation.

## What the numbers mean

- **edges per run** equals the total edge count of the loaded graph (the sum of out-degrees).
  Each iteration follows every out-edge exactly once, dereferencing each edge's target vertex so
  the pass does real pointer-following work, not a cached-degree read.
- **average / median / stddev TPS** are computed over the per-iteration throughput samples. TPS
  is traversed edges per second.

The traversal is schema-agnostic: it walks every out-edge group of every vertex whatever the edge
type, and never looks at labels, so the number is comparable across generated graphs, samples, and
your own data.

## Graph generation

The generation panel is a convenience for producing a graph to measure. A few things to know:

- It writes into **the namespace the switcher shows**, whichever that is, and never falls back to
  `default` when it is some other one. The response names the namespace it wrote, so you can always
  tell from the result alone which graph grew.
- It is **additive**. Generated vertices and edges are added on top of the current graph; nothing
  is wiped. Generated edges only ever target vertices from the same call, so generating on top of a
  loaded sample leaves a second, disconnected component: generate into an empty graph when the
  numbers (or the analytics) should describe one dataset.
- `edges / vertex` is **per vertex**, so the total edge count is roughly `vertices x edges/vertex`.
  Targets are drawn distinct, so the out-degree is silently capped at the number of generated
  vertices (`nodeCount=200&edgeCount=500` yields 40,000 edges, not 100,000), and under
  `preferential` the earliest vertices get fewer by construction.
- **Presets:** *small* (200 vertices, 5 edges each, `uniform`), *medium* (10,000 x 10, `uniform`),
  and *scale* (100,000 x 10, `preferential`, roughly one million edges). A preset sets the
  distribution too, not just the counts. The *scale* preset is heavy: expect seconds of server work
  and real memory use.
- **distribution:** `uniform` spreads edges evenly (no hubs); `preferential` uses
  Barabasi-Albert-style attachment, so heavy-tailed hubs emerge and analytics such as
  [PageRank](/fallen-8-core/graph-analytics/) show real structure at scale.

Generated vertices and edges carry no label at all; the generated edges' type (`edgePropertyId`) is
`A`. That type is a generation detail only, and the benchmark traverses every edge whatever its
type, but it is the value to query a generated graph by, as in `GET /vertex/{id}/edges/out/A`.

## Performance notes

The benchmark is a CPU-parallel, in-memory traversal (a partitioned parallel scan sized to the
processor count). Throughput scales with CPU cores and memory bandwidth. There is no GPU code
path here: [GPU acceleration](/fallen-8-core/running/#gpu-acceleration) in Fallen-8 reaches only
the model sidecars (Ollama and the NLP enrichment tier), never graph traversal.

One call does `iterations x edges` traversals inside a single synchronous request, using every
core: at the *scale* preset's roughly one million edges, the default 1,000 iterations is about a
billion edge dereferences. `iterations` is capped server-side by
`Fallen8:Security:BenchmarkMaxIterations` (default `10000`); a higher count is a `400`, and an omitted
one uses 1,000 clamped to the ceiling. A pass still cannot be cancelled once it has started, so lower
the count (10 to 50) on million-edge graphs and do not benchmark an instance that is serving traffic.

## REST equivalents

The screen calls two endpoints. Both are exposed at the API root (not under the versioned
`api/v0.1` prefix) and both act on the namespace in the URL:

| Method and route | Purpose |
|---|---|
| `GET /ns/{namespace}/generate?nodeCount=&edgeCount=&distribution=` | Add a generated graph to that namespace (returns the generation result below). |
| `GET /ns/{namespace}/benchmark?iterations=` | Run the timed edge-traversal passes over that namespace (returns the TPS statistics). |

These two are the only namespace-scoped routes with **no bare-URL alias to `default`**. Everywhere
else `/vertex` means `/ns/default/vertex`; here a URL that names no namespace answers `400`
("Namespace required") and names the scoped form, because one operation writes a graph and the
other reports a graph's throughput as yours - picking a graph for you is the wrong answer in both
cases. Call `GET /ns/default/generate` when `default` is genuinely what you meant.

```bash
curl 'http://localhost:8080/ns/flights/generate?nodeCount=10000&edgeCount=10&distribution=preferential'
```

```json
{
  "namespace": "flights",
  "verticesCreated": 10000,
  "edgesCreated": 99945,
  "distribution": "preferential",
  "elapsedMilliseconds": 412.8,
  "vertexCountAfter": 10000,
  "edgeCountAfter": 99945
}
```

`verticesCreated` and `edgesCreated` are what this call added, counted rather than derived from the
arguments: targets are drawn distinct, so `edgesCreated` falls below `nodeCount x edgeCount`
whenever the requested out-degree exceeds the available targets, and under `preferential` it always
does. The 55 missing edges above are exactly that: vertex *i* can only attach to the *i* vertices
before it, so the total is `nodeCount x edgeCount - edgeCount x (edgeCount + 1) / 2`.
`vertexCountAfter` and `edgeCountAfter` are the namespace's totals once generation finished, which
differ from the created counts whenever the namespace already held data (here it was empty).

All three `/generate` parameters are optional, with server-side defaults of 200 vertices, 5
out-edges per vertex, and `uniform`. A non-numeric or negative count, or a distribution other than
`uniform` or `preferential`, answers `400`.

`GET /ns/{namespace}/benchmark` returns `iterations`, `edgesTraversed` (edges in a single pass),
`averageTps`, `medianTps`, and `standardDeviationTps`. `iterations` also defaults to 1000 when
omitted. It answers `400` on a graph with no vertices, and on a non-numeric or non-positive
iteration count. An unknown namespace is a `404`, and one this process did not load is a `503`.
See the full contract in the [API reference](/fallen-8-core/api-reference/).

## See also

- [Namespaces](/fallen-8-core/namespaces/) explains the `/ns/{name}/…` addressing this screen uses.
- [F8 Studio](/fallen-8-core/studio/) is the workbench this screen lives in.
- [Running Fallen-8](/fallen-8-core/running/) covers launch options and configuration that affect
  performance.
- [Architecture](/fallen-8-core/architecture/) shows how the engine and REST API fit together.
