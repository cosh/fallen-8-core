# Capacity bench: implementation plan

Landed in one pass, in this order, because each step is the input to the next.

## 1. The schema first

`fallen-8-bench/capacity-report.schema.json`. Written before any measuring code, because it is the
contract every other piece keys on: the tool emits it, the renderer consumes it, and a third party
validates against it. Required objects: `tool`, `source`, `environment`, `profile`, `metrics`, with
`metrics` carrying the four families. `schemaVersion` is `const: "1"` so a consumer can refuse an unknown
major.

## 2. The tool

- `CapacityReport.cs`: the result model, one type per schema object, `JsonPropertyName` on every member so
  the emitted names are the schema's names and not the C# ones.
- `GraphBuilder.cs`: deterministic construction (fixed seed, fixed creation stamp) plus the retained-bytes
  reading. Determinism matters more than realism: a number that cannot be repeated elsewhere is not
  comparable.
- `Measurements.cs`: the four measurements, each small enough to read in one sitting, because a reader has
  to be able to see exactly what was timed.
- `Program.cs`: argument parsing (`--output`, `--profile`, `--runner-label`), the profile's shapes, the
  environment and source capture, and the JSON write.

Two traps handled explicitly, both of which produce plausible-looking wrong numbers:

- **Construct the engine before taking the memory baseline.** Otherwise the engine's fixed cost lands in
  the per-vertex delta, and inflates it more the smaller the scenario.
- **Discard the first save.** It pays first-touch costs a steady-state instance would not.

## 3. The renderer

`scripts/update-capacity-doc.mjs`. Validates the report structurally (major version, required objects,
finite numbers in every row) and names the offending path when something is off, so a malformed report
fails here instead of producing a silently wrong page. Then splices five generated regions:
`environment`, `memory`, `writes`, `save`, `traversal`. Zero dependencies, so it runs anywhere Node does.

## 4. The page

`docs/src/content/docs/capacity-and-performance.md` keeps all of its prose and gives up all of its numbers.
The interpretation stays hand-written and machine-independent ("per-edge cost falls as degree rises"),
while every figure comes from the regions. The page opens with how to run the tool yourself.

## 5. Wiring

- `fallen-8-core.sln`: add the project so it builds and its warnings are gated with everything else.
- `package.json`: `bench:capacity`, `bench:capacity:quick`, `docs:capacity`, `docs:capacity:check`.
- `.github/workflows/capacity.yml`: manual dispatch, measure, render, build the docs site as the gate,
  upload the artifact, optionally commit back.

## Verification

- `dotnet build fallen-8-core.sln` clean at zero warnings, with the new project in the solution.
- Full test suite green (the tool has no tests of its own: it IS a measuring instrument, and asserting a
  throughput number would either be tautological or flaky. The renderer's validation is what protects the
  page, and the docs build is what protects the markdown).
- The tool run end to end on a real machine, the report inspected by hand for sane values, and the page
  rendered from it.
- `npm --prefix docs run build` green, so the generated markdown produces valid pages and links.
