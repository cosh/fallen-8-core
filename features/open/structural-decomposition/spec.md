# Structural decomposition — shrink the god-classes for dev velocity

Status: open, under implementation on `feature/structural-decomposition` (branch-only workflow).
Related: [engine-performance](../../done/engine-performance/), [subgraph](../../done/subgraph/),
[web-ui](../../done/web-ui/), [index-lifecycle](../../done/index-lifecycle/),
[api-error-envelope](../api-error-envelope/), [studio-embeddable](../studio-embeddable/).

Revised 2026-07-22 after two independent architecture reviews (engine side; contracts/UI side)
verified every claim below against the code. Findings that changed the approach are marked
**[review]**; the full sweep is recorded under "Impact on existing features".

## Motivation

The cost of changing this codebase scales with how much unrelated context a file forces the
reader to load — and that holds double for coding agents, which pay for every line they must
read before they can edit safely. The code-health review flagged four structural concentrations
that impose exactly that tax:

| Target | Size today | Concern |
|---|---|---|
| `fallen-8-core/Fallen8.cs` | 3579 lines, one sealed class | one file owns storage mutation, indexing orchestration, WAL/persistence, load/save, scan, trim, embedding projection, metrics, change feed |
| `fallen-8-core-apiApp/Controllers/GraphController.cs` | 1894 lines | CRUD + six scan families + element embeddings + path + index management in one controller |
| web god-screens | Query 1008, Browser 736, Analytics 727, Subgraph 603 | each bundles many sub-components + state in one file |
| hand-rolled RW-spinlock | `Helper/AThreadSafeElement` (mask `0xfff00000`) | non-standard primitive under every index + `ServiceFactory`/`IndexFactory` |

None is a bug. The goal is a codebase where a change to one subsystem means opening one small
file whose behavior is pinned by tests — easy for maintainers, easy for agents, safe to work
on in parallel.

**[review]** The original framing "~7 unrelated subsystems" overstated the seams: `Fallen8`'s
private core (`_snapshot` + the nested `Snapshot` type, `_currentId`, the vertex/edge counters,
WAL flags) cross-cuts every proposed grouping, and embedding projection is invoked from nine
mutation-path call sites. The subsystems share one mutable core by design (the lock-free
single-writer discipline); the decomposition below respects that instead of fighting it.

## Guiding principle: behavior-preserving, incremental, zero-risk-first

The **whole test suite (C# + web) stays green at every step, and no observable behavior
changes** — this is pure internal restructuring. Prefer the cheapest, lowest-risk move that buys
navigability first (mechanical splits), pin untested behavior **before** moving code, and only
then do the higher-value extractions. Every phase is independently shippable; a phase that
cannot stay green is reverted, not forced.

## Targets & approach

### 0. OpenAPI determinism (pre-step) **[review]**

The snapshot's `paths` object is emitted in discovery order (controller file order × action
declaration order), which today interleaves resources — so *any* regrouping of controller code
reorders it, and the original "snapshot stays byte-identical" criterion was unachievable as
written. Fix the generator first: a document transformer on `AddOpenApi` sorts `paths`
alphabetically, and a unit test pins the exact (path, method, tag) inventory of the served
document. One reviewed snapshot regeneration (a pure-reorder diff) makes byte-identity an
honest, durable criterion for this and every future refactor.

### 1. Fallen8 — partial-class split, then narrow extractions

- **Phase: partial classes (zero-risk).** Split the single file into `Fallen8.cs` (core) plus
  subsystem parts (`Fallen8.Storage.cs`, `Fallen8.Scan.cs`, `Fallen8.Persistence.cs`,
  `Fallen8.Embeddings.cs`, `Fallen8.Trim.cs`, `Fallen8.Metrics.cs`, `Fallen8.ChangeFeed.cs`) —
  same `sealed partial class Fallen8`, same members, no signature change.
  **Core-part rule [review]:** *all* field declarations, the nested `Snapshot` type, and the
  segment constants stay in the core part; subsystem parts contain only methods/properties.
  C# does not specify field-initializer order across partial-class files — the current
  initializers were audited as order-independent, and this rule keeps that safety structural
  rather than accidental. (It also protects the one test that reflects a private field by name,
  `MemoryFootprintTest`.) The lock-free snapshot contract keeps its one home in the core part.
- **Phase: narrow extractions [review — rescoped].** The originally proposed `GraphScanner` is
  not a clean seam: the scan family captures the private `_snapshot`/`Snapshot` protocol, and
  the public entry points are `AFallen8` overrides that must stay on `Fallen8` anyway. Extract
  only what moves with zero risk: the already-static, snapshot-free helpers (`FilterLive`,
  `FindElementsIndex`, `TryOrderedRangeIndexScan`, the `Binary*Method` family, `CheckLabel`)
  into a static `ScanHelpers` class. An `EmbeddingProjection` collaborator is **optional** and
  only worth it if the mechanism grows again; if extracted it must re-read `IndexFactory` per
  call (the property is wholesale-swapped on failed-load restore and nulled in `Dispose` — a
  constructor-captured reference goes stale) and must document its writer-thread affinity.
  Persistence extraction is **out of scope**: `PersistencyFactory.Load` takes the concrete
  engine plus a `ref` into `_currentId` and calls back into it — bidirectional coupling, not a
  seam.

### 2. GraphController — partial-class split; real controllers only on need **[review — restructured]**

The original plan jumped straight to five per-resource controllers. Review showed that move is
far more expensive than claimed: ~13 test files construct `GraphController` directly, the
"already extracted" shared helpers (`AwaitAndAccept`, `RolledBackResult`, `TryResolveType`,
`TryConvertLiteral`, `CreateResult`, `CarriesInlineCode`, `MaxPageSize`) are in fact private
instance members, OpenAPI tags derive from the controller name, and the proposed mapping left
`GET /graph` and `POST /path/...` unassigned. So:

- **Phase: partial-class split** — `GraphController.Vertex.cs`, `.Edge.cs`, `.GraphElement.cs`,
  `.Scan.cs`, `.Index.cs`, `.Path.cs`, with the shared private helpers, constructor, and
  class-level attributes in the core part. Zero test churn, tags/logger category/routing
  untouched; all 35 actions use absolute route templates, so URLs cannot change. Same
  navigability payoff as real controllers, none of the cost.
- **Deferred: real per-resource controllers.** *Revisit trigger:* a concrete per-resource need
  (e.g. a policy or filter that applies to one resource only). If triggered, the move carries:
  `[Tags("Graph")]` on every new class (pin the wire contract), extraction of the shared
  helpers, a full 35-action assignment table including `/graph` and `/path`, replication of
  `[ApiController]`/`[ApiVersion("0.1")]`, and dropping the vestigial `IRESTService`. The three
  element-embedding endpoints must **not** move under `EmbeddingController` — its class-level
  authorize policy would 403 them whenever the text-in provider is off
  (`ElementEmbeddingEndpointTest` pins provider-off reachability).

### 3. Web god-screens — mechanical moves first, designed decomposition last **[review — split in two]**

- **Mechanical (safe file moves):** `BrowserScreen` and `AnalyticsScreen` contain cleanly
  delineated inner components (`AdjacencyPanel`, `EmbeddingsTab`, `PropertiesTab`,
  `ElementDetail`; `AnalyticsRunner`, `GraphShapePanel`) that use the `useInstanceStore()`
  seam — move them to `components/`. (`SubgraphScreen` turned out to have none during
  implementation: its pattern-builder JSX closes over screen-local state, so it joins the
  designed bucket below rather than being carved up.)
- **Designed (do last):** `QueryScreen` is one ~570-line component with ~25 interdependent
  `useState` hooks spanning six scan families — decomposing it requires designing a scan-runner
  hook contract, not a file move. Same for `SubgraphScreen`'s inline pattern builder. Both
  happen after the studio-embeddable shell seams land.
- **Constraints stated now** so extractions don't fight [studio-embeddable](../studio-embeddable/):
  extracted units receive instance/store state via `useInstanceStore()` or props — never module
  singletons — and introduce **no new direct `localStorage` reads**.
- **[review]** The original "existing screen tests are the guard" was false for the largest
  screen: `AnalyticsScreen` has zero tests, and QueryScreen's non-embedding flows are
  unrendered by any test. Pinning tests land **before** these moves (see Verification).

### 4. Concurrency — execute the index-lifecycle defect-(d) decision, spike-gated **[review — reframed]**

This is not "unify two concurrency models": the two models guard different write topologies.
The store's volatile-snapshot discipline works because store mutation is single-writer; indices
are **multi-writer** (request threads call `AddOrUpdate`/`TryRemoveKey` directly while the
engine writer thread purges and projects). Replacing `AThreadSafeElement` with snapshot
publishing is therefore a category error unless index writes are first routed through the
single writer — which is exactly [index-lifecycle](../../done/index-lifecycle/) item 3.5,
explicitly deferred there because it changes observable REST behavior.

Within this feature, only the standard-lock swap (index-lifecycle's named fallback shape,
defect d) is eligible, gated on the benchmark criteria that spec already defines. Scope note:
`AThreadSafeElement` also underpins `IndexFactory` and `ServiceFactory` (~170 acquire sites) —
the spike decides their inclusion explicitly. *This item may conclude "keep it, it's correct
and fast" — the primitive was re-audited as correct in practice, and that remains an
acceptable outcome.*

## Non-goals / revisit triggers

- **No API, wire-format, or behavior change** anywhere in this feature — restructuring only.
  (The one-time snapshot `paths` reorder in target 0 is the deliberate, reviewed exception.)
- No new abstraction layers "for the future" beyond the extractions named above. *Revisit
  trigger:* a concrete new subsystem needs a seam that isn't there yet.
- Real per-resource controllers: deferred, trigger above (target 2).
- `EmbeddingProjection` extraction: optional, trigger above (target 1).
- Concurrency (#4) does not proceed past the spike without evidence. *Revisit trigger:* a
  measured contention problem on the index path, or a concurrency bug traced to
  `AThreadSafeElement`.

## Impact on existing features (review sweep, 2026-07-22)

- **[api-error-envelope](../api-error-envelope/)** — its inventory counts 53 error sites in
  `GraphController` by location; the split stales that table. Sequenced **after** this
  feature's target 2; regenerate the inventory then. (Noted in that spec.)
- **[studio-embeddable](../studio-embeddable/)** — the original "alongside" sequencing would
  have churned the same files concurrently. New order: mechanical screen extractions (here) →
  embeddable shell seams → QueryScreen designed decomposition (here). The two hook constraints
  in target 3 keep extractions embeddable-compatible.
- **[index-lifecycle](../../done/index-lifecycle/)** — target 4 re-enters ground that feature
  deliberately deferred (defect d / item 3.5); this spec cross-references its decision and
  benchmarks instead of re-deriving them.
- **OpenAPI snapshot** — one pure-reorder diff in target 0; byte-stable afterwards. The
  inventory test becomes the executable contract guard.
- **NL-assist** — unaffected: fragments unchanged, and the dataset contains no tags or
  operationIds. No `RETRAIN-LOG.md` entry needed.
- **Root README / `pics/architecture.svg`** — names `GraphController`; unaffected by the
  partial split, must be updated only if the deferred real-controller move ever triggers.
- **Persistence/WAL** — verified not order- or reflection-sensitive over `Fallen8`; file
  splits cannot touch the wire format. Stored queries and persisted recipes: unaffected.

## Verification

Every phase: full `dotnet test` + web `vitest` green, `dotnet build` 0 warnings, OpenAPI
snapshot byte-identical (after target 0's one-time reorder). **Pinning tests land before code
moves** — the review found the safety net has holes exactly where code is about to move:

- Engine: `FulltextIndexScan` wrapper, `GetCountOf<T>`, the unpinned `GraphScan`/`IndexScan`
  operator arms and invalid-operator branches, metrics gauge accessors
  (`IndexCountForMetrics`/`IndexEntriesForMetrics`), `MeasureCheckpointBytes` re-save branch,
  change-feed removal-descriptor dedup.
- REST (hosted pipeline, not direct construction): routing smokes for `/vertex`, `/edge`,
  `/graph`, and the four non-vector `/scan/*` families; route-precedence pins for the
  literal-vs-parameter sibling routes; the OpenAPI (path, method, tag) inventory test.
- Web: `AnalyticsScreen` render + error mapping, QueryScreen non-embedding scan flows,
  BrowserScreen non-embedding surface, SubgraphScreen pattern-builder draft→request flow.

See [plan.md](./plan.md) for phase ordering and commit discipline.
