# Correctness Fixes — Specification

> **Status:** Planned. Consolidates latent defects surfaced by the multi-team repository
> review (engine, algorithms/indices, persistence). These are localized fixes (each a fix +
> regression test), independent of the larger structural themes, and should land first.

## 1. Problem

The review found several latent bugs that cause silent data loss, wrong query results, or
outages. Most share a single root cause: `System.Collections.Immutable` collections are
mutated as if their mutators were in place, but `ImmutableList.Add/Remove` return a **new**
list and the result is discarded.

## 2. Defects (each must get a failing regression test first)

| # | Defect | Location | Effect |
|---|--------|----------|--------|
| B1 | `AddOrUpdate` / `RemoveValue` discard the `ImmutableList` return | `Index/DictionaryIndex.cs:133,186` | Index keeps only the **first** element per key; removals are no-ops |
| B2 | Same discard bug | `Index/Fulltext/RegExIndex.cs:313,367` | Same data loss |
| B3 | `Between` predicate inverted (`key <= lower && key >= upper`) | `Index/Range/RangeIndex.cs:418` | Range scans return empty/wrong results (reached via `Fallen8.RangeIndexScan`) |
| B4 | `TryRemoveGraphElement_private` restore path discards `Insert` return and reads `OutEdges` where it means `InEdges` | `Fallen8.cs:743,773` | Failed removals don't roll back |
| B5 | `GetPropertyCount` dereferences `_properties` without a null guard | `AGraphElementModel.cs:117` | NRE for elements created without properties |
| B6 | Transaction worker has no try/catch around `TryExecute` | `Transaction/TransactionManager.cs:87` | A faulting transaction kills the single worker thread → DB bricked until restart |
| B7 | Spatial R-Tree `Save`/`Load` throw `NotImplementedException` | `Index/Spatial/Implementation/RTree/RTree.cs:1881` | Spatial indexes are silently lost on every checkpoint |
| B8 | BLS accepts `EdgeCost`/`VertexCost`/`MaxPathWeight` but never uses them | `Algorithms/Path/BidirectionalLevelSynchronousSSSP.cs` | "Shortest path" is hop-count only; compiled cost delegates are silently discarded |

## 3. Decisions to make

- **B8** is a fork: either (a) implement weighting (invoke `Path.CalculateWeight`, filter by
  `MaxPathWeight`, order by weight before `Take(maxResults)`) — which for correct weighted
  shortest paths implies a Dijkstra/A*-style frontier, a larger change — or (b) remove the
  cost/weight surface from the API so callers aren't misled. Recommend (b) now (small, honest)
  and track weighted paths as a separate algorithm feature.
- **B7** may be deferred to the persistence-hardening theme if R-Tree serialization is
  substantial; the *minimum* correct behavior is to not silently lose the index (either
  persist it or rebuild it on load and document the choice).

## 4. Acceptance criteria

- Each defect has a test that fails on the current code and passes after the fix.
- B1/B2: an index with ≥2 elements under one key returns all of them; removing one leaves the
  rest. B3: a `lower < upper` range returns the elements in range. B4: a rolled-back removal
  restores vertex/edge state. B6: a transaction that throws leaves the worker alive and the DB
  responsive. B8: the API no longer advertises unused knobs (option b) or returns
  weight-ordered, weight-filtered paths (option a).
- Full suite stays green.

## 5. Notes

The index bugs (B1–B3) will be partly obviated by the `core-storage-representation` theme, but
they are live data-loss bugs today and should be fixed immediately with tests, independent of
that larger change.
