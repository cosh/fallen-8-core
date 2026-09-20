# Sidecar shared options - Specification

> **Status:** Open, spec and plan only at the time of writing. One branch,
> `feature/sidecar-shared-options`, carrying three pieces of work that share no code but do share a
> cause: the same thing written down in more than one place. Follow the feature workflow in
> [CLAUDE.md](../../../CLAUDE.md).

## 1. Summary

Three deployables sit beside a Fallen-8 and reach it only over its public HTTP contract:
`fallen-8-mcp`, `fallen-8-integrations` and `fallen-8-agents`. They already share the behavioural
seam (`fallen-8-rest-client`). They do **not** share the three option families every one of them
needs, so each carries its own copy: the target it points at, the tenant and instance identity it
declares, and the OTLP endpoint it pushes to. The copies are not variations on a theme. The
identity one is byte-identical across all three but for a twelve-character string, and the OTLP one
is byte-identical outright.

This feature gives those three families one home, and takes the two defects the third copy had
drifted into with it. It also clears two smaller pieces of the same kind: a feature record filed
under `open/` that landed three weeks ago, and a generated sample that rewrites 11,600 lines of
committed history every time CI looks at it, whether or not anything changed.

Nothing here changes a configuration key, a section name, a default, or anything an operator
writes. An operator's `appsettings.json` and every `F8_*` compose variable mean exactly what they
meant before. That is the constraint the design is built around, not a hoped-for outcome.

## 2. Why the copies exist, and why that reason does not cover them

The target-options copy is **argued for** in the code, and the argument is good as far as it goes:

> Section name and shape match `fallen-8-mcp`'s and `fallen-8-integrations`' as a small copied
> options class: an operator configuring three sidecars should not learn three spellings, while the
> configuration SHAPE stays each deployable's own so one may gain a knob the others have no use for.
> - `fallen-8-agents/Configuration/Fallen8TargetOptions.cs`

Both halves of that are worth keeping. The shapes really do differ, and each difference is earned:
`fallen-8-mcp` has a `TlsInsecure` lab escape hatch the other two deliberately refuse;
`fallen-8-integrations` has a `DefaultNamespace` and an `EmbedConcurrency` that mean nothing to a
host that writes no graph; `fallen-8-agents` has a `Deadline` the other two lack.

What the argument does not cover is the part that is the same in all three: a base URL, an API key,
the header it travels under, and a per-request deadline in seconds. Nor does it cover the identity
and OTLP families, where nothing differs and no comment claims anything does. **A shared base class
with a derived class per deployable keeps every property of the argument** - one spelling for the
operator, each deployable's own shape, room for a knob the others have no use for - and removes the
copy. That is the design.

## 3. What lives where, and the rule that decides it

`fallen-8-rest-client` is the one home for what these three share, and its project file carries a
rule that is load-bearing here:

> Deliberately NO ProjectReference and no PackageReference. EVERY consumer may reach a Fallen-8 only
> over its public HTTP contract, so this library must not be able to hand any of them the engine or
> the API app; everything it needs is in the shared framework.

All three option families are plain objects. The target and OTLP ones need `System`; the identity
one needs `System.Collections.Generic` for the resource-attribute list. **So all three move with no
new dependency at the seam and the rule stands untouched.**

The same rule decides what does *not* move. The OTLP **wiring** - the `AddOpenTelemetry`,
`WithMetrics`, `WithTracing`, `WithLogging` block - is about 40 lines in each deployable and about
half of it is identical between `fallen-8-mcp` and `fallen-8-agents`. Extracting it would require
the five OpenTelemetry packages at the seam, which every consumer would then take transitively.
**That is a deliberate non-goal of this feature**, recorded here rather than left as an apparent
oversight: the seam's freedom from dependencies is worth more than 20 lines, and the wiring's
differences (which meter, which activity source, whether the framework's own GenAI spans are
registered) are real per-deployable choices rather than accidents. What this feature does instead is
make the three wiring blocks *agree* where they were silently disagreeing, which is section 5.

### 3.1 The new types

Four files in `fallen-8-rest-client/Configuration/`, named with the `A` prefix the engine already
uses for an abstract base (`AGraphElementModel`, `ABucketIndex`, `ATransaction`):

| Type | Holds | Supplied by the derived class |
|---|---|---|
| `OptionBounds` | the floor and the ceiling on a configured seconds value | nothing (static) |
| `AFallen8TargetOptions` | `BaseUrl`, `ApiKey`, `ApiKeyHeader`, `TimeoutSeconds`, `Deadline` | the default timeout, through a protected constructor |
| `AFleetIdentityOptions` | `Tenant`, `Instance`, `ResourceAttributes()` | the instance-id prefix, through a protected constructor |
| `AFleetObservabilityOptions` | `Otlp`, `OtlpEnabled` | nothing |

plus two small shared types the nested copies become: `IdentityLevel` (an id and a name) and
`OtlpOptions` (an endpoint). Both were nested classes in three places; they are now one top-level
type each. Configuration binding is by property name, not by type name, so
`Mcp:Identity:Tenant:Id` and `Agents:Observability:Otlp:Endpoint` bind exactly as before.

`SectionName` stays on each derived class, because the section name is the one thing that genuinely
differs per deployable (`Mcp:Identity`, `Integrations:Identity`, `Agents:Identity`) and it is what
an operator writes.

`OptionBounds` moves out of `fallen-8-agents`, where it was `internal`, and becomes `public` at the
seam. It is the one home for a rule the other two deployables had not learned yet, which is the
first of the two defects.

## 4. Defect 1: two hosts crash on a large deadline instead of clamping it

`OptionBounds` exists in the agent host because of a measured incident recorded on the type itself:
`CancelAfter` and `PeriodicTimer` both refuse a delay past `Int32.MaxValue` **milliseconds**, so a
large configured seconds value threw `ArgumentOutOfRangeException` naming a *parameter* rather than
the *setting* an operator could act on. The fix was a clamp with both a floor and a ceiling.

The other two deployables learned only the floor:

- `fallen-8-mcp/Hosting/McpHost.cs:90` - `client.Timeout = TimeSpan.FromSeconds(Math.Max(1, target.TimeoutSeconds));`
- `fallen-8-integrations/Run/GraphTargetFactory.cs:83` - `Timeout = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)),`

`HttpClient.Timeout` has the same ceiling as the timers do: it accepts `InfiniteTimeSpan` or a
positive span up to `Int32.MaxValue` milliseconds, and throws
`ArgumentOutOfRangeException` for anything larger. So `Fallen8Target:TimeoutSeconds` set to a large
number - which is how an operator asks for "effectively no deadline", and which the floor comment
invites by explaining only the low end - takes the host down at startup with an exception naming
`value`. Both sites become `target.Deadline`, which is the clamp.

**This is asserted here and proved by a test, not by reading.** The test sets a large value and
asserts the deployable starts and the deadline is the clamped one; before the fix it fails with the
exception above. Deleting the clamp must bring it back.

## 5. Defect 2: the integrations runtime's telemetry describes a different service than it says

Three wiring blocks, three different answers to two questions that should have one each.

**`service.instance.id`.** `fallen-8-mcp` and `fallen-8-agents` both pass the resolved instance id
into `AddService(..., serviceInstanceId:)`, each with a comment saying why: otherwise the SDK mints
a random per-process GUID and the promoted label churns on every restart.
`fallen-8-integrations` does not pass it, so its panels churn. The fix is the one line the other two
have, and the shared `ResourceAttributes()` is what makes the value available in one form to all
three.

**The resource that logs carry.** `fallen-8-mcp` and `fallen-8-agents` use `otel.WithLogging(...)`,
which shares the resource configured by `ConfigureResource` above it, so all three signals describe
one service. `fallen-8-integrations` instead calls `services.AddLogging(... AddOpenTelemetry(...))`
and builds a **second** resource inside it with `ResourceBuilder.CreateDefault()`. Its logs
therefore carry a resource assembled separately from the one its metrics and traces carry: same four
identity attributes, but no `service.instance.id` at all and whatever `CreateDefault()` contributes.
Aligning it on `WithLogging` gives it one resource, like the other two, and removes the second
`AddService`/`AddAttributes` call.

There is one thing deliberately **left** as it is, because changing it would be the kind of silent
break this feature is supposed to be the opposite of: `IncludeFormattedMessage` and
`IncludeScopes`, which the integrations log block sets and the other two do not. Those change what a
log record contains rather than what service it is attributed to. They are preserved by configuring
`WithLogging`'s options, not dropped.

## 5.1 Defect 3: the shipped dashboard cannot see the integrations runtime's logs

The four service names are `fallen8` (apiApp), `fallen8-mcp`, `fallen8-agents` and
`fallen-8-integrations`. The last one is the odd spelling, and the first instinct is to leave it
alone: a `service.name` is the primary key an operator's routing, dashboards and alerts are written
against, so renaming one is not a cosmetic act. That instinct was the first version of this section,
and it was **wrong**, because this repository ships a dashboard that keys on it:

    observability/grafana/dashboards/per-tenant.json, panel "Logs (Loki)"
    {service_name=~"fallen8.*"} | fallen8_instance_id=~"$instance"

Loki's regular expressions are fully anchored, so `fallen8.*` matches `fallen8`, `fallen8-mcp` and
`fallen8-agents` and does **not** match `fallen-8-integrations`. The panel's own description is
"Logs scoped to the selected instance". It silently shows every service on that instance except the
integrations runtime, and it has done so since the runtime landed. The convention the regex encodes
is written down in `observability/loki/loki-config.yaml`, which states that "the dashboards select
streams by `{service_name=~"fallen8.*"}`".

So the odd spelling is not an inconsistency somebody might one day tidy; it is the cause of a
shipped observability gap. The name is aligned to **`fallen8-integrations`** and the regex is left
exactly as it is, because the regex is the convention and the name is the thing that departed from
it.

This is the one operator-visible change on this branch, and it earns a line in the feature record
and in the docs rather than a silent edit: an operator who wrote their own panel against
`fallen-8-integrations` must repoint it, and in exchange the dashboard this repository ships starts
telling them the truth.

**Pinned by a test that derives its expectation from the consumer.** Rather than asserting the four
names against a list in the test, the new test reads the selector out of
`per-tenant.json` itself, turns it into a .NET regex, and asserts that every `AddService("...")`
literal in the apiApp and the three sidecars matches it. A fifth deployable whose name misses the
dashboard therefore fails the suite instead of quietly missing the panel, and a deliberate change to
the convention has to change the dashboard first, which is the right order.

## 6. The gate that keeps the copies from coming back

A convention test in `fallen-8-unittest/CodeQualityTest.cs`, **derived rather than listed**: it
enumerates every class, record, interface and enum declared in the four REST-only projects and fails
when one simple name is declared in more than one of them. A hand-maintained list of the types that
happen to be duplicated today would pass the moment a fifth copy of something else appeared, which
is exactly the failure it is meant to catch.

One allowlisted name, with its reason: `Program`, which the web SDK requires per assembly.

The test is mutation-checked both ways round: with the duplicate types restored it must fail and name
them, and it must pass on the finished tree without the allowlist doing the work.

## 7. The two smaller items on the same branch

**A feature record filed as pending that is not.**
`features/open/integration-embed-concurrency/` landed on 2026-09-02 in merge `7a42dc85` and never
moved. `features/open/` means pending work only, so it moves to `features/done/`.

`features/open/platform-integrity-audit/` is **correctly** filed and is not touched: its status line
names W7, W8 and the P1 remainder as still pending. This is a correction of the review that produced
this branch, which listed it as drift.

`cleanup-report.md` has sat at the repository root since July as a point-in-time report.
`features/done/docs-site/spec.md` already calls it "a transient artifact, left as-is". It moves to
`features/done/cleanup-report-2026-07/report.md`, where every other point-in-time report lives, and
the three documents that cite it are repointed.

`CLAUDE.md` says the suite takes about 90 seconds. Measured on this tree it takes 6 minutes 2
seconds for 2820 tests. A wrong number in the one file every contributor reads first is worth the
one-line fix.

`docs/src/content/docs/debugging.md` introduces the MCP server as "a fourth project and a separate
process that no launch config covers" and lists its compose port. The integrations runtime and the
agent host are the fifth and sixth and appear nowhere on the page. Both get a row in the ports table
and a mention beside the MCP one, including the fact that neither publishes a host port.

**A generated sample that rewrites its own history.** `.github/workflows/refresh-sbom.yml` refetches
Fallen-8's own SBOM, rebuilds the `fallen8-deps` sample from it, and commits when the bytes differ.
The bytes always differ: GitHub's dependency-graph endpoint returns a fresh `documentNamespace`
UUID, a fresh `creationInfo.created` timestamp, a `creators` entry carrying the generator's own build
id, and the 1086 packages in no stable order. Ten commits this month each rewrote about 11,600 lines
of two committed files; the most recent one changed four packages of 1086.

The fix is in the generator rather than the workflow. `fallen8Deps.ts` canonicalizes the fetched
document before writing it - `packages` sorted by `SPDXID`, `relationships` sorted by
`(spdxElementId, relatedSpdxElement, relationshipType)`, both of which SPDX defines as unordered
sets - and then compares it against the committed copy with the three volatile document fields
excluded. Unchanged content leaves the file untouched and says so; changed content is written whole,
including its fresh metadata, so the timestamp keeps meaning "when this SBOM last actually changed"
rather than "when CI last looked". The workflow's existing `git status --porcelain` guard then does
the right thing with no change to the workflow itself.

Sorting the packages also stabilises the **derived** file: `sbomToGraph` assigns vertex ids by
package position and its own comment says "packages keep their SBOM order for stable ids", which was
true of the transform and false of its input.

## 8. Non-goals

- **The OTLP wiring is not extracted.** Section 3 gives the reason: the seam's freedom from package
  references is worth more than the 20 shared lines.
- **No configuration key, section name, default or compose variable changes.** Including
  `Fallen8Target:TimeoutSeconds`' three different defaults (330, 330, 630), each of which keeps the
  long comment explaining what it sits above and why. The one operator-visible change on the branch
  is the integrations runtime's `service.name` (section 5.1), which is a defect fix rather than a
  configuration change: it is a literal in wiring code, not something an operator sets.
- **The dashboard's selector is not widened.** Making the regex tolerate both spellings would fix
  the panel and keep the fork; section 5.1 has the reasoning.
- **No new project.** A fourth shared library named for the sidecars would be the other way to do
  this; the existing seam takes all three families with no new dependency, so a fourth csproj, sln
  entry and three project references would buy nothing.
- **The engine is untouched**, so the browser probe's verdict cannot change. It is run anyway,
  because a green unit suite says nothing about that host.

## 9. Impact on existing features

| Layer or feature | Impact | Action |
|---|---|---|
| Engine (`fallen-8-core`) | none: not referenced, not read | none |
| REST contract and OpenAPI snapshot | none: no controller, route or XML doc changes | snapshot re-checked, expected clean |
| `fallen-8-mcp` | target options derive; the deadline clamp fixes defect 1; `McpOptions` composition unchanged | rebuild, MCP contract and coverage tests re-run |
| `fallen-8-integrations` | target options derive; the deadline clamp fixes defect 1; observability aligned per defect 2 | provider-descriptor snapshot re-checked, expected clean |
| `fallen-8-agents` | target options derive; `OptionBounds` moves to the seam and `AgentsOptions.KeepAlive` keeps using it; the one-route pin is unaffected | posture and runtime tests re-run |
| `fallen-8-rest-client` | gains four files and stays dependency-free | its own rule re-asserted by the existing test |
| F8 Studio | none: no client, type or screen touched | Studio suite and tsc run anyway |
| NL-assist dataset and eval | none: no REST surface change, so no retrain entry | none |
| Docs site | `debugging.md` gains two rows and a sentence | docs build with link validation |
| Architecture diagrams | none: no new deployable, no new channel, no changed layer | re-read to confirm, no edit expected |
| `observability` dashboards | **the per-tenant "Logs (Loki)" panel starts including the integrations runtime** (defect 3), and its `service.instance.id` stops churning across restarts (defect 2) | both are fixes to documented intent; the new service-name test derives its expectation from the dashboard itself |
| `observability` docs page | names no service, so nothing there is stale | re-read to confirm; the renamed service is recorded on the integrations docs page instead |
| Persisted recipes and stored queries | none | none |
| `sample-graphs` feature | the committed SBOM and the derived `fallen8-deps.jsonl` become stable under an unchanged dependency set; the sample's content is unchanged | sample rebuild compared byte-for-byte against the committed copy |
| CI | no workflow file changes; `refresh-sbom` commits only on a real change | reasoned in section 7 |

## 10. Verification

Every gate in CLAUDE.md, plus the two that are specific to this branch:

1. `dotnet build` clean, warnings-as-errors on.
2. `dotnet test` green, with the new convention test and the two defect tests mutation-checked.
3. `npx tsc -b` and `npm test` in `fallen-8-web-ui`.
4. The new canonicaliser test: a shuffled document canonicalises to identical bytes, and a genuinely
   changed package set still produces a write.
5. A real rebuild of the `fallen8-deps` sample from the committed SBOM, compared against the
   committed `.jsonl` byte for byte, which is what proves the sort did not change the sample.
6. Docs build with link validation.
7. `docker compose config -q` on the base file and every overlay.
8. The browser probe, because the rule is to run it rather than to reason about whether it applies.
9. An adversarial review of the diff and the cross-feature sweep of section 9, both delegated and
   both verified rather than believed.
