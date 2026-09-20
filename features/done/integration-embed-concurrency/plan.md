# Integration embed concurrency - plan

Runtime-only change to `fallen-8-integrations` (no engine/REST/MCP/OpenAPI surface). Phased so the
correctness rule lands with its tests before any concurrency is switched on anywhere.

Workflow: feature CODE on `feature/integration-embed-concurrency`; review gate before merge. The
gate matters more than usual here: the failure this guards against (a cursor advanced past a gap) is
silent, permanent, and only observable after a restart.

## Phase 1 - The cumulative-ack cursor, at concurrency 1

**Goal:** the loop is rewritten around "record per chunk index, advance over the completed prefix",
and behaves byte-for-byte as today because the bound is still 1.

- `Fallen8TargetOptions`: add `EmbedConcurrency` (default 1) with the doc comment naming what it does
  NOT change (chunk size, item cap, per-request timeout) and the re-embed cost of a pickup.
- `GraphTargetFactory`: pass it to the target's constructor; the constructor parameter defaults to 1
  so every existing `new Fallen8RestTarget(client, ns)` in the tests keeps compiling and behaving.
- `Fallen8RestTarget.EmbedSummariesAsync`: restructure to
  - cut the same 16-item chunks in the same order (unchanged `EmbedBatchSize`),
  - track `landed[chunkIndex]` and a `completed[chunkIndex]` flag,
  - keep TWO counters: `totalLanded` (for the outcome, the exception and the report) and
    `prefixLanded` (for `progress.Advance`), with the prefix computed by walking `completed` from
    the first incomplete index,
  - keep the abort check before each dispatch, and the degrade/failure classification exactly as it
    is.
- At this phase the dispatch loop still awaits each chunk immediately, so ordering is unchanged.

**Tests** (extend `IntegrationsWritePathTest`): every existing embed test must pass UNCHANGED. Add:
prefix and total agree on a fully successful run; a mid-run 503 reports the total landed and stops
dispatch; the `Advance` sequence is monotonic and equals the chunk boundaries.

## Phase 2 - Concurrency, and the cursor under unordered completion

**Goal:** more than one chunk in flight, with the cursor provably honest.

- Bound dispatch with a `SemaphoreSlim(EmbedConcurrency)`, collect the chunk tasks, and await them
  on every exit path (success, degrade, stop, failure) so no write is left orphaned from the count.
- A degrade or a stop stops DISPATCHING and awaits what is out; the first degrade reason wins and is
  reported.
- On stop, `abort.ThrowIfRequested(totalLanded)` is raised after the outstanding chunks have been
  awaited.

**Tests** - this is where the feature is actually judged, so these are behavioural and adversarial:
- **The gap test.** With concurrency 4 and a handler that completes chunk 2 LAST, assert the
  recorded `Advance` values never skip a chunk: no value may exceed the completed prefix at the time
  it is reported. This is the test that fails on a naive `written += take`, and it is the reason the
  feature exists.
- **The pickup test.** Interrupt with a gap outstanding, then resume from the journal and assert the
  entities the gap contained ARE re-embedded (and that nothing after the cursor is skipped).
- Total-versus-prefix: a run where chunk 2 fails after 3 and 4 landed reports the total landed while
  the cursor stayed at chunk 1.
- Concurrency is really bounded: at most `EmbedConcurrency` requests in flight, asserted from the
  handler.
- At concurrency 1 the request ORDER is still chunk order.
- 429 with concurrency on still stops dispatch rather than sending the rest.
- A cancel mid-flight: the in-flight chunk's write is counted, the report is the total, and the
  `RunCancelledException` carries it.

## Phase 3 - Compose, docs, sweep

- `docker-compose.nahil.yml`: an env knob for the integrations service beside the ingestion
  concurrency it already sets, with the default stated.
- `docs/src/content/docs/integrations.md`: one paragraph in the embed-summaries material - what the
  setting does, what it does not change, and that a pickup may re-embed up to N-1 chunks.
- Confirm the impact table: no OpenAPI/MCP/descriptor snapshot drift, no screenshot (no UI change),
  no NL-assist retrain.
- Docs build green; full `dotnet test` green.
