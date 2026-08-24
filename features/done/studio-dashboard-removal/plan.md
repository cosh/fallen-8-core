# Plan: remove the Dashboard, keep its two signals

Phases are ordered so the suite is meaningful at each boundary.

## P1 - Rail bug (independent of the removal)

- `AppShell`: split the rail into a pinned logo and an `overflow-y-auto` item list
  (`flex-1 min-h-0`), so `<nav>` is never overflowed and its `bg-panel`/`border-r` reach the bottom.
- `index.css`: `.rail-scroll`, a thin themed scrollbar next to the existing `.scroll-list` policy.
- Test: pin the structure (`nav-rail-items` exists, is the scroll box, and the nav is not the
  overflow container).

## P2 - Shell-level signals (added BEFORE the Dashboard is deleted, so nothing is briefly lost)

- `registry.ts`: extract the namespace-binding rule out of `useInstanceStore` into a pure
  `boundInstance(instance, namespace, namespaceSupported)` and add a nullable
  `useBoundInstance()` for shell-level consumers. One home for the rule.
- `state/status.ts`: widen `useStatus` to accept `InstanceConfig | null` (disabled while null).
- `AppShell`: render `<DurabilityNotice>` between the header and `<main>`.
- `FirstRunOverlay`: auto-open on an empty, non-dismissed namespace with healthy durability;
  close-means-dismiss on the auto path only.
- Tests: rewrite `dashboard-durability.test.tsx` as `shell-durability.test.tsx`; add
  `first-run-autoshow.test.tsx` covering opens / stays shut when populated / stays shut when
  dismissed / suppressed while durability is unhealthy / re-arms on populate / replay ignores
  the flag.

## P3 - Delete the Dashboard

- `nav.ts`, `routes.tsx` (route out, two redirects in), `screens/DashboardScreen.tsx` deleted,
  `lib/sectionHelp.ts` `dashboard` entry out, `lib/fieldHelp.ts` comment header corrected
  (its keys are Save-games fields, not Dashboard ones).
- Comment sweep: `Stat.tsx`, `lib/samples.ts`, `state/liveFeed.ts`, `api/types.ts`,
  `components/DurabilityNotice.tsx`, `firstrun/*`, `screens/{SaveGames,Samples,Benchmark}Screen.tsx`.
- Tests/e2e: drop `nav-dashboard` from the gated list, repoint `/dashboard` deep links,
  delete `e2e/screenshot-dashboard.spec.ts`.

## P4 - Stay-where-you-are navigation

- `AppShell.switchNamespace` / `switchInstance`, `NamespacesPanel.switchTo`,
  `NamespaceScope`'s two "Switch to default" buttons.
- Tests: `namespaces.test.tsx` (switch from Connect does not navigate; switch from a scoped screen
  keeps the leaf), `app-shell.test.tsx`, `router-basepath.test.ts`.

## P5 - Gates

`npx tsc -b`, `npx vitest run`, `npm run lint` (if present), `npm run build:apiapp`,
`npm --prefix docs run build`. The .NET suite is untouched by a Studio-only change but runs anyway
because `fallen-8-unittest` covers the whole solution.

## P6 - Docs + screenshots

- `docs/src/content/docs/studio.md`: Dashboard row and section out, durability paragraph moved into
  Layout, first-run paragraph reworded, `screen-dashboard.png` reference gone.
- `docs/src/content/docs/debugging.md`: drop the deleted capture spec.
- Delete `docs/src/assets/images/screen-dashboard.png`.
- Recapture every image showing the rail. Two batches per the known recipe: the main keyed app, plus
  a second unkeyed app for `screenshot-integrations` and an ingestion-enabled one for
  `screenshot-knowledge`. `canvas.screenshot` images carry no rail - restore any force-layout churn
  with `git checkout --`.

## P7 - Council + merge

Review agents over the diff, fix findings on the branch, then `git merge --no-ff` to `main`.
Move `features/open/studio-dashboard-removal/` to `features/done/`.
