# Fleet observability

[Observability](observability.md) makes one Fallen-8 process observable. Fleet observability is
the **consumer** side: one place that collects the OpenTelemetry data many Fallen-8 instances
push, keyed by tenant, instance, and namespace, and presents it in a single Grafana pane. It
comes up with `npm run env:up`, so a first-time user sees metrics, traces, and logs immediately.

It has two halves. On the **producer** side the apiApp and the [MCP server](mcp-server.md) stamp a
fleet identity on every signal and add the per-action signals a fleet dashboard needs. On the
**consumer** side a small stack (OpenTelemetry Collector, Prometheus, Tempo, Loki, Grafana)
ingests the push and renders it.

## The identity model

A Fallen-8 process declares, at startup, the tenant it belongs to and its own instance identity;
those become OpenTelemetry **resource attributes** on every metric, trace, and log it emits, so
the consumer can separate the fleet. Namespaces (many per process) carry their own id and name on
the signals scoped to them.

| Level | Id | Name | Config |
|---|---|---|---|
| Tenant | `fallen8.tenant.id` | `fallen8.tenant.name` | `Fallen8:Identity:Tenant:{Id,Name}` |
| Instance | `fallen8.instance.id` | `fallen8.instance.name` | `Fallen8:Identity:Instance:{Id,Name}` |
| Namespace | `fallen8.scope.id` (host-assigned) | `fallen8.namespace.name` | derived from the namespace catalog |

Everything auto-fills, so identity is on with **zero config**: an unset tenant becomes `default`,
an unset instance id becomes a stable per-process `f8-` token, and each name falls back to its id.
Set real values (env `Fallen8__Identity__Tenant__Id`, etc.) to get meaningful fleet labels. The
namespace name is joined to engine-metric series by the consumer via a `fallen8_namespace_info`
metric, because the engine holds only the stable id by design (a rename must not orphan series).

## Collection is push-only

Each Fallen-8 apiApp and MCP server sets its OTLP endpoint to the Collector; nothing scrapes the
producers. Push is the only path that carries traces and logs, and it needs no target list. The
Collector is the single ingest point and the enrichment seam: it derives per-action RED metrics
from spans (the `spanmetrics` connector) and promotes the identity resource attributes to
Prometheus labels.

## What every user action looks like

Three levels, all filterable by tenant / instance / namespace:

- **REST endpoints** from the built-in `http.server.request.duration` metric, keyed by route.
- **Algorithms** (path, subgraph, analytics) from the `fallen8.path.search` / `fallen8.subgraph.run`
  / `fallen8.analytics.run` spans, turned into call-rate and latency metrics by the Collector.
- **MCP tools** from the `fallen8.mcp.tool.calls` / `.duration` instruments the MCP server records
  at its one dispatch seam, tagged with the tool, its tier, and ok/error.

Traces run end to end: an MCP tool span parents the REST request span, which parents the engine's
`fallen8.transaction.execute` span.

## The dashboards

Two dashboards are provisioned as code (in `observability/grafana/dashboards`), both with tenant,
instance, and namespace filter variables:

- **Fleet overview** - instances up, the namespace inventory, commit p99 per tenant, transaction
  queue depth, a WAL-degraded alert panel (red on any instance), commit/rollback throughput, and
  the busiest actions across REST routes, algorithms, and MCP tools.
- **Tenant / instance drill-down** - commit and execute duration percentiles, queue depth and group
  size, checkpoint save/load duration and bytes, WAL flush duration and degraded, vertex/edge and
  index gauges, codegen cache hit rate, HTTP rate/latency by route, per-algorithm and per-MCP-tool
  breakdowns, plus a Tempo traces panel and a Loki logs panel scoped to the selected instance.

## Running it

The stack always comes up with the environment:

```bash
npm run env:up
```

| Service | URL | What it is |
|---|---|---|
| Grafana | http://localhost:3000 | The single pane (open on the trusted network; `F8_GRAFANA_PORT` overrides) |
| OTLP ingest | localhost:4317 (gRPC) / :4318 (HTTP) | Where Fallen-8 instances push |

Prometheus, Tempo, and Loki are internal to the compose network. The whole environment is managed
as one unit - never start or stop individual containers. The bundled Fallen-8 and MCP are wired to
push automatically, under tenant `local`, instance `f8-local` (override with `F8_TENANT_ID`,
`F8_INSTANCE_ID`, `F8_TENANT_NAME`, `F8_INSTANCE_NAME`).

## Connecting an external Fallen-8

Point any Fallen-8 (or MCP server) at the Collector and give it an identity:

```bash
Fallen8__Observability__Otlp__Endpoint=http://<collector-host>:4317
Fallen8__Identity__Tenant__Id=<tenant-guid>
Fallen8__Identity__Tenant__Name=<tenant name>
Fallen8__Identity__Instance__Id=<instance-guid>
Fallen8__Identity__Instance__Name=<instance name>
```

The MCP mirror is `Mcp__Observability__Otlp__Endpoint` and `Mcp__Identity__*`. Set the MCP's
instance id to the apiApp instance it fronts, so its tool panels resolve under that instance.

Reconfiguration is environment config applied on `env:up` (the pipeline wires at process start),
not live hot-reload.

## What isolation is and is not

**Guaranteed.** Every signal from a correctly configured instance carries the tenant, instance, and
namespace ids (and names), so the dashboard separates the fleet. Graph content (labels, index
names, property keys, filter fragments) still never becomes a metric tag - only the operator-named
identity dimensions do (the narrowed [tag-hygiene invariant](observability.md)).

**Not guaranteed (trust-based, stated plainly).**

- **Tenant labels are self-declared.** An instance asserts its own identity from its config and the
  Collector believes it. A misconfigured or hostile instance can claim another tenant's label.
  There is no enforcement in v1. Hard isolation would require the Collector to derive the label from
  a per-tenant ingest token and ignore the producer's claim (the revisit trigger: an untrusted or
  internet-exposed tenant).
- **Ingest is trusted-network only.** The Collector's OTLP port is open with no auth and no TLS. The
  Fallen-8 OTLP exporter has no in-config auth or TLS: it can send a bearer token only via the
  standard `OTEL_EXPORTER_OTLP_HEADERS` environment variable, and TLS is only whatever an `https://`
  endpoint plus the default trust store give. Authenticated remote ingest is therefore possible but
  env-var-driven and out of scope for v1.
- **Grafana** has only its login (open on the trusted network by default).

## See also

- [Observability](observability.md) - the single-process producer signals this consumes
- [MCP server](mcp-server.md) - the per-tool telemetry source
- [Security](security.md) - the tag-hygiene invariant and the trust posture
- [Running Fallen-8](running.md) - the one-command environment
- [Architecture](architecture.md) - how the observability stack fits the whole
