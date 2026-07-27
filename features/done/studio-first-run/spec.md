# Studio first-run show - spec

## Problem

On a fresh deployment a Fallen-8 namespace is empty (registry-driven startup, no save
games). In F8 Studio the Dashboard for an empty namespace is three zeroed stat tiles: a
blank canvas that teaches a newcomer nothing about what Fallen-8 can do. There is also no
way to see that intro again once data exists.

## Behaviour

A short, mostly-passive, code-driven animated **show** that plays once on an empty graph,
teaches the four core capabilities on a canned mock graph, ends on an opt-in handoff, and
can be replayed at any time from a persistent control.

### 1. The show creates nothing

The show is 100% client-side and read-only. It renders a hardcoded mock graph
(`src/firstrun/mockGraph.ts`) and animates it as SVG. It performs **no** writes: no
`PUT /unittest`, no mutations, no persisted server state while it plays. The only backend
read it depends on is the Dashboard's existing `GET /status` poll (which already runs), used
to decide whether to auto-show. Only the explicit end-of-show handoff buttons perform a real
action, and only on click.

> Deviation from the task brief, recorded under its "map to real code" clause: the brief
> names `GET /statistics` for empty detection. In this codebase `/statistics` is the
> **expensive** O(V+E), rate-limited, on-demand graph-shape snapshot (`getStatistics` /
> `graphShape.ts`), whereas `GET /status` (`useStatus`) already carries `vertexCount` /
> `edgeCount` cheaply and is already polled and cached by the shell and Dashboard. The show
> uses `useStatus`. Both are read-only GETs, so the "creates nothing" contract is unchanged.

### 2. The show itself

A mock graph of ~11 labelled vertices with a few edges, drawn as crisp SVG (vector, never a
video, never routed through the real Sigma/graphology canvas). It autoplays once through four
beats, each with one line of real-text caption, then rests on the handoff. Total under ~12s.

1. **Bloom.** Vertices and edges bloom in with spring easing, one subtle radial ripple, then
   settle. Caption: vertices, edges, and typed properties (Enterprise Search).
2. **Path.** A highlighted path pulses between two vertices. Caption names path finding and
   shows the endpoint as text only: `POST /path/{from}/to/{to}` (Lawful Interception).
3. **Rank.** Vertices scale by a precomputed mock rank; two communities tint. Caption names
   graph analytics: `POST /analytics/PAGERANK` (E-Commerce).
4. **Semantic.** Two or three mock "nearest" vertices fly forward and a neighbor expands.
   Caption names vector kNN + GraphRAG: `POST /scan/index/vector`.

Controls **Skip** (jump to the handoff) and **Replay** (restart at beat 1) live inside the
show, are real focusable buttons, and are keyboard reachable. All copy is concise and uses
no em dashes.

### 3. When it auto-shows

The Dashboard is the namespace landing screen. When the active namespace's
`status.vertexCount === 0` **and** the show has not been dismissed for that namespace, the
Dashboard renders `<FirstRunShow>` in place of its stat tiles. Otherwise it renders the
existing Dashboard, unchanged.

Dismissal is persisted per bound namespace key (`<instanceId>/<ns>`) in a small
localStorage-backed store (`firstRunStore`). It is **cleared** whenever the namespace is
observed non-empty, so the show auto-shows again if the graph genuinely empties later, but a
returning user is never nagged on a graph that has simply stayed empty.

### 4. Persistent manual replay

A low-key, always-visible **Replay intro** action in the left rail (pinned below the nav).
It is enabled regardless of connection state, the dismissed flag, and whether the graph is
empty or populated. Clicking it opens the **same** `<FirstRunShow>` inside the existing Radix
Dialog overlay (`@radix-ui/react-dialog` - focus trap, Escape, focus restore for free) on top
of the current screen, plays from beat 1 on the internal mock graph (never the user's real
data), and on close returns the user exactly where they were. It never mutates data and never
touches the dismissed flag.

The auto-show and the manual replay render one component. The only differences: the entry
point, that the manual path ignores the dismissed flag, and that the manual path lives in an
overlay whose close is a no-op on persistence.

### 5. Reveal quality and reduced motion

Bloom reads as a small burst, not a fade: vertices bloom outward, edges cascade a beat behind,
one radial ripple, then settle. Everything is vector and stays razor-sharp at any size; motion
is CSS-transform/opacity (GPU-composited, ~60fps). The beat timeline **pauses when the tab is
hidden** (`document.visibilitychange`). With `prefers-reduced-motion: reduce` the show does not
autoplay: it renders the final rested composition (ranked sizes, tinted communities,
highlighted path) and the handoff immediately, with no large motion and no flashing. Colours
come from the existing theme tokens (`--color-*`), so it is native to the app chrome.

### 6. The handoff (navigation only, on click)

A calm panel, visually separate from the show, offering at most three actions, identical on
both the auto and manual paths. None of them writes; they navigate or dismiss. In particular the
unit-test graph endpoint (`PUT /unittest`) is deliberately NEVER wired into the UI (see CLAUDE.md);
the newcomer's path from empty to a populated graph is the curated Sample gallery.

- **Browse sample graphs** - jumps to the Sample gallery (the Samples screen), where curated,
  styled datasets load in one click.
- **Import your own data** - points at `POST /bulk/import`, with a one-line JSONL schema hint;
  navigates to the Save games screen's interchange section.
- **Explore on my own** - dismiss into the app (auto path: sets the dismissed flag; manual
  path: closes the overlay).

## Non-goals

- No new runtime dependency (no framer-motion/lottie/rive; SVG + CSS/WAAPI only).
- No change to the populated-state UI beyond the added persistent replay control.
- The show is not a real graph render and never reads or writes the user's data.

## Medium seam

`<FirstRunShow>` takes its picture from `mockGraph.ts` + `beats.ts` behind one internal
boundary, so a designer-made Lottie/`.riv` could later replace the code animation without
touching the empty-state detection or the replay wiring.

## Impact on existing features

Swept engine ↔ REST ↔ OpenAPI ↔ Studio ↔ NL-assist ↔ feature READMEs ↔ architecture diagrams
↔ recipes/stored queries:

- **Engine / REST / OpenAPI snapshot / MCP coverage:** no change. This is a frontend-only
  feature; it adds no route and the handoff only navigates (to the Sample gallery / import
  screen), so the OpenAPI snapshot and `McpRestCoverageTest` are untouched. The unit-test graph
  endpoint (`PUT /unittest`) is deliberately never called from the UI (CLAUDE.md).
- **Studio e2e (`fallen-8-web-ui/e2e/studio.spec.ts`):** the empty-graph auto-show on the
  Dashboard intercepts three scenarios that land on an empty Dashboard (1 connect/health, 8
  post-erase, 11 nav-gate). Each is updated to dismiss the show (Explore on my own) before
  asserting the stat tiles. This is expected, surfaced here, and adapted rather than silently
  worked around.
- **NL-assist dataset / eval:** no impact (no delegate/plugin surface change). No
  `RETRAIN-LOG.md` entry needed.
- **Architecture diagrams (root README + docs/architecture.md):** no impact - no new channel,
  deployable, or layer boundary; the show is a screen behaviour inside the already-drawn
  Studio SPA.
- **Docs:** a new "First run" section in `docs/studio.md` (the Studio doc is the single home
  for Studio screen behaviour; sibling studio-* features fold in the same way, so no new
  top-level README key-feature bullet and no new docs index row). Screenshot
  `docs/images/screen-first-run.png` via a capture-only Playwright spec.
- **Sample-graphs delivery (cross-feature change):** the gallery previously fetched its manifest
  and datasets from GitHub raw `main`, so a newly added sample was invisible until merged. To make
  the new **Asymmetric Cyber Warfare** sample (and any future one) show up on rebuild, the default
  `samplesBaseUrl()` now points at a SAME-ORIGIN `/samples`: Vite serves the repo `samples/` in dev
  and copies it into `wwwroot` at build (a small `vite.config.ts` plugin), the apiApp serves it
  (adds a `.jsonl` content type in `Program.cs`), and the Dockerfile copies `samples/` into the UI
  build stage. Cost: the datasets (~5 MB) now ship in the image; benefit: offline, no GitHub
  round-trip, the app shows the samples it was built with. `VITE_F8_SAMPLES_BASE` still overrides
  the base to a remote mirror or fork.
- **Recipes / stored queries:** none.

## Testing

- **Vitest (runnable without a backend):** `<FirstRunShow>` renders captions/controls; Skip
  jumps to the handoff and Replay restarts; reduced-motion renders the rested state with no
  autoplay; the handoff buttons call their injected handlers and the show issues no `fetch`;
  the beat-timeline hook advances and pauses on `visibilitychange`; the dismissed store
  dismiss/clear-on-populate logic.
- **Playwright (`e2e/first-run.spec.ts`, against a live apiApp):** fresh engine auto-shows on
  the Dashboard; **zero non-GET requests** fire while it plays (auto and manual); the persistent
  control replays on both empty and populated graphs as an overlay over the current screen
  using the mock, without changing the dismissed flag; a populated graph shows the Dashboard,
  not the show; the handoff **Browse sample graphs** navigates to the Sample gallery and the UI
  never calls `PUT /unittest`. A capture-only spec (`e2e/screenshot-cyber-sample.spec.ts`)
  renders the cyber-warfare sample in the gallery and on the 3D canvas for the docs.
