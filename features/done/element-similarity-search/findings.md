# Element similarity search: verified findings

> **Status:** research record, no spec or plan yet, nothing implemented. Written before
> `spec.md` so the investigation behind the scope decision is not repeated. Every claim below
> was read out of the code at the cited location.

## Why this exists

The request: for an ARXML-ingested graph, find *similar* graph elements rather than matching
substrings, starting from an element the user is already looking at.

The answer that came back from the sweep: almost all of it already ships. The embedding is
element state behind one accessor, a bound `VectorIndex` is a self-maintaining projection, the
ARXML provider already declares the summary template, the integrations runtime already renders
and posts it, and text-in kNN already works from REST, Studio and MCP. There is no engine work
in this feature, no new index structure and no ANN.

## Chosen scope

**Scope B**, decided 2026-08-24:

1. Chunk the summary embedding write (the blocker below).
2. Studio run form gains an "embed entity summaries" opt-in plus an optional embedding-name
   field, gated on the provider state, showing the descriptor's `entitySummaryTemplate`.
3. Create-index form prefills dimension and metric from `status.embedding`.
4. A "find similar" gesture on the element (Browser Embeddings tab, canvas Detail panel):
   client-side only, reads the element's own vector off already-fetched properties, carries the
   source label and kind, over-fetches `k+1`, drops the source element from the hits.
5. Empty-index warning on the Query screen, provider-off hints on Indexes and Integrations.
6. The missing MCP test for `f8_search` vector/semantic.
7. Docs: `integrations.md`, `vector-search.mdx`, `studio.md`, README key features.

**Embedding name: `default`**, as today. The consequence is accepted deliberately: the
document/knowledge layer binds its `documents` index to the same name
(`Fallen8IngestionOptions.cs`), so ARXML summaries and document chunks share a bound index and
answer the same searches. Revisit if a signal search is observed ranking a document chunk above
a signal.

Explicitly **out of scope**: a server-side element-as-query mode (so the gesture stays
Studio-only and invisible to agents), engine-level self-exclusion, a `reembedAll` backfill, a
score threshold, per-kind summary templates.

## The blocker: the shipped recipe cannot run at real size

`Fallen8RestTarget.EmbedSummariesAsync` (`fallen-8-integrations/Graph/Fallen8RestTarget.cs:456`)
builds one list of every summary and sends **one** POST to `/embedding/elements`. That route
rejects any batch over `Fallen8:Embedding:MaxBatchSize`
(`fallen-8-core-apiApp/Controllers/EmbeddingController.cs:247`), default **64**
(`Fallen8EmbeddingOptions.cs:87`, and 32 under `docker-compose.nahil.yml:67`), and caps the body
at 1 MiB with a compile-time `[RequestSizeLimit(1_048_576)]` (`EmbeddingController.cs:228`).

400 and 413 are not in the degrade-to-absent set `{403, 502, 503}`
(`Fallen8RestTarget.cs:493`), so the run does not degrade with a diagnostic: it throws
`GraphTargetException` and fails with `errorKind: "graph"`. Ordering makes it worse. The embed
write is `SnapshotApplier.cs:329`, reconciliation is `SnapshotApplier.cs:345`, so the graph
writes have already landed and reconciliation never runs.

The recorded production extract is many entities (`features/done/autosar-arxml/spec.md:190`).
The cap is 64. The recipe published in `docs/src/content/docs/integrations.md` has therefore
never been executed at real size; the spec's live "kilometer" acceptance check
(`autosar-arxml/spec.md:242`) was a PR-time manual check, explicitly not a CI gate, against a
fixture well under the cap.

Fix: an offset chunk loop mirroring the `SendBatchedAsync` / `WriteBatchSize` pattern already in
that same file (`Fallen8RestTarget.cs:327`, `:775`), summing the written count across chunks.

**Chunk size, settled by measurement (2026-08-25).** Shipped as 32 on the item cap alone, which was
wrong: the binding constraint is the 120 s client timeout, not the cap. A CPU-backed bge-m3 costs
~3.5 s per element here, so 32 elements is ~113 s. A real many-entity run died on its 86th chunk.
Now 16. The full-extract cost on this hardware is ~10.5 h of inference either way - that is a
deployment property, and the lever for it is a GPU or the Nahil backend rather than the chunk size.

## Studio cannot ask a run to embed

`buildJob` (`fallen-8-web-ui/src/screens/IntegrationsScreen.tsx:765`) returns only `providerId`,
`integrationInstanceId`, `namespace`, `settings`, `credentialValues`, `files`. `embedSummaries`
exists as an unused type member (`src/api/types.ts:1225`) with no writer anywhere in
`fallen-8-web-ui/src`; `embeddingName` is not on the type at all. The runtime requires both
halves (`fallen-8-integrations/Run/JobRunner.cs:230`) and the job half defaults false
(`IntegrationJob.cs:104`).

Consequence: the run report's "embedded" tile (`IntegrationsScreen.tsx:545`) is structurally
always 0 for a Studio-launched run, and it collapses three different states (field omitted,
never asked, asked and legitimately embedded nothing). No pinned test blocks the fix;
`tests/integrations-screen.test.tsx` asserts only `credentialValues`.

## Element-as-query exists nowhere

No route, tool or screen accepts an element id as the kNN query: `/embedding/search` takes text
(`Controllers/Model/EmbeddingREST.cs:130`), `/scan/index/vector` takes `Single[]`
(`Controllers/Model/VectorIndexREST.cs:88`), the engine primitive takes a query vector
(`fallen-8-core/Index/Vector/IVectorIndex.cs:72`), and neither `fallen-8-mcp/Tools/SearchTool.cs`
nor `QueryScreen.tsx:586` offers another source.

The vector is in the browser already but `previewVector`
(`fallen-8-web-ui/src/lib/embeddingProperties.ts:61`) renders only 4 components, so it cannot
even be copied out. There is no self-exclusion anywhere in the stack: the only filter object is
`VectorSearchConstraint{Kind,Label}` (`fallen-8-core/Index/Vector/VectorSearchResult.cs:83`), so
a similarity query returns the source element at rank 1.

## Two more real defects found on the way

- **Create-index dimension default is wrong against the shipped provider.**
  `IndexesScreen.tsx:284` defaults dimension to `"384"` while the same component already holds
  `status.embedding` via `useStatus` at `:291`, and the compose provider is 1024
  (`docker-compose.yml:70`). Accepting the default yields an index that 409s every later
  text-in embed and search (`Helper/BoundIndexContract.cs:69`).
- **An empty or wrongly-bound vector index is indistinguishable from "nothing is similar".**
  `VectorSearchResultREST` carries no member count, and `TryNearestNeighbors` succeeds over a
  zero-length scan (`VectorIndex.cs:627`), so both handlers return 200 with an empty list. The
  member count does exist one screen away (`AdminController.cs:288` to
  `IndexesScreen.tsx:170`).

Order of operations is safe, at least: a bound index created after the vectors exist
materialises itself immediately (`VectorIndex.cs:174` `RebuildProjection`).

## Summary text quality, per kind

The template is `{kind} {arxml.name}, {arxml.descEn}, {arxml.descDe}, {arxml.unit}`
(`AutosarArxmlProvider.cs:130`). It yields embeddable prose for three of seven kinds.

| Kind | Rendered text | Verdict |
| --- | --- | --- |
| `signal` | `signal Odo_ST3, Odometer (...), Kilometerstand (...), km` | rich; the reader denormalises the unit two hops down (`ArxmlReader.cs:633`) and reads DESC (`:532`) |
| `system-signal` | both descriptions, never a unit | usable |
| `pdu` | described only when the file carries DESC | usable |
| `compu-method` | `compu-method CM_TotalDistance, km` | weak: a unit and an opaque id, `Describe` never called (`ArxmlReader.cs:570`) |
| `network`, `ecu`, `frame` | `ecu ALPHA_CTRL`, `frame FRM_AlphaMain` | near-useless; `Describe` never called (`:280`, `:339`, `:461`), no unit reaches them |

Two consequences bind the UI. Every similarity query needs a label constraint, and the
"find similar" gesture must inherit the source element's label rather than searching the whole
index, or a third of the corpus is name-shaped noise. And the template omits `arxml.baseType`,
`arxml.length` and `arxml.initValue`, so "find other 32-bit unsigned counters like this" is not
expressible; there is no per-kind template and no job-level override
(`JobRunner.cs:230` takes the provider's single string).

Changing the template string is not free: it is pinned by
`fallen-8-unittest/IntegrationsBlueprintTest.cs:1439` plus a regex forbidding a literal word
adjacent to a hole (`:1444`), and by the provider-descriptor snapshot.

## Re-running does not backfill

`summaryDirty` is populated only for property-changed and newly created entities
(`SnapshotApplier.cs:270`, `:294`), and the zero-mutation invariant pins it
(`Conformance/ConformanceVerifier.cs:273`, `IntegrationsWritePathTest.cs:517`). So a graph
already imported without the flag cannot be embedded by re-running the integration. Documented
recovery for scope B is `HEAD /ns/<name>/tabularasa` on that namespace then re-run - the route is
HEAD-only (`AdminController.cs:859`; the pinned OpenAPI snapshot lists only `head`) - said plainly
in the new checkbox's help text. Clearing drops index definitions too, so the bound vector index
has to be recreated afterwards.

## Gate impact

- **OpenAPI snapshot**: untouched by scope B. No new route, no changed DTO.
- **Provider-descriptor snapshot**: untouched. `embedSummaries` and `embeddingName` are JOB
  fields (`IntegrationJob.cs:104`, `:112`), not descriptor settings, and
  `entitySummaryTemplate` already exists and is already in the field allowlist
  (`ProviderDescriptorSnapshotTest.cs:72`).
- **MCP coverage**: no new REST op, so no new deferral decision. Trap recorded for later: the
  deferral rule at `McpRestCoverageTest.cs:106` matches any op containing `/embedding/` and
  `:80` matches the whole `/index` family, so a future new route in either family is silently
  auto-deferred under a stale reason and the suite stays green.
- **Studio API-contract sweep**: two new fields on `IntegrationJobRequest` need no
  `ENDPOINT_CALLS` entry (`submitIntegrationJob` is already registered). If a
  `/embedding/elements` client is ever exported from `endpoints.ts` it must be registered or
  excluded with a reason, and note `endpoints.ts:433` currently calls bulk embedding
  "deliberately curl territory", so that would be a conscious reversal.
- **Screenshots**: the Integrations screen gains a checkbox, so recapture
  `docs/src/assets/images/screen-integrations.png`. The find-similar button affects the
  Browser/canvas and semantic-search captures too.
- **Browser probe**: not implicated, scope B touches no engine file.
- **Conformance / write-path tests**: chunking changes the number of write calls the in-memory
  target records (`InMemoryGraphTarget.cs:458` counts `embedSummaries` as a write), so
  `IntegrationsWritePathTest.cs:500` needs checking. The zero-mutation invariant must still
  hold.
- **Spec contradiction to fix**: `features/done/autosar-arxml/spec.md:204` declares semantic
  search "a first-class requirement, not a nice-to-have" while its impact table at `:285`
  records "F8 Studio | zero code change". The Studio gap is the direct consequence.

## Open questions for the spec

1. **Chunk size for the summary write.** Hardcode 64 in the integrations runtime, or publish the
   apiApp's `Fallen8:Embedding:MaxBatchSize` on `GET /status` and read it the way the runtime
   already reads dimension and metric? The runtime's own rule (`Fallen8RestTarget.cs:602`) is
   that such numbers belong to the target, but `/status` publishes no batch cap today, so adding
   one is a contract change with an OpenAPI-snapshot cost. A conservative hardcoded 64 is
   correct against every shipped default and keeps each body far under 1 MiB.
2. **The 1 MiB `[RequestSizeLimit]`** on `/embedding/elements` is a compile-time constant, unlike
   `BulkController` which raises `IHttpMaxRequestBodySizeFeature` from options
   (`BulkController.cs:324`). With chunking it stops mattering. Leave it fixed, or make it
   option-driven for symmetry?
3. **Does the "find similar" gesture constrain on label only, or label and kind?** The summary
   text quality table argues for label. Kind is free.

## Related, deliberately separate

The multi-file ARXML question (a second extract under the same instance id reconciles the first
away; a different instance id yields two disjoint subgraphs that identity can never bridge,
because `arxml-path` is instance-scoped and similarity is banned as an identity signal) is its
own feature-sized question about multi-file ingest in one run. It is not part of this feature.
Nothing here changes identity, and nothing here should.
