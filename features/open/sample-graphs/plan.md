# Sample graphs & benchmark tab — plan

Companion to [spec.md](./spec.md); the spec owns the contract, this file owns the
phasing. Each phase ends green: `dotnet build`/`dotnet test`, web-ui `npm test` + e2e,
warnings-as-errors.

**Status:** phases 0–7 implemented on `feature/sample-graphs`. Deltas from the original
plan, all reflected in the spec: (a) datasets are served from a **public GitHub raw URL**
(top-level `samples/`), not bundled in the Studio `public/`; (b) **no SpatialIndex** sample
— the REST `/index` recipe can't express its non-literal options (deferred, future work);
(c) `movie-night` is a curated ~40-film set (hand-authoring 120 plots was out of scope);
(d) the Fallen-8 SBOM is fetched once and **stored**, refreshed by CI. Marked ✅ below.

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

## Phase 1 — Benchmark tab ✅

- `src/screens/BenchmarkScreen.tsx`; route `/benchmarks` in `routes.tsx`; NAV entry in
  `AppShell.tsx` directly below Canvas.
- Generate controls (`nodeCount`, `edgesPerVertex`, presets incl. scale) and benchmark
  (`iterations`) per spec; session-local run history; current counts from `useStatus`.
- Remove the Dashboard Playground section; shared `generateGraph()` wrapper stays in
  `endpoints.ts` (two future call sites: this tab, scale card).
- Tests: e2e — nav gating, generate→benchmark→history flow (existing
  `generate-sample`/`run-benchmark`/`benchmark-result` testids move here); adjust the
  Dashboard e2e that expects the Playground section.

## Phase 2 — `/generate` distribution parameter (backend) ✅

- `distribution=uniform|preferential` on `BenchmarkController.CreateGraph`, default
  `uniform`; preferential attachment in `ScaleFreeNetwork` via a repeated-endpoint
  pool; invalid value → 400. XML docs updated.
- Tests (MSTest): default unchanged; validation; preferential produces skewed degrees
  (max out-degree ≫ average on a seeded medium graph); both distributions benchmark-able.
- `pwsh scripts/update-openapi-snapshot.ps1`, review diff (additions only).
- UI: Benchmark scale preset + (later) scale card send `distribution=preferential`.

## Phase 3 — dataset pipeline + `karate-club` ✅

- `scripts/build-samples.ts` + `npm run build:samples`: registry, shared jsonl emitter
  (`src/lib/jsonlGraph.ts`), manifest writer, `--verify` (re-import + bind indices),
  `--only <id>` (preserves other manifest entries from the existing index.json).
- `karate-club`: hardcoded Zachary edge list, `faction` ground truth, no embeddings/
  network — pins the pipeline. Output to top-level `samples/`; `.gitattributes`
  `linguist-generated`.
- Tests (vitest): emitter line shapes + version stamping (`tests/jsonl-graph.test.ts`).

## Phase 4 — curated datasets ✅

- `attack-surface`: seeded AD estate (departments, machines, groups, `exploitCost`,
  per-node `description`); embeddings via `POST /embedding/text` (small batches — the CPU
  provider times out on a 64-batch), vector + `$embeddingModel` stamp baked in.
- `movie-night`: curated ~40-film list (title/year/genres/plot in the script), posters
  resolved once via the Wikipedia REST summary API (🎬 fallback), seeded viewers/ratings.
- `air-routes`: OpenFlights airports/routes, top ~250 by route degree, haversine `km`,
  **flag-emoji** icons from a committed OpenFlights country→ISO map (✈️ fallback).
- `fallen8-deps`: the **stored** SBOM → shared `src/lib/sbomGraph.ts` (script + browser).
- Tests (vitest): `sbomGraph` transform against a fixture (`tests/sbom-graph.test.ts`).

## Phase 5 — Sample graphs section (Dashboard) ✅

- `SampleGraphsPanel` — manifest-driven card grid + loader (`src/lib/sampleLoader.ts`):
  confirm+tabularasa when non-empty → fetch from the public URL → import → index recipes →
  re-read → `mergeIntoCanvas` + style config → suggested steps. Embedding gate from
  `/status.embedding` vs. manifest metadata. Scale card points at the Benchmark tab.
- Tests (vitest): the embedding gate's four verdicts (`tests/sample-loader.test.ts`).

## Phase 6 — GitHub card (dynamic dependency graphs) ✅

- Repo input → SBOM fetch in the browser (anonymous) → `sbomToGraph` → the same loading
  contract; honest 404 / 403-rate-limit / empty-SBOM handling; `normalizeRepo` accepts
  owner/repo and URL forms.
- Tests (vitest): `normalizeRepo` accept/reject (`tests/sample-loader.test.ts`).

## Phase 7 — docs & sweep ✅ / in progress

- Feature `README.md` written (surfaces, loading contract, rebuild, storage).
- One-line pointers: bulk-import-export + element-embeddings READMEs point at the v2
  encoding. Root README untouched (readme-with-visuals follow-up).
- Impact sweep in the spec refreshed. `features/open/sample-graphs` → `features/done/`
  on merge.
