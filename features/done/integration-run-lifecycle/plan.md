# Integration run lifecycle: implementation plan

Companion to [spec.md](spec.md). Branch `feature/integration-run-lifecycle`, phases land as
separate commits on that branch, each phase leaves the whole suite green. Cancel ships
first because it is the smallest cut and removes the 409 dead end on its own; multi-file
second; resume last because it builds on both (cancel deletes spool entries, multi-file
changes what a snapshot is).

## Phase 1: cancel

1. **Run token.** Give each `RunTracker` slot a `CancellationTokenSource` created at
   materialise time and disposed at terminal time. `JobRunner` links it into what it passes
   to apply; the request token keeps its exact current meaning (honoured through
   observe+validate, ignored during apply). Rewrite the point-of-no-return comment in
   `JobRunner.cs` to the three-token story (request, run, host) since that is now the one
   home of the explanation.
2. **Safe points.** `SnapshotApplier.ApplyAsync` already receives a token and threads it to
   target calls; make the caller pass the linked token and add explicit checks between
   phases, between write batches, and between embed chunks. On trip: stop, skip
   reconciliation unconditionally (spec section 4, the data-loss argument), and surface a
   `cancelled` terminal outcome distinct from failure.
3. **Terminal state.** `RunStateDto` gains `cancelled: Boolean`; `JobReport` records the
   convergence note as a diagnostic. TS mirror in `fallen-8-web-ui/src/api/types.ts`.
4. **Routes.** Runtime `POST /integration/run/{instanceId}/cancel` in
   `IntegrationEndpoints.cs` (202 delivered / 404 nothing in flight); proxy
   `POST /integrations/run/{instanceId}/cancel` in `IntegrationsController.cs` with XML
   docs and `[ProducesResponseType]`. Append the one-sentence pointer to the `RunGate` 409
   message.
5. **Studio.** Cancel control on the run panel (`integration-run-cancel`, two-step),
   cancelled terminal rendering, endpoint wrapper + `ENDPOINT_CALLS` entry (and backfill
   the two missing run-visibility entries in `api-contract.test.ts`, recorded gap).
6. **Tests.** Runtime: cancel in each phase hits a safe point and never reconciles or
   withdraws (shape of `UnreadableSourceFails`); repeat cancel; cancel with no run in
   flight; gate released after cancel; cancelled slot survives as the identity's last run.
   Studio: control renders only while running, two-step, cancelled state rendering.
7. **Gates.** Full suite, OpenAPI snapshot regenerate (additions: cancel route), review the
   MCP deferral wording in `McpRestCoverageTest.cs` against the new route.

## Phase 2: many files

1. **Descriptor.** `multiple` on `ProviderSetting` (File-only, catalog refuses misuse at
   startup). ARXML declares it; label/help updated per spec FR-1. Update the
   allowed-fields lists in `ProviderDescriptorSnapshotTest`, the TS mirror, and regenerate
   the pinned snapshot.
2. **Wire + files.** `IntegrationJob` accepts object-or-array per file setting (converter),
   ordered, duplicate names refused, array-for-single refused. `JobFiles`/`IJobFiles` grow
   ordered multi-payload access; single-file accessors keep meaning "the only file".
   `RecordingFilesFactory` counts reads across files.
3. **Bounds.** `Integrations:MaxJobFileBytes` (default 512 MiB) enforced at normalize;
   proxy `JobTransportLimit` to 768 MiB; runtime `TransportBound` to 832 MiB; fix the
   stale "(48 MiB)" comment in `fallen-8-integrations/Program.cs`; job-route OpenAPI
   description updated (snapshot regenerate).
4. **ARXML merge.** `ArxmlReader` grows a multi-document entry point: stream each document
   into one shared `Collected` in job order, resolve once. Within-file duplicate keeps the
   per-path diagnostic; cross-file re-declaration collapses first-wins with one aggregate
   per-file diagnostic (new wire code). Bus gate over the union.
5. **Studio.** Multi-staging per spec FR-6 (`multiple` input attribute, dropzone append,
   per-file remove, total size), array emission in `buildJob`, 413 copy names the total.
6. **Tests.** Reader: cross-file reference lands; cross-file duplicate first-wins +
   aggregate diagnostic; within-file duplicate unchanged; union bus gate; order
   determinism (same files, same order, identical snapshot; swapped order differs only
   where first-wins says so). Blueprint: multi-file ARXML fixture through the conformance
   verifier (Deterministic, Idempotent). Normalize: array shapes, duplicate names, totals
   over/under `MaxJobFileBytes`. Studio: staging list behaviors, single-file settings
   unchanged.
7. **Gates.** Full suite, descriptor snapshot script, OpenAPI snapshot script,
   **screenshot recapture** (`F8_SCREENSHOT=1 npx playwright test
   e2e/screenshot-integrations.spec.ts`), docs build.

## Phase 3: resume

1. **Spool.** `Integrations:SpoolDirectory` (empty = off). Entry per identity
   (`run-<instanceId>.json` naming, prefixed so Windows reserved device names cannot
   collide), atomic write (temp + rename), format version. Contents per spec FR-10: job
   envelope without credentials and without file bytes; validated snapshot after
   observe+validate; embed journal. Intent record at accept (FR-11). Delete on every
   terminal outcome, including cancel.
2. **Embed journal.** Journal dirty entity indices **ahead of each write batch**, advance
   the cursor after each embed chunk (both flushed at chunk/batch boundaries, no fsync per
   element). Resume embeds journalled-behind-cursor entities in journal order:
   at-least-once, never skipped.
3. **Startup resume.** Scan spool, oldest first, re-validate, re-resolve against the
   current graph, same run id, slot marked `resumed`, normal gate. Refusals (version,
   validation) become honest terminal slots. Reconcile only at the true end over the
   re-resolved `claimedNow`. The mid-write crash window (elements created, claims index
   not yet flushed) must resolve without twins: go through the repair-aware resolution and
   index re-assertion seams, and pin that window with a test.
4. **Shutdown.** Observe `IHostApplicationLifetime.ApplicationStopping` at the same safe
   points; checkpoint and stop with no terminal state, spool kept. Distinguish from the
   run token (cancel = terminal + spool deleted).
5. **Retry.** Bounded connection-failure retry (3 attempts, short backoff) in
   `Fallen8RestTarget` around target calls, never around a non-idempotent partial batch
   without re-resolution semantics to cover it (embedding and reads are safe; element
   creation retries only on connection-refused before a response was read).
6. **Compose + surfacing.** Volume `f8-integration-runs`, `Integrations__SpoolDirectory`,
   comment rewrite; `RunStateDto.resumed` + TS mirror; Studio resumed note; report
   diagnostic naming post-resume counts.
7. **Tests.** Kill-at-boundary matrix (after intent, after snapshot, mid-write, between
   journal and its batch, mid-embed, before reconcile): resume writes nothing twice,
   embeds at least once each, reconciles once. Cancel deletes the spool. No spool
   configured = no disk writes (pin with a directory watcher in the test, not by reading
   the code). Corrupt/half-written entry refused honestly. Two identities spooled resume
   both. **Linux verification before push** (CI is Linux non-root: path handling, rename
   atomicity, volume permissions; use the docker recipe).
8. **Docs.** `integrations.md` (Files, remembered, run states), `studio.md` paragraph,
   `troubleshooting.md` restart section. Docs build green.

## Phase 4: sweep and record

1. Re-run every gate end to end on the branch: full suite, descriptor snapshot, OpenAPI
   snapshot, screenshot, docs build, `node --check` untouched but compose changed so
   `scripts/env-up.js` still resolves profiles, grep the forbidden words.
2. Re-verify the impact table in spec.md against the files as they are; update the spec's
   "as built" notes where reality diverged.
3. Review gate over the finished branch, then merge; move this directory to
   `features/done/integration-run-lifecycle/`.

## Decisions and revisit triggers

- Cancel first, resume last (ordering above): each phase is independently shippable.
- No parallelism anywhere (spec section 3); triggers recorded there.
- Spool off by default; the compose environment is what turns it on. Mirrors the
  embeddings-provider pattern (engine-adjacent capability, deployment opt-in).
- Same run id across resume, so the Studio `expectedRunId` keeps matching and "one slot per
  identity" stays the retention story.
- At-least-once embeds via journal-ahead, chosen over exactly-once bookkeeping: a duplicate
  embed is an idempotent overwrite of the same vector slot; a skipped embed is the
  unrecoverable loss this feature exists to prevent.
- 512 MiB default job total: covers a full multi-domain vehicle (several extracts of a few tens of MiB)
  without pretending the single-request design is a bulk pipeline. Trigger to revisit: a
  real extract set that does not fit.

## Progress

| Phase | State |
| --- | --- |
| 1 cancel | done: runtime, route, proxy, Studio control, tests, OpenAPI snapshot |
| 2 many files | done: descriptor flag, wire shape, bounds, ARXML union merge, Studio staging, tests |
| 3 resume | done: spool, journal, startup resume, shutdown checkpoint, compose volume, tests |
| 4 sweep | done: docs, README, descriptor and OpenAPI snapshots, screenshot recapture, full gate run, Linux verification |

Deviations from the phase order above: phases 1 to 3 landed as one commit rather than three,
because the wire and run-lifecycle changes overlap in the same files (`JobRunner`,
`SnapshotApplier`, `RunTracker`) and splitting them would have produced intermediate commits that
did not build. What the spec's "As built" section records is the design deltas; this is the only
process deviation.

**FR-15 was answered differently, and the difference matters.** The spec asked for a bounded retry
INSIDE a run, around connection-level failures. That is not what shipped, because a retry loop around
graph writes cannot distinguish "retried and succeeded" from "applied twice" without a test that
proves it, and inventing one for a failure the spool already covers is the kind of change this
feature exists to avoid.

What shipped instead is a retry ACROSS RESTARTS, and it closes a real hole the spool would otherwise
have had: this container restarts alongside the graph it writes into and may come up first, so a
resumed run can fail purely because the graph was not answering yet. An entry deleted on that failure
would lose exactly the hours of work the spool exists to protect, for a reason that says nothing about
the job or the source. So a RESUMED run that failed on the GRAPH keeps its entry and is picked up
again on the next start, bounded by `SpooledRun.Attempts` against `RunSpool.MaxAttempts` (three) so a
graph that is gone for good cannot leave a run retried on every start for ever. The last attempt is
reported as the graph failure it was rather than as a synthetic giving-up, because that message names
the system to go and look at. Found by reviewing the fixes rather than the code, and pinned by two
tests plus a mutation check.
