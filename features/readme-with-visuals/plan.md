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
- [x] Phase 0 — audit (Swagger→Scalar, missing subgraph, .NET 10, stale swaggerDoc.png)
- [x] Phase 1 — images: `pics/architecture.svg`, `pics/subgraph-illustration.svg`, and a real
      `pics/scalarApiReference.png` captured from the running Scalar UI (headless Chrome);
      removed the stale `pics/swaggerDoc.png`
- [x] Phase 2 — README rewritten (Scalar + OpenAPI URLs, .NET 10, architecture section,
      Subgraphs section linking `features/subgraph/`)
- [x] Phase 3 — verify (diagrams are self-contained SVG cards that read on light/dark; the
      screenshot was rendered from the live app and visually confirmed)

## Outcome

- **Diagrams** are hand-authored SVGs (`pics/architecture.svg`, `pics/subgraph-illustration.svg`)
  using self-contained coloured cards, so they read on both light and dark GitHub themes
  without relying on the page background. Each README image has descriptive alt text.
- **Screenshot**: captured live from `https://localhost:5090/scalar/v0.1` with headless
  Chrome (`--virtual-time-budget` to let the SPA render), replacing the stale Swagger PNG.
  It shows the SubGraph endpoint group and the XML-doc operation summaries.
- **README**: Swagger references replaced with Scalar/OpenAPI, .NET 10 called out, an
  Architecture section and a Subgraphs section (with example + link) added; the existing
  sample walkthrough retained.

## Note

The screenshot was capturable here because Chrome was available; if regenerated elsewhere,
run the app in Development and headless-capture `/scalar/v0.1`. Image links use repo-relative
paths so they render on any branch/fork.
