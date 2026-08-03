# Consolidation audit: findings record (2026-08-02)

Read-only cross-cutting pass over the whole solution, weighted toward the work that landed
after 2026-07-26 (semantic layer / unstructured ingestion, documents + NLP sidecar,
element-fulltext-match, edge-type-vs-label), which the prior root `cleanup-report.md` never
saw. This report is the audit record; the decisions taken on it live in
[spec.md](spec.md), the execution slices in [plan.md](plan.md).

**Method.** Baseline verified green before judging: .NET build clean (0 warnings,
warnings-as-errors on), 1162 passed / 28 skipped / 0 failed; web-ui `tsc -b` clean, 589
vitest passed. No copy-paste detector installed; a 13-cluster deep read substituted
(namespace routing, analytics, index family, transactions, codegen, streaming, DTO
triplication, Studio client, plugin discovery, semantic ingestion, embeddings/vector,
best-practice sweep of post-Jul-26 code, docs/propagation). Every candidate finding was
re-opened at its cited lines by an independent adversarial verifier trying to refute it;
only survivors appear below. Finding IDs use the `CA-` prefix so they never collide with
the prior report's A-G scheme. Duplication was judged as duplicated *knowledge* (rule of
three, one shared reason to change, a nameable bug when it changes in one place only),
never duplicated characters; hot-path consolidations are flagged and benchmark-gated.

**Verdicts.** CONFIRMED = every site verified and a concrete bug-if-changed-in-one-place
holds. PLAUSIBLE = real drift but the bug is speculative or a cited detail was off.

## Disposition (2026-08-03, feature landed)

The one record of what happened to each finding; slice numbers are plan.md's, each landed
via its own `feature/<name>` branch and a Fable review gate. "fixed" = the duplication was
single-homed (or the bug repaired); "guarded" = a test now forces the drift to fail loudly;
"left" = deliberately not done, reason recorded.

| Finding | Disposition |
|---|---|
| CA-1 | fixed (slice 1): `IIndex.SupportsPointEqualityLookup`; spatial link-index 400, fulltext accepted |
| CA-2 | fixed (slice 2): `ProblemResults.StatusForFailureReason`; Embedding/Enqueue rollbacks surface mapped statuses |
| CA-3 | fixed (slice 2): `BoundIndexContract.FindConflictForIndex` |
| CA-4 | fixed (slice 3): `CodeGenerationHelper.FragmentCompileEnvironment` + parity test |
| CA-5 | fixed (slice 6): `VectorIndex.Classify`; benchmark before/after showed no regression (bytes/call 0 both sides) |
| CA-6 | fixed (slice 5): `AnalyticsAdjacency.VisitByDirection`/`CountByDirection`; benchmark no regression |
| CA-7 | fixed (slice 2): `EmbeddingProviderProblem.Map` |
| CA-8 | fixed (slice 2): `NamespaceProblems.NotFound` |
| CA-9 | fixed (slice 7): `PluginRegistry.EntriesForContract` |
| CA-10 | fixed (slice 7): `SidecarHttpClient` base (per-client timeout clamps stay local) |
| CA-11 | fixed (slice 7): bidirectional kind map + `TryParseKind` + round-trip test |
| CA-12 | fixed (slice 1): `VectorShapeConflict`/`FulltextShapeConflict`/`EntityShapeConflict` shared by read+enforce |
| CA-13 | fixed (slice 7): `PluginFactory.ContractInterface` + `AvailableBuiltInNames` |
| CA-14 | guarded (slice 4): `McpWriteDtoParityTest` (effective JSON names, REST vs MCP) |
| CA-15 | fixed (slice 8): 11 logger calls to message templates (exception `String.Format`s stay) |
| CA-16 | fixed (slice 4): `_logger ??=` in the three legacy `Load`s + OpenIndex-path regression test |
| CA-17 | fixed (slice 8): `LinksCreated`/`BuildSummary` removed everywhere incl. Studio and the MCP tool's reader (a fifth site the plan had not enumerated); snapshot regenerated |
| CA-18 | fixed (slice 8): mcp-server.md row corrected; unstructured-ingestion.md reduced to a pointer |
| CA-19 | fixed (slice 8): one merged `<summary>`, change-feed rationale kept |
| CA-20 | fixed (slice 8): `Property.cs` + registration deleted |
| CA-21 | guarded (slice 4): round-trip derives from `AllowedLiteralTypes.AllowedNames` |
| CA-22 | guarded (slice 4): reflection completeness sweep over every route-bearing endpoint export |
| CA-23 | fixed (slice 5): `BudgetGuard.IsExhaustedAt` (TriangleCounting's bare per-vertex check stays) |
| CA-24 | fixed (slice 3): `FormatCompileErrors(diagnostics, header)`; `/path` body errors-only (the planned `errorsOnly` flag dropped as a dead argument) |
| CA-25 | left: per-site result branches legitimately differ; the fragile datum (route key) is already one constant |
| CA-26 | left, own follow-up feature (`feature/nullable-everywhere`) |
| CA-27 | left, own follow-up feature (`feature/webui-lint-gate`) |

## 1. Overall assessment

This is a healthy, unusually disciplined codebase. The convention gates are real and are
holding: no `Console.Write*`, no `DateTime.Now`, no blocking `WaitUntilFinished()` on
request threads, no `#pragma`/`#nullable disable`/`GlobalSuppressions` anywhere in product
code, exact package pins, MIT headers everywhere. The hot and hard-won single-homes are
intact: `VectorMath.Score` is the one distance primitive for both kNN and in-traversal
scoring; `AnalyticsAdjacency` really did consolidate the six per-algorithm induced-subgraph
walks; `AGraphElementModel.TryGetEmbedding` is the one coupling point to the embedding
layout; `DurableFileIo`, `ProblemResults`, `JsonlGraphFormat`, and `IndexHelper.CheckObject`
are clean single homes. The prior audit's fixes stuck; no regression of E1/E3/E4/E5 was
found. Several starting guesses came back "leave it alone", verified: streaming (export vs
SSE are different knowledge, correctly separate), the DTO value-coercion spread (deliberate
ingest/egress/interchange split with cross-pointer comments and a culture test), and the
hand-written Studio TS client (an OpenAPI-generated client would be a net loss and is a
conscious design choice).

The drift that exists is almost entirely the predictable residue of fast sequential feature
delivery: a mapping or predicate that a feature author re-expressed locally instead of
reaching for an existing home, often with a comment that *claims* a single home the code no
longer honors. `BoundIndexContract` says "the embedding endpoints and ingestion both check
through here" while two query paths bypass it; `EmbeddingController.ProviderProblem` calls
itself "the single home" while two other sites re-map the same faults; the delegate
validator's whole job is to predict what `/path` and `/subgraph` accept, yet the three
compile environments are maintained by hand with no parity test. One finding is not
consolidation at all but a live correctness bug in the newest feature (CA-1). None of the
drift is deep or architectural, none requires reworking a hot loop, and the total confirmed
set is small relative to 86 shipped features. The right response is a handful of small,
tightly-scoped single-homing slices, not a refactor.

## 2. Findings, ranked by value / risk

### CA-1: semantic-layer "dictionary family" predicate contradicts the canonical capability definition (live bug) - CONFIRMED

- **Concept:** "which index types support point-equality key lookup." The semantic layer
  encodes it as the negative test `index is IVectorIndex || index is IFulltextIndex`
  (equality-capable == not-vector-and-not-fulltext) at five sites, but the repo's canonical
  definition is `IndexCapabilities.Describe` = not-vector AND not-spatial (fulltext and
  range ARE equality-capable; spatial is not).
- **Sites:** [DocumentIngestionService.cs:433](../../../fallen-8-core-apiApp/Ingestion/DocumentIngestionService.cs),
  :669, :837, :902, :932; canonical home
  [IndexCapabilities.cs:50-78](../../../fallen-8-core-apiApp/Helper/IndexCapabilities.cs) /
  [IndexDescriptionREST.cs:82-85](../../../fallen-8-core-apiApp/Controllers/Model/IndexDescriptionREST.cs).
- **Live bug (verified):** a spatial R-Tree index named in a link allowlist passes
  `ValidateLinkRequest` (:902, whose own message promises "exact-equality lookups"), then
  `ResolveLinks` (:932) probes it via `RTree.TryGetValue`, whose `CheckObject` cast of a
  string token to `IGeometry` fails and returns false, so the request silently yields zero
  links instead of the 400 the gate advertises. `GET /document/binding` and the ingest gate
  already disagree with `/status` for the same index.
- **Why it happened:** the semantic layer landed after the index-capabilities work; its
  author wrote a local type-test rather than consuming `IndexCapabilities`.
- **Single home:** an engine-side capability on `IIndex`; `IndexCapabilities.Describe` and
  the ingestion predicate both derive from it.
- **Blast radius:** engine index contract, REST `/document/binding` + ingest gate, MCP
  `f8_documents` bind/ingest, Studio Knowledge/State panel. Risk low, effort low.
  **Behavior change** (spatial-in-allowlist becomes 400): bug fix, test-first, own slice.

### CA-2: `TransactionFailureReason` to HTTP status re-switched in five controllers - CONFIRMED

- **Concept:** the reason-to-status contract (InvalidInput 400, NotFound 404,
  QuotaExceeded/Conflict 409, default 500) after a waited-on write rolls back.
- **Sites:** [GraphController.cs:248](../../../fallen-8-core-apiApp/Controllers/GraphController.cs)
  (canonical `RolledBackResult`),
  [SubGraphController.cs:469](../../../fallen-8-core-apiApp/Controllers/SubGraphController.cs),
  [StoredQueriesController.cs:358](../../../fallen-8-core-apiApp/Controllers/StoredQueriesController.cs),
  [PluginsController.cs:550](../../../fallen-8-core-apiApp/Controllers/PluginsController.cs),
  [BulkController.cs:481](../../../fallen-8-core-apiApp/Controllers/BulkController.cs)
  (deliberate NotFound-to-400 override for per-row batch semantics); plus two sites that
  discard the reason and always 500:
  [EmbeddingController.cs:202/296](../../../fallen-8-core-apiApp/Controllers/EmbeddingController.cs)
  and [DocumentIngestionService.cs:1424](../../../fallen-8-core-apiApp/Ingestion/DocumentIngestionService.cs).
- **Bug-if-split:** re-deciding a reason (e.g. QuotaExceeded to 429) or adding an enum
  member (the enum comment invites growth) requires editing every switch; a miss makes the
  same engine condition return different statuses per endpoint, and a new reason silently
  falls through to 500. The two always-500 sites already lose an advertised 404/409 on a
  TOCTOU rollback.
- **Why:** each controller added its own mapping as features landed; `api-error-envelope`
  (now done) explicitly unified body *shape* and left status mapping duplicated, so this is
  genuinely un-owned.
- **Single home:** `TransactionFailureReason.ToHttpStatus()` (engine Transaction namespace)
  or `ProblemResults.FromFailureReason(reason, detail)`; each controller keeps its tailored
  message. `TransactionFailureReasonTest` already pins the contract. Risk low, effort small.

### CA-3: FR-8 provider/index consistency check triplicated; documented "one home" bypassed by both query paths - CONFIRMED

- **Concept:** provider dimension must equal the index's, and a declared index model
  identity must equal the provider stamp, else 409.
- **Sites:** [BoundIndexContract.cs:43-70](../../../fallen-8-core-apiApp/Helper/BoundIndexContract.cs)
  (the self-described "one home"; its only callers are the write paths); byte-identical
  re-implementations at
  [EmbeddingController.cs:364-377](../../../fallen-8-core-apiApp/Controllers/EmbeddingController.cs)
  (`POST /embedding/search`) and
  [DocumentSearchService.cs:238-251](../../../fallen-8-core-apiApp/Ingestion/DocumentSearchService.cs)
  (`/document/search` dense side).
- **Bug-if-split:** relaxing the model match or changing the 409 in the "one home" is
  picked up on write but not on query, so the same index+provider is accepted for a write
  yet rejected for a query.
- **Single home:** add `FindConflictForIndex(index, id, provider)` to `BoundIndexContract`;
  call it from the two query sites and from the loop body of the by-name `FindConflict`,
  which makes the doc claim true again. Risk low, effort low-med.

### CA-4: delegate validator hand-mirrors the two real compile environments, no parity test - CONFIRMED

- **Concept:** the ambient compile environment for a user fragment (injected using set,
  wrapper namespace, `(TraversalContext context)` signature).
- **Sites:** generators
  [CodeGenerationHelper.cs:229](../../../fallen-8-core-apiApp/Helper/CodeGenerationHelper.cs)
  (path) and :801 (subgraph); the mirror
  [DelegateValidationHelper.cs:181](../../../fallen-8-core-apiApp/Helper/DelegateValidationHelper.cs),
  whose sole job is to forecast what those two accept.
- **Bug-if-split:** add a namespace to the generators and forget the validator (git shows
  the `Index.Vector` using was pasted into both by hand for element-embeddings, commit
  795eb32) and the Studio editor / `POST /delegates/validate` reports a fragment invalid
  (CS0246) that `/path` and `/subgraph` compile and run fine, or the reverse. No test
  exercises a `context`- or `Index.Vector`-using fragment, so the drift is silent.
- **Single home:** a shared usings/namespace/signature definition in `CodeGenerationHelper`
  that `BuildValidationSource` consumes (references are already single-sourced via
  `GlobalReferences`), plus a parity test. Risk low, effort low.

### CA-5: vector "rankability" predicate composed at four sites (writer projection must equal load-rebuild) - CONFIRMED, HOT PATH

- **Concept:** a vector can rank iff `length == Dimension && all-finite && !(Cosine &&
  zero-norm)`.
- **Sites:** [Fallen8.Embeddings.cs:68](../../../fallen-8-core/Fallen8.Embeddings.cs)
  (live writer projection),
  [VectorIndex.cs:200](../../../fallen-8-core/Index/Vector/VectorIndex.cs)
  (`RebuildProjection` skip test), VectorIndex.cs:276-292 (three guarded returns),
  [GraphController.Index.cs:136-147](../../../fallen-8-core-apiApp/Controllers/GraphController.Index.cs)
  (validate-then-400). The atomic helpers `HasNonFiniteComponent`/`IsZeroNorm` are already
  shared; only the composition is duplicated.
- **Bug-if-split:** `Fallen8.Embeddings.cs` explicitly promises the live projection equals
  a load-rebuild; that holds only because sites 1 and 2 hand-mirror the predicate. A
  rankability change in one silently changes bound-index membership across a restart, so
  kNN / semantic answers differ before vs after a reload with no error.
- **Single home:** `VectorRankability Classify(ReadOnlySpan<float>, dimension, metric)`
  static on `VectorIndex`; O(d) over a span, inlinable, no dispatch/allocation. Sites 1-2
  test `== Ok`; sites 3-4 switch on the reason for their distinct messages. Benchmark-gated
  (writer-thread projection). Risk med, effort low.

### CA-6: Direction-to-adjacency mapping hand-copied in four analytics blocks - CONFIRMED, HOT PATH

- **Concept:** mapping a `Direction` to which raw adjacencies to walk (out and/or in) and
  the paired `neighborIsTarget` flag.
- **Sites:** [PageRankAlgorithm.cs:104-111 and :151-158](../../../fallen-8-core/Algorithms/Analytics/PageRankAlgorithm.cs),
  [DegreeCentralityAlgorithm.cs:74-82](../../../fallen-8-core/Algorithms/Analytics/DegreeCentralityAlgorithm.cs),
  [LabelPropagationAlgorithm.cs:95-102](../../../fallen-8-core/Algorithms/Analytics/LabelPropagationAlgorithm.cs).
  TriangleCount/WCC deliberately ignore direction and stay excluded.
- **Bug-if-split:** PageRank holds two of the four blocks (degree divisor and rank push)
  that must stay in lockstep; a slip computes the divisor over a different neighbour set
  than the push and corrupts ranks with no compile error.
- **Single home:** `VisitByDirection`/`CountByDirection` on `AnalyticsAdjacency` (whose
  stated job this already is), keeping the identical monomorphized ref-struct-visitor
  pattern (no delegate, no allocation). Benchmark-gated. Risk med, effort low-med.

### CA-7: embedding-provider fault to (status, title) mapping at three sites; "single home" comment is false - CONFIRMED

- **Concept:** `EmbeddingProviderUnavailableException` to 503,
  `EmbeddingProviderOutputException` to 502.
- **Sites:** [EmbeddingController.cs:76-81](../../../fallen-8-core-apiApp/Controllers/EmbeddingController.cs)
  (`ProviderProblem`, doc-commented as "the single home"),
  [SemanticTraversalHelper.cs:111-118](../../../fallen-8-core-apiApp/Helper/SemanticTraversalHelper.cs)
  (drives `/path`, `/subgraph`),
  [DocumentSearchService.cs:259-268](../../../fallen-8-core-apiApp/Ingestion/DocumentSearchService.cs).
  The intended mapping is also asserted in the exception XML docs
  ([EmbeddingModelIdentity.cs:80/91](../../../fallen-8-core-apiApp/Embedding/EmbeddingModelIdentity.cs)).
- **Bug-if-split:** adding a third fault type or changing the 502 title in the "single
  home" leaves `/path`, `/subgraph`, `/document/search` emitting the old contract for the
  same provider failure.
- **Single home:** `EmbeddingProviderProblem.Map(ex)` returning (status, title);
  `ActionResult` callers wrap in `ProblemResults.Create`, `DocumentSearchService` wraps in
  `SearchOutcome`. `ChatController.ProviderProblem` is a separate concept, excluded.
  Risk low, effort low.

### CA-8: the "namespace not found" 404 body hand-built at three sites - CONFIRMED

- **Concept:** the 404 problem+json (title, detail wording, and the Studio-facing
  `namespace` extension member) for addressing a namespace that does not exist.
- **Sites:** [NamespaceValidationFilter.cs:55](../../../fallen-8-core-apiApp/Namespaces/NamespaceValidationFilter.cs),
  [UnknownNamespaceException.cs:57](../../../fallen-8-core-apiApp/Namespaces/UnknownNamespaceException.cs)
  (the race twin, whose comment promises "the same 404"),
  [NamespacesController.cs:253](../../../fallen-8-core-apiApp/Controllers/NamespacesController.cs).
  All call the generic `ProblemResults.Create` but compose the body independently.
- **Bug-if-split:** rewording the detail or renaming the `namespace` extension member in
  the discoverable management home diverges the data-plane and race 404s (breaking the
  documented "indistinguishable from arriving a moment later" guarantee) and can silently
  break Studio's recover-state, which keys on that member. `NamespaceEndpointTest` asserts
  title and the member but never the detail, so this diverges with zero test failure.
- **Single home:** `NamespaceProblems.NotFound(name)` in the Namespaces folder. Risk low,
  effort low.

### CA-9: analytics discovery predicate re-rolled inline (residual of prior D3 fix) - CONFIRMED

- **Concept:** "Compiled entries whose Contract == C, unioned with the reflection
  built-ins."
- **Sites:** canonical [PluginRegistry.cs:159](../../../fallen-8-core/Plugins/PluginRegistry.cs)
  (`NamesForContract`), consumed by
  [AdminController.cs:226](../../../fallen-8-core-apiApp/Controllers/AdminController.cs)
  (`/status`) and [SubGraphFactory.cs:144](../../../fallen-8-core/SubGraph/SubGraphFactory.cs)
  (the D3 fix); but
  [AnalyticsController.cs:113](../../../fallen-8-core-apiApp/Controllers/AnalyticsController.cs)
  re-implements the `Compiled && Contract==Analytics` filter inline because it also needs
  `Description`.
- **Bug-if-split:** a policy change to `NamesForContract` propagates to `/status` and
  `/subgraph` but silently misses `/analytics/algorithms`, so a Studio picker advertises a
  different set than `/status` reports; the exact class of bug the prior D3 fix closed,
  residual on the one surface D3 did not touch.
- **Single home:** a description-carrying sibling `EntriesForContract` on `PluginRegistry`.
  Risk low, effort med. Relation: overlaps prior-report D3.

### CA-10: sidecar HTTP client duplicated verbatim between docling and NLP clients - CONFIRMED

- **Concept:** trailing-slash endpoint normalization, a DNS-recycling `SocketsHttpHandler`
  (2 min lifetime), and a 30s-TTL cancellation-aware cached `GET /health` probe that
  deliberately does not cache a caller-cancelled request as "down".
- **Sites:** [DoclingClient.cs:43/57/81/234](../../../fallen-8-core-apiApp/Ingestion/DoclingClient.cs)
  vs [NlpClient.cs:91/97/108/163](../../../fallen-8-core-apiApp/Ingestion/NlpClient.cs). The
  ~40-line `IsReachableAsync` is byte-identical but for the log label; `NlpClient`'s own
  comment says it "mirrors DoclingClient".
- **Bug-if-split:** a TTL change or a follow-up fix to the cancel carve-out applied to one
  client leaves the other stale; both feed the same `/status` and `/statistics` surfaces
  with inconsistent freshness.
- **Single home:** a small `SidecarHttpClient` base (or `SidecarHealthProbe`) under
  `Ingestion/`; the two clients supply endpoint, timeout, log label. Two occurrences
  (rule-of-three borderline) but substantial verbatim copy; the embedding provider uses
  `Microsoft.Extensions.AI`/OllamaSharp and is correctly not a third instance. Risk low,
  effort med.

### CA-11: `ChangeEventKind` wire-name bijection lives in two switches - CONFIRMED

- **Concept:** the event-kind vocabulary shared by the SSE serializer and the `?kinds=`
  filter parser.
- **Sites:** parse [ChangeFeedController.cs:289-300](../../../fallen-8-core-apiApp/Controllers/ChangeFeedController.cs);
  format [ChangeEventREST.cs:135-148](../../../fallen-8-core-apiApp/Controllers/Model/ChangeEventREST.cs)
  (`KindName`).
- **Bug-if-split:** add or rename a kind on one side and the feed emits an `event:`/`kind:`
  name a client cannot filter on with `?kinds=`; an asymmetric contract no one-directional
  test catches. The one place in the change-feed feature that did not follow its own
  single-home pattern (`ResyncReason` consts; `JsonlGraphFormat` owning both directions).
- **Single home:** one bidirectional map, or `TryParseKind` next to `KindName`. Risk low,
  effort low.

### CA-12: read view (Role builders) vs enforce view (`Validate*`) recompute the bound-index shape contract - CONFIRMED

- **Sites:** vector [DocumentIngestionService.cs:617](../../../fallen-8-core-apiApp/Ingestion/DocumentIngestionService.cs)
  vs :725; fulltext :644 vs :782.
- **Bug-if-split:** tighten `ValidateVectorIndexShape` only (e.g. require metric match) and
  `GET /document/binding` / the Studio State panel still report Ready=true while
  bind/ingest 409s. The feature premise is "report state honestly", which depends on the
  two views agreeing.
- **Single home:** the Role builders decide; the enforce path throws off their result.
  Folds into CA-1's cleanup. Risk low, effort med.

### CA-13: `PluginContract` to CLR-interface mapping duplicated at four sites (shadow-collision risk) - CONFIRMED

- **Sites:** canonical [PluginCompiler.cs:108](../../../fallen-8-core-apiApp/Helper/PluginCompiler.cs)
  (`ResolveContractType`, private, apiApp); re-encoded at
  [PluginsController.cs:529](../../../fallen-8-core-apiApp/Controllers/PluginsController.cs)
  (`CollidesWithBuiltIn`); hardcoded at
  [AdminController.cs:210](../../../fallen-8-core-apiApp/Controllers/AdminController.cs) and
  [SubGraphFactory.cs:144](../../../fallen-8-core/SubGraph/SubGraphFactory.cs).
- **Bug-if-split:** adding a new contract forces the `ResolveContractType` update (else
  registration fails to compile), but nothing forces the parallel `CollidesWithBuiltIn`
  branch (miss = a user can register a plugin shadowing a new built-in name, violating
  plugin-registration spec 8.6) or the `/status` union (miss = invocable but not
  discoverable, a D3 recurrence).
- **Single home:** carry the interface `Type` on `PluginContract`, or a non-generic
  `PluginFactory.AvailableBuiltInNames(PluginContract)`. Low priority (closed enum, rare
  change). Risk low, effort med.

### CA-14: REST PUT-body shape and its MCP write-DTO mirror have no field-parity guard - CONFIRMED (propagation gap)

- **Sites:** REST [VertexSpecification.cs:55](../../../fallen-8-core-apiApp/Controllers/Model/VertexSpecification.cs)
  / [EdgeSpecification.cs:58](../../../fallen-8-core-apiApp/Controllers/Model/EdgeSpecification.cs)
  / [PropertySpecification.cs:43](../../../fallen-8-core-apiApp/Controllers/Model/PropertySpecification.cs);
  MCP [WriteDto.cs:34/44/54](../../../fallen-8-mcp/Bridge/Dto/WriteDto.cs); the only guards
  ([McpContractTest.cs](../../../fallen-8-unittest/McpContractTest.cs),
  McpRestCoverageTest) pin route+method, not field shape.
- **Bug-if-split:** a new field (as edge `Label` was during edge-type-vs-label) added
  REST-side and missed on the MCP DTO means every element created through `f8_mutate`
  silently loses that value while the contract tests stay green. Highest-risk variant: a
  REST `[JsonPropertyName]` rename, since MCP DTOs bind wire names via camelCase defaults.
- **Fix (a guard, not a merge; the deployable decoupling is deliberate):** a
  reflection-based parity test comparing effective JSON names (REST `[JsonPropertyName]`
  vs MCP camelCase-of-property). Risk med, effort low.

### Lower-value / mechanical

| ID | Verdict | Concept and sites | Fix | Risk/effort |
|---|---|---|---|---|
| CA-15 | CONFIRMED | `String.Format` prerendered into `_logger` at 7 index-family sites (IndexFactory.cs:115/144/335; ABucketIndex/RegExIndex/SingleValueIndex `Load`; RTree.cs:2006) + GraphController.Index.cs:228. Defeats structured logging; `VectorIndex` is the correct exemplar. Overlaps prior E4 (different sites; E4 never reached the index family). | Convert to message templates in place. | low/low |
| CA-16 | PLAUSIBLE | `Load()` dereferences a null `_logger` in the not-found branch of 3 legacy indices (ABucketIndex.cs:401, RegExIndex.cs:601, SingleValueIndex.cs:285); `VectorIndex.Load:886` already has the `??=` fix; `IndexFactory.OpenIndex` calls `Load` with no `Initialize`, and `LoadIndices`' per-index catch then drops the WHOLE index. Trigger narrow (stale ref to a removed element, or tampered sidecar) and untested. | Add the `??=` logger guard to the three `Load`s (mirror VectorIndex); add an OpenIndex-path load test. | med/low |
| CA-17 | CONFIRMED | Dead `BuildSummary` + always-null `DocumentSummaryREST.LinksCreated` left by the async-ingestion migration (DocumentIngestionService.cs:1628, DocumentModels.cs:205). | Delete the method; drop `LinksCreated` (snapshot churn). | low/low |
| CA-18 | CONFIRMED | `docs/src/content/docs/mcp-server.md:94` f8_documents row is stale: omits `binding`/`entities` (read) and `bind` (write) that the tool exposes; the two docs pages disagree today. | Extend the mcp-server.md row; reduce the unstructured-ingestion.md op list to a pointer. | low/low |
| CA-19 | CONFIRMED | Two consecutive `<summary>` blocks on `UpdateProperty` (DocumentIngestionService.cs:1398-1407); emitted XML doc is incoherent. | Merge into one summary (the first carries distinct change-feed rationale; do not just delete it). | low/low |
| CA-20 | PLAUSIBLE | Dead 2-field `Property.cs` DTO shadows the live `PropertySpecification`; registered in AppJsonContext:62, unused, absent from the OpenAPI snapshot. | Delete `Property.cs` + its AppJsonContext registration. | low/low |
| CA-21 | PLAUSIBLE | `AllowedLiteralTypes` is the type-set home, but the bulk round-trip test iterates a hand list (`AllTypedValues`, BulkImportExportTest.cs:121/144), so a future IFormattable-only type could export but fail import with a green suite. | Make the test derive from `AllowedLiteralTypes.AllowedNames`. | low/low |
| CA-22 | PLAUSIBLE | `api-contract.test.ts` only asserts `recorded.length > 30`; ~17 newest-feature endpoints (semantic-layer, unstructured-ingestion, namespaces, instance-config) are not contract-pinned anywhere. (The plugin cluster IS pinned by `plugin-endpoints.test.ts`.) | Force each exported route-bearing endpoint to be recorded, or add the missing calls. | low/low |
| CA-23 | PLAUSIBLE | Budget-check mask idiom `(i & (BudgetCheckInterval-1))==0` at 8 analytics sites; the power-of-two invariant is undocumented and unguarded (GraphAnalyticsDefinition.cs:45). Not a classic drift bug (single shared constant). Hot path. | `BudgetGuard.IsExhaustedAt(counter)` with AggressiveInlining as the one documented home; leave if a benchmark shows anything. | low/low |
| CA-24 | PLAUSIBLE | Roslyn diagnostic-string rendering duplicated at 3 compile sites and already drifted (path variant leaks warnings, no header): CodeGenerationHelper.cs:165/776, PluginCompiler.cs:343 (which already has a local `FormatDiagnostics`). Cosmetic (400 body text). | Shared `FormatDiagnostics(diags, header, errorsOnly)`; align the path site to errors-only. | low/low |
| CA-25 | PLAUSIBLE, leave | Namespace resolution (route value; null means default; else TryGet) re-implemented at 5 pipeline sites (AddressedFallen8.cs:96 et al.). The fragile datum (route key) is already a single constant; each result branch legitimately differs. | **Not worth it.** Keep the pointer comments. | - |
| CA-26 | observed | `<Nullable>enable` only in fallen-8-mcp.csproj; engine + apiApp compile with NRT off. Verified no null-handling drift in new code; latent inconsistency. | Enable repo-wide as its own feature (decision taken; see spec). | high effort |
| CA-27 | observed | No TypeScript lint gate: fallen-8-web-ui has no ESLint/Biome config and no `lint` script; `tsc -b` is the only frontend static gate. | Add ESLint gate as its own small feature (decision taken; see spec). | low/low |

### Rejected (checked, verified NOT findings)

- **RemoveValue O(all-keys) in RegExIndex/SingleValueIndex:** a deliberate,
  spec-documented deferral (index-lifecycle 3.4 scopes exactly these two out). Not drift.
  One sliver: the blanket comment at Fallen8.Storage.cs:744-746 over-generalizes "each
  index's reverse map"; a one-line comment correction at most (folded into the hygiene
  slice).
- **OpenAPI-generated TS client for Studio:** a net loss. `endpoints.ts` and `client.ts`
  (waitForCompletion, namespace scope, x-ndjson, RFC7807 parsing, curated names imported by
  47 files) stay hand-written regardless, and codegen adds a build step the repo
  intentionally lacks. The answer to the codegen question is no. Leave it.
- **`GetAwaiter().GetResult()` in the ingestion startup sweep:** runs once at
  BackgroundService startup off any request thread, exactly the case the
  `WaitUntilFinished` gate documents as acceptable (same category as the allowlisted
  `DurabilityLifecycleService`). Not a violation.

## 3. What was checked and found healthy

Namespace dual-routing fully centralized in `NamespaceRouteConvention` (no controller
hand-writes a `/ns/{ns}` twin) with one `IsValidName` home. `VectorMath.Score` the sole
distance primitive (kNN and traversal bit-identical). `AnalyticsAdjacency` genuinely
consolidated the six induced-subgraph walks; per-algorithm math correctly separate and
monomorphized. Analytics top-K, partition summaries, write-back transaction construction,
and the run-status contract each single-homed. `ABucketIndex` cleanly bases
dictionary+range; `IndexHelper.CheckObject`, the `ReadResource/WriteResource` +
`CollisionException` idiom, and `CanPersist` consistent. Roslyn cache-keying correctly
*different* per path (each keys on its true dependency); metadata references
single-sourced. Streaming export vs SSE different knowledge, correctly separate, correct
`RequestAborted` propagation in both. DTO value-coercion spread deliberate
(ingest/egress/interchange/MCP) with cross-pointer comments and a culture test.
edge-type-vs-label disciplined: one canonical graph-model explanation, pointers elsewhere,
engine/REST/MCP consistent. Ingestion pipeline reuses the transaction builders and awaits
`Completion` (no re-implemented writes, no banned blocking). Engine-to-REST-to-MCP coverage
substantively complete for the newest features; deferral reasons still hold. Studio
error/toast surface and `endpoints.ts` wrappers well-factored. MCP tool count consistent
(11 tools, docs match). Suppression surface clean (no pragmas, no nullable-disable, no
GlobalSuppressions).
