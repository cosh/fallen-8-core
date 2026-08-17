# Capacity bench: specification

## Why

The Capacity and performance page was first written with numbers lifted out of feature specs, all of them
measured once on one developer machine. Published that way they read like product guarantees: a reader had
no way to tell whether "111 B per edge" was a property of Fallen-8 or a property of somebody's laptop, and
no way to check.

A capacity number is only meaningful next to the hardware that produced it. So the numbers stop being
prose and become the output of a tool anyone can run.

## What ships

1. **`fallen-8-bench`**, a console project referencing the engine, that measures four families:
   - **memory**: retained managed bytes per vertex and per edge at several average degrees
   - **writeThroughput**: committed single-element writes per second, WAL on, serial and concurrent
   - **saveStall**: how long a checkpoint holds the single writer thread, by graph size
   - **traversal**: raw out-edge traversal throughput at several graph sizes, through the same engine
     primitive `GET /benchmark` uses

   A fifth family, **load** (how long restoring one namespace's checkpoint takes, on the same shapes as
   `saveStall`), was added later by [namespace-startup-load](../namespace-startup-load/); it is the one
   family the schema leaves optional, for the reason recorded there.

   Two profiles. `full` peaks at the headline shape of **10,000,000 vertices and 100,000,000 edges**,
   which retains around 13 GB, so it wants a machine with 32 GB or more and takes tens of minutes, most
   of it building edges. `quick` is sized for CI but is deliberately not tiny: see the sizing note below.
   `--only <family>` runs one family, for iterating on a single measurement.

2. **`OutEdgeSweep` in the engine**, the one home for the traversal sweep, shared by `GET /benchmark` and
   this tool so a user measuring either gets the same code path. It lives in the engine because that is
   the only place the allocation-free adjacency enumerator is reachable: an out-of-assembly caller has to
   use `VertexModel.GetOutgoingEdgeIds()`, which allocates a key list **per vertex**. On a
   ten-million-vertex graph that was ten million allocations per pass, inside the measurement.

3. **`capacity-report.schema.json`**, the result-file contract. It is a JSON Schema 2020-12 document
   shipped next to the tool, so a third party can validate a report without cloning the repo. Its
   defining property: the file carries the **environment** (OS, CPU, processor count, runtime, GC mode,
   runner label) and the **source** (engine version, commit, dirty-tree flag) alongside the metrics.
   A report cannot exist without saying which machine it describes.

4. **`scripts/update-capacity-doc.mjs`**, which validates a report and renders it into five generated
   regions of the docs page, delimited by `<!-- capacity:<name> -->` markers. Prose outside the regions
   is never touched, so the page stays writable by hand. `--check` fails when the page is stale.

5. **`.github/workflows/capacity.yml`**, manual-dispatch, which measures on a runner, renders, builds the
   docs site as the gate, uploads the report as an artifact, and optionally commits the result back.

## Contract

The schema's major version (`schemaVersion: "1"`) is the compatibility boundary. A consumer must refuse a
major it does not know; the renderer does. Adding an optional field is a minor change and needs no bump;
removing a field, renaming one, or changing a unit is a major change.

**Units are fixed by the schema and are not negotiable per report:** bytes for `bytesPer*`, megabytes for
`retainedMb`, milliseconds for `writerHoldMs`, per-second rates for `writesPerSecond` and
`edgesPerSecond`.

The measurement **shapes** (vertex counts and degrees per profile) are part of what a published row means.
Changing a shape changes the meaning of a table row, so it is a deliberate, called-out change, not a tuning
knob.

## Deliberate limits

- **A shared CI runner is not a performance reference.** It is virtualised and noisy. The workflow uses one
  because it is reproducible and always available, and the page prints the runner label so nobody mistakes
  it for hardware guidance. Real numbers come from a local `full` run.
- **The tool measures the engine in-process, not the REST surface.** HTTP, serialization and ASP.NET are
  out of scope here; `GET /benchmark` and the Studio Benchmark screen cover the served path.
- **A measurement has a minimum useful size.** The first version of this tool traversed 100,000 edges,
  which completes in under a millisecond: the reported rate swung between 186M and 457M edges/s across
  passes on one machine, because it was measuring stopwatch resolution and thread-pool ramp, not
  throughput. Every scenario is now sized so one pass takes tens of milliseconds. This is why `quick` is
  not as small as it could be.
- **Traversal is measured at several sizes, not one.** Following an edge is a chain of dependent loads
  (adjacency slot, edge object, neighbour), so the rate is governed by whether the working set fits in
  cache. A single figure would either flatter the engine (small graph) or understate it (large graph);
  the curve is the honest answer, and the drop across it is memory latency rather than the engine.
- **`quick` numbers are not comparable to `full` numbers.** Different graph sizes amortise fixed costs
  differently, most visibly in `bytesPerVertex`. Compare like with like, which is why the profile is in the
  report and printed on the page.
- **No regression gate.** This feature publishes numbers; it does not fail a build when they move. A
  threshold gate on a noisy shared runner would produce false alarms, and on real hardware there is no
  runner to gate. Revisit if a dedicated machine ever runs it on a schedule.

## Impact on existing features

- **docs-site:** the Capacity and performance page changes from hand-written numbers to generated regions.
  The docs build remains the gate on the rendered output.
- **schema-agnostic-benchmark:** unchanged and complementary. `GET /benchmark` and the Studio screen
  measure a loaded graph over HTTP; this tool measures the engine's own cost from a known graph. The page
  links to the screen for the interactive case.
- **The `[Ignore]`-marked benchmark harnesses in `fallen-8-unittest`** (`MemoryFootprintBenchmark`,
  `AdjacencyMemoryBenchmark`, `WritePathThroughputBenchmark`, `NonBlockingSaveBenchmark`, and the rest)
  stay as they are. They remain the place to profile a specific change inside the test host, with the
  measurement techniques this tool reuses (forced blocking collection for retained bytes; a discarded
  warm-up save; single-element writes for the group-commit gap). This tool is now the single source for the
  **published** numbers. If that split ever drifts, fold the harnesses into this project rather than
  letting two implementations disagree.
- **code-quality:** `fallen-8-bench` is not in the convention test's product-project list, so its console
  output is sanctioned. Its sources still carry the MIT header and it builds under the same
  warnings-as-errors bar.
