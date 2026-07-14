# Codegen Cache Keying — Specification

> **Status:** Implemented (P2 performance) — from the 2026-07 principal-architect & performance
> review. The path-traverser compile cache was keyed coarser than the compiled artifact and rebuilt
> Roslyn metadata references on every compile; both are now tightened to the real dependency.
>
> **Delivered on branch `feature/codegen-cache-keying`:**
> - **(A) Narrow the key** — `GeneratedCodeCache` keys on the `(Filter, Cost)` `ValueTuple` (via
>   `KeyFor`), exposed through `TryGetTraverser`/`AddTraverser`; `GraphController` uses the new accessor.
>   Two `/path` requests differing only in `MaxDepth`/`MaxResults`/`MaxPathWeight`/`PathAlgorithmName`
>   (all applied downstream, never baked into the traverser) now reuse ONE compiled traverser and one
>   collectible context. Null filter/cost stay distinct from an all-defaults object.
> - **(B) Hoist the references** — the Roslyn `MetadataReference` set is a `private static readonly`
>   array built once per process (from the same assembly list + single-file `AppContext.BaseDirectory`
>   fallback), shared by the path and subgraph compile paths, so a cold compile no longer re-reads the
>   framework reference assemblies.
> - **(D) Compile-count hook** — `CodeGenerationHelper.PathCompileCount`/`ResetPathCompileCount`
>   (incremented per `Emit`) make "compiled once" test-observable.
>
> Guarded by `CodegenCacheKeyingTest` (bound-only-differing requests → exactly one compile + shared
> traverser; different-filter requests → two distinct traversers). The pre-existing process-wide-reuse
> test (`EnginePerformanceTest`) was migrated to the `TryGetTraverser` accessor. The 60 s sliding
> expiry (C) was left unchanged — entries still expire so collectible contexts unload
> (`collectible-codegen-assemblies` unaffected).

## 1. Problem / current state

Two independent inefficiencies in the runtime-compiled path-filter path, both verified in the
current tree.

**Issue 1 — the cache key is coarser than the compiled artifact.**
`GeneratedCodeCache` stores each compiled `IPathTraverser` under the *whole* `PathSpecification`
(`GeneratedCodeCache.cs:64-67` — `Traverser.Set(definition, …)`), and `GraphController` both looks up
and stores under that same whole-spec key (`GraphController.cs:1017,1024`). The spec's value equality
(`PathSpecification.cs:156-170`) folds `PathAlgorithmName`, `MaxDepth`, `MaxResults` and
`MaxPathWeight` in alongside `Filter` and `Cost`. But the compiled artifact is produced by
`CreateSource` (`CodeGenerationHelper.cs:125-185`), which reads **only** `definition.Filter` and
`definition.Cost` — never the four numeric/name fields. So two `/path` requests that differ *solely*
in `maxDepth` (or `maxResults` / `maxPathWeight` / `pathAlgorithmName`) generate byte-identical
source, miss the cache, and compile two byte-identical assemblies — each loaded into its own
collectible `AssemblyLoadContext` (`CodeGenerationHelper.cs:93`). That is wasted Roslyn work and a
wasted collectible context per redundant compile.

The numeric bounds and the algorithm name are applied **downstream** at traversal time — the
controller copies `MaxDepth`/`MaxPathWeight`/`MaxResults` into a `ShortestPathDefinition`
(`GraphController.cs:1043-1045`) and selects the algorithm by name — they are never baked into the
traverser. So keying the cache on them is not just unnecessary, it defeats reuse for the artifact's
true dependency. The cache's own doc-comment overclaims this: it says the traverser "depends only on
the … `PathSpecification`" (`GeneratedCodeCache.cs:41-51`), when it depends only on the `Filter`+`Cost`
subset. The **subgraph** cache already keys precisely — on the generated source string
(`CodeGenerationHelper.cs:537`).

**Issue 2 — Roslyn metadata references are rebuilt on every compile.**
`GetGlobalReferences` (`CodeGenerationHelper.cs:208-247`) calls `MetadataReference.CreateFromFile` for
`mscorlib`/`System`/`System.Core`/`System.Runtime` (and the three engine/runtime assemblies) on
**every** compile — it is invoked from both `GeneratePathTraverser` (`CodeGenerationHelper.cs:78`) and
the subgraph `CompileProvider` (`CodeGenerationHelper.cs:570`). Both caches expire on a 60 s sliding
window (`GeneratedCodeCache.cs:35`; subgraph options `CodeGenerationHelper.cs:271`), so any filter used
less often than once per 60 s falls out and pays a full cold compile — including re-reading those
framework reference assemblies from disk (hundreds of ms) even though they never change for the life
of the process.

## 2. Goals / non-goals

**Goals**
- Key the path-traverser cache on the compiled artifact's **true dependency** — the `(Filter, Cost)`
  pair (equivalently, the generated source) — so requests differing only in a numeric bound or the
  algorithm name reuse one compiled traverser and one collectible context.
- Build the Roslyn `MetadataReference` set **once per process** (static readonly), shared by the path
  and subgraph compile paths, so a cold compile no longer re-reads the framework reference assemblies.
- Correct the cache doc-comment to state the real dependency.
- Make the compile count **test-observable** so "compiled once" is asserted directly.

**Non-goals**
- Changing filter/cost **semantics** or the REST contract — path results stay byte-identical.
- Changing the collectible-ALC lifecycle from `collectible-codegen-assemblies` (landed): no
  delegate/type/context may be pinned; entries must still expire so contexts unload.
- Owning compile/execution **resource limits** or the type allow-list — that is
  `dynamic-code-resource-limits` (same file; coordinate, do not overlap).
- Re-doing the process-wide (static) cache backing — `engine-performance` P1 already made it static.
  This narrows the **key**; it does not re-introduce a per-instance cache.
- A persistent/pinning "hot filter" LRU that would keep collectible contexts alive indefinitely — it
  tensions with the landed unload guarantee and is explicitly out of scope.

## 3. Design sketch

**(A) Narrow the path cache key.**
Derive the key from the artifact's real input. Both `PathFilterSpecification` and
`PathCostSpecification` already implement `IEquatable<>` with value equality
(`PathFilterSpecification.cs:129-140`, `PathCostSpecification.cs:103-113`), so a `(Filter, Cost)`
`ValueTuple` is a correct, cheap key — it hashes/compares structurally and handles null components via
`EqualityComparer<T>.Default`. Add `KeyFor(PathSpecification) => (definition.Filter, definition.Cost)`
to `GeneratedCodeCache`, and route both a new `TryGetTraverser(PathSpecification, out IPathTraverser)`
and `AddTraverser(PathSpecification, IPathTraverser)` through it. `GraphController.CalculateShortestPath`
switches from poking `_cache.Traverser.TryGetValue(definition, …)` (`GraphController.cs:1017`) to
`_cache.TryGetTraverser(definition, …)`, keeping the key logic in one place.

The equivalent, subgraph-consistent alternative is to key on the generated **source string** from
`CreateSource` (as the subgraph cache does at `CodeGenerationHelper.cs:537`). The tuple key is
preferred because it avoids rebuilding the source on a cache **hit** (the hot per-request path); the
source key is the fallback if the model-class equality is ever removed. Both keep a null filter/cost
distinct from an explicit all-defaults object (they produce distinct source/tuples), so neither changes
match-everything semantics. The numeric bounds and algorithm selection stay applied downstream,
unchanged.

**(B) Hoist the metadata references.**
Replace the per-call `GetGlobalReferences()` with a `private static readonly MetadataReference[]
_globalReferences`, built once at type init from today's exact logic — the same assembly list and the
same single-file fallback (when `assembly.Location` is empty, fall back to `AppContext.BaseDirectory`).
Point both `GeneratePathTraverser` (`CodeGenerationHelper.cs:78`) and `CompileProvider`
(`CodeGenerationHelper.cs:570`) at the shared array. `MetadataReference` instances are immutable and
process-lifetime and reference no collectible context, so this is unload-safe.

**(C) Expiration.**
Keep bounded eviction (size limit + sliding expiry) so collectible contexts still unload per
`collectible-codegen-assemblies`. With (B) removing the dominant cold-compile cost, expiration is a
secondary lever; the sliding window may be lengthened modestly, but entries must not be pinned. The
trade-off is explicit: a longer window means fewer recompiles but contexts resident longer.

**(D) Compile-count hook.**
Add a test/diagnostic compile counter incremented on each actual `compilation.Emit` in
`GeneratePathTraverser`, exposed and resettable like `ClearSubGraphProviderCache` ("intended for tests
and diagnostics"). The acceptance test asserts exactly one compile across bound-only-differing
requests; reference-equality of the returned traverser is the backstop.

## 4. Acceptance criteria

- Two `/path` requests with identical `filter`+`cost` but different `maxDepth` (and/or `maxResults` /
  `maxPathWeight` / `pathAlgorithmName`) produce **exactly one** compiled traverser: the compile
  counter increments once and both requests resolve to the **same** `IPathTraverser` instance.
- Two requests differing in `filter` or `cost` still compile **two** distinct traversers (no false
  sharing / no semantic change).
- The framework `MetadataReference` set is built **once per process** (by construction / observable via
  the compile path); an opt-in `[TestCategory("Benchmark")]`+`[Ignore]` benchmark shows cold-compile
  latency drops with static references (numbers to be captured on this box).
- Path results are byte-identical to today for both cold and warm requests.
- The collectible-ALC unload guarantee is unchanged:
  `SubGraphCodeGenUnloadTest.CompiledSubGraphProvider_IsUnloadedAfterCacheCleared` and an equivalent
  path-traverser unload assertion still pass — nothing is pinned.
- The existing `PathCompileCache_IsSharedAcrossControllerInstances_CompilesOnce` intent (process-wide
  reuse) is preserved, migrated to the new key/accessor.
- Full suite green.

## 5. Risks

- **Migrating the existing P1 test.** It probes the raw `MemoryCache` with the whole-spec key
  (`EnginePerformanceTest.cs:126,136`); narrowing the key means that raw probe no longer resolves. The
  test must move to the new `TryGetTraverser` accessor — preserve the intent, don't delete it.
- **Same file as `dynamic-code-resource-limits`.** Both touch `CodeGenerationHelper`/`GeneratedCodeCache`.
  Coordinate ordering; the caching changes must not defeat that feature's length/timeout checks, and
  those checks must run before a compile result is cached — each scopes the other out, so keep them
  cleanly separated.
- **Static references under single-file publish.** The `Location`-empty fallback to
  `AppContext.BaseDirectory` must be kept verbatim so static init does not throw when an assembly has no
  on-disk location.
- **Longer expiration keeps contexts resident longer** (more resident metadata) — bounded by the size
  limit; do not over-lengthen, and never pin.

## 6. Keep (do not regress)

- **Process-wide (static) cache backing** (`engine-performance` P1) — this narrows the key, not the
  scope; the cache stays shared across controller instances.
- **Collectible `AssemblyLoadContext` per compiled artifact + eviction-driven unload**
  (`collectible-codegen-assemblies`) — no pinning; entries still expire and unload.
- **Subgraph source-string keying** (`CodeGenerationHelper.cs:537`) — already precise; the path cache is
  being brought into line with it, not changed.
- **Downstream application of `MaxDepth`/`MaxResults`/`MaxPathWeight`/`PathAlgorithmName`** (the
  `ShortestPathDefinition` build and algorithm selection) — unchanged; only the cache key stops folding
  them in.
- **`MaxVariableEdgeLength = 100`** and any `dynamic-code-resource-limits` guards — untouched.
- **Single-writer / lock-free reads**; `/path` stays a read.
