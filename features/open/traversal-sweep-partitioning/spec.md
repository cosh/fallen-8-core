# Traversal sweep partitioning: specification

## Why

The Ryzen full-profile run did not reach the 500M edges/s target, so the sweep was profiled by
controlled experiment on the 16-logical-core Intel workstation (scratch harness over the public engine
API, best of 7 passes, average parallelism derived from process CPU time over wall time).

**Finding 1: the partition formula idles a third of the machine.** `OutEdgeSweep.DefaultPartitionSize`
is `(vertexCount / ProcessorCount) * 3 / 2`, inherited verbatim from the original `ScaleFreeNetwork`
code. A range 1.5x the per-core share yields `ProcessorCount / 1.5` ranges: 11 ranges for 16 workers
here, and 21 ranges for 32 workers on the Ryzen, so its default run leaves 11 hardware threads with
nothing to do. Its own XML doc claims "enough ranges to keep every core busy", which is false. Measured
at the headline shape (10M vertices, 100M edges, one process, one graph, only the range size varied):

| Partitioning | Ranges | Rate | Avg parallelism (of 16) |
| --- | --- | --- | --- |
| default | 11 | 380.7M e/s | 10.0 |
| 8 ranges per core | 128 | 420.9M e/s | 13.1 |
| 16 ranges per core | 257 | 422.0M e/s | 12.6 |
| 32 ranges per core | 513 | 420.1M e/s | 12.9 |

An 11% gain here, from a flat plateau at 8 ranges per core and up. The gain should be larger on the
Ryzen: 21 of 32 workers is 66% occupancy, and the idle 11 are SMT siblings, which are worth the most on
exactly this kind of latency-bound loop (each hardware thread keeps its own misses in flight).

**Finding 2: the inner loop is already at the memory wall.** The cost ladder at 20M edges, marginal
cost per rung: the walk itself (partitioning, span lookup, degree read) is 12% of the pass; loading the
edge objects is 40% (allocation-ordered, so the hardware prefetcher mostly covers them); loading the
target vertex is 48% (random access, one miss per edge). Nothing in the loop body is fat: it is
dependent memory loads.

**Finding 3: full-run heap history costs about 20%.** The committed full-run bench reported 312M e/s at
100M edges on this machine; the standalone harness, same code, same shape, same default partitioning,
reached 380M. The full run builds three traversal graphs (and earlier scenario graphs) in one process,
so the 100M graph lands in a heap shaped by everything before it. Partially confounded with machine
state, but the direction is consistent and the fix is cheap.

## Suggested changes

**S1 (engine, one line): fix `DefaultPartitionSize`.**
`Math.Max(256, vertexCount / (Environment.ProcessorCount * 16))`. Sixteen ranges per core sits on the
measured plateau and, for real user graphs, finer ranges also balance degree skew (a supernode-heavy
range no longer serializes a whole core's share). The 256-vertex floor keeps the range count bounded on
tiny graphs, where per-range dispatch would otherwise dominate a microsecond sweep. Correct the XML doc
at the same time. All three callers (the sweep default, `GET /benchmark`, `fallen-8-bench`) inherit the
fix; no signature, route, or behaviour change beyond speed, and the traversed count is identical.

**S2 (bench tool only): isolate traversal scenarios from heap history.** Before building each traversal
graph in `Measurements.Traversal`, force a full compacting collection (set
`GCSettings.LargeObjectHeapCompactionMode = CompactOnce`, then the same collect-drain-collect the
memory reading already uses). This narrows the standalone-vs-full-run gap so published numbers describe
the engine, not the allocator's mood. Engine untouched.

## Rejected, with the evidence

- **16-wide blocked lookahead in the inner loop:** +2% (45.4ms vs 46.5ms at 20M edges). The
  out-of-order engine already overlaps the misses; not worth the complexity.
- **Array/span fast path instead of `IReadOnlyList` indexing:** 0% (47.1ms vs 46.5ms). Dynamic PGO
  devirtualizes the indexer.
- **Software prefetch of target objects:** needs unsafe code in the engine for a hint the hardware
  mostly cannot use (the target address is itself behind a dependent load). Out of scope for
  non-intrusive.
- **CSR-style neighbour-id overlay:** the only change that removes the 48% target-load cost, and it is
  already a reasoned feature-level rejection (`features/done/csr-adjacency/assessment.md`). Not
  reopened here.

## Expected outcome, honestly

On this Intel box: 422M e/s at 100M edges (from 380M standalone, 312M as published). On the 32-thread
Ryzen the occupancy deficit is larger, so the gain should be too; whether it crosses 500M depends on its
memory subsystem, and no number is promised that was not measured. If the tuned sweep still falls
short there, the remaining distance is memory latency per edge, and the honest options are the rejected
overlay or a different target.

## Impact on existing features

- **capacity-bench:** the committed report and page numbers go stale the moment S1 lands; refresh them
  as part of the same change (plan phase 3), preferably from the Ryzen since it is the machine the
  target is stated for.
- **schema-agnostic-benchmark:** `GET /benchmark` gets faster with unchanged semantics; its docs page
  makes no throughput claim, so nothing to update there.
- No REST contract, OpenAPI, MCP, or Studio impact. No retrain-log entry (no fragment surface change).
