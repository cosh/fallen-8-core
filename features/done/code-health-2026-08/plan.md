# Code health improvement plan, 2026-08

Status: Done - implemented in full on `feature/code-health-2026-08` and merged to `main`
2026-08-26. Every phase below is ticked and carries its as-built note, including the places
the plan or the report turned out to be wrong.
Findings and evidence: [report.md](report.md). Finding ids (M1..M5, S1, C1..C5, D1,
H1..H6) refer to that report.

## Final gate, run on the finished branch

- `dotnet build fallen-8-core.sln --no-incremental`: 0 errors, 27 warnings, all of them the
  pre-existing IL2026 in fallen-8-core-apiApp where `WarningsNotAsErrors` puts them.
- `dotnet test fallen-8-core.sln`: **2148 passed, 0 failed, 32 skipped** (baseline before any
  of this work: 2117 / 0 / 30). Every test name that left the run was accounted for by name
  against the previous run's trx.
- Browser probe (`dotnet publish tools/browser-probe -c Release`, then node): **9 of 9**,
  including the two arms it did not previously execute.
- Studio: `tsc -b` clean, **1162 tests in 95 files**, production and library builds clean.
- Docs site: **40 pages, every internal link valid**.
- OpenAPI snapshot and provider-descriptor snapshot: regenerated where their inputs changed,
  each diff reviewed and exactly as small as intended.
- Hygiene over all 9,467 lines this branch adds: zero em dashes, zero en dashes, zero
  forbidden words.

Ground rules for the whole plan: every fix lands with the test that would have caught it;
no finding is "fixed" by weakening an assertion; the SUSPECTED items are verified at
implementation time before any code changes on their account (the report's line numbers
were read at HEAD on 2026-08-26 and are re-checked when touched).

## Phase 0 - decisions (answered by the owner, 2026-08-26)

- [x] **D1. ARXML `secures` resolution (S1): researched, the code is CORRECT, no change.**
  Three independent sources agree `SECURED-I-PDU.PAYLOAD-REF` targets a PDU-TRIGGERING,
  which is what `ArxmlReader` resolves through: the cantools AUTOSAR 4.2 fixture carries
  `<PAYLOAD-REF DEST="PDU-TRIGGERING">/Cluster/Cluster0/Pch0/message3_triggering</PAYLOAD-REF>`
  (https://github.com/cantools/cantools/blob/master/tests/files/arxml/system-4.2.arxml);
  the EEA COM configuration docs select the payload as Cluster then Channel then PDU,
  i.e. a triggering (https://eeacom-docs.intrepidcs.com/pdus/secured-pdus/); and the
  canmatrix discussion locates the target in the communication-clusters package, where
  triggerings live, not PDUs (https://github.com/ebroecker/canmatrix/issues/382).
  S1 is closed; implementation only adds a one-line note at the collection site citing
  the target type.
- [x] **D2. A client-side timeout on the embedding write DEGRADES to absent**, for the
  embedding write only; every other call keeps timeout-as-failure. (M2's fix applies
  this.)
- [x] **D3. The REST-client seam moves to a small SHARED PROJECT** (references neither
  engine nor apiApp; consumed by fallen-8-mcp and fallen-8-integrations), owning
  send/soft-not-found/timeout-classification. The integrations target adopts the
  bridge's `UrlSafety`-grade namespace validation as part of the move. Note for the
  implementer: the doc comment on the integrations `Fallen8TargetOptions` argues against
  a shared library for the OPTIONS class; the owner's decision here covers the
  behavioral seam - whether the options class joins the shared project or stays copied
  is the implementer's call, with the comment updated either way.
- [x] **D4. `AdjacencyConcurrencyTest`: opt-in gate plus a fast smoke.** The three
  race-hunt methods move behind the same opt-in gate as `LoadPathIntegrityTest`; a fast
  smoke variant stays in the default run. The hunt itself is not weakened.
- [x] **D5. One-home collapses use the TYPE-OWNED homes as proposed** (`RunTracker`,
  `NahilWarmupRetryHandler`, `NamespaceLoader`, `AutosarArxmlProvider`,
  `JobTransportLimit`, `IdentityClaimDto`/`EntityDto`); docs pages keep the user-facing
  conclusion, controller remarks become one-line pointers.
  CORRECTION made at implementation time: this plan originally said feature SPECS become
  pointers too. They do not. CLAUDE.md's one-home gate says in the same breath that
  "specs/plans are historical records and are not rewritten", so a spec that narrates the
  story at length is not a violation to fix - the rule governs the LIVING sites (the owning
  type, controller remarks, the docs-site page, the feature README where one exists). The
  collapses therefore leave every `features/done/*/spec.md` alone, except the two
  contract ENUMERATIONS already extended in phase 1 (the entity-skip list and the
  Fallen8Target key list), which are lists rather than narration.

All implementation phases land on ONE feature branch, `feature/code-health-2026-08`,
gated as usual before merge (owner's decision 2026-08-26).

## Phase 1 - correctness fixes

- [x] M1: carry `written` on every throw path out of `EmbedSummariesAsync`
  (wrap the send, or catch/rethrow with `SummariesWritten`); test: transport failure and
  client timeout mid-chunk both report the landed count.
  As built: the loop is wrapped and the count is attached in exactly ONE place, so the next
  throw site added cannot forget it. `SummariesWritten` became settable for that reason.
- [x] M2: `TimeoutSeconds` on the integrations `Fallen8TargetOptions` (default 330,
  reasoning stated once, pointing at the MCP bridge's identical decision); apply D2's
  outcome to the embedding write's degrade set; test: the configured value reaches the
  client, and the embed path behaves per D2.
  As built: a `GraphTargetTimeoutException` SUBCLASSES `GraphTargetException`, so every
  caller that does not care keeps treating a timeout as the graph failure it always was and
  only the embedding write chooses otherwise. No `appsettings.json` was created for
  fallen-8-integrations: it is deliberately configured by environment only (Program.cs adds
  no file, the Dockerfile publishes none), so the default's single home is the options type.
  The `EmbedBatchSize` doc comment kept 16 but re-derived WHY, since the 120s headroom that
  originally justified it is gone.
- [x] M3: `EnterPhase(WriteEdges)`/`Advance` before `WireEdgesAsync`, `progress` threaded
  in for per-batch advances; `EnterPhase(Validate)` before `_validator.Validate`;
  test: phase-ordering through the real `SnapshotApplier` (progress observed while work
  is pending, not after).
  As built: the applier half is pinned through the real `SnapshotApplier`. The `JobRunner`
  validate half is FIXED but NOT pinned: `SnapshotValidator` is sealed with a non-virtual
  `Validate` whose only observable effect appears after it returns, so pinning it would mean
  adding a seam to production purely for the test. Accepted rather than done.
- [x] M4: fix both templates (`"{kind} {unifi.name}, {unifi.model}, {unifi.state},
  {unifi.ipAddress}"`; `"{kind} {fronius.customName}, {fronius.status}"`); rendered-summary
  test per provider entity kind (as AUTOSAR already has); regenerate the provider
  descriptor snapshot; ~~recapture `screen-integrations.png`~~; fix the stale template copy at
  `features/done/autosar-arxml/spec.md:67` in passing.
  As built: the descriptor snapshot diff is exactly the two template strings. NO screenshot
  recapture was needed and none was done: `screen-integrations.png` renders only the CSV
  provider's form and its template, which this change does not touch (verified by looking at
  the image). The "no literal word beside a hole" rule was homed on
  `ProviderDescriptor.EntitySummaryTemplate` rather than on a provider, because the finding's
  own root cause is that a provider-local comment never reached the other two providers.
- [x] M5: `InstanceHealth` consumes `useStatus()`; one shared poll-interval constant
  replaces the six `15_000` literals; test: one observer set per status key.
- [x] Minors that ride along: move `_files.Create` inside the lease scope
  (`JobRunner.cs`); `Classify` delegates to `IsAuthority` (or a parity test); the
  UniFi/Fronius HTTP-failure translation and the Fronius ipv4-claim paste get the named
  helper UniFi already has.
  As built: `Classify` DELEGATES; no test accompanies it and none is possible, because both
  sites use subtype-aware `is` patterns, so any provider type a test could define that
  `IsAuthority` accepts is necessarily matched by `Classify`'s existing cases too. The drift
  it closes is a future authority type no test can name today. Fronius got no private
  `ClaimAddress` wrapper: both former pastes now call the shared `ClaimIfPresent` helper
  directly, which is the named helper, so a wrapper would only add a name.

## Phase 2 - close the coverage gaps

- [x] C1/C2: browser-probe checks 8 and 9 (sequential sweep with exact count;
  change-feed subscribe + dispose completes). Run the probe locally; it is the only gate
  for these arms.
  As built: the probe now has NINE checks and both arms run for real. Check 1 already proves
  `Thread.Start` throws on this host, which is exactly what makes
  `HostCapabilities.SupportsBackgroundWork` false process-wide, so checks 8 and 9 take the
  sequential arms by construction. CLAUDE.md's browser-host bullet was corrected to say the
  arms are covered by no UNIT test.
- [x] C3: `LiveSettingTest` methods for `KeepAliveSeconds` and `Plugins:MaxCount` through
  a live PATCH, asserting observed behaviour change (mirror the existing ceiling tests).
  As built: `KeepAliveSeconds` is asserted on the SSE wire (a stream opened after the write
  heartbeats, the one already on air does not), because that is the only place a client can
  observe it.
- [x] C4: `Fallen8LiveSettings.ApplyAll` with a throwing `ApplyNow`: failure reported,
  next entry still applies.
- [x] C5: `truncate: false` reaches `GenerateAsync`; `OverLongInputHint` present/absent.
  As built: reused the existing `FakeEmbeddingGenerator` seam rather than adding a fake.
- [x] The named small pins: `Save_AddressingANotLoadedNamespace_Refuses`,
  `UnknownRelationTargetType_SkipsOnlyThatEntity` (and its line in the integrations
  spec's skip list), the "tracks nothing" half of the colon-identity refusal test.
  Correction to the report: the FIRST refusal for a save on a not-loaded namespace is
  pre-action, in `NamespaceValidationFilter`, not the throwing `Namespace.Engine` accessor
  (which is a second guard behind it). The pin asserts the observable contract, so it holds
  either way.

## Phase 3 - test-suite reshape

Zero coverage loss is the constraint; the wins are files, LOC, and honesty of structure.

- [x] Move `ThrowingOnSaveIndex`/`ThrowingOnLoadIndex` to a shared fixture home first
  (three files reference them).
  As built: `ThrowingIndexFixtures.cs`, same namespace so no consumer needed an import
  change. The move also surfaced a sibling hazard recorded for later: `ThrowingOnSaveService`
  is still a private nested double in a file that was renamed, and whoever relocates the
  method using it must carry it along or promote it the same way.
- [x] Execute [test-consolidation-map.md](test-consolidation-map.md): delete the 12
  verified pure duplicates, relocate the 85 mapped methods into their subject files,
  rename the keep-whole files to subject names. Keep every method's prose rationale.
  Honor the map's four warnings, especially: `AuditDefectMcpAlgorithmTest` is the sole
  `PathsTool`/`SubgraphTool` coverage (rename, never thin), and `IndexTest`'s defensive
  `Between` assertions are replaced by the corrective B3 tests moving in.
  As built: 26 event-named files are gone and 18 subject-named files stand in their place.
  ONE of the twelve claimed duplicates was refused with evidence and NOT deleted (see the
  correction in the map): deleting it would have left `PUT /graphelements/properties` with no
  success-path coverage. Two further corrections the fold found: a source and target fixture
  built DIFFERENT graphs, so the eleven moved subgraph tests keep their original graph
  through a small helper rather than having their assertions quietly rewritten to fit a new
  one; and a class doc advertised cancellation coverage ("C7") that no method in the file
  ever tested, which was dropped rather than carried forward as a false claim.
- [x] Trim the duplicated `ApplyNow != null` assertion in `LiveSettingTest.cs:420`, the
  duplicated literal checks in `IntegrationsIdentityTest`'s per-type tests, and the
  line-level verbatim assertion duplicates the map names (keep each test's unique
  assertions).
  As built: each trimmed test was RENAMED to say what it still pins, so no name outlives its
  assertions.
- [x] DataRow the two remaining verified clusters: the eight `Register_..._Returns400`
  methods in `StoredQueryLibraryTest`, the three `TheXBlueprintConforms` methods
  (Fronius keeps its extra credential check separate).
  As built: every input survives as a row carrying its own message, so a failure still names
  which malformation broke. Note for the record: a DataRow collapse reduces METHODS, not test
  CASES, so it barely moves the suite's headline count - the win is readability, not runtime.
- [x] Extract the three shared helpers: `CreateVertices` (19 files), the volatile
  `WebApplicationFactory` base with a settings-dictionary overload (35 files), a
  disposable `TempDirectory` (58 files). Mechanical, reviewed per file.
  As built: `TestVertices`, `VolatileAppFactory` and `TempDirectory`, each stating its
  contract once so the sites stop repeating it. The bar was semantic equivalence, NOT
  uniformity, and several sites were deliberately left alone with a recorded reason - a
  checkpoint test that saves into the current directory and whose cleanup DELIBERATELY
  propagates failures (converting it would have moved the path and started swallowing errors
  the test exists to surface), and an index test whose helper takes explicit per-vertex
  property values the shared sequence cannot reproduce. `TempDirectory` swallows cleanup
  failures on purpose, matching what every hand-rolled site already did, because a delete
  that threw on a still-locked Windows checkpoint would turn a passing test flaky and blame
  the assertion that had already succeeded.
- [x] Apply D4's outcome to `AdjacencyConcurrencyTest`.
  As built: each of the three race hunts became a `_Heavy` method behind the repo's existing
  opt-in gate (`[TestCategory("Benchmark")]` plus `[Ignore]`, the pattern
  `LoadPathIntegrityTest` already uses) and a `_Smoke` method that stays in every run, both
  driving ONE parameterised body so the two can never drift into testing different things.
  The hunt itself is unchanged, and a gross regression (a torn read, a null, a throw) still
  fails on every run.
  MEASUREMENT CORRECTED: the saving is **5 seconds**, not the 69 the audit reported. Measured
  from the trx either side of this change on a quiet machine, the three hunts cost 6.2s before
  and 1.3s after, of a suite that runs in about 105s. The audit's 69s was taken while six
  other agents were loading the same machine. The change is still worth keeping (a stress
  test's cost is worst exactly when the machine is busy, which is CI), but it is a small
  saving and the report's "roughly a third of the suite" claim was wrong.
- Deliberately NOT done, and confirmed not done: DataRow-ing the vocabulary FailsToLoad
  cluster (its prose is the value), touching the 141 endpoint tests with genuine HTTP-layer
  assertions, or any benchmark change beyond the one merge (all remain correctly `[Ignore]`d
  and cost nothing).

Expected shape after: ~180 files instead of 206, ~2,100 methods (net of removals and
phase-2 additions), several hundred LOC of infrastructure gone, suite runtime per D4.

ACTUAL shape after, measured: **208 files** in fallen-8-unittest, not ~180. The estimate was
wrong in a way worth recording: 26 event-named files went away, but 18 subject-named files
replaced them (the audit found their content unique, so it could not be folded into an
existing file), and phases 1, 2 and 4 ADDED files of their own (RestSeamTest, the three
shared helpers, ThrowingIndexFixtures). The honest win is not the file count, it is that no
file is named after a review event any more and 575 lines of duplicated setup are gone.
Suite: 2148 passed, 0 failed, 32 skipped, ~1m50s.

## Phase 4 - duplication, one home, docs

- [x] Apply D3's outcome to the REST-client seam.
  As built: a new library `fallen-8-rest-client` (`NoSQL.GraphDB.Rest`) with no ProjectReference and
  no PackageReference at all, added to the solution and consumed by both deployables. It owns
  `RestSeam` (request building, the shared web-JSON options, the absent-body convention, and the
  unreachable-versus-timed-out classification) plus the moved `UrlSafety`. The CLASSIFICATION is
  shared and the VOCABULARY is not: each consumer passes a `RestSendFailureNaming` and a
  `RestRefusalNaming`, so `BridgeError` and `GraphTargetException`/`GraphTargetTimeoutException`
  keep their own types and wording, including the phase-1 timeout subclass and the one place the
  embed loop attaches `SummariesWritten`. The OPTIONS classes stay copied per deployable and that
  doc comment now says so precisely (the behavioural seam is shared, the configuration shape is
  not). `CodeQualityTest.TheRestOnlyDeployables_ReferenceNeitherTheEngineNorTheApiApp` pins the
  architecture rule for all three projects, since a reference added to the shared library would
  reach both consumers and nothing else would notice.
  Two corrections to the report's claims, both verified: the integrations target's `BuildPrefix`
  matched the reserved `default` alias case-INSENSITIVELY while the platform compares namespace
  names ordinally, so a namespace named `DEFAULT` had its writes sent to the default graph; and
  the two Dockerfiles do NOT copy the repo root (only `Directory.Build.props` plus their own
  project directory), so each needed the new project's directory added to its build context.
  Both container builds were run to prove it.
- [x] H1..H6 collapses per D5: full story at the owning home, one-line pointers
  elsewhere; docs pages keep the user-facing conclusion only.
  As built: H6 and H5's "no literal word beside a hole" rule were already homed in phase 1 and were
  re-verified rather than redone (`SnapshotDocument.EntityDto`, `ProviderDescriptor.EntitySummaryTemplate`).
  H4 needed no code change at all: every satellite site in the apiApp already pointed at
  `NamespaceLoader`, so only the docs page still carried the derivation, and the runtime log line and
  the 409 body keep the full reason as required. Two H3 sites the report does not list were found and
  collapsed as well, both in the web-ui (`api/types.ts` `IntegrationRunState`,
  `screens/IntegrationsScreen.tsx`), plus a compressed restatement at the runtime's own route.
  H1's second in-file copy shrank the OpenAPI description of `POST /integrations/job`, and H3's the
  one of `GET /integrations/run`; the snapshot diff is exactly those two strings. The `JobTransportLimit`
  pointer is `<c>`-quoted rather than a `see cref`, because a cref renders into the published
  description as "int IntegrationsController.JobTransportLimit".
- [x] Provider-internal helpers: `RequireFileTextAsync` on the provider context,
  `EntityDto.SetIfPresent` (one null/whitespace semantic instead of four).
  Done in phase 1 by the providers slice, because they live in the files M4 already had open;
  see the phase 1 minors note for the single semantic chosen and the one site deliberately
  left spelled out (the ARXML reader's own model, which must not import the snapshot
  contract).
- [x] Docs drift: "four routes" becomes six (docs page + three code comments);
  `mcp-server.md`'s `f8_admin` row gains `get_settings`/`set_settings`; optionally the
  run-visibility clause in the README integrations bullet.
  As built: the count is stale at EIGHT sites, not four. Beyond the four the report names, it is
  also in `docker-compose.yml`, `Fallen8IntegrationsOptions` (twice), `fallen-8-web-ui/src/api/types.ts`
  and `IntegrationsEndpointTest` - and the last one was not only prose: `ProxyRoutes` /
  `CallEveryProxyRoute` enumerated four routes, so the capability-off 403, the unsecured 401, both
  unreachable 503s and the "not twinned under /ns" pin all skipped the two run routes that
  run-visibility added. The two routes joined that list (the three test names lost their "AllFour"),
  which is a coverage gain rather than a rename. Where a count carries no information it was dropped
  instead of corrected. The README clause landed: the bullet gained "with a run you can watch" and
  lost a line, so it still fits the list.
- [x] Web-ui minors: `canvas-accessible-name.test.tsx` adopts `fakeForceGraph`; export
  `startDeadline` (or a shared deadline util) and use it in `generate.ts`; dedupe the NL
  panels' reachability-probe effect into `useNlRun`.
  Done alongside M5 in the Studio slice, since they are the same tree. The extracted probe
  gained its first isolated tests, and both "reports nothing" cases assert the probe was
  never SENT rather than that the state is null: the weaker form passed with the instance-mode
  guard removed, which the mutation check caught.
- [x] Record (not fix) the three latent config risks in the writable-instance-config
  feature record, each with its revisit trigger (parallel boot, policy relaxation,
  authority-type addition).
  As built: `features/done/writable-instance-config/` held only a spec and a plan, both
  historical, so there was no living doc to record them in. Added a README there (the repo's
  living-doc convention) carrying the three risks, each with the change that would make it
  reachable, plus where the contract actually lives.

## Impact on existing features (mandatory sweep)

- **Provider descriptors**: M4 changes two shipped descriptors; regenerate
  `features/done/integrations/provider-descriptors.json`
  (`scripts/update-provider-descriptor-snapshot.ps1`) and recapture
  `screen-integrations.png` - that recapture is the reason the snapshot gate exists.
- **OpenAPI snapshot**: H1/H3 shrink `IntegrationsController` remarks; regenerate
  (`scripts/update-openapi-snapshot.ps1`) and review the diff (removals expected only in
  the deliberately edited remarks).
- **MCP coverage**: no REST route is added or removed anywhere in this plan;
  `McpRestCoverageTest`/`McpContractTest` stay green by construction, re-run to confirm.
- **Browser probe**: phase 2 extends it; run it for phases 1..3 regardless, since the
  engine's single-threaded arms have no other gate.
- **NL-assist dataset/eval**: no REST contract change, no retrain entry needed.
- **Studio screenshots**: M5 changes polling, not pixels; no recapture beyond
  `screen-integrations.png` above.
- **Docs site**: pages touched in phase 4 must keep the link-checked build green
  (`npm --prefix docs ci && npm --prefix docs run build`).
- **Architecture diagrams**: unaffected (no new channel or deployable).

## Gates (on the one branch, before merge)

Full `dotnet test` (never `-v q`), web-ui `tsc -b` + vitest + `build:lib`, docs build
where docs change, browser probe where the engine or probe changes, both snapshot scripts
where their inputs change, and the pre-merge sweeps this repo already runs (forbidden
words, dash policy, honest commit messages).
