# Review findings, 2026-08-11 - everything that reached main in the last two days

> **Status:** Open - pending FIXES only. Five independent principal reviews (browser architecture,
> integrations architecture, engine/durability, cross-feature gate compliance, and one verification
> run) over the three merges and the audit commits that landed on main between 2026-08-09 and
> 2026-08-11 (integrations ae7f094, inline-transaction-execution 07e8a58, trim-safety 8a7e727, and
> the direct audit commits f81d86e..0764b38). Every finding below was verified against the tree at
> f9734b7 with file:line evidence. Fixes follow the branch workflow; tick a box when the fix is on
> main with its test.

## Verification snapshot (all gates, main @ f9734b7)

| Gate | Result |
| --- | --- |
| Solution build | 0 errors (28 IL2026 warnings in the apiApp, deliberate and documented) |
| Full test suite | 1801 passed, 0 failed, 30 skipped (one flake, F9 below, passed on re-run) |
| Integrations subset | 291 passed, 0 failed |
| Trimmed browser probe (TrimMode=full, no root) | 13/13 PASS, 0 IL warnings |
| Docs site | builds, all internal links valid |

## Status, 2026-08-12 (updated after the follow-up session)

Every DEFECT in this report (F1 to F10) is fixed on main with tests, across three merges:
`35820c6` (integrations hardening), `81ce25a` (engine integrity), `a7a9c11` (browser teardown plus
the audit's missing docs). Full suite 1810 passed, 0 failed; the trimmed browser probe still passes
13/13 with zero IL warnings; docs build link-clean.

Two findings changed shape while being fixed, and both are worth knowing:

- The self-heal in F2 had to be scoped to STRONG claims. The first version healed any claim whose
  index entry the lookup did not name, but the lookup batch only asks about strong keys, so for a
  weak key "not named" is unknown rather than false - and healing on unknown re-asserted every weak
  claim on every run. The conformance suite caught it as an idempotence failure, which is the suite
  doing exactly its job.
- The F3 test had to assert the wire TEXT, not just the value. A DateTimeOffset rendered
  "08/09/2026 10:00:00 +02:00" parses back to the same instant, so a value-only round-trip passes
  while the defect is live. Mutation-checked: the test fails without the fix.

Still open from this report (all recorded, none a defect in shipped behaviour):

- [x] DONE (208969b): the Studio durability notice (silent while healthy, loud in the three states
  worth interrupting for, and silent rather than green when an older server does not report the
  block) and the batch hydration. The batch swap was NOT the straight substitution it looked like:
  `POST /graphelements/get` omits adjacency by design, so a vertex it returns is complete while an
  EDGE lacks its endpoints and the canvas cannot draw it - edges are still read singly, and the
  client type is now its own `GraphElementProjectionREST` so that distinction cannot be overlooked
  (declaring it `VertexREST | EdgeREST` was a lie TypeScript caught).
- [x] DONE (208969b): the Integrations screenshot, with a studio.md section. Its capture spec stubs
  the descriptor list - the one capture here that does - because the runtime is a separate
  deployable whose port is never published. Corrected on 2026-08-12: the descriptors it served were
  invented rather than copied (it gave UniFi a site filter the runtime deliberately refuses), so the
  published image showed a UI the product does not have. The fixture now replays a committed
  snapshot of the real descriptors, guarded by a drift test, and the image was recaptured. See
  M1 in [the 2026-08-12 report](../review-findings-2026-08-12/report.md).
- [x] DONE (fcb89a6): the checkpoint fan-out. Save queued pooled work and blocked the calling
  thread on it; Load used `Parallel.For`. Both now pick their arm from the host's capability, so a
  save and load COMPLETE on a single-threaded host - verified writing into and reading out of the
  Emscripten VFS. Browser persistence stops being prevented by a deadlock; getting the bytes out of
  the VFS is the host's job. The capability question also got one home
  (`HostCapabilities.SupportsBackgroundWork`), replacing the second probe copy added the night
  before, and three claims that "a browser cannot persist" were corrected rather than left to rot.
- Still open: the conformance-suite doc line about encoded credentials, and the `observability.mdx`
  half of the docs-site coverage below (its `GET /status` field list still omits the durability
  block).
- The integrations polish recorded under "Composition and docs debt" below is done. The
  stale-strong-claim question has its decision on
  paper - deliberately NOT pruning claims a complete snapshot stopped asserting, with a revisit
  trigger, in the integrations spec section 11 - and the code behaviour is unchanged by choice.
- [x] DONE (2026-08-12): [features/done/host-plugin-registration/](../../done/host-plugin-registration/),
  the browser unlock. A host registers its plugin types, so a browser can create indexes and run
  vector search - verified by the committed trimmed wasm probe (`tools/browser-probe`), not asserted.

## Defects, ranked (fix in this order)

### F1 - HIGH, integrations: element id 0 is real, but the snapshot applier uses 0 as its "no element" sentinel

- [x] FIXED on main (35820c6), with tests

The engine assigns the first element of a fresh graph id 0 (`Fallen8.cs` `_currentId = 0`;
`Fallen8.Storage.cs:216,223`). `fallen-8-integrations/Run/SnapshotApplier.cs` zero-initializes
`elementIdByEntity` (`:144`) and treats 0 as unset: `:413` drops any relation whose endpoint id is 0
("Relation has an endpoint with no element" - the comment at `:415` claims this is unreachable; it
is the first created element), and `:466-469` silently skips that entity's summary embedding.
Blast radius: UniFi emits the site FIRST (`UnifiNetworkProvider.cs:202`), so on a fresh namespace
the site gets id 0 and every device's site edge is dropped, on the first run and permanently (the
match path re-assigns 0 forever). Fronius `loggedBy` has the same exposure.

Why no test sees it: `InMemoryGraphTarget.cs:53` starts ids at 1 while the engine starts at 0 -
exactly the fidelity drift the shared contract suite exists to prevent, but no contract test pins
the first id.

Fix: sentinel -1 (or nullable) in the applier, AND a `IntegrationsGraphTargetContractTest` case
asserting the first id both targets hand out is usable end to end (create one element, wire one
relation to it).

### F2 - HIGH, integrations: a run aborted between "create" and "index" leaves claimed-but-unindexed elements; nothing detects or heals it, and the next run silently duplicates

- [x] FIXED on main (35820c6), with tests

Write order is create, properties, edges, THEN index claims (`SnapshotApplier.cs:243-284`). The job
endpoint binds the request-abort token into the whole run (`IntegrationEndpoints.cs:78-88`,
`JobRunner.cs:215-223`), and the apiApp proxy default timeout is 120 s
(`Fallen8IntegrationsOptions.cs:61`) - a UniFi console with sequential per-device GETs (60 s cap
each) plausibly exceeds it, so the proxy hangs up and Kestrel cancels the run BETWEEN graph writes.
The compose file grants `stop_grace_period: 120s` precisely to avoid killing a run between writes;
the front door does exactly that. Created elements then carry `$identity:` and `$claim:` properties
with no index entries: the next run's resolve misses them and creates duplicates; the originals are
never in `f8i-claims`, so reconciliation never withdraws them. The match path never re-asserts
missing index entries (`SnapshotApplier.cs:206-209`, `:233-240`), so the state never converges.

Fix, in descending value: (i) stop honoring the request-abort token once the apply phase begins
(validation and source read stay cancellable; the graph write is seconds); (ii) index claims
immediately after `CreateVerticesAsync`; (iii) self-heal - re-assert index entries the resolve
lookup shows missing for matched elements (AddOrUpdate is idempotent since f160d7f), and let
reconcile re-add `claimedNow` ids the `f8i-claims` scan did not name. Plus: a spec row for
cancel/disconnect, a test pinning apply-phase decoupling, and a Studio-visible message.
(Cross-reference: the crosscut review found the same hole independently.)

### F3 - HIGH, engine (integrations churn): DateTimeOffset egress is not the inverse of ingress

- [x] FIXED on main (81ce25a), with tests

`FormatPropertyValue` (`fallen-8-core-apiApp/Controllers/Model/AGraphElement.cs:86-101`) has arms
for `Single[]` and `DateTime` then falls to generic `IFormattable`, so `DateTimeOffset` renders as
"08/09/2026 10:00:00 +02:00", not "O". Ingress parses "O" (`AllowedLiteralTypes.cs:156-159`), bulk
egress uses "O" (`JsonlGraphFormat.cs:235`), and the integrations runtime diffs stored-vs-intended
by ordinal TEXT (`WireValues.cs:121-124`, `GraphWrites.cs:85-89`) - so any integration emitting a
DateTimeOffset sees a difference on EVERY run and writes on every poll, defeating W6. The
round-trip test pins a hand-written `.ToString("O")` instead of the real egress function
(`LiteralRoundTripTest.cs:130-138`), which is why it passes.

Fix: a `DateTimeOffset` "O" arm in `FormatPropertyValue`, and make the round-trip test iterate
every `AllowedLiteralTypes` member through the ACTUAL egress function.

### F4 - MEDIUM, engine: `RegExIndex.AddOrUpdate` is not idempotent, and index repair does not refuse it

- [x] FIXED on main (81ce25a), with tests

`RegExIndex.cs:322-331` appends to its posting list unconditionally (never got the f160d7f guard
`ABucketIndex.cs:142-146` has), yet `SupportsPointEqualityLookup => true` (`RegExIndex.cs:544`) so
`IndexRepair.TryRepairFromProperty` accepts it (`IndexRepair.cs:156-171`) and its "idempotent, safe
to run on every start" claim (`IndexRepair.cs:96-98`) is false for fulltext: `POST
/index/backfill/{indexId}` duplicates every posting per run and the inflated buckets persist into
the next checkpoint. Fix: idempotence guard in `RegExIndex.AddOrUpdate` (or
`SupportsPointEqualityLookup => false` if repair should refuse fulltext), plus a fulltext case in
`IndexIntegrityTest`.

### F5 - MEDIUM, engine (integrations reads it): the /status durability block can misreport during a /load-triggered WAL replay

- [x] FIXED on main (81ce25a), with tests

`Fallen8.Persistence.cs:290-292` resets `_recoveryRan/_lastRecoveryTruncated/
_lastRecoveryReplayedEntries` at replay ENTRY; `PUT /load` re-runs replay on the writer thread
while `GET /status` stays answerable, so a poller reads "recovery ran, untruncated, 0 replayed"
mid-replay and the previous recovery's truncation evidence is wiped early. The one consumer is the
integrations delete-deferral gate (`Fallen8RestTarget.cs:370-390`), for which a false "clean" is
the worst answer. Fix: compose the recovery facts into one immutable snapshot object published
through a single volatile reference at replay END (13bb370 already built the DTO shape).

### F6 - MEDIUM, integrations: instance-id case footgun

- [x] FIXED on main (35820c6), with tests

Claims are case-sensitive (`ClaimSchema.cs:97`) while the run gate is case-insensitive
(`RunGate.cs:41`), and the Studio form is free text (`IntegrationsScreen.tsx:455-463`): `Office`
then `office` silently forks the identity and orphans everything the first claimed. Fix: fold
instance ids to lowercase in `IntegrationJob.TryNormalize` (v1, no legacy graphs), and have the
Studio offer known identities (enumerable from the `f8i-claims` index) with an explicit
"new identity" path.

### F7 - LOW, engine: `ABucketIndex.AddOrUpdate` lacks the removed-element guard

- [x] FIXED on main (81ce25a), with tests

`RegExIndex` and `VectorIndex` refuse a `_removed` element under the write lock; `ABucketIndex`
(`:118-179`) does not, so repair racing a removal pins a tombstone into `_idx`/`_reverse`, persists
it into the checkpoint, and the next load logs an error per stale id. One-line guard mirroring the
RegEx one.

### F8 - LOW, engine: inline-mode enqueue-after-Dispose diverges by mode; defensive drain releases waiters non-terminal

- [x] FIXED on main (81ce25a), with tests

`TransactionManager.AddTransaction` (`:841-850`) has no `_disposed` check on the inline branch:
threaded throws loudly, inline silently executes against the disposed engine. And `ExecuteInline`'s
defensive `finally` (`:345-350`) completes never-executed deferred items while their
`TransactionInformation` still reads `Enqueued`. Fixes: a `_disposed` guard for parity, and set
`RolledBack` + `InternalError` before completing in the defensive drain.

### F9 - LOW, durability (observed flake): single-shot rename in `WriteAheadLog.WriteHeader` can spuriously roll a Save back on Windows

- [x] FIXED on main (81ce25a), with tests

`WriteAheadLog.cs:519` is one `File.Move(temp, path, overwrite)` attempt; a transient Windows
destination-handle race (AV/indexer) throws `UnauthorizedAccessException` and rolls the
`SaveTransaction` back (observed once in the verification run; passed 10/10 in isolation). Same
single-shot pattern at `PersistencyFactory.cs:506, 586, 644, 743, 953`; `DurableFileIo.cs:130-134`
has a fallback but no bounded retry. Fail-safe semantics held (loud, WAL left unreset). Fix: a
small bounded retry around the rename in `DurableFileIo`/`WriteHeader`.

### F10 - LOW, integrations: cross-instance delete race

- [x] DECIDED (35820c6): recorded in the integrations spec as best-effort under concurrent runs, self-correcting on the next run

Instance A's withdraw-reread-delete (`SnapshotApplier.cs:570-609`) can delete an element instance B
just reclaimed as an unclaimed orphan (`GraphWrites.cs:478`) between A's re-read and A's remove.
Needs a shared strong key plus tight timing and B's next run recreates, so it self-corrects; at
minimum the spec should say "a run touches only what it may" is best-effort under concurrency.

## Composition and docs debt (from the gate review)

- [ ] **PARTLY DONE (a7a9c11): docs-site coverage for the audit's REST surface** (gate breach: rode
  the integrations branch, so its own Phase 9 ran only after the fact). Landed: the durability block on `GET /status`
  (`save-games.mdx`, "Durability model"), `POST /index/backfill/{indexId}` (`indexes.mdx`,
  "Rebuilding an index you already have"), and `POST /graphelements/get` /
  `PUT /graphelements/properties` / `DELETE /graphelements` (`graph-model.mdx`, the REST CRUD table).
  Still open: `observability.mdx` is untouched - its `GET /status` field list does not mention the
  durability block at all, so that page still presents the degraded state as the
  `fallen8.wal.degraded` OTel gauge alone, and a reader of the observability page never learns the
  status probe answers the same question without any OTel wiring.
- [x] **DONE (208969b): the Studio durability notice** (the audit spec's own sweep names it: the
  **Studio** row of "Impact on existing features" in
  [platform-integrity-audit/spec.md](../platform-integrity-audit/spec.md)): `StatusREST` in
  `fallen-8-web-ui/src/api/types.ts` had no durability field. The machine consumer (integrations
  delete gate) read it; the human could not.
- [x] **DONE (208969b): hydration reads a page in one request** (edges still singly - the route omits their endpoints) (`hydrate.ts:31-36, 61-65`: up to 500
  sequential GETs in 25-wide rounds) two commits after `POST /graphelements/get` landed to replace
  exactly that. One request instead of 20 rounds; directly serves the browser priority.
- [x] **DONE (208969b): Integrations screenshot** captured, was missing from `studio.md` and `docs/src/assets/images/`
  (recorded follow-up with no owner; the standing rule says recreate).
- [ ] **Conformance-suite honesty line**: `NoCredentialLeak` watches exact ordinal substrings, so an
  encoded credential escapes; consistent with the declared threat model, but one doc line should
  say so.
- [x] DONE (35820c6, plus the integrations spec rows that landed with the hardening): `summaryDirty` is a
  `HashSet<Int32>`; the credential-resolution failure logs its outcome before returning (`JobRunner`,
  the `CredentialUnavailableException` arm, which is the one failure that never reaches the
  using-block's `LogOutcome`); and `PlanEdges` now reports the in-snapshot strong-key collision as
  `collidingStrongClaim` naming both entities and the key, instead of swallowing it. The two
  consequences that stay as BEHAVIOUR are now written down in
  [features/done/integrations/spec.md](../../done/integrations/spec.md) section 11 rather than left
  implicit: a changed `label` setting relabels only newly created elements, so a rename leaves mixed
  labels within one instance; and a stale strong claim is never withdrawn, so a strong identifier
  recycled inside one instance makes two physical things resolve to one element. Whether a complete
  snapshot may PRUNE the claims it no longer asserts is deliberately not decided, with a revisit
  trigger (the first recycled identifier reported in a live graph).

## Browser blockers beyond the follow-ups (new findings)

- [x] **DONE (fcb89a6): checkpoint fan-out now completes on a single-threaded host** - independent of the filesystem
  question. `PersistencyFactory.Save` queues pooled work (`Task.Run` at `:364, :384, :400`) then
  blocks on `.Result` (`:414, :423`); Load uses `Parallel.For` (`:1240, :1246`). On wasm the blocked
  main thread never yields, so this deadlocks (contained into a rollback, but unusable). A
  sequential arm (chosen the same way inline transaction mode is) would let a browser host
  checkpoint into the Emscripten VFS and export bytes via JS interop. Without it, "no browser
  persistence" is enforced by deadlock rather than by decision.
- [x] **FIXED on main (a7a9c11): change-feed `Dispose` no longer blocks the only thread** (`ChangeFeedDispatcher.cs:423`): a browser
  host that enables the feed and disposes the engine hangs. Needs a non-blocking teardown arm.

## What was verified sound (do not churn)

Inline transaction mode (seam, reentrancy deferral, Dispose-during-drain parity) and the trim-safety
merge (annotation altitude, per-member suppressions, the gate) were endorsed without reshaping. The
MCP coverage gate is exemplary (all 12 new REST paths bridged or reasoned deferrals); the OpenAPI
snapshot is additions-only; both architecture diagrams match the real topology; deployable isolation
holds (integrations references neither engine nor apiApp); namespace engines resolve to Threaded
under Kestrel by construction; the durability signal is genuinely composed into the integrations
delete gate, fail-closed. The audit commits' SetPropertiesTransaction, batch read, idempotent bucket
AddOrUpdate and loud-missing-index were verified correct; the two-day merges did not stale each
other.

## Follow-up features (specs exist)

- **Host plugin registration** - the browser unlock. Spec:
  [features/done/host-plugin-registration/](../../done/host-plugin-registration/). Closes the "no index in
  the browser" blocker and makes name-based resolution work trimmed.
- **Serializer trim-safe core** - deliberately deferred at reduced scope. The honest arithmetic:
  only 36 of the 114 RequiresUnreferencedCode sites are the codec's, and most are genuine (property
  values legitimately include enums, which resolve types). The split (a `TryProcessDirectObject`
  core containing the primitive/token branches, so the three reader suppressions become structural
  impossibilities) is worth doing WHEN a `SerializationReader` suppression next needs a new or
  amended justification - that is the revisit trigger. Recorded in
  [features/done/trim-safety/spec.md](../../done/trim-safety/spec.md).
