# CLAUDE.md

Guidance for working in this repository.

## What this is

Fallen-8 is an in-memory graph database written in C# (.NET 10). Namespaces are under
`NoSQL.GraphDB.*`. Three projects are the database itself:

- **`fallen-8-core`** — the engine: graph model, transactions, indices, algorithms
  (path finding, subgraph), persistence, serialization, plugins.
- **`fallen-8-core-apiApp`** — ASP.NET Core Web API exposing the engine over REST.
  OpenAPI via `Microsoft.AspNetCore.OpenApi`; interactive docs via Scalar.
- **`fallen-8-unittest`** covers every project in the solution (MSTest).

Two more are **separate deployables** that reach the graph over the public REST API only, never in
process, and reference neither the engine nor the apiApp: **`fallen-8-mcp`** (the agent channel) and
**`fallen-8-integrations`** (the job runner that reads a system on the operator's own network). Both
have an architecture note below. They share one small library, **`fallen-8-rest-client`**
(`NoSQL.GraphDB.Rest`): the REST-client seam, which is held to the same rule and references
neither the engine nor the apiApp either. **`fallen-8-bench`** is the throughput harness and does
reference the engine, because it measures it in process.

**User-facing documentation is a [Starlight](https://starlight.astro.build/) site rooted at
[`docs/`](docs/), published to <https://docs.fallen-8.com/>.** The pages are
Markdown/MDX in `docs/src/content/docs/`, one deep-dive per user-facing feature, deliberately
decoupled from `features/` (no spec/plan/phase language, no links into `features/`). That site
is the home users read; `features/<name>/` remains the historical spec/plan record and the
contributor "living doc".

**Where a new doc goes.** Add `docs/src/content/docs/<name>.md` with `title` and `description`
frontmatter (use `.mdx` if it needs components such as `<Tabs>`), then register it in the
`sidebar` in `docs/astro.config.mjs`. Conventions: the site is served from the root of its
custom domain (there is no Astro `base`), so internal links are root-relative (`/<name>/`);
images live in `docs/src/assets/`; and links into the repo tree point at GitHub. Build locally
with `npm --prefix docs ci && npm --prefix docs run build`; the build fails on any broken
internal link. When a feature changes user-visible
behaviour, update its page, and **every user-facing key feature earns a one-line entry in the
root `README.md` "Key features" list**, linking its live page at
`https://docs.fallen-8.com/<name>/`. A feature is not "done" until it is discoverable
from the README and has a page on the docs site.

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

- **A Fallen-8 is a collection of namespaces.** Each namespace is one isolated graph owning
  one `Fallen8` engine; the apiApp's `Namespaces/Fallen8Namespaces.cs` is the collection, and
  every namespace-scoped route also answers under `/ns/{ns}/…` (bare URLs alias the reserved
  `default` namespace). Terminology and the full story:
  [features/done/graph-namespaces/](features/done/graph-namespaces/).
- **Mutation goes through transactions.** To change a graph, build a transaction
  (`CreateVerticesTransaction`, `CreateEdgesTransaction`, `RemoveGraphElementsTransaction`,
  `CreateSubGraphTransaction`, …), `EnqueueTransaction(tx)`, then
  `WaitUntilFinished()` (or pass `waitForCompletion` on the REST call). Reads go directly
  through `IFallen8Read` (`GetAllVertices`, `GetAllEdges`, `GetAllGraphElements`, …).
- **Algorithms are plugins.** Path and subgraph algorithms implement `IPlugin`
  (`IPathTraverser` / `ISubGraphAlgorithm`) and are discovered via `PluginFactory` and
  cached in `PluginCache`.
- **`PUT /unittest` is for the test suite only; never in F8 Studio.** The
  `SampleGraphController` canned graph (`TestGraphGenerator`) exists so tests have a graph to run
  against. **Never surface it as a user action in the Studio UI**: no "load the unittest graph"
  button, no client wrapper. When the UI moves a newcomer from an empty graph to a populated one,
  it sends them to the **Sample gallery** (the Samples screen), which loads curated, styled
  datasets. (The endpoint stays documented as a low-level REST/CLI convenience in the API docs;
  the rule is about not pushing test scaffolding at Studio users.)
- **Dynamic filters over REST are compiled C# fragments.** The path and subgraph APIs take
  filter/cost predicates as strings like `"return (v) => v.Label == \"person\";"`. These
  are compiled at runtime with Roslyn in `App/Helper/CodeGenerationHelper.cs` into the
  `Delegates.*` types in `fallen-8-core/Algorithms/Delegates.cs`, then cached in
  `GeneratedCodeCache`. When adding a new dynamic-filter endpoint, follow this pattern.
- **Stored queries are the pre-compiled alternative.** `POST /storedquery` registers a named,
  compile-validated path filter/cost set or subgraph template; the path and subgraph
  endpoints then accept `"storedQuery": "<name>"` instead of inline fragments. It is a
  reuse/curation convenience (compile once, invoke by name), not a security lockdown —
  dynamic code execution is always on and has no switch (a compiled fragment runs in-process
  with full trust; authentication is the only boundary). The full story lives in
  [features/done/stored-query-library/](features/done/stored-query-library/).
- **Subgraph feature** lives in `fallen-8-core/Algorithms/SubGraph` (algorithm + pattern
  model) and `fallen-8-core/SubGraph/SubGraphFactory.cs` (registration, recalculation).
  Design docs are in [features/done/subgraph/](features/done/subgraph/).
- **Embeddings are element state; semantic traversal reads them.** A named embedding lives
  on the element behind ONE accessor (`AGraphElementModel.TryGetEmbedding`); a
  `VectorIndex` created with `embeddingName` is a derived projection maintained by the
  writer thread; the `semantic` block on `/path` and `/subgraph` supplies a query vector
  (embedded once, up front) to declarative filters/costs and to compiled fragments via the
  `context` parameter. The optional text-in provider (`Fallen8:Embedding`) lives in the
  apiApp only — never in the engine; a bare `dotnet run` has it off, the compose
  environment wires it to the Ollama sidecar by default (`F8_EMBEDDINGS`), and clients
  read its state from `GET /status`. The living docs are
  [features/done/element-embeddings/](features/done/element-embeddings/) and
  [features/done/embedding-provider/](features/done/embedding-provider/).
- **External systems reach a graph through the integrations runtime, a separate deployable.**
  `fallen-8-integrations` runs ONE job per request: it reads a system on the operator's own network
  (a CSV inventory, a UniFi console, a Fronius inverter, an AUTOSAR system extract), describes what
  it saw as a snapshot, and
  writes that into one namespace over the REST API. It keeps no schedule, no run history and no
  credential: **a credential arrives with the job that needs it and is dropped when the run ends**,
  so the container has no credential mount and nothing to rotate. Its container port is never
  published; the browser reaches it through the apiApp's authenticated proxy at `/integrations/*`.
  Identity is exact-match on canonical claim keys and **nothing ever merges two elements**; only a
  snapshot declaring it saw the WHOLE source may withdraw a claim. A new integration is a data
  descriptor plus one `ObserveAsync`, judged by a conformance suite that observes a candidate rather
  than believing it. Living doc:
  [`docs/src/content/docs/integrations.md`](docs/src/content/docs/integrations.md) (published at
  <https://docs.fallen-8.com/integrations/>); the feature record is
  [features/done/integrations/](features/done/integrations/).
- **AI agents reach Fallen-8 through the MCP server — a separate deployable.** `fallen-8-mcp`
  bridges the Model Context Protocol to the REST API over HTTP (it never references the engine
  or the apiApp); its surface is a small, token-frugal set of consolidated tools across
  read/write/admin tiers plus a `code` capability, with three auth modes. **Engine → REST →
  MCP is a one-way propagation rule:** a capability that grows in the engine and reaches the
  REST surface MUST also be surfaced to agents as an MCP tool — or be a conscious, reasoned
  deferral. This is enforced (see Quality gates), not just documented. Living doc:
  [`docs/src/content/docs/mcp-server.md`](docs/src/content/docs/mcp-server.md) (published at
  <https://docs.fallen-8.com/mcp-server/>); the feature record is under
  `features/*/mcp-server/`.

## Quality gates (enforced, feature code-quality)

- **Warnings are errors** (`Directory.Build.props`): fix the warning or `NoWarn` it with a
  comment — never disable the gate. NuGet audit advisories (NU1901–NU1904) stay warnings.
- **Convention tests** (`fallen-8-unittest/CodeQualityTest.cs`) fail the suite on: a missing
  MIT header, `Console.Write*` in product code, `DateTime.Now` outside the documented
  `DateHelper` allowlist, or a non-exact package version.
- **OpenAPI snapshot**: regenerate with `powershell -File scripts/update-openapi-snapshot.ps1` whenever
  a controller's routes or XML docs change; review the printed diff - additions are
  expected, removals only where a deliberately edited remark shrank.
- **Provider-descriptor snapshot**: the shipped integration descriptors are pinned in
  `features/done/integrations/provider-descriptors.json` (the JSON the providers route
  actually returns), and `ProviderDescriptorSnapshotTest` fails the suite when a shipped
  descriptor drifts from it. Regenerate with
  `powershell -File scripts/update-provider-descriptor-snapshot.ps1`. The snapshot is also what the
  docs-screenshot capture replays, so **a descriptor change means recapturing
  `screen-integrations.png`** - that is why the gate exists: the published screenshot once
  showed settings the runtime deliberately does not offer.
- **MCP coverage (engine→REST→MCP)**: `McpRestCoverageTest` fails the suite if a REST
  operation in the OpenAPI snapshot is neither bridged by an MCP tool
  (`McpBridgedEndpoints`) nor recorded as a conscious deferral (with a reason) — so a newly
  added REST endpoint forces a decision to surface it to agents or justify why not.
  `McpContractTest` additionally pins the bridge's routes/methods against the same snapshot.
- **One home per explanation**: a concept is explained once — usually on the type that owns
  the contract or in the feature README — and every other site is a one-line pointer. Do not
  re-narrate a feature's story across call-site comments, controller remarks, the root
  README and the feature README; the feature README is the LIVING doc (specs/plans are
  historical records and are not rewritten).
- **Browser host (trimmed wasm probe)**: `tools/browser-probe` runs the engine as a trimmed
  browser-wasm app, headless under node, and its exit code is the verdict. It is the ONLY
  thing that executes the single-threaded arms: everything gated on
  `HostCapabilities.SupportsBackgroundWork` takes the threaded branch on every machine the
  unit suite runs on, so the browser halves of the transaction writer, the checkpoint
  fan-out, the change-feed teardown and the traversal sweep are covered by no UNIT test -
  the probe's checks are the only thing that runs them. Run it
  with `dotnet publish tools/browser-probe -c Release` (publishing is where ILLink reports
  what the analyzer cannot see) then
  `node tools/browser-probe/bin/Release/net10.0/browser-wasm/AppBundle/main.mjs`; needs the
  `wasm-tools` workload. It is deliberately NOT in `fallen-8-core.sln`, so a plain
  `dotnet build` never requires that workload; CI runs it as its own `browser` job. **If you
  change anything a browser host depends on, run it** - a green unit suite says nothing about
  that host.
- **Docs site build (link-checked)**: the user-facing docs site (`docs/`, a Starlight project)
  builds in CI on every push to `main` (`.github/workflows/docs.yml`) and fails on any broken
  internal link (`starlight-links-validator`). Adding or editing a page must keep that build
  green; run it locally with `npm --prefix docs ci && npm --prefix docs run build`. See
  "What this is" for where pages live and how links/images are written.

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
`features/open/` means **pending work only** — a feature that gets superseded or abandoned
also moves to `features/done/`, with its spec's status line saying so and stating that nothing
was implemented (see [features/done/multi-instance-host/](features/done/multi-instance-host/)).

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
   NL-assist dataset/eval ↔ feature READMEs ↔ **docs-site pages** (`docs/src/content/docs/`)
   ↔ **architecture diagrams** ↔ persisted recipes/stored queries) and records the findings in
   its spec under "Impact on existing features". When another feature's assets are affected — e.g. an engine contract change
   that stales the Studio UI or the fine-tune dataset — do not silently adapt or ignore them:
   surface the impact and ask about next steps with honest options. Impacts that need an
   NL-assist retrain are not re-litigated per feature: append an entry to
   `nl-assist-finetune/RETRAIN-LOG.md` (the next fine-tune run drains all pending entries).
6. **Architecture-doc freshness (mandatory)** — the architecture story lives in exactly two
   places: the diagram + prose in the root [`README.md`](README.md) (the simple view) and
   [`docs/src/content/docs/architecture.md`](docs/src/content/docs/architecture.md) (the full
   view, published at <https://docs.fallen-8.com/architecture/>, its mermaid diagram the
   single source — there is deliberately no duplicate hand-drawn image). If a feature changes
   how clients reach Fallen-8 (a new channel or deployable), how the layers fit, or what ships
   in the deployable, update **both** diagrams in the same PR. A stale architecture diagram is
   a feature-incomplete signal, not a follow-up. Diagram style is fixed: dark surfaces with the
   brand red `#E2001A` accent, colours taken from the F8 logos in `pics/` — never the mermaid
   defaults.

Commit messages and PR descriptions are honest and concise, and do not reference the
assistant or add AI-generated trailers.

> Note: the initial `subgraph` feature predates this workflow and was merged to `main`
> directly. From the next feature onward, use the branch + issue + PR flow above.
