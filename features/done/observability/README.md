# Fallen-8 Observability — Usage

Metrics + traces via OpenTelemetry (BCL instruments in the engine — zero new engine
dependencies), a Prometheus scrape endpoint, a `GET /statistics` graph-shape snapshot, and
liveness/readiness health endpoints. Everything is **off by default**: a default
configuration runs zero OTel code paths and performs identically to before (measured below).

## Turning it on

```jsonc
// appsettings.json
"Fallen8": {
  "Observability": {
    "Prometheus": { "Enabled": true, "RequireApiKey": false },
    "Otlp": { "Endpoint": "http://localhost:4317" },   // optional: metrics + traces, point-to-point
    "TracingSamplingRatio": 1.0,
    "StatisticsElementBudget": 1000000,
    "StatisticsTopN": 20
  }
}
```

Startup logs one posture line per enabled exporter (in the voice of the security warnings).
Pinned packages (apiApp only): `OpenTelemetry.Extensions.Hosting` 1.17.0,
`OpenTelemetry.Exporter.OpenTelemetryProtocol` 1.17.0,
`OpenTelemetry.Exporter.Prometheus.AspNetCore` **1.17.0-beta.1** (the scrape exporter has
long been a release-candidate package; the surface used — one endpoint mapping — is minimal).

## The metric inventory

Meter `NoSQL.GraphDB.Core` (per-engine; durations in seconds, sizes in bytes):

| Instrument | Type | Meaning |
|---|---|---|
| `fallen8.transaction.commits` / `.rollbacks` | Counter | terminal transactions, tagged `transaction.type` (+ `failure.reason` on rollbacks) |
| `fallen8.transaction.commit.duration` | Histogram | enqueue → **durable ack** (recorded after the group fsync) |
| `fallen8.transaction.execute.duration` | Histogram | the body on the single writer |
| `fallen8.transaction.queue.depth` | Gauge | transactions waiting for the writer |
| `fallen8.transaction.group.size` | Histogram | group-commit batch size |
| `fallen8.transaction.nondurable` | Counter | committed with `Durable == false` (degraded log) |
| `fallen8.wal.flush.duration` / `.flush.failures` | Histogram / Counter | the group fsync |
| `fallen8.wal.degraded` | Gauge (0/1) | the D1 sticky fence OR the D3 awaiting-paired-load state — `DurabilityDegraded` as a metric |
| `fallen8.wal.size` | Gauge | current log file length |
| `fallen8.checkpoint.save/load.duration`, `.save/load.bytes`, `.failures` | Histograms / Counter | checkpoint operations |
| `fallen8.graph.vertices` / `.edges`, `fallen8.index.count` / `.entries` | Gauges | shape counters (index entries are the aggregate sum — per-index detail is `/statistics`' job) |

Meter `NoSQL.GraphDB.App`: `fallen8.codegen.cache.hits`/`.misses`,
`fallen8.codegen.compile.duration`, `.compile.failures` (tag `artifact` =
`path_traverser`|`subgraph`). Built-in meters enabled alongside:
`Microsoft.AspNetCore.Hosting`, `Microsoft.AspNetCore.Server.Kestrel`, `System.Runtime`.

**Tag hygiene (hard invariant, pinned by test):** no metric tag value originates from user
input — tags are type/enum/artifact names only. Anything user-named (labels, index names,
property keys) lives in `/statistics`, behind auth.

Useful queries: `histogram_quantile(0.99, rate(fallen8_transaction_commit_duration_bucket[5m]))`
(commit p99), `fallen8_wal_degraded == 1` (alert: durability degraded),
`sum by (failure_reason) (rate(fallen8_transaction_rollbacks_total[5m]))`.

## Scraping

`GET /metrics` is **anonymous by default** — a deliberate, documented call: the inventory
carries aggregate operational numbers only (same sensitivity as the long-anonymous
`/status`), and the server binds loopback by default. To lock it:

```yaml
# prometheus.yml, with Fallen8:Observability:Prometheus:RequireApiKey=true
scrape_configs:
  - job_name: fallen8
    static_configs: [{ targets: ["localhost:5000"] }]
    http_headers:
      X-Api-Key:
        values: ["<your key>"]
```

## Traces

Spans exist only when OTLP is configured (no endpoint → `StartActivity` returns null, spans
cost nothing): the built-in `Microsoft.AspNetCore` server spans, plus
`fallen8.transaction.execute` (parented to the **enqueuing request** across the writer
thread — queue wait and execution are visible under the HTTP span; tags: type, terminal
state, durable), `fallen8.checkpoint.save`/`.load` (partitions, bytes, replayed WAL
entries), `fallen8.codegen.compile` (artifact, success), `fallen8.path.search` and
`fallen8.subgraph.run` (algorithm, result counts). Sampling: parent-based, root ratio via
`TracingSamplingRatio`.

## GET /statistics

The graph-shape snapshot: counts, vertex/edge label cardinalities (top-N + distinct),
in/out/total degree distributions (min/max/mean/p50/p90/p99), property-key cardinalities,
index inventory, and free memory numbers (`GC.GetTotalMemory(false)` + `GCMemoryInfo` —
**never a forced GC**).

```bash
curl -sf -H "X-Api-Key: <key>" http://localhost:5000/statistics
```

- **Advisory snapshot**: lock-free reads concurrent with the writer, not transactionally
  consistent.
- **Budgeted**: exact when `V+E ≤ StatisticsElementBudget` (default 1M); above it the pass
  samples with a uniform stride and says so (`sampled: true`, `sampleStride`) — per-name
  counts are as counted **in the sample** (multiply by the stride to extrapolate), and
  `distinctTotal` is distinct-within-the-sample (sampling honestly undercounts distinct
  values; the DTO documents that rather than pretending to estimate). Degree percentiles
  from a strided sample are statistically sound. `computedInMs` makes the cost visible.
- **Auth + rate limit**: requires the API key when one is configured (it exposes
  schema-shaped data — label/property/index names), and sits under the sensitive
  fixed-window rate limiter so a misconfigured scrape loop cannot turn O(V+E) into a DoS.

## Health

- `GET /healthz` — liveness: 200 once the server answers.
- `GET /readyz` — readiness: 200 once load-at-startup completed (immediately in volatile
  mode). Honest note: hosted services finish `StartAsync` before Kestrel accepts
  connections in the current wiring, so today ready ≈ live once serving — the endpoint pins
  the contract and serves orchestrator convention regardless.

Both anonymous, status-only.

## Measured overhead

Opt-in benchmark (`fallen-8-unittest/ObservabilityOverheadBenchmark.cs`, remove `[Ignore]`
and run `dotnet test --filter "TestCategory=Benchmark"`): 50,000 single-vertex transactions
measured **~146k tx/s with metrics off vs ~153k tx/s with a listener attached** — the delta
is inside run-to-run noise. The zero-config off path takes no timestamps at all (every
hot-path clock read is gated on `instrument.Enabled`), so this is expected: recording two
counters and two histogram samples per transaction is trivia next to a graph mutation.
