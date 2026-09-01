# Integration run visibility: implementation plan

Branch `feature/integration-run-visibility`. Spec: [spec.md](spec.md).

Ordered so the runtime tells the truth first and the UI reads it second. P1 alone already makes a
long run observable over `curl`, which is the part that is currently impossible.

## P1 The runtime remembers the current run (FR-1 to FR-6)

- `fallen-8-integrations/Run/RunTracker.cs` (new): the per-identity slot. One `RunState` per
  instance id, capped at 32, evicting the oldest **finished** slot and never an in-flight one; in
  memory, dropped on restart. Owns the run id (a GUID, passed in - no `Guid.NewGuid()` where a test
  cannot control it), the phase, the phase counter, the completed phases, the start time, and on
  completion the report or the failure.
- `fallen-8-integrations/Run/IRunProgress.cs` (new): the sink the run reports through -
  `EnterPhase(String phase)` and `Advance(Int32 done, Int32 total)`. A **no-op default** so every
  caller that drives `JobRunner` directly keeps compiling and keeps its meaning; the tracker's
  implementation is the only real one.
- Thread the sink through `JobRunner` and `SnapshotApplier`. Phase names are the seven in spec §2 and
  live as constants in one place, because the Studio renders them and a typo would be a silent
  missing row.
  - `observe`, `validate` in `JobRunner`.
  - `resolve`, `write-elements`, `write-edges`, `embed-summaries`, `reconcile` in `SnapshotApplier`,
    each advancing where it already loops in batches.
- `fallen-8-integrations/Hosting/IntegrationEndpoints.cs`: `POST /integration/job` validates and
  claims the gate synchronously, then starts the run and answers 202. `?wait=true` keeps today's 200
  and report. Two new routes: `GET /integration/run` and `GET /integration/run/{instanceId}`.
  - The background task must not be fire-and-forget-and-forgotten: hold it on the tracker so an
    unobserved exception cannot become a silent process-level unhandled rejection.
- Tests: a rejected job never reaches the tracker; an accepted one appears with a phase before it
  finishes; the outcome is readable after the run ends; a 33rd identity evicts the oldest finished
  and not the in-flight one; `?wait=true` still returns the report; phases arrive in order.

**Verify:** `dotnet test --filter "FullyQualifiedName~Integrations"`, and confirm the conformance
suite and the two runner-driven suites are untouched.

## P2 The apiApp proxies it, and MCP gets a decision (FR-8, gates)

- `IntegrationsController`: proxy the two GETs. The POST needs no change beyond passing `wait`
  through; the 120 s client timeout is now correct for every route and is deliberately **not**
  raised (spec FR-8).
- **The MCP decision, by hand.** `/integrations` is deferred by prefix, so the new routes would
  auto-defer under a stale reason. Read the deferral, decide, and write the reason: an agent that
  starts a run and cannot observe it is worse off than one that cannot start it, so the likely answer
  is to bridge the two GETs. Either way the reason must name the run routes explicitly.
- Regenerate the OpenAPI snapshot (`scripts/update-openapi-snapshot.ps1`) and review the diff.
- Tests: `McpRestCoverageTest` and `McpContractTest` pass with the decision recorded, not with the
  prefix silently absorbing it.

## P3 Studio shows it (FR-7)

- `src/api/types.ts` + `endpoints.ts`: the run DTO and two client calls, registered in
  `ENDPOINT_CALLS`.
- `src/screens/IntegrationsScreen.tsx`: on submit, keep the instance id and poll
  `GET /integrations/run/{instanceId}` every 2 s while the run is in flight. Render the phase list
  with per-phase state and counts, the elapsed time, and the report when it ends. The submit button
  stops meaning "hold this connection" and starts meaning "start it".
- Reload survival: the polled identity lives in the per-instance store, so reopening the screen
  re-attaches to a run in flight rather than losing it.
- Tests: the panel renders phases from a stubbed run; a finished run renders the report; a reload
  re-attaches; a 409 still surfaces as a rejection rather than a phase list.

## P4 Docs and screenshot

- `integrations.md`: the `curl` example returns 202 and is followed by a poll. Say plainly that the
  old synchronous shape is `?wait=true` and is for small sources, because the proxy timeout applies.
- `troubleshooting.md`: the case this feature exists for - "the run vanished / I got a timeout but
  the graph kept changing" - with the explanation and the poll.
- Recapture `screen-integrations.png`.
- README key-features line only if the run panel is user-facing enough to earn one; judge at the
  time rather than by default.

## P5 Gate and merge

Full `dotnet test`, Studio suite, docs build. Council gate on the branch, fixes on the branch, then
`git merge --no-ff` and move this directory to `features/done/`.

Browser probe: not implicated, no engine file is touched. If that changes, it becomes mandatory.

## Run ledger

| Phase | State | Notes |
| --- | --- | --- |
| P1 | done | tracker + sink + 7 phases + async route with ?wait=true escape hatch; 17 tests, mutation-checked on the deferred-slot crux |
| P2 | done | proxy carries ?wait (it was dropping it) + two GET proxies; MCP deferral decided by hand and tied to the job route; OpenAPI snapshot regenerated, additions only |
| P3 | done | RunPanel with all 7 phases + counts + elapsed; persisted watch so a reload re-attaches; untracked-identity notice; 7 tests, mutation-checked |
| P4 | done | integrations.md (202 + poll + wait note + the narrowed "keeps nothing"), troubleshooting.md ("a run vanished"), links valid. Screenshot NOT recaptured: see note |
| P5 | done | council returned merge-after-fixes with 3 blockers; all fixed on the branch and each mutation-checked |

## Notes from implementation

**The screenshot was deliberately not recaptured.** The Integrations screen's at-rest appearance is
unchanged: the run panel is conditional on watching a run, and the capture spec submits none, so a
recapture produced byte-different but visually identical output. It was reverted rather than
committed as diff noise.

That leaves the run panel **unillustrated** on a docs page that now describes it, which is a real gap
rather than a satisfied rule. Doing it properly is a SECOND capture, not a recapture: it needs a
seeded `integrationWatch` in local storage (key `f8.workspace.local`), a stubbed
`**/integrations/run/**`, and a viewport or scroll that puts the panel in frame, because it renders
below the form and the form already fills the viewport. Worth doing when that page next gets
attention.

**No README key-features line was added.** Integrations already has one, and "you can watch a run"
is not a feature beside it - it is that feature finally working. Judged rather than added by default,
as the plan asked.

**The proxy timeout was left at 120 s**, per spec FR-8. With the job route answering in
milliseconds it is now correct for every route it covers. The one place it still bites is
`?wait=true`, and that is documented rather than tuned.

## Council gate outcome

Four lenses over the diff, every non-nit finding adversarially verified: **23 survived, 5 refuted**.
Verdict **merge-after-fixes** with three blockers, all of which presented a FALSE outcome to the
operator with nothing on screen to contradict it. All three are fixed on the branch, each
mutation-checked.

**Blocker 1 - a run that ends before its first phase lost its report.** The route's rule was a
dichotomy: either the run enters a phase (202) or it throws (400/409). It is not exhaustive. The
credential-unusable class *returns a report* before `EnterPhase`, so the route answered 202 with a
progress URL, `Finish` no-opped for want of a slot, and the poll 404'd forever - while on `main` the
same request was a 200 carrying `errorKind: credential`. A regression, and it falsified the sentence
this branch publishes in `integrations.md` and in the controller remark. The rule is now three-way and
says so in FR-1.

**Blocker 2 - the persisted watch was thrown away on rehydrate.** `partialize` wrote
`integrationWatch` to storage and the persist `merge` nulled it on the way back in, so a reload lost
the run entirely: FR-7, acceptance 4, the field's own doc comment, the in-panel copy and two docs
sentences were all false. The test named for the behaviour seeded the live in-memory store, so it
could never have caught it - the replacement goes through storage. Fixed together with the 404
gating, because a rehydrated stale identity legitimately 404s and the notice used to assert
"not tracked" for any poll failure, including a 503.

**Blocker 3 - a second run under one identity replayed the first.** The query key was the identity
alone, so react-query served the finished previous run from cache and never refetched (its `running`
was false, so the interval was off), presenting the old run's report as the new run's outcome. The
expected run id is now part of the key.

**Also fixed from the should-fix list:** the run is now dispatched with `Task.Run` (without it FR-1's
"background task" was untrue and a file provider parsed its whole extract on the request thread); the
phase a run ENDS in is recorded rather than left looking like it never ran, with `stoppedInPhase` for
a failure; `write-elements` is entered BEFORE its writes instead of after them; a `?wait=true` run is
tracked too, so "what happened last" cannot be an older run; the embed request rides on the run
instead of in component state, which used to make the tile claim "not requested" after any remount;
and the job route's remarks no longer re-publish two stale body-size numbers into the snapshot.

## Carried forward, deliberately

1. **Only `embed-summaries` advances.** The other phases publish a total that never moves.
   Spec section 2 was CORRECTED to say so rather than left claiming four working counters. Making
   them true means threading the sink into the target's element and edge batch loops and into
   reconciliation.
2. **The phase list is pinned on both sides but not across them.**
   `IntegrationsRunTrackerTest.ThePhaseListIsExactlyTheSevenNamedPhases_InRunOrder` and the matching
   vitest each pin their own copy, with a comment pointing at the other. A rename still needs a
   two-file edit; nothing mechanically enforces it, because no shared artifact carries the names.
3. **`ARunInFlight_ReportsThePhaseItIsIn` is platform-tolerant, not strong.** A refused loopback
   connect costs ~2 s on Windows and ~0.3 ms in a Linux container, so on CI it degenerates to
   asserting that a finished run has a report. The deterministic in-flight coverage is in
   `IntegrationsRunTrackerTest`.
4. **`GET /integration/run` (the list) has no Studio client.** A forgotten identity therefore cannot
   be enumerated from the UI, which is why every attach failure degrades to "no surface at all". That
   route is where a real recovery path would go.
5. **`RunTracker.Slot.Task` is written and never read.** Holding the reference is its whole documented
   purpose - noted so a later reader does not delete it as dead.
6. **The run panel is still unillustrated.** The reload fix makes the seeded-`integrationWatch`
   capture possible again, so the screenshot should be taken on the next pass over that page.
7. **The two GET routes are MCP deferrals with a reason**, tied to the job route: they are bridged in
   the same change that bridges it, never separately.
