# Studio: remove the Dashboard, keep its two signals

Status: implemented on `feature/studio-dashboard-removal`; council-reviewed and merged to `main`.

## Why

The Dashboard was a screen whose whole payload was three tiles: vertex count, edge count, used
memory. Two of those three are already in the top bar on every screen (the namespace switcher
renders `<ns> N v · M e` for the active namespace, and the Connect screen's Instances table sums
them per instance), so the screen cost a rail slot and a click to tell an operator something they
were already looking at. It is removed.

Two things that happened to live there are NOT redundant and must survive the removal:

1. **The durability signal.** `GET /status` reports a degraded write-ahead log, a truncated last
   recovery, and indices dropped by the last checkpoint. `DurabilityNotice` renders those three and
   nothing else. On the Dashboard it was only ever seen by someone who chose to open the Dashboard,
   which is the same "nobody watching finds out" failure the signal exists to prevent.
2. **The first-run walkthrough auto-show.** On an empty, not-yet-dismissed namespace the Dashboard
   rendered `<FirstRunShow variant="auto">` in place of the zeroed tiles.

## Behaviour

### The Dashboard is gone

- No `Dashboard` entry in the rail (`app/nav.ts`), no `DashboardScreen`, no
  `/q/{ns}/dashboard` route, no `dashboard` section-help entry.
- `/q/{ns}/dashboard` and the pre-namespace `/dashboard` **redirect to `/q/{ns}/browser`** rather
  than resolving to an empty `<Outlet/>`: both URLs are three years of bookmarks and the router has
  no not-found component, so dropping the routes would render a blank main area.

### Navigation: you stay where you are

Switching namespace or instance used to navigate to the Dashboard whenever the current route was
not a namespace-scoped screen, and the Connect screen's Namespaces panel navigated there
unconditionally. With no Dashboard there is no "overview" to fall back to, and the honest rule is
the one the user stated: **a context switch never moves you off the screen you are on.**

- **Top-bar namespace switcher** - on a scoped route (`/q/{ns}/<leaf>`), keep the leaf and swap the
  namespace, as today. On a flat route (`/`, `/save-games`, `/integrations`) it now navigates
  **nowhere**: those screens read the active namespace from the registry and update in place.
- **Top-bar instance switcher** - same rule; under `/q/…` it restores the new instance's remembered
  namespace on the CURRENT leaf, and `/q/{ns}` with no leaf targets the bare namespace route
  instead of synthesising a leaf.
- **Connect ▸ Namespaces ▸ "switch to"** - sets the active namespace and stays on Connect. The top
  bar is where the switch shows up, and it is on screen.
- **`NamespaceScope`'s two recover states** ("Switch to default" on a namespace that is missing, and
  on one the server did not load) - keep the current leaf and swap the namespace, so the operator
  lands on the same screen in a namespace that works.

### The durability signal becomes shell-level

`DurabilityNotice` renders in `AppShell`, between the top bar and `<main>`, for the ACTIVE
namespace. It is unchanged as a component (still silent unless one of the three states is true,
still says nothing when the server does not report the block). This is strictly more coverage than
the Dashboard gave it: the warning is now on every screen instead of on the one screen an operator
had no reason to open.

### The first-run auto-show becomes a shell-level overlay

`FirstRunOverlay` already renders `<FirstRunShow>` as a Radix dialog for the rail's **Intro**
button. It now also opens itself, once, when the active namespace is empty and its dismissal flag
is unset - the same condition and the same per-namespace `dismissed` memory the Dashboard used,
including the re-arm when the namespace is next seen non-empty. Closing an AUTO-opened overlay by
any route (Close, Escape, the scrim, "Explore on my own") sets `dismissed`: an overlay that
reappeared on every navigation would be unusable. A replay-opened overlay still never touches the
flag.

**It is silent on the Fallen-8-level screens** (Connect, Save games, Integrations), i.e. the auto
path requires a `/q/{ns}/...` route. This was not planned; it was measured. A modal over Connect
lands on top of a half-finished instance registration and blocks the radio that activates it: the
Connect capture spec failed on exactly that, and so did most of the functional e2e suite (the shared
`registerSecuredInstance` helper is where that radio click lives, and all but one of `studio.spec`'s
scenarios call it). The exact count is not recorded and should be read as "most of them" rather than
a number.

The rule that came out of it stands on its own: the walkthrough is about a graph, and those three
screens are where you wire one up. The one part of that argument that is NOT measured is the
assumption that a newcomer's first act after connecting is to click a rail entry - which is what
carries them onto a scoped screen where the show does fire. Compared with `main`, where the show sat
on the Dashboard and every context switch landed there, this narrows the onboarding reach by one
click; it is a deliberate trade against ambushing an operator mid-registration.

**The durability warning wins over the welcome.** The auto-show is suppressed while the namespace
has a durability problem. On the Dashboard this was solved by ordering (the notice rendered ABOVE
the show), which a modal cannot do - its scrim would put the one signal the operator needs behind
it. A truncated recovery is a leading reason a namespace you expected to hold data is empty, so an
empty namespace with a durability problem gets the warning and no tour. The rail's Intro button
still plays it on demand.

### Rail bug: the panel background stops short of the bottom

Separate, pre-existing bug reported with the removal. The rail is `<nav class="flex h-full …">`'s
first flex item, so `align-items: stretch` sizes it to the container height; its children overflow
visibly past that box, and `bg-panel` + `border-r` end where the box ends. On a ~700px-tall viewport
the last two entries drew on the page background with no border beside them.

The rail keeps the logo pinned and moves the item list into an `overflow-y-auto` child that takes
the remaining height (`flex-1 min-h-0`). The `<nav>` itself is then never overflowed, so its
background and right border always reach the bottom, and every entry stays reachable at any
viewport height. A thin themed scrollbar (`.rail-scroll`) appears only when the items do not fit.
Removing the Dashboard reclaims one slot but does NOT fix this on its own (~14 remaining entries
still exceed a 700px viewport), which is why the overflow fix is here rather than being assumed.

**What was verified, and how far it is reproducible.** The fix was measured once by hand, with a
throwaway Playwright probe at 1000 / 800 / 700 / 600 / 520px: the `<nav>` never overflowed, its box
reached the viewport bottom at every height, the item list scrolled from 800px down, and the last
entry was reachable at every height. That probe was deleted and nothing automated replaced it -
jsdom computes no layout, so the suite pins the STRUCTURE (the markup, plus the `.rail-scroll`
`overflow-y` rule read out of `src/index.css`, which is otherwise unreachable because Vitest does
not apply CSS) and not the geometry. The surviving artifact in-repo is
`screen-delegate-editor.png`, captured at 1440x700 by `screenshot-delegate-editor.spec.ts`, which
shows the fold mid-rail with the background and border running to the bottom edge.

## Impact on existing features

- **studio-first-run** - the auto-show moves from an inline empty state to the shell overlay. Same
  component, same store, same dismissal semantics; new: suppression while durability is unhealthy,
  and close-means-dismiss for the auto path. `docs/src/content/docs/studio.md` "First run" reworded.
- **platform-integrity-audit W5** (the durability signal) - moves from the Dashboard to the shell.
  `tests/dashboard-durability.test.tsx` is rewritten as a shell test; `durability-notice.test.tsx`
  (component-level) is untouched.
- **graph-namespaces** - the namespace switcher's landing rule changes (stay put). Pinned by tests
  in `namespaces.test.tsx`.
- **studio-section-help** - the `dashboard` entry leaves `SECTION_HELP`. The registry test forces a
  mapping for every nav leaf, so the removal is consistent by construction.
- **sample-graphs / save-games / benchmark / studio-coverage** - prose only: several comments and
  doc lines describe their contents as "moved out of the Dashboard". Those sentences are now
  archaeology about a screen that no longer exists and are dropped, not reworded.
- **Docs site** - `studio.md` loses the Dashboard row and section; the durability paragraph moves to
  Layout (it is shell chrome now) and the first-run paragraph stops naming the Dashboard.
  `debugging.md` loses the `screenshot-dashboard.spec.ts` line.
- **Screenshots** - a rail change touches every image that shows the rail. **30** rail-bearing
  images were recaptured and `screen-dashboard.png` deleted along with its capture spec, i.e. 31
  tracked image changes over 34 images; the four element-only shots
  (`sample-cyber-warfare*.png`, `sample-wind-farm*.png`) carry no rail and are deliberately left
  alone. **Ten** capture specs and one functional spec needed `closeIntroIfOpen` because the
  auto-show now opens over them - and one capture had already photographed its scrim while still
  reporting a pass, which is why every recaptured frame was inspected rather than trusted.
- **change-feed** (found while verifying a recaptured frame, fixed here) - the top bar's counts come
  from the namespace inventory, which the feed never invalidated. With no status screen left, that
  made the ONE place a human reads counts a 15s poll. It now follows the feed, including on resync,
  where `onResync` cleared the pending invalidation and its own bound-id prefix could not reach the
  raw-id-keyed inventory. That combination is what published "0 v / 0 e" in `screen-events.png`
  beside a list of freshly created edges.
- **`StatusREST.usedMemory` now has no reader.** The Dashboard's third tile was its only consumer,
  and unlike the vertex and edge counts it is NOT in the top bar, so the number left the UI with the
  screen. That follows from "the Dashboard does not provide any value", but it is a capability loss
  rather than a relocation and is recorded as one. The field stays on the REST contract, where
  `/status` and `/statistics` still serve it.
- **A new 15s request.** The shell now polls `/ns/{ns}/status` (`useStatus`, `poll: true`) beside the
  existing instance health probe, so an idle session makes two `/status`-family requests per interval
  where it made one. That is the price of a warning nobody navigates toward; it is stated here rather
  than left for someone to discover. Polling implies `retry: 0` to match the probe - on a
  pre-namespace server the two rows collapse onto one query key, and without it the probe's
  deliberate fail-fast became a coin flip against a default of three retries.
- **The element-table properties preview now orders user properties before engine embedding
  markers** (`lib/embeddingProperties.ts`, `userPropertiesFirst`). Found by the council in a
  recaptured frame, not by a test: the cell truncates at 80 characters, the REST egress emits
  properties in no guaranteed order, and `$embeddingModel:default=bge-m3#1024#Cosine` is ~44 of
  those characters - so on a graph with embeddings the operator's own keys were being cut in favour
  of bookkeeping they cannot edit. `query-semantic-search.png` was recaptured, and its caption in
  `samples.md` now describes what the frame shows rather than a `title=` a truncated preview can
  never guarantee.
- **Architecture diagrams** - unaffected: no channel, deployable or layer changes.
- **OpenAPI / MCP / engine** - unaffected: no REST surface change, Studio-only. The browser-host
  probe is likewise not implicated: nothing under `fallen-8-core/` changed.
- **NL-assist dataset/eval** - unaffected: the datasets are about delegate fragments, not screens.

## Council review

Three parallel reviewers (correctness; regressions and invariants; scope and measurement honesty)
went over the branch before merge. Everything below was found by review or by inspecting a
recaptured frame - the suite was green through all of it - and each fix is mutation-checked (the
fix reverted, the new test observed to fail, the fix restored).

1. **The auto-show was a live derivation, so it ambushed work that passes through empty.** `open`
   was recomputed from the current vertex count, which the change feed now moves within ~300ms.
   Loading a sample is a tabula rasa followed by an import, so an operator who clicked Load on a
   populated graph got the walkthrough thrown over the Samples screen for the whole duration of the
   import, with the loader's own progress behind its scrim. Deleting the last element in the
   Browser, or another client wiping the graph, did the same. Fixed with an `arrival` latch:
   emptiness is decided once per namespace, on the first `/status` answer for it.
2. **Closing the replay overlay could immediately re-open it as the auto overlay.** `open` had two
   independent reasons and `close()` cleared only the one that fired. Reachable in two clicks on a
   fresh install: Intro on the Connect screen, then "Browse sample graphs" - which closed the replay
   and navigated onto a scoped screen where the auto path re-opened the same dialog. Fixed by
   recording the dismissal whenever the auto path is ARMED, not only when it is what opened it; a
   replay on a populated graph still records nothing.
3. **`sameScopedScreen` could navigate to a route with no screen.** The no-leaf fallback was
   `/q/$ns`, which is a real route with no index child, so it renders the shell around an empty
   `<Outlet/>` - the exact blank screen the `/dashboard` redirect exists to prevent, reachable from
   the button whose whole job is getting an operator OUT of an unreadable namespace. Now falls back
   to the Browser.
4. **The durability banner was not gated on the connection.** `/status` reports durability to an
   unauthorized caller, so an instance that rejected the credential raised a red warning above the
   "rejected the credential" guard. Gated like the overlay is.
5. **Coverage that the retargeting had quietly dropped.** Scenario 9 ("shows the disconnected state,
   not a blank screen") lost its only screen-area assertion when it stopped landing on the
   Dashboard, whose `ErrorBox` was the `role=alert` + Retry it checked; it now asserts what the
   shell actually guarantees there. The rail test asserted class names only, so deleting the
   `.rail-scroll` rule would have kept the suite green. A durability regex had been loosened for no
   reason. `screenshot-wind-farm.spec.ts` was missed by the intro-guard sweep and would have hung.
6. **Stale references the cross-feature sweep missed.** `NamespaceREST.cs`'s `VertexCount` remark
   named the Studio dashboard and ships in the OpenAPI snapshot (fixed, snapshot regenerated - one
   line); `observability.mdx` called Studio "the dashboard"; a capture spec's rationale still said
   the rail has 15 entries.
