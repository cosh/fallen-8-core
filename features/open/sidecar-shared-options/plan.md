# Sidecar shared options - Implementation plan

Seven phases on one branch, `feature/sidecar-shared-options`. Phase 1 is a pure move and must be
behaviour-neutral; phases 2, 3 and 4 are the three defects the move exposed and the gates that keep
them fixed; phases 5 and 6 are the two smaller items the same branch carries; phase 7 is the gate.

Each phase ends buildable and green. A phase that finds something the spec got wrong updates the
spec in the same commit rather than leaving the record behind - that is what makes the spec the
living document and not an account of what was hoped for.

## Phase 1 - the shared types, and nine classes that derive from them

**New, in `fallen-8-rest-client/Configuration/`:**

1. `OptionBounds.cs` - moved verbatim from `fallen-8-agents/Configuration/OptionBounds.cs`,
   `internal static` becoming `public static`. Its two doc paragraphs (the floor and the measured
   `CancelAfter` incident behind the ceiling) move with it: they are the one home for why both ends
   exist, and phase 2 is about a place that had only one of them.
2. `AFallen8TargetOptions.cs` - `public abstract class`, `SectionName = "Fallen8Target"`,
   `BaseUrl`, `ApiKey`, `ApiKeyHeader`, `TimeoutSeconds`, and
   `Deadline => OptionBounds.Seconds(TimeoutSeconds)`. A `protected` constructor takes the default
   timeout, so each deployable keeps its own number and the reason for it.
3. `AFleetIdentityOptions.cs` - `public abstract class` with `Tenant`, `Instance` and the single
   implementation of `Resolve()`, which returns a `FleetIdentity` with the four values already
   defaulted, an `Attributes()` list and the four wire-name constants; a `protected` constructor
   takes the instance-id prefix
   (`f8-mcp-`, `f8-integrations-`, `f8-agents-`). Plus `public sealed class IdentityLevel`,
   promoted from a nested type in three places to one top-level type.
4. `AFleetObservabilityOptions.cs` - `public abstract class` with `Otlp` and `OtlpEnabled`, plus
   `public sealed class OtlpOptions`, promoted the same way.

**Changed - each keeps its name, its section and its own knobs:**

| File | Becomes | Keeps |
|---|---|---|
| `fallen-8-mcp/.../Fallen8TargetOptions.cs` | `: AFallen8TargetOptions`, `base(330)` | `TlsInsecure` |
| `fallen-8-integrations/.../Fallen8TargetOptions.cs` | `: AFallen8TargetOptions`, `base(330)` | `DefaultNamespace`, `EmbedConcurrency` |
| `fallen-8-agents/.../Fallen8TargetOptions.cs` | `: AFallen8TargetOptions`, `base(630)` | - |
| `fallen-8-mcp/.../McpIdentityOptions.cs` | `: AFleetIdentityOptions`, `base("f8-mcp-")` | `SectionName` |
| `fallen-8-integrations/.../IntegrationsIdentityOptions.cs` | `: AFleetIdentityOptions`, `base("f8-integrations-")` | `SectionName` |
| `fallen-8-agents/.../AgentsIdentityOptions.cs` | `: AFleetIdentityOptions`, `base("f8-agents-")` | `SectionName` |
| the three `*ObservabilityOptions.cs` | `: AFleetObservabilityOptions` | `SectionName` |
| `fallen-8-agents/.../OptionBounds.cs` | deleted; `AgentsOptions.KeepAlive` uses the seam's | - |

**What each derived class keeps of its documentation** is the part that is about *it*: the section
it binds from, the knob only it has, what its own default sits above. What moves to the base is the
part that was the same sentence three times. A derived class that ends up with a summary saying
only "the same as the base" gets a one-line pointer instead, per the one-home rule.

**Behaviour-neutrality check, before any defect is touched:** `dotnet build` and `dotnet test`
green with no test edited. The 15 existing references across
`AgentPostureTest`, `AgentRuntimeTest`, `IntegrationsEndpointTest` and `IntegrationsWritePathTest`
construct these options directly, so they are the proof that binding and defaults did not move. If
any of them needs a change, that is a signal the move was not neutral and the diff is wrong.

## Phase 2 - defect 1: the two hosts that crash on a large deadline

`fallen-8-mcp/Hosting/McpHost.cs:90` and `fallen-8-integrations/Run/GraphTargetFactory.cs:83` both
build an `HttpClient.Timeout` from `TimeSpan.FromSeconds(Math.Max(1, ...))`, which has a floor and
no ceiling. Both become `target.Deadline`.

**The test comes first and must fail first.** One test per deployable, each setting
`Fallen8Target:TimeoutSeconds` to a value whose millisecond count exceeds `Int32.MaxValue`,
asserting the host builds its client and the resulting timeout is the clamped ceiling. Run both
against the unfixed call sites and record the exception each one throws; a test that passes before
the fix is testing nothing. Then fix, then re-run, then mutation-check by restoring `Math.Max` and
watching each fail with its own message.

The floor keeps its own test too, because the clamp has two ends and the spec claims both.

## Phase 3 - defects 2 and 3: what the integrations runtime tells a collector

Both are in `fallen-8-integrations/Hosting/IntegrationsObservability.cs`.

1. Pass the resolved instance id into `AddService(..., serviceInstanceId:)`, as the other two do,
   so `service.instance.id` stops being a fresh GUID per process.
2. Replace the separate `services.AddLogging(... AddOpenTelemetry(...))` block with
   `otel.WithLogging(...)`, so logs carry the same resource as metrics and traces.
   `IncludeFormattedMessage` and `IncludeScopes` are preserved through `WithLogging`'s options, not
   dropped: they decide what a record contains, which is not what this phase is about.
3. Rename the service to `fallen8-integrations`, which is what the shipped dashboard's selector has
   always required.

**Tests.** The identity and resource wiring is DI-shaped, so the honest test is over what the
wiring produces rather than over a log line: build the service collection, resolve the OTel
resource, and assert `service.name`, `service.instance.id` and the four identity attributes, once
for each of the three sidecars so the three agree by construction. If the resource cannot be
resolved without a live exporter, the fallback is a narrower test over the one thing that must not
churn - two calls to the wiring produce the same instance id - plus the service-name pin in phase 4,
and the spec records that narrowing rather than implying full coverage.

## Phase 4 - the two gates, both derived and both mutation-checked

In `fallen-8-unittest/CodeQualityTest.cs`:

1. **No type name declared twice across the REST-only projects.** Enumerate every `class`,
   `record`, `interface` and `enum` declaration in `fallen-8-rest-client`, `fallen-8-mcp`,
   `fallen-8-integrations` and `fallen-8-agents`; fail when one simple name appears in more than
   one project. Allowlist: `Program`, because the web SDK requires one per assembly, with that
   reason on the line. Derived, not listed, so a fifth copy of something new fails too.
2. **Every service name matches the selector the shipped dashboard uses.** Read
   `{service_name=~"..."}` out of `observability/grafana/dashboards/per-tenant.json`, translate it
   to a .NET regex, and assert every `AddService("<literal>")` in the apiApp and the three sidecars
   matches. The expectation comes from the consumer, so changing the convention means changing the
   dashboard first.

Mutation checks for both, recorded in the feature record with what each mutant produced: restore
one duplicate type and see gate 1 name it; rename one service and see gate 2 name it. Per the
repo's own trap list, mutate by deleting or by changing a value - warnings-as-errors rejects the
obvious `if (false)` mutants.

## Phase 5 - the bookkeeping

- `git mv features/open/integration-embed-concurrency features/done/` (landed 2026-09-02,
  merge `7a42dc85`).
- `features/open/platform-integrity-audit/` is **not touched**: W7, W8 and the P1 remainder are
  genuinely pending, so `open/` is correct. The review that produced this branch was wrong about it
  and the spec says so.
- `cleanup-report.md` -> `features/done/cleanup-report-2026-07/report.md`, and repoint the three
  documents that cite it (`features/done/audit-defects/report.md`,
  `features/done/consolidation-audit/{report,spec}.md`, `features/done/docs-site/spec.md`).
- `CLAUDE.md`: the suite takes about 6 minutes, measured, not 90 seconds.
- `docs/src/content/docs/debugging.md`: the integrations runtime and the agent host join the ports
  table beside the MCP row, with the fact that neither publishes a host port and both are reached
  through the API's authenticated proxy. The prose that introduces the MCP server as "a fourth
  project" is corrected to name all three sidecars.

## Phase 6 - the SBOM that rewrites its own history

In `fallen-8-web-ui/scripts/samples/fallen8Deps.ts`:

1. A `canonicalize(sbom)` step applied to the fetched document before it is written: `packages`
   sorted by `SPDXID`, `relationships` sorted by `(spdxElementId, relatedSpdxElement,
   relationshipType)`. SPDX defines both as unordered sets.
2. A content comparison against the committed copy with `documentNamespace`,
   `creationInfo.created` and `creationInfo.creators` excluded. Unchanged content leaves the file
   untouched and logs that it did; changed content is written whole, fresh metadata included, so the
   timestamp keeps meaning "when this SBOM last actually changed".
3. The workflow is unchanged: its `git status --porcelain` guard then commits only on a real change.

**Tests** in `fallen-8-web-ui/tests/`: a shuffled document canonicalises to identical bytes; a
document differing only in the three volatile fields is recognised as unchanged; a genuinely added
package is recognised as changed. Plus the proof that matters more than any of them - rebuild the
sample from the committed SBOM and compare `samples/fallen8-deps.jsonl` byte for byte against what
is committed. The sort changes vertex ids, which the transform derives from package position, so
this run is what says whether the committed sample and the docs figures that quote it still hold.
If the ids move, the rebuilt sample is committed as part of this phase and the change is stated;
what must not happen is the sort landing while the committed sample still reflects the old order.

## Phase 7 - the gate

1. `dotnet build` clean; `dotnet test` green.
2. `npx tsc -b` and `npm test` in `fallen-8-web-ui`.
3. `npm --prefix docs run build`, link validation included.
4. `docker compose config -q` on the base file and each overlay.
5. `powershell -File scripts/update-openapi-snapshot.ps1` and the provider-descriptor snapshot
   script: both expected to print no diff, which is the claim that no REST surface moved.
6. The browser probe, published and run, because the rule is to run it rather than to argue it does
   not apply.
7. An adversarial review of the whole diff and the section 9 cross-feature sweep, delegated in
   parallel and verified rather than believed. Its findings land in
   `features/open/sidecar-shared-options/findings.md`.
8. The feature record moves to `features/done/sidecar-shared-options/` and the status line says
   what landed, including the one operator-visible change.

## Risks

- **The move is not as neutral as it looks.** Configuration binding over an inherited property is
  the assumption phase 1 rests on. The existing direct-construction tests are the check, and phase 1
  does not proceed to phase 2 until they pass unedited.
- **The package sort moves every vertex id in a committed sample.** Phase 6 treats that as an
  expected outcome to be verified and committed, not as a surprise to be discovered later.
- **The service rename is operator-visible.** It is a defect fix, it is stated in the record and in
  the docs, and the alternative (widening the dashboard regex) keeps a fork that a future fifth
  deployable would have to copy.
