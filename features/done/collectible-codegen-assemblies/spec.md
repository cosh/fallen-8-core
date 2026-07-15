# Collectible Runtime-Compiled Assemblies — Specification

> **Status:** Planned. Originates from limitations in
> [../subgraph/spec.md](../subgraph/spec.md) (§9) and the REST/API review. Tracked by its
> GitHub feature issue.

## 1. Problem

Both dynamic-filter features compile user-supplied C# fragments at runtime with Roslyn and
load the result with `Assembly.Load(byte[])`:

- path finding — `CodeGenerationHelper.GeneratePathTraverser`
- subgraph — `CodeGenerationHelper.CompileProvider`

`Assembly.Load(byte[])` loads into the default `AssemblyLoadContext`, which is **not
collectible**: those assemblies live for the lifetime of the process and their metadata/JIT
memory is never reclaimed. The subgraph provider cache bounds regrowth for *identical*
filter sets, but a workload that submits many *distinct* filters (path or subgraph) grows
process memory without bound. This is a slow leak, not a correctness bug.

## 2. Goals / non-goals

**Goals**
- Runtime-compiled filter/traverser assemblies can be unloaded when no longer referenced.
- The existing provider cache continues to avoid recompiling identical filter sets; only
  genuinely distinct compilations consume (reclaimable) memory.
- No change to the REST contract or filter semantics.

**Non-goals**
- Sandboxing or restricting what compiled filters may do (separate security concern).
- Removing runtime compilation (it is the accepted design for dynamic filters).

## 3. Design sketch

- Load each generated assembly into a dedicated **collectible** `AssemblyLoadContext`
  (`isCollectible: true`) instead of the default context.
- Tie the lifetime of the load context to the cached compiled provider / traverser: when a
  cache entry is evicted and no live delegates reference it, allow the context to unload.
  Delegates keep the context alive while a subgraph/traverser is in use, so eviction must
  wait for references to drop (the GC unloads the context once unreferenced).
- Keep the content-keyed cache so identical filter sets share one context.
- Audit that no long-lived object pins a delegate/type after the owning subgraph is
  deregistered (which would prevent unload).

## 4. Risks

- Unloadability requires that *nothing* outside the context holds a reference to its types
  or delegates. A leaked reference silently prevents collection — tests must assert actual
  unload (e.g. via a weak reference to the context going dead after GC).
- Collectible contexts have a small per-context overhead; the content-keyed cache keeps the
  number of live contexts proportional to distinct filter sets, not requests.

## 5. Acceptance criteria

- After creating and deregistering many subgraphs with distinct filters (and dropping
  references), the associated load contexts become collectible and unload under GC
  pressure, demonstrated by a weak-reference test.
- Path and subgraph filter behaviour and all existing tests are unchanged.

## 6. Testing

- Weak-reference/unload test: compile N distinct providers, release them, force GC, assert
  the contexts unloaded.
- Regression: existing path and subgraph tests pass.
