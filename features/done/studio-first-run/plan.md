# Studio first-run show - plan

Frontend-only, on branch `feature/studio-first-run`. No engine/REST changes. New code lives
under `fallen-8-web-ui/src/firstrun/`; the empty-state and replay wiring touch only the
Dashboard screen and the app shell.

## Phase 1 - empty detection + show shell (beat 1 burst)

- `src/api/endpoints.ts`: add `createSampleGraph(i)` = `PUT /unittest` (default namespace
  scope, no body) for the handoff.
- `src/firstrun/mockGraph.ts`: hardcoded ~11-vertex mock (positions, labels, edges,
  precomputed rank, community tint ids, the highlighted path, the "nearest" set). The asset
  seam.
- `src/firstrun/beats.ts`: the four beats (id, caption, endpoint text, sweet-spot tag).
- `src/firstrun/FirstRunShow.tsx`: SVG stage rendering the mock + beat-1 bloom + ripple; the
  caption line; Skip/Replay controls; a placeholder handoff.
- `src/index.css`: `.f8fr*` keyframes/classes (bloom, ripple, pulse, fly), theme-token colours,
  and the `prefers-reduced-motion` block.
- `DashboardScreen`: branch on `useStatus` - pending → loader; error → ErrorBox;
  `vertexCount === 0 && !dismissed` → `<FirstRunShow>`; else the existing Dashboard.
- **Test:** Playwright proves empty → show vs populated → Dashboard.

## Phase 2 - reduced motion + controls + persistent replay

- `src/firstrun/useReducedMotion.ts`: `matchMedia('(prefers-reduced-motion: reduce)')` with a
  jsdom guard.
- `src/firstrun/useBeatTimeline.ts`: advances the active beat over time, pausing on
  `document.hidden`; honours reduced motion (jumps to rested state).
- `src/firstrun/firstRunStore.ts`: zustand + persist (`f8.first-run`); `dismissed` map keyed by
  bound id, `dismiss` / `clearIfPopulated`, transient `replayOpen` / `openReplay` / `closeReplay`.
- `src/firstrun/FirstRunOverlay.tsx`: Radix Dialog wrapping `<FirstRunShow>` for manual replay;
  wires the manual handlers (Explore closes; Load runs the mutation then closes; Import closes +
  navigates).
- `AppShell`: a persistent "Replay intro" rail action (pinned bottom, always enabled) + render
  `<FirstRunOverlay>` once.

## Phase 3 - beats 2 and 3

Path pulse (beat 2) and rank/community tint (beat 3), driven by the beat phase.

## Phase 4 - beat 4 + caption polish

Semantic fly-in + neighbor expand (beat 4); finalize all four captions with sweet-spot lines.

## Phase 5 - handoff

The three handoff actions with the inline confirm on Load; identical on auto and manual paths.

## Phase 6 - tests + docs

- Vitest: show structure, controls, reduced-motion, no-network, timeline hook, store.
- Playwright: `first-run.spec.ts` (auto/manual, no-writes assertion, handoff PUT) + update
  `studio.spec.ts` scenarios 1/8/11 + a capture-only `screenshot-first-run.spec.ts`.
- Docs: `docs/studio.md` "First run" section; screenshot. Move `features/open/studio-first-run/`
  → `features/done/` when it lands.
