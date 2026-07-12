# Memory Footprint — Specification

> **Status:** Planned. Memory/GC improvements from the review. `core-storage-representation`
> landed only the **master-store** portion of the structural win; its adjacency-representation
> portion (Phase 4) is **deferred** there, so this theme owns both the still-outstanding adjacency
> overhead and the remaining per-element and lifecycle costs.

## 1. Scope

Reduce steady-state bytes/element and allocation churn, on top of the structural change in
`core-storage-representation`. That theme's landed work removes only the **master-store**
per-element overhead (the ~48 B/edge `ImmutableList` tree node became an ~8 B array slot); the
two per-vertex adjacency `ImmutableList`s (~96 B/edge) and the per-vertex adjacency
`ImmutableDictionary`s (~200–400 B/vertex) are **still outstanding**, because adjacency flattening
(`core-storage-representation` Phase 4) is deferred — that overhead remains for this theme to
address.

## 2. Findings & targets

| # | Finding | Location | ≈ Saving | Effort |
|---|---------|----------|----------|--------|
| M1 | Property store is `ImmutableDictionary<string,object>` (~100 B container for one property) with **boxed** value types (~24 B/box) | `AGraphElementModel.cs:64,181` | ~60–80 B/entry container + box per value-type property | M |
| M2 | Labels and property-ids are **not interned on the runtime create path** (the *load* path already dedups via the string token table) | `AGraphElementModel.cs:82`; `Fallen8.cs:196` | ~30–40 B per duplicate string | S–M |
| M3 | Completed transactions retain their definition/property data until `Trim` (`Cleanup()` only runs in `Trim`) → ~2× transient property data | `Transaction/TransactionManager.cs:42,159`; `Transaction/CreateVerticesTransaction.cs:83` | transient 2× of inserted property data | S |
| M4 | Removed elements keep their full body (properties + adjacency) until `Trim` | `Fallen8.cs:666,1049` | full body of removed elements | S |
| M5 | `EdgeModel.EdgePropertyId` written/read via the **non-tokenized** string path on load (dup per edge) | `Persistency/PersistencyFactory.cs:918,955` | ~30–40 B/edge (load) | S |
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
- **M5:** route `EdgePropertyId` through `WriteOptimized`/`ReadOptimizedString` so it shares the
  token-table instance (also dedups against the adjacency key). (Overlaps `persistence-hardening`.)
- **M6:** ship a sane default `SubGraphQuota`; per-subgraph overhead shrinks once
  `core-storage-representation` lands.

## 4. Acceptance criteria

- A large-graph (e.g. 1M V / 10M E) resident-memory measurement shows a material drop vs. baseline
  after M1+M2 (on top of the storage change).
- Insert-heavy workload shows lower peak (M3) and steady (M4) memory; a soak test confirms no
  unbounded growth from retained transactions/tombstones.
- All existing tests pass; property typed-round-trip fidelity is preserved or improved (M1 must not
  regress `TryGetProperty` typing).

## 5. Notes

M1's boxing is partly inherent to a schemaless `object` property model; the *container* overhead is
not, and is the bigger, safer win. Coordinate M5 with `persistence-hardening` (both touch the
serializer's string handling).
