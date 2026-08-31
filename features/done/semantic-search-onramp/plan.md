# Semantic search on-ramp: implementation plan

> **Outcome:** all three phases done. What the code settled differently from this plan is
> recorded in [spec.md](./spec.md) as *(refined)* notes, not rewritten here. Gate results and
> the live verification are in "What was actually run" at the foot of this file.

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
3. Feature record: move this directory from `features/open/` to `features/done/` as the work
   lands, status line updated. (Done, in the landing merge.)

## What was actually run

Gates, all from the worktree (`node_modules` junctioned from the main checkout for both
`fallen-8-web-ui` and `docs`):

| Gate | Result |
|---|---|
| `npx tsc -b --force` | exit 0 |
| `npx vitest run` | 98 files, 1267 tests pass (baseline before the change: 98 / 1235) |
| `npm run build:lib` | exit 0, artifact check passed (QueryScreen's new imports pull nothing into the canvas bundle) |
| `npm --prefix docs run build` | exit 0, 41 pages, "All internal links are valid" |
| `dotnet build fallen-8-core.sln` | exit 0 |
| OpenAPI / provider-descriptor / MCP-coverage / browser probe | not run, and not needed: `git status` shows no file outside `fallen-8-web-ui/` and `docs/` changed, so no input to any of them moved |

**Live verification, not just mocks.** A mocked client cannot catch a wrong wire body, so the
flow was driven against two real apiApp instances (volatile, keyed, SPA built into `wwwroot`,
Playwright pointed at them with `F8_UI_URL`): one with no embedding provider, one with the
provider enabled at 1024 dimensions and a deliberately unloadable Onnx backend. Confirmed on the
real thing: the query-type control offers `property | index | semantic`; the on-ramp prefills
1024 / Cosine / `default` / `embeddings` from the provider's own numbers; its create posts a real
`POST /index` that the server turns into a genuinely bound projection
(`{"indexId":"embeddings","pluginType":"VectorIndex","embeddingName":"default","capabilities":["vector"],"keys":0,"values":0}`);
the on-ramp then disappears with the new index selected; the empty-index line fires because
nothing is embedded yet; running the search renders the server's actual `503` naming the
unloadable model path rather than a generic failure; and the Indexes screen's pointer is gone now
that a vector index exists. Re-checked on the real server after the review changed the create
hand-off. The throwaway probe specs were deleted afterwards.

**Review.** Five lenses plus a refuting skeptic each; thirteen confirmed defects, all fixed and
pinned, listed in [spec.md](./spec.md) §6. Two process notes worth keeping: the reviewers wrote
executable probes into the worktree rather than arguing, which is why the findings held up (and
their leftovers had to be swept out of a commit); and one gate here is a trap, since the repo has
no prettier config, so running `npx prettier` on a file silently reformats it to an 80-column
style the codebase does not use and tripled one diff before it was reverted.

**Council gate.** Convened on the state that would actually land (origin/main merged in first),
five seats (contract, regression, honesty, quality, docs) and a chair reconciling them against
the code. Verdict: BLOCK, on five items, all addressed before merge:

1. Provider off with zero vector indexes stacked two provider-off paragraphs, and the surviving
   one pointed at a vector (kNN) form that cannot exist in that state. All four non-regression
   seats measured it; the chair upgraded it from the regression seat's "cosmetic" because a
   pointer at an absent control is the dead end this feature deletes.
2. The one provider-off test without a pin on the second paragraph, which is why it escaped.
3. `studio.md` promised the pasted-vector form unconditionally.
4. The feature record was still under `features/open/`.
5. Three false sentences in the spec, which freezes as history on the move - including a claim
   that `screen-indexes.png` shows the FR-4 pointer. Checking it disproved the recapture as well:
   the frame on main was already correct, so that image is restored and the pointer is recorded
   as unphotographed.

Four follow-ups the chair listed as non-blocking were taken anyway, being cheap and about
accuracy: the k and dimension bounds now come from constants rather than five hand-typed copies,
the bind-embedding field states why it disables the create, the Indexes half of the shared
dimension guard got the test its twin already had, and a README bullet stopped implying the
on-ramp checks for embeddings.

**The screenshot follow-up is closed too.** `query-semantic-search.png` was recaptured against a
real bge-m3 provider and restored to `samples.md`. What made it possible was that the weights were
already in the `f8-ollama-models` volume, so it took a container start rather than a 1.2 GB pull:
serve that volume with the repo's own Ollama image, run the apiApp natively against it with the
compose environment's `Fallen8__Embedding__*` values, and the capture spec does the rest (it
asserts the top row is vertex 0, so a wrong model fails the run instead of publishing a wrong
ranking). Recorded here because the next person to need a provider-backed capture will assume a
download.

Still open, and the only piece of this feature that is: the FR-4 pointer is photographed nowhere.
`screenshot-indexes.spec.ts` deliberately leaves the inventory empty, and the pointer requires a
non-empty one. Seeding a single dictionary index in that capture would picture the pointer AND
give that frame the inventory table it currently lacks, which is a change to another frame's
subject and so was left for its own decision.

## Out of the plan, recorded

No GitHub issue or PR unless asked. Review gate (council) runs after implementation,
before merge to `main`; code never lands on `main` directly.
