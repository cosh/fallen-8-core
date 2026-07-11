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
- [ ] Phase 0 — unload test harness
- [ ] Phase 1 — collectible contexts
- [ ] Phase 2 — lifetime management
- [ ] Phase 3 — reference audit
- [ ] Phase 4 — verify
