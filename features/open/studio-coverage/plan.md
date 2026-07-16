# Studio Coverage — Implementation Plan

> **Status:** Implemented on branch `feature/studio-coverage` (phases 0–5, one commit per
> phase, 2026-07-17); awaiting review/merge. Contracts and arbitration rationale live in
> [spec.md](./spec.md). Phases are ordered by leverage and dependency, not by capability
> size; each phase is independently shippable and leaves the Studio consistent.

## Phase 0 — Fix the "NaN MiB" tile

Delete the phantom `freeMemory` field from the client `StatusREST` type and the Dashboard
tile that renders it (spec §9). Standalone, minutes-sized; do it first so the Dashboard is
honest before anything new lands on it.

## Phase 1 — Analytics screen shell + Graph shape + schema cache

- New route `/analytics` + rail item `∑ Analytics`; screen ships with only the **Graph
  shape** panel (spec §4): on-demand Compute, sampled chip, label/property/index/degree
  blocks, index-row "scan" cross-link into Query.
- `useGraphShape` per-instance cache + `<datalist>` wiring on Query's `propertyId` /
  `indexId` inputs (further consumers arrive with their phases).
- Rationale for going first: closes discovery gap G-3 and de-risks every later phase's
  identifier inputs.

## Phase 2 — Stored query library surfaces

- `filters [inline|stored]` source toggle on Path and Subgraph; read-only `DelegateSlot`
  chrome for the selection; draft fields `filterSource` / `storedQuery`; spec builders send
  `storedQuery` and omit fragments (spec §5.1).
- "Save as stored query…" dialog in inline mode; 403 rendered as the single security
  sentence (spec §5.2).
- Dashboard "Stored queries" panel: list/expand/diagnostics, Open in Path/Subgraph, Delete
  behind ConfirmDialog (spec §5.3).
- `endpoints.ts` gains the four `/storedquery` wrappers; `types.ts` specs gain the optional
  `storedQuery` field.
- Highest-priority correctness item — restores Path/Subgraph on locked-down instances. May
  swap with Phase 1 freely; the two are independent.

## Phase 3 — Vector scan kind + index management extension

- `ScanKind` `"vector"` + conditional fields with client-side dimension counter; scored
  results through the shared `ElementTable` score column (built here if Phase 4 hasn't);
  metric legend from the response (spec §6).
- `VectorIndex` creation preset (dimension/metric → `pluginOptions`) and the collapsed
  single-element "Vector add" sub-section with the `[property|explicit]` toggle.
- Wrappers for `POST /scan/index/vector` and `PUT /index/vector/{indexId}`.

## Phase 4 — Analytics runner

- **Run** and **Result** panels on the Analytics screen (spec §3.2–3.3): algorithm picker
  from `GET /analytics/algorithms`, scope/bounds rows, conditional knob editors, write-back
  disclosure + ConfirmDialog, first-class 408/429 messages, top-K hydration with the score
  column, partition table with "Members…" paging, write-back success cross-link line.
- Wrappers for the three `/analytics` endpoints.
- The follow-up canvas "color/size by property" mode is explicitly OUT of this phase — its
  own feature decision.

## Phase 5 — Interchange row

- Administration second row (spec §7): Export blob download + collapsed label filter;
  Import file picker with verbatim server-error relay and stat-cell success summary; size
  warning pointing at curl.
- Wrappers for `GET /bulk/export` / `POST /bulk/import`.

## Deferred — change feed inspector

No phase. Revisit triggers and the recorded tail-console shape are in spec §8; if a trigger
fires, that design is the starting point, not a new debate.

## Testing expectations (all phases)

Per repo convention: MSTest stays backend-only; the web UI's existing test approach
(component/behaviour tests alongside the screens) covers — per phase — the toggle's
mutual-exclusivity guarantees, error-state rendering (403/408/409/429 wording), datalist
fallback when no snapshot exists, dimension-counter validation, and the score column's
ordering legend. Every phase also updates `features/done/web-ui/` docs ONLY by pointer if
the design doc's gap list (G-3) changes — the living doc for this work is this feature's
directory.
