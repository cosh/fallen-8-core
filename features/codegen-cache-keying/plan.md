# Codegen Cache Keying — Plan

Companion to [spec.md](./spec.md). Prove the waste first, then narrow the key, then hoist the
references, then tune expiration without breaking unload. Keep the collectible-ALC lifecycle
(`collectible-codegen-assemblies`) and the process-wide static backing (`engine-performance` P1)
intact throughout.

GitHub issue: to be opened (label: feature).

## Phase 0 — Baseline & guardrails

Intent: pin the current waste and give the fix a benchmark + a characterization test to flip.

- [ ] Add a diagnostic compile counter + reset to `CodeGenerationHelper`, incremented on each actual
      `compilation.Emit` in `GeneratePathTraverser` (`CodeGenerationHelper.cs:86`), exposed like
      `ClearSubGraphProviderCache` ("intended for tests and diagnostics").
- [ ] Characterization test (new `CodeGenCacheKeyingTest`, MSTest, `TestLoggerFactory.Create()`): two
      `PathSpecification`s identical in `Filter`+`Cost` but differing in `MaxDepth` currently miss each
      other — assert **two** compiles today (counter) / two distinct traverser instances. This test
      flips to **one** compile after Phase 1.
- [ ] Opt-in benchmark `[TestCategory("Benchmark")]`+`[Ignore]`: measure cold-compile latency (cache
      cleared/expired) with per-call `GetGlobalReferences` as the before-baseline. Numbers to be
      captured on this box.

## Phase 1 — Narrow the path cache key

Intent: cache on the artifact's real dependency (`Filter`+`Cost`).

- [ ] Add `KeyFor(PathSpecification) => (definition.Filter, definition.Cost)` to `GeneratedCodeCache`
      (value-equatable tuple; `PathFilterSpecification`/`PathCostSpecification` already implement
      `IEquatable<>` — `PathFilterSpecification.cs:129-140`, `PathCostSpecification.cs:103-113`).
- [ ] Route `AddTraverser` and a new `TryGetTraverser(PathSpecification, out IPathTraverser)` through
      `KeyFor`.
- [ ] Update `GraphController.CalculateShortestPath` (`GraphController.cs:1017,1024`) to use
      `TryGetTraverser`/`AddTraverser`, dropping the raw whole-spec `Traverser.TryGetValue`.
- [ ] Correct the `GeneratedCodeCache` doc-comment (`GeneratedCodeCache.cs:41-51`) to state the real
      dependency (`Filter`+`Cost`, not the whole `PathSpecification`).
- [ ] Migrate `PathCompileCache_IsSharedAcrossControllerInstances_CompilesOnce`
      (`EnginePerformanceTest.cs:99`) to the new accessor; add the "bound-only-differs ⇒ one compile,
      same instance" assertion and a "filter/cost-differs ⇒ two compiles" assertion.

## Phase 2 — Hoist the Roslyn metadata references

Intent: build the framework references once, not per compile; benefits path and subgraph.

- [ ] Replace `GetGlobalReferences()` (`CodeGenerationHelper.cs:208-247`) with a
      `static readonly MetadataReference[] _globalReferences` built once, keeping the assembly list and
      the single-file `Location`-empty → `AppContext.BaseDirectory` fallback verbatim.
- [ ] Point `GeneratePathTraverser` (`CodeGenerationHelper.cs:78`) and `CompileProvider`
      (`CodeGenerationHelper.cs:570`) at the shared array.
- [ ] Confirm the subgraph compile path picks up the same references (no behaviour change).

## Phase 3 — Expiration tuning (bounded, unload-safe)

Intent: reduce recompile churn without keeping collectible contexts alive.

- [ ] Keep the size limit + sliding expiry so contexts still unload; optionally lengthen the sliding
      window modestly. Do **not** pin entries.
- [ ] Add a path-traverser unload assertion (weak-reference, mirroring `SubGraphCodeGenUnloadTest`) so
      key-narrowing and any expiry change keep contexts collectible; confirm
      `CompiledSubGraphProvider_IsUnloadedAfterCacheCleared` still passes.

## Measure & document

- [ ] Re-run the Phase 0 benchmark: record cold-compile latency before/after the static references and
      confirm bound-only-differing requests compile once. Numbers to be captured on this box.
- [ ] Note the interaction with `dynamic-code-resource-limits` (same file): the caching changes must not
      defeat its length/timeout guards, and those guards must run before a compile is cached. Confirm no
      regression when they land.

## Progress

- [ ] Phase 0 — compile counter/reset hook; characterization test (two compiles today); opt-in
      cold-compile benchmark baseline.
- [ ] Phase 1 — `(Filter, Cost)` key + `TryGetTraverser`; controller switched; doc-comment corrected;
      P1 test migrated + new bound-only/one-compile and filter-differs/two-compile assertions.
- [ ] Phase 2 — static `MetadataReference[]` shared by path + subgraph compiles.
- [ ] Phase 3 — bounded/unload-safe expiration; path-traverser unload assertion; subgraph unload test
      still green.
- [ ] Measure & document — benchmark re-run (numbers on this box); `dynamic-code-resource-limits`
      interaction noted.
