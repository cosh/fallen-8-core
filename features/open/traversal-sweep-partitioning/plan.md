# Traversal sweep partitioning: implementation plan

Small feature, three phases, branch `feature/traversal-sweep-partitioning` per the workflow.

## Phase 1: the partition default (S1)

`fallen-8-core/Algorithms/Traversal/OutEdgeSweep.cs`:

- `DefaultPartitionSize` becomes `Math.Max(256, vertexCount / (Environment.ProcessorCount * 16))`.
- Rewrite its XML doc to state the real rule and why: sixteen ranges per core sits on the measured
  plateau (spec, finding 1), gives the dynamic partitioner room to balance degree skew, and the
  256-vertex floor bounds per-range dispatch on tiny graphs. The old comment claimed every core stays
  busy while producing `P / 1.5` ranges; do not restate the history, state the rule.

Test: extend the existing benchmark-endpoint coverage with a pin on the default, in the
`AuditDefect*` style: for a representative count and the current processor count, the range count
`ceil(vertices / DefaultPartitionSize(vertices))` is at least `ProcessorCount` once the graph has more
than `256 * ProcessorCount` vertices, and `DefaultPartitionSize` never returns less than 1 for any
non-negative input (0, 1, 255, 256 boundaries). The throughput itself is not asserted: rates are not
testable, shapes are.

## Phase 2: bench-tool heap hygiene (S2)

`fallen-8-bench/Measurements.cs`: at the top of `Traversal`, before the engine is created, force a full
compacting collection (`GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce`,
then the collect, finalizer drain, collect sequence `GraphBuilder.RetainedBytes` already performs).
Comment it with the measured reason: same code, same shape, 312M e/s inside a full run against 380M
standalone (spec, finding 3). No test: it is measurement hygiene in a tool whose output is a report.

## Phase 3: refresh the published numbers

1. `dotnet build` clean, full suite green, `npm run docs:capacity:check` still failing is expected at
   this point (stale numbers), which is the reason for this phase.
2. Re-run `npm run bench:capacity -- --runner-label "<machine>"` on the machine whose numbers the page
   publishes (the Ryzen for the stated 500M target, this workstation otherwise), then
   `npm run docs:capacity`, and commit the report plus the rendered page with the code so the numbers
   and the code that produced them land together.
3. Docs site build green (link gate).

## Gates

- Zero warnings, full MSTest suite green.
- No OpenAPI snapshot change expected: if the snapshot moves, something was touched that this plan does
  not cover, stop and look.
- The sweep's traversed count is behaviour and must not move: the existing endpoint tests already pin
  it against known graphs.
