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
   - **traversal**: raw out-edge traversal throughput, the in-process equivalent of `GET /benchmark`

   Two profiles: `quick` (CI-sized) and `full` (the larger graphs the page's guidance is about).

2. **`capacity-report.schema.json`**, the result-file contract. It is a JSON Schema 2020-12 document
   shipped next to the tool, so a third party can validate a report without cloning the repo. Its
   defining property: the file carries the **environment** (OS, CPU, processor count, runtime, GC mode,
   runner label) and the **source** (engine version, commit, dirty-tree flag) alongside the metrics.
   A report cannot exist without saying which machine it describes.

3. **`scripts/update-capacity-doc.mjs`**, which validates a report and renders it into five generated
   regions of the docs page, delimited by `<!-- capacity:<name> -->` markers. Prose outside the regions
   is never touched, so the page stays writable by hand. `--check` fails when the page is stale.

4. **`.github/workflows/capacity.yml`**, manual-dispatch, which measures on a runner, renders, builds the
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
