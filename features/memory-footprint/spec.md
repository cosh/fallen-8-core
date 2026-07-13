# Memory Footprint — Specification

> **Status:** Implemented (M1, M2, M3, M4, M6). M5 is **deferred** to `persistence-hardening`
> (it is an on-disk format change that needs format versioning). Memory/GC improvements from the
> review, on top of `core-storage-representation`'s landed **master-store** change. This theme
> addresses the **per-element property, lifecycle, interning and quota** costs; it does **not**
> flatten adjacency — that remains deferred (see §1).

## 1. Scope

Reduce steady-state bytes/element and allocation churn, on top of the structural change in
`core-storage-representation`. That theme's landed work removed the **master-store** per-element
overhead (the ~48 B/edge `ImmutableList` tree node became an ~8 B array slot).

What this theme addresses (findings M1–M6): the **per-element property store** (M1), **runtime
string interning** of labels/keys/edge-property-ids (M2), **transaction lifecycle** retention
(M3), **removed-element reclamation** under churn (M4), and a bounded default **subgraph quota**
(M6). None of M1–M6 touch adjacency.

What this theme does **not** address (it was **out of scope** here — this theme changes only
private-behind-accessor state): the **adjacency** overhead — the two per-vertex adjacency
`ImmutableList`s (~96 B/edge) and the per-vertex adjacency `ImmutableDictionary`s. That was
`core-storage-representation` **Phase 4**, deferred there because `VertexModel.OutEdges`/`InEdges`
are part of the **public surface** (changing them needs a public-API-version bump).

> **Update:** adjacency flattening has since **landed** as `features/adjacency-flattening/` (with the
> version bump 0.0.14 → 0.1.0). The two per-vertex `ImmutableList` AVL trees are gone — each
> edge-property group is now a contiguous `EdgeModel[]` behind a copy-on-write, `volatile`-published
> `Dictionary<string, EdgeModel[]>`, and the public surface is a read-only view. The ~48 B/AVL-node ×
> 2-lists per-edge overhead this section flagged is removed; the net win is degree-dependent (the
> `Dictionary` container's fixed cost means a small regression at degree ≈ 2 but ~27–35% less
> per-edge adjacency at degree ≥ 10 — see that feature's `plan.md` Measurements).

## 2. Findings & targets

| # | Finding | Location | ≈ Saving | Effort |
|---|---------|----------|----------|--------|
| M1 | Property store is `ImmutableDictionary<string,object>` (~100 B container for one property) with **boxed** value types (~24 B/box) | `AGraphElementModel.cs:64,181` | ~60–80 B/entry container + box per value-type property | M |
| M2 | Labels and property-ids are **not interned on the runtime create path** (the *load* path already dedups via the string token table) | `AGraphElementModel.cs:82`; `Fallen8.cs:196` | ~30–40 B per duplicate string | S–M |
| M3 | Completed transactions retain their definition/property data until `Trim` (`Cleanup()` only runs in `Trim`) → ~2× transient property data | `Transaction/TransactionManager.cs:42,159`; `Transaction/CreateVerticesTransaction.cs:83` | transient 2× of inserted property data | S |
| M4 | Removed elements keep their full body (properties + adjacency) until `Trim` | `Fallen8.cs:666,1049` | full body of removed elements | S |
| M5 | `EdgeModel.EdgePropertyId` written/read via the **non-tokenized** string path on load (dup per edge) — **DEFERRED to `persistence-hardening`** (on-disk format change; needs format versioning) | `Persistency/PersistencyFactory.cs` | ~30–40 B/edge (load) | S |
| M6 | Subgraphs retained unboundedly by default (`SubGraphQuota` defaults unlimited); each carries its own immutable collections | `SubGraph/SubGraphFactory.cs:71` | per-subgraph collection overhead | S |

## 3. Approach

- **M1:** for the typical handful-of-properties case, store a sorted `KeyValuePair<string,object>[]`
  or a small `Dictionary` instead of `ImmutableDictionary`; de-box common values (singleton
  `true`/`false`, small-int cache) or use a discriminated value struct for primitives. Empty
  property maps are already `null` (good — keep).
- **M2:** a small intern table on `Fallen8` (`ConcurrentDictionary<string,string>`) applied to
  `Label`, property keys, and `EdgePropertyId` in create/`SetProperty` — mirrors what the load
  path already does.
- **M3:** call `Cleanup()` (drop definition/created-model references) as soon as a transaction is
  `Finished`, not only at `Trim`; cap retained transaction-state history. (Overlaps
  `engine-performance` P9.)
- **M4:** free the heavy fields (properties/adjacency) at removal time, or auto-trim past a
  tombstone-ratio threshold.
- **M5 (DEFERRED — not done here):** routing `EdgePropertyId` through
  `WriteOptimized`/`ReadOptimizedString` so it shares the token-table instance changes the
  **on-disk save format**. Doing it now, unversioned, would break loading of existing save files.
  It is therefore deferred to `persistence-hardening`, to land together with that theme's format
  versioning (theme 5) so the reader can tell old (untokenized) from new (tokenized) layouts. See
  `features/persistence-hardening/plan.md`.
- **M6:** ship a sane default `SubGraphQuota`; per-subgraph overhead shrinks once
  `core-storage-representation` lands.

## 4. Acceptance criteria

- A large-graph retained-memory measurement shows a material drop vs. baseline after M1+M2 (on top
  of the storage change). **Met:** measured on a 200k-vertex / 200k-edge property graph (a full
  1M/10M run is impractical in the test environment; the per-element numbers scale linearly). The
  metric is **managed-heap retained** memory (`GC.GetTotalMemory(true)` after a forced blocking
  collection), NOT process RSS / working set. It dropped from **877→245 B/vertex** (4 properties)
  and **760→388 B/edge**, and the whole graph from **312→121 MB managed-heap retained (−61%)**. See
  `plan.md` for the harness and full numbers.
- Insert-heavy workload: M3 targets a lower **peak** and M4 a bounded **steady-state** under churn.
  **Met, with a caveat on M3:** this is not a direct peak measurement. The M3 claim rests on a
  structural argument — each transaction's heavy input is released at commit rather than lingering
  until `Trim`, so it is not retained across the rest of a batch — backed by the M4 soak test; the
  single-snapshot harness does not measure peak, and in fact shows *transient* allocation rising
  **+6–7%** (M2's interned-key copies, disclosed in `plan.md`). M4's bounded steady-state is
  asserted directly: `MemoryFootprintTest` shows the master store stays bounded under churn far
  exceeding the auto-trim threshold.
- All existing tests pass; property typed-round-trip fidelity is preserved or improved (M1 must not
  regress `TryGetProperty` typing). **Met:** `PropertyStoreFidelityTest` pins int/long/double/bool/
  string/DateTime/null round-trips, missing keys, empty/null sets, and de-boxing.

## 5. Notes

M1's boxing is partly inherent to a schemaless `object` property model; the *container* overhead is
not, and is the bigger, safer win (measured −78% of the per-vertex property overhead). M2 trades a
small amount of transient write-path allocation (an interned-key copy of each incoming property
map) for the retained de-duplication — a good trade for a footprint theme. M5 is **deferred** to
`persistence-hardening` because it changes the on-disk format and must land behind that theme's
format versioning; it is **not** implemented here.

**M1 behaviour change (safer):** `SetProperty` on a property-less element previously threw a
`NullReferenceException` (it called `Add` on a null `ImmutableDictionary`); it now allocates the
store and adds the first property. This is strictly safer — a former crash becomes the obvious
"add" intent — and is pinned by
`PropertyStoreFidelityTest.AddPropertyToPropertyLessElement_Succeeds`.

**N1 (accepted trade-off — not fixed here):** `GetAllProperties()` now builds a fresh
`ImmutableDictionary` on each call (it used to hand back a cached reference), a minor allocation on
the save and DTO-read paths. This is inherent to the compact-array design and is deliberately **not**
optimised in this theme: routing serialization through the raw sorted store would change the
on-disk property **byte order** (ordinal, vs. the old dictionary hash order). That is
load-compatible but violates this theme's "on-disk format unchanged" invariant, so a
serialization-path accessor is **deferred to `persistence-hardening`** to land behind that theme's
format-version gate (with M5). See `features/persistence-hardening/plan.md`.

**Intern-table bound:** the M2 intern table is capped (a generous, schema-far-exceeding
1,000,000 distinct strings) and stops adding past the cap (interning becomes a value-equal no-op),
so a pathological high-cardinality-key/label workload cannot pin unbounded strings for the process
lifetime. It is cleared on a full reset (`TabulaRasa`/`Dispose`) but **not** on `Trim`, where the
interned strings are still referenced by the surviving elements.

**Transaction lifecycle:** read created-models
(`GetCreatedVertices()`/`VertexCreated`/`GetCreatedEdges()`) promptly after `WaitUntilFinished()` —
they are dropped at the next `Trim`, **including M4's auto-trim**, after which the handles read
empty.
