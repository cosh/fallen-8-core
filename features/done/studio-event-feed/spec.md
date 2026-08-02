# Studio event feed - spec

> **Status:** Implemented and merged (branch `feature/studio-event-feed`, council-approved
> 2026-08-02; see [plan.md](./plan.md) for the phase record and council outcome, and
> "As-built deltas" below for the recorded deviations).
> **Parent feature:** [change-feed](../../done/change-feed/) (done). This is a pure Studio
> feature: **no engine, REST, OpenAPI, or MCP change of any kind.**

## Problem

The change feed is Fallen-8's liveness story, and in Studio it is invisible. It powers live
mode silently (debounced query invalidations plus a tiny "live" chip), so the user sees
screens stay fresh but never sees *why*. The events themselves - the ordered, filtered,
resync-honest stream that is the feature's whole point - have no surface. A user cannot
answer "what just happened in this graph?", and a demo cannot show the feed off without
dropping to `curl`.

This feature gives the feed a face: a bell in the top bar that signals new events without
being clicked, and an overlay panel that shows the newest events of the **active namespace**
and lets the user configure which events they care about, in exactly the vocabulary the REST
interface uses.

## Decisions

Recorded from the design conversation (2026-08-01); rationale inline in the sections below.

1. **Source: the one shared stream.** The panel taps the existing per-namespace live-mode
   stream and filters client-side; it does not open a second `/changefeed` connection.
2. **Badge counts filtered unread.** The interest filter drives the badge, not just the
   list; display caps at `99+`; a `resync` marks the bell distinctly.
3. **Presentation: right slide-over.** The house Radix Dialog pattern (scrim, focus trap,
   Escape), docked to the right edge instead of centered.
4. **Catch-up via `since`.** Per-namespace session buffers; on resubscribe the stream
   resumes from the last seen event id, replaying missed events from the server ring.

## As-built deltas

Recorded at land time (2026-08-02); each is additive to the contract below.

- **Rename migrates the feed.** §7 specifies clearing on recreate; a namespace RENAME
  additionally moves the buffer and catch-up position to the new name - the graph and its
  feed epoch/sequence are unchanged, so continuity is preserved rather than dropped.
- **Per-reason resync lines.** The §4 gap-marker table shares one line for
  `trim`/`tabulaRasa`/`load`; the panel renders three distinct lines (compacted vs
  replaced vs save game loaded). Same honesty, more precision.
- **Match-nothing is expressible in the UI, not over REST.** Unchecking every kind (or
  both element types) matches nothing; the REST grammar has wildcards but no
  match-nothing, so copy-as-REST disables with an explanation, and the footer states how
  many buffered events the filter hides.
- **Clipboard fallback.** Copy-as-REST fails visibly ("copy failed") when
  `navigator.clipboard` is unavailable (plain-HTTP deployments) instead of throwing.

## Behaviour

### 1. The bell

A feed button joins the top-bar chip cluster in `AppShell` (the `ml-auto` group: docs,
live chip, health chip), rendered whenever an active instance exists. Glyph in the house
style (abstract unicode like the rail's `◉ ▦ ☰`, never an emoji); `aria-label` carries the
unread count.

States, without clicking:

- **Stream up, nothing unread:** quiet (dim foreground), no badge.
- **Unread events matching the interest filter:** an accent badge with the count; the
  display caps at `99+` (the counter itself does not).
- **A `resync` was observed since the panel was last open:** distinct warning treatment
  (danger token) with a tooltip saying continuity was lost and events may be missing -
  a resync is not just another event, it is the stream saying "you missed something".
- **Stream connecting or unavailable (503 / live off):** muted but still clickable; the
  panel then explains the state instead of showing an empty list (see §3).

Opening the panel resets the unread count and the resync flag: visible means read. While
the panel is open no unread accrues - new events land directly in the visible list.

### 2. Source: the shared stream, teed

Studio already holds exactly ONE unfiltered SSE stream per active instance + namespace
(`liveFeed.ts`), because live mode needs every event for its invalidations - and the
change-feed spec §3.7 deliberately chose one unfiltered stream + client-side mapping over
per-consumer server-side filters. The feed panel rides that decision instead of fighting it:

- The live-feed handlers **tee** every element event and every `resync` into a
  per-namespace **event buffer** in the per-namespace instance store: a ring of the newest
  **100 raw (unfiltered) events**, newest first, plus the unread counter and resync flag.
- Buffering raw means a filter change is a pure view re-evaluation: instant, no network,
  and it can *reveal* already-observed events the previous filter hid.
- Session-scoped, never persisted: a reload starts empty (screens refetch their state
  anyway; replaying a stale buffer would imply a history the server does not serve).

Why not a dedicated server-filtered stream: the badge needs an always-on subscription, so a
second stream would be a *permanent* extra subscriber per tab (`MaxSubscribers` defaults to
32 per namespace), and every filter edit would force a reconnect + catch-up dance - for a
view that holds at most 100 events. Server-side filtering remains the story for external
and headless consumers; §5 hands the configured filter over to them instead.

### 3. The panel: right slide-over

The house overlay pattern, right-anchored: a Radix Dialog whose `Overlay` uses
`.modal-overlay` and whose `Content` uses `.panel` plus a new shared `.modal-right`
primitive (fixed to the right edge, full height, `z-50`, width `min(420px, 92vw)`), defined
in `index.css` next to `.modal-center` so the modal stacking order keeps its one home.
Focus trap, Escape-to-close, scrim-click-to-close and focus restore come from Radix exactly
as in `FirstRunOverlay` and `ConfigurationPanel`.

Layout, top to bottom:

1. **Header:** title "Events", the active namespace name, close button.
2. **Stream state line:** one quiet sentence - streaming / connecting / "change feed is
   disabled on this instance (`Fallen8:ChangeFeed:Enabled`); live updates fall back to
   polling" for the 503 case. The honest state lives here, not in a dead-looking list.
3. **Interest filter** (§5), compact.
4. **The event list** (§4), `flex-1`, scrolling.
5. **Footer cap note:** "newest 100 events; older ones fall off" (the list-caps policy
   voice: state the cap, never hide that there is one).

### 4. The event list

Newest on top, at most 100 (the ring's capacity), height capped by the panel with a
scrollbar - consistent with the Studio list-caps policy and far below its 10k ceiling.

Each element-event row shows, from the metadata-only payload:

- a per-kind glyph + the kind name (`vertexCreated`, `propertySet`, ...), colored within
  the existing tokens (created toward accent, removed toward danger, property events
  neutral - no new palette);
- the element id as an **`InspectLink`** (navigation stays one mechanism). A row is
  history, not live state: the link may point at a since-removed element, and the target
  screens already answer "not found" honestly;
- the label as a chip when present;
- property events: the **key** (never a value - the payload is metadata-only by design;
  the link is how you reach current values);
- `edgeCreated`: the edge type (`edgePropertyId`) and `source → target`, each an
  `InspectLink`;
- the `seq` in subtle monospace (`#4712`) - the ordering contract made visible;
- the commit timestamp, relative ("12s ago") with the absolute UTC instant on hover. A
  transaction's events share one `ts`; no extra grouping UI (contiguity is the server's
  guarantee, the shared timestamp is visible as-is).

**`resync` entries are always shown**, regardless of the filter (mirroring "`resync` is
always delivered"), as full-width divider-style rows with the reason and one honest line:

| reason | line |
|---|---|
| `trim`, `tabulaRasa`, `load` | the graph was replaced or compacted; element ids from before this point may be invalid |
| `delegateWrite` | a compiled delegate wrote directly; its changes were not itemized |
| `overflow` | the stream fell behind; events were missed |
| `seekOutOfRange` | the catch-up position was no longer buffered; events in between were not observed |

**Empty state:** "No events yet - committed changes to this namespace appear here live",
plus a nudge that writes from *any* client (another tab, `curl`, an MCP agent) show up,
which is the demo moment this feature exists for.

### 5. The interest filter

The configuration vocabulary is **exactly the REST filter grammar** - same dimensions, same
semantics - so what a user learns in the panel transfers 1:1 to `GET /changefeed` and the
MCP surface:

- **kinds:** checkbox per element-event kind (the six; `resync` is exempt and always
  shown);
- **elements:** `vertex` / `edge` toggles;
- **labels:** chip input, free text, with datalist suggestions from the Graph shape
  snapshot (`shapeSuggestions`: vertex + edge labels; suggestions optional, free form
  always works);
- **keys:** chip input, suggestions from the snapshot's property keys.

Semantics identical to the server, pinned by tests: AND across dimensions, OR within one,
exact case-sensitive match, an unset dimension is a wildcard, an unlabeled element never
matches a `labels` filter, and only property events carry a key - so a `keys` filter hides
creates/removes (the same caveat the REST docs state; surfaced inline via the field-help
mechanism).

The filter drives **both the list and the badge** (decision 2). It applies instantly as a
view over the raw buffer. It persists per instance + namespace (alongside the registry's
other persisted state) so interests survive a reload; the default is everything.

**Copy as REST:** a small affordance renders the current filter as the equivalent
`/ns/{ns}/changefeed?kinds=...&labels=...` query (via the existing `buildChangeFeedQuery`),
for handing to `curl` or a service. It never includes credentials - keys stay out of URLs,
consistent with the feed's auth posture.

### 6. Catch-up on resubscribe (`since`)

The last seen SSE event id (`epoch:seq`) is kept per namespace for the session. When the
stream for a namespace (re)starts and a stored id exists, it is passed as `since`: the
server ring (default 8192 events) replays what was missed, in order, into the buffer - so
switching namespaces and back shows what happened while you were away, which is the
catch-up contract demonstrated in the UI. If the position is gone, the leading
`resync(seekOutOfRange)` arrives in-band and renders as the honest gap marker; a server
restart's epoch mismatch resyncs the same way.

Replayed events flow through the existing invalidation handlers too - harmless by
construction (they collapse into the same debounced refetches).

Plumbing note (spec-level): `streamChanges` already tracks the last id internally for its
own reconnects; it additionally needs to expose it (e.g. an `onFrameId` callback or a
result on close) so the shell can stash it per namespace. `since` is **not** persisted
across page reloads: a fresh page refetches everything anyway, and a stale position would
mostly produce `seekOutOfRange` noise.

### 7. Namespace scoping and lifecycle

- The stream is already `/ns/{ns}/changefeed` for the active namespace only, so the feed
  can never show foreign events; buffers, unread counters and stored ids are keyed by
  instance + namespace, and switching either swaps the bell and list to that scope's state.
- **Namespace recreated in place** (the existing `bumpFeedGeneration` path): that
  namespace's buffer, unread state and stored id are **cleared** - the old events describe
  a dead graph, and a stale `since` against a new feed would be meaningless.
- Multiple tabs each hold their own stream and buffer (already true for live mode); there
  is no cross-tab sync.

## Non-goals

- **No history read, no paging.** The list is the newest 100 *observed* events. The server
  has no history endpoint (its ring is reachable only via `since` on the stream), so there
  is no infinite scroll into the past - "endless list" means the list scrolls, not that
  history is unbounded.
- **No property values in rows.** The metadata-only payload is the feed's security
  posture; rows link to the element instead.
- **No server-side filtering for Studio's stream** (decision 1). External consumers keep
  that story; the copy-as-REST affordance bridges to it.
- **No toasts, OS notifications, or sounds.**
- **No cross-namespace aggregate feed** - the contract is the selected namespace only.
- **No buffer persistence** across reloads, no cross-tab shared buffer.
- **No transaction-grouping UI** beyond the visible shared timestamp.
- **No engine/REST/OpenAPI/MCP change.**

## Impact on existing features

- **Engine / REST contract / OpenAPI snapshot / MCP:** none. No endpoint changes, no
  snapshot regeneration, `McpRestCoverageTest` untouched.
- **change-feed** (`features/done/change-feed/README.md`, docs `change-feed.mdx`): each
  gains a one-line pointer to the Studio Events panel (one home per explanation; the
  panel's story lives in this spec and the Studio docs page).
- **web-ui / AppShell:** the chip cluster gains the bell. The `LiveChip` **stays**: it
  reports stream state for the screens, the bell reports events; the bell's muted state
  mirrors "unavailable" rather than replacing the chip.
- **liveFeed.ts:** gains the tee into the buffer and the `since` handoff; invalidation
  semantics unchanged (catch-up replay only adds debounced refetches).
- **studio-list-caps-policy:** consistent (100-row cap, capped height, cap stated).
- **Docs site + screenshots:** `docs/src/content/docs/studio.md` gains an Events panel
  section; `change-feed.mdx` a short "in Studio" pointer. Any existing screenshot showing
  the top-bar chip cluster is recreated, plus one new panel screenshot (the established
  capture procedure applies). Docs build must stay link-clean.
- **README:** no new entry - Studio and the change feed are both already listed key
  features; this is a surface of both, linked from their pages.
- **Architecture diagrams:** unchanged (no new channel or deployable).
- **NL-assist dataset/eval:** untouched (no delegate grammar involvement).

## Acceptance

Vitest in `fallen-8-web-ui/tests`, extending `live-feed.test.ts` / `app-shell.test.tsx`
plus new suites; behaviour-pinning, edge cases included:

- **Buffer:** ring of 100, newest first, stores raw events, keyed per instance +
  namespace; cleared on the namespace-recreate generation bump; empty after a simulated
  reload (no persistence).
- **Badge:** unread accrues only while the panel is closed and only for filter-matching
  events; display caps at `99+`; a `resync` sets the distinct flag; opening resets count
  and flag; no accrual while open.
- **Filter parity:** table-driven tests mirroring the server's semantics - AND across
  dimensions, OR within, case-sensitive, unset = wildcard, unlabeled never matches
  `labels`, `keys` excludes non-property events.
- **Resync rendering:** always listed regardless of filter, correct per-reason line for
  all six reasons.
- **Catch-up:** resubscribe carries the stored id as `since`; replayed events land in
  order; `seekOutOfRange` renders as the gap marker; the stored id is dropped on
  namespace recreate.
- **Copy as REST:** output round-trips through `buildChangeFeedQuery` (same grammar) and
  never contains credentials.
- **Shell states:** bell absent without an active instance; muted with the explanatory
  panel state when the feed is unavailable; Radix a11y (focus trap, Escape, labelled
  unread count).
- **Suite green** (`npm test` in `fallen-8-web-ui`), lint/build clean; docs-site build
  stays green when the docs land (implementation phase).
