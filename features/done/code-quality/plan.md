# Code Quality Gates & Consolidation — Plan

Companion to [spec.md](./spec.md). Feature branch: `feature/code-quality` (branch-only
workflow — no GitHub issue/PR).

Ordering principle: gates first (so the refactors land under them), then the refactors one
cluster at a time with the full suite green after each, then automation + docs. No existing
test assertion may change.

## Phase 0 — Zero-warning gate

- [x] Fix CS1574 (`DynamicCapabilityAuthorization.cs` cref → `<c>…</c>`).
- [x] `Directory.Build.props`: `TreatWarningsAsErrors`, `WarningsNotAsErrors` for NuGet audit
  (NU1901–NU1904), explicit `AnalysisLevel`; verify all three projects build 0/0 and that an
  injected warning fails the build (manual check, then revert).

## Phase 1 — Convention tests

- [x] `fallen-8-unittest/CodeQualityTest.cs`: repo-root discovery, the four rules from spec
  §3.2 (MIT header, no Console.Write in product code, no DateTime.Now outside the DateHelper
  allowlist, exact package versions), each reporting all violations.
- [x] Hygiene fixes the rules require: `DumpTypeUsage` Console → `Debug.WriteLine`;
  `DateHelper` comment documenting the deliberate `DateTime.Now` (spec §3.4 / non-goal).

## Phase 2 — Consolidation refactors (suite green after each)

- [x] Survey-driven additions (spec §3.6, after the owner widened the scope to the whole
  10-day cycle): `ABucketIndex` (DictionaryIndex/RangeIndex, ~330 lines removed), BLS
  `GetValidEdges` (~232 -> ~70), `Fallen8.TryGetLiveElement<T>`, plus the
  documentation single-homing pass (mode-a, tag hygiene, durable-before-ack, change-feed
  contract, CLAUDE.md stored-query bullet, observability metric-reference banner). The
  remaining surveyed items are the recorded backlog in spec §3.6.

- [x] `AnalyticsAdjacency` struct-visitor walker; migrate DEGREE, PAGERANK, WCC,
  LABELPROPAGATION, TRIANGLECOUNT; delete the six private walkers. Full suite green,
  hand-computed fixtures unchanged.
- [x] `ProblemResults` helper; migrate the seven blocks in `BulkController` and
  `AnalyticsController`. Endpoint tests (status/content-type/detail) unchanged.
- [x] `Fallen8` private generic resolve-initialize-cache-invoke helper for the string-named
  plugin paths (`TryCalculateShortestPath`, `TryRunAnalytics`).

## Phase 3 — Automation + docs + gate

- [x] `scripts/update-openapi-snapshot.ps1` (build, run volatile on 5078, fetch, write
  snapshot, stop, print diff stat); verified byte-stable on a no-change run.
- [x] CLAUDE.md: quality-gates section (warnings-as-errors, convention tests, snapshot
  script); feature `README.md` summarizing the measures + survey numbers.
- [x] Full `dotnet test` green (722 passed); council review (two reviewers: refactor
  behaviour-preservation, scope/docs - no blockers; majors fixed on the branch: snapshot
  script orphan/stale-port robustness, the change-feed contract pointer; honesty fixes);
  `git merge --no-ff` to `main`; move `features/open/code-quality/` → `features/done/`.

## Progress

- [x] Phase 0 — zero-warning gate
- [x] Phase 1 — convention tests + hygiene fixes
- [x] Phase 2 — consolidation refactors
- [x] Phase 3 — snapshot script, docs, council gate, merge + move to done/

## Decision / revisit conditions

Carried in spec §2 Non-goals: analyzer-level raises happen rule-by-rule on demonstrated bug
classes; NRT per rewritten file; no whole-repo reformat; no coverage thresholds; the
DateHelper local-clock semantics get their own feature if cross-timezone savegames become
real.
