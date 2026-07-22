# Unify the HTTP error envelope on ProblemDetails

Status: open (spec/plan only). Related: [api-error-contract](../../done/api-error-contract/),
[api-security-boundary](../../done/api-security-boundary/), [openapi-10](../../done/openapi-10/).
Sequencing: runs **after** [structural-decomposition](../structural-decomposition/)'s
GraphController split — the site inventory below references the pre-split file and is
regenerated then.

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

## Non-goals / revisit triggers

- No new error codes or `type` URI scheme beyond what RFC 7807 needs. *Revisit trigger:* a client asks
  for machine-readable error categories.
- Raw-value action return types (the `bool`/`uint` endpoints) are a separate concern.
- No change to the request-shape-aware dynamic-code gate or auth (those stay as-is).
