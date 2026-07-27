# Plan: documentation site

Phased so each phase leaves the tree buildable. Branch: `feature/docs-site`.

## Phase 1 — Scaffold

- Create the Astro 5 + Starlight project in `docs/` (npm, committed `package-lock.json`).
- `astro.config.mjs`: `site`/`base`, title "Fallen-8", logo, favicon, sidebar skeleton,
  Expressive Code (bundled), Pagefind (bundled).
- Add `.gitignore` entries: `docs/node_modules/`, `docs/dist/`, `docs/.astro/`,
  `docs/public/openapi/`.
- Verify `npx astro build` succeeds on the empty skeleton.

## Phase 2 — Migrate content

- Move the 24 `docs/*.md` into `docs/src/content/docs/` under the IA folders.
- Add `title` + `description` frontmatter (title from the H1; H1 then removed to avoid a
  duplicate heading).
- Rewrite intra-doc `.md` links and `../pics/` / `images/` links to base-aware
  Starlight/Astro references.
- Copy the needed `pics/` assets into `docs/src/assets/`; move `docs/images/` into the site.
- Author `index.mdx` (splash hero + feature `<CardGrid>` of README key features).
- Author Getting Started / Samples multi-shell examples as `<Tabs>`.
- Wire client-side Mermaid; confirm `architecture.md` renders.

## Phase 3 — API reference + llms.txt

- `api-reference.mdx`: client island embedding Scalar from the base-aware OpenAPI URL, with
  graceful fallback when the file is absent.
- Add `starlight-llms-txt` (or current equivalent); verify `/llms.txt` + `/llms-full.txt`.

## Phase 4 — CI workflow

- Add `.github/workflows/docs.yml` (export OpenAPI -> build -> deploy Pages), path-filtered,
  coexisting with `buildAndTest.yml`. Verify action versions are current.

## Phase 5 — README trim + CLAUDE.md link fixes + graduate

- Slim `README.md` to a landing that links to the live site (Step 8), losing no detail (all
  preserved on the site).
- Fix `CLAUDE.md`'s `docs/*.md` link targets and add the "docs/ is now the Starlight site"
  note.
- Move `features/open/docs-site/` -> `features/done/docs-site/`.
- Final: `npx astro build` clean; report local preview commands, the one-time Pages setting,
  the live URL, package/version decisions, and anything untested.

## Optional (only after MVP builds + deploys)

- Live graph viz island (Sigma.js v3 + graphology), client-only, fed by a committed static
  graph JSON. Docs-local; no code lifted from `fallen-8-web-ui`.
