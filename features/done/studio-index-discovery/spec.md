# Studio index discovery

## Problem

The Studio's Query screen treats indices as strings the user must already know:

- **Index id** is a free-form input. Suggestions only appear after the user manually runs
  the budgeted Graph-shape pass (Analytics → Compute), although the server can enumerate
  its registered indices cheaply at any time.
- **Plugin type** on index creation is a free-form input, although `GET /status` already
  returns `availableIndexPlugins` (the `PluginFactory` discovery of every `IIndex`
  implementation: `DictionaryIndex`, `SingleValueIndex`, `RangeIndex`, `RegExIndex`,
  `VectorIndex`, `SpatialIndex`).
- **Per-type creation options** are only wired for `VectorIndex` (dimension + metric).
  The other bucket indices genuinely take no options — but the UI doesn't say so — and
  `SpatialIndex` silently cannot be created over REST at all: its `Initialize` requires
  live CLR objects (`IMetric`, `IEnumerable<IDimension>`) that the `PluginSpecification`
  primitive-literal options cannot express, so `POST /index` with `pluginType:
  "SpatialIndex"` always answers `false`. The UI currently lets the user walk into that
  wall.

## Contract

### API: `GET /status` gains the index inventory

`StatusREST` gains

```json
"indices": [ { "indexId": "nameIndex", "pluginType": "DictionaryIndex" } ]
```

filled from `IndexFactory.GetIndexPluginTypesSnapshot()` (read-locked snapshot, O(#indices)
— no graph pass). `/status` is already the discovery surface (`availableIndexPlugins`
etc.) and is polled by the Studio, so the inventory rides along without a new endpoint.

The `{indexId, pluginType}` pair already exists as `SaveGameIndexREST`; it is renamed to
`IndexDescriptionREST` (one type, two consumers). The wire shape is pinned by
`[JsonPropertyName]`, so persisted save-game registries and the save-game JSON are
unchanged; only the OpenAPI schema component name moves.

### Studio: dropdowns and honest per-type options

- **Plugin type** (create): a `<select>` fed by `status.availableIndexPlugins` (falls back
  to a free-form input when the list is missing/empty, e.g. an older server).
- **Index id** (scans + management): the shared `shape-index-ids` datalist becomes the
  union of `status.indices` (live) and the Graph-shape snapshot (if computed). The input
  stays free-form — a datalist suggests, never forces.
- **Per-type creation options**:
  - `VectorIndex`: dimension (1–4096) + metric (Cosine/DotProduct/L2) — unchanged.
  - `DictionaryIndex`, `SingleValueIndex`, `RangeIndex`, `RegExIndex`: a hint that the
    type takes no creation options.
  - `SpatialIndex`: Create disabled with an honest note — not creatable over REST (see
    Problem). Making it creatable is a separate feature (needs a JSON-expressible spatial
    config: named metric, dimension list).
- After a successful create/delete the status query is invalidated so every dropdown
  reflects the new inventory.

## Non-goals

- REST-creatable spatial indices (JSON spatial config design — own feature).
- A dedicated `GET /index` listing endpoint — `/status` suffices for the single-process
  studio reality; revisit if the inventory grows fields (counts, config echo) that don't
  belong on status.
