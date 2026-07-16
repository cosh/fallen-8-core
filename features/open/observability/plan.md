# Fallen-8 Observability — Plan

Companion to [spec.md](./spec.md). Feature branch: `feature/observability` (branch-only
workflow — no GitHub issue/PR).

Ordering principle: instrument the engine first (BCL-only, testable with `MeterListener` /
`ActivityListener` without any exporter), then wire the apiApp exporters so the instruments
become scrapeable, then add the two new REST surfaces (health, `/statistics`). Every phase ends
build-clean (`dotnet build fallen-8-core.sln`, 0 warnings) and tests-green
(`dotnet test fallen-8-core.sln`).

## Phase 0 — Options + diagnostics scaffold

Intent: the configuration surface and the engine-side homes, with nothing recording yet.

- [x] `Fallen8ObservabilityOptions` (`Fallen8:Observability` section) bound in `Program.cs`,
  validated (budget ≥ 1, TopN ≥ 1, sampling ratio in [0,1]); defaults per spec §3.4.
- [x] `fallen-8-core/Diagnostics/Fallen8Diagnostics.cs` (static `ActivitySource
  "NoSQL.GraphDB.Core"`) and `Fallen8Metrics.cs` (per-engine `Meter "NoSQL.GraphDB.Core"`,
  `IDisposable`), MIT headers; created in the `Fallen8` constructor, disposed in
  `Fallen8.Dispose` **before** the transaction manager (pinned by test).
- [x] Verify by test: engine csproj has no new package reference; two engines in one process
  report independent gauges; disposal unregisters callbacks.

## Phase 1 — Engine meters (transactions, WAL, checkpoint, graph)

Intent: the full §3.2 core-meter inventory, proven by `MeterListener` tests.

- [x] Transaction pipeline (`TransactionManager.cs`): enqueue timestamp on `WorkItem`
  (`Enabled`-gated), `fallen8.transaction.commits` / `.rollbacks` (+ `failure.reason`) /
  `.execute.duration` in `ExecuteTransactionBody`; `.commit.duration`, `.group.size`,
  `.nondurable` in `FlushAndCompleteGroup`; `.queue.depth` gauge over `_transactions.Count`.
- [x] WAL (`WriteAheadLog.cs`): `fallen8.wal.flush.duration` / `.flush.failures` around
  `FlushGroup`; `fallen8.wal.degraded` gauge from the D1 fence (`HasFailed`) OR the D3
  awaiting-paired-load state; `fallen8.wal.size` gauge.
- [x] Checkpoint (`Fallen8.Save` / `Load_internal` + `PersistencyFactory`):
  `fallen8.checkpoint.save|load.duration`, `.save|load.bytes`, `.failures{operation}`.
- [x] Graph/index gauges: `fallen8.graph.vertices|edges`, `fallen8.index.count`,
  `fallen8.index.entries` (aggregate sum; callbacks read published values only, never walk
  structures).
- [x] Tests (`MeterListener`): counter/histogram assertions per operation; injected WAL append
  failure → degraded gauge 1 + nondurable counter, save clears it; tag-hygiene test (no
  user-supplied strings in any tag value); no measurements and no timestamps taken when no
  listener is attached (the `Enabled` gate).

## Phase 2 — Engine + apiApp spans

Intent: the trace story, still exporter-free (ActivityListener tests).

- [x] `fallen8.transaction.execute` span with the captured enqueue-time parent context
  (`AddTransaction` stamps `Activity.Current?.Context`; the writer starts the span with that
  explicit parent), tags `transaction.type`, terminal state, `durable`.
- [x] `fallen8.checkpoint.save` / `fallen8.checkpoint.load` spans (tags: partitions, bytes,
  replayed entry count).
- [x] apiApp: `fallen8.codegen.compile` spans around both Roslyn compiles in
  `CodeGenerationHelper` (tags `artifact`, `success`); `fallen8.path.search` /
  `fallen8.subgraph.run` spans around traverser/algorithm execution.
- [x] Tests (`ActivityListener`): cross-thread parenting (HTTP-shaped parent → transaction
  child); span tags; zero allocations when unsampled (`StartActivity` returns null).

## Phase 3 — apiApp wiring: exporters, `/metrics`, codegen counters

Intent: the instruments become operationally visible.

- [x] OTel packages in the apiApp only (`OpenTelemetry.Extensions.Hosting`,
  `OpenTelemetry.Exporter.Prometheus.AspNetCore`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`;
  exact versions pinned and recorded in the feature README).
- [x] `Program.cs`: register OTel **only when** Prometheus or OTLP is enabled; meters
  `NoSQL.GraphDB.Core`, `NoSQL.GraphDB.App`, `Microsoft.AspNetCore.Hosting`,
  `Microsoft.AspNetCore.Server.Kestrel`, `System.Runtime`; sources `NoSQL.GraphDB.Core`,
  `NoSQL.GraphDB.App`, `Microsoft.AspNetCore`; parent-based sampler with the configured ratio.
- [x] `GET /metrics` mapped when `Prometheus:Enabled=true`; `[AllowAnonymous]` by default,
  API-key-gated when `Prometheus:RequireApiKey=true` (spec §3.7).
- [x] `GeneratedCodeCache` hit/miss counters + compile duration/failure metrics
  (`NoSQL.GraphDB.App` meter).
- [x] Startup posture log line (exporters, `/metrics` auth mode) in the existing security-warning voice.
- [x] Tests (`WebApplicationFactory`): `/metrics` 404 when disabled; 200 + exposition-format
  content containing `fallen8_` series after real operations when enabled; auth matrix
  (anonymous default / 401 when `RequireApiKey` and no key sent); zero-config path registers no
  OTel services.

## Phase 4 — Health endpoints

Intent: liveness/readiness per spec §3.6 (none exist today).

- [x] `AddHealthChecks` + `MapHealthChecks("/healthz")` (liveness) and `"/readyz"` (readiness),
  both `[AllowAnonymous]`, status-only.
- [x] `StartupState` (readiness flag) set by `DurabilityLifecycleService.StartAsync` after
  load completes (immediately in volatile mode); document the "ready ≈ live once serving"
  honesty note in the endpoint remarks.
- [x] Tests: both endpoints 200 anonymous with and without an API key configured; readiness
  reflects the flag.

## Phase 5 — `GET /statistics`

Intent: the graph-shape snapshot with honest, budgeted cost.

- [x] `StatisticsController` (versioned route, `[ProducesResponseType]`, XML docs) +
  `GraphStatisticsREST` DTO (added to `AppJsonContext`): counts, labels (top-N + distinct),
  degree distribution (in/out/total: min/max/mean/p50/p90/p99), property keys (top-N +
  distinct), index inventory (name/type/keys/values), memory (working set, `GC.GetTotalMemory(false)`,
  `GCMemoryInfo` highlights — never a forced GC), `computedInMs` / `sampled` / `sampleStride`.
- [x] Budget + uniform-stride sampling per spec §3.5 (`ElementBudget` default 1M, exact below,
  sampled-and-flagged above; sampled distinct counts documented as within-sample).
- [x] Auth: behind the normal fallback policy (no `[AllowAnonymous]`); rate-limited under the
  existing `SensitiveRateLimitPolicy`.
- [x] Tests: exact correctness on a known small graph (hand-computed degree stats, label and
  property cardinalities, index counts); budget behaviour with a tiny configured budget
  (`sampled`, `sampleStride`, bounded work); auth 401/200 matrix; rate-limit 429.

## Phase 6 — Docs, overhead check, gate

- [x] Feature `README.md`: metric reference table, example Prometheus scrape config (including
  the `http_headers` snippet for `RequireApiKey`), example `curl /statistics`, OTLP example.
- [x] Root `README.md`: short "Observability" section.
- [x] Overhead check: the `write-path-throughput` benchmark path re-run with metrics off vs on
  (listener attached); confirm the off-path is unchanged and record the on-path delta in the
  feature README.
- [x] Full `dotnet test` green (713 passed); build 0 warnings/0 errors introduced.
- [ ] Council review per the repo merge gate; fix findings on the branch; `git merge --no-ff`
  to `main`; move `features/open/observability/` → `features/done/`.

## Progress

- [x] Phase 0 — options + diagnostics scaffold (per-engine Meter, static ActivitySource)
- [x] Phase 1 — engine meters: transactions, WAL, checkpoint, graph/index gauges
- [x] Phase 2 — spans: transaction (cross-thread parent), checkpoint, codegen, algorithms
- [x] Phase 3 — exporters + `/metrics` + codegen counters + posture log
- [x] Phase 4 — `/healthz` + `/readyz` readiness tied to startup load
- [x] Phase 5 — `/statistics` with budgeted sampling, auth, rate limit
- [ ] Phase 6 — docs + overhead benchmark done; council gate, merge + move to done/ pending

## Decision / revisit conditions

- **BCL-only engine instrumentation** is a hard constraint; if a future need (views, exemplars)
  requires SDK types in the engine, that is a new decision, not a drift.
- **`/metrics` anonymous by default** stands on the `/status` precedent + the
  no-user-strings-in-tags invariant; revisit if the metric inventory ever needs a
  user-named tag (it must instead go to `/statistics`).
- **No collector / dashboards / log pipeline** — revisit triggers named in spec §2 Non-goals
  (mcp-server landing for cross-process traces; an explicit user ask for a monitoring compose
  profile).
- **`/statistics` has no cache** in v1; revisit if the operator actually polls it at scrape
  frequency (the rate limiter will make that visible) — then a short TTL cache is the fix, not
  background recomputation.
