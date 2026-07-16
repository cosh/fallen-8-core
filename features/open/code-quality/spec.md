# Code Quality Gates & Consolidation — Specification

> **Status:** Draft. Feature branch: `feature/code-quality` (branch-only workflow — no GitHub
> issue/PR).

## 1. Overview & motivation

The 2026-07 feature sprint landed six features (stored-query-library, change-feed,
bulk-import-export, vector-index, graph-analytics, observability) fast and test-first. The
survey behind this spec found the codebase in **good** shape — every source file carries the
MIT header, the suite is 718 green tests, packages are exact-pinned — but with the debts a
sprint accumulates:

- **One build warning** (a broken `cref` in `DynamicCapabilityAuthorization.cs`) and no gate
  stopping the next one: nothing enforces the "0 warnings" bar the feature plans claim.
- **No central build configuration**: three csprojs each declare their own settings; no
  `Directory.Build.props`, no `TreatWarningsAsErrors`, analyzer level implicit.
- **Duplication left by the sprint**, all verified in the tree:
  - six copies of the in-scope adjacency walk across the five analytics algorithms
    (`CountInScope`/`Distribute`/`TallyNeighborLabels`/`CollectNeighbors` — the same
    group-filter + tombstone-skip + scope-check loop);
  - seven hand-rolled `ObjectResult` + `ProblemDetails` + `application/problem+json` blocks
    across `BulkController` and `AnalyticsController`;
  - three copies of the plugin resolve-initialize-cache-invoke flow in `Fallen8`
    (`TryCalculateShortestPath` ×2, `TryRunAnalytics`).
- **Convention knowledge that lives only in prose** (CLAUDE.md): the MIT-header rule, the
  exact-version-pin rule, and the OpenAPI snapshot regeneration procedure are enforced by
  reviewer diligence, not by the build.
- Two hygiene stragglers: a `Console.WriteLine` in the DEBUG-only
  `SerializationWriter.DumpTypeUsage`, and a `DateTime.Now` in `DateHelper` whose semantics
  are load-bearing (see §3.4 — documented, not blindly "fixed").

This feature turns the implicit quality bar into **enforced measures** (build gates +
CI-enforced convention tests, the same philosophy as `SkillLibraryTest` and
`JsonSourceGenParityTest`) and pays down the sprint's duplication — without behaviour change:
the 718-test suite is the net, and every refactor must keep it green.

## 2. Goals / non-goals

**Goals**

1. **Zero-warning builds, enforced.** Fix the one existing warning; add a repo-root
   `Directory.Build.props` with `TreatWarningsAsErrors` (NuGet audit advisories NU1901–NU1904
   stay warnings — a future CVE disclosure must not brick the build) and an explicit
   `AnalysisLevel`. From then on a warning cannot land.
2. **CI-enforced convention tests** (`CodeQualityTest`, MSTest, walking the source tree):
   - every `.cs` file in the three projects starts with the MIT license header (pins the
     current 100% state);
   - no `Console.Write*` in product code (engine + apiApp) — output goes through
     `ILogger`/`Debug` (the one existing site is fixed);
   - no `DateTime.Now` in product code outside the documented `DateHelper` allowlist (§3.4);
   - every `PackageReference` carries an exact `Version` (no floating/range versions) — the
     pin-everything rule the feature plans follow, now enforced.
3. **Consolidate the sprint's duplication** (behaviour-preserving, suite-green):
   - a shared in-scope adjacency walker for the analytics algorithms (allocation-free via a
     generic struct-visitor, so the hot loops keep their devirtualized shape);
   - a `ProblemResults` helper for problem+json responses;
   - a private generic resolve-initialize-cache-invoke helper in `Fallen8`.
4. **Automate the OpenAPI snapshot regeneration**: `scripts/update-openapi-snapshot.ps1`
   replaces the by-hand run-and-curl procedure every feature plan repeats.
5. **Document the bar** in CLAUDE.md (gates, script, conventions) so the next feature starts
   from it.

**Non-goals** (each with its revisit trigger)

- **Raising the analyzer level to `latest-recommended`/`all`.** Measured: ~3,200 findings,
  dominated by naming (CA1707: 1,422 — test method underscores are the repo's MSTest
  convention), culture-info formatting (CA1305: 520) and logging-template rules (CA1848,
  CA2254). That is churn, not quality, for a single-operator codebase. *Revisit rule-by-rule
  via a `.globalconfig` when a real bug class shows up that a specific CA rule would have
  caught — adopt that rule, fix its findings, keep it as error.*
- **Nullable reference types across the codebase.** The engine predates NRT; annotating it
  wholesale is a multi-thousand-line churn with real regression risk in the persistence and
  transaction paths. *Revisit per-file when a file is substantially rewritten anyway, or if a
  null-deref class of bugs recurs.*
- **Whole-repo `dotnet format` reformat.** It would destroy `git blame` across every file for
  cosmetic gain; the `.editorconfig` governs new/edited code. *No trigger — deliberate.*
- **Coverage thresholds / SonarQube / StyleCop.** The repo's quality mechanism is
  behaviour-pinning tests plus the council gate; a percentage threshold optimizes the metric,
  not the tests. *Revisit if a regression ships that a coverage floor would provably have
  flagged.*
- **Fixing `DateHelper.GetModificationDate`'s `DateTime.Now`.** Not mechanical: timestamps
  are seconds since a Kind-naive 1970 epoch, produced and consumed with the same local
  clock; switching to UTC shifts interpretation of already-persisted dates and can make
  `now − creation` negative for freshly created elements in UTC+ timezones (a
  `Convert.ToUInt32` overflow). *Revisit as its own small feature if cross-timezone
  deployments of one savegame become real; until then the allowlist entry documents it.*

## 3. Design sketch

### 3.1 Build gates (`Directory.Build.props`, repo root)

```xml
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
<WarningsNotAsErrors>NU1901;NU1902;NU1903;NU1904</WarningsNotAsErrors>
<AnalysisLevel>latest</AnalysisLevel>   <!-- default mode, pinned explicitly -->
```

Applies to all three projects (and any future sibling). The one existing warning — CS1574,
a `cref` to `RequireAuthenticatedUser` that XML docs cannot resolve — is fixed first. NuGet
audit warnings are deliberately excluded from errors: a new advisory should surface loudly
(it stays a warning) without breaking every developer's build the morning it is published;
the pinned-fix response stays a human decision (precedent: the Microsoft.OpenApi 2.10.0 pin).

### 3.2 Convention tests (`fallen-8-unittest/CodeQualityTest.cs`)

The tests locate the repo root by walking up from the test assembly to the `.sln` and scan
`fallen-8-core/`, `fallen-8-core-apiApp/`, `fallen-8-unittest/` (excluding `obj/`/`bin/`).
Each rule reports **every** violating file in its assert message. Comment lines (`//`) are
stripped before token checks so prose mentioning a banned token does not trip the rule.

| Rule | Scope | Current state |
|---|---|---|
| MIT header block present at file top | all `.cs` | 0 violations (pins it) |
| no `Console.Write` | product `.cs` | 1 violation → fixed (§3.4) |
| no `DateTime.Now` (allowlist: `Helper/DateHelper.cs`) | product `.cs` | 1 allowlisted |
| `PackageReference` has exact `Version` (no `*`, no floating) | all `.csproj` | 0 violations (pins it) |

### 3.3 Consolidation refactors (behaviour-preserving)

1. **Analytics adjacency walker** — `fallen-8-core/Algorithms/Analytics/AnalyticsAdjacency.cs`
   (internal static): one implementation of the induced-subgraph walk (group filter by
   edge-property-id, tombstoned-edge skip, endpoint scope lookup) consumed by all five
   algorithms through a generic **struct visitor**
   (`Visit<TVisitor>(...) where TVisitor : struct, INeighborVisitor` with
   `void OnNeighbor(int denseIndex)` / plus a counting overload). The struct-generic keeps
   the JIT specializing and inlining per call site — no delegate, no interface dispatch on
   the hot path — so the refactor is shape-preserving, not just behaviour-preserving. The
   six duplicated private walkers are deleted; the algorithms keep their per-vertex loops,
   budget checks and math untouched.
2. **`ProblemResults`** — `fallen-8-core-apiApp/Helper/ProblemResults.cs` (internal static):
   `Create(int status, string title, string detail, Action<ProblemDetails> extend = null)`
   returning the `ObjectResult` with the `application/problem+json` content type. The seven
   duplicated blocks become one-liners; response bytes stay identical (pinned by the existing
   endpoint tests asserting status + content type + detail fragments).
3. **`Fallen8` plugin resolution** — a private
   `TryResolveCachedPlugin<T>(IMemoryCache cache, string name, Action<T> register, out T)`
   (or equivalent) used by `TryCalculateShortestPath` (string overload) and
   `TryRunAnalytics`; the generic `TryCalculateShortestPath<T>` keeps its distinct
   Activator-based shape. Resolve-initialize-cache order unchanged.

### 3.4 Hygiene fixes

- `DynamicCapabilityAuthorization.cs`: the unresolvable `cref` becomes plain code-formatted
  text (`<c>RequireAuthenticatedUser</c>`) — the CS1574 fix.
- `SerializationWriter.DumpTypeUsage` (already `[Conditional("DEBUG")]`):
  `Console.WriteLine` → `System.Diagnostics.Debug.WriteLine` — a debug dump belongs on the
  debug listener, not the process stdout.
- `DateHelper.GetModificationDate`: **kept** (see Non-goals) with a comment stating why, and
  the file allowlisted in the convention test so the debt stays visible and bounded. The
  survey found a second product `DateTime.Now` (the benchmark sample generator); it now goes
  through a new `DateHelper.GetNowStamp()` so the clock convention lives in exactly the one
  allowlisted file.

### 3.5 Snapshot script (`scripts/update-openapi-snapshot.ps1`)

Encapsulates the procedure every feature plan has repeated by hand: build, start the app
volatile on `127.0.0.1:5078` (Development), poll `/openapi/v0.1.json`, write it to
`features/done/web-ui/openapi-v0.1.json`, stop the app, and print the `git diff --stat` so
the additive-only check is one glance. CLAUDE.md's snapshot instructions point at the script.

### 3.6 Survey-driven consolidation (whole-repo + last-10-days sweep)

A two-agent survey (code duplication/bloat; documentation duplication) over the full 10-day
cycle extended §3.3. **Landed in this feature:**

- **`ABucketIndex`** — DictionaryIndex and RangeIndex carried ~200 byte-identical lines of
  IIndex + persistence + reverse-map code each (and had already drifted: RangeIndex had
  dropped `try/finally` in several members). One base class now owns all of it, with an
  `OnKeySetChanged()` hook for RangeIndex's sorted-key invalidation (P4). DictionaryIndex is
  ~40 lines; the lock discipline is uniformly `try/finally`.
- **BLS `GetValidEdges`** — the in/out frontier-expansion near-twins (~232 lines, with the
  same `FrontierElement` construction repeated six times across their filter branch matrix)
  are one ~70-line method; semantics pinned identical (including mark-visited-before-
  vertex-filter).
- **`Fallen8.TryGetLiveElement<T>`** — the three snapshot-resolve copies.
- **Documentation single-homing** (the owner's explicit priority): the mode-(a) durability
  story now lives on `DelegateTransaction` alone; tag hygiene on `Fallen8Metrics` alone;
  durable-before-ack on `ConsumeLoop` alone; the ChangeFeedController no longer states its
  delivery contract twice in one file; CLAUDE.md's stored-query bullet defers to the feature
  docs; the observability spec banner names the README as the living metric reference; and
  CLAUDE.md now codifies the **one-home-per-explanation** rule for future features.

**Recorded backlog** (surveyed, prioritized, deliberately not rushed into this feature —
each is its own small, testable change):

| Item | Size | Note |
|---|---|---|
| Split `Fallen8.TryRemoveGraphElement_private` (~238 lines, mirrored vertex/edge restore blocks) | M | hot mutation path — wants its own careful change + review |
| Extract sprint concerns from `Fallen8` (WAL/replay region, change-feed hooks, gauge accessors) into partials/collaborators | M/L | mechanical, cold paths |
| Shared `IndexScan` guard preamble (4 copies of the resolve-or-false block) | S | |
| Test-side duplication: the delegating `IFallen8` stubs (SubGraphControllerTest, AnalyticsControllerUnitTest) and the per-file WebApplicationFactory + seed helpers | M | test-only |
| Stored-query gating trio in GraphController/SubGraphController remarks (near-verbatim clones) + bulk/analytics controller remark compression | M | XML remarks feed OpenAPI — compress to the 2 sentences a consumer needs, regen snapshot |
| Root README save-games section carries full contract detail | S | shrink to summary + pointer |
| Serializer optimized-array writer family (6× ~52 identical lines + 8 private encoders) | M | legacy, format-sensitive, hot — touch only with format work |
| Legacy serializer dispatchers (`WriteObject`, `WriteTypedArray`, ~230 lines each) | L | do not touch without format work |

Rejected by the survey (genuinely distinct, do not merge): `Trim_internal` vs
`TabulaRasa_internal`; RegExIndex/SingleValueIndex vs the bucket family; BLS vs
WeightedDijkstra traversal models; VectorIndex internals.

## 4. Acceptance criteria

- `dotnet build fallen-8-core.sln` completes with **0 warnings, 0 errors**, and introducing
  a warning fails the build (`TreatWarningsAsErrors` active in all three projects).
- `CodeQualityTest` enforces the four §3.2 rules and passes; deleting a header or adding a
  `Console.WriteLine` to product code makes the suite fail.
- The six analytics walkers, seven problem+json blocks and the string-named resolve-cache
  flows (the generic Activator overload deliberately keeps its distinct shape) are
  consolidated; the full suite (718 tests pre-feature) stays green with **zero assertion
  changes** in existing tests — the refactors are provably behaviour-preserving.
- The snapshot script regenerates `openapi-v0.1.json` byte-stable against a freshly built
  app (no spurious diff when nothing changed).
- CLAUDE.md documents the gates, the convention tests and the script.

## 5. Risks

- **`TreatWarningsAsErrors` breaking future builds on SDK bumps** (new SDKs add analyzers).
  Accepted deliberately — that is the gate working; the response is fixing or consciously
  `NoWarn`-ing with a comment, never disabling the gate. NuGet audit is pre-excluded (§3.1).
- **The struct-visitor refactor changing hot-loop codegen.** Mitigated by the struct-generic
  pattern (specialized, inlinable) and the analytics test suite's exact hand-computed
  values; the opt-in analytics-scale tests (100k-chain budget fixture) stay green.
- **Convention tests being brittle** (encoding, generated files). Scoped to the three project
  directories, `obj`/`bin` excluded, comment lines stripped, allowlists explicit.

## 6. Keep (do not regress)

- Every existing test passes unmodified — no assertion is loosened to make a refactor fit.
- The single-writer/lock-free-reader discipline, the REST contract (routes, status codes,
  response bodies), and the OpenAPI snapshot are all unchanged by the refactors.
- The engine stays dependency-clean (no new engine package; the pinned-versions rule is now
  test-enforced).
- The feature workflow itself: this feature follows spec → plan → branch → council → merge.
