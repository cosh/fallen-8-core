# Sidecar shared options - the review, and what it found

The record of the adversarial review of `feature/sidecar-shared-options`, kept here rather than in
the spec because [CLAUDE.md](../../../CLAUDE.md) makes the spec and plan historical documents and
this is what happened to them afterwards. Corrections to false claims still went into the spec in
place, each marked as a correction, because a false sentence left standing is a defect wherever it
lives.

## How it was run, and what that cost

Seven dimensions in parallel over `git diff main...HEAD`, each finding then handed to three
independent skeptics with distinct lenses (is the technical claim true, is it already handled, is
it overstated), majority refutation killing it. 163 agents, 52 candidates, **24 survived and 28
were refuted**.

| Dimension | Candidates | Survived |
|---|---|---|
| configuration binding | 9 | 3 |
| the deadline clamp | 8 | 3 |
| the OTel wiring | 6 | 5 |
| the three gates | 9 | 5 |
| the SBOM canonicaliser | 5 | 0 |
| truthfulness of every claim | 10 | 6 |
| cross-feature impact | 5 | 2 |

**The most valuable single result is that the three new gates could not fail the way the spec said
they could**, and the reviewers proved it rather than arguing it: they mutated the tree five times
and watched the suite stay green each time. A gate that cannot fail is worse than no gate, because
it is also a claim.

## The clusters, and what each was fixed with

### 1. The gates (5 findings, 4 Major)

Every one of these was mutation-proven by the reviewer and is now mutation-proven fixed.

| What was wrong | The mutant that passed | Now |
|---|---|---|
| All three gates enumerated a hardcoded project list, so a NEW deployable was outside their scan - the one case they advertised catching | a fourth sidecar copying `Fallen8TargetOptions` | the project list is derived from the build graph (any csproj referencing the seam); the derivation gate is a TEXT sweep because a new project cannot be reflected over at all |
| The copy gate keyed on the type NAME plus an exactly equal member set, so it would not have caught the triplication it was written for | restoring the pre-branch `*IdentityOptions` | a NAME rule (a consumer may not re-declare a name the seam owns) beside a SHAPE rule (no two consumers declare the same four-plus members), so a renamed copy is caught |
| The copy gate never scanned the seam, so re-forking a seam type into one sidecar was invisible | re-adding `fallen-8-mcp/Configuration/OptionBounds.cs` | the seam is in scope, and the name rule catches it at any size (this one has two members, below any useful shape threshold) |
| The service-name gate asserted a TOTAL of four literals, never one per project | deleting the MCP registration and adding a second elsewhere | one registration required per OpenTelemetry-wiring project, derived by looking for the call |
| It read only the FIRST `service_name` selector in the dashboard | inserting a laxer `{service_name=~".+"}` panel first | every literal selector is checked; Grafana template variables are skipped as untestable rather than failed on |

Two things found while fixing them, neither reported: the gate's line numbers counted only
non-comment lines, so every citation was off by the length of the licence header and pointed at
nothing an operator could open; and the derived project sweep walked into `fallen-8-web-ui`, whose
embed fixture leaves a recursive `node_modules` link that throws `PathTooLongException`. The sweep
is now gated on a csproj being present.

**The shape threshold is measured, not chosen.** At three members the only cross-assembly matches
on this tree are coincidences: the two `Program` entry points (same member names; `TransportBound`
holds 832 MiB in one and 2 MiB in the other), and `UnifiSite` against `IdentityLevel`, both
`{Id, Name}`. At four there are none. A three-member renamed copy therefore slips, and the code
says so rather than carrying an allowlist that grows until it proves nothing.

### 2. Facts this branch asserted and got wrong (3 findings)

- **`OptionBounds` stated a false limit**, and it was inherited from the agent host rather than
  invented here: the comment said `CancelAfter`, `PeriodicTimer` and `HttpClient.Timeout` all refuse
  a delay past `Int32.MaxValue` ms. Measured on net10.0: `HttpClient` stops at 2,147,483 s, both
  timers accept 4,294,967 s. The clamp value was right for the wrong reason (it is the tightest of
  the three). The doc now gives all three numbers and a test arms all three APIs with the clamp, so
  loosening it fails on `HttpClient`.
- **"Byte-identical" was not true of any of the files.** The identity family differs in class name,
  namespace, `SectionName`, four lines of doc comment and the prefix; the prefixes are 7, 16 and 10
  characters, not the twelve claimed (twelve is the GUID slice, which IS identical). And one of the
  six classes did carry a deployable-specific note worth keeping.
- **`SectionName` does not stay on each derived class**, which the spec said flatly. It does for
  identity and observability; the target family's section is `Fallen8Target` in all three, so it
  lives on the base. Wrong for a third of the change it describes.

### 3. Symptoms described wrongly (3 findings)

- **Neither deadline site failed "at startup"**, and where they do fail is worse. The reviewer
  reverted both fixes and read the stack traces: `fallen-8-mcp` throws from
  `DefaultHttpClientFactory.CreateClient`, so the container comes up healthy and fails every
  bridged tool call; `fallen-8-integrations` throws from `GraphTargetFactory.Create`, called once
  per job at the line `JobRunner` marks as the point of no return, AFTER a source read that can
  take hours. A host that refuses to start would have been the kinder failure.
- **"Logs built a SECOND, differently-attributed resource" was overstated.** Measured, the two
  resources came out attribute-for-attribute identical. Sharing one removes a second place the same
  four attributes must be assembled, which is a divergence waiting rather than a divergence.
- **"Its panels churn" is not supported by anything shipped.** No dashboard in this repository
  selects on `service.instance.id` or its promoted label, so no panel was wrong; what churned was
  the promoted label itself, which an operator's own queries and any grouping by it would see move
  on every restart.

### 4. Things that were not documented, or not pinned (4 findings)

- **The one operator-visible change was documented nowhere**, while the spec's impact table claimed
  it was recorded on a docs page. Three dimensions found this independently, which is the signal
  that it was the branch's largest hole. The observability page was ALSO describing two OTLP
  producers when there are four, omitting the integrations runtime and the agent host entirely. It
  now carries a producer table with all four service names, says which one changed, and says what
  an operator with their own panel must repoint.
- **Defect 2's actual fix was pinned by no test.** It now is, at the level an operator sees: two
  tests resolve the BUILT OpenTelemetry resource and assert `service.instance.id` is the configured
  id and `service.name` matches the dashboard's selector. Dropping `serviceInstanceId` again fails
  with the random GUID quoted in the message.
- **The spec described an API that was never built**: `AFleetIdentityOptions.ResourceAttributes()`,
  which is `Resolve()` returning a `FleetIdentity`. A contributor following the record would not
  have compiled, and the third new public type was missing from the table that counted two.
- **`IncludeScopes = true` was inert**, carried over from a registration where it had no effect
  either: `RedactingLoggerProvider` replaces each `ILoggerProvider` descriptor and implements only
  `ILoggerProvider`, so the logging factory never hands the OTel provider a scope provider. Making
  the wrap forward scopes would be the wrong fix, because a scope value reaches the exporter without
  passing through redaction and a credential is exactly what a scope carries. The flag is gone and
  the reason is written where it was.

### 5. The defect the branch had not swept (1 finding, Major as reported)

`Agents:Mcp:ConnectTimeoutSeconds` had the floor and not the ceiling, in the project the clamp came
from, in four copies of `Math.Max(1, ...)`, one of them arming `CancelAfter` - the very API the
original incident was about. All four now read one clamped `Connect` property.

The verifiers downgraded this to Minor and they were right about the severity: unlike the two sites
the branch fixed, the throw is caught, logged with the exception and surfaced on `GET /agent/status`.
They were also right that it is pre-existing rather than introduced here. What survives the
downgrade is that the warning MISDIRECTS, telling the operator to fix the endpoint or the server
when an unreachable-looking MCP server is in fact up, and that a review of a deduplication branch is
exactly where the last copy of a clamp should have been found.

## Three findings the reviewers REFUTED that were true anyway

Worth recording, because it cuts against the obvious lesson. A 2-of-3 refutation is thin, and
re-checking the cheap ones by hand found three real defects the panel had dismissed:

1. **`SectionName` on the base** (2/3 refuted). Verified by reading the file: it is on
   `AFallen8TargetOptions`. Fixed above.
2. **A pointer to `features/done/sidecar-shared-options/`** from the SBOM generator's comment
   (3/3 refuted, presumably because the feature will move there). The path did not exist when
   written, so the comment was broken on the day it landed. Now a path-free reference.
3. **`mcp-server.md` documenting the deadline as floor-only** (3/3 refuted). It said "floored at
   `1`" with no ceiling, which this branch made incomplete. The same false timer limit was on
   `agents.md`, together with a count ("the two durations") that this branch made stale by clamping
   a third. Both corrected.

So: read the verdicts, and then spot-check the cheap refutations. The panel is a filter, not an
oracle.

## Known gaps, stated rather than implied

- **The no-write path of the SBOM refresh is covered by no test.** `sbomContentEquals` and
  `canonicalizeSbom` are covered thoroughly; the `loadSbom` decision that USES them reads and writes
  real paths through module-level constants, so testing it means refactoring the seam. The reviewers
  raised it and a majority refuted it; the gap is real and small, and the property that matters was
  verified by hand instead (a rebuild after the change is byte-identical, twice).
- **A three-member renamed copy passes the copy gate**, by the measured choice above.
- **The apiApp's `Fallen8Identity` is a fourth instance of the identity concept** and is
  deliberately not folded in; spec section 7.2 has the reasoning and the revisit trigger.
