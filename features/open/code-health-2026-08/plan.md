# Code health improvement plan, 2026-08

Status: Open - awaiting the decisions in phase 0; nothing implemented.
Findings and evidence: [report.md](report.md). Finding ids (M1..M5, S1, C1..C5, D1,
H1..H6) refer to that report.

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
  conclusion, specs and controller remarks become one-line pointers.

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

- [ ] Move `ThrowingOnSaveIndex`/`ThrowingOnLoadIndex` to a shared fixture home first
  (three files reference them).
- [ ] Execute [test-consolidation-map.md](test-consolidation-map.md): delete the 12
  verified pure duplicates, relocate the 85 mapped methods into their subject files,
  rename the keep-whole files to subject names. Keep every method's prose rationale.
  Honor the map's four warnings, especially: `AuditDefectMcpAlgorithmTest` is the sole
  `PathsTool`/`SubgraphTool` coverage (rename, never thin), and `IndexTest`'s defensive
  `Between` assertions are replaced by the corrective B3 tests moving in.
- [ ] Trim the duplicated `ApplyNow != null` assertion in `LiveSettingTest.cs:420`, the
  duplicated literal checks in `IntegrationsIdentityTest`'s per-type tests, and the
  line-level verbatim assertion duplicates the map names (keep each test's unique
  assertions).
- [ ] DataRow the two remaining verified clusters: the eight `Register_..._Returns400`
  methods in `StoredQueryLibraryTest`, the three `TheXBlueprintConforms` methods
  (Fronius keeps its extra credential check separate).
- [ ] Extract the three shared helpers: `CreateVertices` (19 files), the volatile
  `WebApplicationFactory` base with a settings-dictionary overload (35 files), a
  disposable `TempDirectory` (58 files). Mechanical, reviewed per file.
- [ ] Apply D4's outcome to `AdjacencyConcurrencyTest`.
- [ ] Deliberately NOT done: DataRow-ing the vocabulary FailsToLoad cluster (its prose
  is the value), touching the 141 endpoint tests with genuine HTTP-layer assertions, or
  any benchmark change (all 26 are correctly `[Ignore]`d and cost nothing).

Expected shape after: ~180 files instead of 206, ~2,100 methods (net of removals and
phase-2 additions), several hundred LOC of infrastructure gone, suite runtime per D4.

## Phase 4 - duplication, one home, docs

- [ ] Apply D3's outcome to the REST-client seam.
- [ ] H1..H6 collapses per D5: full story at the owning home, one-line pointers
  elsewhere; docs pages keep the user-facing conclusion only.
- [ ] Provider-internal helpers: `RequireFileTextAsync` on the provider context,
  `EntityDto.SetIfPresent` (one null/whitespace semantic instead of four).
- [ ] Docs drift: "four routes" becomes six (docs page + three code comments);
  `mcp-server.md`'s `f8_admin` row gains `get_settings`/`set_settings`; optionally the
  run-visibility clause in the README integrations bullet.
- [ ] Web-ui minors: `canvas-accessible-name.test.tsx` adopts `fakeForceGraph`; export
  `startDeadline` (or a shared deadline util) and use it in `generate.ts`; dedupe the NL
  panels' reachability-probe effect into `useNlRun`.
- [ ] Record (not fix) the three latent config risks in the writable-instance-config
  feature record, each with its revisit trigger (parallel boot, policy relaxation,
  authority-type addition).

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
