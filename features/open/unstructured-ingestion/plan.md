# Plan - unstructured-ingestion

## Changelog

- **2026-08-02** Reordered and extended after the external design review: structured
  chunking and provenance land in phase 1, the write path gains the stub-first
  lifecycle and ceilings (phase 2), fused retrieval is its own phase (3), structural
  linking its own phase (4); acceptance criteria made testable per phase; measured
  memory figure added as a closing deliverable.
- **2026-08-01** Initial plan.

Working rules: no engine changes expected (two flagged verification items in the spec's
Unverified section; if one fails verification, the engine fix is its own reviewed
commit); every phase leaves the solution building, tests green, warnings as errors.
FR references point at [spec.md](spec.md).

## Phase 1 - conversion client and chunker (FR-1, FR-6)

Options class + validation + capability gate; `DoclingClient`
(`to_formats: ["json","md"]`, timeout, `page_range` when applicable, health probe with
short-TTL cache); structured chunker over a pinned DoclingDocument subset with markdown
fallback; identifier extraction with cap.

Acceptance: golden tests pass for heading hierarchy, intact tables, row-window splits
with repeated header rows, PPTX slides, XLSX sheets, page provenance, markdown
fallback, unicode, determinism, identifier cap and false-positive guards; DoclingClient
fake-handler tests cover success, non-200, timeout, missing `json_content` (fallback),
missing both contents (error). Fixture DoclingDocument JSONs pin the schema subset.

## Phase 2 - write path (FR-2 to FR-5, FR-7, FR-8, FR-14, FR-15)

`DocumentController` (ingest file/text, list, get, delete, namespace twins, full
OpenAPI annotations); orchestrator with the stub-first lifecycle (processing to
indexed/failed), embed-before-write, ensured vector + fulltext indices, provenance
properties, caps, ceiling, duplicate-hash 409, replace semantics; additive `/status`
block. Resolve the two Unverified items (fulltext liveness, fulltext population path)
and record findings in the spec.

Acceptance: failure injection at parse, page-cap, chunk-cap, ceiling, embed, and each
write step proves "one failed Document vertex, zero chunks" (and "old document
untouched" for replace); controller matrix covers every documented status code with
reason; an integration test observes the lifecycle (`vertexCreated`, `propertySet`
status, chunk-creation burst) on the change feed; OpenAPI snapshot regenerated,
additions only.

## Phase 3 - fused read path (FR-11, FR-12)

`POST /document/search`: dense side (provider embed with `QueryPrefix`, or
`queryVector`) via `VectorIndexScan` constrained to `Chunk` vertices; lexical side via
`FulltextIndexScan`; RRF fusion (k = 60, candidate depth max(50, 4k)); liveness
filtering; degrade paths with `modeUsed`; model-mismatch 409; `window` expansion over
`next` edges; `groupByDocument` with the FR-11 ordering contract.

Acceptance: deterministic RRF unit tests over synthetic ranks (ties by element id); an
integration fixture where an exact-identifier query hits fused but misses dense-only;
degrade tests for provider-off and fulltext-index-absent; window and group ordering
pinned; 409 on stale index model.

## Phase 4 - structural linking (FR-13)

`link` request block: allowlist validation (equality-capable indices only), per-token
`IndexScan` lookups in deterministic order, `mentions` edges with per-chunk cap,
same-ingest targets excluded, `linksCreated` reporting.

Acceptance: exact-match semantics pinned (case-sensitive, no substring, no
normalization); 400s for unknown or non-equality index ids; cap determinism (token
order, index order, element id); links removed with chunk deletion; zero links when
the block is absent.

## Phase 5 - compose environment

`docling` service (record the chosen image variant and its measured size in the compose
comment), healthcheck, `f8-net`; `Fallen8__Ingestion__*` env block on `fallen8`;
default on; `F8_INGESTION=false` disables the capability and skips the sidecar
(compose profile via `env:up`).

Acceptance: `npm run env:up` cold start ends healthy with ingestion reachable;
`F8_INGESTION=false npm run env:up` starts without the sidecar and `/status` reports
the capability off; gated live smoke (`F8_TEST_DOCLING_ENDPOINT`) converts a real PDF
end to end.

## Phase 6 - Studio Documents screen (FR-9)

Screen + route + nav; `ingestion` status block in the status store; upload and
raw-text forms with degraded states; document table (list caps) with feed-driven
progress; detail view; delete confirm; memory budget element (usage vs ceiling);
fused-search UI with `modeUsed` surfaced and the existing seed affordances; stale-model
badge (FR-16).

Acceptance: vitest covers gating and degraded states, feed-triggered refetch, budget
rendering, search flow with mocked API, delete confirm, stale badge; e2e screenshot
spec added and affected screenshots recaptured; docs-screenshot rule satisfied.

## Phase 7 - MCP surface (FR-10)

`f8_documents`: `list` / `get` / `search` (read), `ingest_text` / `delete` (write);
multipart upload recorded as a conscious deferral with reason; `McpBridgedEndpoints`
updated.

Acceptance: `McpRestCoverageTest` and `McpContractTest` green with the new routes; tier
gating verified (write ops absent and rejected when the write tier is off).

## Phase 8 - sample, docs, diagrams, closure

Dossier sample variant ingested through the real endpoints at load time (linking
enabled against the sample's indices; loads without capability/provider, minus
semantic parts); docs page + sidebar + traversal recipe (FR-12) + README key-features
line; both architecture diagrams gain the docling sidecar; RETRAIN-LOG entry; measure
per-chunk resident memory on a real corpus and replace the spec's 25-30 kB estimate
with the figure; re-run the spec's impact sweep.

Acceptance: docs build link-checked; sample loads in a fresh compose environment and
the demo path (describe, find fused, traverse a `mentions` edge) works; full
`dotnet test` and UI suites green; spec contains the measured figure and resolved
Unverified items.
