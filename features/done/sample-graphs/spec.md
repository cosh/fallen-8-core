# Sample graphs & benchmark tab — spec

Status: **done** · Branch: `feature/sample-graphs` · Created: 2026-07-23

## Motivation

The Dashboard "Playground" is two buttons (`GET /generate`, `GET /benchmark`) that produce
an anonymous random graph and a TPS number. Neither shows what Fallen-8 is actually good
at: labeled graphs styled on the canvas (emoji/image nodes, property-driven color/size/
width), weighted path finding, analytics, spatial and vector indices, semantic search.

This feature splits the playground into two surfaces:

1. **Benchmark** becomes a top-level tab (left nav, directly below Canvas) — its own
   growing workspace, starting with the existing traversal benchmark.
2. **Playground is renamed to "Sample graphs"** and becomes a gallery of curated,
   one-click demo graphs that exercise the whole feature surface — plus a dynamic
   showcase that pulls the dependency graph of *any* public GitHub repository into the
   canvas just in time.

## Non-goals (v1)

- No guided tours / scripted walkthroughs in the UI. Each sample ships *suggested next
  steps* as plain text; automation of those steps is a later feature.
- No GitHub authentication. Dynamic dependency fetch is anonymous (60 requests/hour per
  IP is plenty for a demo); a token field is a revisit trigger, not v1 machinery.
- No streaming/progress protocol for `/bulk/import` — the existing single POST is fine
  at these dataset sizes (≤ ~10 MB).
- No server-side proxying of GitHub or image traffic. The browser talks to
  `api.github.com` and image CDNs directly (CORS verified: `Access-Control-Allow-Origin: *`).

## UX contract

### Benchmark tab

- New nav entry **Benchmark** in `AppShell.tsx` `NAV`, directly below Canvas. Studio
  route is **`/benchmarks`** (plural — the singular collides with the API route
  `GET /benchmark` on full-page loads, same convention as `/indexes`, `/subgraphs`).
- Contents v1:
  - **Graph generation** controls: `nodeCount`, `edgesPerVertex` (defaults 200 / 5),
    plus presets ("small 200×5", "medium 10k×10", "scale 100k×10 ≈ 1M edges"). Calls the
    existing `GET /generate`. Non-empty-graph is fine here (`/generate` adds on top);
    the UI shows current vertex/edge counts from `/status` so the state is visible.
  - **Run benchmark**: `iterations` input (default 1000), calls `GET /benchmark`,
    renders the existing stat grid (edges traversed, avg/median/stddev TPS).
  - **Run history**: a session-local table of runs (timestamp, graph size, iterations,
    results) so before/after comparisons are possible. Not persisted.
  - An honest one-liner: the benchmark traverses the edges `/generate` creates (edge
    property `"A"`); it is a generated-graph benchmark, not a benchmark of whatever is
    loaded. (Benchmarks over arbitrary loaded graphs = future work on this tab.)
- The Dashboard **Playground section is removed**; both buttons move out (generate →
  Benchmark tab, benchmark → Benchmark tab).

### Sample graphs (Dashboard section)

The renamed section stays on the Dashboard (the user asked for a rename, not a new tab;
promoting it to its own tab is a named revisit trigger below). It renders a **card grid
driven by a manifest** (`/samples/index.json`, shipped with the Studio): per sample —
emoji, title, one-line pitch, vertex/edge counts, badges for what it demos (canvas,
path, analytics, semantic, spatial), a **Load** button, and an expandable "what to try"
list. Two special cards are code, not manifest data: the **scale card** (calls
`/generate` with the 100k×10 preset) and the **GitHub card** (repo input, dynamic fetch).

### Loading contract

`POST /bulk/import` requires an empty graph (409 otherwise) — the loader embraces that:

1. If `/status` reports a non-empty graph, a `ConfirmDialog` (typed instance-name
   confirmation, same as Tabula rasa) warns that loading **replaces** the current graph;
   on confirm the loader runs `PUT /tabularasa` first. Empty graph → no dialog.
2. Fetch `/samples/<id>.jsonl` (same-origin static asset) and `POST /bulk/import` it.
3. Create the manifest's **index recipes** via `POST /index` — for embedded datasets a
   bound `VectorIndex` (`embeddingName: "default"`, dimension/metric/model from the
   manifest); the bound index projects the imported vectors automatically
   (element-embeddings contract).
4. Fetch the imported elements (import remaps ids, so re-read via the same listing path
   the Browser screen uses), `mergeIntoCanvas`, and apply the manifest's **style
   config** (e.g. `nodeImageProperty: "icon"`, color-by-`faction`, edge width by
   `rating`). All samples stay well under the 5 000-element canvas label cutoff.
5. Show counts + the sample's suggested next steps + an "open Canvas" link.

Failure honesty: import failures surface the API's problem+json (line number et al.)
verbatim; a partially-committed import (documented bulk-import behaviour) shows the
committed counts and offers Tabula rasa. Nothing retries silently.

### Embedding capability gating

Datasets ship **precomputed vectors** (bge-m3, 1024-dim, Cosine — the compose default),
so import works with the embedding provider *off*: vectors load, the bound index
projects them, `POST /scan/index/vector` works. What needs the provider is **text-in**
(`/embedding/search`, semantic `queryText`): the sample card shows a short note when
`/status.embedding` is disabled or its `modelName`/`dimension` differ from the
manifest's (mismatched provider → text-in answers 409 by the embedding-provider
contract; the note says exactly that). Vector provenance is honest: the build script
writes the `$embeddingModel:default` stamp alongside each vector.

## The lineup

Five curated samples (the ask: cyber security, recommendation, a university classic,
scale, plus one more) and the dependency-graph showcase. Static datasets are
`fallen8-jsonl` files built by the script below and committed to the repo's top-level
`samples/`; the Studio fetches them from a **public GitHub raw URL** at ingest (decoupled
from the Studio bundle — `VITE_F8_SAMPLES_BASE` overrides the base). Each is small enough
to render fully on the canvas.

Counts below are the actual built figures (the manifest is the runtime source of truth).

| id | title | vertices | edges | shows off |
|---|---|---|---|---|
| `karate-club` | Zachary's Karate Club | 34 | 78 | analytics classics: label propagation vs. the real split, triangles, WCC |
| `attack-surface` | AD Attack Surface | 117 | 142 | weighted attack paths (Dijkstra), semantic search, choke-point analytics |
| `movie-night` | Movie Night | 191 | 1 697 | recommendation traversals, poster-image nodes, plot embeddings, PageRank |
| `air-routes` | World Air Routes | 250 | 5 702 | distance-weighted shortest paths, hub analysis, flag-emoji nodes |
| *(generated)* | Scale: 100k × 1M | 100 000 | ~1 000 000 | ingest speed, memory footprint, analytics at scale, benchmark pairing |
| `fallen8-deps` | Fallen-8 Dependencies | 392 | 517 | multi-ecosystem dependency graph; the static twin of the dynamic GitHub card |

Every static sample carries an `icon` property per node — the canvas renders emoji and
image URLs from the *same* property, per value (studio-canvas-viz contract), so movies
show posters 🎬 next to emoji viewers 👤 with zero extra config.

### `karate-club` — the university classic

Zachary (1977), the most-cited graph in community detection. Members carry `name`
("Mr. Hi", "Officer", "Member 3"…), the ground-truth `faction` property, icon 🥋.
Try: LABELPROPAGATION and color by the computed partition, then color by `faction` and
compare; TRIANGLECOUNT; the Mr. Hi → Officer shortest path. No embeddings (no text).

### `attack-surface` — cyber security

A BloodHound-flavoured synthetic Active-Directory estate (seeded, deterministic):
users 👤, admins 🧑‍💼, service accounts 🤖, workstations 💻, servers 🖥️, domain
controllers 🏰, groups 👥 across departments. Edges `memberOf`, `adminTo`, `hasSession`,
`canRDP`, `runsAs` carry an `exploitCost` (Double) — path cost = attack difficulty.
Every node has a one-line `description` ("File server holding the finance quarterly
reports…") — embedded. Try: cheapest attack path from the phished intern's workstation
to the Domain Admins group (Dijkstra on `exploitCost`); `/embedding/search` "where do
the financial documents live"; DEGREE/PAGERANK to find lateral-movement choke points.

### `movie-night` — recommendation

~40 iconic movies + genre nodes 🏷️ + ~140 synthetic viewers 👤 (seeded taste profiles →
real community structure). Edges: `belongsTo`, `rated` (`rating` Double → edge width).
Movies carry `title`, `year`, a short `plot` (embedded), and `icon` = the Wikipedia
poster thumbnail URL (resolved once at build time from a pinned page title; hotlinked from
`upload.wikimedia.org`, with 🎬 fallback when a poster is missing). Try: semantic search
"mind-bending sci-fi about dreams"; a 2-hop viewer→movie→viewer→movie recommendation path;
PAGERANK for the canon; color by label, size by degree, edge width by rating.

### `air-routes` — the fifth graph

OpenFlights data (top ~250 airports by route degree, routes deduped per pair): `iata`,
`city`, `country`, `lat`/`lon` (Doubles), route `km` (haversine, Double) as Dijkstra
cost, `icon` = the country's **flag emoji** (offline-friendly, from an OpenFlights
country→ISO-3166 map committed once), ✈️ where a country has no code. A one-line
description per airport is embedded. Try: a cheapest-km route between two airports;
semantic search ("major airports in Japan"); hubs by degree/PageRank.

**No SpatialIndex** (deferred — future work). The REST `/index` recipe only carries typed
*literal* plugin options; a SpatialIndex needs `IMetric` and an `IEnumerable<IDimension>`
that literals can't express, so it is not declaratively creatable over REST. `lat`/`lon`
ship as Double properties regardless, ready for the day that lands.

### Scale card — 100k vertices, ~1M edges

Not a file: `GET /generate?nodeCount=100000&edgeCount=10` from the card (same endpoint
the Benchmark tab uses; one shared client wrapper, two call sites). The card reports
elapsed time and points at `/status` memory, the Analytics screen (PAGERANK/WCC at
scale), and the Benchmark tab. See "Backend changes" for making the generated graph's
degree distribution worth analyzing.

### `fallen8-deps` + the GitHub card — dependency graphs

The static default is this repository's own dependency graph, built at script time from
GitHub's SBOM export (`GET /repos/cosh/fallen-8-core/dependency-graph/sbom`, SPDX:
392 packages, 517 real package→package DEPENDS_ON relationships across npm / NuGet /
pypi / GitHub Actions). Nodes: label = ecosystem, props `name`, `version`, `license`,
`purl`, ecosystem emoji (📦 🐍 💠 ⚙️), plus a root repo node. Edges: `dependsOn`.
Try: PAGERANK = most-depended-on packages; WCC per ecosystem; color by `license`.

The **GitHub card** does the same *in the browser*: the user enters `owner/repo`, the
client fetches the SBOM from `api.github.com` (CORS-open, anonymous), transforms it
with the **same shared module** the build script uses (`src/lib/sbomGraph.ts` — one
transform, two consumers), and hands the result to the same loading contract as static
samples (wipe-confirm → import → canvas). Honest errors: 404 (no repo / private),
403 with rate-limit headers ("anonymous limit reached, try in N minutes"), empty SBOM.

## Dataset format & build pipeline

**Format:** exactly `fallen8-jsonl` (bulk-import-export feature) — datasets are
ordinary interchange files, loadable by `curl` as much as by the Studio. Embeddings ride
as the reserved `$embedding:default` float[] property with the `$embeddingModel:default`
stamp (element-embeddings v1 layout; the engine projects them on import). Implementation
finding: format v1 could not encode a `float[]` at all — an embedded graph was not even
*exportable* (422). This feature added **format version 2**: `System.Single[]` as a
comma-joined `"R"`-float typed pair. The format is now standardized on v2 — the writer
always stamps it and `System.Single[]` is always available; the reader tolerates an older
v1 stamp identically (no version-dependent behaviour) and unknown versions are rejected.

**Manifest:** `samples/index.json` — per sample: id, title, emoji, pitch, counts, demo
badges, suggested next steps, style config, index recipes, embedding metadata (`model`,
`dimension`, `metric`). The card grid renders purely from the manifest; adding a sixth
static sample is a data change, not a UI change.

**Delivery:** the client fetches `${VITE_F8_SAMPLES_BASE}/index.json` then
`${base}/<file>` (default: this repo's `samples/` on `main` via GitHub raw, CORS-open and
anonymous) and POSTs the file to `/bulk/import`. **No embedding generation at ingest** —
the vectors are baked into the file; a bound `VectorIndex` recipe projects them.

**Build script:** `fallen-8-web-ui/scripts/build-samples.ts` (`npm run build:samples`; TS
so it shares `sbomGraph.ts` and the jsonl emitter with app code — no duplicated
transform). One generator per sample in `scripts/samples/`. Inputs: hardcoded edge list
(karate), seeded PRNG (attack-surface, movie viewers), a curated movie list with posters
resolved via the Wikipedia REST summary API, OpenFlights CSVs (downloaded at build time;
country→ISO map committed once), the **stored** GitHub SBOM. Embeddings via **`POST
/embedding/text`** against a running instance whose provider is on (compose, or a local
instance wired to Ollama bge-m3) — the same provider path Fallen-8 uses; small batches
(the CPU provider's HttpClient times out at 100s on a 64-batch). Deterministic where it
can be: fixed seeds and `creationDate`, vectors rounded to 5 decimals (≈ 10⁻⁵ cosine
error, smaller files). `--verify` re-imports every produced file AND binds its index
recipes against a live empty instance.

**Artifacts are committed** to `samples/` (embedded files are a few MB each; karate is
~16 KB): the Studio serves working demos with no build-time Ollama or network. Marked
`linguist-generated` in `.gitattributes`. Regeneration is needed only when a dataset
definition or the embedding model changes. The Fallen-8 SBOM is fetched once and stored
(`scripts/samples/data/fallen8-sbom.json`); CI (`.github/workflows/refresh-sbom.yml`)
refreshes it only when a dependency manifest changes.

## Backend changes (small, one honest fix)

`ScaleFreeNetwork.CreateScaleFreeNetworkAsync` generates **uniform-random** edges
despite its name — fine for the traversal benchmark, but flat and boring for
analytics-at-scale demos (no hubs, no skew). v1 adds an optional
`distribution=uniform|preferential` query parameter to `GET /generate`
(default `uniform`, existing behaviour and benchmark comparability preserved;
`preferential` = Barabási–Albert-style attachment via a repeated-endpoint pool, O(E)
memory). The scale card and the Benchmark "scale" preset use `preferential`, so PAGERANK
at 100k×1M shows real hubs. Route/XML-doc change → OpenAPI snapshot regeneration; MSTest
coverage for both distributions (param validation, degree-skew sanity).

No other engine or API changes: import, indices, embeddings, path, analytics are all
consumed as-is. (`SampleGraphController`'s `/unittest` fixed graph is untouched.)

## Failure modes & honest limits

- **External images are hotlinked** (Wikimedia posters, flagcdn flags). Offline, those
  nodes fall back to plain styled circles — datasets therefore also carry an emoji-only
  `emoji` property; switching `nodeImageProperty` to it is one dropdown change. The
  suggested-steps text says so.
- **GitHub rate limit** (60/h anonymous) and repo size: SBOMs with > ~6 000 packages get
  a confirm note before import (canvas label cutoff is 5 000; the graph still loads and
  analytics work — it's a canvas-legibility warning, not a cap).
- **Tabula rasa scope**: loading replaces graph *and* index state; the loader recreates
  what its manifest declares and nothing else. Save-games are untouched and remain the
  undo story ("save first" is in the confirm dialog).
- **Scale card duration/memory**: generation of 100k×1M is seconds-to-tens-of-seconds
  server-side; the card shows the API's own elapsed-time response and warns about
  memory on the smallest instances.

## Impact on existing features (mandatory sweep)

- **bulk-import-export** — **extended, not just consumed**: the format is standardized on
  version 2, which adds `System.Single[]` (see "Dataset format"), fixing the latent gap
  where an embedded graph could not be exported (422 on `$embedding:*`). The writer always
  stamps v2; the reader tolerates an older v1 stamp identically. Its README documents the
  encoding; the historical spec is untouched.
- **element-embeddings / embedding-provider / embedding-out-of-box** — consumed
  (reserved-property import path, bound-index projection, `/embedding/text`,
  `/status` gating); the element-embeddings README's "rides bulk import/export" claim
  is now true over REST too and points at the v2 encoding.
- **studio-canvas-viz** — consumed unchanged (icon/image property, palette, size/width
  mapping). Sample style configs are data, not new style-engine capability.
- **index-workspace** — loader-created indices appear on the Indexes screen as normal;
  no change.
- **web-ui / studio-coverage** — Dashboard loses the Playground section; new Benchmark
  screen and Sample-graphs section need e2e coverage (`e2e/studio.spec.ts`) and unit
  tests (sbomGraph transform, manifest handling, loader gating).
- **OpenAPI snapshot** — regenerated once for the `/generate` `distribution` parameter.
- **NL-assist (dataset/eval)** — no query-contract change → **no retrain entry**.
  Side benefit only: sample graphs give NL-assist something real to query.
- **DEBUGGING.md / compose / ports** — untouched (no launch or port changes).
- **Root README** — a screenshot-worthy follow-up once implemented
  (readme-with-visuals style), out of scope here.

## Revisit triggers

- Sample gallery outgrows a Dashboard section (guided tours, > ~8 cards, per-sample
  screenshots) → promote to its own top-level tab.
- Anonymous GitHub rate limit actually bites demo usage → optional token field
  (session-only, sent exclusively to `api.github.com`).
- A second benchmark kind lands (e.g. loaded-graph traversal, path/analytics timing) →
  the Benchmark tab grows sections; the `"A"`-property coupling gets revisited then.
- Datasets grow past ~25 MB committed → move generation to a release artifact instead
  of git.

## Acceptance criteria

1. Left nav shows **Benchmark** directly below Canvas; generate + benchmark + run
   history work there; the Dashboard Playground section is gone.
2. Dashboard shows **Sample graphs** with five curated cards + scale card + GitHub
   card, rendered from the manifest.
3. Each static sample loads via wipe-confirm → import → indices → styled canvas in one
   flow; counts match the manifest; suggested steps display.
4. `attack-surface`, `movie-night`, `air-routes` support text-in semantic search on a
   compose environment out of the box; with the provider off, the cards say exactly
   what still works (vector scan) and what doesn't (text-in).
5. The GitHub card loads `cosh/fallen-8-core` (and any public repo) into the canvas
   just in time, with honest rate-limit/404 errors.
6. `npm run build:samples` reproduces every committed dataset deterministically
   (given a compose environment for embeddings); `--verify` round-trips them.
7. `dotnet test` green (including new `/generate` distribution tests); OpenAPI snapshot
   updated; web-ui unit + e2e suites green.
