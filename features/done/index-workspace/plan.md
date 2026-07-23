# Index workspace — implementation plan

Branch: `feature/index-workspace`. Phases land as separate commits; each leaves the
build green and the tests passing.

## Phase 1 — `/status` inventory: capabilities + counts

- `IndexDescriptionREST`: add `capabilities` (`List<String>`, nullable), `keys` /
  `values` (`Int32?`). XML docs carry the capability derivation table and the live-only
  (not in save-game KPIs) note.
- `AdminController.Status()`: derive capabilities from interface checks
  (`IVectorIndex`, `ISpatialIndex`, `IFulltextIndex`, `IRangeIndex`, base `IIndex`),
  read `CountOfKeys()` / `CountOfValues()`.
- `StatusIndexInventoryTest`: assert capabilities and counts per family
  (DictionaryIndex, RangeIndex, RegExIndex, VectorIndex) and that counts move when
  content is added.
- Regenerate the OpenAPI snapshot (`pwsh scripts/update-openapi-snapshot.ps1`) —
  additions only.

Status: done

## Phase 2 — Indexes screen

- `src/screens/IndexesScreen.tsx`: inventory table (id, type, capability chips, counts,
  binding badge, Query / Delete row actions), create panel (moved from
  `QueryScreen.IndexManagement`), delete behind `ConfirmDialog` with
  bound-vs-content-loss copy, content-management panel (typed-key add / remove-key /
  remove-element via existing `endpoints.ts` bindings + the vector-add form), bound
  indexes fully gated with the self-maintained note.
- Client types: `IndexDescription` gains optional `capabilities`, `keys`, `values`.
- Nav + route: `Indexes` item in `AppShell` NAV, `/indexes` route in `routes.tsx`.
- `fieldHelp.ts`: keys for the new fields.
- Tests: retarget `index-management.test.tsx` at `IndexesScreen` (same behaviour pins:
  plugin dropdown, per-type options, spatial gating, honest created-vs-not message,
  inventory refetch), plus new pins: delete confirmation flow, content-management
  payloads, bound-index gating.

Status: done

## Phase 3 — Query screen goes index-first

- `QueryScreen.tsx`: two modes (property scan / index query); index dropdown fed by the
  live inventory with free-form fallback; query form derived from server-reported
  capabilities with the client pluginType map as fallback for older servers; form
  toggle for multi-capability indexes; index-management section removed.
- `ScanPrefill` simplifies to `{ indexId }`; `GraphShapePanel` updated.
- Tests: re-pin `query-scans.test.tsx` and `embedding-query.test.tsx` on the new flow —
  identical request payloads, new interaction path.

Status: done

## Phase 4 — docs + finish

- Feature `README.md`: the living doc for the index workspace (screen layout, capability
  table pointer, bound-index story pointer).
- Sweep one-line pointers: root README Studio section if it names the Query screen's
  index management; `features/done/studio-*` docs stay historical.
- Full `dotnet test`, web-ui `npm test`, `npm run build`; move
  `features/open/index-workspace/` → `features/done/`.

Status: done
