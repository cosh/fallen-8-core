# README Refresh with Visuals — Plan

Companion to [spec.md](./spec.md).

## Phase 0 — Audit
- List every stale claim/link in the current README (Swagger endpoints, ports, missing
  subgraph, missing .NET 10) and every `pics/` asset, marking `swaggerDoc.png` for
  replacement.

## Phase 1 — Produce the images
- **Architecture diagram** (SVG, light/dark-safe): engine (`fallen-8-core`), API app,
  plugins (index/algorithm/service), transactions, persistence.
- **Subgraph illustration** (SVG): a source graph → a pattern (vertex/edge/vertex) → the
  extracted subgraph, showing pruning.
- **Scalar API screenshot** (PNG): run the app in Development, capture `/scalar/v0.1`.
- Commit under `pics/`; remove the stale `swaggerDoc.png`.

## Phase 2 — Rewrite the README
- Fix API section (Scalar + OpenAPI URLs, correct ports), add .NET 10, refresh the sample
  walkthrough.
- Add a **Subgraph** section with a short example and the new illustration, linking
  [../subgraph/README.md](../subgraph/README.md).
- Embed all images with descriptive alt text; keep the light/dark logo.

## Phase 3 — Verify rendering
- Confirm images resolve via the `main` raw URLs / relative paths and render on light and
  dark GitHub themes. Check all links.

## Status
- [ ] Phase 0 — audit
- [ ] Phase 1 — images (architecture, subgraph, Scalar screenshot)
- [ ] Phase 2 — rewrite README
- [ ] Phase 3 — verify rendering
