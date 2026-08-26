# Code health review, 2026-08-26

Status: Closed - every finding below is fixed, gated and merged to `main` on 2026-08-26.
[plan.md](plan.md) carries the as-built notes, the final gate, and every place this
report turned out to be wrong - of which there were three worth naming: one claimed duplicate
test was not one (deleting it would have removed a route's only success-path coverage), the
stress test said to cost 69s costs 6.2s on an idle machine, and the one-home rule does not
reach feature specs, which are historical records.

Scope: everything merged in the two weeks 2026-08-11 to 2026-08-26 (`0764b38e..main`, ~200
commits, 597 files, ~97k insertions): integrations (+ hardening, autosar-arxml,
file-upload, run-visibility, file-size fix), element-similarity-search,
embed-chunk-timeout-headroom, nahil-backend, bge-m3-input-ceiling, writable-instance-config
(+ configuration-surface, read-only affordance), instance-level-health,
namespace-startup-load, host-plugin-registration, inline-transaction-execution,
browser-checkpoint-sequential, browser-teardown, trim-safety, engine-integrity fixes,
durability work, studio-embeddable-packaging, studio-dashboard-removal, the Studio fixes,
the MCP additions, the bench `load` family, CI, and the nl-assist eval infrastructure.

Method: seven scoped reviewers over the full diff and the files at HEAD (integrations,
embeddings/backends, configuration, engine/browser host, Studio, test suite,
cross-cutting), the last fanning into five sweeps (cross-project duplication,
one-home-per-explanation, docs-vs-code drift, README/architecture freshness, orphaned
artifacts). Every finding below carries file:line evidence read at HEAD. The load-bearing
majors (M1 to M4) were re-verified by hand before entering this report. VERIFIED means
traced fully to code; SUSPECTED means the code shape is confirmed but the conclusion needs
a decision or input this repo does not contain.

## Verdict

**No blockers.** No data corruption, no credential leak, no wrongful delete, no orphaned
artifact, and the architecture diagrams and README are current. The engine-level work of
the window (inline transactions, durable-before-ack, the namespace data-loss guard, the
recovery-outcome publish) held up under a deliberate race hunt with zero live defects.

What needs fixing: four verified correctness majors (all in the integrations/embedding
seam and its Studio edge), two browser coverage gaps the probe was built to close and
does not, two live-config test gaps in proven regression-prone territory, one major
cross-project duplication, five one-home-per-explanation violations, and a test suite
whose perceived oversize is file fragmentation and copy-pasted infrastructure rather
than redundant coverage. (A fifth suspected major, the ARXML `secures` resolution, was
closed by research on 2026-08-26: the code is correct.)

## Majors - correctness (all VERIFIED by hand)

- [x] **M1. A transport failure mid-way through the chunked embedding write reports 0
  summaries embedded, even when chunks landed.**
  `fallen-8-integrations/Graph/Fallen8RestTarget.cs:511-578` accumulates `written` but only
  attaches it on the explicit status-code throw (`:571-576`). A connection failure or a
  client-side timeout throws from `SendCoreAsync` (`:964-981`) with `SummariesWritten`
  left at its default 0 (`Graph/IGraphTarget.cs:234`), which
  `Run/SnapshotApplier.cs:648` then reports as the embedded count. This is exactly the
  "many chunks landed, report said zero" incident the feature was built to prevent, on
  exactly the client-timeout path its own findings.md names; the spec invariant ("a
  number lower than the dirty count is preferred to reporting zero", spec §4) is violated
  on that path. The only mid-chunk-failure test
  (`IntegrationsWritePathTest.AGraphFailureMidChunk_StillCarriesTheCountThatLanded`)
  exercises a 400 status, never a transport failure. Fix: carry `written` on every throw
  path out of the embed loop; add the transport-failure test.

- [x] **M2. The integrations runtime and the apiApp hold competing deadlines, the exact
  mistake this repo removed from the chat gateway and the MCP bridge.**
  `fallen-8-integrations/Run/GraphTargetFactory.cs:83` hardcodes
  `Timeout = TimeSpan.FromSeconds(120)` on every call to the apiApp, while the embedding
  route legitimately runs up to `Fallen8:Embedding:TimeoutSeconds` (300s), and Nahil
  warm-up can consume most of that. The MCP bridge made the opposite choice for the same
  seam and documented why: `fallen-8-mcp/Configuration/Fallen8TargetOptions.cs` defaults
  to 330s, deliberately ABOVE the longest downstream budget, "the same
  two-competing-deadlines mistake this repo removed from the chat gateway". When the 120s
  timeout fires first it also triggers M1. Fix: give the integrations
  `Fallen8TargetOptions` a `TimeoutSeconds` mirroring the MCP bridge's (default 330,
  stated reasoning), and decide whether a client-side timeout on the embedding write
  joins the degrade-to-absent set (see plan, decision D2).

- [x] **M3. The `write-edges` run phase is entered only after the edges are already
  written.** `fallen-8-integrations/Run/SnapshotApplier.cs:332-337`: `WireEdgesAsync`
  (the real batched writes) completes, then `EnterPhase(RunPhases.WriteEdges)` fires. A
  caller polling `GET /integrations/run/{id}` never observes `write-edges` while edges are
  being written. The sibling `write-elements` block 40 lines up (`:291-297`) carries the
  council's own lesson in its comment ("BEFORE the writes, not after them"); it was not
  propagated. Same defect class, milder, in `Run/JobRunner.cs:217-223` where
  `EnterPhase(RunPhases.Validate)` runs after `_validator.Validate(...)`. No test asserts
  `IRunProgress` call ordering through the real applier. Fix: move both `EnterPhase`
  calls before the work, thread `progress` into `WireEdgesAsync` for per-batch advances,
  add an ordering test.

- [x] **M4. The UniFi and Fronius summary templates embed dangling literal words into
  semantic text.** `Providers/UnifiNetwork/UnifiNetworkProvider.cs:151` has
  `state {unifi.state}`, `Providers/FroniusSolar/FroniusSolarProvider.cs:112` has
  `status {fronius.status}`. `EntitySummaryTemplate.Collapse` removes punctuation around
  an empty hole but cannot remove a literal word beside one, so entity kinds that never
  populate the hole (UniFi sites and clients; the Fronius datamanager) embed
  "client Phone, state, 10.0.0.5" and "datamanager, status". The AUTOSAR provider's own
  comment (`Providers/AutosarArxml/AutosarArxmlProvider.cs:128-129`) documents fixing
  exactly this ("would salt every summary with a dangling 'unit' and embed the shape of
  the template instead of the description of the thing"); the lesson never reached the
  two providers that motivated it. No test renders a UniFi or Fronius summary; only
  AUTOSAR has a template/holes agreement test. Fix: drop the literal words from both
  templates, add a rendered-summary test per provider, regenerate
  `provider-descriptors.json`, recapture `screen-integrations.png` (the snapshot is what
  the docs capture replays).

- [x] **M5. Studio polls the same status twice with conflicting intervals.**
  `fallen-8-web-ui/src/app/AppShell.tsx:67-74` and
  `src/components/InstanceHealth.tsx:58-63` each hand-roll a `useQuery` on the identical
  cache key `[instance.id, "status"]` with different `refetchInterval` (15s vs 20s); both
  mount on the Connect screen for the active instance, so the 20s value is dead
  configuration (TanStack schedules per-observer; the shorter wins). The docstring on
  `src/state/status.ts:45-51` warns about exactly this defect class for `retry` on the
  same key. Fix: `InstanceHealth` uses the existing `useStatus()` hook; hoist the
  `15_000` literal (currently at six sites) into one constant beside `listCaps.ts`.

## Major - needs external verification (SUSPECTED) - RESOLVED 2026-08-26

Closed by research, no code change: `SECURED-I-PDU.PAYLOAD-REF` does target a
PDU-TRIGGERING (three independent sources, cited in plan.md D1), so the reader's
resolution through the triggering map is correct. Kept below as originally found, for the
record.

- [x] **S1. The ARXML `secures` relation may never resolve on real files.**
  `Providers/AutosarArxml/ArxmlReader.cs:504-519` collects `PAYLOAD-REF` with
  `ThroughTriggering: true` and resolves it via the PDU-triggering map (`:719-732`). If
  the real AUTOSAR meta-model has `SECURED-I-PDU.PAYLOAD-REF` point directly at an I-PDU
  rather than a PDU-TRIGGERING, every `secures` edge in a production extract resolves to
  null and is silently dropped as `arxmlUnresolvedReference` (fails safe, but a dead
  letter). The fixture cannot rule this out: it is synthetic by the spec's own
  confidentiality rule, so it may simply match what the code assumes. Needs the AUTOSAR
  4.x XSD or a real secured-PDU sample. Recorded as plan decision D1.

## Majors - coverage gaps (VERIFIED)

- [x] **C1/C2. Two single-threaded arms are executed by no test, including the browser
  probe that exists to run them.** The sequential traversal sweep
  (`fallen-8-core/Algorithms/Traversal/OutEdgeSweep.cs`, the `SweepRange` arm) and the
  skip-the-wait branch of `ChangeFeedDispatcher.Dispose`
  (`fallen-8-core/ChangeFeed/ChangeFeedDispatcher.cs:433-443`) are reached only when
  `HostCapabilities.SupportsBackgroundWork` is false, which no test host is and the probe's
  seven checks never trigger (none touches the sweep or a change-feed subscription). The
  gap is honestly disclosed in the probe's header and CLAUDE.md, but disclosure is not
  coverage. Fix: probe checks 8 and 9 (a small sweep with exact edge count; open a
  change-feed-enabled engine, subscribe, dispose, assert completion).

- [x] **C3. Two of six live-tier settings have no live-apply test.**
  `Fallen8:ChangeFeed:KeepAliveSeconds` and `Fallen8:Plugins:MaxCount` have `ApplyNow`
  delegates (`fallen-8-core-apiApp/Configuration/Fallen8SettingCatalog.cs:192-204`) that
  no test invokes through a live PATCH; the other four each have a dedicated
  `LiveSettingTest` method asserting observed behaviour change. The feature's own plan
  (phase 4) requires exactly such a test per promoted key. Fix: two tests mirroring the
  existing ceiling pattern.

- [x] **C4. The "failing live apply downgrades to restart" path is pinned with a hand-fed
  string, never a thrown exception.** `Fallen8LiveSettings.Apply`
  (`fallen-8-core-apiApp/Configuration/Fallen8LiveSettings.cs:97-117`) catches the
  delegate's exception; the only test (`ConfigOverridesTest.cs:606`) feeds
  `applyFailure: "the delegate threw"` into the read model directly. The feature's own
  council log records this failure mode as a real bug once found. Fix: a test with a
  deliberately throwing `ApplyNow`, asserting `FailureFor` reports it and later entries
  still apply.

- [x] **C5. The bge-m3 ceiling fix's two mechanisms are untested.** `truncate: false`
  (`fallen-8-core-apiApp/Embedding/Fallen8EmbeddingProvider.cs:90-130`) and
  `OverLongInputHint`'s substring match (`:275-304`) have zero automated coverage, and the
  code's own comment says "re-check on an OllamaSharp upgrade". Fix: a fake-generator
  test asserting the option reaches `GenerateAsync`, plus present/absent pins on the hint.

## Major - duplication (VERIFIED)

- [x] **D1. The REST-client seam is reimplemented near line-for-line in both REST-only
  deployables.** `fallen-8-mcp/Bridge/Fallen8RestClient.cs:169-207` and
  `fallen-8-integrations/Graph/Fallen8RestTarget.cs:911-981` duplicate request building,
  `JsonContent.Create(body, mediaType: null, JsonOptions)`, the 204/`"null"`-body
  soft-not-found convention (the identical line
  `String.IsNullOrWhiteSpace(text) || text.Trim() == "null" ? null : text`), and the
  timeout-vs-cancellation classification
  (`catch (TaskCanceledException) when (!token.IsCancellationRequested)`), wired to two
  different exception types. Unlike the five-property `Fallen8TargetOptions` copy (a
  documented, conscious tradeoff), this is behavioral logic with no mechanism forcing a
  fix in one seam into the other. A shared source would not violate the architecture rule
  (it references neither engine nor apiApp), but it is a maintainer decision: plan
  decision D3. Related inconsistency: the MCP bridge validates and percent-encodes
  namespaces via `Bridge/UrlSafety.cs`; the integrations target uses bare
  `Uri.EscapeDataString` in `BuildPrefix` (weaker validation, same wire contract).

## Majors - one home per explanation (VERIFIED, five violations)

Each story below is narrated in full at 3+ sites; the fix is one owning home and one-line
pointers everywhere else (docs pages keep the user-facing conclusion, never the
derivation):

- [x] **H1.** The 192 MiB proxy bound / 128 MiB `MaxFileBytes` / ~171 MiB legal job /
  "above ~144 MiB has no effect" derivation: `docs/src/content/docs/integrations.md:113-122`,
  `IntegrationsController.cs` `JobTransportLimit` doc AND the `Job` method remarks.
  Home: `JobTransportLimit`.
- [x] **H2.** The Nahil warm-up retry algorithm (both Retry-After forms, 2s to 30s jittered
  backoff, 60s clamp, caller's timeout the only budget):
  `features/done/nahil-backend/spec.md` FR-4, `docs/src/content/docs/nahil.md:145-165`,
  `Helper/NahilWarmupRetryHandler.cs:36-53`. Home: the handler (it owns the contract).
- [x] **H3.** Run visibility's "one slot per identity, superseded, in memory, capped, not
  a history": spec §, `docs/src/content/docs/integrations.md:82-84`,
  `IntegrationsController.cs:168-170`, `fallen-8-integrations/Run/RunTracker.cs:35-42`.
  Home: `RunTracker`.
- [x] **H4.** Why activation refuses unregistered checkpoint files:
  `features/done/namespace-startup-load/spec.md:96-99`,
  `docs/src/content/docs/namespaces.mdx:199`, `Services/NamespaceLoader.cs:55-61`.
  Home: `NamespaceLoader` (runtime log line and 409 body keep the full reason; they are
  operator-facing strings, not documentation).
- [x] **H5.** The AUTOSAR "a system extract is complete by construction" story:
  `features/done/autosar-arxml/spec.md:32-34` AND its own §10 repeat,
  `docs/src/content/docs/integrations.md:381-383`, `AutosarArxmlProvider.cs:48-50`.
  Home: the provider (it owns `Complete`).
- [x] **H6 (provider-internal).** "Value goes out as the source wrote it" is re-narrated
  in all four providers, "absent is absent, never empty string" at five sites. Home:
  `IdentityClaimDto`/`EntityDto`.

## The test suite: the honest answer to "too many unit tests"

Measured: 2,147 tests (2,117 executed, 30 skipped, 0 failed), 3m45s wall, effectively
single-threaded. The 30 skips are exactly the 26 `[Ignore]`d benchmark methods plus 4
opt-in live/stress tests; no flake is suppressed anywhere (grepped; the two "flaky"
comments describe fixed defects).

**Method-level redundancy is small: 12 verified pure duplicates plus ~14 merge/DataRow
candidates out of 2,117 executed methods (~1.2%).** The size perception is driven by
three other things:

1. **File fragmentation.** 26 files are named after review events, not subjects
   (`AuditDefect*`, `CorrectnessFixes*`, `*Followups*`, `IngestionCouncilFixesTest`):
   9,426 LOC, 209 methods, 11.5% of the suite. A per-method audit of all 26 files
   ([test-consolidation-map.md](test-consolidation-map.md)) settled every row: 9 methods
   are verified pure duplicates (delete), 85 are unique content with a natural subject
   home (relocate), 115 are unique with no better home (the file is renamed to a subject
   name, content untouched). Three warnings gate the fold and are recorded in the map,
   the sharpest being that `AuditDefectMcpAlgorithmTest` is the suite's ONLY coverage of
   the MCP `PathsTool`/`SubgraphTool`, and that `IndexTest`'s own `Between` assertions
   were written defensively around the very bug (B3) whose corrective tests now move in
   and replace them.
2. **Infrastructure duplication.** 19 files hand-roll a near-identical
   `CreateVertices(Fallen8, int)` (~170 LOC); 35 files nest their own
   `WebApplicationFactory<Program>` subclass, mostly the identical volatile-durability
   one-liner; 58 files hand-roll temp-directory create/cleanup. Three small shared
   helpers remove several hundred LOC with zero methods deleted.
3. **Runtime concentration, not spread.** `AdjacencyConcurrencyTest`'s three methods were
   measured at 69.3s, ~31% of total execution (the largest single test 57.2s). Tuning or
   opt-in gating them is a decision (plan D4), because reducing a stress test's iterations
   is a coverage tradeoff, not a cleanup.
   **MEASUREMENT CORRECTED 2026-08-26.** That 69.3s is not reproducible on an idle machine:
   re-measured from the trx of a quiet run, the three cost **6.2s** of a ~105s suite (about
   6%), the largest being 2.6s. The original figure was taken while six other review agents
   were working the same machine, which inflated it roughly twentyfold. The audit did not say
   so, which is the lesson: a timing number needs the machine state it was taken under. The
   gating still landed (see plan D4) because the heavy arms are genuine stress runs and the
   saving grows exactly when the machine is busy, which is CI - but the headline "a third of
   the suite" was wrong and no decision should rest on it.

The 12 verified pure duplicates are named in
[test-consolidation-map.md](test-consolidation-map.md) (9 inside the event-named files,
plus `SubGraphControllerTest.Create_DoesNotMutateSourceGraph`,
`PropertyMutationEndpointTest.PutProperties_SetsAndRemoves_InOneBatch`, and
`NamespaceDurabilityTest.cs:605`, a strict subset of `NamespaceEndpointTest.cs:702`).
DataRow/trim candidates: `StoredQueryLibraryTest`'s eight `Register_..._Returns400`
methods, the three `TheXBlueprintConforms` methods, the duplicated `ApplyNow != null`
assertion in `LiveSettingTest.cs:420`, and the duplicated literal checks in
`IntegrationsIdentityTest`'s per-type tests (trim, keep each test's unique assertions).
Everything else sampled across the ten largest files pins a distinct branch; cross-layer
endpoint-vs-engine overlap is 2 methods out of 143 examined. The web-ui suite (583 cases)
showed breadth, not padding.

## Minors (verified unless marked)

- Docs drift: "the API's four routes" is stale - run-visibility made it six
  (`docs/src/content/docs/integrations.md:36`, `IntegrationsController.cs:44`,
  `fallen-8-integrations/Hosting/IntegrationEndpoints.cs:40`,
  `Security/DynamicCapabilityAuthorization.cs:62`).
- `docs/src/content/docs/mcp-server.md:110`: the `f8_admin` row omits
  `get_settings`/`set_settings` (configuration.md already names them).
- `features/done/autosar-arxml/spec.md:67` still shows the pre-fix template with the
  dangling "unit" (sections 9, code, and snapshot all agree on the fixed one).
- `Fallen8ConfigOverridesSource.IsAuthority` and `Fallen8ConfigOverrides.Classify` switch
  on the same provider types independently; drift reintroduces the read-only-affordance
  bug server-side. Fix: delegate or parity-test.
- `JobRunner.cs:142-177`: `_files.Create(...)` runs after the credential lease resolves
  but before the `using` opens; a throwing future `IJobFilesFactory` would leak the hold
  (SUSPECTED, structural). Move it inside the scope.
- Provider-internal duplication: the read-wrapper copy in Csv/Arxml providers, four
  "write property if present" variants, relation-by-claim built three ways, the Fronius
  ipv4-claim block pasted twice while UniFi has a named helper, and the identical
  HTTP-failure translation pair in `UnifiClient.cs:484-495` / `FroniusClient.cs:320-332`.
- `unknownRelationTargetType` is an entity-skip diagnostic the integrations spec does not
  list and no test names (its three siblings each have a `_SkipsOnlyThatEntity` test).
- No test pins `PUT /save` refusing on a not-loaded namespace (the spec's third
  enforcement point; structurally guaranteed today, unpinned).
- Web-ui: `tests/canvas-accessible-name.test.tsx:54-70` still hand-rolls the
  3d-force-graph mock its own commit claims was consolidated into `fakeForceGraph.ts`;
  `src/delegate/nl/generate.ts` rewrites the `startDeadline` shape because it is not
  exported; both NL panels duplicate the reachability-probe effect `useNlRun` left behind.
- Latent config risks (SUSPECTED, documented rather than fixed): the double-binding of
  `EnableConfigurationWrite` at two capture times; the reload-token subscription gap at
  boot; the `IsLoaded`-means-constructed invariant that only holds while boot is
  sequential (the spec names parallel boot as a revisit trigger).
- Test brittleness: 15+ `StringAssert.Contains` calls pin full explanatory prose instead
  of the diagnostic `Code`;
  `AJobWhoseIdentityCarriesAColon_IsRefusedBeforeTheRunStarts` does not verify the
  "tracks nothing" half its sibling does.
- `nahil.md` / README simple diagram: the simple view says "remote gateway" where the full
  view names Nahil; changed deliberately by the Nahil commit itself, so optional.

Found during implementation, both pre-existing, and both FIXED on 2026-08-26 after the
plan's phases closed:

- [x] `fallen-8-integrations/Providers/AutosarArxml/ArxmlReader.cs` contained two RAW NUL
  bytes inside string literals used as a composite-key separator (actual 0x00 bytes where
  `"\0"` was meant). It compiled and behaved correctly, but it made ripgrep and grep treat
  the whole file as binary, so a 47 KB reader was invisible to content search - which is how
  a reviewer finds anything. Replaced with the two-character escape, byte-for-byte otherwise
  unchanged; the dedup it keys is already pinned by
  `IntegrationsArxmlReaderTest.OneSignalMappedTwiceInOnePdu_IsOneEdge_AndNotADiagnostic`. A
  sweep of every `.cs` file in the repo found no other occurrence.
- [x] UniFi's `BuildDevice`/`BuildClient` took a `diagnostics` parameter neither used (and
  which their sibling `BuildSite` does not take). Removed from both signatures and both call
  sites; the caller's own `diagnostics` list is still used at four other sites, so nothing
  became dead.

## What held up (recorded so the review's coverage is auditable)

- Engine: durable-before-ack ordering is shared, unmodified, by both writer arms;
  inline reentrancy defers instead of nesting; checkpoint-vs-inline interleaving is
  impossible by construction; `RecoveryOutcome` publishes atomically including on a
  faulting replay; both "flake" fixes changed only test-side defects, with mutation
  checks recorded.
- Integrations: identity, reconciliation, credential lifetime (case-folded longest-first
  redaction, value-based hold counting, host/scheme enforcement), the run-race handling,
  and the deliberately inert apiApp proxy all match their specs on direct reading.
- Configuration: the catalog's invariants are structural (tier derived from apply mode,
  three factory methods), the data-loss guard sits at all three required points with
  correct memory ordering, PATCH is fail-closed and whole-batch-atomic.
- Studio: every named fix is a fix at the emitter (the monaco filter-suppression was
  itself reverted when the real fix landed); the embed boundary is enforced by a
  build-time tripwire that parses real CSS/JS; dashboard removal left zero dead code.
- MCP, bench, CI, finetune infra (reviewed directly): nullable not-loaded counts so an
  agent can never read "0 vertices" off a namespace that holds data; the bench `load`
  family validates restored counts; the Nahil overlay fails closed in CI; the eval
  scripts reuse the trainer's bicep/teardown instead of copying them.
- Hygiene: no orphaned artifact in ~240 added files; docs-vs-code spot checks found one
  stale number ("four routes") in dozens of verified claims; both architecture diagrams
  current and on-brand; the open/done feature split is honest.
