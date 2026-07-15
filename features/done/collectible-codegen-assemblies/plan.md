# Collectible Runtime-Compiled Assemblies — Plan

Companion to [spec.md](./spec.md).

## Phase 0 — Prove the leak / establish the unload test
- Add a test that compiles several distinct providers, holds only weak references, forces a
  GC, and asserts the load contexts are still alive today (documents current behaviour and
  becomes the pass condition after the fix).

## Phase 1 — Collectible load contexts
- Introduce a small `CollectibleLoadContext : AssemblyLoadContext(isCollectible: true)` (or
  use the framework type directly).
- Change `CodeGenerationHelper.CompileProvider` and `GeneratePathTraverser` to load the
  emitted image into a fresh collectible context.

## Phase 2 — Lifetime management
- Associate each cached provider/traverser with its load context. On cache eviction, drop
  the strong reference so the context can unload once no delegates remain referenced.
- Verify the subgraph provider cache and the path `GeneratedCodeCache` both release contexts
  on eviction.

## Phase 3 — Reference audit
- Confirm no long-lived structure pins a compiled delegate/type after the owning
  subgraph/traverser is gone (which would block unload). Fix any offenders.

## Phase 4 — Verify
- The weak-reference unload test flips to asserting successful unload; full suite passes.

## Status
- [x] Phase 0 — unload test (weak reference to a generated type)
- [x] Phase 1 — collectible contexts for path and subgraph compiled assemblies
- [x] Phase 2 — lifetime management (subgraph provider cache is now expirable)
- [x] Phase 3 — reference audit (delegates/instances/types no longer pinned indefinitely)
- [x] Phase 4 — verify

## Outcome

- Both compile paths now load the emitted assembly with
  `new AssemblyLoadContext(name, isCollectible: true).LoadFromStream(...)` instead of
  `Assembly.Load(byte[])` (default, non-collectible).
- The subgraph provider cache changed from a never-evicting `ConcurrentDictionary` to a
  `MemoryCache` with a 60-second sliding expiration (matching the path traverser cache), so
  a cached provider no longer pins its load context forever. `ClearSubGraphProviderCache()`
  forces eviction (tests/diagnostics).
- Lifetime: a compiled assembly's collectible context stays alive while the cache entry is
  live or any live subgraph/traverser still references its delegates; once both are gone it
  unloads under GC, reclaiming metadata/JIT memory.
- Test: `SubGraphCodeGenUnloadTest` compiles a unique provider, weakly references a type from
  the generated assembly, clears the cache, and asserts the type (and its context) unload —
  a probe that would fail under the old non-collectible loading.

## Note

The path traverser already used an expiring cache, so no cache change was needed there — only
the collectible load context. Unload assertions use the standard drop-refs + `GC.Collect()` /
`WaitForPendingFinalizers()` loop.
