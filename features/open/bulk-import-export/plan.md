# Bulk Import/Export (JSONL) — Plan

Companion to [spec.md](./spec.md). Streaming JSONL export/import over REST, engine untouched.
Feature branch: `feature/bulk-import-export` (branch-only workflow — no GitHub issue/PR).

Ordering principle: land the format and export first (read-only, zero risk, and it produces
the fixture files everything later needs), then the import happy path against exported files,
then the error/limits hardening that makes the contract honest, then docs + gate. Every phase
ends build-clean with the full suite green.

## Phase 0 — Format + export

Intent: a correct, streaming, internally-consistent NDJSON export; the line schema pinned in
one place.

- [ ] `Fallen8BulkIOOptions` (`Fallen8:BulkIO`: `ImportBatchSize`, `MaxLineBytes`,
  `MaxImportRequestBytes`) bound in `Program.cs`; MIT headers everywhere.
- [ ] `Helper/JsonlGraphFormat.cs`: the version-1 line schema — meta/vertex/edge serialisation
  via `Utf8JsonWriter`, property values as `{type, value}` pairs using the pinned
  invariant-culture formats (`"R"`, `"O"`, `"c"`, `"D"`), the format/version constants.
- [ ] `BulkController` + `GET /bulk/export` (`application/x-ndjson`,
  `vertexLabel`/`edgeLabel` query params): capture vertices then edges
  (`GetAllVertices`/`GetAllEdges`), build the vertex-id set, pre-stream validation pass
  (allow-list check → 422 before the status line; exact meta counts), then stream meta →
  vertices → endpoint-filtered edges with periodic flushes. Never a whole-graph string.
- [ ] `[ProducesResponseType]`/`[Produces]` + XML docs; OpenAPI document renders the endpoint.
- [ ] Tests: export of a seeded mixed graph parses line-by-line back into the expected
  objects (meta counts exact, vertices before edges, label filters honoured, edges
  endpoint-filtered); 422 on a library-seeded non-allow-listed property, asserted
  pre-stream; every `AllowedLiteralTypes` type appears in a fixture and serialises in the
  pinned format.

## Phase 1 — Import happy path

Intent: an exported file loads into a fresh instance and the graphs are structurally equal.

- [ ] `POST /bulk/import` (`application/x-ndjson`, async, `PipeReader` line loop): 409 on a
  non-empty graph; optional meta line validated (format/version, counts remembered).
- [ ] Batching through the single writer: vertex/edge batches of `ImportBatchSize`, one
  `CreateVerticesTransaction`/`CreateEdgesTransaction` per batch, `await tx.Completion`,
  id map extended from `GetCreatedVertices()`/`GetCreatedEdges()` in definition order;
  flush-vertex-batch-on-first-edge so interleaved files work; edge endpoints resolved
  through the map.
- [ ] Property conversion through the existing `ServiceHelper.GenerateProperties` /
  `AllowedLiteralTypes` path (no new conversion code).
- [ ] 200 summary body (`BulkImportResultREST`: `verticesCreated`, `edgesCreated`,
  `linesRead`).
- [ ] Tests: the **round-trip** (export → fresh instance → import → structural equality incl.
  per-type property fidelity, adjacency under the id map, self-loops, shared endpoints);
  id-remap correctness on a hand-written gappy/out-of-order-id file; duplicate file id
  rejected; interleaved vertex/edge file imports; > 2 × `ImportBatchSize` file commits in
  multiple transactions.

## Phase 2 — Error contract, limits, durability pinning

Intent: make the honest parts of the spec (§3.4/§3.5) enforced and tested, not just written.

- [ ] Fail-fast line errors as problem+json with `lineNumber`, `reason`,
  `verticesCommitted`/`edgesCommitted`: malformed JSON, unknown `type`, missing required
  fields, unknown property type name, unconvertible value, unresolved edge endpoint,
  duplicate id, meta-count mismatch (truncation guard), rolled-back batch mapped through
  `TransactionFailureReason`.
- [ ] Limits: `MaxLineBytes` enforcement with line number; the per-request
  `IHttpMaxRequestBodySizeFeature` carve-out from `MaxImportRequestBytes` (null = unlimited);
  413 pinned via `WebApplicationFactory` when a cap is configured.
- [ ] Posture tests through the pipeline: 401 without the configured API key on both
  endpoints; content-type negotiation; sensitive-endpoint limits demonstrably unaffected.
- [ ] Durability tests: WAL-enabled import → fresh engine replay reproduces the graph;
  mid-file failure after ≥ 1 committed batch leaves exactly the committed batches (state +
  replay agree, matching `transaction-atomicity` per-batch guarantees).
- [ ] Concurrent-write-during-export test: writer racing a large export; export completes,
  file is internally consistent, imports cleanly.

## Phase 3 — Docs + gate

- [ ] `features/open/bulk-import-export/README.md`: curl examples (export to file, import a
  file, label-filtered subset via `grep` with the drop-the-meta-line note), the consistency
  and partial-import honesty notes, the id-map memory note, config table.
- [ ] Root `README.md`: a short "Bulk export/import (JSONL)" entry pointing at the feature
  README.
- [ ] Full `dotnet test` green; build 0 warnings/0 errors.
- [ ] Council review per the repo merge gate; fix findings on the branch;
  `git merge --no-ff` to `main`; move `features/open/bulk-import-export/` →
  `features/done/`.

## Progress

- [ ] Phase 0 — format + streaming export + pre-stream validation
- [ ] Phase 1 — import happy path (empty-target gate, batching, id remap, round-trip)
- [ ] Phase 2 — error contract, limits carve-out, WAL/durability + concurrency tests
- [ ] Phase 3 — READMEs, council gate, merge + move to done/

## Decision / revisit conditions

- **JSONL only** — user decision. A second format needs a concrete external-tool interchange
  need, not speculation.
- **Empty-graph-only import (v1)** — merge-import waits for a concrete combine/incremental
  need; the line schema and id-remap design already accommodate it.
- **Fail-fast, one-error report** — collect-and-continue/dry-run waits for an operator
  actually asking to validate without importing.
- **Unlimited import body by default** — the carve-out is deliberate (memory is bounded by
  construction); revisit only as an operator-set cap, never by reapplying the sensitive
  1 MiB limit.
- **No index/subgraph/save-game lines** — revisit when a real restore workflow shows
  re-creating them by hand is a burden; the `version` field is the extension mechanism.
