# API Error Contract — Plan

Companion to [spec.md](./spec.md). Correctness feature: the global net first, then per-endpoint
fixes in dependency order, each flipping its Phase-0 characterization test to the correct contract.

GitHub issue: to be opened (label: `feature`). Branch: `feature/api-error-contract`.

## Phase 0 — Baseline & guardrails

Intent: pin today's contradictions so the fixes visibly flip them, and stand up the end-to-end
harness the acceptance criteria depend on. This is a correctness feature, so no benchmark is needed
(no `[TestCategory("Benchmark")]`/`[Ignore]` gate — that convention is for perf work); the guardrail
is characterization tests + the real-pipeline harness.

- [ ] Add an E2E harness test class booting the real app via `WebApplicationFactory<Program>` (model
  it on `OpenApiDocumentTest.cs`), so ProblemDetails wiring and status codes can be asserted against
  the genuine pipeline.
- [ ] Characterization tests reproducing each defect at its **current** behaviour (each is migrated
  to the correct assertion by its implementation phase, not deleted):
  - [ ] non-integer id on `AddProperty`/`TryRemoveProperty`/`TryRemoveGraphElement`/`GetInDegree`/
    Benchmark → currently throws/500 (E2).
  - [ ] bad `fullQualifiedTypeName` / null `Literal` on the scan actions → currently 500 (E3).
  - [ ] missing edge on `GetSourceVertexForEdge`/`GetTargetVertexForEdge` → currently throws
    `WebException` (E4).
  - [ ] uncompilable filter / unknown algorithm / missing endpoint on `CalculateShortestPath` →
    currently 200-empty (E5).
  - [ ] oversized `maxElements` on `GetGraph`/`GetSubGraphContents` → currently materializes all;
    negative → currently silent empty (E6).

## Phase 1 — Global error envelope (foundation)

Intent: the RFC 7807 safety net beneath every endpoint.
- [ ] `Program.cs`: `builder.Services.AddProblemDetails()`.
- [ ] `Program.cs` pipeline: `app.UseExceptionHandler()` (+ optional `app.UseStatusCodePages()`),
  keeping the developer exception page in Development.
- [ ] Harness test: an action that throws yields `application/problem+json` with the correct status
  and no stack leak outside Development.

## Phase 2 — Route id / literal / type parsing → 400

Intent: malformed values become 400, not 500.
- [ ] Type the route/query id params as `int` (drop `Convert.ToInt32`) on `GraphController`
  `AddProperty` (`:733`), `TryRemoveProperty` (`:788`), `TryRemoveGraphElement` (`:834`),
  `GetInDegree` (`:870`), `GetOutDegree` (`:889`), `GetInEdgeDegree` (`:909`), `GetOutEdgeDegree`
  (`:933`); and `BenchmarkController` `CreateGraph` (`:74`) + `Bench` (`:87`).
- [ ] Guard `definition.Literal` (null → `BadRequest`) and switch `Type.GetType(name, true, true)` to
  `Type.GetType(name, throwOnError: false)` + null-check → `BadRequest` on `GraphScan` (`:500-504`),
  `IndexScan` (`:549-553`), `RangeIndexScan` (`:597-601`), `AddProperty` (`:736-738`).
- [ ] Flip the E2/E3 characterization tests to assert 400.

## Phase 3 — Read not-found → 404

Intent: missing referenced elements return 404 from the read getters.
- [ ] `GetSourceVertexForEdge` (`:341`) / `GetTargetVertexForEdge` (`:363`) → `ActionResult<int>`,
  return `NotFound()` on `!TryGetEdge`, delete the `WebException` throw.
- [ ] Migrate `GraphControllerTest.GetSourceVertexForEdge_WhenEdgeNotExists_*` and
  `GetTargetVertexForEdge_WhenEdgeNotExists_*` (`:333-347`) from `[ExpectedException(typeof(WebException))]`
  to asserting a `NotFoundResult` (404). Keep the happy-path assertions (`:209-210`, `:564-565`).
- [ ] Degree getters → `ActionResult<uint>`: `NotFound` for a missing vertex, `Ok(count)` otherwise
  (removes the `0`-ambiguity). Update their `<response>`/`[ProducesResponseType]` to add 404.

## Phase 4 — Path contract (make 400/404 reachable)

Intent: the declared path codes become reachable; the swallow-to-200 is removed.
- [ ] `CalculateShortestPath` (`:996-1073`) → `ActionResult<List<PathREST>>`. Order: compile-fail
  (non-null `compilerMessage`) → `BadRequest`; unresolved `from`/`to` via `TryGetVertex` → `NotFound`;
  unknown algorithm name (checked against available `IShortestPathAlgorithm` plugins) → `BadRequest`;
  genuine no-path after those checks → `Ok(empty)`; narrow `catch` → 500 ProblemDetails. Keep the
  `MaxDepth <= 0` early empty-200.
- [ ] Flip the E5 characterization tests; add the `path-filter-arity-fix` cross-reference case
  (wrong-arity filter → 400).

## Phase 5 — Bounded reads

Intent: cap result size; handle negatives explicitly.
- [ ] Add an API-layer `MaxPageSize` constant.
- [ ] `GetGraph` (`:317`) and `GetSubGraphContents` (`SubGraphController.cs:230`):
  `Math.Clamp(maxElements, 0, MaxPageSize)`; negative → `BadRequest`. Add the `400` to their
  `[ProducesResponseType]`/`<response>`.
- [ ] Flip the E6 characterization tests (oversized clamped; negative → 400).

## Phase 6 — Attribute / return-type reconciliation + UploadPlugin

Intent: declared codes == reachable codes; no `void` action without an error path.
- [ ] Reconcile `[ProducesResponseType]` on the five GraphController mutations
  (`:119-121,233-236,728-730,783-785,829-831`) with what `RolledBackResult` + route binding can emit
  (property/remove trio gains a reachable `400`; add `409`/`404` only where the engine sets those
  reasons for that action).
- [ ] `AdminController.UploadPlugin` (`:324`): guard `PluginFactory.Assimilate` → `BadRequest` on an
  invalid/incompatible DLL; return an explicit `NoContent()`.

## Phase 7 — AdminController tests

Intent: pin the previously-untested admin surface as part of the contract.
- [ ] New `AdminControllerTest.cs` (MSTest, `TestLoggerFactory.Create()`, arrange/act/assert):
  save → load round-trip; `UploadPlugin` valid + invalid stream (204 vs 400); `Status`; `Trim`;
  `TabulaRasa`; `/service` create + delete.

## Measure & document

Intent: prove the contract end-to-end and keep the docs honest.
- [ ] Full suite green (`dotnet test fallen-8-core.sln`); `transaction-failure-reasons` tests
  unchanged.
- [ ] Harness assertion: `/openapi/v0.1.json` carries the reachable statuses (incl. `409` where
  emittable).
- [ ] Update the touched `<response>` docs and the feature README so the documented contract matches
  the shipped behaviour. Record any client-visible contract change (degree `0→404`, path
  `empty-200→400/404`, `maxElements` clamp).

## Progress

- [ ] Phase 0 — E2E harness + characterization tests pinning E2–E6
- [ ] Phase 1 — AddProblemDetails + UseExceptionHandler (global RFC 7807 net)
- [ ] Phase 2 — id/literal/type parsing → 400
- [ ] Phase 3 — missing edge / degree getters → 404 (WebException tests migrated)
- [ ] Phase 4 — path contract: compile-fail→400, missing endpoint→404, unknown algo→400, no-path→200
- [ ] Phase 5 — bounded `maxElements` (clamp + negative→400)
- [ ] Phase 6 — `[ProducesResponseType]`/return-type reconciliation + UploadPlugin guard
- [ ] Phase 7 — AdminController test suite
- [ ] Measure & document

## Decision / revisit condition

- **`transaction-failure-reasons` (status mapping + problem+json envelope).** That feature deliberately
  kept the rollback responses' string body and declared a general problem+json envelope a non-goal;
  its `RolledBackResult`/`MapFailedSubGraphCreate` **status mapping is correct and is not reopened
  here**. This feature adds the global ProblemDetails envelope for the previously-unhandled paths and
  the new `400` validation bodies. Revisit aligning the existing rollback **string bodies** to
  problem+json only if a client needs a single machine-readable error body across all paths — a body
  change, never a status change.
- **`transaction-failure-reasons` mutation boundary.** The out-of-range remove/property id → `500`
  and in-range/absent → `202` no-op semantics are unchanged; the 404 work here is on the read getters
  only. Reopen only if the mutation not-found contract itself is redesigned (a separate decision).
- **`api-security-boundary`.** Auth error responses (`401`/`403`) are out of scope and owned there;
  this feature's global handler should compose with, not pre-empt, that work.
