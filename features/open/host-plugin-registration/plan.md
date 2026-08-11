# Plan - host plugin registration

Phased so every step leaves the suite green. See [spec.md](spec.md) for the contract; do not
re-litigate decisions recorded there.

## Phase 0 - failing tests first

- [ ] The contract tests from spec "What the tests must pin" groups 1, 3 and 5, written against the
      not-yet-existing `RegisterPluginType<T>` (they fail to compile until Phase 1; keep them in one
      new test class, e.g. `HostPluginRegistrationTest`).

## Phase 1 - the registration member

- [ ] `PluginEntry.IsPersistable` (derived, one home, doc comment saying why not a state).
- [ ] `Fallen8.RegisterPluginType<T>()`: probe instance, name/description read, `IsValidName`,
      contract derivation via `ContractInterface`, `PluginDefinition` with `SourceCode = null`,
      enqueue `RegisterPluginTransaction`. DAM + `new()` on `T` exactly as specced.
- [ ] Decide and record: does the member also go on `AFallen8`/`IFallen8Write`? If yes, repeat the
      annotation on every declaration (it does not flow) and extend `TrimSafetyTest`.

## Phase 2 - persistence exclusion

- [ ] `WalTransactionCodec.TryGetEntryType`: not-loggable for a non-persistable register; the
      remove-side flag on `RemovePluginTransaction`; not-loggable for a non-persistable remove.
- [ ] `PersistencyFactory.SavePlugins`: skip non-persistable entries; pin the only-host-entries
      stale-manifest deletion edge.
- [ ] The `RehydratePlugins` MERGE rule (host entries survive Load; collision: host wins, error
      logged). Pin with spec test group 4.

## Phase 3 - index and service through the registry

- [ ] `PluginContract.Index` (+ `Service`, or a recorded one-line deferral) and the
      `ContractInterface` arms.
- [ ] `IndexFactory.TryCreateIndex` and `OpenIndex` registry-first with `PluginFactory` fallback;
      same for `ServiceFactory` if included. Precedence pinned (spec test group 2).
- [ ] The `TryCreateIndex` trim-annotation decision from spec section 4, taken explicitly, with the
      seam hardening if the suppression route is chosen; `TrimSafetyTest` pins the outcome (group 6).

## Phase 4 - browser-shape verification

- [ ] Inline-mode parity test (spec group 7).
- [ ] Rebuild and run the trimmed wasm probe with a registration + index-create + vector-search leg
      added (the probe lives in the session scratchpad; port it or recreate - it is throwaway, not
      committed). Expect: registration resolves by name, `TryCreateIndex(...,"DictionaryIndex")`
      works after `RegisterPluginType<DictionaryIndex>()`, zero IL warnings.

## Phase 5 - sweep and docs

- [ ] apiApp DTO sweep (spec group 8).
- [ ] `library.mdx` registration story + README clause; docs build link-checked.
- [ ] Cross-feature impact table in the spec re-checked against what was actually built; OpenAPI
      snapshot regenerated ONLY if any inventory response shape changed.
- [ ] Full suite green; move `features/open/host-plugin-registration/` to `features/done/`.
