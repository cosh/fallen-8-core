# Structured Transaction Failure Reasons — Specification

> **Status:** Planned. Follow-up consolidating the API-correctness items deferred across
> `correctness-fixes-followups` (§5) and flagged by the engine-performance / persistence councils:
> client-caused rollbacks currently surface as `500`, quota rejections use inconsistent statuses,
> and two response-doc mismatches remain.

## 1. Problem

The transaction layer collapses every mutation failure into `TransactionState.RolledBack` (+ the
`TransactionInformation.Error` exception added in correctness-fixes-followups). So a controller that
waited on a rolled-back mutation can only tell a *genuine internal fault* (Error != null → 500) from
a *clean* rollback — it cannot tell a **client-caused** clean rollback (a referenced vertex doesn't
exist; a quota was exceeded; a name conflicts) from an internal one, so those wrongly become `500`.
Concretely today:

- `POST /edge` referencing a non-existent vertex → the worker faults/returns false → **500** (should
  be a `4xx`; the client referenced something that isn't there).
- `PUT /subgraph` exceeding the per-subgraph/total **element** quota → clean rollback → **400**, while
  the up-front **count** ceiling returns **409** — two quota breaches, two statuses.
- The five `GraphController` mutations return `202 Accepted` on the fire-and-forget path but their
  `[ProducesResponseType]`/`<response>` docs still say **204** (pre-existing doc drift).
- `PUT /subgraph`: an *empty graph* yields **400** (the copy stage produces a clean `false`) while a
  *populated graph whose pattern matches nothing* yields **201 with an empty subgraph** — inconsistent
  "no-match" contract.

## 2. Design

Introduce a structured **failure reason/category** the transaction layer sets when it rolls back for
a *known* reason, and have controllers map it to the correct HTTP status.

- **`TransactionFailureReason` enum** (e.g. `None`, `InternalError`, `InvalidInput`, `NotFound`,
  `QuotaExceeded`, `Conflict`) exposed on `TransactionInformation` alongside `TransactionState` /
  `Error` (set in place on the caller's instance, under the same happens-before as those — see the
  B6/engine-perf work).
- **Engine mutations communicate WHY they fail** rather than throwing or silently returning false.
  Notably `CreateEdge(s)` must DETECT a missing/removed referenced vertex and fail cleanly with
  `NotFound` (not let the master-store indexer throw); `CreateSubGraph` sets `QuotaExceeded` on a
  post-materialization quota breach and `InvalidInput` on a structurally-invalid pattern; a genuine
  unexpected exception stays `InternalError` (Error != null).
- **Controller mapping** (both `GraphController.RolledBackResult` and `SubGraphController`):
  `InvalidInput → 400`, `NotFound → 404`, `QuotaExceeded → 409` (Conflict — one status for ALL quota
  breaches, replacing the 400/409 split), `Conflict → 409`, `InternalError`/`Error` → `500`. A
  reason of `None` on a rolled-back tx (shouldn't happen for a known path) defaults to `500`.

### Folded-in corrections (cohesive, same controllers)

- **202 vs 204:** the five `GraphController` mutations (`AddVertex`/`AddEdge`/`AddProperty`/
  `TryRemoveProperty`/`TryRemoveGraphElement`) return `Accepted()` (**202**). Update their
  `[ProducesResponseType]` + `<response>` docs from 204 → **202** so OpenAPI matches reality. (Do not
  change the returned code — align the docs to it.)
- **Subgraph no-match consistency:** make the two "valid pattern, no subgraph produced" paths
  consistent. Preferred contract: a syntactically-valid pattern that simply matches nothing is a
  valid **empty result → 201** (empty subgraph) in BOTH the empty-graph and populated-graph cases;
  reserve `400` for a structurally-invalid/uncompilable pattern and the reason-mapped statuses for
  quota/fault. If making empty-graph return 201-empty is disproportionate, the acceptable fallback is
  both → `400` "no valid subgraph produced" — but the two cases MUST end up identical and documented.

  **Decision (implemented): the preferred contract.** The BFS algorithm now (a) validates the
  pattern STRUCTURE up front — before touching the source graph — so a structurally-invalid pattern
  is a clean `false` (⇒ `InvalidInput` ⇒ `400`) whether the graph is empty or populated; and (b)
  when the vertex-copy stage yields zero vertices (empty source graph, or a top-level vertex filter
  that matched nothing), returns the **empty subgraph** (`true`) instead of `false`. So the
  empty-graph and populated-no-match cases are now IDENTICAL — both **201 with an empty subgraph** —
  pinned by `Create_OnEmptyGraph_Returns201WithEmptySubGraph` and
  `Create_WhenPatternMatchesNothingOnPopulatedGraph_Returns201`. Structurally-invalid patterns stay
  `400`; quota breaches are `409`.

## 3. Acceptance criteria

- `POST /edge` with a non-existent source/target vertex returns a `4xx` (NotFound/InvalidInput), not
  `500`, and does not throw inside the worker; a genuine internal fault still returns `500`.
- All subgraph quota breaches (count ceiling AND element ceilings) return ONE consistent status
  (`409`), documented; a genuine fault still `500`; a structurally-invalid pattern still `400`.
- The five GraphController mutation docs declare `202` (matching the returned `Accepted()`); the
  fire-and-forget vs waited behaviour is unchanged.
- Empty-graph and populated-no-match subgraph creates return the SAME status (per the chosen
  contract), pinned by tests.
- Existing tests that asserted the OLD (incorrect) codes are MIGRATED to the new correct codes (this
  is the point of the feature), not deleted; the genuine-fault→500, invalid-pattern→400, and
  duplicate-name→409 paths still hold. Full suite green.
- Single-writer + the transaction lifecycle (WaitUntilFinished, B6 Error/state observability,
  memory-footprint M3 input release, the WAL append) are all unaffected.

## 4. Non-goals

- Changing the transaction model or the REST route/versioning.
- A general problem+json error envelope (the responses keep their current body shape; only the
  status code + a clear message change).
- **Not-found → 4xx for the remove/property mutations (deliberate boundary).** This feature routes
  the *edge-create* client-cause (a missing/removed referenced vertex → `NotFound` → 404) and the
  subgraph reasons through the new channel, but leaves `TryRemoveGraphElement`/`TryRemoveProperty`/
  `AddProperty` as-is: an **out-of-range** element id still throws `ArgumentOutOfRangeException` →
  `InternalError` → **500** (a load-bearing B6 test pins this throw), and an **in-range but
  absent/removed** id is a silent no-op → **202** (idempotent-delete-style; the remove transaction
  does not distinguish "removed nothing"). Making these return 404 is a genuine API-design choice
  (404-on-missing vs idempotent-2xx), not a clear bug, and it would require non-throwing not-found
  detection plus migrating the B6 tests — so it is intentionally deferred. Their `400` responses are
  binding/validation errors only, never "not found".
