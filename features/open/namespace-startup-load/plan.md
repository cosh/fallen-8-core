# Plan - Namespace startup load

Six phases, each independently shippable and testable, in dependency order. **The guard ships
before anything can be excluded**; that ordering is the whole point, not a preference. Branch:
`feature/namespace-startup-load`.

## Phase 0 - Residency and the guard, with residency permanently true

Introduce residency as a concept and defuse the shutdown trap while nothing can yet *be*
non-resident, so the most dangerous code lands with tests and zero blast radius.

- `Namespaces/Namespace.cs`: `IsLoaded`, `TryGetEngine(out Fallen8)`, and `Engine` throwing
  `NamespaceNotLoadedException` when absent.
- `Namespaces/NamespaceNotLoadedException.cs` (new, twinning `UnknownNamespaceException.cs`) and
  `NamespaceProblems.NotLoaded(name)`.
- `Services/DurabilityLifecycleService.cs`: the start loop and the shutdown save loop skip
  non-resident namespaces, one informational line per skip naming the namespace and stating that
  its checkpoint and WAL are untouched; skipped namespaces are excluded from `RegisterAll`.
- `Controllers/AdminController.cs`: `SaveAll` skips them and reports them as **skipped** (today an
  engine-less namespace would be counted as a failure in a 500 body); `Save` refuses per 4.7.
- `Ingestion/DocumentIngestionService.cs` interrupted-document sweep, and
  `Namespaces/Fallen8Namespaces.cs` `Dispose` / dispose-under-gate / `TryDrop`: tolerate an absent
  engine, and `TryDrop` must still delete the WAL and the directory.
- **Verify:** `NotLoadedNamespace_IsNotSavedOnShutdown_AndItsWalAndCheckpointSurvive` (the single
  most important test in the feature: no new checkpoint file, the registry's newest entry for that
  id unchanged, the WAL byte-identical and **not** header-only, and a later boot with it included
  restores the original counts); `SaveAll_SkipsNotLoadedNamespaces_AndReportsThemAsSkipped`;
  `Save_AddressingANotLoadedNamespace_Refuses`; `Drop_OfANotLoadedNamespace_DeletesItsWalAndCatalogEntry`.
  The existing `ShutdownSave_SpansAllNamespaces_AndTheNextBootRestoresThem` is deliberately renamed
  to `..._SpansEveryLoadedNamespace_...` - an intentional contract change, recorded as such, not a
  test that broke.

## Phase 1 - The policy and the boot decision (no REST, no UI)

- `Namespaces/NamespaceCatalog.cs`: `loadOnStartupEnabled` on the entry, re-emitted by the catalog
  writer.
- `Configuration/Fallen8NamespacesOptions.cs`: `LoadOnStartup`, `StartupLoadMode`, and the
  correction of the "an open write-ahead log" doc string; `appsettings.json`.
- `Namespaces/Fallen8Namespaces.cs`: the catalog loop decides **before** constructing an engine.
- `Services/DurabilityLifecycleService.cs`: save-games FR-9's abort scopes to selected namespaces.
- **Verify:** `Boot_SkipsAnExcludedNamespace_AndConstructsNoEngineForIt`;
  `Boot_LogsOneLinePerLoadedAndSkippedNamespace`;
  `Boot_DoesNotAbort_WhenAnExcludedNamespacesCheckpointIsMissing`;
  `Catalog_RetainsNotLoadedEntries_AcrossCreateRenameDrop` (the R2/R5 regression pin);
  `Create_OfANotLoadedNamespacesName_Conflicts`; `StartupLoadMode_All_IgnoresExclusions`;
  `StartupLoadMode_DefaultOnly_LoadsOnlyDefault`; `Default_CannotBeExcluded_ByCatalogOrConfig`.
- Ships as an operator capability through a hand-edited catalog plus config. Already useful.

## Phase 2 - The REST surface

- `NamespaceState.NotLoaded`; `Controllers/Model/NamespaceREST.cs` gains
  `loadOnStartupEnabled` and makes the counts absent-capable; `NamespacesController.ToRest` works
  without an engine.
- `PATCH /ns/{name}`: the third field joins **both** the "supply at least one field" guard **and**
  the up-front validation - otherwise a rename commits and then reports rejected. The update lands
  as one `TryUpdate` under the write lock with a single catalog write (the precedent the
  audit-defects report already set for this controller).
- `NamespaceValidationFilter`: the third branch, so the refusal lands before any action touches an
  engine; plus the exception-filter twin for the off-request path.
- `GET /status` reports residency and omits derived numbers; the change-feed 503 detail gains the
  third cause; `AppJsonContext` registration.
- **Verify:** the exact 503 body pinned, and the existing 404 body asserted **byte-unchanged**; a
  PATCH round-trip; the source-gen parity test; regenerate the OpenAPI snapshot and review the
  diff. MCP in this phase: `f8_overview` reports residency (read tier), and the policy field's
  bridging is recorded with reasoning because the coverage gate cannot see a new field.

## Phase 3 - Activation

- `POST /ns/{name}/activate`: idempotent, rate-limited, does not touch the persisted policy.
- The load routine is extracted out of the hosted service, so its contract becomes "fail this
  request" rather than "abort the process", behind a **per-namespace** load gate - never the
  collection write lock, which a seconds-long load would hold against every create, rename and
  drop in the Fallen-8.
- `fallen-8-mcp` admin tier + `McpBridgedEndpoints`.
- **Verify:** activation restores the checkpoint and the WAL tail;
  `ConcurrentActivation_OfTheSameNamespace_ConstructsExactlyOneEngine` (the two-engines-on-one-WAL
  regression, R5); activation of a loaded namespace is idempotent; activation leaves the policy
  unchanged; `McpContractTest` and `McpRestCoverageTest` green after the snapshot regen.

## Phase 4 - Studio

- `src/api/types.ts` (third state, absent-capable counts), `endpoints.ts` (the PATCH field,
  activation).
- `NamespacesPanel`: one "at startup" column (load / skip / inherit) with the existing
  "takes effect on restart" guidance register.
- `NamespaceSwitcher`: the existing faint-dot plus tag slot, so no new visual language; the count
  formatter must stop throwing on an absent count.
- `NamespaceScope`: a third branch (`namespace-not-loaded`) in prose register, **not** the warn
  palette, offering activation - and no buttons under `lockNamespace`.
- **Verify:** extend `tests/namespaces.test.tsx` (the tag-precedence chain, dash counts, the new
  column); a **new** `NamespaceScope` describe (nothing renders that component today - do not claim
  to extend a test that does not exist); `api-contract.test.ts`; `mount-seam.test.tsx` for the
  locked embed; e2e scenario for the policy round trip (it cannot assert the startup effect, which
  needs a restart and therefore belongs in the MSTest suite).
- Recapture `screen-connect.png`, with the capture spec creating one namespace carrying a
  non-inherit policy so the new column shows a real value.

## Phase 5 - Discoverability, docs and the measurement

- Amend in place: `namespaces` (the primary home, with a new startup-load subsection),
  `save-games` (its Startup table is the most wrong section today, plus the subset-entry note),
  `architecture`, `running`, `capacity-and-performance`, `observability`, `studio`,
  `troubleshooting`; `features/done/graph-namespaces/README.md` as the living doc; the root README
  namespaces entry amended in place. **No new page and no sidebar change.**
- Add the `fallen-8-bench` load row, so the boot-time claim in the spec becomes measured rather
  than asserted.
- **Gates:** `dotnet build` (warnings are errors), `dotnet test fallen-8-core.sln`, the convention
  tests, the OpenAPI snapshot diff reviewed, `npm --prefix docs run build` (link-checked), the web
  UI typecheck + vitest + e2e, and an explicit statement that `tools/browser-probe` was not
  required because nothing under `fallen-8-core` changed.

## Risks and mitigations

| # | Risk | Mitigation | Test |
|---|---|---|---|
| R1 | Shutdown writes an empty checkpoint and truncates the WAL to a header (unrecoverable half) | Phase 0 lands the three-point guard before exclusion is possible; `Engine` throws so a missed site skips instead of NRE-ing | `NotLoadedNamespace_IsNotSavedOnShutdown_AndItsWalAndCheckpointSurvive` |
| R2 | The catalog entry is erased by the next metadata write, stranding the data | Residency is a property of the entry, not of membership (spec 4.4) | `Catalog_RetainsNotLoadedEntries_AcrossCreateRenameDrop` |
| R3 | The freed name is re-minted under a second id over real data | Same as R2 - the name stays reserved | `Create_OfANotLoadedNamespacesName_Conflicts` |
| R4 | A 404 sends the operator to "Recreate (empty)" | 503 with its own title and `namespaceState`; `GET /ns` lists not-loaded namespaces | 503 body pin + the 404 body asserted unchanged |
| R5 | Two engines on one WAL (silently non-durable commits) | No lazy load at all; activation uses a per-namespace gate | `ConcurrentActivation_OfTheSameNamespace_ConstructsExactlyOneEngine` |
| R6 | Zeroed success responses read as "healthy and empty" to a reconciling writer and to the first-run walkthrough | Counts absent, not zero; residency on `/status` and `/ns` | The absent-count pins in both suites |
