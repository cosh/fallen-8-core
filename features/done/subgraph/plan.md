# Subgraph Feature — Implementation Plan

Companion to [spec.md](./spec.md). Phases are ordered by dependency and value. Each phase
lists concrete files and acceptance criteria. Deviations referenced as `KD-n` are defined
in the spec's *Known deviations* section.

## Guiding principles
- Every phase ends green: `dotnet build fallen-8-core.sln` clean and `dotnet test` passing.
- Mirror existing conventions (path-finding REST + Roslyn code-gen, MIT license headers,
  `Try*` return-value style, factory-mediated mutation via transactions).
- No change to the source graph during subgraph creation — assert it in tests.

---

## Phase 0 — Correctness fix (KD-1) ✅ prerequisite for everything

**Problem.** `BreathFirstSearchSubgraphAlgorithm.PathInfo(PathInfo)` copies the element
`HashSet` by reference, so paths that branch from a common prefix corrupt each other's
element sets and cycle detection.

**Work**
- Add a failing test on a branching graph (fan-out + diamond) in
  `fallen-8-unittest/SubGraphTest.cs`.
- Fix the copy constructor to deep-copy the set: `new HashSet<int>(anotherPath._graphElements)`.

**Acceptance**
- New branching test fails before the fix, passes after.
- All previously passing tests still pass.

---

## Phase 1 — REST API (KD-2)

**Goal.** Create, list, read, recalculate, and delete subgraphs over HTTP, consistent with
the path-finding API (code-fragment filters compiled via Roslyn).

**New REST models** (`fallen-8-core-apiApp/Controllers/Model/`)
- `SubGraphSpecification` — `name`, `additionalInformation`, `vertexFilter`, `edgeFilter`,
  `patterns: List<PatternSpecification>`.
- `PatternSpecification` — `type` (`Vertex|Edge|VariableLengthEdge`), `patternName`,
  `graphElementFilter`, `vertexFilter`, `direction`, `edgePropertyFilter`, `edgeFilter`,
  `minLength`, `maxLength`.
- `SubGraphResultREST` — name, metadata, vertex/edge counts, algorithm plugin name.

**Code generation** (`fallen-8-core-apiApp/Helper/`)
- Extend/parallel `CodeGenerationHelper` with a subgraph delegate provider
  (`ISubGraphDelegateProvider` + generated `SubGraphDelegateProvider`) that compiles the
  code fragments into `Delegates.GraphElementFilter / VertexFilter / EdgeFilter /
  EdgePropertyFilter`, then build a `SubGraphDefinition` from a `SubGraphSpecification`.
- Cache compiled providers in `GeneratedCodeCache`.

**Controller** — `fallen-8-core-apiApp/Controllers/SubGraphController.cs`, endpoints per
spec §5. Creation and deletion go through `CreateSubGraphTransaction` /
`RemoveSubGraphTransaction`; reads and recalculation call `SubGraphFactory` directly.

**Tests** — `fallen-8-unittest/SubGraphRestTest.cs`: create/list/get/recalculate/delete
happy paths, invalid-code ⇒ 400, unknown-name ⇒ 404, and spec→definition mapping.

**Acceptance**
- All endpoints reachable; specification→definition mapping unit-tested.
- Invalid code fragments return the compiler diagnostics, not a 500.

---

## Phase 2 — Extend OpenAPI documentation

**Goal.** The new endpoints appear in the generated OpenAPI document / Scalar reference
with request/response schemas and worked examples.

**Work**
- XML doc comments (`<summary>`, `<remarks>`, sample request, `<response>`) on every
  `SubGraphController` action, matching `GraphController`'s style.
- `[ProducesResponseType]` / `[Consumes]` / `[Produces]` on each action so schemas and
  status codes are emitted.
- Ensure the api project's XML documentation file is generated and fed to
  `AddOpenApi` (verify `GenerateDocumentationFile` in the csproj).

**Acceptance**
- `GET /openapi/v0.1.json` (dev) lists all subgraph paths with schemas and examples.

---

## Phase 3 — Persistence (KD-3, KD-4)

**Goal.** Registered subgraphs survive a save/load cycle.

**Work**
- Persist each registered subgraph's **definition + metadata** (name, algorithm plugin
  name, parameters, source id) in the save format handled by `PersistencyFactory`.
- Serialise filter/predicate delegates via `DelegateJson` (KD-4 integration). For
  REST-created subgraphs whose filters are compiled from code fragments, persist the
  original code fragments alongside so they can be recompiled on load.
- On load, after graph elements are restored, recalculate subgraphs in dependency order
  (root sources first) via the existing recursive recalculation.

**Tests** — `fallen-8-unittest`: create → save → new `Fallen8` → load → assert the
recalculated subgraph matches; `DelegateJson` round-trips for each `Delegates.*` type used
by patterns.

**Acceptance**
- Save/load round-trip reproduces registered subgraphs and their contents.

---

## Phase 4 — Algorithm hardening & docs (KD-5, KD-6)

**Work**
- Tighten `ValidatePattern` to reject sequences that end on an edge pattern (KD-5).
- Decide and document variable-length intermediate-vertex semantics (KD-6); add a test
  pinning the chosen behaviour.
- Replace bare `throw new NotImplementedException()` in the algorithm with descriptive
  exceptions.
- Author `features/subgraph/README` usage notes / link from top-level `README.md`.

**Acceptance**
- Invalid pattern sequences are rejected with a clear reason; behaviour documented.

---

## Phase 5 — Principal-architect review & gap closure

- Independent review of the algorithm correctness, REST/DI/versioning consistency, and
  persistence/serialization design.
- Triage findings; fix all confirmed correctness/security issues and consistency gaps.
- Re-run build + full test suite; update this plan's checklist with outcomes.

---

## Status checklist
- [x] Phase 0 — correctness fix + branching tests
- [x] Phase 1 — REST API
- [x] Phase 2 — OpenAPI documentation
- [x] Phase 3 — persistence (recipe-based; DelegateJson **not** used — see below)
- [x] Phase 4 — hardening + docs
- [x] Phase 5 — architect review + gap closure

## Phase 5 — review outcomes

Three independent principal-architect reviews (algorithm correctness, REST/API + security,
architecture + persistence) ran against the implemented feature. Confirmed findings were
fixed with regression tests:

**Correctness**
- **Variable-length range dropped short paths (critical).** A `Min < Max` range behaved
  like a fixed `Max`, and a terminal filter matching only a short path returned an empty
  subgraph. Fixed by accumulating independent per-length copies.
- **Shared path element set (critical, found in Phase 0).** Deep-copy of the path element
  set.
- **Cached algorithm ignored the requested source.** A plugin-cache hit now re-binds the
  algorithm to the source; recalculation runs sequentially to avoid racing the shared
  instance.
- **Leading variable-length edge** silently ignored `Min/Max`; now rejected.
- Pattern sequences ending in an edge are rejected; a cycle-closed path no longer mutates
  its element set.

**REST / resource safety**
- Compiled filter providers are cached by generated source (no recompile / assembly leak
  for identical filter sets).
- `maxLength` is capped; unknown edge directions are rejected instead of silently
  defaulting.
- A failed registration returns a null result, so a losing concurrent create can't report
  success or have its rollback delete the winner.
- `CreateSubGraph` is exception-guarded; the recalculate re-fetch race returns 404.

**Persistence — DelegateJson could not be used.** The review established that `DelegateJson`
cannot round-trip the feature's filter delegates (they are Roslyn-compiled lambdas or
closures, not named static methods). Persistence therefore stores a **recipe** (metadata +
the opaque specification text) and recompiles on load via `ISubGraphRecipeCompiler`. This
supersedes the original plan's DelegateJson approach.

## Known limitations (documented, not fixed)
- ~~**Nested subgraph recalculation is not wired.**~~ Resolved by the
  [nested-subgraph-recalculation](../nested-subgraph-recalculation/) feature —
  `TryCreateSubGraphFromSource` registers nested subgraphs with the correct source and
  `RecalculateAllSubGraphs` refreshes the whole dependency tree in order.
- **Only root-level subgraphs are persisted**, and only those created from a specification
  (delegate-only subgraphs have no recipe). Rehydrated subgraphs are recomputed, so they
  reflect the graph at load time and receive new ids.
- **Runtime-compiled assemblies are not collectible.** Like the existing path API, compiled
  filter assemblies load into the default context and are not unloaded; the provider cache
  bounds growth for repeated identical filter sets but not for unbounded distinct ones.
- **No ceiling on subgraph count / total materialized size.** Creation is authenticated the
  same as the rest of the API; deployments that expose it publicly should add quotas.
