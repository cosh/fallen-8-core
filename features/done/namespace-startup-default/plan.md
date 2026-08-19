# Namespace startup default - Implementation plan

> **SUPERSEDED (2026-08-18), never implemented.** Do not follow these phases. See
> [spec.md](./spec.md)'s status line and
> [writable-instance-config](../writable-instance-config/), which absorbed this work.
Branch: `feature/namespace-startup-default` (feature code never lands on `main`).
Spec: [spec.md](./spec.md). Phases are ordered so each one ends with a green build and suite.

## Phase 0 - Pin today's behaviour before changing it

The effective default is pinned by nothing (spec §9), so the first commit is tests that pass
**against current `main`** and would fail after a careless change.

- `NamespaceEndpointTest`: a host booted with `Fallen8:Namespaces:LoadOnStartup=false` skips an
  `inherit` namespace; with `true` it loads it. Assert through `GET /ns` state, not the log.
- `NamespaceDurabilityTest`: one test asserting `defaultPluginRegistrationEnabled` survives a
  namespace create - the **precedent** for 4.8's re-stamp, proving the pattern works before a second
  slot leans on it. If this test fails on `main`, stop: the precedent is broken and that is a
  separate bug report, not something to build on.

Exit: suite green, two or three new tests, no product change.

## Phase 1 - Engine-side (apiApp) storage, precedence and the write path

1. `NamespaceCatalog.cs`: add `loadOnStartupDefault` (`Boolean?`) to `NamespaceCatalogDocument`.
   Rewrite `NamespaceCatalogEntry.LoadOnStartupEnabled`'s XML doc, which currently asserts there is
   deliberately no document-level slot - it stays true for the reserved `default` namespace and is
   now false for the instance default. Do not delete the reasoning; retarget it.
2. `Fallen8Namespaces.cs`:
   - Hold the slot in a field initialised from the catalog at construction, beside
     `Default.PluginRegistrationEnabled`.
   - **Refactor `IsSelectedForStartupLoad` to take `Boolean? ownPolicy`** rather than a
     `NamespaceCatalogEntry`, so the same method answers "what does a namespace with no override
     inherit" by being called with `null`. This is what keeps §4.2's single home single - no second
     null-to-bool site anywhere.
   - Public accessors beside `MaxNamespaces`: the resolved default (slot, else configuration key -
     **not** mode-composed, per §4.5) and the mode.
   - A `TrySetLoadOnStartupDefault(...)` that writes the field, calls the catalog writer, and answers
     `false` with a reason when `_catalogPath == null` (§4.7).
   - **`WriteCatalogUnlocked`: re-stamp the slot** onto the rebuilt document (§4.8). One line, and
     the whole feature silently reverts without it.
3. `AppJsonContext.cs`: confirm the catalog DTOs are registered (they are) - a new property on an
   already-registered type needs no entry, but `JsonSourceGenParityTest` must stay green.

Exit: `dotnet build` and `dotnet test` green; phase 0's tests still pass; the slot round-trips
through a create/rename/drop in a new test.

## Phase 2 - REST surface

1. `Controllers/Model/NamespaceREST.cs`: `NamespacesREST` gains `LoadOnStartupDefault` (`Boolean`)
   and `StartupLoadMode` (`String`), both with XML `<summary>`. `CS1591` is NoWarn'd here, so a
   missing summary ships a description-less schema property instead of failing the build - check by
   eye. New request type for the collection-level PATCH body carrying `LoadOnStartupDefault`
   (`String` tri-state, same vocabulary as `NamespaceUpdateSpecification.LoadOnStartup`).
2. `Controllers/NamespacesController.cs`: fill the two fields in `GetAll` from the new accessors
   (never by re-reading `IOptions` in the controller - that would be a second home for the
   resolution). Add `PATCH /ns` with the full `[ProducesResponseType]` set including `409`, and XML
   `<summary>`/`<remarks>` saying it changes the next boot only.
3. Verify the tri-state parse is shared with the per-namespace path rather than copied.

Exit: build green. Tests from spec §9's first four bullets added and passing. Then
`powershell -File scripts/update-openapi-snapshot.ps1` and review the printed diff: expected delta is
one new path plus two properties on `NamespacesREST`, nothing removed.

## Phase 3 - MCP bridge (engine to REST to MCP)

`PATCH /ns` is a new path, so `McpRestCoverageTest` and `McpContractTest` **will fail** until this
phase lands or a reasoned deferral is recorded. Bridging is the decision taken (spec §8):

1. `Bridge/Dto/NamespacesDto.cs`: the two new read fields. `NamespaceDto`: per-entry
   `loadOnStartupEnabled` (the bridge carries 4 of 7 entry fields today and no parity gate covers
   read DTOs, so this is by hand).
2. `Tools/OverviewTool.cs`: surface both in the namespace directory, so an agent can distinguish
   "this namespace will not come back after a restart" from "this is fine".
3. `f8_namespace`: bridge the per-namespace `loadOnStartup` field and the new collection-level
   default. Today `AdminTool.cs:75-76` tells agents activation "does not change the startup policy" -
   a dead end an agent cannot escape. Closing it is the point.
4. Update `McpBridgedEndpoints`.

Exit: both MCP gates green, `dotnet test` green.

## Phase 4 - Studio

1. `src/api/types.ts` (hand-maintained mirror, no gate): the two optional fields on the list
   response, and the PATCH body type. `src/api/endpoints.ts`: `setNamespaceLoadOnStartupDefault`.
   Note `tests/api-contract.test.ts`'s `ENDPOINT_CALLS` sweep expects every client export to be
   exercised.
2. Extract `useNamespaces` / the namespaces query key into `src/state/status.ts` so both panels share
   one react-query cache entry and the Configuration row costs no extra request.
3. `NamespacesPanel.tsx`: turn `STARTUP_OPTIONS` into a function of `{loadOnStartupDefault}` so the
   third option reads `inherit (load)` / `inherit (skip)`, bare `inherit` when the field is absent.
   Leave `STARTUP_EFFECT` alone - no fourth vocabulary. **Delete** the `namespace-startup-hint`
   paragraph (§5.1). Two traps: keep the reserved `default` row's reason **under** the select (a
   recorded column-width regression at `NamespacesPanel.tsx:252-261` once pushed the actions column
   out of the scroll viewport), and give the reserved row no `inherit` annotation.
4. `ConfigurationPanel.tsx`: a `namespaces` group with the writable select, **gated on
   `!lockNamespace`** (§5.2). Mode-disabled with its reason when `startupLoadMode !== "catalog"`.
   Panel subtitle stops claiming read-only. Keep env keys behind the overlay - the body has never
   shown one and `studio.md:62` documents that split.
5. `src/app/NamespaceScope.tsx:150-153`: reword so inherit-resolving-to-skip is named.
6. Tests: the three label forms; the write path; `connect-config.test.tsx` must add `listNamespaces`
   to its `vi.mock` or eight tests fire an unmocked fetch; **widen `mount-seam.test.tsx:167-169`** to
   the new testid; negative assert that the hint is gone.

Exit: `npm --prefix fallen-8-web-ui run test` and `run build` green, exit codes confirmed via
`cmd /v:on /c "... & echo EXIT=!ERRORLEVEL!"` (vitest/tsc exit nonzero under the PowerShell wrapper).

## Phase 5 - Docs, screenshots, bookkeeping

1. Docs pages per spec §8's Docs row. `namespaces.mdx#startup-load` stays the single home; the
   heading is not renamed (link-checked build).
2. Recapture `screen-connect.png` **and** `screen-connect-observability.png` against an isolated
   already-running app (`F8_UI_URL`, never the `:5000` webServer race, never piped through
   `Select-Object -First N`). The observability capture must run against an OTLP-configured app or
   its Push section renders "off" and overwrites a good image.
3. `npm --prefix docs ci && npm --prefix docs run build`.
4. Amend the graph-namespaces LIVING README and the root README's Namespaces entry in place.
5. Move `features/open/namespace-startup-default/` to `features/done/`, spec status line updated,
   deviations recorded per phase here.

## Gates (full list)

- `dotnet build fallen-8-core.sln` (warnings are errors; a missing XML summary will **not** fail)
- `dotnet test fallen-8-core.sln` (never `-v q` - it hides the failing test name)
- `powershell -File scripts/update-openapi-snapshot.ps1` (port 5078 free; review the diff)
- `npm --prefix fallen-8-web-ui run test` and `run build`, exit codes confirmed explicitly
- `npm --prefix docs ci && npm --prefix docs run build` (link-checked)
- Both Connect screenshots recaptured
- **Not required, with reasons:** `tools/browser-probe` (`fallen-8-core` untouched, spec §6) and
  `scripts/update-provider-descriptor-snapshot.ps1` (no integration descriptor change)

## Left open

Nothing yet - this section records deviations and unfinished rows as the phases land.
