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
- [x] Phase 0 — reproduce (DefaultQuota_IsUnlimited pins prior unbounded behaviour)
- [x] Phase 1 — quota model (`SubGraphQuota`, exposed as `SubGraphFactory.Quota`)
- [x] Phase 2 — enforcement (count pre-check, per-subgraph and total size post-materialization)
- [x] Phase 3 — REST 409 count pre-check; size/total breaches → 400 with a quota message
- [x] Phase 4 — verify & document

## Outcome

- `SubGraphQuota` (MaxSubGraphCount, MaxElementsPerSubGraph, MaxTotalElements; all
  `int.MaxValue` = unlimited by default) is exposed on `SubGraphFactory.Quota`, with
  `SubGraphCount` for inspection.
- `CreateAndRegisterSubGraph` rejects before materialization when the count ceiling is
  reached, and after materialization when the per-subgraph or aggregate element limit would
  be exceeded. On breach it logs, returns false, registers nothing, and — because the
  subgraph is a separate graph instance — never mutates the source.
- The REST controller pre-checks the count ceiling and returns `409 Conflict`; per-subgraph
  and total-size breaches surface as `400` with a message noting the quota (a distinct 413
  was not feasible because transaction failures do not propagate a typed reason to the
  controller).
- Tests: `SubGraphQuotaTest` covers count, per-subgraph size (reject + at-limit), total
  size, unlimited default, source-unchanged-on-breach, and the controller 409.

## Deviation from plan

The plan preferred passing the per-subgraph cap into the algorithm for early abort. The
implementation checks size after materialization instead (simpler, no algorithm-interface
change); the source graph is never touched, so discarding an over-limit result is safe. If
building large-but-rejected subgraphs becomes a cost concern, early abort can be added
later.
