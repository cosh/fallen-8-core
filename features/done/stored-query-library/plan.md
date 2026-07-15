# Stored Query Library — Plan

Companion to [spec.md](./spec.md). Feature branch: `feature/stored-query-library`
(branch-only workflow — no GitHub issue/PR).

Ordering principle: land the engine library + registration surface first (compile-validated,
transactional, but not yet durable), then invocation, then the security-boundary rewiring —
each of those is independently testable against the previous phase. Durability (snapshot +
WAL) comes once the model is proven, packaging/docs last. Every phase ends with
`dotnet build` clean and `dotnet test` green.

## Phase 0 — Engine model + registration surface

Intent: a stored query can be registered, listed, fetched, and deleted — compiled and
transactional, volatile for now.

- [ ] `fallen-8-core/StoredQueries/`: `StoredQueryKind`, `StoredQueryDefinition`,
  `IStoredQueryCompiler`, `StoredQueryLibrary` (immutable-snapshot reads, writer-thread
  mutation, `MaxStoredQueryCount` quota, name regex `^[A-Za-z0-9_-]{1,128}$`, ordinal
  comparison). MIT headers, `Try*(out, …)` style.
- [ ] `RegisterStoredQueryTransaction` / `RemoveStoredQueryTransaction` on the single-writer
  pipeline; in-transaction re-checks map to `TransactionFailureReason`
  (Conflict / QuotaExceeded / NotFound).
- [ ] apiApp `StoredQueryCompiler : IStoredQueryCompiler` over the existing
  `CodeGenerationHelper` paths (`GeneratePathTraverser`, `TryGenerateSubGraphDefinition`);
  registered on the engine at startup next to the `SubGraphRecipeCompiler`.
- [ ] `StoredQueriesController`: `POST /storedquery` (declarative `DynamicCodePolicy` +
  sensitive rate limit + 1 MiB cap; compile-then-enqueue; 201/400-with-diagnostics/401/403/
  409/429/500), `GET /storedquery`, `GET /storedquery/{name}`, `DELETE /storedquery/{name}`
  (204/404/500 via failure-reason mapping). Full OpenAPI annotations + samples.
- [ ] `Fallen8:StoredQueries:MaxCount` config binding (default 256).
- [ ] Tests: registration validation matrix (name, kind/block mismatch, duplicate, quota),
  compile-failure diagnostics, oversize fragment rejected pre-Roslyn, list/get/delete
  round-trip, transaction failure-reason mapping.

## Phase 1 — Invocation by reference

Intent: the existing endpoints accept `storedQuery` wherever they accept inline fragments.

- [ ] `PathSpecification.StoredQuery` (+ value-equality update so the inline cache keying is
  untouched); `POST /path` resolves the name → pinned `IPathTraverser`; 400 on mixing with
  `filter`/`cost`, 404 unknown name, 400 kind mismatch, 409 `Failed` compile state.
- [ ] `SubGraphSpecification.StoredQuery`; `PUT /subgraph` instantiates the stored template
  under the per-request instance name; the created subgraph's `SpecificationJson` recipe is
  the **materialized** specification (self-contained, survives stored-query deletion).
- [ ] Invoke-during-remove safety: resolution captures the artifact reference once; test the
  race (parallel invokes vs. remove — old-artifact completion or 404, never torn).
- [ ] Tests: stored-vs-inline result equivalence for path (BLS + DIJKSTRA) and subgraph;
  mixing → 400; unknown/mismatch/Failed paths; inline caches and `PathCompileCount`
  semantics unchanged.

## Phase 2 — Security boundary rewiring (the headline)

Intent: the exact spec §3.3 kill-switch matrix, pinned in both switch states.

- [ ] Drop the endpoint-level `DynamicCodePolicy` from `POST /path` and `PUT /subgraph`
  (keep `[Authorize]`); add the imperative `IAuthorizationService` capability check applied
  iff the request carries any inline fragment — same 403 shape as today.
- [ ] Filterless path requests no longer require the switch (deliberate contract fix; update
  the endpoint remarks + OpenAPI responses accordingly).
- [ ] Registration stays declaratively gated; delete/list/get stay ungated by the switch.
- [ ] Tests: the full matrix — both switch states × {register, inline path, inline subgraph,
  stored path, stored subgraph, filterless path, list/get/delete}; 401-before-403 ordering
  preserved; controller remarks carry the trust-boundary honesty note.

## Phase 3 — Durability: snapshot manifest + WAL entries

Intent: stored queries survive Save/Load and crash+replay, symmetrically.

- [ ] `StoredQueryManifest` (+ `CoreJsonContext` source-gen types); `PersistencyFactory`
  save/load of the sidecar next to the subgraph-recipe manifest, same atomic-write +
  commit-point discipline; manifest corruption is loud.
- [ ] Load rehydrates the library and eagerly recompiles via the registered compiler;
  recompile failure keeps the entry as `Failed` + diagnostics (get shows them, invoke → 409);
  no compiler registered ⇒ source-only entries, warn once.
- [ ] `WalEntryType.RegisterStoredQuery = 14` / `RemoveStoredQuery = 15` (additive; format
  version stays 1); codec serialize/decode; commit-order replay re-executes the equivalent
  transactions with `_walSuspended` (no re-logging); replay recompile failures warn and
  continue.
- [ ] Tests: save/load round-trip (recompiled + invocable), WAL register/remove/
  register-then-remove replay, torn-tail safety, failed-recompile-on-load behaviour,
  Save↔WAL symmetry (a query survives replay iff it survives save/load), stored-template
  subgraph survives after stored-query deletion, ALC unload after delete (weak-reference
  test).

## Phase 4 — Docs + polish

- [ ] `features/open/stored-query-library/README.md`: usage walkthrough (register while the
  switch is on → run with it off), request samples for both kinds, the honesty note.
- [ ] Root `README.md` + `CLAUDE.md` architecture note: the stored-query pattern next to the
  dynamic-filter pattern; OpenAPI snapshot regenerated if the pinned contract file is in use
  by dependents (web-ui / mcp-server contract tests).
- [ ] F8 Studio / skill-library / mcp-server are **not** modified here; leave one-line
  pointers in their feature docs where they intersect (mcp read-tier `f8_find_paths` now
  literally true; a future MCP tool tier could expose stored invocation below the code tier).

## Phase 5 — Gate

- [ ] Full `dotnet test fallen-8-core.sln` green; build 0 warnings/0 errors; spec acceptance
  criteria walked through one by one on the branch.
- [ ] Council review per the repo merge gate; fix findings on the branch; `git merge --no-ff`
  to `main`; move `features/open/stored-query-library/` → `features/done/`.

## Progress

- [x] Phase 0 — engine library + transactions + registration REST surface
- [x] Phase 1 — `storedQuery` invocation on `/path` and `/subgraph`
- [x] Phase 2 — request-shape-aware kill-switch gate + full matrix tests
- [x] Phase 3 — snapshot manifest + WAL entries 14/15 + replay
- [x] Phase 4 — READMEs, doc pointers, OpenAPI snapshot
- [x] Phase 5 — council gate, merge, move to done/

## Council outcome (2026-07-15)

Three parallel reviews (correctness/concurrency, regressions/invariants, scope/spec-fidelity):
**3× APPROVE, zero blocker/major findings.** Minor findings fixed on the branch before merge:

- WAL replay now bypasses the registration quota (`RegisterStoredQueryTransaction.BypassQuota`):
  a replayed registration was quota-checked at its original commit, and recovery can run before
  the configured ceiling is applied — re-enforcing a default ceiling could silently drop
  committed operator state.
- The 201 response body uses the controller's own entry reference instead of re-reading
  `tx.Entry` (a concurrent Trim could null it via `Cleanup`).
- Both stored-query transactions release their entry references at completion
  (`ReleaseAfterCompletion`), so deleting a stored query genuinely unpins its compiled artifact
  immediately — pinned by the unload test now passing WITHOUT forcing a Trim.
- Kind parsing accepts only the literal names (`Enum.TryParse` also accepted numeric strings).
- `/path`'s storedQuery-vs-inline mutual-exclusion trigger harmonized with `/subgraph`'s
  (actual non-blank fragments, not object presence).
- Spec §3.3 updated to the as-built authentication mechanism (fallback policy; no endpoint
  `[Authorize]` attribute — a bare one would have changed the no-key posture) and the honest
  "compiles no user-supplied code" wording for filterless requests.
- Fixed a stacked XML doc comment in `WalTransactionCodec`.
- New tests: composed delete-template → Save/Load subgraph survival, corrupt-manifest
  error-log assertion, torn trailing WAL register entry, decoder never-throws probe,
  remove-then-re-register replay, case-sensitive name coexistence, numeric-kind rejection,
  switch-ON pipeline rows for stored-subgraph invocation and list/get/delete.

Accepted without a test (noted honestly): a fault-injection test forcing the stored-query
manifest write to fail mid-Save (the versioned save path is not predictable from outside;
the D6 discipline is code-identical to the reviewed subgraph-recipe path).

## Decision / revisit conditions

- **Two composite kinds only** (`Path`, `SubGraph`) — they are the only artifacts the engine
  compiles and executes; revisit per-fragment kinds when an endpoint accepts a lone fragment
  or mixing stored + inline is concretely needed.
- **No direct execute endpoint** — the existing endpoints already carry the invocation
  parameters; revisit when a kind lands they cannot express.
- **Immutable entries** (delete + re-register, no update/versioning) — revisit when the
  workflow demonstrably hurts an actual user (e.g. an F8 Studio editor).
- **Eager recompile on load, keep-and-mark on failure** — chosen over the recipe path's
  skip-with-warning because a stored query is operator-registered state, not derived state;
  silent disappearance would be data loss from the operator's perspective.
- **Filterless `/path` no longer requires the kill switch** — a deliberate, tested contract
  change made honest by the request-shape-aware gate; revert only if the imperative check
  proves fragile in review.
