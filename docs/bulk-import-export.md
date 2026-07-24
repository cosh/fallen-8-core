# Bulk import and export

`GET /bulk/export` streams a whole graph (or a label-filtered subset) as newline-delimited JSON, and `POST /bulk/import` streams such a file back into an empty graph. The format — `fallen8-jsonl`, format version 2 — carries graph elements only (vertices, edges, and their typed properties, including element embeddings), with every property value typed so it round-trips exactly. Both directions use content type `application/x-ndjson` and stream, so neither side materializes the whole graph as a single JSON string.

## Endpoints

| Method | Route | Content type | Purpose |
|---|---|---|---|
| `GET` | `/bulk/export` | response `application/x-ndjson` | Stream the graph as JSONL |
| `POST` | `/bulk/import` | request `application/x-ndjson` | Load a JSONL file into an empty graph |

`GET /bulk/export` takes optional `vertexLabel` and `edgeLabel` query parameters — exact-match label filters passed straight to the engine's scans. Both endpoints are per-namespace like every data route: a bare URL addresses `default`, `/ns/{name}/bulk/…` addresses a named graph ([namespaces](namespaces.md)). Both sit behind the standard API-key fallback policy ([security](security.md)).

| Endpoint | Status | Meaning |
|---|---|---|
| export | `200` | The NDJSON stream |
| export | `401` | No valid credential |
| export | `422` | An element carries a null value or a property whose type is outside the exportable allow-list; the body names `elementId`/`propertyKey`. Sent *before* any bytes stream |
| import | `200` | Completed; the body is the created-counts summary |
| import | `400` | An invalid line; the body carries `lineNumber` and the committed counts |
| import | `401` | No valid credential |
| import | `409` | The target graph is not empty |
| import | `413` | The body exceeds the configured `MaxImportRequestBytes` |
| import | `500` | A batch transaction faulted; committed counts reported |

## The `fallen8-jsonl` format

One JSON object per line, UTF-8, `\n`-separated, no BOM. Export writes exactly one `meta` line, then every `vertex` line, then every `edge` line. Parsing is strict: an unknown or duplicate top-level field, trailing content after the object, or an unknown `type` all fail the line.

**Meta line** — first line on export; optional on import (only valid as line 1):

```json
{"type":"meta","format":"fallen8-jsonl","version":2,"vertexCount":34,"edgeCount":78}
```

A freshly exported meta line also carries an informational `"exportedAtUtc"` (ISO-8601). On import, `format` must be `fallen8-jsonl` and `version` must be in `1..2`; `vertexCount`/`edgeCount` are optional and, when present, act as a **truncation guard** — a mismatch against the produced counts at end of stream is a `400`. Drop the meta line when hand-filtering a file so its counts cannot go stale.

**Vertex and edge lines** (real lines from `../samples/karate-club.jsonl`):

```json
{"type":"vertex","id":2,"label":"member","creationDate":1767225600,"properties":{"name":{"type":"System.String","value":"Member 2"},"faction":{"type":"System.String","value":"mr-hi"},"icon":{"type":"System.String","value":"🥋"}}}
{"type":"edge","id":100,"edgePropertyId":"interactsWith","source":2,"target":1,"creationDate":1767225600}
```

| Field | Lines | Meaning |
|---|---|---|
| `id` | vertex, edge | 32-bit integer, **a reference within the file only** (see id remapping below) |
| `label` | vertex, edge | The element label; may be null or omitted |
| `creationDate` | vertex, edge | The `UInt32` Unix timestamp the model stores; preserved on import (`modificationDate` is not carried) |
| `properties` | vertex, edge | Object of `key → {type, value}`; omit or leave empty for none |
| `edgePropertyId` | edge | Required non-empty string; the adjacency-list key |
| `source`, `target` | edge | File `id`s of the endpoint vertices |

### Typed property values

Each property is `{"type": "<name>", "value": "<string>"}`. Export emits the full type name (`System.Int32`) and an invariant-culture string; import resolves the [same closed allow-list of 18 primitive types](graph-model.md) (case-insensitive, aliases accepted). Encoding the value as a typed string rather than a raw JSON value is what makes it round-trip **CLR-type-exactly**: `Int64` past 2^53, `Decimal` scale, `Single` vs `Double`, and sub-second `DateTime` ticks all survive.

| Type(s) | Value string |
|---|---|
| `String`, `Char` | verbatim (a `Char` is exactly one character) |
| `Boolean` | `true` / `false` |
| integers (`Byte` … `UInt64`) | invariant decimal |
| `Single`, `Double` | round-trip `"R"` |
| `Decimal` | invariant decimal |
| `DateTime`, `DateTimeOffset` | round-trip `"O"` |
| `TimeSpan` | `"c"` |
| `Guid` | `"D"` |
| `System.Single[]` | comma-joined `"R"` floats; an empty array is the empty string |

`System.Single[]` is the one non-scalar type (added in format version 2). It carries **element embeddings**: a named embedding lives on the reserved property `$embedding:<name>`, e.g. `"$embedding:default":{"type":"System.Single[]","value":"-0.02936,0.02324,…"}`. What an embedding is and how traversal reads it: [semantic traversal](semantic-traversal.md).

## Export

Export takes two back-to-back point-in-time reads of the lock-free store (vertices, then edges), validates every property up front, then streams. An edge whose source or target vertex is not in the exported set is **omitted**, so every exported file is internally consistent and importable by construction — neither a label filter nor a concurrent write can produce a dangling edge line.

**Consistency (honest).** This is data interchange, not a crash-consistent backup. Reads are lock-free, so a write committed *during* the export may or may not appear. The guarantee is exactly: the file is internally consistent, and everything committed before the export began is present (subject to the label filters). For a point-in-time snapshot, quiesce writes or take a [checkpoint](save-games.md).

**Fidelity refusal.** If any element carries a null property value or a value whose CLR type is outside the allow-list (reachable only via the library/plugin API, never the REST write path), the whole export fails with `422` naming the element and property — before the `200` status line, so a failed export is never a half-written file. A non-exportable property that a concurrent writer adds *mid-stream* is instead silently omitted from its element, consistent with the consistency contract above.

## Import

Import reads the request body line by line and batches lines into `CreateVertices`/`CreateEdges` transactions, so its memory is bounded by the batch size plus the id map, not the file size.

- **Empty target only.** A non-empty graph answers `409`. Clear it first with `HEAD /tabularasa` (the addressed namespace) or import into a fresh instance.
- **File ids are references, never engine ids.** Every id is remapped to a fresh engine id; edges are wired to the images of their `source`/`target`. Gappy, high, or out-of-order ids are fine; a duplicate file id (across all vertices *and* edges) is a `400`. Edges may be interleaved with vertices as long as each edge's endpoints appear earlier in the file — export's vertices-then-edges layout is the fast path.
- **Batched writes.** Each batch of `ImportBatchSize` elements is one transaction = one write-ahead-log entry + one fsync, so a crash mid-import replays exactly the committed batches.
- **Fail-fast, partial-commit.** The first invalid line aborts the import with its exact `lineNumber`; batches committed before it **stay committed** (the file is not one transaction). The problem body reports `verticesCommitted`/`edgesCommitted`; because the target had to be empty, recovery is always: `HEAD /tabularasa`, fix the line, retry. A line is invalid on malformed JSON, an unknown `type` or field, a bad or missing required field, an unknown property type or unconvertible value, a duplicate id, an unresolved edge endpoint, an over-long line, or a meta-count mismatch.

On success the body is the created-counts summary:

```json
{"verticesCreated":34,"edgesCreated":78,"linesRead":113}
```

`linesRead` counts every line, including the meta line and blank lines (blank lines are tolerated).

## Round-trip example

Export a graph to a file, then load it into a fresh, empty instance.

```bash
# Export the whole graph. Add ?vertexLabel=member&edgeLabel=interactsWith for a subset.
curl -sf http://localhost:8080/bulk/export -o graph.jsonl

# Import into an empty target.
curl -sf -X POST http://localhost:8080/bulk/import \
     -H "Content-Type: application/x-ndjson" \
     --data-binary @graph.jsonl
# -> {"verticesCreated":34,"edgesCreated":78,"linesRead":113}
```

```powershell
# Export
Invoke-RestMethod http://localhost:8080/bulk/export -OutFile graph.jsonl

# Import
Invoke-RestMethod -Method Post http://localhost:8080/bulk/import `
  -ContentType application/x-ndjson -InFile graph.jsonl
```

Because the file is plain JSONL, `grep`/`jq` slice it directly — just drop the meta line so its counts cannot go stale: `tail -n +2 graph.jsonl | grep '"label":"member"' > members.jsonl`.

## Configuration

Bind under `Fallen8:BulkIO`:

| Key | Default | Effect |
|---|---|---|
| `ImportBatchSize` | `10000` | Elements per import transaction (one WAL entry + one fsync). Bounds import memory independent of file size. |
| `MaxLineBytes` | `1048576` (1 MiB) | A longer import line is a `400` with its line number. |
| `MaxImportRequestBytes` | `null` (unlimited) | Optional whole-body cap; exceeding it is a `413`. An explicit per-endpoint carve-out — the sensitive code/plugin body limit is untouched. |

Non-positive `ImportBatchSize`/`MaxLineBytes` values reset to their defaults.

## Scope

The file carries graph **elements only**: vertices, edges, and their properties. Indexes, subgraph definitions, stored queries, and save-game metadata are not exported — recreate them through their own endpoints after import. For durable point-in-time backups of a running instance, use checkpoints instead ([save games](save-games.md)).

## See also

- [Graph model](graph-model.md) — elements, properties, and the property type allow-list
- [Namespaces](namespaces.md) — the `/ns/{name}/…` routing these endpoints follow
- [Save games](save-games.md) — checkpoints, the write-ahead log, the other durability path
- [Semantic traversal](semantic-traversal.md) — element embeddings, carried here as `System.Single[]`
- [Security](security.md) — the API key that gates both endpoints
- [Samples](samples.md) — the bundled sample datasets ship in this format
