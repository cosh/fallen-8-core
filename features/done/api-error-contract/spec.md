# API Error Contract — Specification

> **Status:** Largely implemented (P1 correctness) — from the 2026-07 principal-architect &
> performance review. Several REST endpoints crashed, threw, or silently swallowed errors in ways that
> contradicted their documented status codes; this establishes one consistent error contract (correct
> status + RFC 7807 body) across the API surface.
>
> **Delivered on branch `feature/api-error-contract`:**
> - **E1 global net** — `Program.cs` registers `AddProblemDetails()`, wires `UseExceptionHandler()`
>   outside Development (dev keeps the developer exception page), and adds `UseStatusCodePages()`, so an
>   unhandled fault (and a bare status result) is now an `application/problem+json` response.
> - **E2 id parsing → 400** — the property/remove mutations and the four degree getters take `int`
>   route params (route-binding failure is a 400 ProblemDetails under `[ApiController]`) instead of
>   `Convert.ToInt32` throwing `FormatException` → 500.
> - **E3 literal/type parsing → 400** — a shared `TryResolveType` + `TryConvertLiteral` guard turns an
>   unknown type name / null literal / unconvertible value on `GraphScan`/`IndexScan`/`RangeIndexScan`/
>   `AddProperty` into a `BadRequest`, not a thrown `TypeLoadException` → 500. The three scans return
>   `ActionResult<IEnumerable<int>>`.
> - **E4 missing edge → 404** — `GetSource`/`GetTargetVertexForEdge` return `ActionResult<int>` +
>   `NotFound()`; the two `[ExpectedException(WebException)]` tests were migrated to assert 404.
> - **E6 bounded reads** — `GetGraph` clamps `maxElements` to `[0, MaxPageSize]` (100 000), so a single
>   request can no longer materialize the whole graph and a negative yields an empty page.
> - **E7 (partial)** — the degree getters return `ActionResult<uint>` (404 for a missing vertex vs
>   200/0 for a live zero-degree vertex, ending the ambiguity); `UploadPlugin` guards `Assimilate` and
>   returns `BadRequest` on failure (and 400 on a null stream), returning `NoContent()` on success.
>
> Guarded by `ApiErrorContractTest` (edge 404, degree 404-vs-0, scan bad-type 400, GetGraph clamp,
> UploadPlugin 400) plus the migrated `GraphControllerTest`/`PathTest`/`PathTestEdgeCases` call sites.
>
> **Deferred (documented):**
> - **E5 (partial)** — the compile-failure → 400 on `/path` already landed with `path-filter-arity-fix`.
>   The *missing-source/target-vertex → 404* and *unknown-algorithm-name → 400* refinements are
>   deferred: an existing test deliberately treats a nonexistent-vertex path query as a 200-empty
>   result, and flipping it to 404 is a judgement-call behaviour change better made explicitly.
> - **E8 admin test suite** — a net-new `AdminControllerTest` covering the full `/save`/`/load`/`/status`/
>   `/trim`/`/tabularasa`/`/plugin`/`/service` surface is deferred; the admin surface is otherwise
>   unchanged by this pass (no regression), and `UploadPlugin`'s new guard is covered by
>   `ApiErrorContractTest`.
> - The exhaustive `[ProducesResponseType]` reconciliation across every mutation is left for the E8
>   follow-up; the reachable statuses touched here (404/400 on the getters/scans) are declared.

## 1. Problem / current state

The API has no global error handling and several endpoints violate their own `[ProducesResponseType]`
/ `<response>` docs. `Program.cs` builds the pipeline (`Program.cs:97-116`) with **no**
`AddProblemDetails()`, **no** `UseExceptionHandler()`, and **no** `UseStatusCodePages()`, so any
unhandled exception escapes as a bare framework 500 (empty body outside Development, HTML dev page in
it) — never the RFC 7807 `application/problem+json` the rest of the contract implies. On top of that
missing net, individual actions throw or swallow where their docs promise a `4xx`:

| # | Issue | Location(s) | Effect |
|---|-------|-------------|--------|
| E1 | No global exception handling / ProblemDetails registration | `Program.cs:97-116` | Unhandled throws → bare 500, never problem+json; masks E2–E5 |
| E2 | Route/query ids parsed with `Convert.ToInt32(string)` → `FormatException` → **500** where docs say **400** | `GraphController.cs:733` (AddProperty), `:788` (TryRemoveProperty), `:834` (TryRemoveGraphElement), `:870` (GetInDegree) — same pattern in `GetOutDegree:889`, `GetInEdgeDegree:909`, `GetOutEdgeDegree:933`; `BenchmarkController.cs:74` (CreateGraph), `:87` (Bench) | Non-integer id → 500, docs promise 400 |
| E3 | `Type.GetType(name, true, true)` (throwOnError) + unchecked `definition.Literal` deref → **500** for a bad type name / missing literal, docs say **400** | `GraphController.cs:500-504` (GraphScan), `:549-553` (IndexScan), `:597-601` (RangeIndexScan), `:736-738` (AddProperty) | Bad `fullQualifiedTypeName` / null `Literal` → 500, docs promise 400 |
| E4 | `GetSourceVertexForEdge`/`GetTargetVertexForEdge` **throw `System.Net.WebException`** (→ 500) for a missing edge though both are documented **404** | throws at `GraphController.cs:349` and `:371`; docs at `:336` and `:358`; the throw is *pinned* by `GraphControllerTest.cs:333-347` (`[ExpectedException(typeof(WebException))]`) | Missing edge → 500; a test entrenches the wrong behaviour |
| E5 | `CalculateShortestPath` wraps its whole body in `catch (Exception)` and returns the empty list (**200**) | `GraphController.cs:996-1073`; compiler message captured `:1020`, logged `:1028`, never surfaced; unknown algorithm → `Fallen8.cs:886,897-903` returns `false` | An uncompilable filter, an unknown algorithm name, and a genuine compute fault all yield "200 with no paths", indistinguishable from "no path exists". The declared `400`/`404` (`:994-995`) are unreachable |
| E6 | Unbounded / negative `maxElements` — no upper clamp | `GraphController.cs:317` (GetGraph; `Take` at `:321,324`); `SubGraphController.cs:230` (GetSubGraphContents; `Take` at `:239,240`) | `int.MaxValue` materializes the whole graph (DoS); a negative falls through to `Take(negative)` → silent empty result |
| E7 | `[ProducesResponseType]` / return-type gaps | mutation attribute sets at `GraphController.cs:119-121,233-236,728-730,783-785,829-831`; ambiguous getters `:874,893,917,941`; `AdminController.cs:324` (UploadPlugin) | Documented codes unreachable or emittable codes undeclared; ambiguous primitives; `UploadPlugin` returns `void` with no error path |
| E8 | AdminController has **no tests at all** — no `AdminControllerTest.cs` | `AdminController.cs` (`/save`, `/load`, `/status`, `/trim`, `/tabularasa`, `/plugin`, `/service`) | The admin surface's status contract is entirely unpinned |

**E7 detail.** The five GraphController mutations all route a waited-on rollback through the shared
`RolledBackResult(reason)` (`GraphController.cs:1250-1271`), which can emit `400/404/409/500`
depending on the structured `TransactionFailureReason`. Their declared sets are inconsistent —
`AddVertex` = `202/400/500` (`:119-121`), `AddEdge` = `202/400/404/500` (`:233-236`), and
`AddProperty`/`TryRemoveProperty`/`TryRemoveGraphElement` = `202/400/500` (`:728-730,783-785,829-831`)
— and the documented `400` is currently unreachable on the property/remove trio (the id `FormatException`
in E2 makes it a 500, not a 400). Separately, the degree getters return raw `uint` `0` for **both**
"vertex not found" and "vertex has zero edges" (`GetInDegree:874`, `GetOutDegree:893`,
`GetInEdgeDegree:917`, `GetOutEdgeDegree:941`) — an ambiguous result — and `UploadPlugin`
(`AdminController.cs:324`) returns `void`, declares `204/400`, but calls `PluginFactory.Assimilate`
with no guarded path, so an invalid/incompatible DLL throws → 500, contradicting the documented 400.

This theme is the missing-net + per-endpoint cleanup that makes the already-correct
`transaction-failure-reasons` mapping the norm rather than the exception. It also makes
`path-filter-arity-fix` observable: a wrong-arity path filter that fails to compile is exactly the
E5 case that today returns a silent empty 200.

## 2. Goals / non-goals

**Goals**

- A global error envelope: register ProblemDetails and an exception handler so any unhandled fault
  becomes an RFC 7807 `application/problem+json` response with the correct status and no stack leak
  outside Development.
- Every endpoint's *reachable* status codes match its `[ProducesResponseType]`/`<response>` docs:
  malformed value → `400`, missing referenced element → `404`, and the swallow-to-200 path removed.
- Bounded reads: `maxElements` is clamped to a configured maximum and negatives are handled
  explicitly.
- The admin surface is pinned by tests for the first time.

**Non-goals**

- Changing the transaction model, the single-writer/lock-free-read model, or the API route/versioning.
- Re-opening the `transaction-failure-reasons` **status mapping** (`RolledBackResult` /
  `MapFailedSubGraphCreate`) — it is correct and complete; this feature only fills the attribute and
  return-type gaps *around* it and adds the global net *beneath* it.
- Re-opening the `transaction-failure-reasons` deliberate mutation boundary: an **out-of-range**
  element id on the remove/property mutations stays `500` and an **in-range/absent** id stays a
  `202` no-op. This feature does not change those mutation semantics (the 404 work here is on the
  *read* getters, not the remove/property transactions).
- Force-migrating the existing rollback responses' **body shape** to problem+json. That envelope was
  an explicit non-goal in `transaction-failure-reasons` (which kept the string body + correct
  status); this feature owns the global envelope for the currently-unhandled paths and the new `400`
  validation bodies, but leaves the existing rollback string bodies as-is (their status codes — the
  load-bearing part — are already right). Aligning those bodies is a documented revisit, not a goal.
- Authentication/authorization error responses (`401`/`403`) — owned by `api-security-boundary`.

## 3. Design sketch

**E1 — global net (foundation).** In `Program.cs`: `builder.Services.AddProblemDetails();` and, in the
pipeline, `app.UseExceptionHandler();` (keep the developer exception page in Development so dev
diagnostics are not masked; ProblemDetails elsewhere). Optionally `app.UseStatusCodePages()` so bare
status results also carry a problem body. This is the safety net; the per-endpoint fixes below make
the *intended* status reachable rather than relying on the net.

**E2 — id parsing → 400.** Type the offending route/query parameters as `int` (route templates gain no
`:int` constraint, so binding failure is a **400 ModelState** ProblemDetails under `[ApiController]`,
not a 404 route-miss) and delete the manual `Convert.ToInt32`. Where a bespoke message is wanted, use
`int.TryParse(...) → BadRequest(...)`. Applies to the `GraphController` property/remove/degree actions
and both `BenchmarkController` actions. Standardize on **400 for a malformed value**, never 404.

**E3 — literal/type parsing → 400.** Guard `definition.Literal` (null → `BadRequest`) and replace
`Type.GetType(name, true, true)` with `Type.GetType(name, throwOnError: false)` + a null check →
`BadRequest("unknown type …")`, instead of a thrown `TypeLoadException`. Applies to GraphScan,
IndexScan, RangeIndexScan, and AddProperty.

**E4 — missing edge → 404.** Change `GetSourceVertexForEdge`/`GetTargetVertexForEdge` to
`ActionResult<int>`, return `NotFound()` when `TryGetEdge` is false, and delete the `WebException`
throw. Migrate the two `[ExpectedException(typeof(WebException))]` tests
(`GraphControllerTest.cs:333-347`) to assert a `NotFoundResult` / 404 — this is the point of the fix,
not a regression.

**E5 — path contract.** Change `CalculateShortestPath` to `ActionResult<List<PathREST>>` and order the
checks so each declared code is reachable:
1. Compile the traverser; if `GeneratePathTraverser` returns a non-null compiler message (traverser
   null), return `BadRequest(compilerMessage)` — this is where `path-filter-arity-fix` becomes
   visible instead of a silent empty 200.
2. Resolve `from` and `to` via `_fallen8.TryGetVertex`; a missing endpoint → `NotFound`.
3. Validate the algorithm name against the available `IShortestPathAlgorithm` plugins; an unknown
   name → `BadRequest` (rather than the `TryCalculateShortestPath` → `false` swallow).
4. Call `TryCalculateShortestPath`; a `false`/empty result **after** the above checks is a genuine
   "no path" → `Ok(empty)` (200).
5. Keep a narrow `catch` that returns a **500 ProblemDetails**, not the swallow-to-200. The
   `MaxDepth <= 0` early return stays a valid empty 200.

**E6 — bounded reads.** Add a configured `MaxPageSize` (an API-layer constant). In `GetGraph` and
`GetSubGraphContents`, `Math.Clamp(maxElements, 0, MaxPageSize)` for the positive range and reject a
negative with `BadRequest` (documented) so a bounded, explicit result replaces `Take(int.MaxValue)`
and `Take(negative)`. Optional design extension (not required): a `skip`/offset query parameter for
paging past the first page.

**E7 — attribute & return-type reconciliation.**
- Declare on each mutation exactly the statuses its `RolledBackResult` path and route binding can
  actually emit (e.g. the property/remove trio gains a genuinely-reachable `400`; add `409`/`404`
  only where the reason channel can set them for that action — do not decorate with codes the engine
  never produces).
- Migrate the ambiguous degree getters to `ActionResult<uint>`: `NotFound` for a missing vertex, `Ok(count)`
  otherwise — removing the `0`-means-two-things ambiguity. This is a deliberate, documented **read**
  contract change (distinct from the mutation not-found boundary, which is untouched).
- Wrap `UploadPlugin` so an invalid/incompatible DLL from `PluginFactory.Assimilate` is caught and
  returned as `BadRequest` (matching its documented 400) rather than escaping as 500; return an
  explicit `NoContent()`/`Ok()`.

**E8 — admin tests.** New `AdminControllerTest.cs` (MSTest, `TestLoggerFactory.Create()`,
arrange/act/assert) pinning: save → load round-trip; `UploadPlugin` valid + invalid stream; `Status`;
`Trim`; `TabulaRasa`; and `/service` create + delete.

## 4. Acceptance criteria

- Non-integer id on AddProperty / TryRemoveProperty / TryRemoveGraphElement / the four degree getters /
  both Benchmark actions → **400** (was 500), verified.
- Bad `fullQualifiedTypeName` or null `Literal` on GraphScan / IndexScan / RangeIndexScan / AddProperty
  → **400** (was 500).
- Missing edge on GetSource/GetTarget → **404** (no thrown `WebException`); the two migrated tests
  assert 404.
- Path: uncompilable filter → **400** (with the compiler message); unknown algorithm name → **400**;
  missing source/target vertex → **404**; a genuine no-path still → **200** empty; `MaxDepth <= 0`
  still → 200 empty. The declared 400/404 are now reachable.
- Oversized `maxElements` clamped to `MaxPageSize` (the whole graph is not materialized); a negative →
  **400** — pinned for both GetGraph and GetSubGraphContents.
- Any unhandled exception → RFC 7807 ProblemDetails with the correct status and no stack leak outside
  Development — verified end-to-end through `WebApplicationFactory<Program>` (as `OpenApiDocumentTest`
  does).
- The OpenAPI document at `/openapi/v0.1.json` carries the reachable statuses (including `409` where a
  mutation can emit it) — asserted via the same E2E harness.
- New `AdminControllerTest` suite green (save/load round-trip, UploadPlugin valid+invalid, Status,
  Trim, TabulaRasa, service create/delete).
- Full suite green; the `transaction-failure-reasons` mapping tests pass **unchanged**.

## 5. Risks

- **Behavioural contract changes.** `0 → 404` on the degree getters and `empty-200 → 400/404` on the
  path endpoint change observable behaviour; clients relying on the old shape must adapt. Mitigate by
  documenting the codes in `<response>` and migrating the pinned tests deliberately (not silently).
- **Exception-handler wiring.** `UseExceptionHandler` must not mask the Development exception page;
  register it so dev keeps rich diagnostics and only non-dev emits the ProblemDetails 500.
- **Route binding vs route matching.** Typing a route param as `int` (no `:int` constraint) must still
  reach the action and 400 on a bad value rather than 404 on a route miss; the feature standardizes on
  400-for-malformed-value and a test pins it.
- **Envelope consistency.** Adding global ProblemDetails must not change the success responses or the
  already-correct rollback responses (keep their status and body); only the previously-unhandled and
  newly-validated paths gain the problem+json body.

## 6. Keep (do not regress)

- The `transaction-failure-reasons` → HTTP mapping in `GraphController.RolledBackResult`
  (`:1250-1271`) and `SubGraphController.MapFailedSubGraphCreate` (`:332-359`): `InvalidInput→400`,
  `NotFound→404`, `QuotaExceeded`/`Conflict→409`, else `→500`. Do not change the mapping — only fill
  the attribute/return-type gaps around it.
- `SubGraphController.CreateSubGraph` is already fully hardened (try/catch, `ActionResult`, up-front
  conflict/quota/missing-source checks, `409` declared, no swallow). Preserve; the only remaining item
  in that controller is E6's `GetSubGraphContents` clamp.
- The `AddEdge` missing-vertex → 404 path and its `TransactionFailureReasonTest` cases — untouched.
- The `waitForCompletion=false` fire-and-forget **202** path — unchanged regardless of eventual
  outcome.
- The engine `Try*(out result, …) : bool` "not found/invalid" pattern; single writer; lock-free reads
  over the volatile snapshot.
