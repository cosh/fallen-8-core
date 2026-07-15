# CLAUDE.md

Guidance for working in this repository.

## What this is

Fallen-8 is an in-memory graph database written in C# (.NET 10). Namespaces are under
`NoSQL.GraphDB.*`. The solution has three projects:

- **`fallen-8-core`** — the engine: graph model, transactions, indices, algorithms
  (path finding, subgraph), persistence, serialization, plugins.
- **`fallen-8-core-apiApp`** — ASP.NET Core Web API exposing the engine over REST.
  OpenAPI via `Microsoft.AspNetCore.OpenApi`; interactive docs via Scalar.
- **`fallen-8-unittest`** — MSTest test suite covering both projects.

## Build & test

```bash
dotnet build fallen-8-core.sln            # build everything (net10.0)
dotnet test  fallen-8-core.sln            # run all tests (~90s)

# Run a focused subset while iterating:
dotnet test fallen-8-core.sln --filter "FullyQualifiedName~SubGraphTest"
```

Run the API (Development shows the Scalar reference and the OpenAPI JSON):

```bash
dotnet run --project fallen-8-core-apiApp
# OpenAPI doc:   /openapi/v0.1.json
# Scalar UI:     /scalar/v0.1
```

## Architecture notes

- **Mutation goes through transactions.** To change a graph, build a transaction
  (`CreateVerticesTransaction`, `CreateEdgesTransaction`, `RemoveGraphElementsTransaction`,
  `CreateSubGraphTransaction`, …), `EnqueueTransaction(tx)`, then
  `WaitUntilFinished()` (or pass `waitForCompletion` on the REST call). Reads go directly
  through `IFallen8Read` (`GetAllVertices`, `GetAllEdges`, `GetAllGraphElements`, …).
- **Algorithms are plugins.** Path and subgraph algorithms implement `IPlugin`
  (`IPathTraverser` / `ISubGraphAlgorithm`) and are discovered via `PluginFactory` and
  cached in `PluginCache`.
- **Dynamic filters over REST are compiled C# fragments.** The path and subgraph APIs take
  filter/cost predicates as strings like `"return (v) => v.Label == \"person\";"`. These
  are compiled at runtime with Roslyn in `App/Helper/CodeGenerationHelper.cs` into the
  `Delegates.*` types in `fallen-8-core/Algorithms/Delegates.cs`, then cached in
  `GeneratedCodeCache`. When adding a new dynamic-filter endpoint, follow this pattern.
- **Stored queries are the pre-compiled alternative.** `POST /storedquery` registers a named,
  compile-validated path filter/cost set or subgraph template
  (`fallen-8-core/StoredQueries/`, pinned artifacts, persisted by manifest + WAL); the path
  and subgraph endpoints then accept `"storedQuery": "<name>"` instead of inline fragments.
  Registration requires `EnableDynamicCodeExecution`; invocation by name does not (the gate
  on those endpoints is request-shape-aware). Design docs are in
  [features/done/stored-query-library/](features/done/stored-query-library/).
- **Subgraph feature** lives in `fallen-8-core/Algorithms/SubGraph` (algorithm + pattern
  model) and `fallen-8-core/SubGraph/SubGraphFactory.cs` (registration, recalculation).
  Design docs are in [features/done/subgraph/](features/done/subgraph/).

## Conventions

- Every source file starts with the MIT license header block (copy an existing file's).
- Public APIs use the `Try*(out result, …) : bool` pattern rather than throwing for
  expected "not found"/"invalid" cases.
- Controllers are API-versioned (`api/v{version}`, default `0.1`) and annotate actions with
  `[ProducesResponseType]` / `[Consumes]` / `[Produces]` plus XML `<summary>`/`<remarks>`
  so they surface correctly in OpenAPI.
- Tests are MSTest (`[TestClass]`/`[TestMethod]`), arrange/act/assert, and use
  `TestLoggerFactory.Create()` for a logger. Prefer tests that pin behaviour and cover
  branching/edge cases, not just the happy path.

## Feature workflow

Feature docs are split by status: `features/open/<name>/` for work not yet implemented
(spec/plan only), `features/done/<name>/` once it is implemented and merged. A new feature
starts under `features/open/`; move its directory to `features/done/` when it lands.

Every non-trivial feature follows the same lifecycle so work is visible and reviewable:

1. **Spec & plan** — create `features/open/<name>/spec.md` and `features/open/<name>/plan.md`
   describing the behaviour, contract, and phased implementation. (Optionally a `README.md`
   with usage.) See [features/done/subgraph/](features/done/subgraph/) for the reference example.
2. **GitHub issue** — open a feature-level issue so the work is tracked and visible on
   GitHub. Label it `feature`. Link the `features/<name>/` docs from the issue.
3. **Feature branch** — branch from `main` as `feature/<name>`. Do not commit feature work
   directly to `main`.
4. **Pull request** — open a PR from the feature branch to `main` that references the issue
   (`Closes #<n>`). Keep it a draft while implementing; mark ready for review when the
   plan's phases are done, the build is clean, and tests pass.

Commit messages and PR descriptions are honest and concise, and do not reference the
assistant or add AI-generated trailers.

> Note: the initial `subgraph` feature predates this workflow and was merged to `main`
> directly. From the next feature onward, use the branch + issue + PR flow above.
