# Studio event feed - plan

Phases; each leaves the build green (`npm run build`, `npm test` in `fallen-8-web-ui`).
Contract in [spec.md](./spec.md); no engine/REST/OpenAPI/MCP work anywhere in this plan.

## Phase 1 - buffer state + stream tee

- `src/state/eventFeed.ts`: the per-namespace feed state in the per-namespace instance
  store - ring buffer (capacity 100, raw events, newest first), unread counter, resync
  flag, stored last event id, panel-open flag. Actions: `record(event)`,
  `recordResync(event)`, `markOpened()`/`markClosed()` (open resets unread + resync flag,
  no accrual while open), `clear()`.
- `src/api/changefeed.ts`: expose the last seen SSE frame id to the caller (an
  `onFrameId` callback next to `onEvent`) - no other behavior change; `streamChanges`
  keeps handling its own reconnect `since` internally as today.
- `src/state/liveFeed.ts`: tee `onEvent`/`onResync` into the feed state; stash the frame
  id per namespace; pass the stored id as `since` when the effect (re)subscribes; clear
  buffer + unread + stored id on `bumpFeedGeneration` (namespace recreated in place).
- Tests (`tests/event-feed.test.ts`, extend `tests/live-feed.test.ts`): ring semantics
  (cap 100, newest first, raw storage), per instance+namespace keying, unread accrual
  rules (closed-only, resync flag distinct), `since` handoff on resubscribe, replayed
  events land in order, `seekOutOfRange` recorded as a resync entry, clear-on-bump,
  nothing persisted across a simulated reload.

## Phase 2 - interest filter model

- `src/state/feedFilter.ts`: the filter model (kinds / elements / labels / keys, mirroring
  `ChangeFeedFilter`), `matchesFilter(event, filter)` with exact server-parity semantics,
  and persistence per instance + namespace alongside the registry's persisted state
  (default: everything). Filtered views over the raw buffer; unread accrual consults the
  filter.
- Copy-as-REST: render the active filter through the existing `buildChangeFeedQuery` into
  a `/ns/{ns}/changefeed?...` string; never any credential material.
- Tests (`tests/feed-filter.test.ts`): table-driven parity suite - AND across dimensions,
  OR within one, case-sensitive exact match, unset dimension = wildcard, unlabeled element
  never matches `labels`, `keys` excludes non-property events; persistence round-trip;
  copy-as-REST round-trips through the query builder and contains no key.

## Phase 3 - the bell + the panel

- `src/index.css`: add the `.modal-right` primitive next to `.modal-center` (the modal
  stacking-order comment block stays the one home; scrim `z-40`, panel `z-50`, right
  edge, full height, width `min(420px, 92vw)`).
- `src/components/EventFeedBell.tsx`: chip-cluster button in `AppShell` (between live and
  health chips) - house-style glyph, accent count badge capped at `99+`, danger treatment
  + tooltip when a resync was observed, muted-but-clickable when connecting/unavailable,
  absent without an active instance, `aria-label` with the unread count.
- `src/components/EventFeedPanel.tsx`: Radix Dialog slide-over per spec §3 - header
  (title, namespace, close), stream-state line (incl. the honest 503 explanation), filter
  block (kind checkboxes, element toggles, label/key chip inputs with Graph-shape datalist
  suggestions, field-help for the `keys`-excludes-creates caveat), event list (per-kind
  glyph + tokens-only coloring, `InspectLink` ids incl. `source → target`, label chip,
  property key, monospace `#seq`, relative timestamp with absolute UTC on hover), resync
  divider rows with the per-reason lines, empty state, footer cap note, copy-as-REST
  affordance.
- Tests (`tests/event-feed-panel.test.tsx`, extend `tests/app-shell.test.tsx`): bell
  states incl. badge cap and reset-on-open, panel a11y (focus trap, Escape, labelled
  count), one row assertion per event kind, resync rows always visible regardless of
  filter, filter edits reshaping the list instantly.

## Phase 4 - docs + screenshots

- `docs/src/content/docs/studio.md`: an Events panel section (what the bell shows, the
  filter vocabulary, the catch-up behavior, the 100-event cap).
- `docs/src/content/docs/change-feed.mdx` and `features/done/change-feed/README.md`: one
  pointer line each to the Studio Events panel (one home per explanation).
- Recreate every screenshot showing the top-bar chip cluster, add one panel screenshot
  (established capture procedure); `npm --prefix docs ci && npm --prefix docs run build`
  stays link-clean.

## Phase 5 - verify + council

- `npm run build`, `npm test`; run the apiApp + Studio and exercise end-to-end: mutate via
  REST/a second tab, watch the badge and list, switch namespaces and back (catch-up),
  recreate a namespace (clear), disable the feed (muted bell + explanation).
- Council review (correctness/contract-parity, UX/regression, scope), fix findings on the
  branch, then `git merge --no-ff` to `main` and move
  `features/open/studio-event-feed/` to `features/done/`.

## Status

- [x] Phase 1 - buffer state + stream tee
- [x] Phase 2 - interest filter model
- [x] Phase 3 - the bell + the panel
- [x] Phase 4 - docs + screenshots (14 capture specs against an isolated durable apiApp;
  all top-bar images refreshed, new `screen-events.png` shot over a live feed)
- [x] Phase 5 - verify + council (3 parallel reviewers: no blockers; every should-fix
  applied on the branch, most notably the onFrameId detached-store fix so a purge
  mid-stream cannot silently lose the catch-up position, plus a clipboard fallback and
  the recorded as-built deltas in spec.md; 560 web-ui tests, 1061 dotnet tests, docs
  build link-clean)
