# Bulk Import/Export (JSONL) — Usage

Streaming whole-graph interchange as newline-delimited JSON (`fallen8-jsonl`, versions 1–2).
Companion docs: [spec.md](./spec.md) (contract) and [plan.md](./plan.md) (phases). The
Studio's sample graphs (feature sample-graphs) ship in exactly this format.

## Export

```bash
# The whole graph:
curl -sf http://localhost:5000/bulk/export -o graph.jsonl

# A label-filtered subset (edges are endpoint-filtered against the exported vertices,
# so the file always imports):
curl -sf "http://localhost:5000/bulk/export?vertexLabel=person&edgeLabel=knows" -o people.jsonl
```

The stream is: one `meta` line (format version + exact counts), then `vertex` lines, then
`edge` lines. Property values travel as typed pairs, so `Int64` above 2^53, `Decimal` scale,
and sub-second `DateTime` ticks all survive — every type the REST surface can create
round-trips value- and CLR-type-exactly.

**Embeddings (format version 2):** element embeddings are the reserved `$embedding:<name>`
`float[]` properties (feature element-embeddings) and travel as
`{"type": "System.Single[]", "value": "0.1,-0.5,…"}` — comma-joined `"R"` floats, the one
non-scalar type of the format. The version stamp is the lowest sufficient one: a file
containing any array is stamped `2`, everything else stays `1` and remains readable by
older builds (a version-1 stamp that carries an array is rejected — the stamp is a promise,
not a hint). Importing an embedded file and then creating a bound `VectorIndex` projects
the vectors immediately.

**Consistency (honest):** this is data interchange, not a crash-consistent backup. A write
committed during the export may or may not appear; the guarantee is that the file is
*internally consistent* (every edge's endpoints resolve within the file) and everything
committed before the export began is present. For a point-in-time backup, quiesce writes or
use the save-game machinery.

An element carrying a property outside the exportable type allow-list (or a null value)
fails the whole export with **422 before anything is streamed** — never a half-written file.

## Import

```bash
curl -sf -X POST http://localhost:5000/bulk/import \
     -H "Content-Type: application/x-ndjson" \
     --data-binary @graph.jsonl
# -> {"verticesCreated":2000,"edgesCreated":1999,"linesRead":4000}
```

- **Empty graph only (v1):** a non-empty target answers 409 — run `PUT /tabularasa` first or
  import into a fresh instance.
- **File ids are references, never engine ids.** Import remaps unconditionally; edges are
  wired to the images of their endpoints. Gappy, high, out-of-order ids are all fine;
  duplicates are a 400.
- **Batched through the single writer:** `Fallen8:BulkIO:ImportBatchSize` (default 10 000)
  elements per transaction = one WAL entry + one fsync each. Memory is bounded by the batch
  size plus the id map (~8+ bytes per vertex — a 10 M-vertex file needs low hundreds of MB
  for the map; that is the only structure that grows with file size).
- **Fail-fast:** the first invalid line aborts with its exact `lineNumber` in a
  problem+json body. Batches committed before the failure **stay committed** (each batch is
  atomic and WAL-logged; the file is not one transaction) — the body reports
  `verticesCommitted`/`edgesCommitted`, and recovery is always "`/tabularasa`, fix the line,
  retry".

### Filtering files by hand

`grep` slicing works — just drop the meta line, whose counts would no longer match (they act
as a truncation guard when present):

```bash
tail -n +2 graph.jsonl | grep '"label":"person"' > people-only.jsonl
```

Blank lines are tolerated; a meta line is only valid as line 1.

## Configuration

```jsonc
"Fallen8": {
  "BulkIO": {
    "ImportBatchSize": 10000,        // elements per create transaction
    "MaxLineBytes": 1048576,         // longer lines are a 400 with their line number
    "MaxImportRequestBytes": null    // optional whole-body cap (413); null = unlimited -
                                     // an explicit carve-out from the sensitive 1 MiB limit,
                                     // which stays untouched for the code/plugin endpoints
  }
}
```

Both endpoints sit behind the standard fallback auth policy (API key) like every other
endpoint. The file carries graph **elements** only — indices, subgraph definitions, and
save-game metadata are re-created via their own endpoints.
