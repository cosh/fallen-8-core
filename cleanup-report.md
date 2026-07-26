# Cleanup report: dead code, inconsistencies, and spec divergence

Investigation of `fallen-8-core` after ~3 weeks of heavy feature churn (572 commits, 2026-07-11 to 2026-07-26). This is a report only. Nothing has been changed.

## How this was produced (honesty note)

- **Baseline (verified directly by me):** `dotnet build fallen-8-core.sln` is clean, **0 warnings / 0 errors** (warnings-are-errors is on). `dotnet test` is green: **1034 passed, 28 skipped, 0 failed** (~51 s). The 28 skips are all `[Ignore]`-gated benchmarks and live-model / case-sensitive-FS smokes; each was opened and none hides a broken feature (all have a paired non-ignored correctness test).
- **Findings:** produced by a two-pass agent audit (one surveyor per area, then an independent adversarial verifier that re-opened the cited files, grepped every project, checked git blame/log, and tried to *refute* each finding). Every "confirmed" item below survived that refutation with a concrete file+line citation. I additionally re-opened and personally re-confirmed the highest-severity items (A1, A4, and the suppression sweep).
- **Confidence:** `confirmed` = the code was opened and the behaviour verified end to end (usually plus git). `suspected` = plausible but rests on a judgement I could not close alone (almost always: "is this deliberate public API / a reserved extension point?"). Those are questions, not delete-orders.
- **False positives ruled out** before calling anything dead: reflection/plugin discovery, runtime-compiled Roslyn fragments, DI, serialization/WAL round-trip, public library + REST surface, capability-flag gating, and test-only / cross-project usage.
- **Specs are not ground truth.** Where code and a `features/` spec disagree, git history was used to judge intent. Where the change looks deliberate, the proposed action is *update the stale spec*, never *revert the code*. 13 candidate findings were dropped as deliberate-and-correct (Section H).

## Count summary

| Section | Area | Confirmed | Suspected | Total |
|---|---|---|---|---|
| A | Behavioural issues hidden behind a clean surface (masquerade) | 6 | 0 | 6 |
| B | REST contract inconsistencies (status codes / endpoint shape) | 6 | 0 | 6 |
| C | Dead code (confirmed) | 13 | 0 | 13 |
| D | Dead-ish code needing your call (public API / extension points) | 4 | 5 | 9 |
| E | Cross-cutting inconsistencies | 5 | 0 | 5 |
| F | Spec vs code divergence (stale docs; deliberate code) | 15 | 0 | 15 |
| G | Test-quality & quality-gate gaps | 3 | 0 | 3 |
| **Total** | | **52** | **5** | **57** |
| H | Candidates verified as deliberate/correct and dropped | | | 13 |

Open questions are consolidated in Section I.

---

## A. Behavioural issues hidden behind a clean surface (highest priority)

These are places where a green build/tests and a "done" spec mask a real runtime defect. All confirmed.

### A1. `/path` swallows every runtime fault into an empty HTTP 200
- **Location:** [GraphController.Path.cs:262-268](fallen-8-core-apiApp/Controllers/GraphController.Path.cs#L262-L268) (`catch (Exception)` → `return result;` where `result` is the empty list from :129)
- **Category:** swallowed-exception / divergence
- **What & why:** The whole traversal is wrapped in a broad catch that only logs, then returns the empty `List<PathREST>` as 200. Only the compile-failure sub-case escapes early as 400 (:194). A filter/cost fragment that *compiles but throws at runtime* (e.g. `(double)v.GetProperty("age")` on a missing/string property → InvalidCast, or a divide-by-zero cost), or an empty `pathAlgorithmName` (ArgumentException at [Fallen8.cs:476](fallen-8-core/Fallen8.cs#L476)), is masked as "no path found." The path algorithms contain zero catch blocks, so the fault reaches this handler; the local catch also preempts the global `UseExceptionHandler`/ProblemDetails net. The sibling `/subgraph` returns 500 for the same fault class. This is the *same defect* `api-error-contract` reported as fixed (`review-2026-07-followups.md`): only the compile-failure leak was closed; spec E5 point 5 ("narrow catch → 500 ProblemDetails") was never implemented and is not listed as a deferral. **Also surfaces over MCP:** `f8_paths` relays the 200 and reports `count:0` / "no path (or an internal limit was hit)" to the agent, i.e. a faulted query is presented as an authoritative negative.
- **Confidence:** confirmed (I re-opened the file myself). No test pins the runtime-throw behaviour.
- **Proposed action:** fix — narrow the catch to return 500 ProblemDetails (or 400 for a user-fragment fault), matching `/subgraph`; add a throwing-fragment test.

### A2. `/path` documents a 404 for a missing source/target vertex it never returns
- **Location:** [GraphController.Path.cs:90](fallen-8-core-apiApp/Controllers/GraphController.Path.cs#L90) (`<response code="404">Source or target vertex not found…</response>`); no `TryGetVertex(from/to)` anywhere in the action
- **Category:** divergence (doc vs reachable behaviour)
- **What & why:** A nonexistent vertex id flows into the algorithm, which returns null → 200-empty (pinned by `PathTestEdgeCases.cs:99-113`). The only reachable 404 is the stored-query-name case. The missing-vertex→404 was consciously deferred (spec.md:30-34) but the `<response 404>` prose was never trimmed, so the OpenAPI contract advertises a status the code cannot emit.
- **Confidence:** confirmed.
- **Proposed action:** fix — either drop the vertex clause from the 404 doc (keep the stored-query case) or implement the deferred `TryGetVertex` checks. **Question Q1.**

### A3. `GetSubGraphContents` never got the E6 bounded-read clamp its spec pins
- **Location:** [SubGraphController.cs:373-374](fallen-8-core-apiApp/Controllers/SubGraphController.cs#L373-L374) (`GetAllEdges().Take(maxElements)` with no `Math.Clamp`)
- **Category:** silent-fallback / divergence
- **What & why:** `api-error-contract` E6 (spec.md:180-181, plan.md Phase 5) pins `Math.Clamp(maxElements, 0, MaxPageSize)` for **both** `GetGraph` and `GetSubGraphContents`; only `GetGraph` ([GraphController.GraphElement.cs:86](fallen-8-core-apiApp/Controllers/GraphController.GraphElement.cs#L86)) received it. git confirms the SubGraph `Take` is unchanged since the endpoint was created and the E6 test covers only `GetGraph`. An oversized `maxElements` (int.MaxValue) materializes + serializes the whole subgraph unbounded — the exact amplification the clamp was meant to bound. (The negative→empty-200 behaviour is the same on both endpoints and was a documented deferral, so that half is not unique.)
- **Confidence:** confirmed.
- **Proposed action:** fix — apply the same clamp.

### A4. `DelegateTransaction` reports `Durable=true` while never being WAL-logged
- **Location:** [DelegateTransaction.cs:47-53](fallen-8-core/Transaction/DelegateTransaction.cs#L47-L53); [WalTransactionCodec.cs:99](fallen-8-core/Persistency/WalTransactionCodec.cs#L99) (`default: return false`); [Fallen8.Persistence.cs](fallen-8-core/Fallen8.Persistence.cs) `LogCommittedTransaction` (`return true` without buffering)
- **Category:** silent-fallback
- **What & why:** A `DelegateTransaction` composes real graph mutations but is not recognised by the WAL codec, so with the WAL enabled and healthy it is never appended, yet `TransactionInformation.Durable` stays `true` (the `Durable=false` branch at `TransactionManager.cs:294` is skipped). This contradicts the `Durable` XML contract (documented as WAL-durability, false only on append-failure/degraded/suspended). A crash before the next snapshot silently loses these acknowledged mutations — codified by `PluginWriteTransactionsTest.cs:176` (`VertexCount==0` after WAL-only replay). **This is REST-reachable, not just a library hatch:** [AnalyticsWriteBack.cs:70](fallen-8-core-apiApp/Helper/AnalyticsWriteBack.cs#L70) routes analytics property write-back through `DelegateTransaction` (used by `AnalyticsController`). I confirmed both the codec `default` and the REST reachability directly.
- **Confidence:** confirmed.
- **Proposed action:** verify-with-user — report `Durable=false` for WAL-enabled delegate writes (matching the "committed in memory, not in the log" semantics), or amend the `Durable` contract to carve out snapshot-only delegate writes. **Question Q2.**

### A5. `IndexFactory.TryCreateIndex` swallows its exception with no log
- **Location:** [IndexFactory.cs:138-142](fallen-8-core/Index/IndexFactory.cs#L138-L142) (`catch (Exception) { index = null; return false; }`)
- **Category:** swallowed-exception
- **What & why:** `VectorIndex.Initialize` throws validation errors (bad dimension/metric/embeddingName) without logging; this catch discards them and returns a bare `false`, which the REST `CreateIndex` renders as `200 OK` body `false`. An operator whose index "just didn't get created" has no diagnostic trail — contradicting the sibling factories' consistent log-then-return-false discipline *and* the vector-index spec/plan's own "the failure is logged" claim. Also returns bare `false` on a write-lock collision, where `TryDeleteIndex` throws `CollisionException`. Operability gap, not data loss.
- **Confidence:** confirmed.
- **Proposed action:** fix — `LogError(ex, …)` before returning false; distinguish collision from real failure.

### A6. Batch vertex create: a post-append embedding-projection throw is not captured for rollback
- **Location:** [Fallen8.Storage.cs:276-285](fallen-8-core/Fallen8.Storage.cs#L276-L285) (`CreateVertices_internal`); [CreateVerticesTransaction.cs:65](fallen-8-core/Transaction/CreateVerticesTransaction.cs#L65)
- **Category:** correctness (atomicity edge case)
- **What & why:** `CreateVerticesTransaction` captures the created batch **only via the return value** at :65. If `ProjectAllEmbeddingsOf` throws after the atomic append (realistically only an OOM in the unguarded `GetIndicesSnapshot()` / `GetPropertyStoreForSerialization()`, since the per-index body is try/catch-guarded), the assignment never completes, `_verticesCreated` stays its empty initializer, and `Rollback` removes nothing — leaving the appended vertices visible with counters advanced under a `RolledBack` result (and absent from the WAL, so a reload diverges). This violates the `RolledBack ⇒ zero observable effect` contract. The **edge** path avoids this by populating a caller-owned `createdEdges` list *before* projection ([Fallen8.Storage.cs:396](fallen-8-core/Fallen8.Storage.cs#L396)). `id==index` is preserved, so no id-space corruption. Narrow trigger (OOM), so low severity, but a real asymmetry.
- **Confidence:** confirmed (narrow reachability).
- **Proposed action:** fix — mirror the edge path (pass a caller-owned list, `AddRange` before projection); add a fault-injection test.

---

## B. REST contract inconsistencies (documented status codes vs actual behaviour)

### B1. Index/service mutation endpoints return a bare `bool` → always HTTP 200, never the 400/404 they declare
- **Location:** [GraphController.Index.cs](fallen-8-core-apiApp/Controllers/GraphController.Index.cs) `CreateIndex` L181, `AddToIndex` L213, `RemoveKeyFromIndex` L254, `RemoveGraphElementFromIndex` L282, `DeleteIndex` L317; [AdminController.cs](fallen-8-core-apiApp/Controllers/AdminController.cs) `CreateService` L639
- **Category:** divergence / inconsistency
- **What & why:** Each returns `bool` (serialized as 200 with body `true`/`false`) and only `_logger.LogError` on a miss, so their `[ProducesResponseType(400)]`/`(404)` and `<response>` docs can never fire. The sibling `AddToVectorIndex` in the same file (and all Vertex/Edge/GraphElement/SubGraph endpoints) return `IActionResult` with real `NotFound`/`BadRequest`. A client cannot distinguish "not found" from "operation returned false." (Note: `AdminController.DeleteService` is honest — it declares only 200 — so it is *not* part of this group.)
- **Confidence:** confirmed.
- **Proposed action:** verify-with-user / consolidate — convert to `IActionResult` returning `NotFound()`/`BadRequest()` to match siblings and their own docs. **Question Q3.**

### B2. Two scan endpoints return raw types and emit 204-on-miss, never their declared 404
- **Location:** [GraphController.Scan.cs](fallen-8-core-apiApp/Controllers/GraphController.Scan.cs) `FulltextIndexScan` L215-230, `SpatialIndexScanSearchDistance` L331-364
- **Category:** inconsistency / divergence
- **What & why:** Both return raw DTO/`IEnumerable` (not `IActionResult`) and `return null` on a miss (unknown/wrong-type index, missing reference element), which the framework renders as **204 No Content** (pinned by `HostedRoutingSmokeTest`). They declare `[ProducesResponseType(404)]` that is therefore unreachable, and diverge from the four sibling scan endpoints (`VectorIndexScan` actually emits 404). (The surveyor's original "→500 / 200-with-null" wording was corrected by the verifier: `[ApiController]` binding turns a null body into 400, and a miss is 204.)
- **Confidence:** confirmed.
- **Proposed action:** verify-with-user — convert to `IActionResult` with `BadRequest`/`NotFound` matching the other scans, and fix the stale 404 annotation. **Question Q3.**

### B3. `AdminController.Load` does not null-guard its body → 500 where siblings return 400
- **Location:** [AdminController.cs:371-402](fallen-8-core-apiApp/Controllers/AdminController.cs#L371-L402) (dereferences `definition.StartServices` at L373)
- **Category:** inconsistency
- **What & why:** A JSON `null` body NREs to 500; `Save` uses `definition?.…` and every other write endpoint (Vertex/SubGraph/StoredQuery/Chat/Embedding) guards null bodies with a 400. A missing file also rolls back to 500, not the documented 400.
- **Confidence:** confirmed.
- **Proposed action:** fix — null-guard the body (return `BadRequest`) like the siblings.

### B4. `HEAD /trim` and `/tabularasa` are `void` actions but declare 204
- **Location:** [AdminController.cs](fallen-8-core-apiApp/Controllers/AdminController.cs) `Trim` L343, `TabulaRasa` L484 (both `void` + `[ProducesResponseType(204)]`)
- **Category:** divergence
- **What & why:** A `void` MVC action yields `EmptyResult`, leaving the default **200**, not the documented 204. No result filter maps void→204.
- **Confidence:** confirmed.
- **Proposed action:** verify-with-user — return `NoContent()` explicitly (or correct the docs to 200). **Question Q3.**

### B5. `GraphScan`'s `interestingLabel` filter is silently dropped
- **Location:** [Fallen8.Scan.cs:350-360](fallen-8-core/Fallen8.Scan.cs#L350-L360) (`FindElements(finder, literal, propertyId, interestingLabel=null)` calls the single-arg overload, defaulting `interestingLabel` to null)
- **Category:** divergence (latent)
- **What & why:** The overload accepts `interestingLabel` but never forwards it, so `CheckLabel(null)` yields an always-true predicate and the label filter is a no-op. Latent through the current REST surface (the `/scan/graph/property` endpoint passes 4 args, no label field) but fully reachable via the public engine/library API (`IFallen8Read`/`AFallen8`/`AddressedFallen8` all expose and forward it).
- **Confidence:** confirmed.
- **Proposed action:** question — forward `interestingLabel` to the seeker overload (fix the filter), or remove the dead parameter if label filtering on GraphScan is unwanted. **Question Q4.**

### B6. `InvokeFunction` returns 409 for a Failed-state function, but the spec says 404
- **Location:** [PluginsController.cs:306-310](fallen-8-core-apiApp/Controllers/PluginsController.cs#L306-L310)
- **Category:** divergence (deliberate)
- **What & why:** Spec 4.2/7 and plan Phase 3 say invoking an unknown *or Failed* function returns 404; the controller returns 404 only for unknown and **409 Conflict** for registered-but-Failed. git shows the 409 was introduced deliberately (commit b45a0c9) with its own `[ProducesResponseType(409)]` and is arguably more precise. No test exercises the Failed path.
- **Confidence:** confirmed (deliberate code).
- **Proposed action:** update-stale-spec (align spec 4.2/7 to 409).

---

## C. Dead code (confirmed)

| # | Location | What | Action |
|---|---|---|---|
| C1 | [appsettings.json:26](fallen-8-core-apiApp/appsettings.json#L26) `"PluginDirectory": null` | Binds to no property (`Fallen8SecurityOptions` has none); DLL-upload leftover, plugin-registration plan explicitly said to drop it | delete |
| C2 | [SerializationWriter.cs:37-38](fallen-8-core/Serializer/SerializationWriter.cs#L37-L38), [SerializationReader.cs:34](fallen-8-core/Serializer/SerializationReader.cs#L34) | `BinaryFormatter`/`FormatterAssemblyStyle` usings; the `OtherType` path migrated to `System.Text.Json`, no reference remains | delete |
| C3 | [BreadthFirstSearchSubgraphAlgorithm.cs:755-765](fallen-8-core/Algorithms/SubGraph/BreadthFirstSearchSubgraphAlgorithm.cs#L755-L765) | Private nested class `EdgeInfo` declared, never instantiated/referenced anywhere | delete |
| C4 | [EdgeModel.cs:29](fallen-8-core/Model/EdgeModel.cs#L29) | Stray `using System.Security.Cryptography.X509Certificates;` (no X509 symbol in file) | delete |
| C5 | [ScaleFreeNetwork.cs:47](fallen-8-core-apiApp/Controllers/Benchmark/ScaleFreeNetwork.cs#L47) | Field `_numberOfToBeTestedVertices` read only self-referentially in its own clamp; value never consumed | delete |
| C6 | [RTree.cs:431-435](fallen-8-core/Index/Spatial/Implementation/RTree/RTree.cs#L431-L435), [ARTreeContainer.cs:46](fallen-8-core/Index/Spatial/Implementation/SpatialContainer/ARTreeContainer.cs#L46) | Commented-out for-loops and a commented `Level` property in the ported R-Tree | delete |
| C7 | [SchemaBuilder.cs:68-71](fallen-8-mcp/Tools/SchemaBuilder.cs#L68-L71), [McpTool.cs:91-99](fallen-8-mcp/Tools/McpTool.cs#L91-L99) | `SchemaBuilder.Bool` + `ToolArgs.GetBool` have zero callers (no MCP tool declares a bool arg) | verify (keep for DSL symmetry?) |
| C8 | [SubGraphFactory.cs:54,90,473-481,538](fallen-8-core/SubGraph/SubGraphFactory.cs#L473-L481) | `_subGraphDependencies` map is only written/cleared, never read; recalc/persistence scan `SourceFallen8Id` instead. nested-subgraph-recalculation spec §2 said this map would be *replaced* by a read-driven registry; it never was | delete (and note in spec) |
| C9 | [AddPropertiesTransaction.cs:89-94](fallen-8-core/Transaction/AddPropertiesTransaction.cs#L89-L94) | Public builder `AddEdge(PropertyAddDefinition)` — a misnamed copy-paste of the `AddProperty` overload, called nowhere | verify (delete or rename) |
| C10 | [DelegateTransaction.cs:66,82](fallen-8-core/Transaction/DelegateTransaction.cs#L82) | `Name`/`_name` is write-only: set by `AnalyticsWriteBack`, never read (logs/spans use `GetType().Name`), so the "diagnostics only" label surfaces in no diagnostic | verify |
| C11 | [Delegates.cs:69](fallen-8-core/Algorithms/Delegates.cs#L69) | `LabelFilter` delegate has no engine or REST consumer (the `/delegates/validate` endpoint rejects it); docs/delegates.md:41 wrongly calls it "engine-internal" | verify (remove or fix doc) |
| C12 | [PathAndAnalyticsDto.cs:68,90,94](fallen-8-mcp/Bridge/Dto/PathAndAnalyticsDto.cs#L90) | `AnalyticsRequest.MaxIterations`/`Parameters` never populated and `PathElementDto.Weight` never read; agents cannot set analytics knobs (DampingFactor etc.). Diverges from mcp spec §179 | verify (wire the options surface, or remove) |
| C13 | [types.ts:650](fallen-8-web-ui/src/api/types.ts#L650) | `GraphFunctionInvocation` interface unreferenced (`invokeGraphFunction` builds the body inline) | verify (delete or wire) |

---

## D. Dead-ish code needing your call (public API surface / reserved extension points)

These are unreferenced but sit on the packaged public library surface or read like deliberate extension points, so they are questions, not delete-orders.

| # | Location | What | Confidence |
|---|---|---|---|
| D1 | [SaveTransaction.cs:76-79](fallen-8-core/Transaction/SaveTransaction.cs#L76-L79) | `GetOptimalNumberOfPartitions()` called only by a benchmark; `AdminController` inlines the identical `ProcessorCount*3/2` formula as its live `/save` default (so under-used + duplicated, not superseded) | suspected |
| D2 | [PluginFactory.cs:47,117](fallen-8-core/Plugin/PluginFactory.cs#L117) | `TryFind<T>(out, TypeEvaluator)` + the `TypeEvaluator` delegate have zero callers anywhere | suspected |
| D3 | [SubGraphFactory.cs:140](fallen-8-core/SubGraph/SubGraphFactory.cs#L140) | `GetAvailableSubGraphPlugins()` unused; unlike path/analytics there is no subgraph discovery surface in `/status`, and it wouldn't union registry-registered plugins even if wired | confirmed unused |
| D4 | [IService.cs:80](fallen-8-core/Service/IService.cs#L80) | `OnServiceRestart()` never invoked by the engine (only a test stub implements `IService`); reserved SPI hook or dead? | suspected |
| D5 | [DrawingFastSerializationHelper.cs:36](fallen-8-core/Serializer/DrawingFastSerializationHelper.cs#L36), [WebFastSerializationHelper.cs:36](fallen-8-core/Serializer/WebFastSerializationHelper.cs#L36) | `IFastSerializationTypeSurrogate` impls for `System.Drawing.Color`/`Hashtable` never registered (`TypeSurrogates` is always empty); ported general-purpose scaffolding | suspected |
| D6 | [IByteCompressor.cs:31](fallen-8-core/Serializer/IByteCompressor.cs#L31), [IMemoryStreamByteCompressor.cs:37](fallen-8-core/Serializer/IMemoryStreamByteCompressor.cs#L37) | Compressor interfaces with zero implementers/consumers; public, on the NuGet surface, framed as a pluggable compressor | suspected |
| D7 | [ICurve.cs:40](fallen-8-core/Index/Spatial/ICurve.cs#L40) | Spatial `ICurve : IGeometry` never implemented/consumed (siblings `IPoint`/`IMBR`/`IGeometry` are heavily used) | confirmed unused |
| D8 | [Fallen8SecurityOptions.cs:118](fallen-8-core-apiApp/Configuration/Fallen8SecurityOptions.cs#L118) | `AllowRemoteAccess` bound but read nowhere in the apiApp (the MCP option of the same name *is* enforced). Already self-documented "Reserved and currently NOT enforced" | keep-and-document |
| D9 | [Fallen8SecurityOptions.cs:101](fallen-8-core-apiApp/Configuration/Fallen8SecurityOptions.cs#L101) | `MaxSensitiveRequestBodyBytes` read nowhere; the real limit is a hard-coded `[RequestSizeLimit(1_048_576)]` on each sensitive endpoint, so the option is inert despite its "rejected with 413" doc | verify (wire or delete) |

---

## E. Cross-cutting inconsistencies (all confirmed)

### E1. Duplicated (slightly divergent) durable-write / delete IO helpers
- **Location:** [WriteAheadLog.cs:664-687](fallen-8-core/Persistency/WriteAheadLog.cs#L664-L687) vs [PersistencyFactory.cs:537-545,845-858](fallen-8-core/Persistency/PersistencyFactory.cs#L537-L545)
- `WriteAllBytesDurably` and `TryDeleteFile` are near-identical copies (differ only in `FileOptions.SequentialScan` vs `None`), and the temp+fsync+`File.Move(…,true)` atomic-commit sequence is hand-duplicated. Risk: the two atomic-write paths drift. **Action:** consolidate into one internal IO utility (or document the deliberate self-containment). **Question Q5.**

### E2. Two error-body shapes coexist: RFC 7807 ProblemDetails vs plain strings
- **Location:** plain-string [GraphController.cs:247-268](fallen-8-core-apiApp/Controllers/GraphController.cs#L247-L268) (`RolledBackResult`), StoredQueriesController; ProblemDetails NamespacesController / ChangeFeedController / AdminController
- The split is per-call-site (not a tidy newer-vs-core controller split) and acknowledged in `McpContractTest.cs:39-41` but unresolved. **There is an open feature for exactly this: `features/open/api-error-envelope/`.** **Action:** this is the open feature's job — confirm scope rather than fix ad hoc. **Question Q6.**

### E3. `ChangeFeedController` uses framework `Problem()` while `ProblemResults` is the documented single home
- **Location:** [ChangeFeedController.cs:119,128,143,149](fallen-8-core-apiApp/Controllers/ChangeFeedController.cs#L119) vs [Helper/ProblemResults.cs](fallen-8-core-apiApp/Helper/ProblemResults.cs)
- Every other RFC 7807 site (31 calls across 10 files) uses `ProblemResults.Create`; ChangeFeedController is the lone outlier calling bare `Problem(...)`. **Action:** consolidate.

### E4. Three divergent `ILogger` call styles for parameterised messages
- **Location:** `$`-interpolation [BreadthFirstSearchSubgraphAlgorithm.cs:125,278](fallen-8-core/Algorithms/SubGraph/BreadthFirstSearchSubgraphAlgorithm.cs#L278); `String.Format` [GraphController.Index.cs:225](fallen-8-core-apiApp/Controllers/GraphController.Index.cs#L225), SubGraphFactory.cs:461; structured template [TransactionManager.cs:326](fallen-8-core/Transaction/TransactionManager.cs#L326)
- Interpolation/`String.Format` defeat structured logging and are not gated (CodeQualityTest bans only `Console.Write*`). **Action:** consolidate on the structured named-template style.

### E5. Triplicated `Pass(JsonElement?)` bridge helper + inline parse-or-empty variants (MCP)
- **Location:** [AdminTool.cs:158-161](fallen-8-mcp/Tools/AdminTool.cs#L158-L161), [NamespaceTool.cs:128-131](fallen-8-mcp/Tools/NamespaceTool.cs#L128-L131), [PluginsTool.cs:250-253](fallen-8-mcp/Tools/PluginsTool.cs#L250-L253) (byte-identical) + inline variants
- Exactly the duplication CLAUDE.md's "one home" rule targets. **Action:** consolidate (needs an object/array default-factory split).

---

## F. Spec vs code divergence (stale docs; the code is deliberate)

For every item here git indicates the **code change was deliberate** and the doc/spec simply lagged. Per the repo convention that specs are historical records, some of these are one-line pointers rather than rewrites — flagged where relevant.

### F1. Shipped `appsettings.json` runs plugin registration OFF, opposite code + docs + spec (most operationally significant)
- **Location:** [appsettings.json:25](fallen-8-core-apiApp/appsettings.json#L25) `"EnableDynamicPluginLoading": false`
- The code default is `true` ([Fallen8SecurityOptions.cs:89](fallen-8-core-apiApp/Configuration/Fallen8SecurityOptions.cs#L89)), docs/security.md, docs/running.md, docs/plugin-registration.md and plugin-registration/spec.md §4.5 all say on-by-default, and the flip commit (38fbfc6) deliberately changed the code default false→true but **did not touch appsettings.json**. Because appsettings.json is always loaded, its explicit `false` wins at runtime — the hosted product ships with registration OFF, contradicting the documented contract. The value is REST-effective (`DynamicCapabilityAuthorization.cs:149` reads it). git says this is most likely an **unintentional stale leftover** from the api-security-boundary era (when off-by-default was the design), not a deliberate lockdown.
- **Confidence:** confirmed. **Action:** verify-with-user (set to true to match, or keep locked-down and fix code/docs to say false). **Question Q7 — the one I'd most like your call on.**

### F2. Dockerfile comment still describes the removed "upload + load plugin DLLs" behaviour
- **Location:** [Dockerfile:45](Dockerfile#L45) — flag now gates source registration (POST /plugins/*); the parenthetical is stale. **Action:** update.

### F3. `WriteAheadLog` XML docs cite a removed `Append` method
- **Location:** [WriteAheadLog.cs:49,103,104,236](fallen-8-core/Persistency/WriteAheadLog.cs#L49) — superseded by `AppendBuffered`+`FlushGroup`; the `<see cref="Append"/>` links are dangling. **Action:** update.

### F4. `collectible-codegen-assemblies` spec still says "Status: Planned" though implemented
- **Location:** [collectible-codegen-assemblies/spec.md:3](features/done/collectible-codegen-assemblies/spec.md) — sibling specs already reference it as "(landed)". **Action:** update header.

### F5. Three more `features/done/` specs still say "Status: Planned"
- **Location:** [core-storage-representation/spec.md:3](features/done/core-storage-representation/spec.md), [engine-performance/spec.md:3](features/done/engine-performance/spec.md), [adjacency-flattening/spec.md:3](features/done/adjacency-flattening/spec.md) — all implemented (segmented store, contiguous adjacency, perf P1/P3/P7/P10). **Action:** update headers.

### F6. `subgraph-quotas` spec/plan say defaults are "unlimited"; code ships bounded defaults
- **Location:** [subgraph-quotas/spec.md](features/done/subgraph-quotas/spec.md) §2/§3 vs [SubGraphQuota.cs](fallen-8-core/SubGraph/SubGraphQuota.cs) (1024 / 10M / 25M). git commit 800f087 ("M6: ship a generous-but-bounded default") is a deliberate later change; a test even asserts the default must be bounded. **Action:** update spec/plan.

### F7. Base `subgraph` spec still describes the removed `GraphElementPattern` / `graphElementFilter`
- **Location:** [subgraph/spec.md](features/done/subgraph/spec.md) §3.1/§3.2/§5 — `subgraph-typed-filters` deleted `GraphElementPattern` and retyped both slots to `Delegates.VertexFilter`/`EdgeFilter`. **Action:** update (or add a "superseded by subgraph-typed-filters" pointer).

### F8. `embedding-provider` spec: "GET /status is untouched" is stale
- **Location:** [embedding-provider/spec.md:293](features/done/embedding-provider/spec.md) — the later `embedding-out-of-box` feature added the `/status` embedding block (commit 57fc5e5). Accurate in its own historical frame; stale as a current-state claim. **Action:** judgment call (pointer vs leave as historical record).

### F9. `docs/mcp-server.md` says "Nine" tools but lists/registers ten
- **Location:** [docs/mcp-server.md:79](docs/mcp-server.md#L79) — the table (85-94) and `McpHost.cs:108-124` both have ten (f8_plugins was added by plugin-registration; the count word wasn't updated). **Action:** update.

### F10. `docs/mcp-server.md` f8_mutate row omits the batch ops
- **Location:** [docs/mcp-server.md:91](docs/mcp-server.md#L91) — lists 6 ops; `MutateTool` and mcp-followups added `create_vertices`/`create_edges`. **Action:** update.

### F11. `observability` README states a tag-hygiene invariant the code no longer holds
- **Location:** [observability/README.md:52-54](features/done/observability/README.md) — "no metric tag value originates from user input" is now false: the user-supplied namespace name is stamped as `fallen8.namespace.name` on the HTTP server metric. `fleet-observability` deliberately narrowed the wording (and lists this README as an update target); docs/observability.md and Fallen8Metrics.cs already carry the narrowed text, but the README wasn't updated. **Action:** update to the narrowed wording.

### F12. `graph-namespaces` spec keeps the old strict name regex
- **Location:** [graph-namespaces/spec.md:75,288](features/done/graph-namespaces/spec.md) `^[a-z0-9-]{1,63}$` — validation was deliberately relaxed to a permissive blocklist (commit 21afd87); the living-doc README already matches the code, and the spec self-declares as a historical record. **Action:** at most a one-line pointer (judgment call).

### F13. `instance-config` spec lists a chat-config `endpoint` field the DTO deliberately omits
- **Location:** [instance-config/spec.md:106-119](features/done/instance-config/spec.md) — `ChatProviderStatsREST` omits `endpoint` by design ("matching the embedding block's tag hygiene"); the spec's own /status sketch already omits it. **Action:** update the sketch.

### F14. Studio Dashboard panels relocated off the Dashboard
- **Location:** [studio-semantics/spec.md](features/done/studio-semantics/spec.md), [studio-coverage/spec.md](features/done/studio-coverage/spec.md) — provider card → Connect (Configuration), Stored queries → Query screen, jsonl interchange → Save games. Deliberate (instance-config); docs/studio.md is correct. **Action:** keep-and-document (specs are historical).

### F15. Stale "plugin upload" baked into the shipped OpenAPI document description
- **Location:** [Program.cs:105](fallen-8-core-apiApp/Program.cs#L105) → pinned in [features/done/web-ui/openapi-v0.1.json:5](features/done/web-ui/openapi-v0.1.json#L5) — the info.description lists "plugin upload" as a collection-level path; the DLL-upload endpoint was removed and plugins are per-namespace. **Action:** update the description string and regenerate the snapshot.

---

## G. Test-quality & quality-gate gaps (all confirmed)

### G1. `crash-durability` D4/D6 ship as "Implemented" with no fault-injection test
- **Location:** [crash-durability-hardening/plan.md:131-134](features/done/crash-durability-hardening/plan.md) ("honest note") vs spec.md:3 "Implemented" + §4 acceptance
- The D4 (core-data replay fail-stop) and D6 (recipe-manifest fail-loud) implementations are real and correct, but **no test drives either fault path** — nothing forces a core-data replay entry to throw/return-false, and nothing injects a manifest-write failure. So reverting either fix would keep the whole suite green. Disclosed only in a buried plan note that contradicts plan.md:5's "every fix lands with a test that would fail on the current tree."
- **Action:** add-real-test (a small internal replay/save fault seam). **Question Q8.**

### G2. The L1 cross-bunch load test can silently exercise a single bunch on 1 CPU
- **Location:** [LoadPathIntegrityTest.cs:131](fallen-8-unittest/LoadPathIntegrityTest.cs#L131) (default `SaveTransaction`)
- Bunch count = `min(byWork, Environment.ProcessorCount)`. On a 1-CPU runner **or a cgroup-CPU-limited container** it collapses to a single bunch; the parallel load runs single-threaded, so the concurrent `edgeTodo` race that L1 fixes is never exercised, yet the test passes and never asserts `>1` bunch. Note: forcing `SavePartitions>=2` will *not* help (it is applied only as a `Math.Min` cap); assert `BunchFileCount > 1` instead.
- **Action:** fix the test (assert multiple bunches). **Question Q8.**

### G3. `CodeQualityTest` clock/Console gates match narrow literals and miss obvious alternates
- **Location:** [CodeQualityTest.cs:110,138](fallen-8-unittest/CodeQualityTest.cs#L110)
- The Console gate (`Contains("Console.Write")`) misses `Console.Out.Write`/`Console.Error.Write`; the clock gate (`\bDateTime\.Now\b`) misses `DateTimeOffset.Now` — the same local/DST class the C8 fix removed. Latent today (product code is clean) but a future commit would slip through green. No analyzer/BannedSymbols backstop exists.
- **Action:** broaden both gates (keep DateHelper allowlisted). **Question Q8.**

**Minor (my own direct check):** the two `<NoWarn>$(NoWarn);1591</NoWarn>` entries in [fallen-8-core-apiApp.csproj:9](fallen-8-core-apiApp/fallen-8-core-apiApp.csproj#L9) and [fallen-8-mcp.csproj:10](fallen-8-mcp/fallen-8-mcp.csproj#L10) lack the explanatory comment the repo's own gate policy requests. The verifier judged this cosmetic (CS1591 is the standard GenerateDocumentationFile+NoWarn pattern, not a meaningful gate), so it is *not* counted as a finding — noted only for completeness. Otherwise the suppression surface is clean: no `#pragma warning disable`, no `#nullable disable`, no `GlobalSuppressions`, no editorconfig `severity=none`, and all `[UnconditionalSuppressMessage]` attributes are justified trimming/AOT suppressions.

---

## H. Candidates verified as deliberate/correct and dropped (transparency)

Not defects. Listed so you know they were checked, not missed.

1. `LoadTransaction.Rollback` is a `//TODO` no-op — **not a bug:** `LoadCore` fully restores pre-load state inside its own try/catch, so nothing remains for the manager's Rollback.
2. Two apiApp persistence `_json` options use reflection-based serialization — **deliberate,** IL2026-suppressed with justification (trimming disabled).
3. Index management moved Query → dedicated Indexes rail — **deliberate** `index-workspace` feature.
4. NL-assist default `builtin` → `instance` (POST /chat) — **deliberate** `instance-config` supersession.
5. Fine-tune model rename `f8-delegate` → `phi4-f8-mini`/`phi4-f8` — **deliberate** `delegate-model-variants`.
6. README architecture SVG → inline mermaid; images moved to docs/ — **deliberate** `readme-with-visuals`/rebrand.
7. `NoWarn 1591` lacks a comment — cosmetic, not a real gate gap (see Section G note).
8-13. Six further per-area candidates the verifier rejected as correct behaviour or alive-via-reflection/DI/test-only (e.g. `PluginFactory.InvalidateDiscoveryCache` kept alive by `EnginePerformanceTest`, the `#if DEBUG` serializer diagnostics being intentional, WAL ordinals 1-18 all live).

---

## I. Open questions (consolidated)

Behavioural fixes:
- **Q1 (A2):** `/path` 404 doc for a missing vertex — trim the doc to the stored-query case, or implement the deferred `TryGetVertex` 404 checks?
- **Q2 (A4):** WAL-enabled `DelegateTransaction` — report `Durable=false`, or amend the `Durable` contract to carve out snapshot-only delegate writes?
- **Q3 (B1/B2/B4):** Should the `bool`/raw-return and `void` endpoints be converted to `IActionResult` returning the 400/404/204 they document (matching their siblings), or are their current shapes accepted and the docs should change instead?
- **Q4 (B5):** `GraphScan.interestingLabel` — forward it (fix the filter) or remove the dead parameter?

Config / operational:
- **Q7 (F1):** Is `appsettings.json` shipping `EnableDynamicPluginLoading=false` intentional (secure-by-default), or a stale leftover that should be `true` to match the code default, docs, and spec? *(Highest-impact decision — it flips a security-relevant default for every hosted deployment.)*

Consolidation / scope:
- **Q5 (E1):** Consolidate the duplicated durable-write/atomic-rename/delete IO helpers into one internal utility, or keep the WAL deliberately self-contained?
- **Q6 (E2):** The plain-string vs ProblemDetails error-body split — is this the job of the existing `features/open/api-error-envelope/` feature (defer to it), or do you want the plain-string core controllers migrated now?

Tests / gates:
- **Q8 (G1/G2/G3):** Add the missing fault-injection tests (D4/D6), fix the L1 single-bunch test to assert `>1` bunch, and broaden the CodeQualityTest gates to catch `DateTimeOffset.Now` / `Console.Out|Error.Write`?

Dead-code judgment calls (public API / extension points — Section C/D):
- **Q9:** For the public-surface items (D1 `GetOptimalNumberOfPartitions`, D2 `TryFind(TypeEvaluator)`, D4 `IService.OnServiceRestart`, D5 Drawing/Web surrogates, D6 compressor interfaces, D7 `ICurve`, plus C7 MCP `Bool`/`GetBool`, C9 `AddPropertiesTransaction.AddEdge`, C10 `DelegateTransaction.Name`, C11 `LabelFilter`, C12 MCP DTO knobs, C13 web-ui type): delete as dead, or keep as intended API/extension points? A blanket "delete unless load-bearing" is fine if you prefer.

Spec hygiene (Section F):
- **Q10:** For the stale specs, do you want the "Status: Planned" headers (F4/F5) and factual drifts (F6/F7/F11/F13) updated, and for the ones the repo treats as historical records (F8/F12/F14) a one-line "superseded by X" pointer, or left entirely as-is?

Unambiguous items I can just do once you greenlight (no judgment needed): the confirmed dead deletions C1-C6, C8; the stale-doc updates F2/F3/F9/F10/F15.

---

## J. Changes applied (2026-07-26)

Approved buckets acted on: **A + B** (behavioural / REST), **C** (dead code), **E** (consolidations, minus E1). Baseline held throughout: build 0 warnings / 0 errors; tests 1033 passed / 28 skipped / 0 failed (one transient flaky failure in an unrelated timing test did not recur across two further runs). OpenAPI snapshot regenerated and its diff reviewed (only the deliberately-changed response codes/descriptions). Nothing committed.

**Dead code removed (C):** appsettings `EnableDynamicPluginLoading` (per your "remove the key") + `PluginDirectory` (C1); BinaryFormatter usings (C2); `EdgeInfo` class (C3); X509 using in EdgeModel (C4); `_numberOfToBeTestedVertices` field (C5); RTree/ARTreeContainer commented code (C6); `_subGraphDependencies` map + its Track/UnTrack methods (C8).

**Behavioural fixes (A/B):** `/path` now returns 500 on a runtime fault instead of a masked 200-empty (A1); `GetSubGraphContents` clamps `maxElements` to `MaxPageSize`, now the single `internal` home of the cap (A3); `IndexFactory.TryCreateIndex` logs before returning false (A5); batch vertex-create records into a caller-owned list before embedding projection, matching the edge path so a residual throw rolls back (A6); `AdminController.Load` null-guards its body → 400 (B3).

**Docs-match-code (B1/B2/B4/A2):** removed the unreachable 400/404 annotations and corrected the response text on the bool-returning index/service endpoints and the two scan endpoints (now documenting the 200-false / 204-on-miss reality); `/trim` and `/tabularasa` documented as 200 (void → 200, not 204); `/path`'s 404 doc trimmed to the stored-query case.

**Consolidations (E):** `ChangeFeedController` routed through `ProblemResults.Create` (E3); five interpolation/`String.Format` log calls converted to structured templates (E4); the triplicated MCP `Pass()` helper unified into `ToolResults.Pass`/`PassArray` (E5).

**Deferred / not done this pass:** E1 (durable-IO helper consolidation) held as a focused, separately-reviewed change because it touches the WAL + checkpoint atomic-write/rename/fsync commit points. E2 (error-body unification) belongs to the open `api-error-envelope` feature. Section F (stale specs/docs) and Section G (test gaps) were out of the selected scope — note this leaves the A1/A3/A6/B3 behavioural fixes not yet pinned by a regression test. The Section C/D public-API/extension-point items (C7, C9-C13, D1-D9) and B5/A4 remain open questions.

---

## K. Second pass (2026-07-26): E1, tests, F, and the dead-API re-analysis

After the first pass, the remaining work was approved and applied. Build stays clean (0/0); the .NET suite is **1038 passed / 28 skipped / 0 failed** across repeated runs; the web-ui suite is **450 passed** and `tsc -b` is clean.

**E1 (done):** the duplicated durable-write / atomic-rename / best-effort-delete logic is now single-sourced in a new `fallen-8-core/Persistency/DurableFileIo.cs`; `WriteAheadLog` and `PersistencyFactory` call it (thin per-class adapters for the `FileOptions` difference). Durability suite green.

**Tests (done):** pinned **A1** (`/path` → 500 on a runtime fault), **A3** (subgraph clamp), **B3** (`Load` null → 400); made the **G2** L1 cross-bunch test fail loudly on single-bunch degeneration (inconclusive on 1 CPU); broadened the **G3** `CodeQualityTest` gates to catch `Console.Out/Error.Write` and `DateTimeOffset.Now`. **A6** and **G1 (D4/D6)** deferred — they need a fault-injection seam into engine internals; a "test" that doesn't drive the fault would be a weak-test masquerade, so they are a focused follow-up (add `InternalsVisibleTo` + a minimal fault hook).

**F (done, per policy):** Planned→Implemented headers on 4 done-feature specs; corrected drift (subgraph-quotas "unlimited"→bounded, instance-config chat `endpoint`, observability README tag-hygiene narrowing, mcp-server.md count 9→10 + batch ops); one-line "superseded by X" pointers on the historical-record specs (subgraph, embedding-provider, graph-namespaces, studio-semantics/coverage); Dockerfile + OpenAPI `info.description` + WAL `Append` crefs corrected. Snapshot regenerated and diff-reviewed.

**Dead-API re-analysis (the important correction).** On the user's instruction — "nobody uses it now ≠ not needed; be careful and honest" — every C/D item was re-analyzed (git history + spec/UI intent + sibling capability) with an adversarial "prove it is needed" pass. This flipped **5 of the original 8 "clear deletes"**. Final outcomes:

- **WIRE — unwired-but-intended, now made functional:**
  - **C11 — functional GraphScan label filter.** The private `FindElements` overload dropped `interestingLabel` ([Fallen8.Scan.cs](fallen-8-core/Fallen8.Scan.cs)), so the committed `ScanSpecification.Label` field and the whole label-filter path were a silent no-op. Now: the engine forwards `interestingLabel`, the REST `/scan/graph/property` passes `definition.Label`, docs updated, pinned by `GraphScanLabelFilterTest`. `Delegates.LabelFilter` kept.
  - **D3 — subgraph discovery.** `GetAvailableSubGraphPlugins` is now registry-aware and surfaced on `GET /status` as `availableSubGraphPlugins` (parity with path/analytics/index). Snapshot + `SubGraphControllerTest` updated.
  - **C9** `AddPropertiesTransaction.AddEdge` → renamed to the `AddProperty(PropertyAddDefinition)` overload (fixes the copy-paste name; restores family symmetry).
  - **C10** `DelegateTransaction.Name` → surfaced on the execute span (`transaction.name`) so identical DelegateTransaction spans are distinguishable.
  - **C13** web-ui `invokeGraphFunction` body now typed `satisfies GraphFunctionInvocation`.
- **KEEP — deliberate extension points (no change):** **C7** (MCP schema-DSL boolean primitive + §8 deferrals), **D6** (public compressor-interface extension seam), **D7** (`ICurve` public geometry SPI subtype).
- **DELETE — genuinely vestigial (applied):** **D1** `SaveTransaction.GetOptimalNumberOfPartitions` (superseded by work-based auto-sizing; benchmark switched to `SavePartitions=0`), **D2** `PluginFactory.TryFind(TypeEvaluator)` + `TypeEvaluator` (carried from the .NET-Framework port, never wired, excluded from the documented discovery API), **D5** the two `Drawing`/`Web` FastSerializer surrogate impls (ported Color/Hashtable scaffolding, never registered — the `IFastSerializationTypeSurrogate` seam stays). All three were on the public NuGet surface; deletion accepted as negligible-risk per the user.
- **C12 (analytics knobs)** classified WIRE but **deferred** at the user's choice (a real feature: expose `maxIterations`/`DampingFactor` through `f8_analytics`).

**Still open / not done (at end of second pass):** A4, A6 + G1 fault-injection tests, C12, E2. (B5 is resolved by C11.) These were then tackled in the loose-ends pass below.

---

## L. Loose-ends pass (2026-07-26, follow-up)

Committed on `feature/cleanup-2026-07`. Suite stays green (1040 passed / 28 skipped / 0 failed).

- **A4 — done.** A WAL-enabled `DelegateTransaction` (which composes real mutations the WAL codec does not serialize) now reports `Durable=false` instead of the misleading `true`; `LogCommittedTransaction` distinguishes it from the Save/Load lifecycle transactions (which stay `durable=true`). Pinned by a new `Durable==false` assertion in `PluginWriteTransactionsTest`.
- **G1 D6 — done.** New fault-injection test (`WalSubGraphSupportTest.D6_ManifestWriteFailure…`) blocks the subgraph recipe-manifest sidecar with a directory and asserts the Save fails (rolled back) with the WAL left unreset so the logged subgraph replays. Black-box, no production change.
- **C12 — done.** `f8_analytics` now forwards `maxIterations` and a numeric `parameters` map (e.g. `DampingFactor`) to the REST/engine, which already supported them; schema + docs updated; pinned by `AnalyticsToolKnobsTest` (captures the outbound request body).
- **A6 and G1 D4 — deliberately NOT hacked.** Both need a fault-injection *seam* into engine internals: A6's projection throw is OOM-only (its only escape points, `GetPropertyStoreForSerialization`/`GetIndicesSnapshot`, can't be forced black-box), and D4's core-data replay fail-stop needs a crafted/corrupt WAL frame. The only ways to test them are a production test-hook (bloat, against the repo ethos) or brittle reflection/frame-crafting (against the test-quality bar). The right home is a dedicated durability fault-injection seam (a dedicated small feature), not a drive-by. Note: A6's fix is in place and structurally mirrors the *tested* edge path; D4's sibling (torn-tail decode fail-stop) is already tested by `WriteAheadLogHardeningTest`, so the residual untested surface is the TryExecute-returns-false/throws branch only.
- **E2 — belongs to the open `api-error-envelope` feature.** That feature's spec explicitly captures the plain-string→ProblemDetails migration as its own unit *because* it is heavy and cross-cutting (53 sites in GraphController alone; nearly every controller test asserts an error body). Doing it under this cleanup branch would violate the feature workflow and cause massive churn. Correctly deferred to its own `feature/api-error-envelope` branch. (The one clean sub-part, ChangeFeed → `ProblemResults` (E3), was already done in the first pass.)
- **Flaky test — observed but unidentified; not reproduced.** An intermittent single-test failure surfaced 3 times early in the session (during the Group 4, W1 and W3 full runs), each passing on immediate re-run and never on a trx-logged run. It then did NOT recur across 18+ consecutive clean full runs, including a dedicated 10-run hunt. All 3 occurrences coincided with heavy concurrent load (parallel builds; a leftover apiApp dev process early on), which points to a load-sensitive timing flake rather than a deterministic defect. Its identity could not be confirmed. Recommendation: if it resurfaces in CI, capture the failing run's trx (the culprit is likely a timing/concurrency-sensitive test) — not pursued further here as it would not reproduce.
