# Consolidation audit: spec

Status: Approved (2026-08-03). Findings record: [report.md](report.md). Slices:
[plan.md](plan.md).

## 1. What this feature is

A cross-cutting consolidation of the codebase after the 2026-06/08 feature wave: give each
piece of duplicated *knowledge* found by the audit exactly one home, close the guard gaps
that let the layers drift silently, and fix the one live bug the audit surfaced. Goal is a
best-of-breed codebase, not a smaller one. Pure consolidation slices preserve observable
behavior exactly; the deliberate behavior changes are enumerated in section 4 and nowhere
else.

Guiding principle (owner's call, 2026-08-03): **no external consumers depend on existing
state, so prefer the correct contract over the preserved quirk.**

## 2. Scope

In scope (finding IDs from [report.md](report.md)):

- **Correctness:** CA-1 (equality-capability predicate), CA-12 (read vs enforce view),
  CA-16 (null logger in index `Load`).
- **Single-homing:** CA-2 (failure-reason to HTTP status), CA-3 (FR-8 provider/index
  check), CA-4 (fragment compile environment), CA-5 (vector rankability, hot),
  CA-6 (analytics direction mapping, hot), CA-7 (provider-fault mapping),
  CA-8 (namespace-404 body), CA-9 (analytics discovery predicate), CA-10 (sidecar HTTP
  client), CA-11 (change-feed kind bijection), CA-13 (contract-to-interface mapping),
  CA-23 (budget-check idiom, hot), CA-24 (Roslyn diagnostics rendering).
- **Guards:** CA-14 (REST/MCP write-DTO field parity test), CA-21 (allow-list-derived bulk
  round-trip test), CA-22 (Studio contract-test coverage).
- **Hygiene:** CA-15 (index-family log templates), CA-17 (dead `BuildSummary` +
  `LinksCreated`), CA-18 (mcp-server.md f8_documents row), CA-19 (double `<summary>`),
  CA-20 (dead `Property.cs`), the over-general comment at `Fallen8.Storage.cs:744`.

Out of scope, spun off as their own features (records to be created when they start):

- **webui-lint-gate** (CA-27): ESLint flat config + typescript-eslint for
  `fallen-8-web-ui`, `lint` script, CI wiring.
- **nullable-everywhere** (CA-26): `<Nullable>enable` for the apiApp, then the engine
  (fallen-8-mcp already has it). The largest item; sequenced after this feature.

Explicit non-goals (audit verdicts, not deferrals):

- No OpenAPI-generated TypeScript client for Studio; the hand-curated client is a
  deliberate design and a generator would be a net loss.
- No reverse-map `RemoveValue` for RegExIndex/SingleValueIndex; that is index-lifecycle
  3.4's documented deferral, not drift.
- No change to the ingestion startup sweep's blocking wait (matches the documented
  startup/shutdown exception of the WaitUntilFinished gate).
- No merging of the deliberate hot-path or cross-deployable duplications catalogued as
  healthy in the report (streaming writers, value-coercion homes, MCP DTO decoupling,
  namespace resolution branches / CA-25).

## 3. Decisions taken (2026-08-03)

1. **CA-1 home is engine-side.** The equality-capability lives on the index contract in
   `fallen-8-core` (the layer that owns it); `IndexCapabilities.Describe` (apiApp) and the
   semantic layer's dictionary-family checks both derive from it. No third copy.
2. **Hot-path consolidations (CA-5, CA-6, CA-23) proceed, benchmark-gated.** They are
   perf-neutral by construction (monomorphized ref-struct visitors, span-based inlinable
   statics); the measurement is a safety net. Any regression: revert and document the
   duplication as deliberate.
3. **CA-17: drop `LinksCreated`.** Async ingestion means the 202 can never honestly carry
   a link count; an always-null field is a small lie in the contract. Snapshot regenerated.
4. **CA-26 nullable: yes, repo-wide, own feature, last.** apiApp first, then engine.
5. **CA-27 lint gate: yes, own small feature.**
6. **Rule-of-three borderlines:** do CA-10, CA-11, CA-14; leave CA-25 alone.
7. **Process:** no GitHub issues, no PRs; `feature/<name>` branch per slice, merged to
   `main` after the review gate. Spec/plan and the per-slice review gate run on Claude
   Fable; implementation runs on Claude Opus.

## 4. Deliberate behavior changes (exhaustive)

Everything not listed here is behavior-identical; if implementation discovers otherwise,
stop and surface it.

1. **CA-1 (slice 1):** two directions, both from aligning the semantic layer's link/entity
   gates to the canonical `IndexCapabilities` rule (now the engine-owned
   `IIndex.SupportsPointEqualityLookup`). (a) A spatial index named in a document-link
   allowlist (or bound as the entity dedup index) is rejected with 400/409 at validation
   time instead of silently producing zero links / silently failing dedup - the reported
   bug. (b) A fulltext index is now *accepted* as an equality-capable link/entity index
   (it was wrongly rejected before); its `AddOrUpdate`/`TryGetValue` are exact string-key
   operations, so this is functionally sound and matches what `/status` reports. Read view
   (`GET /document/binding`), the 428 gate, and enforcement now agree with `/status` for
   every index. The `/status` capability inventory output is unchanged (its lists were
   already correct; the engine property just becomes their source of truth).
2. **CA-2 (slice 2):** `EmbeddingController`'s post-precheck rollback and
   `DocumentIngestionService.Enqueue` route through the shared reason-to-status mapper, so
   a TOCTOU rollback surfaces the advertised 404/409 instead of a blanket 500.
   (`BulkController`'s per-row NotFound-to-400 stays, as a documented override.)
3. **CA-17 (slice 8):** `DocumentSummaryREST.LinksCreated` is removed from the DTO and the
   OpenAPI document.
4. **CA-24 (slice 3):** the `/path` compile-failure body stops leaking warning/info
   diagnostics and gains the same errors-only rendering with header the subgraph and
   plugin paths already use.

## 5. Impact on existing features (mandatory sweep)

- **Engine (`fallen-8-core`):** new capability member on the index contract (CA-1); new
  statics on `AnalyticsAdjacency`, `VectorIndex`, `BudgetGuard`, `PluginRegistry`,
  `TransactionFailureReason` extension. Engine package version bump per the csproj
  versioning note if the public surface changes (the IIndex capability is a public-surface
  addition: minor bump, documented in the csproj comment block).
- **REST contract / OpenAPI snapshot:** slices 1 (400 on invalid link allowlist), 2
  (Embedding/Document rollback statuses), 8 (drop `LinksCreated`) touch response docs;
  regenerate via `pwsh scripts/update-openapi-snapshot.ps1` in those slices and review the
  diff. Other slices: no snapshot change expected; a diff appearing is a stop signal.
- **MCP:** no new REST operations, so no new tools and no McpRestCoverageTest deferrals.
  `f8_documents` bind/ingest inherit CA-1's 400. CA-14 adds a parity test in
  fallen-8-unittest touching the MCP write DTOs (guard only, no shape change).
- **Studio (`fallen-8-web-ui`):** no client changes for CA-1 (the client does not gate
  link-index candidates). CA-17 DOES touch the client, correcting the audit report's claim:
  `linksCreated` is declared at types.ts:184 and rendered by KnowledgeScreen.tsx:143
  (always null today, so the render is dead) - slice 8 removes both alongside the DTO
  field (found at the slice 1 gate, 2026-08-03). CA-22 extends the contract test only.
- **Tests:** existing pins that must keep passing unchanged: TransactionFailureReasonTest,
  VectorIndexTest/BoundVectorIndexTest, GraphAnalytics tests, NamespaceEndpointTest,
  ChangeFeed tests, EmbeddingProviderTest, DocumentSearchEndpointTest,
  JsonSourceGenParityTest (update for the dropped DTO field), McpContractTest,
  McpRestCoverageTest. New tests per plan.md.
- **Docs site:** CA-18 edits `mcp-server.md` + `unstructured-ingestion.md`;
  `unstructured-ingestion.md` also gains the corrected link-allowlist rule (CA-1). Docs
  build must stay green (`npm --prefix docs ci && npm --prefix docs run build`).
- **Feature READMEs:** the semantic-layer feature record gets a one-line pointer to CA-1's
  corrected capability rule; index-lifecycle is untouched (its deferral stands).
- **Architecture diagrams:** unaffected; no new channel or deployable.
- **NL-assist dataset/eval:** unaffected. No delegate-language surface change (CA-4
  changes how the environments are *maintained*, not what fragments are legal). No
  RETRAIN-LOG entry needed; if implementation ever changes what a fragment may reference,
  that becomes an entry.
- **Persisted state / save games:** unaffected; no serialization format or WAL change.
- **Prior cleanup-report.md:** untouched; this feature's record supersedes nothing there
  (fresh `CA-` ID space, overlaps noted per finding).

## 6. Acceptance

- Every slice: build clean (warnings-as-errors), full suite green, web-ui `tsc` + vitest
  green when touched, snapshot regenerated where section 5 says so and diff reviewed.
- Hot-path slices (5, 6): before/after measurement recorded in the slice's commit or the
  plan; regression = revert.
- The false "single home" claims called out in the report (BoundIndexContract,
  EmbeddingController.ProviderProblem, UnknownNamespaceException) are true statements when
  this feature lands.
- report.md findings each end in one of: fixed (slice N), guarded (slice N), or
  explicitly left (with the reason recorded here or in the report).
