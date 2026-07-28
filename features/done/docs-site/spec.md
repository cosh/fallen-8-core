# Documentation site (Starlight on GitHub Pages)

## Goal

Turn the existing Markdown documentation set into a fast, searchable, navigable static
site built with [Starlight](https://starlight.astro.build/) (Astro 5), and deploy it
continuously to GitHub Pages at `https://cosh.github.io/fallen-8-core/` via a new,
separate GitHub Actions workflow.

This is a presentation layer over the docs we already have; it does not invent product
behaviour and does not touch the engine, the apiApp, F8 Studio, or the Docker configs.

## Decisions (confirmed with the maintainer before scaffolding)

The task brief assumed docs were scattered across the README and `features/`. The repo has
since grown a canonical, user-facing Markdown docs set in top-level `docs/` that the README
links into and that CLAUDE.md mandates as "the home users read." Three forks were resolved:

1. **The site takes over `docs/`.** `docs/` becomes the Astro/Starlight project root (its
   own `package.json`, `package-lock.json`, `astro.config.mjs`, `src/`, `public/`). The
   existing 24 deep-dive `*.md` files move into `docs/src/content/docs/` and become the
   site's pages.
2. **Content is the existing `docs/*.md`, moved once (no duplication).** Each explanation
   still lives exactly once, now inside the Starlight content collection. This keeps the
   repo's "one home per explanation" gate intact. Only the new navigational shell
   (homepage, Getting Started, Samples with multi-shell tabs) is authored fresh.
3. **The Features set is the README "Key features" list.** One page per user-facing
   capability = the README key-feature bullets, each already backed by a `docs/*.md`
   deep-dive. We do NOT create a page per `features/` subdirectory (most are internal
   engineering tasks, not user-facing capabilities).

## Consequence: link rewrites

Taking over `docs/` moves the `docs/*.md` files under `docs/src/content/docs/`, so the old
`docs/<name>.md` paths stop resolving on GitHub. Live references to fix:

- **`README.md`** — slimmed to a landing (Step 8) and pointed at the live site.
- **`CLAUDE.md`** — its `docs/*.md` pointers updated to the new source locations, plus a
  one-line note that `docs/` is now the Starlight project.

`features/*/spec.md` and `*/plan.md` are frozen historical records (CLAUDE.md: specs "are
historical records and are not rewritten"), so their `docs/*.md` links are left as-is; they
already point at a moment in time. `cleanup-report.md` is a transient artifact, left as-is.

## Non-goals

- No new REST API reference: the API Reference page **embeds Scalar** against the existing
  OpenAPI document; it does not re-document endpoints by hand.
- No enterprise/site-search infra: Starlight's built-in Pagefind is the search.
- No content invented to fill a template section; absent README sections are skipped.
- The live graph visualization is an explicitly optional stretch goal, attempted only after
  the MVP builds and deploys.

## Information architecture

Sidebar (Starlight groups). Source file in parentheses; all move into
`docs/src/content/docs/`.

- **(root)** Home — `index.mdx` (new, splash template).
- **Getting Started**
  - Running (`running.md`) — the one-command default, bare `dotnet run`, config, security
    switches, GPU. Multi-shell samples become `<Tabs>`.
  - Security (`security.md`).
- **Samples** — Sample gallery walkthrough (`samples.md`).
- **Features** (one page per README key feature)
  - Graph model (`graph-model.md`), Delegates (`delegates.md`), Path finding
    (`path-finding.md`), Subgraphs (`subgraphs.md`), Graph analytics
    (`graph-analytics.md`), Stored queries (`stored-queries.md`), Indexes (`indexes.md`),
    Vector search (`vector-search.md`), Semantic traversal (`semantic-traversal.md`),
    Bulk import/export (`bulk-import-export.md`), Live change feed (`change-feed.md`),
    Save games (`save-games.md`), Namespaces (`namespaces.md`), Observability
    (`observability.md`), Plugins (`plugins.md`), Plugin registration
    (`plugin-registration.md`).
- **F8 Studio** — Studio (`studio.md`), incl. the NL assist.
- **AI agents** — MCP server (`mcp-server.md`).
- **Reference**
  - API Reference (new `api-reference.mdx`, embeds Scalar).
  - REST API (`rest-api.md`).
  - Architecture (`architecture.md`, keeps its Mermaid diagram).
- **Help**
  - Troubleshooting (`troubleshooting.md`).
  - Debugging in VS Code (`debugging.md`, moved in from the old root `DEBUGGING.md`).
  - License.

The README key-feature bullets for **REST API, F8 Studio, MCP server, Security** are also
capabilities; they live in their natural sections above (Reference / F8 Studio / AI agents /
Getting Started) rather than duplicated under "Features". Every key-feature bullet still gets
a homepage card.

`docs/README.md` (the current index tables) is not carried as a page; the sidebar plus the
homepage grid replace it. Its intro line seeds the homepage tagline.

## Homepage (`index.mdx`, splash template)

- Hero: `pics/F8White.svg` (copied into site assets), tagline from the current README intro,
  action buttons: primary "Get Started" -> Running, secondary "API Reference" -> Scalar page,
  and "View on GitHub" -> `https://github.com/cosh/fallen-8-core`.
- Feature grid: a `<CardGrid>` of every README key-feature bullet as a base-aware
  `<LinkCard>` to its page (all bullets have a page, so no plain `<Card>` is needed).
  Descriptions transcribed from the README bullets.
- Nothing fabricated below the grid.

## Tooling

- Astro 5 + `@astrojs/starlight` (latest), Node 20+, npm with a committed
  `package-lock.json`. Self-contained under `docs/`; not wired into the root `package.json`
  or the UI build.
- `astro.config.mjs`: `site: 'https://cosh.github.io'`, `base: '/fallen-8-core'`. All
  internal links and assets base-aware.
- Header logo `pics/F8White.svg`; favicon from `pics/F8Icon.ico`. Light/dark handled (the
  brand has `F8White.svg` and `F8Black.svg`).
- Expressive Code (bundled) for shell samples; `<Tabs>`/`<TabItem>` for multi-shell
  (HTTP / cURL / PowerShell / Bash) examples.
- Pagefind search (bundled, default).
- **Mermaid**: rendered client-side (a lightweight Astro/remark integration, no build-time
  headless browser) so `architecture.md`'s Mermaid diagram (its single source) renders as
  authored. If the chosen plugin proves flaky in CI, fall back to pre-rendering that one
  diagram to a committed SVG. Not an MVP blocker.
- `starlight-llms-txt` (or current equivalent) to emit `/llms.txt` and `/llms-full.txt`.

## API Reference (reuse Scalar)

- A dedicated route embeds Scalar, reading the OpenAPI document from
  `/fallen-8-core/openapi/v0.1.json` (served from `docs/public/openapi/v0.1.json`).
- The JSON is produced in CI, not committed; `docs/public/openapi/` is gitignored.
- The page is a client island that fetches the base-aware URL and either mounts Scalar or,
  when the file is absent (local dev, or a failed CI export), shows a short note plus a link
  to the running endpoint. Builds succeed with or without the file.

## Assets

- Copy (not move/modify) the logos and diagrams the site needs from `pics/`
  (`F8White.svg`, `F8Black.svg`, `F8Icon.ico`, `subgraph-illustration.svg`,
  `scalarApiReference.png`) into `docs/src/assets/`.
- `docs/images/` (the screenshots) moves with `docs/` into the site's assets. Intra-doc
  image links are rewritten to Astro asset imports / base-aware paths.

## CI: `.github/workflows/docs.yml` (new, separate)

- Triggers: `push` to `main` path-filtered to `docs/**`, `fallen-8-core-apiApp/**`, and
  `.github/workflows/docs.yml`; plus `workflow_dispatch`. Does not run on unrelated changes;
  does not touch `buildAndTest.yml`.
- `permissions: contents: read, pages: write, id-token: write`; `concurrency: group: pages`.
- Build job: checkout; setup-dotnet (10.0.x; no `global.json` to honour); **export OpenAPI**
  (continue-on-error) by running the built app with `ASPNETCORE_ENVIRONMENT=Development` and
  an http-only URL, polling `/openapi/v0.1.json` into `docs/public/openapi/v0.1.json`; then
  build with `withastro/action@v3` (path `./docs`, npm, Node 20).
- Deploy job: `actions/deploy-pages@v4` to the `github-pages` environment.
- No packages added to the .NET project; the app is only run to emit its spec.

## Impact on existing features (cross-feature impact check)

- **README key-features list**: this site is downstream of it. New key features keep getting
  a `docs/*.md`; that page now also becomes a site page automatically (it lives in the
  Starlight content collection). Add a one-line note to CLAUDE.md so future feature work
  knows `docs/*.md` == a site page.
- **OpenAPI snapshot / MCP coverage gates**: untouched. The docs workflow consumes the
  OpenAPI document read-only; it does not regenerate the committed snapshot
  (`scripts/update-openapi-snapshot.ps1`) and does not affect `McpRestCoverageTest`.
- **Studio / NL-assist**: no engine or REST contract change, so no Studio or fine-tune
  dataset impact. No `RETRAIN-LOG.md` entry needed.
- **Architecture diagrams**: this feature adds a new deployable-adjacent artifact (a docs
  site) but does not change how clients reach Fallen-8, so the README and `architecture.md`
  architecture diagrams do not change. The docs site is tooling, not a runtime channel.
- **`buildAndTest.yml`, `release.yml`, `refresh-sbom.yml`**: unchanged; the new workflow is
  additive and path-filtered.

## Acceptance criteria

- `npx astro build` succeeds in `docs/` with no errors; `npm run dev` serves locally, with
  or without `docs/public/openapi/v0.1.json` present.
- The built site works under `/fallen-8-core/` with no broken internal links or missing
  assets.
- Features section has exactly one page per README key feature; nothing invented.
- Homepage uses the splash template with a hero and a feature `<CardGrid>`; no fabricated
  marketing section.
- Multi-shell examples render as tabs; Pagefind returns results.
- `docs.yml` exports the OpenAPI doc, builds, and deploys to Pages; the API Reference page
  renders Scalar when the file is present and degrades gracefully when absent;
  `buildAndTest.yml` is unchanged.
- `/llms.txt` and `/llms-full.txt` are generated.
- No changes to .NET source, `fallen-8-web-ui` app code, or docker configs beyond adding
  `docs/`, the workflow, `.gitignore` entries, the README trim, and the CLAUDE.md link fixes.

## Risks / to verify during implementation

- Exact current package names/versions for the Mermaid and llms-txt plugins (verified against
  their READMEs at implementation time, not from memory).
- Scalar's current recommended embed method for Astro/MDX.
- Relative-link rewriting across the reorganized content folders (validated by the build's
  link checker / a manual pass).
- Whether editing CLAUDE.md is in bounds: it is a necessary, mechanical consequence of the
  take-over-`docs/` decision (link targets only, plus one note line); flagged here for review.

## Outcome (as built)

- **Tooling (current, not the brief's stale "Astro 5"):** Astro 7 + `@astrojs/starlight` 0.41
  (scaffolded from the official Starlight template), `astro-mermaid` (client-side Mermaid, no
  build-time browser), `starlight-llms-txt` (emits `/llms.txt`, `/llms-full.txt`,
  `/llms-small.txt`), and `starlight-links-validator` (build fails on broken internal links).
  CI builds on Node 22.
- **Structure:** `docs/` is the Starlight project; the 24 deep dives live flat in
  `docs/src/content/docs/` (so their sibling links stayed valid) and are grouped by a manual
  sidebar. New pages: `index.mdx` (splash home + feature `<CardGrid>`), `api-reference.mdx`
  (Scalar embed, graceful fallback), `license.md`, and `debugging.md` (the old root
  `DEBUGGING.md`, moved in as a native page so the site never bounces to raw GitHub; its
  doc-sync note travels with it).
- **Tabs:** 17 pages had stacked bash/PowerShell blocks converted to `<Tabs syncKey="shell">`.
- **Repo-tree links** (`../fallen-8-core/*.cs`, `../samples/`) were rewritten to `github.com`
  URLs.
- **Verification:** `astro build` green with the link validator passing; a deterministic
  content-preservation diff over every Tabs-converted page (it caught and fixed one dropped
  "See also" bullet); `npm run dev` serves locally with the OpenAPI file absent.

## Deferred / not done (honest)

- **F8 Studio "Docs" link:** the maintainer asked that the UI point at the docs site once it is
  live. That is a `fallen-8-web-ui` app change (this PR's ground rules protect it) and cannot
  work until the site is deployed, so it is a tracked follow-up, not part of this PR.
- **Optional live graph visualization (Sigma.js):** stretch goal, not attempted. MVP first.
- **Untested here:** the live GitHub Pages deployment (needs merge plus the one-time
  Settings > Pages > Source: GitHub Actions), and the visual rendering of the Scalar "present"
  path and Mermaid diagrams (both verified at the markup/bundle level, not rendered in a
  browser in this environment).
