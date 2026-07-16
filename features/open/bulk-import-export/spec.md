# Bulk Import/Export (JSONL) — Specification

> **Status:** Draft, spec only (no implementation yet). Follow the feature workflow in the
> repository root `CLAUDE.md`. Feature branch: `feature/bulk-import-export` (branch-only
> workflow — no GitHub issue/PR).

## 1. Overview & requirements

Fallen-8 has no way to get a whole graph **out** as data or **in** as data. The checkpoint
format is the engine's own binary snapshot (opaque, version-coupled, machine-local); the REST
read surface is deliberately capped (`GetGraph` clamps to `MaxPageSize = 100_000`,
`GraphController.cs:78`/`:349`, feature `api-error-contract` E6); and the write surface creates
one element per request (`PUT /vertex`, `PUT /edge`). Backing up a graph as inspectable data,
moving it between instances, seeding a test box from production, or diffing two graphs with
ordinary text tools is currently impossible without writing a client-side crawler.

This feature adds **streaming JSONL export and import of the graph over REST**:

- `GET /bulk/export` streams the graph (or a label-filtered subset) as newline-delimited JSON
  (`application/x-ndjson`), vertices first, then edges, in constant additional memory.
- `POST /bulk/import` streams a JSONL body back in, batching lines into large create
  transactions through the single writer, remapping the file's ids to fresh engine ids.

**JSONL is the only format** (user decision). One JSON object per line is trivially streamable
in both directions, splittable/filterable with `grep`/`jq`, and needs no schema tooling.

## 2. Goals / non-goals

**Goals**

- A precise, versioned **line schema** (§3.1) that round-trips every property type the REST
  surface can create (the `AllowedLiteralTypes` allow-list, `Helper/AllowedLiteralTypes.cs`),
  with an honest fidelity statement for everything else.
- **Export** (§3.2): a new `BulkController` endpoint streaming NDJSON — never materialising the
  graph as one string — with an explicit, honest consistency contract under concurrent writes,
  and optional `vertexLabel`/`edgeLabel` filters mapping 1:1 to the existing
  `GetAllVertices(label)`/`GetAllEdges(label)` read surface (`IFallen8Read.cs:101/:108`).
- **Import** (§3.3): a streamed, line-by-line parse batched into `CreateVerticesTransaction`/
  `CreateEdgesTransaction` (one WAL entry + one group-commit fsync per batch, per
  `write-path-throughput`), **empty-graph target only in v1**, with **id remapping** — the
  file's ids are references resolved during import, never preserved.
- A **line-numbered error contract** (§3.4): fail-fast, problem+json per `api-error-contract`,
  with the partial-import state stated honestly.
- **Limits** (§3.5): a max line length, an explicit per-endpoint body-size carve-out from the
  `api-security-boundary` limits, and the same auth posture as every other mutation.
- MSTest coverage at the repo bar (§4), including a full export → fresh instance → import →
  structural-equality round-trip.

**Non-goals** (each with its revisit trigger)

- **CSV / GraphML / GEXF / any second format.** JSONL only, by user decision. Revisit when a
  concrete interchange need with a specific external tool (e.g. Gephi wants GEXF) actually
  arrives — not speculatively.
- **Merge-import into a non-empty graph.** v1 imports into an empty graph only (§3.3). Revisit
  when there is a concrete need to combine two graphs or run incremental loads; the id-remap
  design already accommodates it (nothing in the line schema assumes an empty target).
- **Collect-and-continue / dry-run validation.** v1 is fail-fast on the first bad line.
  Revisit when an operator actually asks to validate a large file without importing it.
- **Resumable/multi-part upload protocols, compression negotiation, S3/object-store
  connectors.** This is a single-process, self-hosted, single-operator app; a `curl`-able
  streaming endpoint is the right size. Revisit only if a deployment appears whose network
  genuinely cannot hold one HTTP request open for the duration of a load.
- **Exporting indices, subgraph definitions, or save-game metadata.** The file carries graph
  *elements* (vertices, edges, properties) only. Indices are rebuilt after import via the
  existing index endpoints; subgraph recipes are re-registered via `PUT /subgraph`. Revisit
  when a real restore workflow demonstrates that re-creating them by hand is a burden (then:
  additional line types, same format version discipline).
- **Preserving `modificationDate`.** `VertexDefinition`/`EdgeDefinition` accept only
  `CreationDate`; imported elements get fresh modification timestamps. Revisit only if the
  create path ever grows a modification-date parameter for its own reasons.
- **Non-allow-listed CLR property types.** A property whose runtime type is outside
  `AllowedLiteralTypes` (only reachable via embedded/library or plugin use — the REST write
  path cannot create one) fails the export up front (§3.2) rather than exporting lossily.
  Revisit with pluggable value converters when a real plugin workload stores custom types.

## 3. Design sketch

### 3.1 Line schema (`fallen8-jsonl`, version 1)

One JSON object per line, UTF-8, `\n`-separated, no BOM. Every object carries a `type`
discriminator. Unknown top-level fields are rejected (strict v1; the `version` field is the
evolution mechanism).

**Meta line** — written first by export; optional on import (see below):

```json
{"type":"meta","format":"fallen8-jsonl","version":1,"exportedAtUtc":"2026-07-15T12:00:00Z","vertexCount":2,"edgeCount":1}
```

**Vertex line:**

```json
{"type":"vertex","id":42,"label":"person","creationDate":1713862800,"properties":{"name":{"type":"System.String","value":"Alice"},"age":{"type":"System.Int32","value":"30"}}}
```

**Edge line** — adds `source`/`target` (file-id references) and `edgePropertyId` (required by
`EdgeDefinition.EdgePropertyId` — it keys the adjacency lists):

```json
{"type":"edge","id":99,"label":"friendship","edgePropertyId":"knows","source":42,"target":43,"creationDate":1713862800,"properties":{"since":{"type":"System.DateTime","value":"2024-01-15T00:00:00.0000000"}}}
```

Field semantics:

- `id` — a 32-bit integer that is **a reference within the file only**. Export writes the
  element's current engine id; import treats it purely as the join key for `source`/`target`
  and assigns fresh engine ids (§3.3). Ids may be non-contiguous (label-filtered subsets,
  removed elements at export time leave gaps); duplicates within one file are an error.
- `label` — nullable, exactly as the engine's `Label`.
- `creationDate` — the `UInt32` Unix timestamp the model stores; preserved through import.
  `modificationDate` is deliberately absent (non-goal above).
- `properties` — an object mapping `propertyId` → `{"type": <name>, "value": <string>}`. This
  deliberately mirrors the existing REST write contract (`PropertySpecification`:
  `fullQualifiedTypeName` + string `propertyValue`). Type names resolve through
  `AllowedLiteralTypes` and only it (the R3 guarantee); value PARSING is the format's own
  per-type invariant-culture code in `JsonlGraphFormat` — the REST path's
  `Convert.ChangeType` is culture-sensitive as called (interchange must not inherit the
  server culture) and `TimeSpan`/`Guid`/`DateTimeOffset` are not `IConvertible` at all, so
  the shared-path idea could not deliver the fidelity contract. Values are
  invariant-culture strings using
  round-trip formats (`"R"` for `Single`/`Double`, `"O"` for `DateTime`/`DateTimeOffset`,
  `"c"` for `TimeSpan`, `"D"` for `Guid`; `Decimal`/integers via invariant `ToString`).
  Absent/empty `properties` ⇒ no properties.

**Round-trip fidelity statement (honest).** All 18 `AllowedLiteralTypes` types round-trip
value-exactly (`AGraphElementModel` `ValueEquals` semantics), *including* the CLR type — a
typed string pair never suffers JSON number coercion (`Int64` precision, `Decimal` scale,
`Single` vs `Double` are all preserved, unlike the existing `Vertex`/`Edge` DTOs, which
flatten properties to raw JSON values). Anything outside the allow-list does not round-trip
and is rejected at export time rather than silently degraded — as are null property values
and strings/chars containing unpaired surrogates (invalid UTF-16, which the JSON writer
would otherwise silently replace with U+FFFD).

**Why a meta line, and why no trailer.** The meta line carries the format version (the only
evolution hook a one-object-per-line format has) and exact counts — export knows them up front
(§3.2), and import uses them as a **truncation guard**: when a meta line is present, a
count mismatch at end-of-stream is an error. It is *optional* on import so hand-authored and
tool-filtered files stay first-class — `grep '"label":"person"' full.jsonl > subset.jsonl`
produces a valid import file precisely because the operator can drop the now-wrong meta line.
A separate trailer line would duplicate the counts for no additional guarantee and complicate
`head`/`grep`-style slicing, so there is none.

### 3.2 Export — `GET /bulk/export`

New `BulkController` (`[Route("api/v{version:apiVersion}/[controller]")]` + root-relative
action routes, like every other controller), `Produces("application/x-ndjson")`. Query
parameters: `vertexLabel` (optional), `edgeLabel` (optional) — passed straight to
`GetAllVertices(vertexLabel)` / `GetAllEdges(edgeLabel)`.

Sequence:

1. **Capture** `var vertices = _fallen8.GetAllVertices(vertexLabel);` then
   `var edges = _fallen8.GetAllEdges(edgeLabel);` — two back-to-back point-in-time projections
   of the lock-free snapshot (`scan-result-representation`: right-sized `IReadOnlyList<T>`,
   ~8 bytes/element of reference array — this is the engine's own read surface, not a JSON
   string). Build a `HashSet<int>` of the captured vertex ids.
2. **Validate before streaming:** one pass over both lists checks every property's runtime
   type against `AllowedLiteralTypes`. A non-exportable property fails the whole request with
   a **422 problem+json** naming the element id and property — *before* the 200 status line is
   sent, so a failed export is never a half-written file. The same pass counts the edges whose
   `SourceVertex.Id`/`TargetVertex.Id` are both in the vertex-id set, so the meta line's
   counts are exact.
3. **Stream:** meta line, then vertex lines, then edge lines, each serialised with
   `Utf8JsonWriter` straight into the response body (flushed every N lines). Edges with an
   endpoint outside the captured vertex set are **omitted** (they cannot appear given step 1's
   capture order unless a concurrent write raced the two scans, or a label filter excluded an
   endpoint) — this is what makes every exported file **internally consistent and importable
   by construction**.

**Consistency contract (honest).** Export is **not a transactional snapshot**. Reads are
lock-free over the volatile snapshot; the vertex and edge captures are two successive reads.
A write committed during the export may or may not appear in the file. The guarantee is
exactly: *the file is internally consistent* — every edge line's `source`/`target` resolves to
a vertex line in the same file — and *elements committed before the vertex capture are all
present* (subject to the label filters). An operator who needs a point-in-time backup quiesces
writes or uses the checkpoint/save-game machinery; this endpoint is data interchange, not
crash-consistent backup, and the docs say so.

Auth/limits posture: the endpoint inherits the `FallbackPolicy` (API key when configured,
`api-security-boundary`) and the global rate limiter; it is **not** on the sensitive
(code/plugin) partition — it executes no user code. It is the sanctioned whole-graph read; the
`GetGraph` `MaxPageSize` clamp stays exactly as it is (streaming NDJSON does not have the
materialise-everything DoS shape that motivated E6, and memory here is bounded by step 1's
reference lists, which the engine's own scans already allocate).

### 3.3 Import — `POST /bulk/import`

`Consumes("application/x-ndjson")`. The endpoint is `async`, reads the request body as a
stream (`PipeReader`), and never buffers the whole payload.

**Target mode (v1): empty graph only.** If `VertexCount != 0 || EdgeCount != 0`, respond
**409 problem+json** ("import requires an empty graph; use `/tabularasa` first or a fresh
instance"). This is the honest v1: with an empty target, id remapping is unambiguous, a failed
import is trivially recoverable (`/tabularasa` + retry), and no merge/conflict policy has to
be invented. Merge-import is parked with its trigger (§2). The emptiness check runs on the
request thread before any parsing; the single-writer model means a racing external write can
still land mid-import — imports are an operator activity on a quiet instance, and the docs
say so (v1 does not add a write-freeze mechanism).

**Line processing (single pass, in file order):**

1. If line 1 is a meta line: validate `format` == `fallen8-jsonl` and `version` == 1 (unknown
   version → 400); remember the counts for the end-of-stream truncation check. No meta line ⇒
   proceed without counts.
2. A **vertex line** becomes a `VertexDefinition` (properties converted via the
   `ServiceHelper.GenerateProperties` / `AllowedLiteralTypes` path) appended to the pending
   vertex batch, alongside its file id. A duplicate file id → line error.
3. An **edge line** first **flushes any pending vertex batch** (so its ids are mapped — this
   makes interleaved files legal while keeping export's vertices-then-edges layout the fast
   path), then resolves `source`/`target` through the id map. An unresolved reference → line
   error ("edge at line N references unknown id K"). The resolved `EdgeDefinition` joins the
   pending edge batch.
4. **Flush** when a batch reaches `ImportBatchSize` (default **10 000**, configurable): build
   one `CreateVerticesTransaction`/`CreateEdgesTransaction` carrying the whole batch,
   `EnqueueTransaction`, `await tx.Completion` (the awaitable from `write-path-throughput` —
   no request-thread pinning), then check the terminal state. On commit, read
   `GetCreatedVertices()`/`GetCreatedEdges()` and extend the id map: `construct-then-commit`
   (feature `transaction-atomicity`) builds models in definition order against a local id
   counter, so `created[i]` corresponds to `batch[i]` — file id → `created[i].Id`. A
   `RolledBack` batch maps through the existing `TransactionFailureReason` → status mapping
   and aborts the import (§3.4).
5. At end of stream: flush remaining batches; if a meta line was present, compare produced
   counts against it (mismatch → error: truncated or edited file). Respond **200** with a
   summary body: `{"verticesCreated":n,"edgesCreated":m,"linesRead":k}`.

**Why batches of 10 000, why one transaction per batch.** Each batch is one enqueue, one
writer-thread execution, one WAL entry, and one group-commit fsync — the WAL cost is amortised
over 10 000 elements exactly the way `write-path-throughput` amortises it over a drained
group, but deterministically. Memory during import is bounded by
`ImportBatchSize × (definition + created-model reference)` plus the id map — independent of
file size. 10 000 keeps a batch's definitions in the tens of megabytes even at the 1 MiB line
cap while leaving the writer's per-transaction overhead noise-level.

**Id remapping is unconditional.** Even into an empty graph, file ids are *not* preserved:
the engine's `id == index` invariant (`transaction-atomicity`) means ids are assigned by the
store, and a file with gaps (filtered subset) or arbitrary ids must not dictate id-space
layout. The map is `Dictionary<int,int>` (file id → engine id), built per §3.3.4; it is the
only import structure that grows with file size (≈ 8+ bytes/vertex — 10 M vertices ≈ low
hundreds of MB; acceptable for v1 and stated in the docs).

**WAL/durability interplay.** Nothing new: each batch is an ordinary logged transaction, so a
crash mid-import replays exactly the committed batches (durable-before-ack per batch). The
import as a whole is **not** atomic — see §3.4.

**Read-only interplay: none exists.** The apiApp has no read-only mode today; import is a
mutation gated exactly like every other mutation (the all-or-nothing API-key fallback). If a
read-only mode ever lands, this endpoint joins the mutations it disables.

### 3.4 Error contract

- All error responses are RFC 7807 `application/problem+json` (`api-error-contract` global
  envelope), with `extensions` carrying `lineNumber`, `reason`, `verticesCommitted`,
  `edgesCommitted` where applicable.
- **Fail-fast:** the first invalid line stops the import. Invalid = malformed JSON, unknown
  `type` discriminator, unknown/absent required field, non-allow-listed property type name,
  unconvertible property value, duplicate file id, unresolved edge endpoint, line over the
  length cap, meta-count mismatch. Status: **400** (413 for the length cap breach when it is
  the request that is oversized). The bounded "error report" of v1 is exactly one error with
  an exact line number — collect-and-continue is parked (§2).
- **Partial-import semantics (honest):** batches committed before the failure **stay
  committed** — each batch is atomic (`transaction-atomicity`: a `RolledBack` batch has zero
  effect; a committed batch is fully applied and WAL-logged), but the file is not one
  transaction. The error body reports exactly how many elements were committed; because v1
  requires an empty target, recovery is always "`/tabularasa`, fix line N, retry". The spec
  deliberately does not pretend to whole-file atomicity it cannot provide without holding
  100 M elements outside the store.
- A batch that the engine itself rolls back (e.g. `InvalidInput`) surfaces through the same
  `TransactionFailureReason` mapping the single-element mutations use, wrapped in the
  line-context problem body (the reported line is the batch's last line).

### 3.5 Limits & security

| Concern | Decision |
|---|---|
| Auth | Both endpoints inherit the `FallbackPolicy` (API key when configured). No `[AllowAnonymous]`. Not on the sensitive rate-limit partition (no user code runs); global limiter applies. |
| Max line length | `Fallen8:BulkIO:MaxLineBytes`, default **1 MiB**. A longer line → error with its line number (one element with >1 MiB of properties is a modelling smell, and the cap bounds per-line parse memory). |
| Import body size | **Explicit carve-out** from the `api-security-boundary` body limits: the import action sets `IHttpMaxRequestBodySizeFeature.MaxRequestBodySize` per-request from `Fallen8:BulkIO:MaxImportRequestBytes`, default **null (unlimited)**. Justification: the endpoint exists precisely to carry whole-graph payloads; memory is bounded by construction (`MaxLineBytes` × parse + `ImportBatchSize` × definitions), so the body cap is a disk/graph-size control, not a memory control — and any finite default would be an arbitrary failure point for legitimate loads. The operator can set a cap; the 1 MiB `MaxSensitiveRequestBodyBytes` limit (code/plugin endpoints) is untouched and does not apply here. |
| Export | No body; response size is the graph's size. Bounded memory per §3.2 step 1. |
| Config | New `Fallen8BulkIOOptions` (`Fallen8:BulkIO`): `ImportBatchSize` (10 000), `MaxLineBytes` (1 MiB), `MaxImportRequestBytes` (null). |

### 3.6 Where things live

```
fallen-8-core-apiApp/
  Controllers/BulkController.cs          export + import actions
  Controllers/Model/BulkImportResultREST.cs   the 200 summary body
  Configuration/Fallen8BulkIOOptions.cs
  Helper/JsonlGraphFormat.cs             line (de)serialisation, meta handling, version const
fallen-8-unittest/                       tests (repo convention: one suite project)
```

The engine (`fallen-8-core`) is **unchanged**: export consumes `IFallen8Read`
(`GetAllVertices`/`GetAllEdges`), import consumes the existing batch transactions. MIT headers
on every new file; `[ProducesResponseType]`/`[Consumes]`/`[Produces]` + XML docs so the
OpenAPI document is honest.

## 4. Acceptance criteria

All MSTest, arrange/act/assert, `TestLoggerFactory.Create()`; pipeline-level behaviour
(auth, body limits, content types, problem+json) through `WebApplicationFactory<Program>`.

1. **Round-trip.** Seed a graph with multiple labels, self-loops, shared endpoints, and at
   least one property of *every* `AllowedLiteralTypes` type (incl. `Decimal` scale, `Int64`
   above 2^53, `Double` needing `"R"`, `DateTime` with sub-second ticks). Export → import into
   a **fresh** instance → structural equality: vertex/edge counts, labels, creation dates,
   full property bags (`ValueEquals`, including CLR type), and adjacency shape (edges connect
   the images of their original endpoints under the id map).
2. **Id remapping.** A hand-written file with non-contiguous, high, out-of-order ids imports
   correctly: edges are wired to the right vertices; engine ids start at 0 regardless of file
   ids; a duplicate file id → 400 naming the line.
3. **Malformed lines.** Bad JSON, unknown `type`, missing `edgePropertyId`, unknown property
   type name, unconvertible value, over-long line — each → 400 problem+json with the exact
   `lineNumber` and honest `verticesCommitted`/`edgesCommitted`.
4. **Edge to missing vertex.** An edge referencing an id never defined → 400 with the line
   number; previously committed batches remain (asserted), matching §3.4.
5. **Empty-target gate.** Import into a non-empty graph → 409 with nothing parsed/mutated.
6. **Large-batch commit behaviour.** An import of > 2 × `ImportBatchSize` vertices commits in
   multiple transactions (observable via transaction results/counts); with the WAL enabled, a
   fresh engine replaying snapshot+log reproduces the imported graph (durable-before-ack per
   batch); a mid-file failure after ≥ 1 committed batch leaves exactly those batches (WAL
   replay agrees).
7. **Export consistency.** With a concurrent writer creating vertices+edges during a large
   export, the export completes, every edge line's endpoints resolve within the file, and the
   file imports cleanly into a fresh instance. Meta counts equal actual line counts.
8. **Filters.** `vertexLabel`/`edgeLabel` exports exactly the engine's label-filtered scans,
   with edges endpoint-filtered against the exported vertex set; the subset file imports.
9. **Fidelity refusal.** An engine-side property outside the allow-list (seeded via the
   library API) → export responds 422 *before* streaming (no partial file).
10. **Posture.** With an API key configured, both endpoints 401 without it; import over the
    configured `MaxImportRequestBytes` → 413; content types as declared; suite green, build
    clean.

## 5. Risks

- **Torn export under concurrent writes** is inherent to lock-free reads. Mitigated by the
  endpoint-filtering construction (files are always importable) and by documenting the
  quiesce-or-use-checkpoints guidance; *not* mitigated by pretending to snapshot isolation.
- **Id-map memory on huge files** grows O(vertices). Stated in docs (§3.3); acceptable for the
  single-process reality. If it ever bites, the revisit is a spillable map, not a format change.
- **Import is not atomic across batches.** The 409-empty-target gate keeps recovery trivial;
  the error body's committed counts keep it honest. The temptation to buffer the whole file
  for atomicity is explicitly rejected (unbounded memory).
- **Race on the emptiness check** (check on request thread, mutations on the writer): a
  concurrent external write after the check lands the import in a technically non-empty graph.
  Accepted for v1 (operator activity, documented); the alternative — a writer-thread freeze —
  is heavier machinery than the situation warrants.
- **Schema drift.** The `version` field plus strict unknown-field rejection is the whole
  evolution story; any line-schema change bumps the version and the import gains an explicit
  upgrade path. The version constant lives in one place (`JsonlGraphFormat`).
- **Culture/format bugs** in value strings (decimal comma, non-round-trip floats) are the
  classic interchange failure; the invariant-culture + `"R"`/`"O"`/`"c"`/`"D"` choices are
  pinned by the per-type fidelity test (§4.1).

## 6. Keep (do not regress)

- **The engine is untouched:** single-writer mutation via the existing transactions,
  lock-free reads over the volatile snapshot, `id == index` (`transaction-atomicity`),
  construct-then-commit batch semantics, group-commit WAL behaviour
  (`write-path-throughput`) — this feature is a pure consumer of all of them.
- **`GetGraph`'s `MaxPageSize` clamp** (`api-error-contract` E6) stays; export is the
  sanctioned unbounded read *because* it streams.
- **`api-security-boundary` posture:** the fallback auth policy, the sensitive-endpoint
  1 MiB body limit and rate partition (unchanged, not applicable here), CORS, and the
  loopback-by-default bind all stay exactly as they are; the import body-size carve-out is
  per-endpoint and explicit, never a global loosening.
- **`AllowedLiteralTypes` as the single property-type vocabulary** — import resolves types
  through it and only it (never `Type.GetType` on file input), preserving the
  `dynamic-code-resource-limits` R3 guarantee on this new input surface.
- **The problem+json error envelope and the `TransactionFailureReason` → status mapping** —
  reused, not forked.
