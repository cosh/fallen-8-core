# Scan-Result Representation — Specification

> **Status:** Planned (P1 performance) — from the 2026-07 principal-architect & performance review.
> The master store shed its immutable tree (`core-storage-representation`), but every full-graph
> read still re-materialises one: de-tree the scan results so a read hands back a right-sized,
> contiguous read-only sequence instead of a per-call AVL tree.

## 1. Problem / current state

The master store is now the good segmented array (`core-storage-representation`, `adjacency-flattening`
LANDED). But the read surface that projects it back to callers still builds an `ImmutableList<T>` —
a balanced AVL tree — on **every** call, and nobody who receives it ever retains it, so the
immutability buys nothing.

- `GetAllVertices` (`Fallen8.cs:1997`), `GetAllEdges` (`Fallen8.cs:2009`) and `GetAllGraphElements`
  (`Fallen8.cs:2018`) each wrap a fresh sequential scan in `ImmutableList.CreateRange<T>(...)`. The
  underlying walk is already the cheap, cache-friendly `LiveElementsSequential(_snapshot)` (finding
  P7 landed — the scan is **already sequential**, not PLINQ); the cost that remains is the tree the
  result is packed into.
- `FindElementsIndex` (`Fallen8.cs:1755`) builds its result the same way —
  `ImmutableList.CreateRange<AGraphElementModel>(index.GetKeyValues().AsParallel()… .SelectMany …
  .Distinct())` — and here the `.AsParallel()` (`Fallen8.cs:1759`) is a real find: the finder is a
  cheap `IComparable.CompareTo`, so PLINQ's partition/merge overhead is paid over a light predicate,
  and the flattened set is then re-treed. `TryOrderedRangeIndexScan` (`Fallen8.cs:1784`) likewise
  re-trees a freshly-deduped set (`ImmutableList.CreateRange(matched.Distinct())`, `Fallen8.cs:1813`).
- The public contract is `ImmutableList<T>` up and down the read surface: `IFallen8Read.cs:101,108,115`
  (the `GetAll*` trio) and `IFallen8Read.cs:139,152` / `AFallen8.cs:121-127` (`IndexScan`,
  `RangeIndexScan`). `IndexScan`'s `Equals` branch returns the index's own posting-list bucket
  (`index.TryGetValue`, `Fallen8.cs:748`) — that one **is** genuinely shared with and retained by the
  index, so its copy-on-write is meaningful; every other branch returns a per-call throwaway tree.

**Measured cost.** `core-storage-representation/plan.md:69` recorded the full scan of 2,500,000
visited elements at **357.2 ms / 158.3 MB** allocated (≈ 63 B per visited element), and
`core-storage-representation/plan.md:84` explicitly attributes the residual to *"the `ImmutableList`
result building"* — named there and left for a later theme (that theme's Phase 4 became
`adjacency-flattening`, which de-treed the per-vertex **adjacency**, not the scan result). An
`ImmutableList` node is ~48 B plus pointer-chasing enumeration; a flat reference array is 8 B/slot
with a contiguous walk. At 10M+ elements one `GetAllVertices()` allocates ~400 MB of tree nodes to
hand back a read-only sequence the caller drops immediately.

**Amplifier — the subgraph algorithm.** `BreathFirstSearchSubgraphAlgorithm` calls these per phase:
`GetAllVertices()` (`…:149`, `…:633`), `GetAllEdges()` (`…:209`, `…:643`) and `GetAllGraphElements()`
(`…:695`), so each phase re-materialises a whole-graph tree (engine-performance P8). One of those
sites uses the `ImmutableList`-only `.ForEach(…)` (`…:643`).

## 2. Goals / non-goals

**Goals.**
- Change the whole-graph read surface to return `IReadOnlyList<T>` backed by a **right-sized**
  `List<T>`/array built freshly per call. Immutability is not part of the contract these methods
  need — no caller retains the result — so drop the tree.
- Extend the same return-type change to the index-scan surface (`IndexScan`/`RangeIndexScan`) so the
  read contract is uniform and the per-call throwaway trees there are gone too; keep returning the
  index's shared bucket (as `IReadOnlyList<T>`) on the `Equals` fast path, since that one is retained.
- Fix the one genuine PLINQ-over-a-light-predicate that remains on a scan path (`FindElementsIndex`,
  `Fallen8.cs:1759`): make the finder + copy sequential; reserve `.AsParallel()` for the genuinely
  heavy user-predicate scan only.
- Follow the API-version-bump precedent set by `adjacency-flattening` (public library signatures
  change → bump the `fallen-8-core` package version; the REST `api/v0.1` surface is unchanged because
  controllers project to DTOs).
- Optionally add a streaming `IEnumerable<T>` read overload so callers that only iterate (the
  subgraph phases) can skip materialisation entirely.

**Non-goals.**
- **Do not touch the master store.** It is already the good segmented array (`core-storage-representation`)
  — this is purely about how a read *projects* it.
- Do not change `IIndex`'s own return types (`GetKeyValues`/`TryGetValue`/`GreaterThan`/`LowerThan`
  stay `ImmutableList<…>`): the index legitimately retains its posting lists, so their copy-on-write
  is load-bearing. This feature only changes what `Fallen8`/`IFallen8Read` hand *out*.
- Do not change the transaction/read model: mutations stay on the single writer; reads stay
  lock-free over the volatile `_snapshot`. A returned `IReadOnlyList<T>` is a point-in-time
  projection of the snapshot captured at call time, exactly as `ImmutableList` was.
- No REST route/version change, no DTO shape change.
- Do not resurrect a persistent CSR structure — see `csr-adjacency/assessment.md` (SKIP); a
  freshly-built read-only projection is *not* that and does not reopen it.

## 3. Design sketch

**Return type.** `ImmutableList<T>` implements `IReadOnlyList<T>`, so widening the declared return
type to `IReadOnlyList<T>` is source-compatible for the shared-bucket path and lets the built paths
return a plain `List<T>`/array. New signatures:

```
IReadOnlyList<VertexModel>        GetAllVertices(string interestingLabel = null);
IReadOnlyList<EdgeModel>          GetAllEdges(string interestingLabel = null);
IReadOnlyList<AGraphElementModel> GetAllGraphElements(string interestingLabel = null);
bool IndexScan(out IReadOnlyList<AGraphElementModel> result, …);
bool RangeIndexScan(out IReadOnlyList<AGraphElementModel> result, …);
```

**`GetAll*` bodies.** Replace `ImmutableList.CreateRange(LiveElementsSequential(_snapshot).Where(…))`
with a direct sequential fill into a `List<T>`:

- Capture `var snap = _snapshot;` once (same volatile read the methods rely on now), then walk
  `[0, snap.Count)` over the segments exactly as `LiveElementsSequential`/`GetCountOf<T>`
  (`Fallen8.cs:1977`) already do, appending live, type-and-label-matching elements to a `List<T>`.
- **Right-size the unfiltered case:** when `interestingLabel == null`, pre-size the list from the
  maintained `VertexCount`/`EdgeCount` (and their sum for `GetAllGraphElements`) so no doubling
  churn occurs; the filtered case starts from a modest capacity and grows. Return the `List<T>` typed
  as `IReadOnlyList<T>`. Result order is unchanged (id order), which is what the sequential walk
  already produced — no caller relied on any other order.

**Index-scan bodies.** In `IndexScan`/`RangeIndexScan`, the `Equals` branch keeps assigning the
index's shared bucket (via a local `ImmutableList<…>` from `TryGetValue`, then out as
`IReadOnlyList<…>`). The ordered/`NotEquals` branches call the reworked `FindElementsIndex`, which now
returns a right-sized `List<AGraphElementModel>`: iterate `index.GetKeyValues()` **sequentially**,
apply `finder`, and add the matched buckets' elements into a de-duplicating collector
(`HashSet<AGraphElementModel>` reference identity → `List`) so the cross-bucket `.Distinct()` is
preserved without a second pass or a tree. `TryOrderedRangeIndexScan` returns the deduped `List`
directly instead of `ImmutableList.CreateRange(matched.Distinct())`.

**Optional streaming overload.** Add `IEnumerable<AGraphElementModel> EnumerateGraphElements(string
interestingLabel = null)` (and vertex/edge variants) that `yield return`s the filtered live elements
without materialising. The subgraph algorithm's per-phase `foreach`/`.Where` sites can consume these
directly; `GetAll*` can be expressed as `new List<T>(EnumerateX(...))` to avoid duplicated filter
logic. This overload is a pure add — no existing caller has to move.

**Call-site migration (mechanical).**
- `BreathFirstSearchSubgraphAlgorithm.cs:643` uses `ImmutableList.ForEach(…)` (not on
  `IReadOnlyList<T>`) → rewrite as a `foreach` (or consume the streaming overload).
- Explicitly-typed locals `ImmutableList<VertexModel> … = …GetAllVertices()` at
  `Benchmark/ScaleFreeNetwork.cs:143` and `ConcurrentStorageTest.cs:467` → `IReadOnlyList<VertexModel>`
  (or `var`).
- The `IFallen8Read` mock in `SubGraphControllerTest.cs:439-441` → update the three signatures.
- `IndexScan`/`RangeIndexScan` callers that fed the `out` into an `ImmutableList` local → `IReadOnlyList`.
- `.Count`, `.Take`, `.Where`, `.Select`, `.OrderBy`, `.Single`, `.First`, `foreach` (the rest of the
  call sites — `GraphController.cs:321,324`, `SubGraphController.cs:239,240`, the test suite) all work
  unchanged on `IReadOnlyList<T>`/LINQ.

**Versioning.** Bump `fallen-8-core.csproj:12` `<Version>` (breaking public-API change, as
`adjacency-flattening` did 0.0.14 → 0.1.0). REST `api/v0.1` is untouched.

## 4. Acceptance criteria

- An opt-in benchmark (`[TestCategory("Benchmark")]` + `[Ignore]`, per the repo convention) measures
  `GetAllVertices`/`GetAllEdges`/`GetAllGraphElements` at 1,000,000 and 2,500,000 elements and shows:
  - The scan's own allocation drops from the recorded ~158 MB of AVL nodes (≈ 63 B/visited element at
    2.5M) to a single right-sized reference array — on 64-bit ≈ 8 bytes × live count (≈ **8 MB** at 1M,
    ≈ **20 MB** at 2.5M) — i.e. roughly an **order-of-magnitude** reduction, with **no per-node
    allocation**. (Numbers to be captured on this box.)
  - Iteration is a flat contiguous walk; wall time target ≈ **half** the recorded 357 ms/2.5M scan.
    (To be captured on this box.)
- A characterization test pins result **parity**: for a mixed graph with removals and label filters,
  the new `IReadOnlyList<T>` contains exactly the same elements (and in the same id order) the old
  `ImmutableList<T>` did — for `GetAll*`, `IndexScan` (every operator, incl. the shared-bucket
  `Equals` path) and `RangeIndexScan`, including the cross-bucket de-dup.
- `FindElementsIndex` no longer calls `.AsParallel()`; the genuinely heavy user-predicate scan
  (`FindElements(ElementSeeker …)`, `Fallen8.cs:1734`, already `List`-returning) stays parallel via
  `LiveElements`.
- The subgraph algorithm's per-phase scans consume the new surface (no `ImmutableList.ForEach`), and
  the subgraph benchmark/tests still pass — a phase that previously re-treed the whole graph now
  fills an array (or streams).
- The full MSTest suite is green with the interface change (mock + typed locals migrated, not
  deleted).

## 5. Risks

- **Source/binary break for library consumers.** Widening `ImmutableList<T>` → `IReadOnlyList<T>` is a
  breaking public-API change. Mitigated by the version bump (the `adjacency-flattening` precedent) and
  by the fact that the common member surface (`.Count`, indexer, `foreach`, LINQ) is unchanged; only
  `ImmutableList`-specific members (`.ForEach`, `.Add`/`.Remove` producing a new list) are lost, and
  the read surface is not a mutation surface.
- **A caller that mutated the returned list** would break — but the contract was already immutable
  (`ImmutableList`), so no correct caller did.
- **Right-sizing from `VertexCount`/`EdgeCount`** assumes those counters stay exact under concurrent
  mutation. They are maintained incrementally on the single writer (engine-performance P3) and are
  used only as a **capacity hint** — a stale hint costs a resize, never a wrong result, because the
  authoritative fill is the snapshot walk. Capture the snapshot and the count consistently (read the
  count for the hint, then walk `snap`); over-/under-shoot is harmless.
- **De-dup collector for the index path** must use reference identity to match `.Distinct()`'s current
  behaviour on `AGraphElementModel`; verify the parity test covers an element indexed under several
  matching keys.

## 6. Keep (do not regress)

- **The segmented master store and the sequential scan** (`LiveElementsSequential`, `GetCountOf<T>`,
  engine-performance P7) — the walk is already right; only the packaging changes.
- **The index's own copy-on-write posting lists** — `IIndex` return types are unchanged, and
  `IndexScan`'s `Equals` path keeps returning the shared bucket (no needless copy).
- **The heavy-predicate parallel scan** (`FindElements(ElementSeeker …)` via `LiveElements`) stays
  parallel — this feature removes PLINQ only where the predicate is light.
- **Lock-free reads over the volatile `_snapshot`** and the single-writer mutation model — a returned
  `IReadOnlyList<T>` is still a point-in-time projection captured at call time.
- **Result order (id order)** and **null/removed filtering** — same elements, same order as today.
