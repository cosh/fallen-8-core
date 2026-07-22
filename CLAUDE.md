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
  compile-validated path filter/cost set or subgraph template; the path and subgraph
  endpoints then accept `"storedQuery": "<name>"` instead of inline fragments. Registration
  requires `EnableDynamicCodeExecution`; invocation by name does not — the full gating story
  lives in [features/done/stored-query-library/](features/done/stored-query-library/).
- **Subgraph feature** lives in `fallen-8-core/Algorithms/SubGraph` (algorithm + pattern
  model) and `fallen-8-core/SubGraph/SubGraphFactory.cs` (registration, recalculation).
  Design docs are in [features/done/subgraph/](features/done/subgraph/).
- **Embeddings are element state; semantic traversal reads them.** A named embedding lives
  on the element behind ONE accessor (`AGraphElementModel.TryGetEmbedding`); a
  `VectorIndex` created with `embeddingName` is a derived projection maintained by the
  writer thread; the `semantic` block on `/path` and `/subgraph` supplies a query vector
  (embedded once, up front) to declarative filters/costs and to compiled fragments via the
  `context` parameter. The optional text-in provider (`Fallen8:Embedding`, off by default)
  lives in the apiApp only — never in the engine. The living docs are
  [features/done/element-embeddings/](features/done/element-embeddings/) and
  [features/done/embedding-provider/](features/done/embedding-provider/).

## Quality gates (enforced, feature code-quality)

- **Warnings are errors** (`Directory.Build.props`): fix the warning or `NoWarn` it with a
  comment — never disable the gate. NuGet audit advisories (NU1901–NU1904) stay warnings.
- **Convention tests** (`fallen-8-unittest/CodeQualityTest.cs`) fail the suite on: a missing
  MIT header, `Console.Write*` in product code, `DateTime.Now` outside the documented
  `DateHelper` allowlist, or a non-exact package version.
- **OpenAPI snapshot**: regenerate with `pwsh scripts/update-openapi-snapshot.ps1` whenever
  a controller's routes or XML docs change; review the printed diff - additions are
  expected, removals only where a deliberately edited remark shrank.
- **One home per explanation**: a concept is explained once — usually on the type that owns
  the contract or in the feature README — and every other site is a one-line pointer. Do not
  re-narrate a feature's story across call-site comments, controller remarks, the root
  README and the feature README; the feature README is the LIVING doc (specs/plans are
  historical records and are not rewritten).

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
5. **Cross-feature impact check (mandatory)** — every feature sweeps the other layers and
   features it may affect (engine ↔ REST contract ↔ OpenAPI snapshot ↔ Studio UI ↔
   NL-assist dataset/eval ↔ feature READMEs ↔ persisted recipes/stored queries) and records
   the findings in its spec under "Impact on existing features". When another feature's
   assets are affected — e.g. an engine contract change that stales the Studio UI or the
   fine-tune dataset — do not silently adapt or ignore them: surface the impact and ask
   about next steps with honest options.

Commit messages and PR descriptions are honest and concise, and do not reference the
assistant or add AI-generated trailers.

> Note: the initial `subgraph` feature predates this workflow and was merged to `main`
> directly. From the next feature onward, use the branch + issue + PR flow above.
