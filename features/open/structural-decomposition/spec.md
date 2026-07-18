# Structural decomposition — shrink the god-classes for dev velocity

Status: open (spec/plan only). Related: [engine-performance](../../done/engine-performance/),
[subgraph](../../done/subgraph/), [web-ui](../../done/web-ui/), and the code-health-sweep that
surfaced these (branch `feature/code-health-sweep`).

## Motivation

The code-health review flagged four structural concentrations that slow future work. None is a bug;
each is a size/coupling problem that makes changes riskier and slower than they should be. They were
deliberately left out of the inline sweep because they are large and want their own coherent,
behavior-preserving effort:

| Target | Size | Concern |
|---|---|---|
| `fallen-8-core/Fallen8.cs` | 3579 lines, one sealed class | owns ~7 unrelated subsystems (storage, indexing orchestration, WAL/persistence, load/save, scan, trim, embedding projection, metrics, change feed, stored queries) |
| `fallen-8-core-apiApp/Controllers/GraphController.cs` | ~1650 lines (post-sweep) | CRUD + six scan families + embeddings + path + index management in one controller |
| web god-screens | Query 1011, Analytics 730, Browser 656, Subgraph 626 | each bundles many sub-components + state in one file |
| two concurrency models | — | hand-rolled spinlock `Helper/AThreadSafeElement` (magic mask `0xfff00000`) for indices vs the lock-free `volatile` snapshot discipline for `Fallen8`/models |

## Guiding principle: behavior-preserving, incremental, zero-risk-first

The **whole test suite (802 C# + 267 web) stays green at every step, and no observable behavior
changes** — this is pure internal restructuring. Prefer the cheapest, lowest-risk move that buys
navigability first (mechanical splits), and only then the higher-value collaborator extractions. Every
phase is independently shippable.

## Targets & approach

### 1. Fallen8 — partial-class split, then collaborator extraction

- **Phase 1 (zero-risk): partial classes.** Split the single 3579-line file into `Fallen8.Storage.cs`,
  `Fallen8.Indexing.cs`, `Fallen8.Embeddings.cs`, `Fallen8.Scan.cs`, `Fallen8.Persistence.cs`,
  `Fallen8.Metrics.cs`, etc. — same `sealed partial class Fallen8`, same members, no signature change.
  This is a pure file reorganization: the compiler proves it behavior-identical, and navigability
  improves immediately. Do this first.
- **Phase 2 (higher-value): extract true collaborators** where the seam is already clean — e.g. an
  `EmbeddingProjection` type owning `ProjectEmbeddingToBoundIndices`/`ProjectAllEmbeddingsOf`, and a
  `GraphScanner` owning the `FindElements`/scan family — with `Fallen8` delegating. Each extraction is
  its own commit behind the green suite. Storage/WAL already have factories (`PersistencyFactory`) to
  lean on.

### 2. GraphController — split by resource

Controllers are more separable than the engine (route attributes are per-action, so splitting classes
does not change routing). Split into `VertexController`, `EdgeController`, `GraphElementController`,
`ScanController` (the six scan families), `IndexController`, and keep path finding where it fits. The
shared helpers the sweep already extracted (`AwaitAndAccept`, `DynamicCodeCapabilityGate`,
`VectorSearchConstraintBuilder`) become the shared surface these controllers reuse. Routes, request/
response shapes, and the OpenAPI snapshot are unchanged (verify the snapshot is byte-identical after).

### 3. web god-screens — extract sub-components + hooks

Pull the self-contained pieces out of each screen into `components/` + custom hooks (e.g. QueryScreen's
editor, result table, and run-controls). The `useInstanceStore()` seam the sweep added is the pattern.
Behavior-preserving component extraction; the existing screen tests are the guard. This also feeds the
[studio-embeddable](../studio-embeddable/) work (smaller, more reusable units are easier to embed).

### 4. Concurrency-model unification — evaluate before changing (spike)

This is the **highest-risk** item and is scoped as a **spike first, change second**. `AThreadSafeElement`
(reader/writer counts packed in one `int`, busy `Thread.Yield()`) predates the lock-free snapshot
discipline. Before touching it: benchmark the index write/read paths that use it, and decide whether it
can be replaced by (a) the same `volatile` snapshot discipline the models use, or (b) a standard
`ReaderWriterLockSlim`/lock, without regressing the index hot paths. Only proceed with a replacement if
the spike shows no perf regression AND clearer correctness. *This item may conclude "keep it, it's
correct and fast" — that is an acceptable outcome.*

## Non-goals / revisit triggers

- **No API, wire-format, or behavior change** anywhere in this feature — it is restructuring only.
- No new abstraction layers "for the future" beyond the extractions named above. *Revisit trigger:* a
  concrete new subsystem needs a seam that isn't there yet.
- The concurrency unification (#4) does not proceed past the spike without evidence. *Revisit trigger:*
  a measured contention problem on the index path, or a concurrency bug traced to `AThreadSafeElement`.
- Sequencing: do #1-Phase 1 and #2 first (cheapest, highest navigability payoff); #3 alongside the
  studio-embeddable work; #4 last and only on evidence.

## Verification

Every phase: full `dotnet test` (802) + web `vitest` (267) green, `dotnet build` 0 warnings, and — for
#2 — the OpenAPI snapshot regenerated and confirmed byte-identical (no contract drift). A phase that
cannot stay green is reverted, not forced.
