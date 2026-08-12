# Review findings, 2026-08-12

Status: Closed 2026-08-12 - every reviewed finding is fixed and all three follow-ups under
"Still open after this batch" are landed, the last being the conformance-suite doc line
(fac91f5). Scope: the 16 unpushed commits `origin/main..main`
(5b384ac..95b421a): integrations hardening, engine integrity fixes, browser teardown +
checkpoint fan-out, Studio durability + batch hydration, trim follow-up, canvas aria role,
and the docs/bookkeeping that rode along. Method: six dimension reviewers over the full
diff and the files at HEAD, every finding adversarially verified against the code before
it entered this report; the gate lead re-verified the blocker and the screenshot major
by hand. One claim was refuted in verification (a supposed MIT-header corruption in
`HostCapabilities.cs`; the header is fine) and does not appear below.

## Verdict

**The gate did not pass for push as reviewed.** One blocker, one hard-rule violation, and
three majors first. Everything else is recorded here so nothing needs re-finding.

**Resolved on 2026-08-12** on `feature/review-fixes-2026-08-12`. Every item below is ticked
or carries a recorded reason. Two rounds were needed: the fixes went in, then an
adversarial pass over THE FIXES raised 16 further problems (a false log statement, a
comment asserting a measurement nobody made, a thread-unsafe test logger, a
"this is THE home" label installed on one of three competing homes), which round two
closed. Those are listed under "Second round" so the second pass is as auditable as the
first.

Gates after the fixes: full .NET suite **1831 passed** / 0 failed / 30 skipped (from 1810,
so +21 tests); Studio **823/823** with `tsc -b` clean (from 807); a no-incremental rebuild
at **0 errors and 28 warnings, all pre-existing IL2026** in the apiApp where
`WarningsNotAsErrors` puts them deliberately, and **zero** from the engine, the
integrations runtime or the test project; docs build with link check clean (36 pages);
**zero em or en dashes in any line this branch adds** (byte-exact UTF-8 sweep, the
remaining counts in four files are pre-existing and byte-identical to `main`); no BOM or
encoding drift in any changed file (checked in both directions against `main`); OpenAPI
snapshot correctly untouched.

Two verifications were done by hand rather than by test, because no test could give them:

- **B1 against a real server.** The old double-stringified body answers
  `HTTP 400 - "The JSON value could not be converted to
  System.Collections.Generic.List`1[System.Int32]"`; the fixed body answers `HTTP 200`
  with the batch payload. That is the defect and its fix observed against the actual
  ASP.NET Core model binder, which is the one thing the mocked suite structurally cannot
  check.
- **M1 by looking at the image.** The recaptured screenshot shows "CSV device list",
  "UniFi Network" with exactly its two real settings and no site filter, and "Fronius
  Solar API (local)" with the real base-URL guidance instead of the invented
  `https://192.168.1.1`. The finding existed because nobody had looked.

## Blocker

- [x] **B1. The batch read never runs: `getGraphElements` double-stringifies the body.**
  `fallen-8-web-ui/src/api/endpoints.ts:288` passes `body: JSON.stringify(ids)` into
  `apiRequest`, which itself does `init.body = JSON.stringify(options.body)`
  (`client.ts:211`), so the wire body is the JSON *string* `"[3,4]"`, not the array.
  ASP.NET Core's `[FromBody] List<Int32>` binder answers 400, and `hydrate.ts:78`
  swallows it with `.catch(() => null)` into the per-element fallback - so the commit's
  headline behaviour ("hydration reads a page in one request") never happens against a
  real server, and every hydration pays one guaranteed-failing POST first. Invisible to
  the whole suite because `hydrate-batch.test.ts` mocks `getGraphElements` and
  `api-contract.test.ts` asserts route+method only. Every other POST passes the raw
  object (`scanProperty`, `scanProperties`). Fix: `body: ids`, plus a test that pins the
  WIRE body shape (assert the fetch stub received an array, not a string).

## Hard-rule violation

- [x] **H1. Em dashes in added lines.** `docs/src/content/docs/library.mdx`: four added
  lines carry six em dashes. The standing rule is plain hyphens in every added line.
  Replace them; the byte-exact sweep found no other file affected.

## Major

- [x] **M1. The committed Integrations screenshot shows a UI the product refuses to
  have.** `fallen-8-web-ui/e2e/screenshot-integrations.spec.ts:52` claims its
  `SHIPPED_DESCRIPTORS` are "copied from their own descriptor definitions"; none of the
  three matches the shipped provider. Worst: the fixture gives UniFi a `site` setting
  and a `trustSelfSigned` toggle, while `UnifiNetworkProvider.cs:101-107` ships exactly
  two settings with the explicit design comment that NO setting narrows the run - a site
  filter is the control the product rejects because narrowing turns completeness-licensed
  withdrawal into unrecoverable deletion. The CSV provider's id, name and settings and
  Fronius's name/settings are also wrong, `requiresCredential` is invented, and
  `canObserveCompleteState`/`readOnly` (required by the UI type) are missing.
  `docs/src/assets/images/screen-integrations.png` was captured from this fiction and is
  embedded in the published Studio page. Fix: rebuild the fixture from the three real
  descriptors, recapture the screenshot, and leave a pointer at each descriptor that the
  fixture copies it (a drift gate - see N12 - is the durable answer).

- [x] **M2. The durability signal's last hop is unpinned.** Deleting
  `<DurabilityNotice durability={data.durability} />` from
  `fallen-8-web-ui/src/screens/DashboardScreen.tsx:113` keeps the whole suite green -
  reintroducing exactly the failure the feature names ("nothing showed it to the person
  watching the dashboard"). The component test binds the component only. Fix: a
  dashboard-level test that renders the screen with a degraded `/status` payload and
  finds the notice.

- [x] **M3. The reconcile-half of the interrupted-run heal has no test.** Deleting the
  `ReconcileAsync` block that re-asserts claimed-now ids missing from the claims-index
  scan (`fallen-8-integrations/Run/SnapshotApplier.cs:645-667`) passes every test,
  silently re-shipping the permanent-orphan state the commit message says it heals. The
  `CollidingStrongClaim` diagnostic (lines 423-434) is likewise shipped untested. Fix:
  one test constructing the missing-claims-index-entry state, one constructing two
  entities asserting the same strong claim.

## Minor

Engine:

- [x] **m1.** `PersistencyFactory.cs:1273` - the inline load arms of
  `ForEachIndex`/`ForEach` throw raw exceptions, bypassing the `AggregateException`
  normalization that converts bunch-load failures into the documented "The savegame is
  corrupt" `InvalidDataException`; a single-threaded host gets a different contract for
  the same corrupt file.
- [x] **m2.** `Fallen8.Persistence.cs:400` - an exception escaping the replay loop
  itself (I/O fault mid-`ReadEntries`, or `Trim`/`TabulaRasa` markers throwing) reaches
  the `finally` with `truncated:false`, so `/status` reports a clean, complete recovery
  for a replay that stopped mid-log.
- [x] **m3.** `DurableFileIo.cs:173` - the retry filter admits every `IOException`,
  which includes `FileNotFoundException`/`DirectoryNotFoundException`/
  `PathTooLongException`, so the documented "a missing temp file or a bad path is not
  retried" is false; those burn ~75 ms of futile retries before the real error surfaces.
- [x] **m4.** `PersistencyFactory.cs:511` (and 591, 649, 748, 958) - the checkpoint's
  own five temp-to-final publishes still use raw `File.Move`, exposed to the same
  scanner/AV rename race the retry was built for; one refused rename still rolls back an
  otherwise-complete save.
- [x] **m5.** `Index/SingleValueIndex.cs:119` - repair-eligible
  (`SupportsPointEqualityLookup => true`, passes `IndexRepair.TryRepairFromProperty`'s
  gate) but lacks the removed-element guard added to `ABucketIndex`; the tombstone-pinning
  race fixed for the bucket family is still live here, and the new `ABucketIndex.cs:142`
  comment ("this index was the one that did not enforce it") is factually wrong.

Integrations:

- [x] **m6.** `SnapshotApplier.cs:296` - a crash between a create call and the flush
  that lands that element's first strong index entry leaves an element the resolve can
  never find: the next run duplicates it permanently and reconciliation can never
  withdraw the original. The `ClaimReindexed` doc overstates the heal; either shrink the
  claim or order the first flush before the create acknowledgment.
- [x] **m7.** `JobRunner.cs:238` - reverting `CancellationToken.None` back to the
  caller's token (the half-applied-snapshot defect this fixes) fails no test; the new
  credential-failure `LogOutcome` at line 149 is also unasserted.
- [x] **m8.** `IntegrationJob.cs:180` - removing `.ToLowerInvariant()` keeps the suite
  green although the comment calls it "the one normalisation that protects data"; add a
  mixed-case-retype test.

Studio:

- [x] **m9.** `hydrate.ts:107` - progress now counts successes instead of attempts, so
  any not-found id leaves "hydrating N/M" permanently short of M.
- [x] **m10.** `hydrate-batch.test.ts` - no test passes an `AbortSignal` (the blanket
  `.catch` converts an abort into up to a full batch of post-abort fallback requests),
  none exercises a rejecting single-edge re-read, none the `ids.length === cap` boundary.

Tests:

- [x] **m11.** `DurableFileIo.PublishWithRetry` has zero tests despite being internal
  and deterministically testable; reverting the WAL call sites to bare `File.Move` fails
  nothing.
- [x] **m12.** `LiteralRoundTripTest.cs:145` - `EveryAllowedLiteral...` covers 10 of the
  18 allow-listed types (missing `byte`, `sbyte`, `short`, `ushort`, `uint`, `ulong`,
  `char`, `TimeSpan`); the name over-claims and `TimeSpan` - the nearest structural
  relative of the DateTimeOffset defect - is unpinned. Enumerate
  `AllowedLiteralTypes` so an added 19th type fails the test by construction.
- [x] **m13.** `IntegrationsWritePathTest.cs:653` - the ordering test pins
  index-before-edges only; its fixture never triggers a property write, so the "before
  the property writes" half of its own name is unasserted.

Docs and bookkeeping:

- [x] **m14.** `features/open/platform-integrity-audit/spec.md:9` - the status line
  says the docs-site pages "never happened"; commit a7a9c11 in this very push added them.
- [x] **m15.** `features/done/review-findings-2026-08-11/report.md:221` (in `features/open/` at review time) - the "PARTLY
  DONE" item's still-open list predates cc3b5ca and contradicts itself about the
  PlanEdges collision diagnostic.
- [x] **m16.** Same report, line 204 - the docs-debt tick's scope includes
  `observability.mdx:61` (degraded state described as an OTel gauge only), which is
  untouched at HEAD; the tick overstates.
- [x] **m17.** `docs/src/content/docs/studio.md` - the Dashboard section says nothing
  about the new durability notice; the UI-change-updates-docs rule requires it.
- [x] **m18.** `AGraphElement.cs:97` - the eight-line DateTimeOffset comment re-tells
  the full churn story that `LiteralRoundTripTest.cs` also narrates; one home, one-line
  pointer at the other.

## Notes (recorded; fix or consciously accept)

- [x] **N1.** `SnapshotApplier.cs:500` - `WireEdgesAsync` still uses `0` as its
  "no already-wired edge" sentinel ten lines above the `NoElement` fix; unreachable
  today (verified), but the next refactor inherits a trap.
- [x] **N2.** `HostCapabilities.cs:71` - the probe catches only
  `PlatformNotSupportedException`; any other throw at first touch poisons the type with
  `TypeInitializationException` on paths (dispose, checkpoint) that must not throw.
- [x] **N3.** `Algorithms/Traversal/OutEdgeSweep.cs:109` - the last ungated
  `Parallel.ForEach` in the engine, by this change set's own premise a single-threaded
  host cannot complete it. **Check whether it genuinely deadlocks on WASM** (the calling
  thread participates in `Parallel.ForEach`, so it may complete degenerately); if it
  does deadlock, this is a browser blocker hiding in a note.
- [x] **N4.** `TransactionManager.cs:856` - the inline refuse-after-Dispose guard reads
  non-volatile `_disposed` outside any lock; on a threaded host running explicit Inline
  mode the race is narrowed, not closed.
- [x] **N5.** `IPluginCompiler.cs:56` - `out Type artifact` carries no
  `DynamicallyAccessedMembers`, so a host-supplied compiler returning a statically-known
  type gets zero trim warnings at either end; the requirement lives nowhere.
- [x] **N6.** `SnapshotApplier.cs:238` - a weak identity-index entry lost while the
  property survives now drifts unrepaired until an index rebuild (deliberate scoping;
  record the acceptance).
- [x] **N7.** `JobRunner.cs:243` - a cancelled run's `finally` logs the success-shaped
  "finished in 0 ms: 0 created" line because `Complete` never ran.
- [x] **N8.** `hydrate.ts:97` - mixed vertex/edge pages now render edges grouped last
  instead of scan order (behaviour change; probably fine, decide once).
- [x] **N9.** `DashboardScreen.tsx:75` - an empty graph shows `FirstRunShow` before the
  durability notice, so a truncated recovery that lost everything greets the user with
  "get started" instead of the warning that state exists to surface.
- [x] **N10.** The single-threaded arms guarded by `SupportsBackgroundWork` are
  unexecutable by any test on a threaded host (structural; the browser probe run is the
  compensating control - keep it in the release ritual).
- [x] **N11.** `TransactionManager.cs:351` - the deferred-drain terminal-state fix
  (`RolledBack` + `InternalError`) is reachable only via an unforeseen fault and no test
  reaches it.
- [x] **N12.** The screenshot fixture hand-copies descriptors with no drift gate; a
  reworded provider label makes the docs image silently stale (pairs with M1).
- [x] **N13.** `docs/src/content/docs/indexes.mdx:69` - the backfill row says "count of
  entries written" where the endpoint returns an outcome object, and the
  `"replace": true` exact-rebuild mode is undocumented.
- [x] **N14.** `features/open/host-plugin-registration/spec.md:44` - bare file:line
  anchors drifted within this very push; anchor to a commit or a symbol.
- [x] **N15.** `features/done/review-findings-2026-08-11/` (in `features/open/` at review time) - every F-item and follow-up
  is ticked; once the conformance-suite doc line lands, move the directory to
  `features/done/` per "open holds pending work only".

## Verified sound (so silence is not ambiguity)

The council checked and confirmed, among 57 clean areas: inline save/load arms are
exactly the previous threaded code on threaded hosts (no server regression);
`RecoveryOutcome` publication is torn-read-proof; `PublishWithRetry` is bounded and
cannot publish a stale file; the `NoElement = -1` sweep covers both consumers and the
id-0 tests are mutation-sensitive; index-claims-first ordering converges from every
other crash window and steady-state runs issue zero mutations; the apply-phase
`CancellationToken.None` is bounded by the 120 s per-call HTTP timeout (not a wedge);
DateTimeOffset egress is now genuinely the inverse of ingress end to end;
`GraphElementProjectionREST` matches the server serialization; the canvas `role="img"`
sites are the only two label-on-div sites and the tests query by role; the IL2067
suppression is method-scoped with sound reasoning; library.mdx's browser claims,
save-games.mdx's durability block, and the rest-api.mdx route inventory are true at
HEAD; all ten F-ticks of the 2026-08-11 report are real fixes with tests.

## Outcomes that differ from what the finding prescribed

A tick above means the finding is closed, not that it was closed the way the finding
guessed. These are the ones where the answer turned out to be different, and why.

- **N3 was not a note, it was a browser blocker.** The finding asked whether
  `OutEdgeSweep`'s ungated `Parallel.ForEach` genuinely cannot complete on a
  single-threaded host, or degenerately completes. It is now gated through
  `HostCapabilities` like the checkpoint fan-out. The *reason* recorded at the site had to
  be rewritten in the second round: the first attempt asserted a mechanism that is false
  for the default scheduler (the root replica runs inline on the calling thread) and cited
  a measurement no harness in this repo can produce. The gate is right; the comment now
  points at the one home for the capability question instead of inventing a third story.
- **N5 could not be landed by one owner.** The `DynamicallyAccessedMembers` annotation on
  `IPluginCompiler.TryCompile`'s `out Type` and on the apiApp implementation must land
  together - the interface alone fails the build with IL2092. With the annotation flowing,
  the `IL2067` suppression on `BuildRehydratedPluginEntry` became redundant and was
  deleted, verified by a no-incremental rebuild in which no IL2067 reappears. The trim
  requirement now lives in the type system rather than behind a suppression.
- **m6 (the create-to-index crash window) was not closed, and the claim shrank instead.**
  An `IndexEntry` needs the element id, which only exists after the create call answers,
  so the first strong-claim index write is necessarily a second round trip and the window
  cannot be closed from the applier. The documentation no longer overstates the heal: the
  residual window is stated where the heal is described. Closing it for real needs the
  write side to accept a create-with-claims in one call, which is a feature, not a fix.
- **N6 (weak-claim drift) is a recorded acceptance, not a fix.** Extending the heal to
  weak claims would re-fire every run, because the lookup batch only asks about strong
  keys, so "not named" is unknown rather than false - that was a real idempotence bug
  fixed earlier and must not come back. The acceptance and its reason now sit at the code.
- **P1's home moved to the interface.** The removed-element rule had three competing
  narrations pointing at each other in a circle. The honest home is
  `IIndex.AddOrUpdate` - the contract every implementation shares - not whichever concrete
  index narrated it best, so the rule and its engine-level reason live there and
  `ABucketIndex`, `RegExIndex`, `VectorIndex` and `SingleValueIndex` each carry one line.
  A fourth narration in `IndexIntegrityTest` was reduced too.
- **P13 was a latent duplicate-key bug, now fixed.** The order fix collected results into
  a map keyed by id, so a repeated id would return the same element twice. Hydration now
  dedupes its input, which also stops it asking the server for the same id twice; the cap
  counts distinct elements. Two tests, both mutation-checked.

## Second round: problems found in the fixes themselves

Verifying the fixes was worth as much as the original review. Sixteen problems, all
closed or recorded:

- **The worst was a confident falsehood.** A new log line asserted "the caller cancelled
  before the apply phase, so nothing was written and nothing was withdrawn" on a path
  where that is untrue: `EmbedSummariesAsync` caught only `HttpRequestException`, so an
  HttpClient timeout on the embedding write escaped as a `TaskCanceledException` after
  elements had been created, properties written and claims possibly withdrawn. Fixed at
  the seam (both sends now go through one helper that turns a timeout into the domain
  failure it is) AND at the runner (an `applyStarted` flag makes the sentence structurally
  true rather than dependent on target behaviour). Both halves pinned, both
  mutation-checked. A vague line replaced by a false one is worse than the vague line.
- A test logger shared with every engine component incremented a plain `Int32` and
  appended to a plain `List` while a checkpoint's sidecar fan-out logged from pooled
  tasks. Now `Interlocked` and a `ConcurrentQueue`, pinned by a test driving eight threads
  that fails on the old shape with `Expected:<40000>. Actual:<22049>`.
- `RunSaver`'s inline arm let a saver's failure escape at fan-out time, abandoning the
  remaining partitions and leaving their temp files behind - the same
  single-threaded-host-gets-a-different-contract shape that m1 fixed for the load path.
- The widened load-core catch relabelled every failure as "the savegame is corrupt",
  including an `OutOfMemoryException` on a legitimately large bunch. A resource verdict is
  now separated from a data verdict, since "restore a backup" cannot help a machine that
  is merely too small.
- `DurabilityNotice` told operators that rebuilding a dropped index is "a single call on
  the Indexes screen". That screen has no such action. It now names the REST call and says
  so plainly - the in-app text and the published page had drifted apart.
- The `studio.md` durability paragraph said "below the tiles", which is wrong on an empty
  namespace, where the notice deliberately renders ABOVE the first-run walkthrough because
  a truncated recovery is one reason a namespace is unexpectedly empty.
- One verifier note was checked and **rejected**: it asked for long lines to be wrapped in
  the plugin-registration spec, but nine of the eleven are markdown table rows that cannot
  wrap without breaking the table. Only the two genuine prose lines were rewrapped.

## Still open after this batch

- [x] **DONE (2026-08-12): N10's compensating control is now committed and enforced.** This
  report accepted that the single-threaded arms guarded by `SupportsBackgroundWork` cannot
  be executed by any test on a threaded host, on the grounds that "the trimmed browser
  probe run is the compensating control - keep it in the release ritual". That probe was
  not in the repository: it was a throwaway in a past session's scratchpad, so the control
  could not actually be run by anyone, and the WASM-critical arms were executed by nothing.
  It is now `tools/browser-probe`, a trimmed browser-wasm app run headless under node whose
  exit code is its verdict, wired into CI as the `browser` job and recorded in CLAUDE.md's
  quality gates. It is deliberately outside `fallen-8-core.sln` so a plain `dotnet build`
  never needs the wasm workload. Of its seven checks, one is a negative control - index
  creation must FAIL before registration - so the probe cannot pass by accidentally running
  on a threaded host. It earned its keep immediately, catching at publish time what no test
  saw: `CA1416` on `Thread.Start`, and `IL2026` proving four trim annotations had become
  broader than the truth. The `HostCapabilities` test-seam alternative was NOT taken: it
  would mean production code carrying a mutable global for tests, and a real runtime is
  better evidence than a faked flag.
- [x] DONE (fac91f5): the conformance-suite doc line about encoded credentials, carried over from
  the 2026-08-11 report - it landed in the NoCredentialLeak row of the integrations spec,
  section 13, and both report directories moved to `features/done/` (its N15).
- [x] **DONE (2026-08-12): the flake is identified and fixed - and it was neither of the
  suspects.** A CI re-run of commit `2e77793` (attempt 2 of run 31597712633) failed
  `ChangeFeedEngineTest.InboxOverflow_BecomesAResync_ForRingAndSubscribers` where attempt 1
  of the SAME commit had passed - same code, different outcome, which is the definition of
  a flake and the identification the nine green re-runs could not give. The race was in the
  TEST, not the product: `PauseDispatchForTest` holds the gate INSIDE `ProcessDescriptor`,
  but the dispatch loop's `TryRead` needs no gate, so a paused dispatcher can still REMOVE
  the one parked descriptor and block holding it in hand, leaving the 1-slot inbox free
  again (the seam's own doc says it stalls "after it reads a descriptor"). The test
  published two descriptors and assumed the second must be dropped; that was only true when
  the test won a scheduling race against the pool continuation, and on a busy runner it
  lost - nothing was dropped, so the product correctly owed no resync while the test
  demanded one. Fixed by publishing CAPACITY + 2 while paused (at most one descriptor can
  be in the dispatcher's hand and one in the slot, so of three, at least one is refused on
  either side of the race) and reading UP TO the resync instead of asserting positions,
  with the ring-replay pin computed from the resync's actual sequence. Mutation-checked
  (an unrecorded drop times out the bounded read) and ran 20/20 green. The earlier local
  `Failed: 1, Passed: 1879` was in all likelihood this same test, though the `-v q` run
  that produced it kept no name, so that attribution stays a strong inference rather than
  a fact - which is exactly why the trx-logger lesson below stands.
  **Lesson applied going forward:** run the gate suite with
  `--logger "trx;LogFileName=..."` so a flake can always be named after the fact.
