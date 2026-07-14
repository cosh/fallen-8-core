# Trim / Reader Safety — Plan

Companion to [spec.md](./spec.md). P1 correctness first: the identity-stable hash (Part A) is a
one-line safety fix and lands first; then the automatic behaviour change (Part B). Every invariant is
pinned by a test.

GitHub issue: to be opened (label: `feature`). Feature branch: `feature/trim-reader-safety`.

## Phase 0 — Baseline & guardrails

Intent: prove the hazard deterministically and stress it, before touching any behaviour, so the
fixes are guarded.

- [ ] Add `fallen-8-unittest/TrimReaderSafetyTest.cs` (MSTest, arrange/act/assert,
  `TestLoggerFactory.Create()`).
- [ ] Deterministic mechanism test: put a `VertexModel` into a `HashSet<VertexModel>`, mutate its
  `Id` (via the internal `SetId` reached the way the other tests reach internals), and assert the set
  still `Contains` it and a `Dictionary<VertexModel,_>` still finds the key. Written to assert the
  **post-fix** invariant, so it FAILS against today's `VertexModel.GetHashCode() => Id`
  (`VertexModel.cs:492`).
- [ ] Concurrency stress guard `TraversalConcurrentWithTrim_NoDuplicateOrMissedFrontier`: on several
  threads, loop a BLS `/path` traversal while enqueuing `TrimTransaction`s; assert no exception, a
  consistent result set, and no wrong-id resolution. Intermittently fails on `main`; must be
  deterministic after Phases 1–2.
- [ ] Opt-in benchmark `fallen-8-unittest/TrimReaderSafetyBenchmark.cs`
  (`[TestCategory("Benchmark")]` **and** `[Ignore]`, excluded from the default `dotnet test`, own
  output prefix): (a) BLS frontier blow-up / duplicate expansion when a renumber lands mid-traversal;
  (b) auto-trim cost + retained-memory bound under churn (free-fields vs. today's rebuild). No
  fabricated figures — "to be captured on this box".

## Phase 1 — Identity-stable hash (Part A)

Intent: remove the trim-vs-hash-container hazard with a zero-cost change.

- [ ] Change `VertexModel.GetHashCode()` (`VertexModel.cs:492`) to
  `System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this)`.
- [ ] Confirm no change needed for `EdgeModel` (`EdgeModel.cs`, no override) or
  `PathElement.GetHashCode` (`PathElement.cs:195-197`, delegates to the edge's identity hash).
- [ ] Green the Phase 0 deterministic test; run the full path/subgraph suites (`PathTest`,
  `WeightedDijkstraPathTest`, `SubGraphTest`) to confirm no behavioural reliance on id-hashing.

## Phase 2 — Bound churn without renumbering (Part B)

Intent: auto-trim frees heavy fields and keeps slots (no id reassignment); renumbering is reserved
for the explicit `TrimTransaction`; auto-trim is opt-in, default off.

- [ ] Add internal `AGraphElementModel.ReleaseBodyForTombstone()` — null `_properties`
  (`AGraphElementModel.cs:76`), a single volatile write; override on `VertexModel` to also null
  `_outEdges` / `_inEdges` (`VertexModel.cs:60,66`).
- [ ] Rework `MaybeAutoTrim` (`Fallen8.cs:1904`): instead of `Trim_internal()` (renumber), walk the
  published snapshot and call `ReleaseBodyForTombstone()` on each `_removed` slot, keeping the slot
  and the id space. Do NOT reassign ids; do NOT emit the WAL `Trim` marker
  (`Fallen8.cs:1917-1920`) on this path — verify WAL replay determinism is unaffected (no id-space
  change).
- [ ] Demote auto-trim to opt-in: default it OFF (`_autoTrimEnabled = false`, threshold still
  configurable, `Fallen8.cs:1894,1911`); expose enable + threshold through the admin surface
  (`AFallen8`/`IFallen8Admin`).
- [ ] Leave `Trim_internal`'s compaction + `SetId(i)` renumber (`Fallen8.cs:1947-1959`) unchanged,
  reachable only via `TrimTransaction` (`TrimTransaction.cs:43-47`); add a doc-comment stating it
  reassigns ids and must not run concurrently with id-holding readers/clients.
- [ ] Tests: auto-trim-enabled churn keeps retained memory bounded AND leaves every surviving `Id`
  unchanged; the removed-vertex-adjacency-freed reader case is benign; the Phase 0 stress guard is
  now deterministic.

## Phase 3 — Id-stability contract & docs

Intent: write down the invariant this feature establishes.

- [ ] XML docs on the REST vertex/edge id parameters and on `TrimTransaction`: element ids are stable
  handles across auto-trim; only an explicit `TrimTransaction` renumbers, and it must not run
  concurrently with readers/clients that hold ids.
- [ ] Note the future `id → slot` directory as the path to renumber-free compaction, and that it is
  the shared prerequisite that constrains `non-blocking-save/` (do not build it here).

## Measure & document

Intent: capture the numbers and reconcile the cross-referenced decisions.

- [ ] Run `TrimReaderSafetyBenchmark` opt-in; record the auto-trim free-fields retained-memory bound
  and the BLS-under-trim consistency result — "captured on this box".
- [ ] Update cross-references: `non-blocking-save/` (the hash hazard is fixed; renumber is now
  explicit-only — the in-place-`SetId` note still stands for element *contents*),
  `memory-footprint/` M4 (mechanism changed from renumber to free-fields, default off), and
  `core-storage-representation/spec.md` §6 (the trim-vs-reader hash-container hazard is resolved).
- [ ] Confirm 0 warnings / 0 errors; full default suite green; benchmarks `[Ignore]`d.

## Progress

- [ ] Phase 0 — baseline & guardrails (deterministic mechanism test, concurrency stress guard,
  opt-in benchmark)
- [ ] Phase 1 — identity-stable `VertexModel.GetHashCode()` (Part A)
- [ ] Phase 2 — free-fields auto-trim, opt-in/default-off, renumber reserved for `TrimTransaction`
  (Part B)
- [ ] Phase 3 — id-stability contract & docs
- [ ] Measure & document — benchmark numbers captured; cross-refs reconciled

## Decision / revisit condition

This feature touches two prior decisions and must relate to them without reopening either:

- **`memory-footprint/` M4 (auto-trim).** M4 introduced post-commit auto-trim that reused the
  renumbering `Trim_internal`. This feature keeps M4's *placement* (post-commit, single writer) and
  its bounded-churn *goal* but changes its *mechanism* to free-fields-keep-slot and flips its default
  to OFF, because the renumber is the P1 hazard. The soak test (`MemoryFootprintTest`) still guards
  the bound.
- **`non-blocking-save/` (deferred, measured).** In-place id mutation is one reason that save
  deferral stands. This feature removes the *hash-container* consequence of in-place id mutation and
  makes renumbering explicit-only, but element *contents* still mutate in place, so the non-blocking
  save deferral is **unchanged** — not reopened here.

**Revisit condition (future `id → slot` directory).** Decouple id from storage slot only when either
(a) callers need renumber-free id-space compaction under sustained churn (reclaiming shells, not just
freeing heavy fields), or (b) `non-blocking-save/` is reopened for its stated condition
(tens-of-millions of elements saved frequently) — since a per-snapshot `id → slot` map is the shared
prerequisite for both. Until then, explicit `TrimTransaction` remains the only id-renumbering path.
