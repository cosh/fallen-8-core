# Plan — studio index discovery

## Phase 1 — API: index inventory on `GET /status`

- Rename `SaveGameIndexREST` → `IndexDescriptionREST` (own file under
  `Controllers/Model/`), update `SaveGameREST`, `SaveGameRegistry`, `AppJsonContext`.
- `StatusREST.Indices : List<IndexDescriptionREST>` + XML docs/example.
- `AdminController.Status()` fills it from `IndexFactory.GetIndexPluginTypesSnapshot()`.
- Tests: extend `JsonSourceGenParityTest` StatusREST samples; endpoint test that
  `POST /index` → `GET /status` lists the index with its plugin type, and that
  `SpatialIndex` creation over REST answers `false` (pins the "not creatable" contract
  the UI note relies on).
- Regenerate the OpenAPI snapshot (`pwsh scripts/update-openapi-snapshot.ps1`).

## Phase 2 — Studio

- `npm run gen:api`; add `indices` to the hand-maintained `StatusREST` type (reuse the
  existing `SaveGameIndex` shape, renamed `IndexDescription`).
- `QueryScreen`:
  - status query (shared key `[instance.id, "status"]`) feeding: plugin-type `<select>`,
    `shape-index-ids` datalist union, per-type option gating (vector fields / "no
    options" hint / spatial create disabled).
  - invalidate the status query on create/delete success.
  - update the free-form-ids footer note (index ids are now live; property/label
    suggestions still come from the Graph-shape snapshot).
- Field-help entries for the changed inputs.

## Phase 3 — Verify

- `dotnet build` + `dotnet test` (full suite), `npm run build` + `npm test`.
- Manual pass over the Query screen against a running instance.

## Status

- [x] Phase 1
- [x] Phase 2
- [x] Phase 3 (builds + suites green; manual pass against a running instance still open)
