# Unstructured ingestion - documents in, graph out

Turn unstructured artifacts (PDF, Word, spreadsheets, slides, plain text, markdown) into
ordinary graph state: a document vertex, chunk vertices with embedded text, typed edges,
optional exact-match links into the domain graph. Retrieval over chunks fuses dense and
lexical signals by default; a hit is a vertex, so it seeds `/path` and `/subgraph` like
any other vertex.

## Changelog

- **2026-08-02** Revised after an external design review. Adopted: fused (dense +
  lexical) retrieval as the default read path (FR-11), chunking from structured
  DoclingDocument JSON with markdown fallback (FR-6), per-chunk/per-document provenance
  (FR-4), opt-in exact-match structural linking via index allowlists (FR-13, reverses
  the earlier "no auto-linking" decision), an explicit read-path contract (FR-11,
  FR-12), bounded synchronous ingestion with change-feed-visible progress (FR-2),
  an enforced chunk ceiling (FR-14), re-ingestion semantics (FR-15), embedding-model
  change detection (FR-16), table-aware chunking keeping XLSX in v1 (FR-6). Added
  "Rejected alternatives" and "Unverified" sections. Provider-side sparse vectors
  (review's Path A) rejected against the actual provider contract; fusion is RRF over
  the existing vector and fulltext indices (Path B).
- **2026-08-01** Initial spec.

## Problem

Fallen-8 has the semantic read side: named element embeddings as durable state, bound
`VectorIndex` projections with exact kNN, text-in embedding via the capability-gated
provider, the `semantic` block on traversal (see
[element-embeddings](../../done/element-embeddings/README.md) and
[embedding-provider](../../done/embedding-provider/README.md)). It lacks the write side
for real-world knowledge: no way to hand it a document and get searchable, traversable
graph state back.

Scenario: a user stores describing information as documents, ingests them, then types a
description ("the server that terminates TLS for the shop") to find matching chunk
vertices and traverse from them, over document structure and, when linking was enabled,
over `mentions` edges into the domain graph.

## Decisions

- **Pipeline: parse, chunk, embed, write; document vertex first.** The Document vertex
  is created up front with `status: processing` and finishes `indexed` or `failed`.
  Status transitions are committed property writes, so the existing change feed carries
  ingestion progress (`vertexCreated`, `propertySet` events) with no feed contract
  change. Chunks are written only after embedding succeeds; a failed ingest leaves
  exactly one failed Document vertex and zero chunks.
- **Parsing is a sidecar, exactly like model inference.**
  [docling-serve](https://github.com/docling-project/docling-serve) (MIT), called by the
  apiApp over REST (`/v1/convert/file`). Engine gains no parser. Bare `dotnet run` has
  ingestion off; compose wires the sidecar by default. The browser never talks to
  docling directly. docling-serve is the only converter in v1; a pure-.NET in-process
  conversion backend (PdfPig, OpenXML) is deferred, not rejected: a later
  capability-gated alternative behind the same orchestration.
- **Structured conversion output is primary.** Conversion requests
  `to_formats: ["json", "md"]`; chunking works from the DoclingDocument JSON (heading
  hierarchy, table boundaries, page provenance). Markdown heading-split is the fallback
  (txt/md ingest, or `json_content` absent).
- **Plain text and markdown never need the sidecar.** With docling unreachable, text
  ingestion keeps working; binary formats answer 503 with a reason.
- **Fused retrieval by default (review Path B).** Chunk search fuses dense kNN (bound
  vector index) with the existing fulltext index via reciprocal rank fusion. Rationale:
  dense embeddings systematically miss exact identifiers (part numbers, error codes,
  hex ids), the very token class this feature extracts; published hybrid-versus-dense
  evaluations show large recall gaps on such corpora. Path A (sparse output from the
  embedding provider) is rejected for v1: the provider contract is
  `EmbedAsync(texts) -> float[][]` (dense only,
  `fallen-8-core-apiApp/Embedding/Fallen8EmbeddingProvider.cs`), and the default Ollama
  backend exposes no lexical-weight output at all. Fallen-8's lexical signal is the
  regex fulltext index (match-count scored, not BM25); RRF consumes ranks, so it
  composes, but lexical quality is bounded by that index family.
- **Engine changes: none expected, two candidates flagged.** Everything rides existing
  transactions, embeddings, indices, and scans (`VectorIndexScan` with label
  constraint, `FulltextIndexScan`, `IndexScan`). Two mechanisms are verification items
  (see Unverified); if either fails verification, the fix is a small, tested engine
  change and is called out in the PR rather than worked around.
- **Opt-in exact-match structural linking, no LLM.** Per ingestion request, extracted
  identifier tokens are looked up in an explicit allowlist of existing equality-capable
  indices; exact ordinal match creates a `mentions` edge from chunk to hit, capped per
  chunk. Off by default. No fuzzy matching, no embedding-similarity linking, no type
  inference. Allowlists name index ids, not property names: an equality lookup per
  token needs an index; a property-name match would be a full graph scan per distinct
  token and is rejected.
- **Chunk text lives on the chunk vertex; nothing else is retained.** No original
  binary, no full converted markdown. Provenance (FR-4) preserves citation ability and
  re-ingestion diffing without storing content twice.
- **Synchronous, bounded, instrumented.** Byte cap before parse, page cap after parse,
  chunk caps after chunking, namespace chunk ceiling before write, configurable docling
  timeout. Progress via the document status lifecycle on the change feed. No job queue
  in v1.
- **Memory envelope, stated and enforced.** Estimate per chunk with fused retrieval on:
  UTF-16 chunk text up to 4,000 chars ~ 8 kB, dense vector 4 kB on the element plus
  4 kB in the bound index slab, roughly another text copy in the fulltext index, plus
  bookkeeping: ~25-30 kB resident. 10k chunks ~ 300 MB; 100k chunks ~ 3 GB. This
  estimate is replaced by a measured figure when implementation lands (plan phase 8).
  The ceiling (FR-14) makes the wall an error, not an OOM.

## Functional requirements

- **FR-1 Capability gate and discovery.** Config `Fallen8:Ingestion`: `Enabled` (false),
  `MaxUploadBytes` (32 MB), `MaxPages` (500), `MaxChunksPerDocument` (2,000),
  `MaxChunksPerNamespace` (100,000), `ChunkMinChars` (800), `ChunkMaxChars` (4,000),
  `MaxIdentifiersPerChunk` (64), `MaxLinksPerChunk` (16), `EmbeddingName` ("default"),
  `EnsureVectorIndex` (true), `VectorIndexId` ("documents"), `EnsureFulltextIndex`
  (true), `FulltextIndexId` ("documents-text"), `Docling:Endpoint`,
  `Docling:TimeoutSeconds` (120). All `/document` routes answer 403 while disabled.
  `GET /status` / `GET /statistics` gain an additive `ingestion` block: `enabled`, text
  formats, docling-dependent formats, `docling: { configured, reachable }` (cached
  short-TTL probe), and the configured limits. Studio gates on this block.
- **FR-2 Ingest a file.** `POST /document`, multipart/form-data: `file`; form fields
  `name` (default filename), `embed` (default true), `linkJson` (optional, FR-13),
  `propertiesJson` (optional user tags), `sourceUri` (optional), `replaceDocumentId`
  (optional, FR-15). Formats v1: `.txt`, `.md` (no sidecar); `.pdf`, `.docx`, `.xlsx`,
  `.pptx`, `.html` (docling). Lifecycle: up-front validation (400 format/options, 403
  gate or `embed:true` with provider off, 409 duplicate hash, 413 over byte cap, 507
  ceiling already reached, 503 docling required but unreachable), then Document vertex
  `status: processing`, then parse, page-cap check (fail over `MaxPages`), chunk
  (fail over `MaxChunksPerDocument`, 507-fail if ceiling would be crossed), embed in
  provider batches, resolve links, write chunks + edges + embeddings (+ `mentions`) in
  enqueued transactions, finish `status: indexed` with counts. Any failure after the
  stub removes all created chunks and leaves the stub `status: failed` with an `error`
  property; the invariant is "a failed ingest leaves exactly one failed Document vertex
  and zero chunks". Response: `documentId`, `name`, `sourceFormat`, `status`,
  `chunkCount`, `embedded`, `linksCreated`.
- **FR-3 Ingest raw text.** `POST /document/text`, JSON: `name` (required), `text`
  (required), `format` `markdown` | `plain` (default markdown), plus `embed`, `link`,
  `properties`, `sourceUri`, `replaceDocumentId`. Same lifecycle minus parsing; works
  with the sidecar absent.
- **FR-4 Graph model and provenance.** Document vertex (label `Document`): `name`,
  `sourceFormat`, `sourceUri` (when given), `status`, `error` (failed only),
  `chunkCount`, `pageCount` (when known), `contentHash` (SHA-256 of upload bytes or
  normalized text), `converter` ("docling-serve" or "none"), `converterVersion` (when
  obtainable, see Unverified), `chunkerConfig` (fingerprint, e.g.
  `structured/v1;min=800;max=4000`), `embeddingModel` + `embeddingDimension` (when
  embedded), plus user tags. Chunk vertex (label `Chunk`): `text`, `order`, `kind`
  (`text` | `table`), `headingPath` (when known), `pageFrom`/`pageTo` (when known),
  `identifiers`, plus the same user tags. Edges: `contains` (document to chunk), `next`
  (chunk order), `mentions` (chunk to domain vertex, FR-13). Chunk embeddings are
  written under `EmbeddingName` with the provider's model stamp. Element creation dates
  come from the engine.
- **FR-5 Ensured indices.** First ingestion in a namespace idempotently ensures: the
  bound `VectorIndex` (`VectorIndexId`, provider dimension and metric, bound to
  `EmbeddingName`) and the fulltext index (`FulltextIndexId`) over chunk text, which
  ingestion populates at write time. Existing id with conflicting shape: 409, nothing
  written. Either can be disabled by config; disabling the fulltext index degrades
  FR-11 to dense-only (stated in `/status`).
- **FR-6 Chunking.** Primary: DoclingDocument JSON. Section hierarchy becomes
  `headingPath`; tables stay intact as `kind: table` chunks (markdown-serialized),
  oversize tables split by row windows repeating the header row; PPTX slides and XLSX
  sheets map through the same structure (a sheet's tables become table chunks). Page
  provenance from the document's provenance entries. Fallback: markdown heading-split
  (txt/md, or missing `json_content`). Both paths: merge below `ChunkMinChars`, split
  above `ChunkMaxChars` at paragraph (or row-window) boundaries, deterministic output.
  Identifier extraction (UPPER_SNAKE, CamelCase, mixed underscore, hex ids) runs per
  final chunk, deduplicated, capped at `MaxIdentifiersPerChunk` by first occurrence,
  stored sorted.
- **FR-7 Manage documents.** `GET /document`: summaries plus namespace chunk usage and
  ceiling. `GET /document/{id}`: summary plus per-chunk `id`, `order`, `kind`,
  `headingPath`, pages, `identifiers`, `textPreview` (full text stays one home: the
  chunk property, readable via graph element routes). `DELETE /document/{id}`: removes
  the document, its chunks, and all their edges (including `mentions` and user-drawn
  edges, engine cascade) in one `RemoveGraphElementsTransaction`; standard
  `waitForCompletion`; 404 unknown, 400 not a `Document`.
- **FR-8 Namespace scoping.** Every route answers under `/ns/{ns}/document...`; bare
  URLs alias `default`. Indices, ceilings, and counts are per namespace.
- **FR-9 Studio: Documents screen.** Upload dropzone + raw-text form (gated on the
  `ingestion` block, reasons shown when off), document table (list-caps policy) with
  live progress driven by the existing change feed (status `propertySet` on a
  `Document` triggers refetch), detail view with chunk previews, delete with confirm.
  A memory budget element: namespace chunk count against the ceiling with the estimated
  resident cost. Degraded modes stated: provider off (ingest offered `embed:false`
  only, labelled), docling off (binary formats greyed with reason). Search UI for FR-11
  with the existing hit affordances (inspect, send to canvas, use as path seed);
  stale-model badge per FR-16. Screenshots and docs page per the standing UI rule.
- **FR-10 MCP surface.** `f8_documents` tool, op-level tier gating (the `f8_plugins`
  precedent): `list` / `get` / `search` read tier, `ingest_text` / `delete` write tier.
  Binary upload over MCP: conscious deferral, reason recorded (base64 through tool
  calls is token-hostile; agents hold text). `McpBridgedEndpoints` and coverage/contract
  tests updated in the same phase as the controller.
- **FR-11 Fused chunk search.** `POST /document/search`, JSON: `queryText` (required
  unless `queryVector` given; always used for the lexical side when present),
  `queryVector` (optional client-side dense query), `mode` `fused` | `dense` |
  `lexical` (default fused), `k` (default 10, max 100), `window` (default 0, max 5),
  `groupByDocument` (default false). Dense side: provider-embedded `queryText` (with
  `QueryPrefix`) or `queryVector`, `VectorIndexScan` constrained to vertices with label
  `Chunk`, candidate depth `max(50, 4k)`. Lexical side: `FulltextIndexScan`, same
  depth, ranked by score. Fusion: RRF, `score = sum over sides of 1/(60 + rank)`,
  ties by ascending element id. Results are filtered to live elements before fusion.
  Provider off or no dense query: `fused` degrades to lexical and the response says so
  (`modeUsed`). Fulltext index absent: degrades to dense, same honesty. Dense-side
  model identity mismatch: 409 (existing provider consistency contract). Hit shape:
  `chunkId`, `documentId`, `score`, `text`, `headingPath`, pages, `identifiers`, and
  when `window > 0` the neighbouring chunks over `next` edges in both directions
  (`chunkId`, `order`, `text`). `groupByDocument: true` groups hits per document
  (document metadata included), documents ordered by their best hit, chunks within a
  document ordered by `order`, duplicates collapsed. That is the deduplication and
  ordering contract.
- **FR-12 Read patterns beyond search.** Sibling window: the `window` parameter
  (FR-11). Parent rollup: `groupByDocument` plus `GET /document/{id}`. Arbitrary
  expansion (walk `contains`/`next`/`mentions` from a hit): a documented traversal
  recipe over existing adjacency and path endpoints on the docs page, not new surface.
  The MCP `search` op mirrors FR-11's parameters.
- **FR-13 Structural linking (opt-in).** Request block `link`:
  `{ "indexIds": [...], "maxLinksPerChunk": n }` (`n` capped by config). Validation up
  front: every id must exist and be an equality-capable index (dictionary family), else
  400. For each chunk, identifier tokens in first-occurrence order are looked up per
  allowlisted index (`IndexScan`, exact ordinal equality); each distinct hit gets one
  `mentions` edge from the chunk, self and duplicate targets suppressed, stopping at
  the cap (deterministic: token order, then index order, then ascending element id).
  Chunk vertices from the same ingest are never link targets. Response reports
  `linksCreated`. No fuzzy matching, no similarity linking.
- **FR-14 Enforced ceiling.** `MaxChunksPerNamespace`: checked before the stub (current
  count at ceiling: 507 with a reason) and after chunking (current + new over ceiling:
  ingest fails per FR-2, 507 reason on the failed stub). Usage exposed in
  `GET /document` and `/statistics`. Nothing OOMs silently; the wall is an error.
- **FR-15 Re-ingestion.** Identical bytes (same `contentHash`, same namespace): 409
  pointing at the existing document. `replaceDocumentId`: validated to be a `Document`;
  the new document is ingested fully first; on success the old document and its chunks
  are removed in one transaction; on failure the old document is untouched. Edges
  (including user-drawn ones) onto replaced chunks are removed with them; no edge
  migration in v1, stated.
- **FR-16 Embedding-model change detection.** Documents record `embeddingModel` /
  `embeddingDimension` at ingest (FR-4). `GET /document` flags documents whose recorded
  identity differs from the current provider stamp; Studio shows a stale badge. The
  re-embed path is a documented recipe over the existing bulk `/embedding/elements`
  endpoint (re-embeds from chunk text, bound index reprojects, stamps update); a
  dedicated re-embed endpoint is deferred.

## Non-goals

- No LLM entity extraction, no community summarization (out of scope for v1, per
  review; structural linking only).
- No cross-encoder reranking, no multi-vector or late-interaction scoring, no BM25
  engine build; the lexical signal is the existing fulltext index family.
- No async job queue, progress streaming beyond the change feed, or upload resumption.
- No storage of original files or full converted markdown.
- No per-request chunking knobs; config-level bounds are the contract.
- No document-level embeddings.
- No pure-.NET in-process conversion in v1 (deferred alternative backend, not
  rejected).
- No new change-feed event kinds.

## Rejected alternatives

- **Provider-side sparse vectors (review Path A).** The provider contract returns dense
  `float[][]` only; Ollama has no lexical-weight output; ONNX would need a nonstandard
  export plus a contract change across all three backends. Rejected for v1; becomes
  interesting only if the provider contract ever grows a sparse surface.
- **BM25 lexical engine.** A new index family for better lexical ranking. RRF needs
  ranks, the regex fulltext index provides them; build a better lexical index only on
  relevance evidence against real corpora.
- **Property-name link allowlists.** Equality lookup per token without an index is a
  full graph scan per distinct token (`GraphScan` is O(elements)); rejected for cost.
  Allowlists name index ids.
- **New change-feed event kinds for ingestion progress.** The feed's contract is
  committed mutations with property keys only. The document status lifecycle already
  rides it; a parse-progress event type would change a shipped contract for cosmetic
  gain. Rejected.
- **Cutting XLSX.** Rejected in favour of table-aware chunking from structured output;
  spreadsheets are a common home for identifier-heavy content. Caps bound the
  explosion.
- **Hand-authored sample linking.** Superseded: the shipped sample ingests its dossier
  texts through the real endpoints with linking enabled, demoing the actual feature.
- **Storing sparse weights per chunk.** Moot under Path B; the lexical side lives in
  the fulltext index, whose memory cost is stated in the envelope instead.

## Unverified (resolve during implementation, do not assume)

- **Converter version capture.** docling-serve's convert response carries
  `md_content`/`json_content`/`status`/`timings` but no version field (checked against
  its usage docs). `converterVersion` is best-effort (probe or configured); absent
  otherwise.
- **Fulltext liveness.** Whether the fulltext index filters removed elements at query
  time is unverified (the vector index does; the bucket family may not). FR-11 filters
  hits to live elements regardless; if the engine layer needs the fix, it is a small
  engine change, called out in the PR.
- **Fulltext population path.** The transactional path for adding chunk text to the
  fulltext index at ingest (writer-thread discipline) mirrors whatever the existing
  index-add REST surface uses; exact mechanism confirmed in phase 2.
- **`page_range` on non-PDF formats.** Supported per docling-serve docs; behaviour for
  office formats unverified. The page cap is enforced post-parse from the document's
  page count either way; `page_range` is only an optimization to bound parse cost.
- **DoclingDocument schema surface.** The chunker models a minimal subset (texts,
  sections, tables, provenance). Pin the subset with fixture documents; treat schema
  drift across docling versions as a test-caught event, not a runtime surprise.

## Impact on existing features

- **Engine:** no changes expected; two flagged candidates above, each small and
  explicit if verification fails.
- **REST contract / OpenAPI snapshot:** new `DocumentController` routes (ingest, text,
  list, get, delete, search) and an additive `/status` block; regenerate the snapshot.
- **MCP:** five operations bridged via `f8_documents`, one conscious deferral
  (multipart upload) with reason; coverage/contract tests updated same phase.
- **Change feed:** consumed, not changed; Studio subscribes for document status
  transitions.
- **Studio UI:** new screen and nav entry, `ingestion` status block, memory budget
  element, stale-model badge, screenshots recaptured.
- **NL-assist dataset/eval:** new user-facing endpoints; append a RETRAIN-LOG entry.
- **Docs site:** new page `unstructured-ingestion.md` (+ sidebar), traversal recipe
  section (FR-12), pointers from embedding-provider and studio pages, README
  key-features line.
- **Architecture diagrams (both, same PR):** docling sidecar joins the compose
  environment; house style.
- **Compose environment:** new `docling` service, default on, `F8_INGESTION=false`
  disables the capability and skips the sidecar (profile in `env:up`).
- **Samples:** one sample gains a dossier variant ingested through the real endpoints
  at load time (linking enabled against the sample's indices) when the capability and
  provider are on; loads without them, minus the semantic parts. Never references
  `PUT /unittest`.
- **Element embeddings / embedding provider / stored queries / subgraph / bulk
  import-export:** consumed unchanged; their READMEs remain the single home for their
  stories. Bulk import stays a whole-graph, empty-target tool and is not a re-ingest
  path.

## Test expectations

- **Chunker goldens:** structured path (heading hierarchy, intact tables, row-window
  splits with repeated header, PPTX slides, XLSX sheets, page provenance), markdown
  fallback, bounds, unicode, determinism, identifier extraction with cap and
  false-positive guards.
- **Orchestration failure injection:** at parse, page-cap, chunk-cap, ceiling, embed,
  link-resolve, and each write step; every failure proves the invariant (one failed
  Document vertex, zero chunks, old document untouched on replace); embed-before-write
  order pinned; lifecycle events observed on the change feed in an integration test.
- **Controller matrix:** every documented status code with reason for FR-2/3/7/11/13/
  14/15; namespace twins.
- **Fusion:** deterministic RRF unit tests over synthetic ranks; an integration fixture
  where an exact-identifier query is found fused but missed dense-only; degrade paths
  (`modeUsed`), liveness filtering, model-mismatch 409, window and group ordering.
- **Linking:** exact-match only (case-sensitive, no substring), allowlist validation
  400s, cap determinism, links removed with chunk deletion, same-ingest targets
  excluded.
- **Gated live smoke:** `F8_TEST_DOCLING_ENDPOINT` real conversion end to end.
- **Studio vitest:** gating and degraded states, feed-driven progress refetch, memory
  budget rendering, search UI with seed affordances, stale badge.
- **Gates:** OpenAPI snapshot; MCP coverage/contract; docs build link-checked;
  convention tests hold with zero new NoWarn.

## Revisit triggers

- Documents routinely exceed the synchronous bounds on real corpora: async ingestion
  job with a status resource.
- Relevance complaints where fused retrieval genuinely misses: better lexical index
  (BM25-class) or reranker, benchmarked against the complaint corpus first.
- The provider contract grows a sparse/multi-vector surface: reopen Path A.
- Exact-match linking proves too strict in practice: specify fuzzy or type-aware
  linking as its own feature, never silently.
- Stale-model detection fires often: promote the re-embed recipe to an endpoint.
- Corpora pressing the ceiling: namespace partitioning and curation remain the answer;
  revisit only if that stops being acceptable.
- Demand for whole-document search: document-level summary embeddings.
- A no-Python deployment requirement materializes: the deferred pure-.NET conversion
  backend.
