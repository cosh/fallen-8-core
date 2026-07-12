# Memory Footprint — Plan

Companion to [spec.md](./spec.md). Findings M1–M6 defined there.

## Phase 1 — Quick wins (S, low risk)
- **M2** intern labels/property-ids/`EdgePropertyId` on the create + `SetProperty` paths.
- **M3** release transaction definitions/created-models at `Finished` (not at `Trim`); bound
  transaction-state history. (Shared with engine-performance P9.)
- **M5** tokenize `EdgePropertyId` in the serializer. (Shared with persistence-hardening.)
- **M6** ship a default `SubGraphQuota`.

## Phase 2 — Removed-element reclamation (S–M)
- **M4** free properties/adjacency on removal, and/or auto-trim past a tombstone-ratio threshold.
  Add a soak test asserting bounded growth under churn.

## Phase 3 — Property storage compaction (M)
- **M1** replace per-element `ImmutableDictionary<string,object>` with a compact sorted array (or
  small dictionary); de-box common value types. Guard `TryGetProperty` typing with tests.

## Measurement
- Establish a baseline resident-memory measurement (large synthetic graph) before Phase 1 and
  re-measure after each phase; record numbers here. Depends on `core-storage-representation` for
  the dominant structural saving.

## Status
- [ ] Phase 1 — interning, tx release, tokenized edge-property-id, default quota
- [ ] Phase 2 — removed-element reclamation
- [ ] Phase 3 — property storage compaction + de-boxing
