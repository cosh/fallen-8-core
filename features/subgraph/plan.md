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
- [ ] Phase 0 — correctness fix + branching test
- [ ] Phase 1 — REST API
- [ ] Phase 2 — OpenAPI documentation
- [ ] Phase 3 — persistence + DelegateJson integration
- [ ] Phase 4 — hardening + docs
- [ ] Phase 5 — architect review + gap closure
