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
| P6 | done | integrations.md, vector-search.mdx, studio.md, troubleshooting.md, README, arxml spec fix; screenshot recaptured (status embedding stubbed, viewport 1180) |
| P7 | done | gates green (dotnet 2094, Studio 1133, docs links valid); council returned merge-after-fixes, all fixes applied on the branch |

## Council gate outcome

Four review lenses over `main..HEAD`, every non-nit finding put through adversarial refutation:
16 survived, 8 refuted. Verdict **merge-after-fixes**. All of it was fixed on the branch before
merging rather than carried:

**Blocker (fixed).** Both new docs passages instructed `PUT /ns/<name>/tabularasa`. The route is
HEAD-only (`AdminController.cs:859`, and the pinned OpenAPI snapshot lists only `head`), so the one
recovery this feature documents for its own no-backfill non-goal could not work - and against the
shipped container the SPA fallback would most likely answer it 200 with the app shell, making it a
silent no-op rather than an honest 405. Corrected in both published pages and in the two feature
docs that seeded them, with the note that clearing also drops index definitions so the bound index
must be recreated.

**Two real consequences of chunking that P1 never examined (fixed).**

- `POST /embedding/elements` is the only route this runtime calls that carries the
  sensitive-endpoint rate limit (one process-wide fixed window, 30 permits / 10 s, QueueLimit 0).
  Unchunked that was one request; chunked, the recorded extract is ~384 of them, so **429 became
  reachable for the first time** - and it was not in the degrade set, so a throttle would have
  failed a run whose graph writes had already landed. 429 now degrades with the count that landed.
- The throw path discarded the accumulated `written`, so a mid-chunk non-degrade refusal reported
  `summariesEmbedded: 0` for vectors that are on their elements - contradicting spec §4 and P1's own
  test rationale. The count now rides on `GraphTargetException.SummariesWritten` and the applier
  records it before rethrowing.

**Also fixed:** my inserted paragraph in `vector-search.mdx` had split the options table and
orphaned the `model` row into literal pipe text (confirmed in the built HTML and in
`llms-full.txt`, invisible to the links-only gate); the new embedding-name box was unvalidated, so
a typo failed the run *after* the graph writes committed and the recovery is a tabula rasa; the k+1
over-fetch was unclamped, so a find-similar search at the advertised maximum k=1024 asked for 1025
and answered 400; and Clear did not reset the source-element exclusion, so it kept filtering an
unrelated query.

**The canvas hole is closed.** The earlier claim in this plan that no test reaches the canvas Detail
panel was wrong - `canvas-find.test.tsx` already selects into it. Five tests now cover the
find-similar call site there, including an **edge** fixture asserting `kind === "edge"`, which is
the arm neither the unit suite nor the live run touched and the one where an inverted flag would
have constrained an edge search to vertices and looked exactly like "nothing is similar". Reaching
it needed `getEdge` mocked, since an edge selection takes a different route than a vertex.
Mutation-checked: inverting the flag reddens that test alone.

## Carried forward, deliberately

1. **Acceptance 1 is unproven at scale.** A run over the recorded 12,261-entity extract has only
   been demonstrated at fixture scale. ~384 chunks is exactly where the rate limiter becomes
   reachable, and per-chunk latency on a real GPU Ollama or Nahil worker is unmeasured here, so
   whether 429 fires on every large import or almost never is unknown. It now degrades rather than
   failing, which is why this is a note and not a blocker.
2. **MCP `f8_mutate set_embedding` remains executed by no test**, so FR-10's second clause is unmet.
   No `fallen-8-mcp` file changed on this branch, the arm is main's untested code, and the read-tier
   fixture deliberately cannot register `f8_mutate`. It belongs in `McpWriteToolsTest.cs` as a
   follow-up rather than a spec edit.
3. **`EmbedBatchSize` is a compile-time 32** pinned to the smallest *shipped* cap.
   `Fallen8:Embedding:MaxBatchSize` is operator-writable with a minimum of 1, so a hand-lowered cap
   still fails a run. Removing the constant depends on publishing the cap on `GET /status`, which
   this feature scoped out to keep the OpenAPI snapshot untouched.
4. **The recaptured `screen-integrations.png` stubs the `/status` embedding block** (bge-m3, 1024,
   Cosine) to photograph the compose default, because the capture app has no provider. It is the
   first capture in that spec to fake instance state rather than replay a pinned artifact, so a
   change to the compose default model, dimension or metric makes the published screenshot lie with
   no gate noticing. Worth pinning the stub against `docker-compose.yml`.
5. **The gesture is Studio-only**, per the spec's first non-goal: no agent and no non-Studio client
   can ask "similar to this element". Revisit when an agent workflow needs it.
