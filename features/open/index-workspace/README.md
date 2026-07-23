# Index workspace

Indexes have their own top-level **Indexes** screen in F8 Studio; the **Query** screen is
query-only and index-first. (`spec.md` / `plan.md` in this directory are the historical
design records.)

## Indexes screen (`/indexes`)

- **Inventory** — the live `/status` inventory: id, plugin type, the query families the
  index answers, key/value counts, and the bound-embedding badge for self-maintained
  vector projections. The capability derivation contract lives on the server's
  `IndexDescriptionREST.Capabilities`; the client fallback for older servers is
  `fallen-8-web-ui/src/lib/indexCapabilities.ts`.
- **Create** — plugin types from server discovery; VectorIndex takes dimension, metric,
  optional embedding binding and model stamp; SpatialIndex is not creatable over REST.
- **Delete** — behind a typed confirmation that states the consequence honestly: a bound
  vector index rebuilds its projection on recreation, everything else loses its content.
  There is no update: index definitions are immutable.
- **Content** (click a row) — typed-key add / remove-key for key-literal indexes, the
  vector-add form for raw vector indexes, remove-element for all; a bound index shows
  only the self-maintained note. Bulk loads belong to pipelines, not a browser.

Vector generation is unchanged by this feature: bind an index to an embedding name at
creation and write embeddings on the elements (Browser → Embeddings, or the embedding
provider) — see [element-embeddings](../../done/element-embeddings/) and
[embedding-provider](../../done/embedding-provider/).

## Query screen (`/query`)

Two modes: **property scan** (walks elements, no index) or **ask an index** — pick the
index from the live inventory (free-form fallback when none is known) and the query form
follows its capabilities:

| capability | form | endpoint |
|---|---|---|
| equality | operator + typed literal | `POST /scan/index/all` |
| range | typed limits + inclusivity | `POST /scan/index/range` |
| fulltext | query string | `POST /scan/index/fulltext` |
| spatial | reference element + distance | `POST /scan/index/spatial` |
| vector | kNN by vector or provider text | `POST /scan/index/vector`, `POST /embedding/search` |

Behaviour pins: `StatusIndexInventoryTest` (server contract),
`tests/index-management.test.tsx` (Indexes screen), `tests/query-scans.test.tsx` and
`tests/embedding-query.test.tsx` (Query flows).
