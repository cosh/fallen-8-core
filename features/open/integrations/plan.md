# Integrations: implementation plan

The spec is [spec.md](spec.md) and owns every rule. This owns the order the work lands in, what makes each
phase done, and the rules the work is held to.

Two ordering principles decide the sequence. **Each phase is verifiable when it lands**, so a phase that
could only be judged once a later phase exists is split until it can be judged alone. **The conformance
suite lands before the blueprints**, so each blueprint is the suite's consumer rather than its model: a
suite written after the providers is shaped by the providers that happen to exist, and every place a
blueprint has to be bent to conform is a real finding about the contract. Within the blueprints the cheap
floor precedes the expensive one, because *csv-device-list* measures the contract's weight while the
contract is still cheap to change.

Branch: `feature/integrations`. Feature code never lands on `main` directly, and commit messages are
conventional commits describing the change, with no reference to any assistant.

## Phases

| Phase | What lands | Done when |
| --- | --- | --- |
| **0. The deployable, empty but real** | *fallen-8-integrations* (`Microsoft.NET.Sdk.Web`, net10.0, root namespace *NoSQL.GraphDB.Integrations*, `IsPackable=false`, nullable on, implicit usings off, **no** reference to *fallen-8-core* or *fallen-8-core-apiApp*) in `fallen-8-core.sln`; `GET /health` and an empty `GET /integration/providers`; the *Integrations* and *Fallen8Target* options types; Dockerfile, the multi-arch entry in `.github/workflows/release.yml`, the *f8-integrations* compose service on the *integrations* profile (unpublished, read only, `/tmp` tmpfs, 120s grace, the two read-only mounts, *Fallen8Target__BaseUrl* and *Fallen8Target__ApiKey* reusing *F8_API_KEY*), the *fallen8* service's two new variables, the `scripts/env-up.js` profile push and warnings, the `scripts/env-info.js` lines, `--profile integrations` in *env:down*, *env:logs* and *env:status*; *IntegrationsController* and *IntegrationsClient* proxying the four routes bodies-untouched; the new project in both *CodeQualityTest* lists and referenced by the test project; the three superseded `features/open/integration-*` directories deleted | `dotnet build fallen-8-core.sln` clean with warnings as errors; `dotnet test` green including *CodeQualityTest* and *AuditDefectRemarksTest* over the new code; the snapshot regenerated with `powershell.exe -File scripts/update-openapi-snapshot.ps1` showing four additions and no removals; *NamespaceEndpointTest* passes with the four routes declared Fallen-8-level; *McpRestCoverageTest* passes with the deferral rule and its reason; `node --check` clean on both scripts; `docker compose --profile integrations config` resolves; the proxy answers 403 with *Fallen8:Integrations:Enabled* off and 503 with it on and no sidecar |
| **1. The contract and the validator** | *SnapshotDocument*, *EntityDto*, *IdentityClaimDto*, *ClaimReferenceDto*, *RelationDto*, *DiagnosticDto*, *SnapshotCompleteness* with *Unspecified* at zero; *IIntegrationProvider*, *ProviderDescriptor*, *ProviderSetting*, *SettingKind*, *ProviderContext*, *ProviderCatalog* with its startup check of declared claim types; *IdentifierVocabulary* and the embedded *identifier-vocabulary.v1.json* with *IdentifierStrength*, *IdentifierScope*, the canonicalisers and the accept patterns; *SnapshotValidator* with the envelope-fatal versus entity-skipped split, behind `POST /integration/snapshot/validate` and `GET /integration/vocabulary` | each of the eleven vocabulary entries has a test for its canonicaliser and its accept pattern, including the dot that separates *fronius-logger-id* from *fronius-unique-id*; a malformed vocabulary file throws on load rather than starting; every diagnostic code in spec section 9 has a test that produces it; an envelope error leaves the document unapplied and an entity error skips exactly one entity; a provider declaring an unknown or wrongly scoped claim type fails catalog construction; both read routes answer through the proxy |
| **2. Identity, resolution and the write path** | *ClaimSchema*, *ClaimKeyComposer* (three scope forms, *ForEdge*, *PrimaryKey*), *IdentityResolver* and *Resolution*, all pure; *IGraphTarget* with the ten methods of spec section 10, plus *InMemoryGraphTarget* and *Fallen8RestTarget*; *SnapshotApplier* in the fixed order; *JobRunner* with the checks, the eager credential fetch, one run at a time per identity and the four failure kinds; reconciliation as set difference, effective-only withdrawal, deletion on the last claim gated on *SafeToDelete*; *RedactingLoggerProvider*, *ActiveCredentials*, *CredentialResolver*, *CredentialLease*, *CredentialHostGuard*, *RootedNames*, the self-signed host callback, the run fingerprint; *IntegrationsMetrics* and the OTLP wiring with the redaction wrap installed last | every rule in spec sections 5, 7, 10 and 11 has a test that fails if the rule is inverted, specifically: the unclaimed arm of the in-scope rule (removing it duplicates on the second run over one unchanged fixture); the more-than-one-of-its-own case contributing its chosen element to the claimed-now set (removing it deletes the element every run); a weak claim never resolving, not even against this instance's own element; an edge hit that is not this instance's falling through to a create; withdrawal counted only where the claim property was present; deletion deferred on each of the three durability signals independently. A shared contract suite runs the same assertions against both targets so the fake cannot drift stricter or laxer than the platform. A missing index raises rather than answering empty, and the ensure-repair-retry-once path is tested including the second failure surfacing. A run over an unchanged fixture reports *issuedMutations* false. No credential value reaches a log sink, a report or the graph under a provider that tries all three. `HEAD /trim` between two runs does not change which element an entity resolves to |
| **3. The conformance suite** | *ConformanceCheck* with values 0 to 11, *ConformanceFinding*, *ConformanceReport*; *ConformanceVerifier.VerifyAsync* running the candidate twice through the real runner, catalog, validator, credential path and redaction against *InMemoryGraphTarget*; the recording handler behind the provider's *HttpClient* and the source-double contract; *IObservableProvider*, with a provider that does not implement it recorded as unjudgeable and failing; one negative fixture per check plus the two positives | each negative fixture fails **its own** check by name and the suite asserts on *Failed* rather than on "conforms", including *ClaimScoped*, whose red path is a substituted cross-instance resolver rather than a wrong provider (spec section 13); *EveryCheckTheEnumDeclaresIsActuallyReported* passes; the whole suite runs with no network, no live source and no live graph; *RunsOffline* passes a provider reaching a supplied source double and fails one that needs something the suite cannot stand in for; every check has been mutation-checked once by hand (rule 2) so none is silently unconditional |
| **4. *csv-device-list*, the floor** | the provider, its hand-written parser, and its two diagnostics (*rowWithoutMac*, *duplicateMacInFile*); whatever the contract has to change to let it exist, recorded in the spec's own words | *TheShippedCsvBlueprintConforms* passes; the parse tests cover quotes, doubled quotes, each named delimiter form, a missing header row, a missing *mac* column, a row with no MAC, a repeated MAC, and a newline inside a quoted field reported rather than mis-parsed; a missing or unreadable file fails the run and withdraws nothing; the provider contains no path handling at all; and the parsing body is reported against the roughly-hundred-line expectation, any overshoot written up as a contract finding |
| **5. *unifi-network*, the many-entity one** | the vendor OpenAPI document read whole first, then the DTOs; the paged list reader with its three defences and the page-count backstop, the per-device details read, the flat client type, the defensive 429 handling, the base-URL refusal | the three paging defences each have a test that fails when that defence alone is removed; a device answering 404 mid-run is omitted while any other failure fails the run; a console listing no sites fails the run; a whole-run test asserts only GET was issued; VPN and Teleport clients are counted and not emitted; a bare host is refused naming both published forms; the provider conforms; and the three vendor sources are named at each of the three sites that depend on their silence, which are the 429 handler, the error-body parse and the API-key header constant |
| **6. *fronius-solar*, the no-strong-overlap one** | the vendor document read whole first, then the DTOs; *GetAPIVersion.cgi* for the base URL, the status-code table, the raw-element *StatusCode* read, the entity decoding, the logger-versus-inverter address derivation | each of the five document findings has a test that fails without its handling, specifically a 200 carrying code 12 failing the run with the name *DeviceNotAvailable*, a string *StatusCode* not throwing, an HTML-entity *CustomName* landing decoded and a plain one unchanged, a logger id with a dot claiming under *fronius-logger-id*, and *GetLoggerInfo* failing the documented way tolerated while any other failure is not; the address lands on the logging device when one answers and on the single inverter when none does; a host name asserts no address claim and reports why; an empty inverter list fails the run; no realtime request is issued anywhere; and it conforms with no credential setting |
| **7. The AI surface** | the declarative entity summary template on the descriptor, opt-in per provider and per instance, default off, with dimension and metric read from `GET /status`; then the degradation matrix over it | the template renders from data with no provider-authored code on the path; embedding is off unless both opt-ins are set; no dimension, metric or model name appears as a literal anywhere; and every matrix cell that is written has a fixture that makes it red. **A cell with no such fixture is not written**, and the matrix ships with fewer than sixteen cells rather than with cells that cannot fail |
| **8. F8 Studio** | an Integrations screen: the provider list, a settings form rendered from *SettingKind*, *Required* and *Help*, job submission, and the report with its diagnostics; absent when the API answers 403 | adding a hypothetical fourth provider requires zero change under `fallen-8-web-ui`, asserted by rendering the form from a descriptor fixture the screen has never seen; a credential setting renders as a **name** field whose help text says it names a file the operator provides, never as a value field; the screen is absent with *F8_INTEGRATIONS=false*; the Studio list-cap policy is respected; the vitest and typecheck gates pass |
| **9. Docs and diagrams** | `docs/src/content/docs/integrations.md` registered in the *Features* sidebar group, with the worked claim-property queries that stand in for stored queries; the one-line README "Key features" entry; both architecture diagrams; recaptured screenshots of the new screen | `npm --prefix docs ci && npm --prefix docs run build` passes, which fails on any broken internal link; the README entry links `https://cosh.github.io/fallen-8-core/integrations/`; both diagrams show the runtime, its mounts and the proxy edge in the fixed dark plus `#E2001A` style; and the page states the user-facing contract without restating the resolution and reconciliation rules, which the spec owns |
| **10. The merge gate** | the full suite, the regenerated snapshot, the re-run impact sweep, the adversarial pass, and the dash check over the whole diff | the branch is green; the snapshot diff is reviewed; spec section 18 matches the files as they now are; the adversarial pass has produced a list naming each invariant it examined and, for each, the mutation that turns at least one named test red, since an invariant on that list with no such mutation is a missing test and blocks the merge, which is what stops the pass from being satisfied by the sentence "found nothing"; and no em dash or en dash appears anywhere in the diff, including code comments |

## Learnings carried in from the first attempt

Rules for this attempt, ordered by what each one costs when it is ignored.

**1. A check that cannot fail is worse than no check.** Worse rather than merely useless, because it is
trusted: everyone who sees it pass reads it as evidence, so a false green costs more than a gap somebody
can see. Two shapes of it are available in this feature: an offline check recorded unconditionally, which
passes even the provider that opened its own socket (spec section 13), and a degradation matrix written
over behaviour that does not exist yet (spec section 17). The first attempt hit the first of those three
times and nearly shipped the second. So the rule is procedural, not aspirational: **before writing a
check, name the fixture that makes it red, and write that fixture in the same commit.** If no fixture can
make it red, the check does not go in, and the honest artefact is a shorter suite. This is why phase 7
ships fewer than sixteen matrix cells if that is what is true, and why phase 3 includes
*EveryCheckTheEnumDeclaresIsActuallyReported*: a check the enum declares and the verifier never records is
exactly this failure wearing a name.

**2. Mutation-test the checks that matter, once, by hand.** Letting weak claims into the lookup, making
resolution look across instances, and removing the unclaimed arm must each turn tests red; a mutation that
stays green means the test was decoration and the real test is unwritten.

**3. A comment justifying why something diverges from what it models is a finding.** A fake stricter than
the platform, a test-only constructor weakening a published schema, a provider handed a path: each time
the fix is one level below the comment, not in it.

**4. Build the second consumer before believing an abstraction.** Hence the shared contract suite pinning
*InMemoryGraphTarget* against *Fallen8RestTarget*, and the conformance suite landing before the blueprints.

**5. Fetch a vendor's contract whole before writing a DTO.** The full reads produced five findings in the
Fronius document and three in the UniFi one that no amount of reasoning supplies; record each fact's
provenance where it is used, including facts that came from outside the machine-readable contract. Nor is
Fallen-8's own surface invented: every route in the seam table is read out of the apiApp's controllers
before anything depends on it.

**6. Correct prose and code in the same pass.** When a decision here changes, grep the code for its
wording before the commit closes, or the code contradicts the document that governs it.

**7. Do not let revision history become the document.** Record a decision once, in the present tense, at
the site that owns it; replace an old sentence rather than annotating it.

**8. Plan an adversarial pass on any invariant that sounds obviously true.** The in-scope rule needs its
unclaimed arm and the claim-space invariant is about what reconciliation deletes rather than what a run
writes: both read as pedantry until the duplicate-every-run consequence is traced, and neither is found by
reading the rule sympathetically. Phase 10 budgets time for it.

## Decisions and revisit triggers

| Not built | Trigger to reopen |
| --- | --- |
| Any scheduler, interval, floor, enable step, run history or instance store | never here. Timing belongs to whoever cares about the data |
| A save or trim issued on a caller's behalf | a job report has to promise durability rather than commitment |
| Any credential cache, including "resolve once per run and keep it" | none |
| Credentials from an environment variable | none |
| Credentials from a command | never |
| Credentials from an HTTP broker | a user who cannot mount a file, or an audit-trail requirement |
| Per-credential allowed-host binding | one runtime has to hold credentials for hosts at different levels of trust |
| Certificate pinning instead of a named self-signed host | a source must be reached across a network the operator does not control |
| Any unification of two elements: cross-instance or cross-provider matching, merge candidates, confirm and reject, consolidation, the incident-edge read, a *user-asserted* claim type | never in this runtime. Unification is a durable record of somebody's decision and a job runner keeps nothing, so it is a separate feature |
| Similarity of any kind as an input to resolution | never |
| A non-asserted observations collection on the snapshot | a consumer for it exists |
| Any provider setting that narrows what a complete snapshot covers, including a UniFi site filter and an *includeClients* flag | never. A source too noisy to describe in one snapshot gets its own integration |
| A property precedence table over unprefixed canonical keys | something concrete demands it |
| An event-driven provider, for which the *Partial* completeness value already exists | a source that delivers changes rather than state |
| Stored queries registered by a provider | never. A registered query cannot express a per-instance claim scope, and a label scope returns the wrong rows |
| Numeric readings or any time series in the graph | never. The fleet observability stack holds metrics |
| UniFi legacy username and password auth | the vendor publishes a contract for it |
| UniFi *port* and *network* entity kinds | somebody needs them |
| A UniFi *hasIp* relation | never |
| Fronius realtime data, and the *Values* versus *Value* divergence it would meet | somebody adds readings |
| An MCP tool for any of the four routes | the runtime can tell a new identity from a mistyped one |
| NLP enrichment over provider properties | a provider whose source carries genuine free text, and then only over fields it declares as free text |

## Progress

- [ ] Phase 0: the deployable, empty but real
- [ ] Phase 1: the contract and the validator
- [ ] Phase 2: identity, resolution and the write path
- [ ] Phase 3: the conformance suite
- [ ] Phase 4: *csv-device-list*
- [ ] Phase 5: *unifi-network*
- [ ] Phase 6: *fronius-solar*
- [ ] Phase 7: the AI surface
- [ ] Phase 8: F8 Studio
- [ ] Phase 9: docs and diagrams
- [ ] Phase 10: the merge gate
