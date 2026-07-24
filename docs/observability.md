# Observability

Fallen-8 emits metrics and traces through BCL instruments — `System.Diagnostics.Metrics` meters and an `ActivitySource` — so the engine carries **no OpenTelemetry dependency**. The server wires OpenTelemetry only when you configure an exporter; a default configuration runs **zero OTel code paths** and, with no listener attached, every hot-path clock read and `StartActivity` call is skipped. Three surfaces are always available regardless of that wiring: `GET /statistics` (a graph-shape snapshot), `GET /status` (a cheap discovery probe), and the `GET /healthz` / `GET /readyz` health probes. Two more are opt-in: the Prometheus scrape endpoint `GET /metrics` and OTLP export of metrics + traces.

## Turning it on

All keys live under the `Fallen8:Observability` section (override via environment with the standard double-underscore form, e.g. `Fallen8__Observability__Prometheus__Enabled=true`). Everything defaults to off.

| Key | Type | Default | Effect |
|---|---|---|---|
| `Prometheus:Enabled` | bool | `false` | Maps `GET /metrics` (Prometheus exposition format). |
| `Prometheus:RequireApiKey` | bool | `false` | Require the API key on `/metrics` instead of the anonymous default. |
| `Otlp:Endpoint` | string | `null` | OTLP endpoint (e.g. `http://localhost:4317`). When set, adds an OTLP exporter for **metrics *and* traces** — point-to-point, no collector required. |
| `TracingSamplingRatio` | double | `1.0` | Root sampling ratio (parent-based sampler); clamped to `[0, 1]`. |
| `StatisticsElementBudget` | int | `1000000` | `GET /statistics` sampling threshold (see below). |
| `StatisticsTopN` | int | `20` | Top-N size for label / property-key cardinality lists. |

An OTel pipeline is registered only when `Prometheus:Enabled` is true **or** `Otlp:Endpoint` is set. Prometheus is metrics-only; traces export exclusively via OTLP. Each enabled exporter logs one posture line at startup.

## GET /metrics (Prometheus)

Mapped only when `Prometheus:Enabled=true`. **Anonymous by default** — the inventory carries aggregate operational numbers only (no user-supplied strings), the same sensitivity as `/status`. Set `Prometheus:RequireApiKey=true` to gate it with the API key; note that requirement only bites when `Fallen8:Security:ApiKey` is actually configured (see [security](security.md)). The exporter mangles instrument names to Prometheus conventions: dots become underscores and counters gain a `_total` suffix.

```bash
curl http://localhost:8080/metrics
```
```powershell
Invoke-RestMethod -Uri http://localhost:8080/metrics
```

**Engine meter `NoSQL.GraphDB.Core`** (per-engine; a host-assigned `fallen8.scope.id` tag carries the namespace id so several namespaces stay distinguishable; durations in seconds, sizes in bytes):

| Instrument | Type | Meaning |
|---|---|---|
| `fallen8.transaction.commits` / `.rollbacks` | Counter | Terminal transactions, tagged `transaction.type` (+ `failure.reason` on rollbacks). |
| `fallen8.transaction.commit.duration` | Histogram | Enqueue → **durable ack** (recorded after the group fsync). |
| `fallen8.transaction.execute.duration` | Histogram | The transaction body on the single writer thread. |
| `fallen8.transaction.queue.depth` | Gauge | Transactions waiting for the writer. |
| `fallen8.transaction.group.size` | Histogram | Group-commit batch size at flush. |
| `fallen8.transaction.nondurable` | Counter | Committed in memory but the WAL frame did not reach disk (degraded log). |
| `fallen8.wal.flush.duration` / `.flush.failures` | Histogram / Counter | The group fsync and its failures. |
| `fallen8.wal.degraded` | Gauge (0/1) | 1 while the WAL failure fence has tripped or an anchored log awaits its paired load. |
| `fallen8.wal.size` | Gauge | Current write-ahead log file length. |
| `fallen8.checkpoint.save.duration` / `.load.duration` | Histogram | Checkpoint save / load duration (load includes WAL replay). |
| `fallen8.checkpoint.save.bytes` / `.load.bytes` | Histogram | Bytes written / read per checkpoint. |
| `fallen8.checkpoint.failures` | Counter | Failed checkpoint ops, tagged `operation` = `save`\|`load`. |
| `fallen8.graph.vertices` / `.edges` | Gauge | Live element counts. |
| `fallen8.index.count` / `.entries` | Gauge | Registered indices; total keys across all indices (aggregate — per-index detail is `/statistics`' job). |

**App meter `NoSQL.GraphDB.App`** (process-wide; the Roslyn codegen signals — see [delegates](delegates.md)):

| Instrument | Type | Meaning |
|---|---|---|
| `fallen8.codegen.cache.hits` / `.misses` | Counter | Compiled-artifact cache lookups, tagged `artifact` = `path_traverser`\|`subgraph`. A miss triggers a compile. |
| `fallen8.codegen.compile.duration` | Histogram | Roslyn compile duration, tagged `artifact`, `success`. |
| `fallen8.codegen.compile.failures` | Counter | Failed compiles, tagged `artifact`. |
| `fallen8.delegate.validate` | Counter | Delegate-fragment validations, tagged `kind`, `result` (aggregate first-pass compile signal). |

**Built-in meters** enabled alongside (native to .NET, no instrumentation packages): `Microsoft.AspNetCore.Hosting`, `Microsoft.AspNetCore.Server.Kestrel`, `System.Runtime`.

**Tag hygiene (hard invariant, pinned by test):** no metric tag value originates from user input — tags are type/enum/artifact names only. Anything user-named (labels, index names, property keys) lives in `/statistics`, behind auth.

A minimal scrape config (add the header block only when `RequireApiKey=true`):

```yaml
# prometheus.yml
scrape_configs:
  - job_name: fallen8
    static_configs: [{ targets: ["localhost:8080"] }]
    # http_headers:            # only when Prometheus:RequireApiKey=true
    #   X-Api-Key: { values: ["<your key>"] }
```

Example PromQL: `histogram_quantile(0.99, rate(fallen8_transaction_commit_duration_bucket[5m]))` (commit p99); `fallen8_wal_degraded == 1` (alert: durability degraded).

## Traces

Spans exist only when `Otlp:Endpoint` is set — with no endpoint no sampler listens, `StartActivity` returns null, and spans cost nothing. Sampling is parent-based with the root ratio from `TracingSamplingRatio`.

**Span-parenting contract.** A transaction body runs on the engine's single background writer thread, not on the request thread. The `fallen8.transaction.execute` span nonetheless parents to the ASP.NET Core request span that enqueued it: the enqueuing `Activity` context is captured at enqueue time and passed across the thread boundary, so queue wait and execution appear nested under the HTTP request span.

| Source | Span | Notes |
|---|---|---|
| `Microsoft.AspNetCore` | server request span | Built-in root span per request. |
| `NoSQL.GraphDB.Core` | `fallen8.transaction.execute` | Parented to the enqueuing request (above); tags `transaction.type`, `transaction.state`, `transaction.durable` (committed only). |
| `NoSQL.GraphDB.Core` | `fallen8.checkpoint.save` / `.load` | Checkpoint operations. |
| `NoSQL.GraphDB.App` | `fallen8.codegen.compile` | Roslyn compile of a filter/cost fragment. |
| `NoSQL.GraphDB.App` | `fallen8.path.search` / `fallen8.subgraph.run` | Algorithm runs. |

## GET /statistics

An **advisory**, lock-free graph-shape snapshot — reads run concurrent with the writer, so it is not transactionally consistent. Always available (no OTel wiring required). Requires the API key when one is configured (it exposes schema-shaped names) and sits under the sensitive fixed-window rate limiter so a runaway scrape loop cannot turn an O(V+E) pass into a DoS.

```bash
curl -H "X-Api-Key: <key>" http://localhost:8080/statistics
```
```powershell
Invoke-RestMethod -Uri http://localhost:8080/statistics -Headers @{ "X-Api-Key" = "<key>" }
```

| Field | Meaning |
|---|---|
| `vertexCount` / `edgeCount` | Exact live counts (O(1)). |
| `vertexLabels` / `edgeLabels` / `propertyKeys` | `{ top: [{ name, count }], distinctTotal }` — top-N by element count plus distinct total. |
| `inDegree` / `outDegree` / `totalDegree` | `{ min, max, mean, p50, p90, p99 }` over the sampled vertices. |
| `indices` | Registered indices: `{ name, type, keys, values }`. |
| `memory` | `processWorkingSetBytes`, `gcHeapBytes`, `gcLastHeapSizeBytes`, `gcFragmentedBytes` — free reads, **never a forced GC**. |
| `computedInMs` | Wall-clock cost of the snapshot. |
| `sampled` / `sampleStride` | See budget below. |
| `embedding` | The embedding provider block (see `/status` below). |

**Budget behavior.** Exact when `V+E ≤ StatisticsElementBudget` (default 1M). Above it the pass touches at most the budget with a uniform stride and flags it: `sampled: true`, `sampleStride: N`. Per-name counts are then as counted **in the sample** (multiply by the stride to extrapolate); `distinctTotal` is distinct-within-the-sample (sampling honestly undercounts distinct values). Degree percentiles from a strided sample remain statistically sound.

## GET /status

The cheap discovery probe — config and O(1) count reads only, no graph pass. Anonymous even when an API key is set, so it doubles as the connection check. Reports:

- `usedMemory`, `vertexCount`, `edgeCount`.
- `indices` — the live index inventory (id, plugin type, capabilities, key/value counts).
- `availableIndexPlugins`, `availablePathPlugins`, `availableAnalyticsPlugins`, `availableServicePlugins` — the discovered [plugins](plugins.md).
- `apiKeyRequired` (server config) and `authenticated` (this request) — a client is authorized iff `!apiKeyRequired || authenticated` (see [security](security.md)).
- `embedding` — the embedding provider state; reading it never triggers the lazy model load. See [semantic traversal](semantic-traversal.md).

```bash
curl http://localhost:8080/status
```
```powershell
Invoke-RestMethod -Uri http://localhost:8080/status
```

## Health probes

Both are anonymous and status-only (no body), always mapped.

| Probe | Healthy (200) when |
|---|---|
| `GET /healthz` | Liveness — the server answers (no checks run). |
| `GET /readyz` | Readiness — load-at-startup has completed (immediately in volatile mode); `503` before that. |

Honest note: hosted services finish startup before Kestrel accepts connections in the current wiring, so today `readyz` is effectively equivalent to `healthz` once the server serves. The endpoint still pins the contract and serves orchestrator convention.

## Overhead

When an exporter is enabled the cost is noise-level — recording a handful of counters/histograms per transaction next to a graph mutation; when disabled it is zero, because no timestamps are taken (hot-path clock reads are gated on `instrument.Enabled`) and `StartActivity` returns null with no listener.

## See also

- [Security](security.md) — whether `/metrics` and `/statistics` require the API key
- [Save games](save-games.md) — what the WAL / checkpoint / degraded-durability metrics mean
- [Delegates](delegates.md) — the codegen cache the `fallen8.codegen.*` metrics track
- [Semantic traversal](semantic-traversal.md) — the `embedding` block on `/status` and `/statistics`
- [Running Fallen-8](running.md) — configuring the server and compose
- [REST API](rest-api.md) — the full endpoint surface
- [Studio](studio.md) — the dashboard that reads `/status` and `/statistics`
