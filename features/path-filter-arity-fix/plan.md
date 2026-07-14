# Path Filter Arity Fix — Plan

Companion to [spec.md](./spec.md). This is a functional-correctness fix (P1), not a performance
change, so the Phase 0 guardrail is a **failing characterization test**, not an opt-in benchmark.
Contract reconciliation first, then the 400 surfacing, then the regression net.

GitHub issue: to be opened (label: `feature`). Branch: `feature/path-filter-arity-fix`.

## Phase 0 — Baseline & guardrails (prove the bug, red)
Intent: a test that fails on `main` for exactly this reason, so the fix is provable and can't
regress. No benchmark/`[Ignore]` — the defect is functional, not a perf regression.
- [ ] Add a codegen test (new `PathCodeGenerationTest.cs`, mirroring `SubGraphCodeGenerationTest`)
  that builds a `PathSpecification` with a **default** `PathFilterSpecification`, calls
  `CodeGenerationHelper.GeneratePathTraverser(out var traverser, definition)`, and asserts the
  compiler message is `null` and `traverser` is non-null. Confirm it **fails today** (compiler
  message is non-null: the `(e,d)`/`(p,d)` lambdas won't bind to the one-arg delegates).
- [ ] Add a controller-level characterization test in `GraphControllerTest` (or a new file):
  `/path` over a tiny known graph with `"filter": {}` returns `[]` today — capture the wrong
  behaviour so the fix flips it to a real path.

## Phase 1 — Reconcile the contract (defaults + docs, Option A)
Intent: make the fragment source of truth one-arg, matching `Delegates.EdgeFilter` / `EdgePropertyFilter`.
- [ ] `PathFilterSpecification.cs`: `EdgeProperty` `[DefaultValue]` (`:76`) and initializer (`:80`)
  `"return (p,d) => true;"` → `"return (p) => true;"`.
- [ ] `PathFilterSpecification.cs`: `Edge` `[DefaultValue]` (`:118`) and initializer (`:122`)
  `"return (e,d) => true;"` → `"return (e) => true;"`.
- [ ] `PathFilterSpecification.cs`: rewrite the `<remarks>` bullets and `<example>` tags
  (`:54,56,69-73,111-115`) from `(p,d)`/`(e,d)` to `(p)`/`(e)`. Leave the `Vertex` `(v)` forms
  (already correct).
- [ ] `PathSpecification.cs:44,46`: `<example>` JSON `edgePropertyFilter`/`edgeFilter` → one-arg.
- [ ] `GraphController.cs:978-979`: request-sample fragments → one-arg.
- [ ] Do **not** touch `CodeGenerationHelper.GenerateMethodSyntax` — verbatim emission is correct
  once the fragments are one-arg.
- [ ] Phase 0's codegen test now passes (defaults compile, traverser non-null).

## Phase 2 — Surface compile failures as 400 (controller)
Intent: a real malformed fragment becomes a diagnosable `400`, not a silent `200`-empty; a genuine
no-path stays `200`-empty. Coordinates with `api-error-contract`.
- [ ] Change `CalculateShortestPath` (`GraphController.cs:996`) return type to
  `ActionResult<List<PathREST>>`.
- [ ] When `GeneratePathTraverser` yields a non-null compiler message (null traverser), return
  `BadRequest(compilerMessage)` instead of the empty list (`GraphController.cs:1026-1030`).
- [ ] Keep the success path returning the bare `List<PathREST>` (200 body unchanged); a successful
  compile with no path found still returns `200` with `[]`.
- [ ] Confirm `[ProducesResponseType(200)]` / `[ProducesResponseType(400)]` (`:993-994`) now match
  reality; adjust `<response>` remarks if wording drifts.

## Phase 3 — Regression tests
Intent: pin the corrected contract end-to-end.
- [ ] Extend `PathCodeGenerationTest`: a representative custom one-arg filter
  (`"return (e) => e.Label == \"knows\";"`) compiles and its delegate filters correctly; a
  malformed fragment (e.g. the old `(e,d)` form, or `"return (e) => e.Nope;"`) returns a non-null
  compiler message.
- [ ] Controller test: `/path` with default filters over a small graph returns the expected
  non-empty path; the Phase 0 `"filter": {}` characterization now returns a path, not `[]`.
- [ ] Controller test: a malformed filter fragment yields `400` carrying the diagnostics; a valid
  filter with no reachable path yields `200`-empty.
- [ ] Run the full suite (`MockPathTraverser`-based `PathTest`/`WeightedDijkstraPathTest`,
  `OpenApiDocumentTest`) — all green, 200-body and OpenAPI shapes unchanged.

## Measure & document
Intent: close the loop honestly.
- [ ] Note in the PR that callers using the old two-arg `(e,d)` docs will now receive a `400` with a
  clear compiler message (previously a silent `200`-empty) — a deliberate, better contract.
- [ ] Confirm no new allocations/paths on the hot read path (the change is fragment-string + a
  return-type widening); no benchmark needed for a functional fix.
- [ ] Cross-link `weighted-shortest-paths` (same delegates, same fix benefits Dijkstra) and
  `api-error-contract` (the 400 surfacing) from the issue/PR.

## Progress
- [ ] Phase 0 — failing codegen + controller characterization tests (bug proven red)
- [ ] Phase 1 — one-arg defaults + docs reconciled (Option A); codegen test green
- [ ] Phase 2 — compile failure → 400 with diagnostics; genuine no-path stays 200-empty
- [ ] Phase 3 — custom-filter + malformed-filter + default-filter regression tests; full suite green
- [ ] Measure & document — PR note on the behaviour change; cross-links

## Decision / revisit condition
Option A (one-arg reconciliation) is chosen because no call site threads a `Direction` into the
filter predicates — direction is resolved structurally by the traversal. **Revisit Option B**
(extend `Delegates.EdgeFilter`/`EdgePropertyFilter` to `(EdgeModel, Direction)` /
`(String, Direction)`, updating `PathHelper`, the BLS/Dijkstra call sites, the codegen return-type
strings, and the mock) **only if direction-aware edge filtering becomes a genuine requirement** —
e.g. a caller needs to accept an edge on the outgoing pass but reject it on the incoming pass. Until
then, B is gratuitous churn to add a parameter no code consumes.
