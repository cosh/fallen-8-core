# Sidecar shared options - Specification

> **Status:** IMPLEMENTED on `feature/sidecar-shared-options`, and reviewed. One branch carrying
> three pieces of work that share no code but do share a cause: the same thing written down in more
> than one place. Follow the feature workflow in [CLAUDE.md](../../../CLAUDE.md).
>
> **Three defects fell out of the deduplication, and the third was found by this spec being wrong.**
> Defect 1 (two hosts throwing on a large deadline) and defect 2 (the integrations runtime's
> telemetry misdescribing itself) were visible from reading the three copies side by side. Defect 3
> was not: this spec's first version argued that the odd `service.name` spelling should be left
> alone because nothing in the repository keyed on it. Something did - the shipped Grafana dashboard
> - and section 5.1 records both the finding and the reversal.
>
> **A delegated adversarial review then found 24 more things, and most of them were claims in this
> document.** Its most useful result was that all three new gates could not fail the way section 6
> originally described, proven by five mutations that passed; they are rebuilt and every one of
> those mutants is now killed. It also measured two facts this spec had asserted and got wrong (the
> ceiling of `CancelAfter` and `PeriodicTimer`, and how alike the copies were), found one defect the
> branch had not swept (a fourth unclamped site in the project the clamp came from), and found that
> the one operator-visible change was documented nowhere while section 9 claimed it was. Each
> correction is marked in place rather than quietly applied, because a spec that agrees with the
> code afterwards is not a record of anything.

## 1. Summary

Three deployables sit beside a Fallen-8 and reach it only over its public HTTP contract:
`fallen-8-mcp`, `fallen-8-integrations` and `fallen-8-agents`. They already share the behavioural
seam (`fallen-8-rest-client`). They do **not** share the three option families every one of them
needs, so each carries its own copy: the target it points at, the tenant and instance identity it
declares, and the OTLP endpoint it pushes to.

**How alike the copies are, stated precisely**, because an earlier draft of this paragraph called
two of them "byte-identical" and a review checked. The identity family differs in class name,
namespace, the `SectionName` value, four lines of doc comment, and the prefix an auto-filled
instance id carries (`f8-mcp-`, `f8-integrations-`, `f8-agents-`, which are 7, 16 and 10 characters;
the twelve in the earlier draft was the GUID slice, which IS identical in all three). Its
`ResourceAttributes()` body was identical but for that prefix. The OTLP family differs in the same
first four ways and its one property is identical. So: identical in substance, and not in bytes.

This feature gives those three families one home, and takes the two defects the third copy had
drifted into with it. It also clears two smaller pieces of the same kind: a feature record filed
under `open/` that landed three weeks ago, and a generated sample that rewrites 11,600 lines of
committed history every time CI looks at it, whether or not anything changed.

Nothing here changes a configuration key, a section name, a default, or anything an operator
writes. An operator's `appsettings.json` and every `F8_*` compose variable mean exactly what they
meant before. That is the constraint the design is built around, not a hoped-for outcome.

**One thing IS operator-visible, and that sentence does not cover it.** The integrations runtime's
OpenTelemetry `service.name` changes from `fallen-8-integrations` to `fallen8-integrations`. It is
a literal in wiring code rather than a setting, and changing it fixes a shipped defect rather than
following from the refactor - but an operator whose own dashboard keys on the old spelling has to
repoint it. Section 5.1 is the whole story, and it is stated here so that section 1 is not read as
a promise the branch does not keep.

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
the header it travels under, and a per-request deadline in seconds. It covers the identity and OTLP families
less well than it looks, too: their substance is the same in all three, and one of the six classes
DOES carry a deployable-specific note worth keeping (the integrations OTLP one, on log export
running behind the credential redaction wrap). That note is why the derived classes keep their own
summaries rather than becoming empty shells. **A shared base class
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
| `AFleetIdentityOptions` | `Tenant`, `Instance`, `Resolve()` | the instance-id prefix, through a protected constructor |
| `AFleetObservabilityOptions` | `Otlp`, `OtlpEnabled` | nothing |

plus three shared top-level types. Two are the nested copies promoted: `IdentityLevel` (an id and
a name) and `OtlpOptions` (an endpoint), each previously nested in three places. The third is new:
`Resolve()` returns a **`FleetIdentity`** carrying `TenantId`, `TenantName`, `InstanceId`,
`InstanceName`, an `Attributes()` list and the four `*Key` constants that are the wire names the
collector promotes and the Grafana panels join on. It exists because reading the instance id back
out of the attribute LIST was itself the third copy of a line
(`attributes.First(kv => kv.Key == "fallen8.instance.id")`) and because that value is needed on its
own, as OTel's `service.instance.id`. It deliberately mirrors the apiApp's own `Fallen8Identity`,
which has had that shape from the start (section 7.2). Configuration binding is by property name, not by type name, so
`Mcp:Identity:Tenant:Id` and `Agents:Observability:Otlp:Endpoint` bind exactly as before.

`SectionName` follows the section, which is not the same answer for all three families. The identity
and observability ones differ per deployable (`Mcp:Identity`, `Integrations:Identity`,
`Agents:Identity`), so each derived class keeps its own. The TARGET one is `Fallen8Target` in all
three, which is the whole point of that family, so it lives on the base and there is one home for
it. An earlier draft of this paragraph said it stays on each derived class, full stop, which was
wrong for a third of the change it describes.

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

`HttpClient.Timeout` has a ceiling too, and a LOWER one than the timers: measured on net10.0 it
accepts `InfiniteTimeSpan` or a positive span up to `Int32.MaxValue` milliseconds (2,147,483 s),
while `CancelAfter` and `PeriodicTimer` go to 4,294,967 s. So `Fallen8Target:TimeoutSeconds` set to
a large number - which is how an operator asks for "effectively no deadline", and which the floor
comment invites by explaining only the low end - throws `ArgumentOutOfRangeException` naming
`value`. Both sites become `target.Deadline`, which is the clamp.

**Neither site fails at startup, and where they DO fail is worse.** An earlier draft of this
section said "takes the host down at startup"; a review reverted both fixes and read the stack
traces. In `fallen-8-mcp` the assignment runs inside the named client's configure delegate, so it
throws from `DefaultHttpClientFactory.CreateClient` on every bridged tool call: the container comes
up, answers `/health`, reports green, and then fails everything. In `fallen-8-integrations`
`GraphTargetFactory.Create` runs once per job from the line `JobRunner` marks as the point of no
return, AFTER the source read that can take hours - so an operator lost the whole read, every run,
from a healthy-looking container. A host that will not start is the kinder failure; neither of these
was that.

**This is asserted here and proved by a test, not by reading.** Each test builds the client the way
its deployable does and asserts the resulting deadline is the clamped one; before the fix each
failed with the exception above, which is where this section's quotation of it comes from. Deleting
either clamp brings it back. (The tests assert a `TimeSpan` and start no host, which is the right
scope for what the fix changes.)

## 5. Defect 2: the integrations runtime's telemetry describes a different service than it says

Three wiring blocks, three different answers to two questions that should have one each.

**`service.instance.id`.** `fallen-8-mcp` and `fallen-8-agents` both pass the resolved instance id
into `AddService(..., serviceInstanceId:)`, each with a comment saying why: otherwise the SDK mints
a random per-process GUID and the promoted label churns on every restart.
`fallen-8-integrations` did not pass it. **What that cost is narrower than "its panels churn",**
which is what an earlier draft said: no dashboard in this repository selects on
`service.instance.id` or its promoted Prometheus label, so no shipped panel was ever wrong - every
panel filters on the `fallen8.*` attributes this runtime already stamped. What churned was the
promoted resource label itself, so an operator's own queries, and anything grouping or counting by
it, saw a new value on every restart. The fix is the one line the other two have, and
`Resolve()`'s `FleetIdentity.InstanceId` is what makes the value available in one form to all
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

## 6. The gates that keep the copies from coming back

Three convention tests in `fallen-8-unittest/CodeQualityTest.cs`. **The first version of all three
could not fail the way this section originally claimed**, and a review proved it by mutating the
tree five times and watching them pass. What follows is what shipped after that.

**The project list is derived from the build graph**, not listed: any project whose csproj takes a
`ProjectReference` on the seam. The first version hardcoded the three sidecars, so the one change
the gates advertised catching - a new deployable that copies instead of deriving - was outside their
scan entirely.

1. **Each consumer's target/identity/observability options derive from the seam's base.** A TEXT
   sweep, deliberately: a brand-new project is not referenced by the test assembly and so cannot be
   reflected over at all, which is exactly the case that has to fail. Restoring the pre-branch
   triplication now fails it on all three copies with correct line numbers.
2. **No type is copied between the seam and its consumers.** Two rules, because one cannot express
   both halves. A NAME rule: a consumer may not re-declare a name the seam owns, at any size, which
   is what catches a re-forked `OptionBounds` (two members, below any useful shape threshold) and
   what the first version missed because only one project declared the name. And a SHAPE rule: no
   two consumers declare the same set of four-plus members, which catches a RENAMED copy - the case
   the first version missed for every one of the nine classes this branch de-duplicated, since it
   keyed on the type name.
   **The threshold is measured.** At three members the only cross-assembly shape matches on this
   tree are coincidences (the two `Program` entry points, and `UnifiSite` against `IdentityLevel`,
   both `{Id, Name}`); at four there are none. A three-member renamed copy therefore slips, and
   saying so beats an allowlist that grows until the gate proves nothing.
3. **Every OpenTelemetry-wiring project names a service that matches every literal selector the
   shipped dashboard uses.** Per project, because asserting only a TOTAL of four literals let a
   review delete one registration, add a second elsewhere, and pass. Every selector, because reading
   only the first one let it hide a strict selector behind a lax one. Grafana template variables are
   skipped as untestable rather than failed on.

One allowlisted name, with its reason: none. `Program` needed one while the rule was name-based;
the shape rule does not flag it, because the two entry points share member names and nothing else.

Beside these, two tests read the BUILT OTel resource rather than the source, which is what pins
defects 2 and 3 at the level an operator sees: the resolved instance id really is
`service.instance.id`, and the declared `service.name` really does match the dashboard.

**Every one of the five mutants the review used is now a killed mutant**, each naming the defect: a
fourth sidecar copying the options class; a re-forked `OptionBounds`; a renamed `RunSpool` copy; a
deleted service registration masked by a second one; and a laxer dashboard panel inserted first.

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

## 7.1 What the implementation changed about section 7

Two of the bookkeeping items were **not** done as written, and both are corrections to the review
that produced this branch rather than changes of mind about the goal.

- **`platform-integrity-audit` stays under `features/open/`.** Its status line names W7, W8 and the
  P1 remainder as pending, so `open/` is exactly right. The review called it drift and was wrong.
- **`cleanup-report.md` stays at the repository root.** All 71 of its citations are paths relative
  to the root, so moving it would break every one, and `features/done/docs-site/spec.md` had
  already recorded the decision to leave it. What was actually wrong is that a July snapshot at
  the root reads as a current defect list, so it now carries a banner saying what it is, naming
  two findings since acted on and pointing at the three live records. Its two links to the
  pre-Starlight docs path are repointed; its two links to files that no longer exist are left,
  because the file being gone IS the disposition.

## 7.2 The fourth copy, considered and deferred

The apiApp has its own `Fallen8Identity`, which resolves the same four values with the same
defaults and yields the same four resource attributes. It is a fourth instance of this concept and
it is **deliberately not** folded into the seam: the apiApp is the server the three sidecars talk
TO, and having it depend on their shared REST-client library would point the dependency the wrong
way down the architecture. The seam's own `FleetIdentity` was written to mirror that class rather
than to replace it, so the two agree in shape. The copy gate does not trip on the pair because it
scopes itself to the seam and the projects that consume it, and the apiApp is neither - which is a
scope decision rather than, as an earlier draft implied, a property of the gate's rule. Revisit if a
third thing ever needs the same resolution, since two is a coincidence and three is a pattern.

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
| `observability` docs page | **it was stale in two ways and is fixed**: it described two OTLP producers when there are four (omitting this runtime and the agent host entirely), and the rename was documented nowhere at all while this table claimed otherwise | a producer table naming all four service names, which one changed and what an operator must repoint |
| Persisted recipes and stored queries | none | none |
| `sample-graphs` feature | **a one-time renumbering landed**: every vertex id in `fallen8-deps.jsonl` moved once and the stored SBOM was sorted in place, after which repeated builds were verified byte-identical. The graph is unchanged - 1086 vertices, 1850 edges, `index.json` untouched | rebuilt, hashed twice, and the unchanged `index.json` is the check that no count moved |
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
