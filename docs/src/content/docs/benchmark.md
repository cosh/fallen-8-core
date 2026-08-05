---
title: "Benchmark"
description: "Measure raw edge-traversal throughput over whatever graph is loaded: generate a graph or point it at a sample, then run timed passes that follow every out-edge."
---

The **Benchmark** section of [F8 Studio](/fallen-8-core/studio/) measures one thing: how fast
Fallen-8 traverses edges in memory. It follows every outgoing edge of every vertex in the
currently loaded graph, regardless of edge type
([edge type vs label](/fallen-8-core/graph-model/#edge-type-vs-label)), and reports edges traversed
per second (TPS). This is raw traversal throughput, not query latency and not analytics timing.

The screen (route `/benchmarks`) is Fallen-8-level: rather than taking a namespace parameter, it
always generates into and measures the `default` namespace's graph on the active instance,
whatever namespace the switcher is on. It does not aggregate across namespaces.

## Running a benchmark

1. Open **Benchmark** in the left rail.
2. (Optional) Give it a graph to measure. You can point the benchmark at anything already
   loaded, a [sample](/fallen-8-core/samples/), a restored [save game](/fallen-8-core/save-games/),
   or your own data, so this step is only needed when the graph is empty. To conjure one, use the
   **Graph generation** panel: set `vertices`, `edges / vertex`, and a `distribution`, or click a
   preset, then **Generate**.
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

The screen calls two Fallen-8-level endpoints. Both are exposed at the API root as bare paths
(not under the versioned `api/v0.1` prefix) and operate on the `default` namespace:

| Method and route | Purpose |
|---|---|
| `GET /generate?nodeCount=&edgeCount=&distribution=` | Add a generated graph (returns a human-readable timing summary). |
| `GET /benchmark?iterations=` | Run the timed edge-traversal passes (returns the TPS statistics). |

All three `/generate` parameters are optional, with server-side defaults of 200 vertices, 5
out-edges per vertex, and `uniform`. A non-numeric or negative count, or a distribution other than
`uniform` or `preferential`, answers `400`.

`GET /benchmark` returns `iterations`, `edgesTraversed` (edges in a single pass), `averageTps`,
`medianTps`, and `standardDeviationTps`. `iterations` also defaults to 1000 when omitted. It
answers `400` on a graph with no vertices, and on a non-numeric or non-positive iteration count.
See the full contract in the [API reference](/fallen-8-core/api-reference/).

## See also

- [F8 Studio](/fallen-8-core/studio/) is the workbench this screen lives in.
- [Running Fallen-8](/fallen-8-core/running/) covers launch options and configuration that affect
  performance.
- [Architecture](/fallen-8-core/architecture/) shows how the engine and REST API fit together.
