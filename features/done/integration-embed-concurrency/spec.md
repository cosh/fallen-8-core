# Integration embed concurrency - spec

## Problem

The summary embedding phase of an integration run is the one that takes hours. It is also the only
embedding path in the product that is still strictly sequential: `Fallen8RestTarget.EmbedSummariesAsync`
posts one chunk of 16 summaries, waits for it, posts the next. Against a REMOTE backend most of each
round trip is network and queueing rather than inference, so the runtime spends that time idle. The
apiApp's document-ingestion path already fixed this for itself (`Fallen8:Embedding:MaxConcurrentBatches`,
and the Nahil compose ships it at 2); this loop never got the equivalent.

**Raising the chunk SIZE is not the lever, and that is measured rather than assumed.** Two ceilings
bind it, both recorded on the test that pins `chunk <= 16`
([IntegrationsWritePathTest](../../../fallen-8-unittest/IntegrationsWritePathTest.cs), "a chunk must stay
inside BOTH the smallest shipped item cap and the client timeout"):

- **32 is the smallest item cap the product ships**, and it ships it on the backend this is about:
  `docker-compose.nahil.yml` sets `Fallen8__Embedding__MaxBatchSize` to 32. A larger chunk answers
  400, which is deliberately outside the degrade set, so it FAILS the run after the graph writes
  have landed and before reconciliation.
- **16 is a time budget.** At the ~3.5s per element a CPU-backed `bge-m3` costs, 32 elements is
  ~113s against a client timeout of the same order, and a real large extract died on its 86th chunk
  of 32 for exactly that reason.

Concurrency is the lever that touches neither: no single request gets larger or slower, so the item
cap and the per-request timeout are unaffected, and the win is the idle time between requests.

This is a **runtime-only change** to `fallen-8-integrations`. No engine method, no REST route, no
MCP tool, no OpenAPI change: the runtime issues the same `POST /embedding/elements` requests it
issues today, only more than one at a time.

## The invariant that makes this non-trivial

**The resume cursor is a PREFIX COUNT.** `SpooledProgress.Remaining()` resumes by skipping the first
`Embedded` entries of the plan (`Array.Copy(EmbedEntities, Embedded, ...)`), so `Embedded = 32` means
exactly "the first 32 entries of the plan are done". The cursor moves on `IRunProgress.Advance`,
which the chunk loop calls per chunk (`SpooledRunJournal.Advance` is the only per-chunk signal there
is, by deliberate design).

Out-of-order completion therefore breaks it silently and permanently: with four chunks in flight,
chunk 4 can land while chunk 2 is still out, a naive `written += take` reaches 48, the journal
records 48, and a pickup resumes at plan index 48 with chunk 2's sixteen summaries never embedded.
They are then lost for good, because the applier embeds only entities whose data CHANGED - so a
later run finds everything equal and embeds nothing. That is precisely the loss the journal exists
to prevent.

So the feature is not "add a semaphore". It is "keep the cursor honest while completion is
unordered".

## Decisions

| Decision | Choice | Note |
|---|---|---|
| Chunk size | **Unchanged at 16** | Both ceilings above still bind. This feature does not touch `EmbedBatchSize`, and the `<= 16` test stays as it is. |
| Concurrency | `Fallen8Target:EmbedConcurrency`, **default 1** | Default 1 is today's behaviour exactly, including the order requests go out in. It lives beside `TimeoutSeconds` in `Fallen8TargetOptions` because it describes how this runtime talks to its target. |
| Cursor rule | **Cumulative ack**: advance only over the longest COMPLETED PREFIX | Chunks 1, 3, 4 done with 2 outstanding leaves the cursor at the end of chunk 1. The spooled format is unchanged (still one `Embedded` integer), so `Remaining()` stays correct by construction and no resume state migrates. |
| Cost of that rule | A pickup may re-embed up to `EmbedConcurrency - 1` chunks | Re-embedding is idempotent (an embedding is element state, written by id) and bounded by a handful of chunks against a phase measured in hours. Cheap insurance versus the alternative, which is losing summaries. |
| Report count | **Total landed**, not the prefix | Two different numbers, deliberately: `EmbeddingWriteOutcome.Written` and `SummariesWritten` answer "how many vectors are on elements" (they feed `report.SummariesEmbedded`), while the cursor answers "what may be skipped on a pickup". Conflating them would either under-report real work or corrupt the resume. |
| Stop | Checked before each DISPATCH; in-flight chunks are **awaited**, never abandoned | A chunk is one atomic write on the target, and abandoning it in flight leaves a vector that landed uncounted - the one thing the written count must never be wrong about. The stop then carries the total landed, as today. |
| Degrade (403/429/502/503, client timeout) | Stops DISPATCHING new chunks, awaits what is out, reports the degrade | Same reasoning as today ("the next chunk faces the same model"), only now "stop the loop" has to mean "stop starting more". 429 matters more here, not less: concurrency is exactly what makes a fixed-window rate limit easier to trip, which is why the bound is configuration and defaults to 1. |
| 400 and other refusals | **Unchanged**: a real graph failure, carrying the count | It says the runtime sent something the route will never accept. |
| Progress reporting | Reports the PREFIX, so the number a watcher sees never goes backwards | The panel and the run state already treat `Advance` as monotonic; a prefix is monotonic, a total across unordered completion is not (it can jump). |

## Behaviour after the change

`EmbedSummariesAsync` keeps its signature and its outcome type. Internally:

1. The summaries are cut into the same 16-item chunks, in the same order.
2. Up to `EmbedConcurrency` chunks are in flight, bounded by a semaphore. With 1, the dispatch order
   and the request sequence are byte-for-byte what they are today.
3. Before dispatching each chunk the loop checks the abort, exactly where it checks today.
4. As each chunk completes it is recorded against its own index. The cursor is then advanced over
   the completed prefix and reported once through `progress.Advance(prefixWritten, total)`; a chunk
   that completed out of order contributes nothing to the cursor until its predecessors have.
5. A degrade or a stop ends dispatching, awaits the outstanding chunks (so their writes are counted
   and their vectors are not orphaned from the count), and then returns or throws with the TOTAL
   landed.
6. On success every chunk has landed, so the prefix is the total and both numbers agree - which is
   the only case that exists today.

Nothing about the run's observable contract changes: same requests, same report fields, same
diagnostics, same cancellation semantics. What changes is how much of the wait happens in parallel.

## Impact on existing features

| Feature / layer | Impact | Handling |
|---|---|---|
| Engine / REST / MCP / OpenAPI | **None** - the same route, called more often in parallel | No snapshot regeneration, no MCP coverage change |
| [integrations](../../done/integrations/) run lifecycle | The embed phase's internals; phases, report fields and diagnostics unchanged | The `<= 16` chunk test and the degrade/failure tests must pass UNCHANGED at the default of 1 |
| Run journal / spool ([integration-run-lifecycle](../../done/integration-run-lifecycle/)) | The cursor's meaning is preserved rather than changed: still a prefix, still one integer | No spooled-format change, no migration. The cumulative-ack rule is documented ON the journal, which owns that contract |
| Rate limiting on `/embedding/elements` | Concurrency makes the sensitive-endpoint fixed window easy to trip, and the review showed it is 30 requests per 10 s, i.e. 3/s, which a fast remote backend brushes even SEQUENTIALLY and blows through at concurrency 4 | **Changed during implementation.** Degrading on 429 was not survivable once this feature made it reachable: the phase would end, the run would COMPLETE, the journal would be deleted, and those entities could never be embedded again (the applier only embeds what changed). So a 429 now drops the rate to sequential, waits out the window and asks that chunk again; a refusal at the reduced rate still degrades, because that is the window rather than our burst |
| `docker-compose.nahil.yml` | **No change, deliberately.** That file overlays the embedding BACKEND and declares no `integrations` service, so the base compose's `Fallen8Target__EmbedConcurrency` already applies under the overlay - and `F8_NAHIL_EMBED_CONCURRENCY` is already taken by `Fallen8__Embedding__MaxConcurrentBatches`, so reusing the name would have collided | The knob lives in `docker-compose.yml` beside the runtime's other settings, with `.env.example` documenting it |
| docs `integrations.md` | The embed phase is described as the long one; the tunable is new user-visible behaviour | One paragraph on the setting, what it does not change (chunk size, item cap), and the re-embed cost of a pickup |
| Studio / NL-assist / architecture diagrams | No surface change | No screenshot, no retrain, no diagram change |

## Non-goals (with revisit triggers)

- **A completed-SET journal** (record which plan indices are done rather than how many). Exact, and
  it would remove the small re-embed cost, but it changes the spooled format, `Remaining()`,
  `Describe()` and every resume test. Revisit if the re-embed cost is ever measured to matter.
- **Raising `EmbedBatchSize`.** Both ceilings above are real and one is measured. Revisit only with
  the item cap discoverable (it is not on `/status` today) and a per-request budget that survives
  CPU inference.
- **A global cap across callers.** The apiApp's `MaxConcurrentBatches` semaphore is per-document, so
  nothing today bounds what the backend sees across concurrent callers either. If protecting the
  backend becomes the point, the bound belongs in `Fallen8EmbeddingProvider` where every path passes,
  not in each caller - a separate change, and a bigger one.
- **Retrying a degraded chunk.** Unchanged: the degrade set describes the provider or the window, so
  the next chunk faces the same answer.
- **Concurrency anywhere else in the run.** The graph writes are ordered against index flushes and
  reconciliation; only the embed phase is a bag of independent writes.
