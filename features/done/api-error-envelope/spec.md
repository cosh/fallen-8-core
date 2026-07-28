# Unify the HTTP error envelope on ProblemDetails

Status: **done** — implemented on `feature/api-error-envelope`. Related:
[api-error-contract](../api-error-contract/), [api-security-boundary](../api-security-boundary/),
[openapi-10](../openapi-10/). Followed [structural-decomposition](../structural-decomposition/)'s
GraphController split (merged 2026-07-22); the migration ran against the partial-class files.

## Delivered

- `Helper/ProblemResults.cs` gained one-for-one wrappers — `BadRequest(detail)`, `NotFound(detail)`,
  `Conflict(detail)`, `InternalServerError(detail)`, and the general `StatusCode(status, detail)` —
  each an RFC 7807 `application/problem+json` `ObjectResult` whose `title` is the status reason phrase
  and whose `detail` is the former human message. `Create(...)` stays for the bespoke-title callers
  (the embedding-provider 502/503).
- Every plain-string / `StatusCode(code, string)` error return now flows through `ProblemResults`,
  across **all** error-returning controllers — the actual surface was wider than the pre-split
  inventory below: the Graph\* partials, SubGraph, Embedding, StoredQueries, Analytics, **Plugins**,
  Admin, Benchmark, **Chat**, Delegates and SaveGames — plus the two error-producing helpers
  (`StoredQueryResolver`, `SemanticTraversalHelper`, which built `ConflictObjectResult`/
  `BadRequestObjectResult`/`new ObjectResult{StatusCode=…}` directly). Status codes are unchanged;
  only the body shape becomes uniform. `RolledBackResult`'s reason→status mapping is kept, its bodies
  routed through `ProblemResults.StatusCode`.
- A shared test helper `fallen-8-unittest/ProblemAssert.cs` (`AssertProblem`, asserting a unit-level
  action result is a problem+json `ObjectResult` with the expected status and optional `detail`
  substring) DRYs the assertion churn; the migrated controller tests assert the envelope through it.
  `StatusCodeOf`-style assertions were left as-is (they already read
  `IStatusCodeActionResult.StatusCode`, which the new `ObjectResult` still satisfies).
- Full suite green (1041 passed, 0 failed). The OpenAPI snapshot is **unchanged**: the type-less
  `[ProducesResponseType(4xx/5xx)]` attributes already advertised `ProblemDetails` under
  `AddProblemDetails()`, so the runtime now matches the doc it always published.

## Motivation

The REST surface returns errors in **three different shapes** today, which the code-health-sweep
review surfaced:

1. **Plain-string bodies** via `BadRequest("...")` / `NotFound("...")` / `Conflict("...")` /
   `StatusCode(code, "...")` — the majority, concentrated in `GraphController` and `SubGraphController`.
2. **RFC 7807 `application/problem+json`** via `Helper/ProblemResults.Create(...)` — used by the
   newer `EmbeddingController` (502/503) and the transaction rolled-back path.
3. **Raw values / framework defaults** — a few actions return bare `bool`/`uint` or rely on the
   global handler.

`AddProblemDetails()` is already registered in `Program.cs`, so the framework is ready to serve
problem+json everywhere — but the explicit string bodies bypass it. A client cannot rely on a single
error contract, and the OpenAPI doc advertises `ProblemDetails` responses the string endpoints don't
actually return.

This is **not a sweep-sized change**: there are **134 error-return sites across 9 controllers**
(`GraphController` alone has 53), and nearly every controller test asserts an error body or status,
so the migration carries heavy, cross-cutting test churn. It is captured here as its own feature so it
can be done coherently and verified as a unit, rather than piecemeal.

## Goal

Every error response from the REST API is `application/problem+json` (RFC 7807) with a consistent
shape: `type`/`title`/`status`/`detail` (plus the existing extension members where already used). The
HTTP status codes themselves do **not** change — only the body shape becomes uniform. `ProblemResults`
becomes the single home through which all error responses flow.

## Scope (the 134 sites)

| Controller | Error-return sites | Notes |
|---|---|---|
| GraphController | 53 | the main offender; plain-string BadRequest/NotFound + `RolledBackResult`'s `StatusCode(code, string)` |
| SubGraphController | 22 | plain-string BadRequest |
| EmbeddingController | 19 | already partly ProblemDetails — reconcile the plain-string BadRequests |
| StoredQueriesController | 16 | plain-string |
| AnalyticsController | 14 | plain-string |
| Admin / Bulk / SaveGames / Delegates / Benchmark | ~10 | remainder |

## Behavior contract

- **Status codes unchanged.** A request that got 400/404/409/500 still gets the same code; only the
  body shape changes (string → problem+json). This is the single observable change, and it is uniform.
- `RolledBackResult` (GraphController) already maps `TransactionFailureReason` → 400/404/409/500 — keep
  the mapping, route its bodies through `ProblemResults`.
- The `title`/`detail` split: the existing human string becomes `detail`; `title` is a short, stable
  category per status (e.g. "Bad Request", "Not Found").
- Raw-value actions (`bool`/`uint`) are out of scope here (they are a separate return-type decision).

## Plan (phased, test-churn-aware)

1. **Helper convenience methods** — add `ProblemResults.BadRequest(detail)`, `.NotFound(detail)`,
   `.Conflict(detail)` wrappers so each call site is a one-for-one swap (`return BadRequest(x)` →
   `return ProblemResults.BadRequest(x)`), keeping diffs mechanical and reviewable.
2. **One controller at a time**, smallest first (Delegates/SaveGames/Benchmark → Analytics → Stored →
   Embedding → SubGraph → Graph), each with its test updates, each landing green. This keeps every
   step shippable and the review tractable.
3. **A shared test assertion helper** (`AssertProblem(response, status, detailContains)`) so the test
   churn is itself DRY and consistent.
4. **Regenerate the OpenAPI snapshot** once at the end (the `[ProducesResponseType]` bodies become
   `ProblemDetails` uniformly) and reconcile the doc.

## Impact on existing features

Sweep across the layers this feature touches (mandatory cross-feature check):

- **Engine** — no change. The error contract lives entirely in the REST layer; the engine's
  `Try*(out …) : bool` pattern and the transaction reason channel are untouched.
- **REST contract / OpenAPI snapshot** — the observable change is body shape only (string →
  problem+json); status codes are identical. `features/done/web-ui/openapi-v0.1.json` is byte-unchanged
  because the type-less `[ProducesResponseType]` sets already mapped to `ProblemDetails`.
- **Studio UI (`fallen-8-web-ui`)** — regression caught and fixed. `api/client.ts`'s `ApiError`
  rendered the raw response body (`ErrorBox` shows `error.body`; the message embeds it), so an error
  would have surfaced as raw problem+json JSON. `ApiError` now extracts the problem+json `detail`
  (falling back to `title`, then the raw body) via `problemDetail(...)`, so the displayed text is
  **identical to before** the envelope change. No screenshots change (rendered error text is the same
  string). The full web-ui vitest suite passes (490 tests); `bulk-errors.test.ts` was updated to mock
  problem+json and pin the extraction, with a plain-string case retained for the pass-through.
- **MCP server (`fallen-8-mcp`)** — no change needed, net improvement. `Bridge/Fallen8RestClient.MapErrorAsync`
  already handled **both** problem+json (→ title/detail) and plain-string bodies (→ detail), so agents
  now get consistent structured errors. No REST operation was added, so `McpRestCoverageTest` /
  `McpContractTest` are unaffected.
- **NL-assist dataset / eval** — unaffected. The fine-tune corpus is about generating filter/cost
  code, not about HTTP error body shapes; no `RETRAIN-LOG.md` entry is warranted.
- **Docs site** — the [rest-api](../../../docs/src/content/docs/rest-api.mdx) "Errors" convention row
  was tightened to state the uniform envelope; the link-checked docs build stays green.
- **Architecture diagrams** — unaffected; no new channel, deployable, or layer boundary.
- **Persisted recipes / stored queries** — unaffected; only the *error* responses of the stored-query
  resolution paths changed shape (via `StoredQueryResolver`), not the stored artifacts.

## Non-goals / revisit triggers

- No new error codes or `type` URI scheme beyond what RFC 7807 needs. *Revisit trigger:* a client asks
  for machine-readable error categories.
- Raw-value action return types (the `bool`/`uint` endpoints) are a separate concern.
- No change to the request-shape-aware dynamic-code gate or auth (those stay as-is).
