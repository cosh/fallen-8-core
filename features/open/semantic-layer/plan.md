# Plan - semantic-layer (one increment, one branch, one council)

Working rules as before: engine untouched unless a review proves otherwise (flag it, pin with
a test); every phase builds green, warnings-as-errors; each phase's tests land with it. FR
refs point at [spec.md](spec.md). Branch `feature/semantic-layer`.

## Phase 1 - NLP service (`fallen-8-nlp`, FR-1)

Standalone FastAPI app + `Dockerfile` (spacy, de_core_news_sm, en_core_web_sm, langdetect).
`GET /health`, `POST /enrich` (batched via `nlp.pipe`, language hint/detect/default, entities
+ noun-chunk key terms, bounds -> 413). pytest with German + English fixtures.

Acceptance: pytest green (entities and key terms present, language-routed; empty; over-limit;
batch; health). `ruff`/format clean. Image builds.

## Phase 2 - apiApp NLP client + gate + status (FR-2)

`Fallen8:Nlp` options + capability gate; `INlpClient`/`NlpClient` (health cache like docling);
`GET /status` + `/statistics` gain `nlp` block. Fake-client seam for later phases.

Acceptance: status block present/enabled-gated; client fake-handler tests (enrich success,
non-200, timeout, caller-cancel); gate 403 when off does not apply (enrichment is additive -
verify ingestion proceeds with NLP off).

## Phase 3 - docling async + conversion knobs (FR-4)

`DoclingClient` submit/poll/result; timeout across the poll loop; per-request options
(`DoOcr` default false, `TableMode` fast, `OcrEngine`); pre-parse page/size guard.

Acceptance: fake-handler tests for submit->poll(pending->done)->result, poll-loop timeout,
task failure -> `DoclingUnavailableException`, options in the request body; a real end-to-end
kept under the gated live smoke.

## Phase 4 - async ingestion worker + startup sweep (FR-3, FR-5)

Channel-backed `IHostedService` job queue (bounded concurrency + capacity); `POST /document*`
validates, creates the stub, enqueues, returns `202`; over-capacity 503. Startup sweep flips
orphaned `processing` docs to `failed:interrupted`. The pipeline moves off-thread; the
one-failed-Document/zero-chunks invariant and the change-feed lifecycle are preserved.

Acceptance: a job reaches `indexed` (integration, feed observed); capacity 503; sweep test;
all prior ingestion failure-injection tests pass through the async path (adjusted to await
the terminal status via the feed/poll).

## Phase 5 - entity graph (FR-6)

Enrich after chunking; upsert `Entity` vertices deduped by `(normalized,type)` via the
ensured entity-key dictionary index; `mentions` edges chunk->entity (capped); `keyTerms` chunk
property; `enriched` flag on the document; enrichment failure is non-fatal.

Acceptance: fake-NLP tests - dedup across chunks/documents, mentions cap, keyTerms property,
enrich-off => zero entities + `enriched:false`, NLP failure => document still `indexed`.

## Phase 6 - explicit index binding (FR-7)

Remove `EnsureVectorIndex`/`EnsureFulltextIndex` auto-create. Per-namespace persisted binding
(vector/fulltext/entity index ids). `GET/PUT /document/binding`, `POST /document/binding/ensure`.
Ingestion resolves the binding and answers 428 when unsatisfied; never auto-creates.

Acceptance: 428 until bound; `ensure` creates exactly the bound indices with correct shapes;
`PUT` 409 on shape mismatch; binding persists across reload; no code path auto-creates (grep +
test).

## Phase 7 - MCP (FR-9)

`f8_documents` gains `entities` (read) and `binding` (read; `ensure` write-tier). Coverage +
contract tests; snapshot alignment.

Acceptance: MCP tests green; tier gating verified; coverage/contract hold.

## Phase 8 - Studio "Knowledge" (FR-8)

Rename Documents -> Knowledge; route `/q/{ns}/knowledge`; rail entry LAST (after Benchmark).
State panel (binding + index state, create/pick, ingest gated until satisfied); drag-and-drop
dropzone; Entities view; per-hit entity chips; async `processing` reflected via the feed;
degraded modes. Vitest + e2e screenshot spec.

Acceptance: vitest for gating/create/pick, dropzone drop-to-ingest, entities view, async row,
degraded NLP; tsc clean; full vitest green; screenshots recaptured.

## Phase 9 - compose + docs + diagrams + gates (FR-10)

`nlp` sidecar in compose behind the `ingestion` profile (default on; `F8_NLP=false` opts out).
Rework the docs page into the semantic-layer story; both architecture diagrams gain the `nlp`
sidecar; README key-feature line; OpenAPI snapshot regenerated; RETRAIN-LOG decision recorded.

Acceptance: `npm run env:up` cold start healthy with all sidecars; docs build link-checked;
OpenAPI + MCP gates green; full `dotnet test` + web-ui suites green.

## Phase 10 - council + merge

Convene the council (correctness/concurrency incl. the new background worker + entity dedup
races; regressions incl. the async ingest contract change and the removed auto-ensure; scope
incl. German/English correctness and honest deferrals). Fix on the branch, pin with tests,
then `git merge --no-ff` to main + push.
