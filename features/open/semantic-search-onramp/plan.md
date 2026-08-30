# Semantic search on-ramp: implementation plan

> Spec: [spec.md](./spec.md). Branch `feature/semantic-search-onramp` (worktree
> `.claude/worktrees/studio-semantic-search`). Studio-only change: everything below lives
> in `fallen-8-web-ui/` and `docs/`; the .NET solution, OpenAPI snapshot and MCP bridge
> must come out byte-identical.

## Worktree setup (before any JS gate)

`fallen-8-web-ui/node_modules` is not checked out here. Junction it from the main
checkout rather than reinstalling:

```powershell
New-Item -ItemType Junction -Path fallen-8-web-ui\node_modules `
  -Target C:\Users\HenningRauch\Code\personal\fallen-8-core\fallen-8-web-ui\node_modules
```

Remove it later with `[System.IO.Directory]::Delete(path)` (never
`Remove-Item -Recurse`, which follows the junction into the real store). Verify gate exit
codes through cmd with delayed expansion:
`cmd /v:on /c "npx vitest run ... & echo EXIT=!ERRORLEVEL!"`.

## Phase 1 - the `semantic` query mode (spec FR-1, FR-2)

1. `src/state/instanceStore.ts`: extend `QueryDraft` with the semantic mode's fields
   (text, k, kind, label, selected index id) following the existing shape; add the
   migrate step that lifts a persisted index-mode draft with `vectorSource: "text"` into
   the semantic mode and drops `vectorSource` from the vector form's remaining state.
2. `src/screens/QueryScreen.tsx`:
   - add `semantic` to the mode row; form = index selector (vector indexes only, bound
     badge, single-entry preselect) + text + k + kind + label; submit =
     `postEmbeddingSearch` (exists in `src/api/endpoints.ts`); results reuse the existing
     scored-element path (hydration, `ElementTable`, metric legend, send-to-canvas).
   - remove the `vector | text (provider)` source toggle from the index-mode vector form;
     that form is vector-paste only. Keep `ScanPrefill` consumption exactly as is.
   - provider gating: reuse `useEmbeddingProvider` and the null/false sentences verbatim
     from the current text-source block; the mode stays visible, controls disable.
   - empty-bound-index warning: reuse the existing zero-members check for the selected
     index.
3. Tests (`tests/embedding-query.test.tsx`, `tests/query-scans.test.tsx`): mode renders,
   provider null vs false gating, search happy path hits `POST /embedding/search` with
   the built body, index-mode form no longer offers text, draft migration, find-similar
   prefill still lands in index mode. Note the react19-act and 5s-timeout flake baselines
   before blaming a change.

## Phase 2 - the on-ramp (spec FR-3, FR-4)

1. QueryScreen semantic mode, zero vector indexes in inventory: render the on-ramp
   (sentence + inline create) per spec FR-3. Reuse the Indexes screen's create call and
   the provider-identity prefill (extract a shared helper if the prefill logic would
   otherwise be copied; do not duplicate it). On success invalidate the status/inventory
   query, select the new index, reveal the form.
2. `src/screens/IndexesScreen.tsx`: the FR-4 one-liner when no `VectorIndex` exists.
3. Tests: on-ramp render conditions (zero vector indexes AND provider on; provider
   off/unknown shows only the sentence), create-then-search flow with mocked client,
   Indexes hint presence/absence (`tests/index-management.test.tsx`).

## Phase 3 - docs, screenshots, gates

1. Docs edits per spec FR-5 (studio.md, troubleshooting.md, vector-search.md,
   samples.md). Sweep `docs/src/assets/images/` for captures replaying the Query vector
   form or Indexes empty state; recapture with the isolated-app + `F8_UI_URL` flow
   (`features/done/*/` screenshot notes; avoid the :5000 webServer race in a worktree,
   never pipe the background launch through `Select-Object`).
2. Gates, all green before review:
   - `npx tsc --noEmit` and `npx vitest run` in `fallen-8-web-ui` (cmd exit-code check);
   - `dotnet build fallen-8-core.sln` and `dotnet test fallen-8-core.sln` (normal
     verbosity, never `-v q`) - expected untouched, run to prove it;
   - `powershell -File scripts/update-openapi-snapshot.ps1` must print an empty diff;
   - `npm --prefix docs ci && npm --prefix docs run build` (link check);
   - repo hygiene grep for the two forbidden words before any commit.
3. Feature record: move `features/open/semantic-search-onramp/` to `features/done/` in
   the landing PR/merge, status line updated.

## Out of the plan, recorded

No GitHub issue or PR unless asked. Review gate (council) runs after implementation,
before merge to `main`; code never lands on `main` directly.
