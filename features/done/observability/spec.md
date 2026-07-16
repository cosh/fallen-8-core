# Fallen-8 Observability — Specification

> **Status:** Implemented and merged (branch `feature/observability`, council-approved
> 2026-07-16; see [plan.md](./plan.md) for the phase record and council outcome).

## 1. Overview & motivation

The 2026-07 review cycle landed a lot of performance and durability work — group commit
(`write-path-throughput`), the WAL failure fence and `Durable` signal
(`crash-durability-hardening` D1), single-pass checkpoint CRC (`checkpoint-io-efficiency`),
bounded transaction retention (`transaction-retention`), the codegen cache re-keying
(`codegen-cache-keying`). All of it is verified by tests and benchmarks — and **none of it is
visible in operation**. A running server today exposes exactly one aggregate signal
(`GET /status`: element counts + process virtual memory, `AdminController.cs:134`) and
structured console logs. If commit latency regresses, the queue backs up, the WAL degrades, or
a checkpoint doubles in duration, the operator finds out from symptoms, not from a metric.

This feature makes the engine's health and the graph's shape observable **in operation**, with
two deliverables:

1. **Metrics + traces via OpenTelemetry.** The engine emits through the BCL abstractions only
   (`System.Diagnostics.Metrics.Meter`, `System.Diagnostics.ActivitySource` — both in the
   `net10.0` shared framework, zero new engine dependencies). The apiApp wires the OpenTelemetry
   SDK and exporters: a Prometheus scrape endpoint (`/metrics`) behind a config flag, and an
   OTLP exporter as a config-gated option.
2. **A `GET /statistics` REST endpoint** returning a graph-shape snapshot: counts, label
   cardinalities, degree distribution, property-key cardinalities, index inventory, memory —
   each stat with an honest, bounded cost.

Fallen-8 is a single-process, self-hosted, single-operator system. The bar is "an operator can
point Prometheus (or `curl`) at one endpoint and see regressions", not an enterprise
observability platform — see Non-goals.

## 2. Goals / non-goals

**Goals**

- Engine instrumentation with **no OTel package dependency in `fallen-8-core`**: a per-engine
  `Meter` (disposed with the engine, so unit tests that build many engines never leak gauge
  callbacks) plus one static `ActivitySource`. When nothing listens, every instrument no-ops;
  hot-path timing is gated on `instrument.Enabled` so the uninstrumented cost stays ~zero.
- A concrete `fallen8.*` metric inventory (§3.2) grounded in real code locations: transaction
  commit latency / queue depth / failures by reason, WAL flush duration + degraded state,
  checkpoint save/load durations + bytes, element counts, index counts/sizes, codegen cache
  hits/misses + compile duration. HTTP and runtime metrics come from the built-in
  `Microsoft.AspNetCore.Hosting` / `System.Runtime` meters — no custom code.
- Traces: ASP.NET Core's built-in `Microsoft.AspNetCore` ActivitySource, plus `fallen8.*`
  spans around transaction execution (parented to the enqueuing HTTP request across the writer
  thread), checkpoint save/load, dynamic-code compiles, and path/subgraph algorithm runs.
- apiApp wiring: `OpenTelemetry.Extensions.Hosting` + `OpenTelemetry.Exporter.Prometheus.AspNetCore`
  (scrape endpoint, on by `Fallen8:Observability:Prometheus:Enabled`) +
  `OpenTelemetry.Exporter.OpenTelemetryProtocol` (on when `Fallen8:Observability:Otlp:Endpoint`
  is set). No collector required; OTLP is point-to-point when wanted.
- `GET /statistics` (versioned controller, repo conventions) with every stat's cost stated and
  everything O(V+E) bounded by an element budget + uniform sampling (§3.5).
- Liveness/readiness health endpoints (none exist today — verified) tied to the
  `hosted-durability-lifecycle` load-at-startup.
- MSTest coverage: `MeterListener`/`ActivityListener` assertions around key operations,
  `/statistics` correctness on a known small graph, budget behaviour, auth interaction.

**Non-goals** (each with its concrete revisit trigger)

- **No OpenTelemetry Collector / sidecar requirement.** Prometheus scrapes the app directly;
  OTLP (when configured) goes point-to-point. *Revisit when Fallen-8 becomes more than one
  process in practice — concretely, when the `mcp-server` deployable lands and cross-process
  trace correlation is actually wanted.*
- **No dashboards-as-code, no shipped Grafana/alerting rules, no compose monitoring profile.**
  The metric names + a README example query list are the deliverable. *Revisit if the user asks
  for a `--profile monitoring` compose service after running with `/metrics` for a while.*
- **No OTel logs pipeline.** The existing structured console logging stays untouched.
  *Revisit together with the collector trigger above (log/trace correlation is only worth it
  across processes).*
- **No multi-tenant / instance labels.** One engine per process; `service.name=fallen8` and
  OTel resource defaults suffice. *Revisit only if multi-engine hosting ever becomes a feature.*
- **No custom histogram bucket tuning, views, or exemplars in v1.** SDK defaults. *Revisit
  when a real dashboard shows bucket misfit for commit-latency percentiles.*
- **No engine-internal "memory accounting" walker.** `memory-footprint` measures retained heap
  with `GC.GetTotalMemory(true)` in benchmarks; forcing a blocking GC from a metrics callback
  or REST endpoint is unacceptable. §3.5 reports process + GC numbers that are free. *Revisit
  if a per-structure byte accounting is built for its own sake (it is not planned).*

## 3. Design sketch

### 3.1 Engine instrumentation model (`fallen-8-core`, BCL only)

```
fallen-8-core/Diagnostics/Fallen8Diagnostics.cs   static ActivitySource "NoSQL.GraphDB.Core"
fallen-8-core/Diagnostics/Fallen8Metrics.cs       per-engine Meter "NoSQL.GraphDB.Core", IDisposable
```

- `Fallen8Metrics` is constructed by `Fallen8`'s constructor and disposed by `Fallen8.Dispose`.
  Per-engine (not static) because observable gauges capture the engine instance
  (`VertexCount`, queue depth, WAL state); a static meter would keep disposed test engines
  alive and double-report. Disposing the `Meter` unregisters all its instruments.
- The `ActivitySource` is static (spans capture no engine state).
- `System.Diagnostics.DiagnosticSource` ships in the shared framework; **the engine csproj
  gains no package reference.**
- Hot-path discipline: counters/histograms are recorded only after an `Enabled` check where a
  timestamp would otherwise be taken (`Stopwatch.GetTimestamp()` stamps in `AddTransaction` /
  `FlushAndCompleteGroup` are skipped when the commit-latency histogram has no listener);
  span creation relies on `ActivitySource.StartActivity` returning null when unsampled.

Instrumentation points (all verified in the current tree):

| Point | Location |
|---|---|
| Enqueue timestamp, queue depth | `Transaction/TransactionManager.cs` — `AddTransaction` (stamp on the `WorkItem`), `_transactions.Count` (`BlockingCollection`) for the gauge |
| Execute duration, commit/rollback counters, failure reason | `TransactionManager.ExecuteTransactionBody` (`TryExecute` timing; `SetTransactionState` terminal transitions; `FailureReason`) |
| Commit latency (enqueue → durable ack), group size, non-durable commits | `TransactionManager.FlushAndCompleteGroup` — elapsed recorded just before `Completion.TrySetResult()`, i.e. exactly the durable-before-ack point; `Durable == false` increments the non-durable counter |
| WAL flush duration + failures | `WriteAheadLog.FlushGroup` (`WriteAheadLog.cs:280`) — the group fsync |
| WAL degraded state | `WriteAheadLog.HasFailed` (`WriteAheadLog.cs:240`, the D1 sticky fence) + the D3 awaiting-paired-load suspension — surfaced as one 0/1 gauge |
| WAL size | log file length via the open handle (`WriteAheadLog.cs`) |
| Checkpoint save/load duration + bytes | `Fallen8.Save` (`Fallen8.cs:899` → `PersistencyFactory.Save`) and `Fallen8.Load_internal` (`Fallen8.cs:2088`); bytes = header + sidecar sizes the factory already knows |
| Element counts | `Fallen8.VertexCount` / `Fallen8.EdgeCount` (O(1) maintained counters) |
| Index count / entries | `IndexFactory.Indices` + `IIndex.CountOfKeys()` / `CountOfValues()` (`IIndex.cs:58/64`) |

### 3.2 Metric inventory (names, types, units)

Naming follows OTel semantic-convention style: dot-separated lowercase, unit in the instrument
(never the name), low-cardinality tags only (enum names, never user strings — see §3.7).
Durations are histograms in seconds (`s`), sizes in bytes (`By`).

**Meter `NoSQL.GraphDB.Core`** (engine):

| Instrument | Type | Unit | Tags | Source |
|---|---|---|---|---|
| `fallen8.transaction.commits` | Counter | `{transaction}` | `transaction.type` | terminal `Finished` |
| `fallen8.transaction.rollbacks` | Counter | `{transaction}` | `transaction.type`, `failure.reason` (the `transaction-failure-reasons` enum) | terminal `RolledBack` |
| `fallen8.transaction.commit.duration` | Histogram | `s` | `transaction.type` | enqueue → durable ack (`FlushAndCompleteGroup`) |
| `fallen8.transaction.execute.duration` | Histogram | `s` | `transaction.type` | `TryExecute` body on the writer |
| `fallen8.transaction.queue.depth` | ObservableGauge | `{transaction}` | — | `_transactions.Count` |
| `fallen8.transaction.group.size` | Histogram | `{transaction}` | — | group-commit batch size at flush |
| `fallen8.transaction.nondurable` | Counter | `{transaction}` | — | committed with `Durable == false` (D1/D3) |
| `fallen8.wal.flush.duration` | Histogram | `s` | — | `FlushGroup` fsync |
| `fallen8.wal.flush.failures` | Counter | `{failure}` | — | flush returned false / threw |
| `fallen8.wal.degraded` | ObservableGauge | — (0/1) | — | sticky fence tripped OR awaiting paired load — the metric face of `DurabilityDegraded` |
| `fallen8.wal.size` | ObservableGauge | `By` | — | current log length |
| `fallen8.checkpoint.save.duration` | Histogram | `s` | — | `Fallen8.Save` |
| `fallen8.checkpoint.save.bytes` | Histogram | `By` | — | bytes written per save |
| `fallen8.checkpoint.load.duration` | Histogram | `s` | — | `Load_internal` (includes WAL replay) |
| `fallen8.checkpoint.load.bytes` | Histogram | `By` | — | bytes read per load |
| `fallen8.checkpoint.failures` | Counter | `{failure}` | `operation` = `save`\|`load` | a failed save/load |
| `fallen8.graph.vertices` | ObservableGauge | `{vertex}` | — | `VertexCount` |
| `fallen8.graph.edges` | ObservableGauge | `{edge}` | — | `EdgeCount` |
| `fallen8.index.count` | ObservableGauge | `{index}` | — | `IndexFactory.Indices.Count` |
| `fallen8.index.entries` | ObservableGauge | `{entry}` | — | Σ `CountOfKeys()` over all indices (aggregate only; per-index detail lives in `/statistics` behind auth, §3.7) |

**Meter `NoSQL.GraphDB.App`** (apiApp):

| Instrument | Type | Unit | Tags | Source |
|---|---|---|---|---|
| `fallen8.codegen.cache.hits` / `fallen8.codegen.cache.misses` | Counter | `{lookup}` | `artifact` = `path_traverser`\|`subgraph` | `GeneratedCodeCache.TryGetTraverser` / the subgraph compile path |
| `fallen8.codegen.compile.duration` | Histogram | `s` | `artifact`, `success` | around the Roslyn `Emit` in `CodeGenerationHelper.GeneratePathTraverser` (`:90`) and the subgraph compile (`:669`) |
| `fallen8.codegen.compile.failures` | Counter | `{failure}` | `artifact` | failed `EmitResult` |

**Built-in meters, enabled by name (no instrumentation packages):**
`Microsoft.AspNetCore.Hosting` (+ `Microsoft.AspNetCore.Server.Kestrel`) for
`http.server.request.duration` etc., and `System.Runtime` for GC/heap/threadpool — both native
in .NET 10.

### 3.3 Traces

`ActivitySource "NoSQL.GraphDB.Core"` / `"NoSQL.GraphDB.App"` spans:

- `fallen8.transaction.execute` — starts at `ExecuteTransactionBody` and closes at the
  durable-ack point in the group flush (so its duration covers execute **through** the group
  fsync), tags `transaction.type`, terminal state, and `durable` (committed transactions
  only — durability is meaningless for a rollback). The parent is the **enqueuing request's**
  activity: `AddTransaction` captures `Activity.Current?.Context` on the `WorkItem`, and the
  writer starts the span with that explicit parent — so a slow REST mutation shows its queue
  wait + execution even though the body runs on the decoupled single writer thread. The span
  is immediately detached from the writer thread's `Activity.Current` (it outlives the
  method), so same-group transactions without an ambient parent are never mis-parented.
- `fallen8.checkpoint.save` / `fallen8.checkpoint.load` — around `Save` / `Load_internal`,
  tags: partitions, bytes, replayed WAL entry count.
- `fallen8.codegen.compile` — around each Roslyn compile, tags `artifact`, `success`. (A
  cache HIT never creates a span, so a `cache` tag would be the constant `miss` — dropped;
  hit/miss ratios live in the `fallen8.codegen.cache.*` counters.)
- `fallen8.path.search` / `fallen8.subgraph.run` — around `IPathTraverser`/algorithm execution
  in `GraphController` and `SubGraphFactory` recalculation, tags: algorithm plugin name,
  result count.
- HTTP server spans come from the built-in `Microsoft.AspNetCore` source.

Trace **export** exists only via OTLP (Prometheus is metrics-only): with no
`Otlp:Endpoint` configured, no sampler listens and `StartActivity` returns null — spans cost
nothing. Sampling: parent-based always-on by default,
`Fallen8:Observability:Tracing:SamplingRatio` (default `1.0`) for the root sampler — honest
default for a single-operator box with modest request rates.

### 3.4 apiApp wiring & configuration

New `Fallen8ObservabilityOptions` (`Fallen8:Observability` section, sibling of
`Fallen8:Security` / `Fallen8:Durability`):

```
Fallen8:Observability:Prometheus:Enabled         bool, default false  -> maps GET /metrics
Fallen8:Observability:Prometheus:RequireApiKey   bool, default false  (see §3.7)
Fallen8:Observability:Otlp:Endpoint              string, default null -> adds OTLP exporter (metrics + traces)
Fallen8:Observability:TracingSamplingRatio       double, default 1.0
Fallen8:Observability:StatisticsElementBudget    int, default 1_000_000 (§3.5)
Fallen8:Observability:StatisticsTopN             int, default 20
```

`Program.cs` adds `AddOpenTelemetry()` **only when** Prometheus or OTLP is enabled — a fully
default configuration runs zero OTel code paths, same opt-in philosophy as the WAL and the
security gates. Packages (apiApp only, exact versions pinned at implementation time):
`OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.Prometheus.AspNetCore` (note: this
exporter has long been a release-candidate package — pin it and say so in the feature README;
it is nevertheless the standard in-process scrape endpoint), and
`OpenTelemetry.Exporter.OpenTelemetryProtocol`. Startup logs a posture line per enabled
exporter (including the honest `/metrics` auth mode — `RequireApiKey` without a configured
key is called out as effectively anonymous) and an info line when everything is off, in the
voice of the existing security warnings.

### 3.5 `GET /statistics` — graph-shape snapshot

New `StatisticsController` (`api/v{version}`, default `0.1`, `[ProducesResponseType]` + XML
docs per repo conventions), returning `GraphStatisticsREST`. Reads are lock-free over the
volatile snapshot exactly like `GetAllVertices` (array-backed since
`scan-result-representation`) — the result is an **advisory snapshot**, not transactionally
consistent, and says so in its OpenAPI remarks.

Honest cost per stat, and how each is bounded:

| Stat | Computation | Cost | Bound |
|---|---|---|---|
| `vertexCount`, `edgeCount` | `Fallen8.VertexCount/EdgeCount` | O(1) | — |
| `labels` (vertex + edge): top-N `{label, count}` + `distinctTotal` | one pass over the element snapshot, dictionary count | **O(V+E)** | element budget |
| `degree` (in/out/total): min, max, mean, p50/p90/p99 | `VertexModel.GetInDegree()/GetOutDegree()` (`VertexModel.cs:328/334` — adjacency group-count sums, O(#edge-property groups) per vertex, **never** an edge walk), collect + sort | **O(V)** + O(S log S) sort of the collected sample | vertex budget |
| `propertyKeys`: top-N by element count + `distinctTotal` | per-element property-id iteration | **O(P)** (total properties — the heaviest stat) | element budget |
| `indices`: `[{name, type, keys, values}]` | `IndexFactory.Indices` + `CountOfKeys()/CountOfValues()` | O(#indices) | — |
| `memory`: `processWorkingSetBytes`, `gcHeapBytes` (`GC.GetTotalMemory(false)`), `GCMemoryInfo` highlights | BCL reads | O(1), **never forces a GC** (deliberate contrast with `memory-footprint`'s benchmark-only `GetTotalMemory(true)`) | — |

**Budget & sampling (the decision):** computed **on demand, with a budget** — no background
recomputation, no cache in v1 (a single operator hitting this occasionally does not justify
staleness machinery). When `V+E ≤ ElementBudget` (default 1M) the pass is exact. Above it, the
scan iterates the snapshot with a uniform stride so at most `ElementBudget` elements are
touched, and the response sets `sampled: true` + `sampleStride`; per-label/per-key counts are
reported **as counted in the sample** (with the stride so a caller can extrapolate), and
`distinctTotal` becomes "distinct within the sample" — sampling honestly undercounts distinct
values and the DTO documents that rather than pretending to estimate. Degree percentiles from a
strided sample are statistically sound. The response always carries `computedInMs`, `sampled`,
and `sampleStride`. The endpoint additionally sits under the existing
`SensitiveRateLimitPolicy` fixed-window limiter so a scrape-loop misconfiguration cannot turn
O(V+E) into a DoS.

### 3.6 Health endpoints

None exist today (verified: no `AddHealthChecks`/`MapHealthChecks` in the tree). Add ASP.NET
Core health checks:

- `GET /healthz` — liveness: process is up and serving. Always `Healthy` once Kestrel answers.
- `GET /readyz` — readiness: a check backed by a `StartupState` flag that
  `DurabilityLifecycleService.StartAsync` sets after load-at-startup completes (or immediately
  in volatile mode). Honest note: hosted services complete `StartAsync` **before** Kestrel
  accepts connections in the current wiring, so today ready ≈ live once serving; the endpoint
  is still worth having because it pins the contract, serves orchestrator convention
  (compose/k8s probes), and stays correct if startup load ever becomes asynchronous.

Both return status only (no body data) and are `[AllowAnonymous]` — same posture as `/status`.

### 3.7 Security posture (`api-security-boundary` interaction)

- **`/statistics` requires the API key** (when one is configured — the normal fallback
  policy, no `[AllowAnonymous]`). It exposes *schema-shaped* data: label names, property-key
  names, index names. That is graph content, not just operational numbers.
- **`/metrics` is `[AllowAnonymous]`, documented as a deliberate call.** Reasons: (a) the
  precedent — `/status` (counts + memory) is already `[AllowAnonymous]` today; (b) the metric
  inventory is designed to carry **zero user-supplied strings** (the per-index entry gauge is
  aggregate-only precisely so no index name appears; all tags are enum/type names), so the
  exposure is aggregate operational numbers of the same sensitivity as `/status`; (c) the
  server binds loopback by default (`api-security-boundary`), so anonymous means
  anonymous-on-loopback unless the operator deliberately exposes it; (d) Prometheus cannot send
  the `X-Api-Key` header without awkward per-scrape config. Operators who want it locked set
  `Prometheus:RequireApiKey=true`, which drops the anonymous exemption and requires the header
  (documented with the matching Prometheus `http_headers` snippet).
- Health endpoints: anonymous, status-only (§3.6).
- The metric-tag rule is a hard invariant pinned by test: **no metric tag value may originate
  from user input** (labels, index names, property keys, filter fragments).

### 3.8 Test harness (MSTest, `fallen-8-unittest`)

- **Meter tests** with `MeterListener` against a real engine: commit N transactions → commit
  counter advanced by N, commit-duration histogram recorded, queue-depth gauge readable;
  rollback carries the `failure.reason` tag; WAL-enabled engine records flush durations; an
  injected append failure flips `fallen8.wal.degraded` to 1 and counts non-durable commits;
  save/load record durations + bytes; disposing the engine unregisters its gauges (a second
  engine in the same test run reports only itself).
- **Activity tests** with `ActivityListener`: a transaction span carries the enqueuing parent
  context; checkpoint and compile spans appear with their tags; with no listener, no span
  objects are created (allocation-free assertion via `StartActivity` null).
- **`/statistics` correctness** on a known small graph (the sample generator): exact counts,
  exact label/property cardinalities, hand-computable degree stats (min/max/mean/percentiles),
  index inventory matching created indices.
- **Budget behaviour:** options with a tiny `ElementBudget` against a graph larger than it →
  `sampled == true`, `sampleStride` correct, touched-element count bounded (observable via the
  sampled totals), percentiles still returned.
- **Auth interaction** via `WebApplicationFactory`: with an API key configured — `/statistics`
  401 without / 200 with the key; `/metrics` 200 anonymous by default and 401 anonymous when
  `RequireApiKey=true`; `/healthz`/`/readyz` 200 anonymous.
- **Tag hygiene:** the no-user-strings-in-tags invariant asserted over every emitted
  measurement in the meter tests.

## 4. Acceptance criteria

- **Engine dependency-clean:** `fallen-8-core.csproj` gains no package reference; all
  instrumentation compiles against BCL types only.
- **Metrics live:** with `Prometheus:Enabled=true`, `GET /metrics` serves the §3.2 instruments
  in Prometheus exposition format after real operations (commit, rollback, save, load, codegen
  hit/miss), plus the built-in HTTP + runtime meters. With everything disabled, no OTel
  services are registered and engine overhead is limited to `Enabled` checks.
- **DurabilityDegraded surfaced:** a WAL append failure is visible as
  `fallen8.wal.degraded == 1` + a non-zero `fallen8.transaction.nondurable` without reading a
  single log line; a subsequent save returns the gauge to 0.
- **Traces:** with an OTLP endpoint configured, a REST mutation produces an HTTP server span
  with a child `fallen8.transaction.execute` span (cross-thread parenting works); checkpoint
  and compile spans appear.
- **`/statistics`:** exact on small graphs, budget-bounded + honestly flagged on large ones,
  rate-limited, auth-gated; never forces a GC; O(E) work never exceeds the configured budget.
- **Health:** `/healthz` and `/readyz` exist, anonymous, readiness tied to startup-load
  completion.
- **Suite green**, build clean, all §3.8 tests present.

## 5. Risks

- **Prometheus exporter package maturity** (long-lived RC): pin the exact version; the surface
  used (one endpoint mapping) is minimal; fallback is serving the OTLP path only, with the
  scrape endpoint noted as blocked (has not been necessary in practice).
- **Writer-thread overhead:** every instrument sits on the single writer's hot path. Mitigated
  by `Enabled` gating (no timestamps when nobody listens) and by the existing benchmark suite —
  the `write-path-throughput` benchmark re-run with metrics on/off is part of the plan's gate.
- **Gauge callbacks under teardown:** observable gauges read engine state from the exporter's
  collection thread; callbacks must read only atomically-published values (counts, flags) —
  never walk structures. The per-engine `Meter` disposal ordering (dispose metrics before the
  transaction manager) is pinned by test.
- **Cardinality creep:** future tags could silently explode series (e.g. per-label counts as
  tags — deliberately excluded; that is `/statistics`' job). The tag-hygiene test and the §3.7
  invariant guard this.
- **`/statistics` on a hot large graph** is still a budgeted-but-real scan contending with
  readers (lock-free, but cache pressure). Rate limiting + the budget keep the worst case
  bounded; the DTO's `computedInMs` makes the cost visible to the operator.

## 6. Keep (do not regress)

- **Single-writer / lock-free-read invariants:** no instrumentation may add locks, block the
  writer, or introduce a second mutator. Gauges read published state; histograms record on the
  thread that did the work.
- **Durable-before-ack** (`write-path-throughput`): commit-latency recording happens around,
  never inside, the flush-then-complete ordering; completion semantics are untouched.
- **The D1 fence and `Durable`/`DurabilityDegraded` semantics** (`crash-durability-hardening`,
  `transaction-retention`): the gauge is a *view* of the existing signal, not a new channel.
- **`api-security-boundary` posture:** the fallback auth policy, loopback-by-default bind,
  and the code/plugin kill switches are unchanged; `/metrics` anonymity is an explicit,
  documented exemption of aggregate data only.
- **Existing `/status` contract** (anonymous, `StatusREST` shape): unchanged; `/statistics` is
  additive.
- **Zero-config behaviour:** a default `appsettings.json` runs no OTel code, exposes no new
  ports, and performs identically to today (benchmarks pin it).
- **The repo test bar:** every metric, span, stat, budget, and auth behaviour lands with
  arrange/act/assert MSTests in `fallen-8-unittest`.
