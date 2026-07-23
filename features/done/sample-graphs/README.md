# Sample graphs & Benchmark — Usage

Two Studio surfaces that show what Fallen-8 does, plus the small backend/format work that
makes them possible. Contract in [spec.md](./spec.md); phasing in [plan.md](./plan.md).

## Benchmark tab

Left nav **Benchmark** (below Canvas). Generate a random graph (`nodeCount`,
`edgesPerVertex`, presets incl. a 100k×1M "scale" preset), pick the edge **distribution**
(uniform or preferential), then run the edge-traversal benchmark (`iterations`) and read
avg/median/stddev TPS. A session-local run history keeps before/after numbers. The
benchmark traverses the edges `/generate` creates (edge property `"A"`) — a
generated-graph benchmark, not a measurement of an arbitrary loaded graph.

## Sample graphs (Dashboard)

A manifest-driven card grid. Each curated card shows counts, what it demonstrates, and a
**Load** button; loading fetches the dataset from a **public GitHub raw URL** and ingests
it, then applies a canvas style and shows suggested next steps.

| Sample | Shows off |
| --- | --- |
| 🥋 Zachary's Karate Club | label propagation vs. the real 1977 split, triangles, WCC |
| 🛡️ AD Attack Surface | Dijkstra attack paths on `exploitCost`, semantic search, choke-point analytics |
| 🎬 Movie Night | poster-image nodes, plot embeddings, rating-weighted recommendations, PageRank |
| ✈️ World Air Routes | distance-weighted shortest paths, hub analysis, country-flag emoji nodes |
| 📈 Scale 100k × 1M | (Benchmark tab) ingest speed, memory, analytics at scale |
| 📦 Fallen-8 Dependencies | multi-ecosystem dependency graph; the static twin of the GitHub card |
| 🐙 Any GitHub repo | fetches any public repo's dependency graph just-in-time |

**Loading contract.** Import requires an empty graph, so loading into a non-empty
instance is gated behind a typed-name confirm that runs Tabula rasa first (save a
checkpoint if you need one). Then: fetch → `POST /bulk/import` → create the manifest's
index recipes (a bound `VectorIndex` for embedded samples) → re-read the elements onto the
canvas with the sample's style. **No embedding generation happens at load time** — the
vectors are baked into the file and the bound index projects them.

**Embedding gate.** Bring-your-own-vector always works (the vectors ride in the file), so
vector scans work even with the provider off. Text-in features (semantic search,
`queryText`) need a provider whose identity matches the baked vectors; each card says what
works on the current instance based on `GET /status`.

## Datasets: format, storage, rebuild

- **Format** is `fallen8-jsonl` (the engine's own bulk interchange) — standard, curl-able,
  and the reason ingestion needs no special client. Embeddings travel as the reserved
  `$embedding:default` `System.Single[]` property (format **version 2**, added by this
  feature — see the [bulk-import-export README](../../done/bulk-import-export/README.md)).
- **Storage.** The built files live in the repo's top-level `samples/` and are served from
  a public raw URL; the Studio fetches from there (`VITE_F8_SAMPLES_BASE` overrides the
  base — e.g. a fork or a feature branch before merge). Only the live GitHub card is
  fetched per-request; everything else is precomputed and committed.
- **The Fallen-8 SBOM** (fallen8-deps) is fetched **once** and committed
  (`fallen-8-web-ui/scripts/samples/data/fallen8-sbom.json`); a CI job
  (`.github/workflows/refresh-sbom.yml`) refreshes it only when a dependency manifest
  changes, so no build ever hits GitHub's rate-limited SBOM endpoint at runtime.

### Rebuilding

```bash
# From fallen-8-web-ui/. Embedded samples need an instance with the embedding provider ON
# (compose environment, or a local instance wired to Ollama bge-m3) — embedding happens
# HERE, at build time, never at ingest.
npm run build:samples                        # all datasets -> ../samples/
npm run build:samples -- --only karate-club  # one sample
npm run build:samples -- --verify            # round-trip each file + bind its indices
                                             # against a live EMPTY instance (F8_BASE)
F8_DEPS_REFETCH=1 npm run build:samples -- --only fallen8-deps   # re-pull the SBOM
```

Adding a static sample = a generator in `scripts/samples/` registered in
`build-samples.ts`; the card grid renders it from the manifest with no UI change.
