# Knowledge demo: the semantic layer as a loadable sample

A one-click Samples gallery entry that shows the semantic layer doing the thing it was built
for: authored documents go in through the REAL pipeline, and the knowledge graph that comes out
is joined to an ordinary domain graph, so a question is answered by BOTH graphs together.

This is the gallery sample [unstructured-ingestion](../../done/unstructured-ingestion/spec.md)
deferred with a named revisit trigger ("build it when the sample loader gains a document-ingest
step for another reason, **or on explicit request**"). This is the explicit request.

## Changelog

- **2026-08-04** Initial spec, written after de-risking the whole chain against a live compose
  environment (see "Verified up front"). Scenario and deliverable scope chosen by the user.

## Problem

The semantic layer is the largest capability in Fallen-8 with no demo. A newcomer on the
Knowledge screen faces an empty state, a binding that is not created, and no documents, and the
one worked example lives in prose on the docs site. Worse, the deferral reasoning still holds
for every SHIPPED sample: they name vertices in prose with spaces and hyphens (`DC01`,
`DOMAIN ADMINS`, `FIN-FS01`), which the identifier extractor does not extract, so pointing the
existing gallery at the semantic layer would link nothing and demo nothing.

The feature's real thesis is also its least discoverable: a Chunk vertex is an ORDINARY vertex,
so the text you searched and the assets you operate are one graph. Nothing in the product
currently shows that.

## Verified up front (live, before speccing)

Each of these was proven against a running compose environment, and several contradict what the
documentation implies. They are requirements-shaping facts, not assumptions.

- **A `DictionaryIndex` does NOT backfill on creation.** A fresh index created after its
  vertices existed answered `POST /scan/index/all` with `[]`, while a hand-seeded twin answered
  `[0]`. There is no bulk index-add route: only `PUT /index/{indexId}`, one element per call. A
  bound `VectorIndex` DOES project existing embeddings, which is why the embedded samples work
  and why this gap was never noticed.
- **The loader creates indexes AFTER `POST /bulk/import`** and has no document step at all
  (`fallen-8-web-ui/src/lib/sampleLoader.ts`). There is no C# sample loader; the gallery is
  client-side orchestration over REST, and `SampleManifestEntry.indexRecipes` is literally the
  `POST /index` body.
- **Identifier extractor bars** (`fallen-8-core-apiApp/Ingestion/IdentifierExtractor.cs`):
  underscore tokens must start uppercase and run at least 4 characters, CamelCase needs two
  humps and at least 6 characters, plus `0x` hex. `WTG_A17` and `GBX_BATCH_2023_11` extract;
  lowercase or hyphenated tags do not. Linking is ordinal-exact against the indexed value.
- **A PDF figure caption is silently dropped.** The chunker models texts, sections and tables;
  caption text docling attaches to a picture never becomes chunk text.
- **Chunking merges below 800 characters,** so a short multi-section document collapses into ONE
  chunk. Authored sections need roughly 900 characters or more to yield a real chunk chain.
- **A table becomes one `kind: table` chunk** serialized as markdown, and identifiers are
  extracted from every cell: a 4-row register yielded 8 asset tags, which makes a table chunk a
  natural many-way hub into the domain graph.
- **NER mistypes identifiers** (`WTG_A17` came back `GPE`, `NW_STD_0417` came back `ORG`).
  Expected noise from a general English model; the demo must not present entity types as
  authoritative.

## Decisions

- **Scenario: wind-farm fleet asset integrity.** "Northwind Energy" operates turbines off
  Esbjerg in the Danish North Sea. Asset tags are natively identifier-shaped, which is the
  honest reason to pick this domain: industrial tagging conventions and the extractor's grammar
  agree without contrivance. Rejected: an SRE/microservice scenario (would rehash the docs
  page's existing `EDGE_TLS_01` example) and a pharma cold-chain one (a general English NER
  model types biomedical terms poorly, so the entity network would read as noise).
- **The domain graph is baked; the documents are ingested LIVE.** This is the central decision.
  Baking chunks, entities and `mentions` edges into the jsonl would be faster and fully
  deterministic, and it would demo nothing: the pipeline is the feature. So the operational
  graph (turbines, gearboxes, batches, substations, work orders) imports as ordinary
  `fallen8-jsonl` like every other sample, and the three documents go through `POST /document`
  and `POST /document/text` exactly as a user's would, converting in docling, embedding through
  the provider, enriching in the NLP sidecar, and linking into the imported graph.
- **Three documents, three converter paths, one story.** A PDF with a figure (docling PDF path),
  a spreadsheet register (docling XLSX path, and the table-chunk hub), and a markdown standard
  (no sidecar at all). Each explains a mechanism, so retrieval answers "why" questions rather
  than matching keywords.
- **The documents are committed assets, not build output.** Authoring them needs a PDF and XLSX
  writer; the sample build is `vite-node` TypeScript. Adding a document-generation toolchain to
  the build to re-derive three static files on every run is bloat, and the repo already pins
  network-sourced sample inputs (the stored SBOM, the curated movie list) rather than refetching
  them. The three files live under `samples/documents/` with a committed generator script
  recording provenance, run by hand when the content changes.
- **Index seeding stays client-side.** Because creation does not backfill, the loader seeds the
  asset-tag index with one `PUT /index/{indexId}` per tagged vertex: **89 calls** for this dataset
  (89 of its 94 vertices carry an `assetTag`; the five technicians deliberately do not). A bulk
  index-add endpoint would be the better engine answer and is a named revisit trigger, NOT part of
  this feature: adding REST surface to ship a demo inverts the cost.
- **Every new manifest field is optional.** The five existing samples keep validating and the
  loader's behaviour for them is byte-identical; a sample without `documents` never touches the
  `/document` surface.
- **Degraded modes are stated, never silently skipped.** The card reports what the sample needs
  (ingestion on, docling for the binary documents, NLP for the entity network, a matching
  embedding provider) and refuses rather than half-loading into a misleading state.
- **Never references `PUT /unittest`.** The standing rule; this sample is the intended answer to
  "give me a populated graph".

## The scenario, concretely

**The domain graph (baked):** `Site`, `Turbine`, `Gearbox`, `CastingBatch`, `Substation`,
`GridConnection`, `WorkOrder`, `Technician` and `Standard` vertices. Assets carry an `assetTag`
property holding an identifier-shaped tag (`WTG_A17`, `GBX_A17_02`, `GBX_BATCH_2023_11`,
`SUB_NORD_02`); `Technician` deliberately carries none, so people reach the graph through NER
instead. The seven edge types, as shipped: `has_component`, `from_batch`, `feeds`, `located_at`,
`performed_on`, `carried_out_by`, `applies_to`.

**The documents (ingested live):**

1. `nw-rca-wtg-a17.pdf` (picture) A root-cause analysis of the `GBX_A17_02` failure. Explains
   the mechanism: subsurface rolling contact fatigue initiates micro cracks, the first through
   crack leaves a pit, the pit wrecks the contact geometry so spalling self-accelerates, and the
   low-hardness casting batch brought it forward of design life. Carries a
   vibration-spectrum figure whose meaning is ALSO in body prose, because captions do not
   survive conversion.
2. `nw-fleet-register.xlsx` (table) The maintenance register: asset tag, component, batch, last
   service, vibration reading, status. Deliberately lists only recently serviced units, so the
   table does not give away the full batch membership.
3. `nw-std-0417.md` (text only) Engineering standard `NW_STD_0417`. Explains WHY the alarm sits
   at 4.5 mm/s RMS: sidebands around gear mesh frequency spaced at bearing pass frequency are
   diagnostic, so a single overall level would either miss early spalling or cry wolf.

**Shared entities the NER discovers across documents:** `Northwind Energy` and the two
fictional gearbox manufacturers (ORG), `Esbjerg`, `Denmark`, `North Sea` (GPE/FAC/LOC), and the
reliability engineer who signs all three (PERSON). Deduplicated per namespace on (type, normalized text), so
one vertex per entity with a mention count across the corpus.

**The payoff, which needs both graphs.** Ask *"why did the bearing fail"* on the Knowledge
screen. The top chunk is the RCA's explanation. That one chunk vertex has `mentions` edges
reaching BOTH the NER entities and the domain assets `WTG_A17` and `GBX_A17_02`. Send it to the
canvas, expand `GBX_A17_02` to `GBX_BATCH_2023_11`, expand that, and six more gearboxes appear,
five still in service on live turbines feeding `SUB_NORD_02`. **No document states this.** The
documents explain the mechanism; the graph computes the blast radius. That is the demo.

## Functional requirements

- **FR-1 Manifest contract, additively extended.** `SampleManifestEntry` gains three optional
  fields: `indexSeeds?: { indexId, propertyId }[]` (fill an equality index from an imported
  property), `documents?: SampleDocument[]`, `linkIndexIds?: string[]` (the linking allowlist
  passed on every ingest), and the `knowledge` member joins `SampleBadge`.
  `SampleDocument` is `{ file, name, kind: "text" | "binary", format?: "markdown" | "plain" }`.
  All optional: absent means the loader behaves exactly as today.
- **FR-2 Loader: seed the index.** After the existing index-recipe step, for each `indexSeeds`
  entry the loader enumerates imported vertices carrying `propertyId` and issues one
  `addToIndex` per vertex, keyed on that property's string value. Reports progress as a count.
  A `false` answer (index or element absent) fails the load loudly rather than proceeding to a
  linking step that would silently link nothing.
- **FR-3 Loader: bind the semantic layer.** When `documents` is non-empty, `POST
  /document/binding/ensure` before the first ingest, and surface a non-ready result as a failure
  with the role detail. This is the "all required indexes" prerequisite, created explicitly.
- **FR-4 Loader: ingest the documents.** In manifest order, fetch each asset from the samples
  base and ingest it: `text` through `ingestText`, `binary` through `ingestFile` as multipart.
  Both carry `link: { indexIds: linkIndexIds }`. Ingestion is asynchronous (202), so the loader
  polls `GET /document/{id}` until `indexed` or `failed`, with a bounded timeout, and reports
  which document is converting. A `failed` document fails the load with the server's reason.
- **FR-5 Ingest wrapper gains linking.** `ingestFile` gains `link` and passes it as the
  `linkJson` form field (the multipart twin of the JSON `link` block). `ingestText` already
  carries `link`.
- **FR-6 Gating and degraded modes.** An `ingestionGate(entry, status)` mirroring the existing
  `embeddingGate` resolves to one of seven states: `not-needed`, `ready`, `status-unknown` (BLOCKING, and
  deliberately without a diagnosis: `/status` has not resolved yet, so naming a cause would send
  the user to fix a setting that is probably already right), `ingestion-off`,
  `provider-off` (BLOCKING: chunks embed at ingest, and a provider-less load would demo half the
  feature), `docling-unreachable` (blocking, but only when the sample has binary documents), or
  `nlp-off` (a WARNING, not a block: enrichment is additive, so the sample still loads with no
  entity network and the card says so). The Samples card disables loading for blocking states and
  names the environment variable that fixes it.
- **FR-7 The dataset.** A deterministic generator, fixed seed and fixed `creationDate`, emitting
  the domain graph with `assetTag` on every asset, an `icon` per label for the canvas, and a
  `styleConfig` colouring by label. The batch that fails has seven members so the blast radius
  is visibly larger than the documents' coverage.
- **FR-8 trySteps carry the discovery.** The card's post-load steps walk the payoff explicitly:
  the search query to type, the chunk to send to canvas, the two expansions, and what to notice
  (five at-risk turbines no document names).
- **FR-9 Studio surface.** The knowledge badge renders; the gate renders; the new loader steps
  appear in the progress line. No new screen: the sample lands the user on the existing
  Knowledge and Canvas screens, which is the point.
- **FR-10 Namespace honesty.** The loader operates on the instance's selected namespace like
  every other sample, and the document surface is namespace-scoped, so the sample works in a
  dedicated namespace without touching `default`.

## Non-goals

- No new REST surface, no engine change. Specifically no bulk index-add and no index backfill
  (both named revisit triggers).
- No baked chunks, entities or `mentions` edges: the pipeline runs for real or the sample fails.
- No OCR. The figure is decorative by design; its meaning is in prose.
- No LLM in the loop anywhere. Linking is exact-match, enrichment is spaCy.
- No new change-feed events, no new document formats, no chunking knobs.
- No second gallery sample, and no retrofit of the existing five to use `indexSeeds`.

## Impact on existing features

- **Engine:** none. Nothing in `fallen-8-core` changes.
- **REST contract / OpenAPI snapshot:** none. Every call the loader makes already exists, so the
  snapshot does not move (asserted). The `linkJson` form field is already accepted by
  `POST /document`.
- **apiApp static-file serving:** NO change, after a false start. A `.md -> text/markdown` mapping
  was added on the assumption that an unmapped extension would make the SPA fallback serve
  `index.html` (which would have meant ingesting a web page), then removed once the council
  checked the actual runtime: `.md`, `.pdf` and `.xlsx` are all already in
  `FileExtensionContentTypeProvider`'s default map. Only `.jsonl` genuinely is not, and that
  mapping already existed. The hand-rolled tables in `vite.config.ts` and `nginx.conf` DO need the
  entries, because they are not the framework default.
- **Git attributes (required, found by the council):** `samples/documents/*.pdf|.xlsx` are marked
  `binary` and `*.md` is pinned to `eol=lf`. The PDF contains no NUL byte, so git's heuristic
  classifies it as TEXT and `core.autocrlf=true` (the Git-for-Windows default) rewrites its 138 LF
  bytes on checkout, shifting every later offset so `startxref` lands mid-stream and the file is
  unreadable. Since `docker build` uses the working tree as context, a Windows-built image would
  have shipped the broken PDF. Proven with a clone test, fixed, and re-proven.
- **Studio bundle packaging (added during implementation):** `vite.config.ts`'s sample copy
  becomes recursive (a flat `copyFileSync` threw `EPERM` on the new `documents/` subdirectory,
  which would have broken the production build and both images) and gains a
  `SHIPPABLE_SAMPLE_FILE` ALLOWLIST. Allowlist rather than denylist on purpose: with a recursive
  copy, anything later added beside the assets (a venv, a cache, a large authoring file) would
  otherwise land in the image by default. That predicate now defines what ships from `samples/`,
  so it is a packaging contract, not a tidy-up.
- **MCP:** none. No REST operation is added, so engine to REST to MCP propagation has nothing to
  carry and `McpRestCoverageTest` is unaffected.
- **Sample gallery (`sample-graphs`):** the manifest schema grows three optional fields and one
  badge, the loader grows three steps, and a seventh sample joins the registry. The five
  existing entries are untouched and their load path is unchanged.
- **Unstructured ingestion / semantic layer:** this is the demo those features deferred. Their
  READMEs stay the single home for the pipeline's story; the new docs page points at them rather
  than restating them, and the deferral in
  `features/done/unstructured-ingestion/spec.md` is marked resolved with a pointer here.
- **Studio UI:** Samples card badge and gate, loader progress steps. Screenshots recaptured per
  the standing UI rule (Samples card, and the Knowledge screen populated).
- **NL-assist dataset/eval:** no entry. `nl-assist-finetune/RETRAIN-LOG.md` records changes to
  the delegate-fragment surface only, and this feature adds no delegate kind, fragment idiom or
  prompt-contract change. Conscious decision, not an omission.
- **Docs site:** REVISED during implementation. No new page and no sidebar entry: the walkthrough
  lives as a section on the existing samples page, which is where every other sample is
  documented, and the semantic-layer page gains a pointer to it. A third page would have restated
  the pipeline that `unstructured-ingestion.md` already owns and the gallery mechanics that
  `samples.md` already owns, against the one-home-per-explanation rule. The samples page's claim
  that "no embedding work happens at load time" was also corrected, since it is no longer true of
  every sample. Root README: an entry under the existing Samples section rather than a
  Key features line, because this is a demo OF the semantic layer (which already has its line),
  not a new capability.
- **Architecture diagrams:** unchanged. No new channel, deployable or layer; the sample uses the
  existing docling and NLP sidecars already drawn.
- **Compose environment:** unchanged. The sample needs `F8_INGESTION` and `F8_NLP` on, which is
  already the `env:up` default.

## Test expectations

- **Loader unit tests (vitest, mocked fetch):** seeding issues one add per tagged vertex with
  the right key and skips untagged ones; a `false` add fails the load; binding non-ready fails
  with the role detail; text and binary documents route to the right endpoint with the link
  block attached; polling resolves on `indexed`, fails on `failed`, and gives up on timeout;
  a sample with no `documents` makes zero `/document` calls (the no-regression pin for the
  existing five).
- **Gate unit tests:** every `ingestionGate` branch, including `nlp-off` degrading to a warning
  that still permits loading, and `docling-unreachable` only blocking when the sample actually
  has binary documents.
- **Manifest contract test:** the committed `samples/index.json` parses against the types, the
  new sample's counts match its jsonl, every `linkIndexIds` id is produced by an `indexRecipes`
  entry, and every `documents[].file` exists on disk.
- **Generator determinism:** building twice yields byte-identical jsonl.
- **Gates:** vitest and tsc clean, `dotnet build` and `dotnet test` unaffected and green, docs
  site builds link-checked, OpenAPI snapshot unchanged (asserted, since a diff here would mean
  accidental REST surface).
- **Live end-to-end run:** the sample loaded against the compose environment, with the payoff
  traversal walked and the counts recorded in the plan's run ledger.

## Known limitations, accepted

- **The load is not cancellable.** `loadSampleGraph` takes no `AbortSignal`, so navigating away
  mid-load leaves the poll loop running to completion while the mutation observer is gone: the
  server finishes the work and the canvas is never populated. Pre-existing in the loader, but this
  feature stretches the window from about a second to minutes, so it is recorded rather than
  quietly inherited. Threading the mutation's signal through `fetch` and `getDocument` is the fix
  when it matters.
- **Two full-graph reads per load.** The seed step reads the graph to find tagged vertices and the
  render step reads it again. They cannot be collapsed: ingestion happens between them and adds
  the chunk, document and entity vertices the canvas is supposed to show.
- **The XLSX has no `headingPath`.** docling gives a spreadsheet sheet no heading hierarchy, so
  the register's chunks carry none. Harmless, and visible in the Studio's chunk list.

## Revisit triggers

- **Seeding gets slow or runs against a remote instance:** add a bulk index-add endpoint (or an
  index backfill on creation) and collapse the seed step onto it. The arithmetic to beat is 89
  sequential round trips for this dataset, negligible on localhost and roughly a round-trip-time
  multiple anywhere else; treat 2 seconds of seeding as the threshold worth acting on.
- **Index backfill lands for another reason:** drop `indexSeeds` entirely; it becomes dead
  weight the moment `POST /index` populates itself.
- **Ingestion at load time proves too slow** (docling PDF conversion dominates the load): offer
  a text-only variant of the sample, or bake the chunks with a loud label saying the pipeline
  did not run. Only with a measured number.
- **A second document-bearing sample is wanted:** the manifest fields are already general, but
  revisit whether the three loader steps deserve extraction from `loadSampleGraph`.
- **Figure captions start surviving conversion** (chunker grows picture support): the RCA's
  prose duplication of the caption can relax.
- **NER noise on asset tags bothers users in the demo:** consider an entity-type filter on the
  Knowledge screen's default view; do not silently drop entities.
