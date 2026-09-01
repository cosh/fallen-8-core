# Integration run visibility: specification

> **Status:** implemented and merged (2026-08-25) on branch `feature/integration-run-visibility`.
> Council-approved after three blockers were fixed on the branch; see plan.md.

## 1. The problem, which is not a missing progress bar

Three shipped facts combine into a hole:

1. `POST /integration/job` is **synchronous**. The report exists only as that HTTP response, and the
   runtime keeps no run history, so the response is the only copy that will ever exist.
2. The apiApp's integrations proxy times out at **120 s**
   (`Fallen8IntegrationsOptions.TimeoutSeconds`, not raised in compose). One shared client, no
   per-route exemption.
3. The run **deliberately outlives the caller**: the apply phase is invoked with
   `CancellationToken.None` (`JobRunner.cs:256`), pinned by
   `TheApplyPhaseFinishesEvenAfterTheCallerHasWalkedAway`.

So for any source that takes longer than two minutes, the connection that would carry the report is
*guaranteed* to be gone while the run correctly keeps going. The operator sees a proxy error, the
work continues invisibly, and when it finishes or fails there is **no surface anywhere** that can say
which.

This is not hypothetical. A real ARXML import wrote its whole graph and then embedded summaries
for hours before dying part-way through embedding. None of that was reportable: not the progress,
not the outcome, not the diagnostics.

Two phases make it worse, because both can run for a long time while the graph shows no change at
all, which is indistinguishable from a hang: **observe** (parsing a large extract) and **embed
summaries** (hours of model inference).

## 2. What a run actually does

The phases a progress surface reports are the ones that exist, not invented stages:

| Phase | What it is | Counter |
| --- | --- | --- |
| `observe` | the provider reads the source | none: the provider owns that loop |
| `validate` | snapshot validation | none |
| `resolve` | claim-key lookups against the identity index | total only |
| `write-elements` | batched element and property writes | total only |
| `write-edges` | derived-key edge writes | total only |
| `embed-summaries` | summary embedding, chunked | **advances**, per chunk |
| `reconcile` | withdraw by set difference, then delete | none |

*Corrected during implementation.* An earlier draft of this table claimed four phases carry a working
`n of m`. They do not: only `embed-summaries` advances, because only it loops where the sink can
reach. The others publish their **total** when the phase opens, which tells a reader how much work
the phase covers but never moves. That is honest and it is enough for the phases in question, all of
which are seconds; the one phase that runs for hours is the one that ticks. Threading the sink into
the target's element and edge batch loops, and into reconciliation, is the follow-up that would make
the other columns true.

## 3. FR summary

- **FR-1 Accepted, not awaited.** `POST /integration/job` answers **202** with
  `{runId, instanceId, providerId}` and executes the run on a background task. Everything that can
  be judged *before* the run starts still happens synchronously and still answers 400/409 - the job
  shape, the provider lookup, the identity shape, the file limits, and claiming the `RunGate`. An
  accepted job is one that really started; a rejected one never ran, exactly as today.
  - **A third outcome, found at the merge gate and not in the draft.** A run can also *return a
    report* before it enters its first phase: the credential-unusable class does exactly that. That is
    neither "started" nor "rejected", and treating it as started answered 202 while dropping the only
    copy of the report. Such a run answers **200 with its report**, inline, as it did before this
    feature. The route's rule is therefore three-way, not two-way, and it is stated here because one
    counterexample is all it took to make the two-way version a lie.
- **FR-2 The run is not tied to the request.** The background run uses its own token, so closing the
  browser cannot cancel it. This makes explicit what `CancellationToken.None` on the apply phase
  already implied for half the run. It is dispatched with `Task.Run`, because otherwise everything up
  to the provider's first await - including a file provider decoding and parsing a whole extract -
  would run on the request thread and the 202 would wait for it.
- **FR-3 `?wait=true` keeps the old behaviour.** The synchronous shape stays reachable for scripts
  and small sources: same 200 and same report body as today. Documented as unsuitable for a large
  source, because the proxy timeout still applies to it.
- **FR-4 In-flight progress.** `GET /integration/run` returns every tracked run;
  `GET /integration/run/{instanceId}` returns one. A tracked run carries the run id, provider,
  instance, namespace, start time, the current phase, that phase's `done`/`total` when it has one,
  the phases already completed, and the counts accumulated so far.
- **FR-5 The outcome survives the request.** When a run ends, its slot keeps the **final report** (or
  the failure and `errorKind`) so the operator can learn the outcome after the connection is long
  gone. This is the point of the feature.
- **FR-6 Bounded, and not run history.** **One slot per identity**, superseded by that identity's
  next run, capped at 32 identities with the oldest *finished* slot evicted first (an in-flight run
  is never evicted), held in memory and dropped entirely on restart. See §4.
- **FR-7 Studio run panel.** The Integrations screen shows the phase list with live counts and
  elapsed time while a run is in flight, and the report when it ends. It polls
  `GET /integrations/run/{instanceId}` and **survives a page reload**, because the identity is
  enough to re-find the run.
- **FR-8 The proxy timeout becomes correct rather than raised.** With FR-1, `POST` returns in
  milliseconds, so 120 s is right for every route and no default changes. The one place it still
  bites is `?wait=true`, which is documented.

## 4. Why this does not break "the runtime keeps nothing"

The integrations runtime deliberately holds no schedule, no run history and no credential, and that
rule is load-bearing: a runtime that remembered runs would own a second copy of a decision it cannot
judge, and one that remembered credentials would have something to rotate.

What FR-6 adds is **the state of the current run, and of the most recent run per identity**. It is
not history: there is no list of past runs, nothing is queryable by time, nothing is persisted, and
the next run under the same identity overwrites the slot. A restart loses all of it, which is
correct, because the only consumer is an operator watching a run they just started.

Stated as the rule this feature adds: *the runtime may remember what is happening now, and what
happened last, per identity. It may not remember more than that.* If a future need wants a real run
log, that is a different feature with a different owner (the graph, or the operator's own
collection), and this slot is not it.

## 5. Non-goals, each with a revisit trigger

- **No persistence.** A restart mid-run loses the progress *and* the outcome; the run itself dies
  with the process anyway, so there is nothing to resume. Revisit when runs become resumable, which
  they are not.
- **No cancellation.** There is no "stop this run". The apply phase is explicitly designed to
  finish, and stopping half way through reconciliation is the one thing that would leave the graph
  in a state no later run reasons about correctly. Revisit only with a designed abort point.
- **No per-item progress inside `observe`.** The provider owns that loop and the contract does not
  ask it to report; instrumenting a multi-million-element XML parse would mean instrumenting the
  reader. The phase is
  named and timed, which is what distinguishes "parsing" from "hung". Revisit if a provider is
  reported as appearing hung for minutes with no phase change.
- **No run history, no schedule.** Unchanged (§4).
- **No progress from inside an embedding chunk.** A chunk is one HTTP call to the target, so the
  finest granularity available is the chunk: `embed-summaries` advances 16 at a time. Honest, and
  at ~3 s per element that is a visible tick roughly every 45 s.

## 6. Impact on existing features

| Area | Impact |
| --- | --- |
| Engine (`fallen-8-core`) | None. No engine file is touched, so the browser probe is not implicated. |
| Integrations contract | **Breaking on the job route**: 200-with-report becomes 202-with-run-id unless `?wait=true`. Acceptable and deliberate: the 200 was unreachable for any real source. `JobRunner.RunAsync` keeps its synchronous signature, so `IntegrationsWritePathTest`, `IntegrationsFileUploadTest` and the conformance suite - which drive the runner directly - are untouched. |
| OpenAPI snapshot | Two new proxied GET routes plus the changed POST response. Regenerate with `scripts/update-openapi-snapshot.ps1`. |
| MCP | `/integrations` is deferred **by prefix**, so the new routes would auto-defer under a stale reason and the coverage gate would stay green without anyone deciding. Per the trap recorded in `features/done/element-similarity-search/plan.md`, this must be a deliberate decision: the deferral reason is updated to name the run routes, or they are bridged. Decide in P2, do not let the prefix decide. |
| Provider-descriptor snapshot | Untouched. No descriptor field changes. |
| Studio API-contract sweep | Two new client functions need `ENDPOINT_CALLS` entries. |
| Docs | `integrations.md` currently shows a `curl` whose response is the report; that becomes the 202 plus a poll. `troubleshooting.md` gains the case this feature exists for: "my run vanished". |
| Screenshots | The Integrations screen gains the run panel, so recapture `screen-integrations.png`. |
| Conformance suite | The verifier drives `JobRunner`, not the route, so the invariants it pins (zero mutation on an unchanged source, no similarity in identity, no forbidden property keys) are unaffected. Verify rather than assume. |

## 7. Acceptance

1. `POST /integration/job` with a bad identity still answers 400 **without** starting a run; a
   second run under an in-flight identity still answers 409.
2. A good job answers 202 in milliseconds, and `GET /integration/run/{id}` immediately shows a
   phase.
3. Closing the caller does not stop the run, and the outcome is readable afterwards from
   `GET /integration/run/{id}`.
4. During an ARXML import the Studio panel shows `observe`, then `write-elements` with a rising
   count, then `embed-summaries` with `n of m`, and the report when it ends - across a page reload.
5. A 33rd identity evicts the oldest finished slot and never an in-flight one.
6. `dotnet test` and the Studio suites green; docs site builds with no broken link.
