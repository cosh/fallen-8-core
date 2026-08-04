# F8 Studio: per-section "How does this work?" help - spec

Status: open (spec/plan only). Owner: TBD. Feature branch: feature/studio-section-help (from main).
Related: [web-ui](../../done/web-ui/spec.md), [studio-first-run](../../done/studio-first-run/spec.md),
[docs-site](../../done/docs-site/spec.md), [standalone-ui](../../done/standalone-ui/spec.md).

> **One-home rule (feature docs-site):** the *explanation* of every feature lives on its
> Starlight page under `docs/src/content/docs/`. This feature adds no new prose about how any
> feature works; it only wires each Studio section to the 1-3 pages that already explain it.
> The section-to-page mapping is the only new content, and it lives in exactly one file.

## Problem

A newcomer landing on any Studio screen (Path, Subgraph, Indexes, Knowledge, ...) has no
in-context route to the documentation that explains what that screen does. Today the only
doc affordance is a single top-bar `docs` pill (in `fallen-8-web-ui/src/app/AppShell.tsx`) that
opens the docs *home* (`https://cosh.github.io/fallen-8-core/`), dropping the reader on the
splash page with no idea which of ~30 pages is relevant to the screen they are on. The in-app
`fieldHelp` tooltips (`fallen-8-web-ui/src/lib/fieldHelp.ts`) explain individual form fields but
never answer the higher-level question "what is this whole section for, and where do I read
more?".

We want a dedicated, discoverable button on every main section that answers exactly that:
**"How does this work?"**, opening a short list of the 1 to 3 docs pages that explain the
feature behind the current screen.

## Starting position (already good - do not regress)

- The nav sections are declared once, centrally, in the `NAV` array in
  `fallen-8-web-ui/src/app/AppShell.tsx` (each entry: `{ leaf, label, icon, scoped }`), and the
  shell already computes and highlights the active section. That single source is what this
  feature keys its help registry against.
- The existing top-bar `docs` pill (`data-testid="docs-link"`, opens the docs site in a new tab
  with `target="_blank" rel="noopener noreferrer"`) stays exactly as-is. It remains the "docs
  home" entry point; the new per-section button is the "docs for *this* screen" entry point.
- The `fieldHelp` system (`lib/fieldHelp.ts` + `components/Field.tsx`) stays as-is and is
  unaffected. Field-level help and section-level help are complementary, not overlapping.
- Styling primitives already exist: `.panel` / `.panel-title`, `.btn`, the border-pill chip
  style used by the `docs` pill, and the dark-theme tokens in `src/index.css`. The new UI reuses
  these; it introduces no new design language.

## Decisions (locked with the requester)

| Decision | Choice |
|---|---|
| Interaction | **Anchored popover.** The button drops a small popover listing 1-3 links, each with a one-line blurb; each row opens the docs page in a new tab. Not a modal, not an inline strip. |
| Links per section | **1 to 3**, ordered primary-first. Never more than 3. |
| Where it renders | **Once, by the shell**, driven by the active nav leaf, in the top bar's right-hand chip cluster next to the `docs` pill (top-right, reads as the top of the current screen). Not wired into each screen individually (avoids 14 near-duplicate edits and drift; keeps one home). |
| Mapping home | **One file:** `fallen-8-web-ui/src/lib/sectionHelp.ts`, keyed by the same `leaf` strings as `NAV`. Mirrors the `fieldHelp.ts` pattern. |
| Benchmark gap | The Benchmark section has no docs page today, so **a new Benchmark docs page is authored as part of this feature** so every section maps to its own home. |
| Link target | External published docs site, new tab, `rel="noopener noreferrer"` (same posture as the existing `docs` pill). Links are stored as slugs and resolved against one `DOCS_BASE` constant. |
| Label | Button text "How does this work?" is also the button's accessible name (no overriding `aria-label`, per WCAG 2.5.3 Label in Name). The current section is conveyed by the button's `title` tooltip and the popover heading "How {Section} works". |

## Behaviour after the change

### 1. The button

A compact pill button appears in the top bar's right-hand chip cluster (next to the `docs` pill)
on every main section, styled like that pill (border-pill, `text-fg-dim hover:text-accent`),
prefixed with a `?` glyph and reading "How does this work?". It carries `data-testid="section-help"`.
Its visible text is its accessible name (no overriding `aria-label`, per WCAG 2.5.3); the current
section is named by its `title` tooltip (e.g. "How path finding works").

The button is present only when the active route resolves to a nav leaf that has an entry in the
section-help registry. On non-section routes (deep-linked element inspects, the first-run
"Intro" replay overlay, unknown routes) it does not render.

### 2. The popover

Clicking the button opens an anchored popover below it containing:

- A heading: "How {Section} works" (e.g. "How Path finding works").
- 1 to 3 link rows, primary first. Each row shows the docs page **title** (bold) and a
  **one-line blurb** taken from that page's `description` frontmatter, with a small outbound
  glyph. Clicking a row opens `${DOCS_BASE}${slug}/` in a new tab (`target="_blank"
  rel="noopener noreferrer"`).

The popover is keyboard-accessible: the trigger activates on Enter/Space, and the popover closes
on Escape (returning focus to the button), on an outside click, and when a link is chosen. It
never traps the whole screen the way a modal would.

### 3. The mapping registry (single home)

`fallen-8-web-ui/src/lib/sectionHelp.ts`:

```ts
// slug -> published page is `${DOCS_BASE}${slug}/`; DOCS_BASE is defined once and shared
// with the existing top-bar docs pill so there is one docs-origin constant, not two.
export interface SectionDocLink {
  slug: string;   // docs page slug, e.g. "path-finding" (NOT a full URL)
  title: string;  // page title shown as the row label
  blurb: string;  // one-line description (mirrors the page's frontmatter description)
}
export interface SectionHelp {
  heading: string;            // "How Path finding works"
  links: SectionDocLink[];    // length 1..3, ordered primary-first
}
// keyed by the SAME leaf strings used in AppShell's NAV array
export const SECTION_HELP: Record<string, SectionHelp> = { /* see table below */ };
```

### 4. Section-to-docs mapping

Primary page first; blurbs are sourced from each page's `description` frontmatter so the popover
stays in step with the docs. Slugs resolve to `https://cosh.github.io/fallen-8-core/<slug>/`.

| Section (nav leaf) | Primary | Secondary | Tertiary |
|---|---|---|---|
| Connect (`/`) | `running` | `standalone-ui` | `security` |
| Dashboard (`dashboard`) | `studio` | `observability` | `namespaces` |
| Samples (`samples`) | `samples` | `graph-model` | - |
| Save games (`/save-games`) | `save-games` | - | - |
| Browser (`browser`) | `graph-model` | `namespaces` | `bulk-import-export` |
| Query (`query`) | `delegates` | `stored-queries` | `api-reference` |
| Indexes (`indexes`) | `indexes` | `vector-search` | - |
| Path (`path`) | `path-finding` | `delegates` | `semantic-traversal` |
| Subgraph (`subgraphs`) | `subgraphs` | `delegates` | `semantic-traversal` |
| Analytics (`analytics`) | `graph-analytics` | - | - |
| Plugins (`plugins`) | `plugins` | `plugin-registration` | - |
| Canvas (`canvas`) | `studio` | `graph-model` | - |
| Benchmark (`/benchmarks`) | `benchmark` (NEW) | `running` | `architecture` |
| Knowledge (`knowledge`) | `unstructured-ingestion` | `vector-search` | `semantic-traversal` |

### 5. New Benchmark docs page

Because Benchmark is a real user-facing Studio section with no page today, author
`docs/src/content/docs/benchmark.mdx` with `title` + `description` frontmatter, register it in the
`sidebar` in `docs/astro.config.mjs` (under the **F8 Studio** group, next to `studio` and
`standalone-ui`), and, if Benchmark is classified a key feature, add its one-line entry to the
root `README.md` "Key features" list linking `https://cosh.github.io/fallen-8-core/benchmark/`
(per the CLAUDE.md README rule). Content is drawn from the existing benchmark work
(`features/done/schema-agnostic-benchmark/`); this feature does not invent benchmark behaviour,
it documents what the Benchmark screen already does.

### Limitations and named revisit triggers

- **Single fixed placement (top bar, next to the docs pill).** Revisit if a screen's own header
  needs the button inline for layout reasons; the registry/component split already allows a screen
  to opt into rendering `<SectionHelp/>` itself.
- **Blurbs are hand-mirrored from frontmatter, not imported at build time.** A drift guard test
  (below) keeps them honest against slug existence; if blurb text drift becomes a maintenance
  cost, revisit by generating the registry from the docs frontmatter at build time.

## Non-goals (right-sizing, with revisit triggers)

- **No in-app docs viewer / rendered Markdown.** Links open the external Starlight site.
  Revisit only if the product must work fully offline.
- **No replacement of the top-bar `docs` pill or the `fieldHelp` tooltips.** Both stay.
- **No new documentation prose about how features work.** The pages already exist (one-home
  rule). The single exception is the new Benchmark page, which fills a genuine gap.
- **No per-field or per-tab help beyond section level.** That is `fieldHelp`'s job.
- **No analytics/telemetry on clicks.** Revisit if the team wants to measure which sections
  drive doc reads.
- **No engine/REST/MCP surface.** This is frontend + docs only; the engine -> REST -> MCP
  propagation rule does not fire (no new operation).

## Impact on existing features (mandatory cross-feature sweep)

Swept engine <-> REST/OpenAPI <-> MCP <-> Studio UI <-> NL-assist <-> feature READMEs <->
docs-site pages <-> architecture diagrams <-> recipes/stored queries:

| Layer / feature | Impact | Handling |
|---|---|---|
| Engine (`fallen-8-core`) | **None** | No engine change. |
| REST / OpenAPI snapshot | **None** | No new route; no snapshot regeneration. |
| MCP (`fallen-8-mcp`) | **None** | No REST operation added, so the engine -> REST -> MCP coverage rule does not fire; `McpRestCoverageTest` / `McpContractTest` stay green untouched. |
| Studio UI (`fallen-8-web-ui`) | **This feature** | New `lib/sectionHelp.ts` registry + one `SectionHelp` popover component rendered by the shell; `DOCS_BASE` extracted so the top-bar `docs` pill and the new button share one origin constant. |
| NL-assist dataset / eval | **None** | No delegate/plugin/NL surface change. Reviewed: **no retrain**, no `RETRAIN-LOG.md` entry. |
| Docs site (`docs/`) | **Yes** | New `benchmark.mdx` page + `astro.config.mjs` sidebar entry; a short note in `studio.md` pointing out the per-section help button. The Starlight link-check build stays green (new page + valid internal links). |
| README | **Yes (conditional)** | If Benchmark is treated as a key feature, add its "Key features" line linking the new page. |
| Architecture diagrams (root README + `docs/architecture.md`) | **None** | No new channel or deployable; the diagrams do not change. |
| Screenshots | **Yes** | Per the UI-change rule, recapture the affected Studio screenshots that now show the "How does this work?" button (at least the screens depicted in `docs/studio.md`). |
| Tests | **Yes (additive)** | New vitest for the component + registry; new coverage + drift-guard tests (below). Existing vitest, e2e (`fallen-8-web-ui/e2e/studio.spec.ts`), and the .NET suites stay green. |
| Samples / stored queries | **None** | No change. |

**Single-home doc assignment:** how each feature works stays on its own Starlight page
(unchanged). The *only* new authored prose is `benchmark.mdx`. The section-to-page *mapping* is
owned solely by `sectionHelp.ts`; no other file re-lists it.

## Testing

New/updated tests (MSTest is not involved; this is vitest + e2e + the docs build):

1. **Component behaviour (vitest).** Button renders with the section's `title` and its visible
   "How does this work?" text as the accessible name; clicking opens the popover; the popover
   lists the expected titles/blurbs for a sample section; each row is an
   `<a target="_blank" rel="noopener noreferrer">` pointing at `${DOCS_BASE}${slug}/`; Escape
   (restoring focus to the button), an outside click, and choosing a link each close it.
2. **Cap enforcement (vitest).** Every `SECTION_HELP` entry has `1 <= links.length <= 3`
   (fails the suite otherwise), matching the locked "max 3" decision.
3. **Nav coverage (vitest).** Every `leaf` in `AppShell`'s `NAV` array has a `SECTION_HELP`
   entry, so adding a future nav section forces a help mapping (same spirit as
   `McpRestCoverageTest` forcing a decision when a new REST op appears). "Intro" and non-nav
   routes are explicitly exempt.
4. **Slug-existence drift guard (vitest).** Every `slug` referenced in `SECTION_HELP` resolves to
   an actual `docs/src/content/docs/<slug>.(md|mdx)` file (read from the sibling `docs/` tree),
   so a renamed/removed page or a typo cannot ship a dead in-app link. This is the UI-side analog
   of the Starlight link validator.
5. **e2e smoke (`e2e/studio.spec.ts`).** On at least one scoped screen, the `section-help` button
   is present and opening it reveals link(s) to the docs origin.
6. **Docs build.** `npm --prefix docs ci && npm --prefix docs run build` stays green with the new
   `benchmark.mdx` and sidebar entry (link-checked in CI).

## Behavior-preservation contract

- Default behaviour of every existing screen is unchanged except for the addition of the
  top-right help button; no screen's data flow, routing, or layout below the header changes.
- The top-bar `docs` pill and all `fieldHelp` tooltips behave identically to before.
- No new REST route, no OpenAPI snapshot change, no `AppJsonContext` change, no MCP change.
- The full existing vitest suite, the Playwright e2e suite, and the .NET test suite remain green;
  the only additions are the tests listed above.
- The docs site continues to build with zero broken internal links.
