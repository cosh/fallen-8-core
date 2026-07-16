# Code Quality Gates & Consolidation — Usage

The living summary of the measures this feature installed (survey numbers and the full
backlog live in [spec.md](./spec.md) §3.6).

## The gates

- **Warnings are errors** — `Directory.Build.props`, all projects. NuGet audit advisories
  (NU1901–NU1904) stay warnings so a fresh CVE surfaces loudly without bricking builds.
- **Convention tests** — `fallen-8-unittest/CodeQualityTest.cs` fails the suite on: a
  missing MIT header, `Console.Write*` in product code, `DateTime.Now` outside the
  documented `DateHelper` allowlist, or a non-exact package version.
- **`pwsh scripts/update-openapi-snapshot.ps1`** regenerates the pinned OpenAPI snapshot
  (replaces the by-hand procedure); the printed diff stat must stay additive.
- **One home per explanation** (CLAUDE.md): each concept is documented once — on the type
  owning the contract or in the feature README — and pointed to from everywhere else.

## What was consolidated

| Consolidation | Before → after |
|---|---|
| `ABucketIndex` (DictionaryIndex + RangeIndex) | ~200 duplicated lines each, drifting lock discipline → one base, uniform `try/finally` |
| BLS `GetValidEdges` | 2 near-twin methods, ~232 lines, 6 repeated constructions → 1 method, ~70 lines |
| `AnalyticsAdjacency` struct-visitor walk | 6 per-algorithm walker copies → 1 (allocation-free, JIT-specialized) |
| `ProblemResults` | 7 hand-rolled problem+json blocks → 1 helper |
| `Fallen8.ResolveCachedPlugin<T>` / `TryGetLiveElement<T>` | 3+3 copies → 1+1 |
| Doc single-homing | mode-(a) → `DelegateTransaction`; tag hygiene → `Fallen8Metrics`; durable-before-ack → `ConsumeLoop`; change-feed contract stated once per file |

Deliberately not done (with revisit triggers, spec §2): raising the analyzer mode
(~3,200 findings of naming/formatting churn), nullable annotations wholesale, whole-repo
reformat, coverage thresholds, and the `DateHelper` local-clock semantics.
