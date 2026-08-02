# Semantic layer - the Knowledge screen, an entity network, and non-blocking ingestion

An evolution of [unstructured-ingestion](../../done/unstructured-ingestion/README.md) into a
**semantic layer**: documents become a traversable knowledge graph, not just searchable
chunks. Named entities and key terms are extracted from chunk text by a local spaCy service
and become first-class, deduplicated **Entity vertices** that chunks `mentions`; ingestion is
**asynchronous** so a large scanned PDF never holds a request open; index creation is
**explicit** (pick or create, never automatic); and the Studio screen is renamed **Knowledge**
and moved to the bottom of the rail.

## Problem

Three things surfaced once the shipped feature met real documents:

1. **"Documents" undersells it and hides in the middle of the rail.** The value is a semantic
   layer over a knowledge graph, not a document list.
2. **Large real documents block.** A German legal Gutachten is scanned, so docling runs full
   OCR + layout + table models per page (docling's own report: ~0.6 s/page CPU *before* heavy
   OCR). Synchronous ingestion holds the HTTP request open for minutes and the row sits at
   `processing` with no way to walk away.
3. **A chunk graph is not a knowledge graph.** Retrieval finds chunks, but chunks only connect
   to their siblings and (optionally) to pre-existing domain vertices by exact identifier
   match. There is no notion of the *entities* a corpus is about, so you cannot traverse
   "which documents mention this person / organization / place".

And two decisions the shipped feature made are now wrong for real use: indices are
auto-created on first ingest (the operator wants to choose), and upload is a plain file input
(the operator wants to drop a file on the page).

## Decisions

- **Entities are a local NLP job, not an LLM.** A standalone, Dockerized **spaCy** service
  (`fallen-8-nlp`, FastAPI, MIT) enriches chunk text: named entities (`doc.ents`) and key
  terms (`doc.noun_chunks`). spaCy is fast, CPU-cheap, offline, and MIT - it sidesteps the
  exact cost objection that made the original spec defer *LLM* entity extraction (~75% of
  GraphRAG indexing cost), while delivering the entity network. It is a separate deployable
  like docling and Ollama: the engine and apiApp never import a model runtime.
- **German AND English, chosen deliberately, with the model configurable.** The service loads
  a German and an English spaCy model (both MIT) and routes each document by detected language
  (hint overridable). Running English NER on German legal text was explicitly rejected: it
  produces near-garbage entities. The model per language is a config/build knob
  (`F8_NLP_MODEL_DE`/`F8_NLP_MODEL_EN`, default the `sm` models) so a hard domain like legal
  German can trade up to `md`/`lg` without a code change - the build bakes the chosen model,
  the runtime loads the same name. Other languages are a further model-add.
- **`spacy-layout` is NOT used.** docling already does layout, reading order and tables (it is
  the reason docling is in the stack, and `spacy-layout` wraps docling anyway). spaCy's value
  here is NER + noun-chunk terms over the text docling already extracted - no second layout
  pass.
- **Entities are first-class vertices; chunks mention them.** Each entity becomes an `Entity`
  vertex (properties `text`, `type` (the spaCy label: PERSON/ORG/LOC/...), `normalized`),
  **deduplicated per namespace** by `(normalized, type)`; a chunk links to it with a
  `mentions` edge. Key terms (noun chunks) stay a chunk property (`keyTerms`), like today's
  `identifiers` - cheap tags, not nodes. This is the "semantic layer network": traverse
  chunk -mentions-> Entity <-mentions- chunk to find co-mentioning documents.
- **`mentions` is one edge type for two sources, and that is correct.** FR-13 identifier
  linking connects a chunk to a pre-existing DOMAIN vertex you already had; entity extraction
  connects a chunk to a NEW `Entity` vertex the corpus produced. Both mean "this chunk refers
  to this vertex"; they coexist on the same edge type. Entity dedup is exact normalized-string
  per type in v1 (fuzzy resolution, e.g. "Dr. A. Muster" vs "Muster", is a revisit trigger).
- **Ingestion is asynchronous.** `POST /document` returns immediately with the `processing`
  stub; a bounded in-process background worker drives parse -> chunk -> embed -> enrich ->
  write -> finish and the change feed carries every status transition (already true). docling
  is called through its **async task API** (`/v1/convert/file/async` + poll + result), so no
  request is held open. Conversion knobs (OCR off by default, `table_mode: fast`, a pre-parse
  page/size guard) cut the common cost. A `failed` document carries its reason exactly as
  before. On restart, a document still `processing` (its worker died) is swept to `failed`
  with reason `interrupted` - no silent zombie.
- **Index creation is explicit, never automatic.** The auto-ensure is removed. The Knowledge
  screen shows the **layer binding** for the namespace (which vector + fulltext + entity-key
  indices back it, and whether each exists with the right shape) and lets the operator **pick
  existing compatible indices** or **create the required ones** with a button. Ingestion is
  **blocked (428 Precondition Required)** with a clear message until the binding is satisfied;
  it never creates an index behind the operator's back.
- **REST resource stays `document`; the Studio screen becomes "Knowledge".** You upload
  documents (the unit); "Knowledge" is the concept the layer builds from them. Renaming the
  REST/MCP resource would churn the OpenAPI snapshot, MCP bridge and tests for no external
  gain (nothing depends on it yet, but the resource noun is honest). The change is Studio-only:
  rail label + route `/q/{ns}/knowledge`, positioned last, after Benchmark.

## Functional requirements

- **FR-1 NLP service (`fallen-8-nlp`).** A standalone FastAPI app, its own `Dockerfile`
  (installs `spacy`, `de_core_news_sm`, `en_core_web_sm`, a language detector). `GET /health`;
  `POST /enrich` takes `{ items: [{ id, text }], languageHint? }` and returns per item
  `{ id, language, entities: [{ text, label, start, end }], keyTerms: [string] }` - clean,
  JSON-serializable, no graph knowledge. Language per item: hint, else detected, else the
  default model. Batch-friendly (spaCy `nlp.pipe`). Bounded input (max items, max chars);
  over-limit is a 413. It never calls Fallen-8 (one-way: F8 -> nlp).
- **FR-2 NLP capability gate + discovery.** apiApp config `Fallen8:Nlp` (`Enabled` default
  false, `Endpoint`, `TimeoutSeconds`, `MaxCharsPerChunk`). Disabled: ingestion still runs, no
  enrichment happens, and the entity network is simply empty (never a hard failure - enrichment
  is additive). `GET /status` gains `nlp: { enabled, configured, reachable }` (cached probe,
  like docling). The compose environment runs the sidecar by default; a bare `dotnet run` has
  it off.
- **FR-3 Async ingestion, one global FIFO queue.** `POST /document` and `POST /document/text`
  validate up front (as today: format, gate, dup-hash, byte cap, binding satisfied), create the
  `processing` stub, ENQUEUE a job, and return `202 Accepted` with the stub summary. There is a
  **single global `Channel`-backed queue shared across ALL namespaces**, drained by ONE
  consumer (`IHostedService`) in strict **arrival order** (FIFO) - a large scanned PDF in one
  namespace delays later jobs everywhere, which is the honest trade for in-order, bounded,
  single-writer-friendly processing. The queue is bounded; over-capacity enqueue answers `503`
  with a reason. **Each job carries its namespace name** (plus the stub's documentId and the
  validated request); the worker re-resolves the concrete engine from that name via
  `Fallen8Namespaces` at processing time. This is deliberate: the namespace is addressed on the
  request thread through an `AsyncLocal` that does NOT flow to the worker thread, so the job
  must name its namespace rather than rely on the ambient. If the namespace was dropped before
  the job ran, the job is discarded with a log line (its Document went with the namespace); if
  the stub is gone or no longer a `processing` `Document` (a reload reassigned ids), the job is
  skipped. The pipeline, its failure/cleanup invariant (one failed Document, zero chunks), and
  the change-feed lifecycle are unchanged except for running off the request thread on the
  re-resolved engine.
- **FR-4 docling async + knobs.** The docling client submits via `/v1/convert/file/async`,
  polls `/v1/status/poll/{task_id}`, fetches `/v1/result/{task_id}`, honouring the configured
  timeout across the poll loop. Conversion options are sent per request:
  `Fallen8:Ingestion:Docling` gains `DoOcr` (default false), `TableMode` (`fast`),
  `OcrEngine`. A pre-parse guard rejects an upload over `MaxUploadBytes` (exists) or, for a
  known page count, over `MaxPages` BEFORE submitting (413), so the cap saves time instead of
  being discovered after conversion.
- **FR-5 Startup sweep.** On engine load, any `Document` left `status: processing` (a worker
  that never finished across a restart) is swept to `failed` with `error: interrupted` in one
  transaction, so the list never shows a permanent zombie.
- **FR-6 Entity extraction into the graph.** When the NLP provider is on and `embed`/enrich is
  requested, the background pipeline calls `POST /enrich` with the chunk texts (batched,
  bounded), then, in the same write phase as chunks/edges: upserts `Entity` vertices
  (dedup key `type + "" + normalized`, normalized = casefold+trim; one vertex per key
  per namespace via the ensured entity-key index), creates `mentions` edges chunk -> entity
  (capped per chunk by `MaxEntitiesPerChunk`), and writes `keyTerms` as a chunk property
  (deduped, capped). Enrichment failure does NOT fail the ingest (additive): the document
  still indexes, `enriched: false` is recorded, and the reason is logged.
- **FR-7 Explicit index binding (no auto-create).** Per-namespace persisted binding
  `{ vectorIndexId, fulltextIndexId, entityIndexId }` (defaults from config). `GET
  /document/binding` returns the binding plus each index's live state (`exists`, `shapeOk`,
  and for the vector index the dimension/metric/embeddingName/model vs the provider). `PUT
  /document/binding` sets the ids (validates they exist with the right shape, else 409).
  `POST /document/binding/ensure` creates the bound indices that are missing, with the correct
  shapes, in one call (explicit button; never implicit). Ingestion resolves the binding and
  answers **428** with a message naming the missing/incompatible index when it is not
  satisfied - it NEVER auto-creates. The entity-key index is a dictionary index over the
  entity dedup key.
- **FR-8 Studio "Knowledge" screen.** The screen is renamed **Knowledge**, its route is
  `/q/{ns}/knowledge`, and its rail entry moves to the BOTTOM, after Benchmark. It gains: a
  **State panel** at the top showing the binding and each index's state, with "Create the
  required indexes" and per-index "pick existing" selectors (nothing is auto-created; ingestion
  controls are disabled with the reason until the binding is satisfied); a **drag-and-drop
  dropzone** over the upload area (drop a file anywhere on it; the file picker stays as a
  fallback); an **Entities** view (the namespace's `Entity` vertices by type with mention
  counts, each a send-to-canvas/inspect affordance); and per-search-hit entity chips. Async is
  reflected: upload returns immediately, the row appears `processing` and flips live via the
  change feed. Degraded modes stated: NLP off (no entities, labelled), provider off
  (text-only), docling off (binary greyed). Screenshots + docs per the standing rule.
- **FR-9 MCP surface.** `f8_documents` gains `entities` (read: list/inspect the namespace's
  entities, filter by type) and `binding` (read the layer binding + index state; `ensure`
  under the write tier). `ingest_text` returns the `202`/processing shape. Coverage/contract
  tests updated; the enrich/entity write path is server-side so no new bridged docling/nlp
  routes appear in the F8 OpenAPI surface.
- **FR-10 Compose.** A `nlp` service (built from `fallen-8-nlp/Dockerfile`) joins the
  environment behind the same `ingestion` profile, default on, wired via `Fallen8:Nlp`.
  `F8_NLP=false` disables the capability and skips the sidecar. docling image stays pinned.

## Non-goals

- No LLM entity extraction, no relation extraction between entities, no coreference beyond
  exact normalized-string dedup, no entity linking to external KBs (Wikidata).
- No languages beyond German/English in v1 (model-add follow-up).
- No async job persistence/durability across restart beyond the FR-5 sweep (a job in flight
  at shutdown is lost and its document fails `interrupted`; re-upload to retry).
- No progress percentage or streaming; the change feed's status transitions are the progress.
- No `spacy-layout`, no re-doing docling's layout.
- No REST/MCP resource rename (`document` stays); Studio-only rename to "Knowledge".
- No engine changes beyond what a review proves necessary (the shipped feature needed exactly
  one small index guard; this increment aims for zero and flags any that arise).

## Impact on existing features

- **unstructured-ingestion:** this supersedes its synchronous, auto-ensure, chunk-only model;
  its README/spec stay the historical record and gain a pointer here. The `document` REST/MCP
  surface is extended, not broken (ingest now `202`; a new `binding` sub-resource; `entities`).
- **Engine:** entities/edges/properties are ordinary graph state; the entity-key index is an
  ordinary dictionary index. No engine change expected (the RegExIndex removed-guard from the
  prior council already landed on main).
- **REST/OpenAPI snapshot:** new `binding` routes, `entities` read, `202` on ingest; regenerate.
- **MCP:** `f8_documents` grows ops; coverage/contract tests updated.
- **Studio:** rename + reorder + new panels; status store gains `nlp`; screenshots recaptured
  (rail changed again).
- **NL-assist:** no delegate-fragment surface change (entities are data, drafted filters
  reference `Entity`/`mentions` exactly as any user label) - checked against RETRAIN-LOG's own
  criteria, no entry, per the prior increment's precedent.
- **Docs/diagrams:** the `nlp` sidecar joins both architecture diagrams; the docs page is
  reworked into the semantic-layer story; README key-feature line updated. **The end-to-end
  pipeline is documented explicitly** with its own flow/sequence diagram: upload -> (docling
  async convert, binary only) -> chunk + identifier extraction -> embed -> NLP enrich (entities
  + key terms) -> write Document/Chunk/Entity vertices + contains/next/mentions edges, with the
  async status transitions shown riding the change feed. Not just screenshots.
- **Compose/env:** a fourth sidecar; document the footprint honestly.

## Test expectations

- **NLP service:** pytest for `/enrich` (German + English fixtures: entities and key terms
  present and language-routed; empty text; over-limit 413; batch), `/health`.
- **DoclingClient async:** fake-handler tests for submit -> poll (pending then done) -> result,
  timeout across the poll loop, task failure -> `DoclingUnavailableException`.
- **Background worker:** a job runs to `indexed`; capacity limit answers 503; the FR-5 startup
  sweep flips a seeded `processing` doc to `failed:interrupted`.
- **Entity graph:** a fake NLP client yields deterministic entities; assert `Entity` vertices
  are deduped per `(normalized,type)`, `mentions` edges are created and capped, `keyTerms`
  land as a chunk property, enrichment-off leaves zero entities and `enriched:false`, an NLP
  failure does not fail the ingest.
- **Binding:** ingest is 428 until the binding is satisfied; `ensure` creates exactly the
  bound indices; `PUT` validates shapes (409 on mismatch); no path auto-creates.
- **Studio vitest:** State-panel gating (ingest disabled until bound), create/pick actions,
  dropzone (drop event ingests), entities view, async `processing` row, degraded NLP.
- **Gates:** OpenAPI snapshot; MCP coverage/contract; docs build link-checked; convention
  tests; a gated live smoke (`F8_TEST_NLP_ENDPOINT`) for the real spaCy service.

## Revisit triggers

- Fuzzy/canonical entity resolution (same entity, different surface forms) once exact dedup
  proves too coarse.
- More languages, or a larger model (`de_core_news_lg`) if `sm` accuracy is short.
- Durable async job queue if a restart mid-corpus is a real operational loss.
- Relation extraction / entity linking to external KBs if the network needs typed edges
  between entities, not just co-mention.
- The binding concept generalizing to other per-namespace feature settings (promote the
  persisted-settings mechanism).
