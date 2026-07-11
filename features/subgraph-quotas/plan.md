# Subgraph Quotas — Plan

Companion to [spec.md](./spec.md).

## Phase 0 — Reproduce
- Add a test showing unbounded creation (many subgraphs, including empty-filter full clones)
  succeeds today with no ceiling.

## Phase 1 — Quota model
- Add `SubGraphQuota` (MaxSubGraphCount, MaxElementsPerSubGraph, MaxTotalElements; unlimited
  by default) and expose it on `SubGraphFactory`.

## Phase 2 — Enforcement
- Enforce `MaxSubGraphCount` before creating.
- Enforce per-subgraph size: prefer passing the element cap into the algorithm to abort
  early; otherwise check the materialized count and discard+fail. Enforce total size across
  registered subgraphs.
- On breach: do not register, do not mutate the source, return a distinct failure the REST
  layer maps to a clear 4xx.

## Phase 3 — REST surface & defaults
- The API app configures conservative defaults and maps quota breaches to `409`/`413` with
  the offending limit in the message. Document the knobs.

## Phase 4 — Verify & document
- Full test pass; update [../subgraph/spec.md](../subgraph/spec.md) §9 to note quotas exist.

## Status
- [ ] Phase 0 — reproduce
- [ ] Phase 1 — quota model
- [ ] Phase 2 — enforcement
- [ ] Phase 3 — REST surface & defaults
- [ ] Phase 4 — verify & document
