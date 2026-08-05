---
title: "Capacity and performance"
description: "What a graph costs in RAM, how fast writes commit, what a save game stalls, and how to measure all of it on your own hardware."
---

Fallen-8 keeps the whole graph resident, so two questions come before any other: how much memory does my
graph need, and how fast can I write to it. This page answers both, and it answers them the only way a
capacity question can honestly be answered: with a measurement, from a named machine, that you can repeat
on yours.

## Measure it yourself

The numbers below are not hand-written. They are produced by
[`fallen-8-bench`](https://github.com/cosh/fallen-8-core/tree/main/fallen-8-bench), a console tool in the
repository that measures memory, write throughput, checkpoint stalls and traversal speed, then writes a
result file:

```bash
# quick is sized for CI; full uses the larger graphs this page's guidance is really about
dotnet run --project fallen-8-bench -c Release -- --profile full --runner-label "my box"
```

That writes `fallen-8-bench/results/capacity-report.json`, conforming to
[`capacity-report.schema.json`](https://github.com/cosh/fallen-8-core/blob/main/fallen-8-bench/capacity-report.schema.json).
The schema is the contract: it carries the metrics **and** the environment that produced them, because a
capacity figure with no hardware attached is a fact about somebody's laptop, not about Fallen-8. Rendering
a report into this page is one command, and a GitHub Action does exactly this:

```bash
node scripts/update-capacity-doc.mjs fallen-8-bench/results/capacity-report.json
```

Run it against your own hardware and compare. If your numbers differ from these by a lot, the machine
description below is the first place to look, not the engine.

<!-- capacity:environment -->

The numbers on this page come from one recorded run of that tool. They describe **that machine**:

| | |
| --- | --- |
| Machine | dev workstation (Windows, 16 cores) |
| CPU | Intel64 Family 6 Model 197 Stepping 2, GenuineIntel, 16 logical processors |
| Memory | 97,717 MB available to the runtime |
| OS | Microsoft Windows 10.0.26200 (X64) |
| Runtime | .NET 10.0.10, server GC on |
| Engine | 0.3.0.0, commit `6cbbfc4262` (uncommitted changes present) |
| Profile | `quick` |
| Measured | 2026-08-05 13:07 UTC |

<!-- /capacity:environment -->

## Memory: what a graph costs

Measured as **retained managed heap** attributable to the graph, after a forced blocking compacting
collection, with the engine's own fixed cost excluded. Process RSS is higher: it also carries the runtime,
the GC's free space and, in the service, ASP.NET.

<!-- capacity:memory -->

| Graph | Retained | Per vertex | Per edge (adjacency included) |
| --- | --- | --- | --- |
| 20,000 vertices, 40,000 edges (avg degree 2) | 9.0 MB | 128.5 B | 171.4 B |
| 20,000 vertices, 200,000 edges (avg degree 10) | 25.0 MB | 128.2 B | 118.4 B |
| 10,000 vertices, 200,000 edges (avg degree 20) | 22.5 MB | 129.9 B | 111.2 B |

<!-- /capacity:memory -->

Two things to read from this, which hold regardless of the machine:

- **A bare vertex has a flat cost.** Properties are on top and dominate quickly: a handful of string
  properties per element outweighs the structural cost.
- **Per-edge cost falls as degree rises.** Each edge-property group is one contiguous `EdgeModel[]`, and a
  vertex with a single group carries no dictionary at all, so the fixed part of the adjacency amortises
  over more edges as vertices get busier.

For rough planning, take the per-edge figure at your expected degree, add the per-vertex figure, and add
your properties. A vertex whose out-degree changes constantly can transiently hold spare capacity in its
group array (bounded at roughly twice the group), so a heavy-churn graph sits slightly above the table.

[Vector](/fallen-8-core/vector-search/) indexes are the one component with a formula rather than a
measurement. Vectors are held in one flat `float[]`, so the cost is roughly `4 x dimensions` bytes per
indexed element plus about 64 bytes of bookkeeping: `bge-m3` at 1024 dimensions costs about 4.1 KB per
indexed element, an order of magnitude more than the vertex it hangs off. Index only what you will search.

## Writes: throughput and the shape of a commit

Mutations are serialised through **one writer thread**, and with the write-ahead log on (the service
default) each commit group is fsync'd before the call returns. Two consequences dominate everything else.

**Batch, and concurrency pays.** A commit group amortises one fsync across everything drained into it, so
a stream of single-element transactions is the worst case and a batch transaction is the best. The
measurement below is deliberately the worst case, single-element writes with the WAL on:

<!-- capacity:writes -->

| Producers | Throughput |
| --- | --- |
| serial (1 producer) | 783 writes/s |
| 32 concurrent producers | 15,707 writes/s |

That is roughly 20.1x from group commit alone, measured over 4,000 single-element writes with the WAL on, and the serial latency floor is unchanged: a group of one still fsyncs immediately.

<!-- /capacity:writes -->

If you control the shape of your writes, prefer `CreateVerticesTransaction` and `CreateEdgesTransaction`
over per-element calls, or use [bulk import](/fallen-8-core/bulk-import-export/), which batches for you.

**A batch is all or nothing.** Ten thousand vertices in one transaction either all commit or none do, so
batching costs you nothing in atomicity.

## A save game stalls the writer

`PUT /save` runs on the same single writer thread and holds it for the entire save, serialize plus disk
I/O. Every mutation enqueued during a save waits:

<!-- capacity:save -->

| Graph size | Save duration (writer held) |
| --- | --- |
| 30,000 elements | 13.1 ms |
| 120,000 elements | 21.9 ms |

<!-- /capacity:save -->

This is a known, measured, deliberate trade-off: moving the save off the writer needs a consistent
point-in-time view of mutable element objects. The practical guidance follows from it. The WAL already
makes every commit durable, so **checkpoints do not have to be frequent**: save on a schedule that suits
your restore-point needs rather than out of fear of data loss, and avoid saving a very large graph in the
middle of a write burst. Reads are unaffected, because they never touch the writer.

## Reads: what is cheap and what is linear

| Operation | Cost |
| --- | --- |
| Element by id, degree, adjacency walk | O(1), lock-free against a published snapshot |
| [Index](/fallen-8-core/indexes/) point lookup | O(1) for a dictionary index |
| Range index scan | O(log n + k) against a cached ascending key array, rebuilt lazily after a key-set change |
| Fulltext index scan | Index-bounded, with scores |
| [Vector](/fallen-8-core/vector-search/) index scan | **Exact** SIMD brute force over every indexed element: linear in indexed elements, memory-bandwidth bound at roughly `4 x dimensions` bytes per candidate |
| `POST /scan/graph/property/{id}` and the all-property scan | **O(n) full scan, no index**, and deliberately sequential: the per-element predicate is too cheap to pay for partition and merge |
| [Analytics](/fallen-8-core/graph-analytics/) | Whole-graph, time-budgeted (default 30 s, max 300 s, one run at a time) |
| [Path finding](/fallen-8-core/path-finding/) | Frontier-bounded, and dominated by your filter fragments |

Readers never block writers and writers never block readers: the graph is published copy-on-write, so a
reader holds a consistent snapshot for the whole operation.

Raw out-edge traversal, the same work `GET /benchmark` does, on the machine described above:

<!-- capacity:traversal -->

| Graph | Passes | Out-edge traversal |
| --- | --- | --- |
| 10,000 vertices, 100,000 edges | 10 | 228,346,996 edges/s |

<!-- /capacity:traversal -->

That figure is memory-bandwidth bound and scales with cores, so it moves more between machines than any
other number here. The [Benchmark](/fallen-8-core/benchmark/) screen runs the same measurement against
whatever graph you have loaded.

## Knobs that actually move the numbers

| Knob | Effect |
| --- | --- |
| Batch your transactions | The single biggest write-throughput lever, worth the multiple measured above |
| `Fallen8:Durability:Volatile=true` | No WAL, no checkpoints, no boot load: the fastest possible writes, and a restart loses everything |
| `Fallen8:Durability:SaveOnShutdown` | `false` skips the final checkpoint and relies on WAL replay, trading a longer boot for a faster stop |
| Save frequency | Each save costs the stall measured above; the WAL is what makes rare saves safe |
| Server GC | On by default in the engine package, the service and the benchmark tool, and the right choice for a resident graph |
| `Fallen8:Analytics:MaxConcurrentRuns` | Defaults to 1, so a heavy analytics run cannot be stacked on itself |
| `Fallen8:BulkIO:ImportBatchSize` | Defaults to 10,000 elements per committed batch on import |

Configuration keys and their environment-variable forms are in
[Running Fallen-8](/fallen-8-core/running/#configuration-keys).

## What is deliberately not optimised

Honest limits, so you can plan around them rather than discover them:

- **The save stalls the writer** (above). Revisit territory is tens of millions of elements saved
  frequently.
- **Property scans are linear.** They are a discovery tool. Anything on a hot path wants an
  [index](/fallen-8-core/indexes/).
- **There is no compressed adjacency structure.** A CSR-style representation was assessed and
  deliberately rejected: edges here are first-class objects with their own ids, properties and index
  membership, and the graph is continuously mutated, so CSR would add a second structure to maintain
  without removing the objects that dominate the footprint.
- **Graph traversal is CPU-only.** GPU acceleration in Fallen-8 reaches only the model sidecars, never
  the graph itself.

## See also

- [Benchmark](/fallen-8-core/benchmark/): measure traversal throughput against a loaded graph from Studio
- [Save games](/fallen-8-core/save-games/): the WAL, checkpoints, and what survives a crash
- [Use as a library](/fallen-8-core/library/): in-process consumption, where you control GC and batching
- [Observability](/fallen-8-core/observability/): the metrics that show these costs on a live instance
- [Bulk import/export](/fallen-8-core/bulk-import-export/): the batched path for loading large datasets
