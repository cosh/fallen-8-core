# Path Filter Arity Fix — Specification

> **Status:** Planned — P1 functional bug, from the 2026-07 principal-architect & performance
> review. The shipped default `/path` filters do not compile against their own delegate signatures,
> so a `/path` query that carries a filter block silently returns "no paths"; reconcile the contract.

## 1. Problem / current state

The dynamic path filters are compiled C# fragments (per the CLAUDE.md pattern) that must match the
delegate signatures in `Delegates.cs`. Two of the three edge-side signatures take **one** argument:

- `Delegates.cs:41` — `public delegate bool EdgePropertyFilter(String edgePropertyId);`
- `Delegates.cs:55` — `public delegate bool EdgeFilter(EdgeModel edge);`
- `Delegates.cs:62` — `public delegate bool VertexFilter(VertexModel vertex);` (one arg — the correct shape)

But the REST DTO ships **two-argument** default fragments for the two edge filters:

- `PathFilterSpecification.cs:76,80` — `EdgeProperty` `[DefaultValue]` + field initializer are `"return (p,d) => true;"`.
- `PathFilterSpecification.cs:118,122` — `Edge` `[DefaultValue]` + field initializer are `"return (e,d) => true;"`.
- `PathFilterSpecification.cs:97,101` — `Vertex` is `"return (v) => true;"` — **one** arg, correctly matching `VertexFilter`. This asymmetry is the tell: the vertex default was written to match; the edge defaults were not.

Every `<remarks>`/`<example>` doc snippet reinforces the wrong forms: `PathFilterSpecification.cs:54,56,69-73,111-115`, the `PathSpecification.cs:44,46` example block, and the `GraphController.cs:978-979` request sample all use `(p,d)` / `(e,d)`.

`CodeGenerationHelper.GenerateMethodSyntax` (`CodeGenerationHelper.cs:187-205`) emits the fragment
**verbatim** into a method typed as the target delegate — e.g. `public Delegates.EdgeFilter EdgeFilter() { return (e,d) => true; }` — with **no arity adjustment**. Assigning a two-parameter lambda to a one-parameter delegate is a hard Roslyn compile error.

`CalculateShortestPath` swallows that failure: on a null traverser it logs the compiler message and
returns the pre-initialized empty list (`GraphController.cs:1020-1030`), and the outer `catch`
returns the same empty list (`GraphController.cs:1066-1072`). So any `/path` request that **sends a
filter block** while leaving `edgeFilter`/`edgePropertyFilter` at their defaults — including a bare
`"filter": {}` — compiles nothing, and **silently returns `200 OK` with `[]`**. (A request that
omits `filter` entirely is fine: `CreateSource` gates on `definition.Filter != null` at
`CodeGenerationHelper.cs:149` and emits `return null;` for a null fragment, which compiles to a
match-all null delegate. The bug is specifically triggered by a *present* filter object.)

The endpoint already declares `[ProducesResponseType(StatusCodes.Status400BadRequest)]`
(`GraphController.cs:994`) for an "Invalid path specification", but that 400 is **unreachable** — the
method's return type is `List<PathREST>` (`GraphController.cs:996`), so a compile failure can only
become a misleading `200`-empty, never the documented `400`.

Why the suite doesn't catch it: `PathTest.cs` and `WeightedDijkstraPathTest.cs` never drive
`CodeGenerationHelper.GeneratePathTraverser`; they hand-build a `MockPathTraverser` whose delegates
are correct **one-arg** lambdas (`PathTest.cs:61,66,75`) and assign them straight onto a
`ShortestPathDefinition`. The only codegen tests that compile real fragments are the subgraph ones,
which correctly use one-arg forms (`SubGraphCodeGenerationTest.cs`). Nothing exercises the shipped
`PathFilterSpecification` defaults through Roslyn — so the broken contract is invisible.

The engine confirms one-arg is the real contract: every filter invocation passes exactly one
argument — `PathHelper.GetValidEdges` (`edgepropertyFilter(edgeContainer.Key)` `:64`,
`edgeFilter(aEdge)` `:76`, `vertexFilter(...)` `:81`), and the BLS frontier walkers
(`BidirectionalLevelSynchronousSSSP.cs:636,646,652`). Direction is chosen **structurally** (which
adjacency list is walked / which endpoint is taken), never handed to the predicate.

## 2. Goals / non-goals

**Goals**

- A `/path` request using the **default** filters (including `"filter": {}`) returns the correct
  paths, not an empty list.
- The shipped `PathFilterSpecification` defaults compile end-to-end through
  `GeneratePathTraverser` in a unit test, so this can never regress silently.
- Every `[DefaultValue]`, field initializer, and doc `<example>`/`<remarks>` snippet matches the
  real delegate arity.
- A genuinely malformed filter fragment surfaces as `400` with the compiler diagnostics, instead of
  a silent `200`-empty (the already-declared-but-unreachable 400 becomes reachable). Coordinates
  with `api-error-contract`.

**Non-goals**

- Changing the read model or the transaction model — `/path` stays a lock-free read over the
  snapshot; nothing here goes through the writer.
- Adding **direction-aware** filtering (Option B below). No call site wants a direction argument;
  see §3.
- The per-request `GeneratedCodeCache` miss (`engine-performance` P1) — orthogonal; not touched
  here.
- The cost delegates (`EdgeCost(EdgeModel)`, `VertexCost(VertexModel)`) — already one-arg and their
  `(e)` / `(v)` doc examples already match; left alone.

## 3. Design sketch

**Decision: Option A — reconcile the DTO defaults and docs *down* to the real one-arg signatures.**
Recommended over Option B (extend the delegates with a `Direction` argument) because:

- Every call site invokes the filters with exactly one argument (`PathHelper.cs:64,76,81`;
  `BidirectionalLevelSynchronousSSSP.cs:636,646,652`; `WeightedDijkstraShortestPath.cs:201-203`
  read the same delegate types). Direction is already resolved structurally by which adjacency list
  is traversed — the predicate never needs it.
- The vertex filter is already one-arg and correct; Option B would either leave a `(v)` vs
  `(e, d)` asymmetry or force a gratuitous churn of the vertex signature too.
- Option B means changing `Delegates.EdgeFilter`/`EdgePropertyFilter`, every BLS/Dijkstra call
  site, `PathHelper`, the codegen return-type strings, and the mock — a wide blast radius to add a
  parameter no code consumes. Only pursue B if direction-aware filtering becomes a genuine product
  requirement (see the revisit condition in the plan).

**Concrete changes (Option A):**

- `PathFilterSpecification.cs`: `EdgeProperty` default `"return (p,d) => true;"` → `"return (p) => true;"`
  (both the `[DefaultValue]` at `:76` and the initializer at `:80`); `Edge` default
  `"return (e,d) => true;"` → `"return (e) => true;"` (`[DefaultValue]` `:118`, initializer `:122`);
  rewrite the `<remarks>` example bullets and `<example>` tags (`:54,56,69-73,111-115`) from
  `(p,d)`/`(e,d)` to `(p)`/`(e)`. `Vertex` is left unchanged (already correct).
- `PathSpecification.cs:44,46`: the `<example>` JSON block `(p,d)`/`(e,d)` → `(p)`/`(e)`.
- `GraphController.cs:978-979`: the request-sample `edgeFilter`/`edgePropertyFilter` fragments →
  one-arg.
- `CodeGenerationHelper.cs` is left as-is — it correctly emits the fragment verbatim; once the
  fragments are one-arg they compile against the one-arg delegates. (No arity-rewriting magic is
  introduced; the fix is in the source of truth, the fragment strings.)

**Surface compile failures as 400** (coordinated with `api-error-contract`): change
`CalculateShortestPath` to return `ActionResult<List<PathREST>>`. When `GeneratePathTraverser`
returns a non-null compiler message (traverser is null), return `BadRequest(compilerMessage)` — the
already-declared 400 becomes reachable and carries the diagnostics. A **successful** compile that
simply finds no path still returns `200` with `[]` (a genuine empty result is not an error). The
outer `catch` keeps mapping unexpected exceptions, but a compile failure is now a first-class 400,
not a swallowed empty. This is the same "a client-caused failure deserves a 4xx, not a misleading
success" principle that `transaction-failure-reasons` applied to mutations; here it applies to the
read-side compile step.

**Regression guard (the point of the fix):**

- A codegen test in the spirit of `SubGraphCodeGenerationTest`: build a `PathSpecification` whose
  `Filter` is a **default** `PathFilterSpecification`, call
  `CodeGenerationHelper.GeneratePathTraverser(out traverser, definition)`, assert the returned
  compiler message is `null` and the traverser is non-null, and invoke each produced delegate
  (`EdgePropertyFilter()("x")`, `EdgeFilter()(edge)`, `VertexFilter()(vertex)`) to prove they bind
  and run. Add a representative **custom** one-arg filter case too (e.g.
  `"return (e) => e.Label == \"knows\";"`) and a **malformed** fragment case asserting a non-null
  compiler message.
- A controller-level test: a `/path` call with default filters over a small graph returns a
  non-empty result where a path exists; a malformed filter fragment yields `400`.

## 4. Acceptance criteria

- A `/path` request with a present-but-default filter block (and `"filter": {}`) returns the correct
  paths on a graph where a path exists — not `[]`.
- `GeneratePathTraverser` compiles the shipped `PathFilterSpecification` defaults with a `null`
  compiler message and a non-null traverser, pinned by a unit test that also invokes the resulting
  delegates; a representative custom one-arg filter compiles too.
- Every `[DefaultValue]`, field initializer, and doc example for `edgeFilter`/`edgePropertyFilter`
  uses the one-arg form and matches `Delegates.EdgeFilter`/`EdgePropertyFilter`.
- A malformed filter fragment returns `400` with the Roslyn diagnostics in the body (the
  `[ProducesResponseType(400)]` is now reachable); a compile that finds no path still returns
  `200`-empty.
- Full suite green; the `MockPathTraverser`-based path/Dijkstra tests are unaffected.

## 5. Risks

- **Return-type change on the endpoint.** Switching `CalculateShortestPath` to
  `ActionResult<List<PathREST>>` must keep the `200` success body byte-identical (a bare
  `List<PathREST>`), so existing 200 consumers and the OpenAPI `200` shape are unchanged; only the
  error path gains a real `400`. Verify against `OpenApiDocumentTest`.
- **Client fragments already written to the two-arg form.** Any caller who copied the old `(e,d)`
  docs will now get a `400` instead of a silent `200`-empty. That is strictly better (a loud,
  diagnosable error vs. silent wrong results), and it is the correct contract, but it is a
  behaviour change worth noting in the PR.
- **Cache interaction.** With the defaults now compiling, the traverser is cached where before it
  never was; benign, and independent of the `engine-performance` P1 per-request-cache issue.

## 6. Keep (do not regress)

- The `Filter == null` → match-all path (`CodeGenerationHelper.cs:149`) must keep working: omitting
  the filter block still runs an unfiltered traversal.
- The verbatim-fragment codegen (`GenerateMethodSyntax`) stays verbatim — do **not** add lambda
  rewriting; the fix belongs in the fragment source of truth, not in a codegen transform.
- One-arg is the whole-engine contract: `PathHelper.GetValidEdges`, the BLS frontier walkers, and
  the Dijkstra step expansion all invoke the filters with one argument — no call site is touched.
- The `MockPathTraverser` tests already use correct one-arg lambdas; they must stay green unchanged.
- A genuine no-path result stays `200` with `[]` — only a compile failure becomes `400`.
