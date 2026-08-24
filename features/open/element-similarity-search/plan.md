# Element similarity search: implementation plan

Branch `feature/element-similarity-search`. Spec: [spec.md](spec.md). Verified research:
[findings.md](findings.md).

Phases are ordered so each one is independently useful and independently testable. P1 is the
only phase that fixes a defect users can already hit; everything after it is reach.

## P1 Chunked summary write (FR-1)

The blocker. Without it, nothing else in this feature is observable at real size.

- `fallen-8-integrations/Graph/Fallen8RestTarget.cs`: give `EmbedSummariesAsync` an offset chunk
  loop, mirroring the `SendBatchedAsync` / `WriteBatchSize` pattern already in that file. Sum the
  written count across chunks. Keep the existing status-code rule per chunk: `{403, 502, 503}`
  stop the loop and degrade with the status named; anything else throws `GraphTargetException`.
- Chunk size is a named constant beside the existing write-batch constant. **32, not 64.** The
  planning note originally said 64 on the strength of the apiApp default; that is wrong, because
  `docker-compose.nahil.yml` sets 32, and a chunk over the cap is not survivable (400 is
  correctly outside the degrade set, so it fails a run whose graph writes already landed). Sizing
  to the smallest shipped cap makes every shipped configuration work. It also keeps each body far
  inside the 1 MiB `[RequestSizeLimit]` no configuration can move.
- Partial success is now reachable, which it was not before, so two reporting sites need fixing:
  `SnapshotApplier` must set `report.SummariesEmbedded` *before* the degrade branch rather than
  returning zero, and the diagnostic must name the shortfall rather than the whole batch. The
  `EmbeddingWriteOutcome.Degraded` docstring ("why nothing was embedded") is likewise no longer
  true and is the contract's own home for the rule.
- Tests: 200 summaries go out in chunks that are each within the cap and total exactly 200; a 503
  on the third chunk reports the 64 that landed and stops; a 400 on the second chunk still
  throws; a partially embedded run reports the landed count and a "1 of 2" diagnostic. Check
  `IntegrationsWritePathTest` for a call-count expectation this changes, and re-assert the
  zero-mutation invariant.
- Mutation-check the tests by widening the constant so no chunking happens; at least the chunking
  and mid-chunk tests must go red.

**Verify:** `dotnet test --filter "FullyQualifiedName~Integrations"`.

## P2 Studio can ask for embeddings (FR-2, FR-3, FR-4, FR-9 for Integrations)

- `src/api/types.ts`: add `embeddingName?: string` to `IntegrationJobRequest`.
- `src/screens/IntegrationsScreen.tsx`: checkbox plus optional name input beside the descriptor
  settings; both written into `buildJob`. Gate on the existing provider hook, disabled with the
  existing provider-off sentence as title when the provider is off. Show the descriptor's
  `entitySummaryTemplate` when present. Help text states the tabula-rasa recovery for a graph
  already imported without the flag.
- Same file: render the summaries-embedded tile only when the run asked, "not requested"
  otherwise.
- Tests: `buildJob` carries both fields when checked and neither when unchecked; the checkbox is
  disabled with the provider off; the tile reads "not requested" for a run that did not ask.

**Verify:** the Studio unit suite. Baseline it first; `delegate-editor.test.tsx` is a known
load-dependent flake and must not be attributed to this change.

## P3 Index form prefill and empty-index honesty (FR-5, FR-8, FR-9 for Indexes)

- `src/screens/IndexesScreen.tsx`: initialise dimension and metric from the `status.embedding`
  the component already holds, falling back to today's constants; name the provider identity
  beside the fields; add the provider-off sentence.
- `src/screens/QueryScreen.tsx`: when the selected vector index reports zero members, show the
  "no members yet, write embeddings or check the bound embedding name" hint beside Run, and
  render the index's `bound:<embeddingName>` in the vector form.
- Tests: the form prefills 1024/Cosine against a reported provider and 384 without one; the
  zero-member hint appears for an empty index and not for a populated one.

## P4 Find similar (FR-6, FR-7)

The user-visible point of the feature.

- `src/state/instanceStore.ts`: widen `ScanPrefill` from `{indexId}` to also carry a query
  vector, the source element id, and the source label and kind.
- `src/components/EmbeddingsTab.tsx`: a per-embedding-row "find similar" action reading the
  element's `$embedding:<name>` value off the already-fetched properties, the same read the tab
  already does.
- `src/screens/CanvasScreen.tsx`: the same action in the Detail panel, beside "Expand
  neighbors".
- `src/screens/QueryScreen.tsx`: accept the widened prefill, select the vector source, request
  `k+1`, and drop the source element id from the rendered hits. The inherited label is visible
  and clearable.
- Tests: the gesture prefills vector and label and navigates; the source element is absent from
  rendered hits; an element with no embedding of that name offers no action.

**Verify:** Studio unit suite, then the app itself. A mocked client test cannot prove the wire
body is right, so exercise this against a live apiApp with the provider on.

## P5 MCP coverage (FR-10)

- Extend the existing real-server fixture in `fallen-8-unittest/McpReadToolsTest.cs` with a bound
  vector index and two element embeddings; assert `f8_search` modes `vector` and `semantic`
  return the expected ids and honour label and kind. Add a `f8_mutate set_embedding` assertion.
- No new tool, no bridged-endpoint change, no deferral change.

## P6 Docs, screenshots, README

- `docs/src/content/docs/integrations.md`: name `embedSummaries` and `embeddingName` with its
  default, give the Studio path rather than curl only, and inline a create-index body that works
  (dimension from `GET /status`, metric Cosine, `embeddingName` default).
- `docs/src/content/docs/vector-search.mdx`: add `embeddingName` to the runnable example.
- `docs/src/content/docs/studio.md`: the Embeddings tab is described as set/replace/remove only;
  add find-similar. Update the Query and Indexes sections.
- `docs/src/content/docs/troubleshooting.md`: a row for "semantic search returns nothing or 503",
  pointing at the model-pull script.
- Root `README.md`: the key-features entry linking the live page.
- `features/done/autosar-arxml/spec.md`: fix the impact-table contradiction.
- Recapture `docs/src/assets/images/screen-integrations.png`; review the Browser/canvas and
  semantic-search captures.

**Verify:** `npm --prefix docs ci && npm --prefix docs run build` (fails on any broken internal
link).

## P7 Gate and merge

Full `dotnet build` and `dotnet test`, Studio unit suite, docs build. Then the council review
gate on the branch, fixes on the branch, and only then `git merge --no-ff` to `main`. Move
`features/open/element-similarity-search/` to `features/done/` in the same merge.

The browser probe is deliberately **not** run: no engine file is touched. If that stops being
true, it becomes mandatory.

## Run ledger

| Phase | State | Notes |
| --- | --- | --- |
| P1 | done | chunk 32, the smallest shipped cap, not the 64 default; partial-write reporting fixed in the applier and the outcome contract |
| P2 | done | checkbox + name field + template disclosure + honest tile; 8 tests, mutation-checked |
| P3 | done | prefill from status.embedding with edit-wins; empty-index hint on Query; 8 tests, mutation-checked |
| P4 | done | client-side gesture, bound-index lookup, label inheritance, k+1 + visible exclusion; 17 tests, mutation-checked |
| P5 | done | 7 arms on the real-server fixture: cosine ranking, label+kind constraints, arg errors, provider-absent semantic. set_embedding still uncovered (write tier, separate class) |
| P6 | not started | |
| P7 | not started | |
