# Review findings, 2026-08-12

Status: Open - pending fixes only. Scope: the 16 unpushed commits `origin/main..main`
(5b384ac..95b421a): integrations hardening, engine integrity fixes, browser teardown +
checkpoint fan-out, Studio durability + batch hydration, trim follow-up, canvas aria role,
and the docs/bookkeeping that rode along. Method: six dimension reviewers over the full
diff and the files at HEAD, every finding adversarially verified against the code before
it entered this report; the gate lead re-verified the blocker and the screenshot major
by hand. One claim was refuted in verification (a supposed MIT-header corruption in
`HostCapabilities.cs`; the header is fine) and does not appear below.

## Verdict

**The gate does not pass for push.** One blocker, one hard-rule violation, and three
majors first. Everything else is recorded here so nothing needs re-finding.

Central gates at HEAD, all green: full .NET suite 1810 passed / 0 failed / 30 skipped;
Studio suite 807/807; docs build with link check clean (36 pages); no em/en dashes in
added lines outside the one file flagged below (byte-exact UTF-8 sweep); OpenAPI snapshot
correctly untouched (the one apiApp change alters a value rendering, not routes or XML
docs); MCP one-way propagation holds (new REST operations bridged or consciously
deferred).

## Blocker

- [ ] **B1. The batch read never runs: `getGraphElements` double-stringifies the body.**
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

- [ ] **H1. Em dashes in added lines.** `docs/src/content/docs/library.mdx`: four added
  lines carry six em dashes. The standing rule is plain hyphens in every added line.
  Replace them; the byte-exact sweep found no other file affected.

## Major

- [ ] **M1. The committed Integrations screenshot shows a UI the product refuses to
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

- [ ] **M2. The durability signal's last hop is unpinned.** Deleting
  `<DurabilityNotice durability={data.durability} />` from
  `fallen-8-web-ui/src/screens/DashboardScreen.tsx:113` keeps the whole suite green -
  reintroducing exactly the failure the feature names ("nothing showed it to the person
  watching the dashboard"). The component test binds the component only. Fix: a
  dashboard-level test that renders the screen with a degraded `/status` payload and
  finds the notice.

- [ ] **M3. The reconcile-half of the interrupted-run heal has no test.** Deleting the
  `ReconcileAsync` block that re-asserts claimed-now ids missing from the claims-index
  scan (`fallen-8-integrations/Run/SnapshotApplier.cs:645-667`) passes every test,
  silently re-shipping the permanent-orphan state the commit message says it heals. The
  `CollidingStrongClaim` diagnostic (lines 423-434) is likewise shipped untested. Fix:
  one test constructing the missing-claims-index-entry state, one constructing two
  entities asserting the same strong claim.

## Minor

Engine:

- [ ] **m1.** `PersistencyFactory.cs:1273` - the inline load arms of
  `ForEachIndex`/`ForEach` throw raw exceptions, bypassing the `AggregateException`
  normalization that converts bunch-load failures into the documented "The savegame is
  corrupt" `InvalidDataException`; a single-threaded host gets a different contract for
  the same corrupt file.
- [ ] **m2.** `Fallen8.Persistence.cs:400` - an exception escaping the replay loop
  itself (I/O fault mid-`ReadEntries`, or `Trim`/`TabulaRasa` markers throwing) reaches
  the `finally` with `truncated:false`, so `/status` reports a clean, complete recovery
  for a replay that stopped mid-log.
- [ ] **m3.** `DurableFileIo.cs:173` - the retry filter admits every `IOException`,
  which includes `FileNotFoundException`/`DirectoryNotFoundException`/
  `PathTooLongException`, so the documented "a missing temp file or a bad path is not
  retried" is false; those burn ~75 ms of futile retries before the real error surfaces.
- [ ] **m4.** `PersistencyFactory.cs:511` (and 591, 649, 748, 958) - the checkpoint's
  own five temp-to-final publishes still use raw `File.Move`, exposed to the same
  scanner/AV rename race the retry was built for; one refused rename still rolls back an
  otherwise-complete save.
- [ ] **m5.** `Index/SingleValueIndex.cs:119` - repair-eligible
  (`SupportsPointEqualityLookup => true`, passes `IndexRepair.TryRepairFromProperty`'s
  gate) but lacks the removed-element guard added to `ABucketIndex`; the tombstone-pinning
  race fixed for the bucket family is still live here, and the new `ABucketIndex.cs:142`
  comment ("this index was the one that did not enforce it") is factually wrong.

Integrations:

- [ ] **m6.** `SnapshotApplier.cs:296` - a crash between a create call and the flush
  that lands that element's first strong index entry leaves an element the resolve can
  never find: the next run duplicates it permanently and reconciliation can never
  withdraw the original. The `ClaimReindexed` doc overstates the heal; either shrink the
  claim or order the first flush before the create acknowledgment.
- [ ] **m7.** `JobRunner.cs:238` - reverting `CancellationToken.None` back to the
  caller's token (the half-applied-snapshot defect this fixes) fails no test; the new
  credential-failure `LogOutcome` at line 149 is also unasserted.
- [ ] **m8.** `IntegrationJob.cs:180` - removing `.ToLowerInvariant()` keeps the suite
  green although the comment calls it "the one normalisation that protects data"; add a
  mixed-case-retype test.

Studio:

- [ ] **m9.** `hydrate.ts:107` - progress now counts successes instead of attempts, so
  any not-found id leaves "hydrating N/M" permanently short of M.
- [ ] **m10.** `hydrate-batch.test.ts` - no test passes an `AbortSignal` (the blanket
  `.catch` converts an abort into up to a full batch of post-abort fallback requests),
  none exercises a rejecting single-edge re-read, none the `ids.length === cap` boundary.

Tests:

- [ ] **m11.** `DurableFileIo.PublishWithRetry` has zero tests despite being internal
  and deterministically testable; reverting the WAL call sites to bare `File.Move` fails
  nothing.
- [ ] **m12.** `LiteralRoundTripTest.cs:145` - `EveryAllowedLiteral...` covers 10 of the
  18 allow-listed types (missing `byte`, `sbyte`, `short`, `ushort`, `uint`, `ulong`,
  `char`, `TimeSpan`); the name over-claims and `TimeSpan` - the nearest structural
  relative of the DateTimeOffset defect - is unpinned. Enumerate
  `AllowedLiteralTypes` so an added 19th type fails the test by construction.
- [ ] **m13.** `IntegrationsWritePathTest.cs:653` - the ordering test pins
  index-before-edges only; its fixture never triggers a property write, so the "before
  the property writes" half of its own name is unasserted.

Docs and bookkeeping:

- [ ] **m14.** `features/open/platform-integrity-audit/spec.md:9` - the status line
  says the docs-site pages "never happened"; commit a7a9c11 in this very push added them.
- [ ] **m15.** `features/open/review-findings-2026-08-11/report.md:221` - the "PARTLY
  DONE" item's still-open list predates cc3b5ca and contradicts itself about the
  PlanEdges collision diagnostic.
- [ ] **m16.** Same report, line 204 - the docs-debt tick's scope includes
  `observability.mdx:61` (degraded state described as an OTel gauge only), which is
  untouched at HEAD; the tick overstates.
- [ ] **m17.** `docs/src/content/docs/studio.md` - the Dashboard section says nothing
  about the new durability notice; the UI-change-updates-docs rule requires it.
- [ ] **m18.** `AGraphElement.cs:97` - the eight-line DateTimeOffset comment re-tells
  the full churn story that `LiteralRoundTripTest.cs` also narrates; one home, one-line
  pointer at the other.

## Notes (recorded; fix or consciously accept)

- [ ] **N1.** `SnapshotApplier.cs:500` - `WireEdgesAsync` still uses `0` as its
  "no already-wired edge" sentinel ten lines above the `NoElement` fix; unreachable
  today (verified), but the next refactor inherits a trap.
- [ ] **N2.** `HostCapabilities.cs:71` - the probe catches only
  `PlatformNotSupportedException`; any other throw at first touch poisons the type with
  `TypeInitializationException` on paths (dispose, checkpoint) that must not throw.
- [ ] **N3.** `Algorithms/Traversal/OutEdgeSweep.cs:109` - the last ungated
  `Parallel.ForEach` in the engine, by this change set's own premise a single-threaded
  host cannot complete it. **Check whether it genuinely deadlocks on WASM** (the calling
  thread participates in `Parallel.ForEach`, so it may complete degenerately); if it
  does deadlock, this is a browser blocker hiding in a note.
- [ ] **N4.** `TransactionManager.cs:856` - the inline refuse-after-Dispose guard reads
  non-volatile `_disposed` outside any lock; on a threaded host running explicit Inline
  mode the race is narrowed, not closed.
- [ ] **N5.** `IPluginCompiler.cs:56` - `out Type artifact` carries no
  `DynamicallyAccessedMembers`, so a host-supplied compiler returning a statically-known
  type gets zero trim warnings at either end; the requirement lives nowhere.
- [ ] **N6.** `SnapshotApplier.cs:238` - a weak identity-index entry lost while the
  property survives now drifts unrepaired until an index rebuild (deliberate scoping;
  record the acceptance).
- [ ] **N7.** `JobRunner.cs:243` - a cancelled run's `finally` logs the success-shaped
  "finished in 0 ms: 0 created" line because `Complete` never ran.
- [ ] **N8.** `hydrate.ts:97` - mixed vertex/edge pages now render edges grouped last
  instead of scan order (behaviour change; probably fine, decide once).
- [ ] **N9.** `DashboardScreen.tsx:75` - an empty graph shows `FirstRunShow` before the
  durability notice, so a truncated recovery that lost everything greets the user with
  "get started" instead of the warning that state exists to surface.
- [ ] **N10.** The single-threaded arms guarded by `SupportsBackgroundWork` are
  unexecutable by any test on a threaded host (structural; the browser probe run is the
  compensating control - keep it in the release ritual).
- [ ] **N11.** `TransactionManager.cs:351` - the deferred-drain terminal-state fix
  (`RolledBack` + `InternalError`) is reachable only via an unforeseen fault and no test
  reaches it.
- [ ] **N12.** The screenshot fixture hand-copies descriptors with no drift gate; a
  reworded provider label makes the docs image silently stale (pairs with M1).
- [ ] **N13.** `docs/src/content/docs/indexes.mdx:69` - the backfill row says "count of
  entries written" where the endpoint returns an outcome object, and the
  `"replace": true` exact-rebuild mode is undocumented.
- [ ] **N14.** `features/open/host-plugin-registration/spec.md:44` - bare file:line
  anchors drifted within this very push; anchor to a commit or a symbol.
- [ ] **N15.** `features/open/review-findings-2026-08-11/` - every F-item and follow-up
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
