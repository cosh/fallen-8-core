# Host plugin registration - Specification

> **Status:** Open, spec only (no implementation yet). Feature branch:
> `feature/host-plugin-registration` (branch-only workflow, no GitHub issue/PR unless asked).
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

| Area | Impact |
| --- | --- |
| Engine public surface | One new member (`RegisterPluginType<T>`), one new derived property (`PluginEntry.IsPersistable`), two new `PluginContract` members. Additive |
| REST contract / OpenAPI | None planned - registration is an in-process host API. If a later feature wants it over REST, the apiApp already has the Roslyn path; a conscious non-goal here |
| MCP | No new REST operation, nothing to bridge. If `PluginContract.Index`/`Service` change any discovery-backed inventory response shape, re-check the snapshot |
| WAL / checkpoint | Two choke-point changes (`TryGetEntryType`, `SavePlugins`) plus the `RehydratePlugins` merge rule. No format change; existing logs replay unchanged |
| Trim safety | The reason this design exists; the `TryCreateIndex` RUC decision is the one deliberate trim-surface change and is pinned |
| Browser | Closes the "no index in the browser" blocker; with it, vector search works in wasm. The checkpoint fan-out blocker recorded alongside it is already closed (save and load pick a sequential arm from `HostCapabilities.SupportsBackgroundWork`), so index creation is what a browser host is still missing |
| Studio / NL-assist | None - no REST change, no delegate surface |
| Docs | `library.mdx` gains the registration story (replacing the "typed overload is the only trim-safe way" framing with "register your plugins, then names work"); README library bullet gets a clause |

## Out of scope

REST exposure of type registration; a `Func<IPlugin>` factory overload; process-wide registration;
persistence of host entries; the browser checkpoint story (its sequential save/load arm already
shipped, gated by `HostCapabilities.SupportsBackgroundWork`); Roslyn anywhere.
