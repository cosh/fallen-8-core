# Sample graphs & benchmark tab — plan

Companion to [spec.md](./spec.md); the spec owns the contract, this file owns the
phasing. Each phase ends green: `dotnet build`/`dotnet test`, web-ui `npm test` + e2e,
warnings-as-errors.

## Phase 0 — pin the interchange details ✅ (became the format-v2 change)

Findings (pinned by `BulkImportExportTest`):

- Line schema: `{"type":"vertex","id":…,"label":…,"creationDate":…,"properties":{…}}`
  (edges add `edgePropertyId`/`source`/`target`); every property is a
  `{"type": <name>, "value": <invariant string>}` pair. Strict fields, no duplicates.
- **v1 could not encode `float[]`** — embedded graphs were not even exportable (422).
  Resolved by extending the format to **version 2**: `System.Single[]` as comma-joined
  `"R"` floats; reader accepts 1–2; writer stamps the lowest sufficient version; a
  v1-stamped file carrying an array is rejected. Implemented in `JsonlGraphFormat` +
  `BulkController`, tested endpoint-to-engine (export stamps v2, vector + model stamp
  round-trip, v1-with-array 400s with line number, meta-less files read at full
  capability).
- Bound `VectorIndex` created AFTER import backfills from element state
  (`BoundIndex_CreatedOverExistingData_MaterializesImmediately`) — loader order is
  tabula rasa → import → create indices.
- `TabulaRasa_internal` calls `IndexFactory.DeleteAllIndices()` — the wipe covers
  indices; the loader recreates what its manifest declares.
- `creationDate` is a `UInt32` unix timestamp; datasets use a fixed constant for
  determinism.

## Phase 1 — Benchmark tab

- `src/screens/BenchmarkScreen.tsx`; route `/benchmarks` in `routes.tsx`; NAV entry in
  `AppShell.tsx` directly below Canvas.
- Generate controls (`nodeCount`, `edgesPerVertex`, presets incl. scale) and benchmark
  (`iterations`) per spec; session-local run history; current counts from `useStatus`.
- Remove the Dashboard Playground section; shared `generateGraph()` wrapper stays in
  `endpoints.ts` (two future call sites: this tab, scale card).
- Tests: e2e — nav gating, generate→benchmark→history flow (existing
  `generate-sample`/`run-benchmark`/`benchmark-result` testids move here); adjust the
  Dashboard e2e that expects the Playground section.

## Phase 2 — `/generate` distribution parameter (backend)

- `distribution=uniform|preferential` on `BenchmarkController.CreateGraph`, default
  `uniform`; preferential attachment in `ScaleFreeNetwork` via a repeated-endpoint
  pool; invalid value → 400. XML docs updated.
- Tests (MSTest): default unchanged; validation; preferential produces skewed degrees
  (max out-degree ≫ average on a seeded medium graph); both distributions benchmark-able.
- `pwsh scripts/update-openapi-snapshot.ps1`, review diff (additions only).
- UI: Benchmark scale preset + (later) scale card send `distribution=preferential`.

## Phase 3 — dataset pipeline + `karate-club`

- `fallen-8-web-ui/scripts/build-samples.ts` + `npm run build:samples`: sample registry,
  jsonl emitter (phase-0 contract), manifest writer, `--verify` (re-import each file,
  compare meta counts), `--only <id>`.
- `karate-club` first: hardcoded Zachary edge list, `faction` ground truth, no
  embeddings, no network — pins the whole pipeline cheaply.
- Commit `public/samples/karate-club.jsonl` + `index.json`; `.gitattributes`
  `linguist-generated` for `public/samples/*.jsonl`.
- Tests (vitest): emitter output line shapes; manifest schema guard.

## Phase 4 — curated datasets

- `attack-surface`: seeded generator (departments, machines, groups, edge
  `exploitCost`, per-node `description`); embeddings via `POST /embedding/text`
  (compose env required; batch = provider `MaxBatchSize`; write vector + model stamp
  properties).
- `movie-night`: curated ~120-movie list (title/year/genres/plot in the script),
  posters resolved once via the Wikipedia REST summary API; seeded viewers/ratings.
- `air-routes`: download OpenFlights airports/routes, top ~250 by route degree,
  haversine `km`, country→ISO-code map for flagcdn icons, `emoji` fallback property.
- `fallen8-deps`: fetch the repo SBOM, transform via the new shared
  `src/lib/sbomGraph.ts` (script + browser share this module).
- Commit all artifacts; `--verify` green; spot-check semantic search quality on a
  compose env (queries from the spec's "try" lists) and tune description texts once.
- Tests (vitest): `sbomGraph` transform against a small SBOM fixture (root node,
  ecosystems, DEPENDS_ON edges, license property); deterministic-generator snapshots
  (counts, first lines) for attack-surface/movie-night.

## Phase 5 — Sample graphs section (Dashboard)

- Replace the Playground section with the manifest-driven card grid; loading contract
  per spec (confirm+tabularasa when non-empty → fetch asset → import → index recipes →
  re-read elements → `mergeIntoCanvas` + style config → suggested steps message).
- Embedding gating note from `/status.embedding` vs. manifest metadata; scale card
  (shared `generateGraph`, preferential, elapsed-time display, memory warning).
- Tests: vitest for the loader's decision logic (empty vs. non-empty, provider
  match/mismatch/off); e2e — load `karate-club` end-to-end into the canvas, card grid
  renders from the manifest.

## Phase 6 — GitHub card (dynamic dependency graphs)

- Repo input → SBOM fetch in the browser (anonymous) → `sbomGraph` → the phase-5
  loading contract; >5 000-package legibility note; honest 404/403-rate-limit/empty
  handling per spec.
- Tests: vitest with mocked fetch (success, 404, 403 with rate-limit headers, huge
  SBOM note); e2e happy path only if the runner has network, otherwise mocked.

## Phase 7 — docs & sweep

- Feature `README.md` (usage: loading samples, rebuilding datasets, adding a sample =
  script registry entry + manifest, GitHub card limits).
- One-line pointers: bulk-import-export README (datasets ship in this format);
  root README stays untouched (readme-with-visuals follow-up noted in spec).
- Re-run the spec's impact sweep; `features/open/sample-graphs` → `features/done/` on
  merge.
