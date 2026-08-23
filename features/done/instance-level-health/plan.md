# Instance-level health - Implementation plan

Branch: `feature/instance-level-health`. Spec: [spec.md](spec.md). Studio-only; nothing under
`fallen-8-core*/` is touched, so the C# gates are unaffected (verified, not assumed: see P4).

## P1 - The pure module

`fallen-8-web-ui/src/lib/namespaceTotals.ts`

- `summarizeInventory(entries: NamespaceEntry[]): NamespaceTotals` with
  `{ namespaces, vertices: number | null, edges: number | null, unreported: number }`. A `null`
  count contributes nothing and increments `unreported`; `vertices`/`edges` are `null` only when no
  entry reported at all (spec 5b), which is what stops the aggregate from inventing a zero.
- `describeTotals(t): { label, title }` composing `N ns · V v · E e`, the `>=` prefix when
  `unreported > 0`, and the tooltip. Reuses `formatCountOrDash` / `ABSENT` from `lib/format.ts` - no
  second spelling of "absent", no second number formatter.

Tests: `fallen-8-web-ui/tests/namespace-totals.test.ts` (the pure cases of spec 6).

## P2 - The cell

Moved out of `ConnectScreen.tsx` into `fallen-8-web-ui/src/components/InstanceHealth.tsx` (spec 4.1);
the screen keeps a one-line import and its docblock points at the component.

- Keep the `/status` probe exactly as it is, including both `unauthorized` wordings and the CORS
  hint; states 1-3 are untouched code paths.
- Add the inventory query: `queryKey: [instance.id, "namespaces"]`, `queryFn: listNamespaces`,
  `enabled` only when the probe succeeded AND `isAuthorized`, `refetchInterval: 15_000`, `retry: 0` -
  the same key and cadence as `AppShell.tsx:182` / `NamespacesPanel.tsx:124`, so the active
  instance's row rides the existing cache row (spec 4.2).
- Render states 4-7 from `describeTotals`, with the 404 and the generic-failure fallbacks reading the
  probe's counts (the latter labelled `default:`).
- Add `data-testid="instance-health"` so a test can scope to the cell inside its row.

Tests: `fallen-8-web-ui/tests/instance-health.test.tsx` (the DOM cases of spec 6, including "no
inventory request is made" in the unreachable and unauthorized states).

## P3 - Docs and screenshot

- `docs/src/content/docs/studio.md:60`: rewrite the health-cell clause. It currently claims a lazy
  `GET /status` showing vertex/edge counts; the truth is an instance-level total over the namespace
  inventory, with the partial and degraded readings named. One home only - the Namespaces-panel
  sentence in the same paragraph keeps owning the per-namespace story.
- Recapture `docs/src/assets/images/screen-connect.png` per the repo recipe: build the SPA
  (`npm run build:apiapp`), run ONE isolated apiApp on a dedicated free port with
  `Fallen8__Durability__Volatile=true` and `Fallen8__Security__ApiKey=e2e-key`, then
  `F8_SCREENSHOT=1 F8_UI_URL=... npx playwright test e2e/screenshot-connect.spec.ts`. `F8_UI_URL`
  avoids the `:5000` webServer race. Confirm with `git status` that no other image changed.
- No README "Key features" entry: this is a correctness fix to an existing Studio surface, not a new
  user-facing feature.

## P4 - Gates

| Gate | Command | Why it applies |
|---|---|---|
| web-ui unit suite | `cmd /v:on /c "npx vitest run >vitest.log 2>&1 & echo EXIT=!ERRORLEVEL!"` | the change is in it; delayed expansion because the wrapper's own exit code lies |
| typecheck + bundle | `npm run build:apiapp` (runs `tsc -b`) | also produces the `wwwroot` the screenshot capture serves |
| docs site | `npm --prefix docs run build` | link-checked; `studio.md` changed |
| C# suite | not run; `git diff --name-only main` must show no `.cs`, no `.csproj`, no OpenAPI snapshot and no provider-descriptor change | claim to be verified, not assumed |
| browser probe | not applicable: no engine code, no `HostCapabilities` branch |  |

## P5 - Gate and merge

Council review (2-3 parallel reviewers) on Fable, findings fixed on the branch, then
`git merge --no-ff` into `main` and `git mv features/open/instance-level-health features/done/`.

## Run ledger

| Date | Step | Result |
|---|---|---|
| 2026-08-23 | Spec + plan written, branch created | done |
| 2026-08-23 | P1 + P2 implemented; 25 new tests (14 pure, 11 DOM) | green |
| 2026-08-23 | Mutation check 1: `summarizeInventory` folds a null count in as 0 | 3 tests fail (the two absent-glyph cases and the null-sum case) - the honesty rule is really pinned |
| 2026-08-23 | Mutation check 2: drop the `inventory.isPending` guard | 7 DOM tests fail, including the in-flight one, which caught the degraded `default:` label flashing |
| 2026-08-23 | Full web-ui suite | 89 files / 1042 tests pass, `EXIT=0` |
| 2026-08-23 | `npm run build:apiapp` (tsc -b + vite) | `EXIT=0` |
| 2026-08-23 | Live verification against a real apiApp (5 namespaces, empty `default`, 10 v / 12 e in two others) | cell read `5 ns · 10 v · 12 e` where the bare `/status` says `0 v · 0 e`; the unkeyed row still read `unauthorized — check the API key` |
| 2026-08-23 | Screenshot recapture (`screenshot-configuration` then `screenshot-connect`, `F8_UI_URL` on a dedicated port) | `screen-connect.png` updated (`0 v · 0 e` -> `3 ns · 0 v · 0 e`); `screen-configuration.png` came back byte-identical |
| 2026-08-23 | `npm --prefix docs run build` | `EXIT=0`, all internal links valid |
| 2026-08-23 | P4 C# claim verified: `git diff --name-only main -- '*.cs' '*.csproj' '*.json'` | empty |
