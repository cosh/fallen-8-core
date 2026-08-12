# Host plugin registration - Specification

> **Status:** IMPLEMENTED and merged to `main` (branch `feature/host-plugin-registration`, branch-only
> workflow, no GitHub issue/PR unless asked). Every phase of [plan.md](plan.md) is done. This closes
> the largest remaining browser blocker: a browser host can now create indexes and run vector search.
> Phases 0-3 are in the engine and pinned by
> `HostPluginRegistrationTest` and `TrimSafetyTest`. The browser claim is not asserted but gated: the
> committed trimmed browser-wasm harness `tools/browser-probe` runs headless under node in CI, and all
> seven of its checks pass - a thread cannot start on that runtime, an engine can be constructed and
> written to, index creation FAILS before registration and SUCCEEDS after it, vector search works, a
> checkpoint round trip keeps a host-registered index and its content, and a host registration survives a
> Load. See [plan.md](plan.md) for what each phase did and did not do.
>
> This is the follow-up recorded in [features/done/trim-safety/spec.md](../../done/trim-safety/spec.md)
> ("Known gap"), designed in a principal architecture review on 2026-08-11 and written down here so
> implementation can start from a settled contract.
>
> Code references below name the FILE and the SYMBOL, never a line number: the engine files this
> spec points into keep moving, and a drifted line number reads as a verified citation while
> pointing at unrelated code.

## Why

Name-based plugin resolution goes through `PluginFactory`, which discovers candidates by enumerating
`*.dll` under `AppContext.BaseDirectory`. Two hosts cannot use that:

- **A browser-wasm host has no dll files there at all** (WebCIL packaging), so every name resolves
  to a clean false - regardless of trimming. Since `IndexFactory.TryCreateIndex` is the ONLY way to
  create an index and resolves ONLY through `PluginFactory.TryFindPlugin`
  (`Index/IndexFactory.cs`, `TryCreateIndex`), a browser host today cannot create any index: no
  DictionaryIndex, no RangeIndex, no VectorIndex,
  and therefore no vector search. This is the single largest remaining browser blocker.
- **A trimmed host** cannot keep a type that exists only as a string in scanned assemblies; the
  string-named APIs now warn (`[RequiresUnreferencedCode]`, feature trim-safety) and degrade to
  false at runtime.

The engine already has the right seam: `Fallen8.Plugins` (`PluginRegistry`, per namespace) is
consulted BEFORE the built-in cache and `PluginFactory` in `Fallen8.ResolveCachedPlugin`
(`Fallen8.cs`) - but today
it holds only runtime-COMPILED plugins (Roslyn, apiApp-only), and index/service creation never
consults it. This feature lets the HOST register plugin TYPES, compile-free: a statically-known
type flows from the host's `typeof(MyAlgo)` to the `Activator` call with no scanning and no
suppression, because `PluginEntry.Artifact` was annotated with
`DynamicallyAccessedMembers(PublicParameterlessConstructor)` by the trim-safety feature for exactly
this.

Alternatives were evaluated and rejected in review: typed overloads per family close nothing at
checkpoint-load time (indexes are rehydrated BY NAME) and spread IL2091 annotation virality; a
static provider hook on `PluginFactory` is process-wide mutable state that bypasses the
transaction/writer discipline and splits enumeration across two homes. The registry design wins
because it adds the capability by REMOVING an asymmetry: index and service become consultable like
every other family, instead of adding a mechanism.

## Contract

### 1. One new public member on the engine

```csharp
// Fallen8 (and AFallen8/IFallen8Write if the abstract surface should carry it - implementer's call,
// but the annotation must then be repeated on every declaration, as with the typed path overload):
public TransactionInformation RegisterPluginType<
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>()
    where T : class, IPlugin, new();
```

- **Generic, not name-plus-factory.** The DAM annotation flows into the already-annotated
  `PluginEntry.Artifact` with zero suppressions. A `Func<IPlugin>` factory would need a parallel
  storage field on `PluginEntry` and a second activation branch in `TryActivate` - two homes for
  "how an instance is made". The registry's read surface already operates on `Artifact` as a `Type`.
- **`new()` constraint, deliberately.** Activation NEEDS the parameterless constructor, so requiring
  it turns a runtime activation failure into a compile error at the host. (The typed path overloads
  deliberately avoid `new()` because implementers there are not constructed by the engine;
  registration is different.)
- **Name from the instance, never explicit.** Registration activates ONE probe instance of `T`,
  reads `PluginName` and `Description`, and validates with `PluginRegistry.IsValidName`. The
  existing invariant "the persisted name must equal the compiled type's PluginName"
  (the doc contract on `PluginDefinition.Name`) stays unbreakable because there is no name parameter
  to diverge.
- **Contract derived, not passed.** Resolve `typeof(T)` against `PluginFactory.ContractInterface`
  (the single CA-13 contract-to-interface home): the contract whose interface `T` implements. Zero
  or more than one match: reject as `InvalidInput`.
- **A transaction, not a direct call.** The helper builds the entry and enqueues a
  `RegisterPluginTransaction`; the existing TOCTOU re-checks in `PluginRegistry.TryRegister` apply
  (`InvalidInput` / `Conflict` / `QuotaExceeded`). On an Inline-mode engine (the browser) it is
  terminal on return; threaded hosts wait. Single-writer discipline preserved, no new machinery.
  Host entries count against the existing registry quota (the ceiling is about registry size).
- **Decided during implementation (the open question in the signature above): the member lives on the
  concrete `Fallen8` ONLY - not on `AFallen8`, not on `IFallen8Write`.** Reasons: registration is host
  wiring rather than graph mutation, so the read/write abstraction has no business carrying it; and the
  DAM annotation does NOT flow between a declaration and its override, so every extra declaration is
  another place it can silently drift (the cost the typed path overload already pays).
  **Consequence, accepted:** code holding only an `AFallen8` or `IFallen8Write` reference cannot register
  a type. It needs the concrete `Fallen8` that the host constructed, so an embedder passing an
  abstraction around must register at construction time or keep the concrete reference.
  `TrimSafetyTest.HostTypeRegistration_IsTrimSafe_AndKeepsTheRegisteredTypesConstructor` reads the member
  off `typeof(Fallen8)`, which makes a later move a deliberate, test-visible change.

### 2. Persistence exclusion: a derived rule, not a new state

A host entry IS compiled (an artifact type exists), so a new `PluginCompileState` would conflate
two axes and force edits in `TryActivate` and `EntriesForContract`. Instead, ONE derived rule with
one home:

```csharp
// PluginEntry:
public bool IsPersistable => Definition?.SourceCode != null;
```

consumed at exactly the two choke points where persistence happens:

1. **WAL**: `WalTransactionCodec.TryGetEntryType` returns `false` for a `RegisterPluginTransaction`
   whose entry is not persistable. Exact in-repo precedent: a recipe-less
   `CreateSubGraphTransaction` is already classified not-loggable (the
   `CreateSubGraphTransaction` arm of `WalTransactionCodec.TryGetEntryType`).
   The commit then reports `Durable = true`, which is honest: there is nothing to persist - host
   code re-establishes the registration on every start. The REMOVE side needs one flag:
   `RemovePluginTransaction` carries only a name, so its `TryExecute` records whether the removed
   entry was persistable and `TryGetEntryType` classifies the remove not-loggable when it was not -
   otherwise a persisted log would replay a remove for an entry that never existed there.
2. **Checkpoint**: `PersistencyFactory.SavePlugins` skips non-persistable entries. Note the existing
   edge: if ONLY host entries exist, `definitions.Count == 0` deletes any stale manifest - correct,
   and must be pinned by a test.

### 3. The Load hole (found in review; MUST be closed by this feature)

`RehydratePlugins` ends in `Plugins.ReplaceAll(manifest entries)` (`Fallen8.Persistence.cs`,
`RehydratePlugins`),
which would wipe every host registration on any Load - including a Load whose index rehydration
needs those very types. Rule: the rehydration MERGES, preserving non-persistable entries; on an
ordinal name collision the host entry wins and an error is logged. Host-wins rationale: the host's
type is what the running process can actually execute; in a compiler-less host the colliding
persisted source would rehydrate `SourceOnly` (uninvocable) anyway.

### 4. Index and service join the registry (the actual browser payoff)

Verified in review: path/analytics/subgraph already resolve registry-first; index and service do
NOT and cannot - `PluginContract` has no Index/Service member.

- **Index: mandatory.** Add `PluginContract.Index` with `ContractInterface` returning
  `typeof(IIndex)`. Route `IndexFactory.TryCreateIndex` AND checkpoint rehydration
  `IndexFactory.OpenIndex` (both resolve through `PluginFactory.TryFindPlugin` today)
  registry-first (fresh instance per call, which is
  what an index needs - each index IS an instance), falling back to `PluginFactory`. Registry-first
  precedence matches `ResolveCachedPlugin` and must be pinned. Routing `OpenIndex` is what makes a
  host-registered index type survive a checkpoint round trip - the half of the hole typed overloads
  could never close.
- **Service: include for symmetry** (`PluginContract.Service`, `typeof(IService)`, the two call
  sites in `ServiceFactory`), or record a one-line conscious deferral; not on the browser critical
  path.
- **Trim-warning residue - decide explicitly during implementation:** `TryCreateIndex` carries
  `[RequiresUnreferencedCode]` today; after this feature that blanket statement stops being true
  (the registry path resolves statically-known types). Preferred: split resolution into a clean
  registry-first helper plus a discovery fallback behind a per-member suppressed seam (the
  `ReplaySubGraphCreateSuppressed` pattern), justified as "the fallback degrades to a clean
  not-found; the supported trimmed path is host registration" - and HARDEN discovery's narrow
  guards inside that seam so the justification is actually true (`PluginFactory`'s own type doc
  admits `GetExportedTypes` is unguarded). If that hardening is out of appetite, keep the RUC and
  document the host's one-line suppression. Do not suppress without making the claim true.

### 5. Scope: per-namespace, on `PluginRegistry`

Consistent with stored queries, subgraphs and compiled plugins (one graph, one registry); the
concurrency story (writer-thread mutation, lock-free snapshot reads) is already solved; the browser
host has one engine; enumeration surfaces (`NamesForContract` into /status and pickers) come for
free. A multi-namespace embedder registers per engine - a loop, not a problem. The apiApp does not
need this feature (it has Roslyn).

## What the tests must pin

1. **Resolution:** a host-registered path/analytics type resolves by name through the string-named
   APIs; fresh instance per call; delete + re-register never served stale; invisible in a second
   engine (per-namespace isolation).
2. **Index-through-registry:** register an index type, `TryCreateIndex` resolves it registry-first
   (precedence over a same-named discovered plugin, pinned); Save then Load rehydrates it via
   `OpenIndex` through the registry with content restored.
3. **Non-persistence:** with the WAL on, a host registration commits `Durable == true` and writes NO
   `RegisterPlugin` frame (restart: entry absent, while a source-registered plugin in the same log
   still replays); a checkpoint's plugin manifest omits host entries; a graph with ONLY host entries
   leaves no manifest; removing a host entry writes no `RemovePlugin` frame.
4. **Load survival:** host entries survive `Load`/`RehydratePlugins`; a name collision with a
   manifest entry resolves host-wins with a logged error.
5. **Registration rules:** invalid `PluginName` is `InvalidInput`; duplicate is `Conflict`; ceiling
   is `QuotaExceeded`; a `T` matching zero or two contracts is `InvalidInput`; rollback removes a
   just-registered entry.
6. **Trim pins** (TrimSafetyTest style - only what the build cannot see): `RegisterPluginType`
   carries no `RequiresUnreferencedCode`; its `T` carries the DAM annotation and `new()`; whichever
   `TryCreateIndex` annotation decision is taken is pinned so it cannot silently flip.
7. **Inline-mode parity:** registration + index-create + save/load on an `Inline`-mode engine (the
   browser execution shape), composing with `InlineTransactionExecutionTest`'s pattern.
8. **apiApp sweep:** the plugin list DTOs tolerate `sourceCode: null` (host entries are visible
   through GET surfaces on an embedded-engine host; the DTO contract must not assume source).

## Impact on existing features

Re-checked against what was actually built; a row that turned out differently says so.

| Area | Impact |
| --- | --- |
| Engine public surface | As built: `Fallen8.RegisterPluginType<T>` (concrete class only - see section 1), `PluginEntry.IsPersistable`, and `PluginContract.Index` / `PluginContract.Service`. Additive, with one behaviour change on existing members the row did not anticipate: `IndexFactory.GetAvailableIndexPlugins` and `ServiceFactory.GetAvailableServicePlugins` now UNION the registry's names with the discovered ones, so a registered type is listed. The rehydration entry point `PluginRegistry.ReplaceAll` became `ReplacePersistedEntries`, which is `internal`, so no consumer can see the rename |
| REST contract / OpenAPI | **No new route or shape, but two controller files DID change, so the row as first written was wrong.** Registration stayed an in-process host API and the apiApp still reaches the same registry through its Roslyn path. What changed: `AdminController`'s `GET /status` built its index and service inventories from discovery alone, so a host-registered type - the point of the feature - was invisible there while path/subgraph/analytics registered plugins were visible; and `PluginREST`'s XML docs enumerated the old `category`/`contract` values. Both are DESCRIPTION and CONTENT changes rather than schema changes, but the XML docs reach the published document, so the **OpenAPI snapshot was regenerated** |
| MCP | Nothing to bridge: no new REST operation and no response SHAPE changed, so `McpRestCoverageTest` has nothing new to cover. The available-plugins lists can now carry one more NAME (a host-registered type), which is content, not shape |
| WAL / checkpoint | As planned (`TryGetEntryType`, `SavePlugins`, the `RehydratePlugins` merge), plus one mechanism the spec called for and implementation made concrete: `RemovePluginTransaction` carries an `internal` `RemovedEntryWasPersistable` set in `TryExecute`, which is what lets the remove side be classified not-loggable. No format change; existing logs replay unchanged |
| Trim safety | Correction - the trim-surface change is FOUR members, not one. `IndexFactory.TryCreateIndex` and `OpenIndex` and `ServiceFactory.TryAddService` and `OpenService` all dropped `[RequiresUnreferencedCode]`, each factory keeping exactly one suppressed one-line discovery seam (`TryFindDiscoveredIndexSuppressed`, `TryFindDiscoveredServiceSuppressed`); the section-4 hardening WAS done, so the justification is true (`PluginFactory`'s `GetExportedTypes` and `Assembly.Load` now degrade through `IsDeploymentFailure`). `TrimSafetyTest` pins both the absent requirement and the seams. `SaveTransaction` / `LoadTransaction` deliberately keep theirs - a checkpoint resolves property value types reflectively on load, which host registration does not address - so a browser host that checkpoints suppresses IL2026 at its own call site, as `tools/browser-probe` does |
| Browser | As built and gated by the probe rather than argued: index creation and vector search work in wasm once the host registers the type, and a host-registered index survives a checkpoint round trip. The checkpoint fan-out blocker recorded alongside it was already closed (save and load pick a sequential arm from `HostCapabilities.SupportsBackgroundWork`), so both halves of the gap recorded in trim-safety are now closed. Residue, deliberate: the path and analytics name-based members keep their `[RequiresUnreferencedCode]`, so a browser host that prefers a NAME there registers the type and suppresses IL2026 at its own call site, and it must re-register on every start (before any `LoadTransaction`) and move the checkpoint bytes out of the Emscripten filesystem itself |
| Studio / NL-assist | None, confirmed as built - no REST change and no delegate surface was touched |
| Docs | As built: `library.mdx` gained a "Registering plugin types when discovery cannot help" section, its false "no index in a browser at all" paragraph is gone, and one row of its trim table had to change too, because index and service creation no longer warn. The README library bullet gained a clause. `plugin-registration.md` keeps its own home (source-compiled plugins over REST) and is not re-narrated |

## Out of scope

REST exposure of type registration; a `Func<IPlugin>` factory overload; process-wide registration;
persistence of host entries; the browser checkpoint story (its sequential save/load arm already
shipped, gated by `HostCapabilities.SupportsBackgroundWork`); Roslyn anywhere.
