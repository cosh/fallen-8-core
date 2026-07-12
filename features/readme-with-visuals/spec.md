# README Refresh with Visuals — Specification

> **Status:** Planned. Tracked by its GitHub feature issue.

## 1. Problem

The top-level [README.md](../../README.md) is out of date and thin on visuals:

- It documents the API via **Swagger** (`/swagger/index.html`) and shows
  `pics/swaggerDoc.png`, but the app migrated to **Scalar** (`/scalar/v0.1`, OpenAPI at
  `/openapi/v0.1.json`). The screenshot and links are stale.
- It predates the **subgraph** feature entirely — a major capability is undocumented.
- Beyond the logo and one (outdated) screenshot, there are no diagrams explaining the
  architecture or how features work.

## 2. Goals / non-goals

**Goals**
- Accurate content: Scalar (not Swagger), correct ports/endpoints, .NET 10, and a section
  covering the subgraph feature (linking [../subgraph/README.md](../subgraph/README.md)).
- **Visuals are a first-class requirement of this feature** (see §3). The refreshed README
  must include, at minimum:
  1. the existing Fallen-8 logo (kept),
  2. a **current API reference screenshot** (Scalar) replacing the stale
     `pics/swaggerDoc.png`,
  3. a new **architecture diagram** (engine / API app / plugins / persistence),
  4. a new **subgraph illustration** showing a source graph, a pattern, and the extracted
     subgraph.
- All images live under `pics/` and are referenced with repo-relative or
  `raw.githubusercontent.com/.../main/pics/...` URLs consistent with the current README.

**Non-goals**
- Changing the product; this is documentation + images only.
- A full docs site.

## 3. Image requirements (explicit)

- **Format:** diagrams as SVG (crisp, theme-friendly, matches existing `F8*.svg`);
  screenshots as PNG. Reasonable dimensions; keep file sizes modest.
- **Theme:** diagrams should be legible on both light and dark GitHub themes (avoid
  hardcoded backgrounds that vanish on one theme), mirroring the light/dark logo pair
  already in `pics/`.
- **Provenance:** screenshots generated from the running app (Development, Scalar at
  `/scalar/v0.1`); diagrams authored as source-controlled SVG so they can be regenerated.
- **Alt text:** every image reference includes descriptive alt text.
- **No stale assets:** remove or replace `pics/swaggerDoc.png`.

## 4. Acceptance criteria

- README shows the logo, a current Scalar screenshot, an architecture diagram, and a
  subgraph illustration, each with alt text.
- No remaining references to Swagger; endpoints/ports are correct against the current app.
- A subgraph section exists and links the feature docs.
- Images render correctly on github.com in both light and dark themes.

## 5. Notes

- Existing assets in `pics/`: `F8White.svg`, `F8Black.svg`, `F8Icon.ico`,
  `iconwhite.png`, `iconblack.png`, `swaggerDoc.png` (to be replaced).
- The API build already emits OpenAPI at `/openapi/v0.1.json`; the Scalar UI at
  `/scalar/v0.1` is the source for the screenshot.
