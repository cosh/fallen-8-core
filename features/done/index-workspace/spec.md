# Index workspace

Indexes get their own top-level home in F8 Studio, and the Query screen becomes
query-only. Today both concerns share one screen: index lifecycle (create/delete), the
vector-add form and the inventory chips sit below the scan form on **Query**, while the
scan form itself is *mechanism-first* — the user picks "index scan / range / fulltext /
spatial / vector" from a dropdown and then types the index id into a free-form field, so
a typo silently returns zero results instead of an error.

## Behaviour

### Indexes screen (new, top-level)

A new `Indexes` nav item (route `/indexes` — plural, so the SPA fallback never collides
with the API's `/index/...` routes, same trick as `/subgraphs`). It owns everything
about index *objects*:

- **Inventory table** fed by the live `/status` inventory: id, plugin type, capability
  chips, key/value counts, and — for bound vector indexes — the embedding binding and
  model stamp. Row actions: *Query* (jumps to the Query screen with the index
  preselected) and *Delete*.
- **Create** with the plugin-type dropdown from `availableIndexPlugins`, the
  VectorIndex option block (dimension, metric, optional embedding binding, optional
  model stamp) and the SpatialIndex create gating — moved unchanged from the Query
  screen.
- **Delete behind a confirmation dialog** (today it is a one-click destructive button).
  The dialog copy is honest about what is lost: a *bound* vector index rebuilds its
  projection from element embeddings on recreation, every other index loses its content
  until it is re-populated.
- **Content management** for the selected index, completing the API surface that has had
  client bindings but no UI:
  - add a graph element under a typed key — `PUT /index/{id}` (key-literal indexes only),
  - remove a key — `DELETE /index/{id}/propertyValue`,
  - remove a graph element — `DELETE /index/{id}/{elementId}`,
  - the vector-add form (element id, property / explicit vector) — `PUT /index/vector/{id}`.
  A bound vector index disables all content forms with the existing "self-maintained
  projection" explanation: adds are rejected by the server, and removes would just fight
  the writer thread.
- **No update.** Index definitions are immutable in the engine; the screen offers
  create / inspect / delete, and the delete confirmation carries the recreate story.
- **Vector generation stays as-is** (decision 2026-07-22): the screen *explains* the
  bound-embedding path (bind at creation → write embeddings on elements → the index
  follows); bulk vectorization remains a pipeline concern. No server-side vectorization
  rules.

### Query screen (reworked)

Two modes: **property scan** (the index-less path, unchanged) or **index query**. Index
query is *index-first*: pick the index from a dropdown of the live inventory (free-form
input remains as fallback for servers whose `/status` predates the inventory), and the
UI derives the legal query forms from the index's server-reported capabilities:

| capability | form | endpoint |
|---|---|---|
| `equality` | operator + typed literal + result type | `POST /scan/index/all` |
| `range` | typed limits + inclusivity + result type | `POST /scan/index/range` |
| `fulltext` | query string | `POST /scan/index/fulltext` |
| `spatial` | reference element + distance | `POST /scan/index/spatial` |
| `vector` | kNN block (vector or provider text) | `POST /scan/index/vector`, `POST /embedding/search` |

An index with several capabilities (RangeIndex: equality + range; RegExIndex: equality +
fulltext) gets a small form toggle. When the server reports no capabilities (older
instance), the client falls back to a pluginType map for the five built-ins and offers
every form for unknown types.

Index management disappears from the Query screen entirely. The Graph-shape panel's
"Scan" prefill keeps working (it now preselects the index in index-query mode).

### `/status` contract extension

`IndexDescriptionREST` gains three nullable, live-only fields (same convention as the
existing `embeddingName` / `model`: `/status` populates them, save-game KPIs do not):

- `capabilities: string[]` — which query families the index answers, derived from the
  interfaces the index implements, not from a name table, so third-party plugins report
  honestly: `IVectorIndex` → `vector`; `ISpatialIndex` → `spatial`; `IFulltextIndex` →
  `equality` + `fulltext`; `IRangeIndex` → `equality` + `range`; any other `IIndex` →
  `equality`. Vector and spatial indexes do NOT report `equality`: their keys (float[],
  geometry) cannot travel as the wire's `IComparable` literal.
- `keys: int?`, `values: int?` — `CountOfKeys()` / `CountOfValues()` snapshots, so the
  Indexes screen shows population without a budgeted Graph-shape pass. `CountOfValues`
  is O(entries) on bucket indexes; at the self-hosted scale this repo targets that is
  negligible for a 15 s status poll. Revisit trigger: if a profiled status poll ever
  shows the counts dominating, move them behind an opt-in query flag.

No engine changes: capabilities are interface checks and counts are existing `IIndex`
members, all read in `AdminController.Status()`.

## Impact on existing features

- **studio-index-discovery** (done): its Query-screen placement of the create/delete UI
  moves here; the `/status` inventory contract it introduced is extended, not changed.
  Its spec/plan stay as historical records; `StatusIndexInventoryTest` grows assertions
  for the new fields.
- **studio-coverage** (done): FR-10 (index management on Query) is superseded by this
  feature; the scan flows (FR-8/9/11) survive re-shaped into the index-first form.
  `query-scans.test.tsx`, `index-management.test.tsx` and `embedding-query.test.tsx`
  are re-pinned against the new layout, same request payloads.
- **element-embeddings / embedding-provider** (done): behaviour untouched; the bound
  index guardrails and the semantic (text) kNN path move house with their forms.
- **save-games** (done): `SaveGameKpis` embeds `IndexDescriptionREST`; the new fields
  are nullable and stay live-only, matching the existing `embeddingName` precedent.
- **OpenAPI snapshot**: additive schema change on `IndexDescriptionREST` → regenerate;
  additions only.
- **NL-assist**: unaffected — the dataset and eval cover delegate generation, which no
  index screen touches. No RETRAIN-LOG entry needed.
- **Persisted recipes / stored queries**: unaffected — no query contract changes.

## Non-goals

- Server-side vectorization rules / bulk backfill (explicitly declined 2026-07-22).
- An index rename/reconfigure endpoint (engine-level "update").
- Making SpatialIndex creatable over REST.
- Changing any scan endpoint's request or response shape.
