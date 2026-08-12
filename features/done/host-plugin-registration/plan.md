# Plan - host plugin registration

Phased so every step leaves the suite green. See [spec.md](spec.md) for the contract; do not
re-litigate decisions recorded there.

Boxes below are ticked only where the work is in the branch. Anything not done says what is missing
instead of being ticked, and the one place the plan was deliberately NOT followed (phase 4) says why.

## Phase 0 - failing tests first

- [x] The contract tests from spec "What the tests must pin" groups 1, 3 and 5, written against the
      not-yet-existing `RegisterPluginType<T>` (they fail to compile until Phase 1; keep them in one
      new test class, e.g. `HostPluginRegistrationTest`). Landed as
      `fallen-8-unittest/HostPluginRegistrationTest.cs`, which now covers groups 1-5 and 7.

## Phase 1 - the registration member

- [x] `PluginEntry.IsPersistable` (derived, one home, doc comment saying why not a state).
- [x] `Fallen8.RegisterPluginType<T>()`: probe instance, name/description read, `IsValidName`,
      contract derivation via `ContractInterface`, `PluginDefinition` with `SourceCode = null`,
      enqueue `RegisterPluginTransaction`. DAM + `new()` on `T` exactly as specced. A registration
      rejected before an entry exists still returns one tracked transaction
      (`Fallen8.RejectedPluginRegistration`), so every outcome is inspected the same way.
- [x] Decide and record: does the member also go on `AFallen8`/`IFallen8Write`? **Decided: no, the
      concrete `Fallen8` only.** The decision and its accepted consequence (a holder of an `AFallen8`
      or `IFallen8Write` reference cannot register a type) are recorded in spec section 1, so no
      annotation had to be repeated and `TrimSafetyTest` reads the member off `typeof(Fallen8)`.

## Phase 2 - persistence exclusion

- [x] `WalTransactionCodec.TryGetEntryType`: not-loggable for a non-persistable register; the
      remove-side flag on `RemovePluginTransaction`; not-loggable for a non-persistable remove.
- [x] `PersistencyFactory.SavePlugins`: skip non-persistable entries; pin the only-host-entries
      stale-manifest deletion edge.
- [x] The `RehydratePlugins` MERGE rule (host entries survive Load; collision: host wins, error
      logged), implemented as `PluginRegistry.ReplacePersistedEntries`. Pinned with spec test group 4.

## Phase 3 - index and service through the registry

- [x] `PluginContract.Index` (+ `Service`, or a recorded one-line deferral) and the
      `ContractInterface` arms. Service was INCLUDED, not deferred.
- [x] `IndexFactory.TryCreateIndex` and `OpenIndex` registry-first with `PluginFactory` fallback;
      same for `ServiceFactory` if included. Precedence pinned (spec test group 2).
- [x] The `TryCreateIndex` trim-annotation decision from spec section 4, taken explicitly, with the
      seam hardening if the suppression route is chosen; `TrimSafetyTest` pins the outcome (group 6).
      Outcome: the suppression route, on all four members, with the hardening actually done - see the
      Trim safety row of the spec's impact table.

## Phase 4 - browser-shape verification

- [x] Inline-mode parity test (spec group 7):
      `HostPluginRegistrationTest.InlineEngine_RegistersATypeAndCreatesAnIndex_ThatSurvivesASaveLoadRoundTrip`.
- [x] Rebuild and run the trimmed wasm probe with a registration + index-create + vector-search leg
      added. **This item was deliberately NOT followed as written.** It said the probe "lives in the
      session scratchpad; port it or recreate - it is throwaway, not committed". A throwaway probe is
      exactly how the previous one vanished, twice leaving a review's "compensating control" pointing
      at something nobody could run, so the probe is now COMMITTED at `tools/browser-probe` and wired
      into CI as the `browser` job in `.github/workflows/buildAndTest.yml`. It publishes trimmed
      (`TrimMode=full`, no rooted assembly, so a trim warning fails the build) and runs headless under
      node; its exit code is the verdict. All seven checks pass - see the spec's status line for the
      list of what they assert.

## Phase 5 - sweep and docs

- [x] apiApp DTO sweep (spec group 8), pinned by
      `HostPluginApiSurfaceTest.PluginGetSurfaces_ProjectAHostRegisteredTypeThatCarriesNoSource`,
      which asserts the null source survives the JSON round trip, plus
      `PluginSummaryDto_DocumentsEveryCategoryAndContract` so a later `PluginCategory` or
      `PluginContract` member cannot be added without documenting it. The XML docs that enumerate
      those values reach the published OpenAPI document, so the snapshot was regenerated (below).
- [x] `library.mdx` registration story + README clause; docs build link-checked.
      `docs/src/content/docs/library.mdx` gained "Registering plugin types when discovery cannot help"
      and lost its "no index in a browser" claim, and the README library bullet gained a clause. The
      link-checked Starlight build passes on this branch: 36 pages, all internal links valid.
- [x] Cross-feature impact table in the spec re-checked against what was actually built, two rows
      corrected. The OpenAPI snapshot WAS regenerated: no route, method or response SHAPE changed, but
      the `category` and `contract` XML docs gained the new `Index`/`Service` values and `sourceCode`
      gained the "null for a host-registered entry" note, and those descriptions are part of the
      published document. Reviewed diff: five description lines, additions only.
- [x] Full suite green; moved to `features/done/`. Gates at merge: .NET 1880 passed / 0 failed / 30
      skipped; Studio 823/823; a no-incremental rebuild at 0 errors with 23 warnings, all of them the
      apiApp's deliberate IL2026 exemption and none from the engine, the integrations runtime or the
      test project; the link-checked docs build green; the browser probe green (re-run after the
      second round of fixes, because a discovery-memoization change landed in between).
