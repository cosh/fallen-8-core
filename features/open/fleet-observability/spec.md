# Fleet observability (consumer + producer identity): Specification

> **Status:** Open (spec for sign-off). Branch `feature/fleet-observability`.
> Renamed from the working title `observability-consumer`: the work is producer plus
> consumer, so the name reflects the whole (a fleet of Fallen-8 instances observed in one
> pane), not just the collector container.

## 1. Overview & motivation

The [observability feature](../../done/observability/) made a single Fallen-8 process observable:
BCL instruments in the engine, an OTLP push exporter and a Prometheus scrape endpoint in the
apiApp, `GET /statistics`, and health probes. It deliberately stopped at one process. Its own
non-goal named the exact trigger to go further: *"No multi-tenant / instance labels ... Revisit
only if multi-engine hosting ever becomes a feature."* Namespaces landed (multi-engine hosting),
and now the ask is the consumer side: **one place that collects the OpenTelemetry data emitted by
many Fallen-8 instances and presents it in a single dashboard**, keyed by tenant, instance, and
namespace, showing every action a user can perform (REST endpoints, path/subgraph/analytics
algorithms, and MCP tools).

Delivering that is not consumer-only. The producer does not emit the identity or the per-action
signals the dashboard needs. So this feature has two halves:

1. **Producer identity + action signals** (`fallen-8-core-apiApp` and `fallen-8-mcp`; the engine
   `fallen-8-core` stays untouched). Stamp a tenant/instance identity and carry namespace id +
   name, per-algorithm analytics, and per-MCP-tool usage onto the emitted telemetry.
2. **The consumer stack** (new): an OpenTelemetry Collector, Prometheus (metrics), Tempo
   (traces), Loki (logs), and Grafana (the single pane, dashboards provisioned as code), wired
   into the compose environment so it comes up with `npm run env:up` and the bundled Fallen-8 and
   MCP push to it out of the box.

**Honesty note up front.** Tenant isolation here is **soft and trust-based**, not enforced. Each
Fallen-8 self-declares its tenant/instance identity from its own configuration, and the consumer
believes it. The push model, the trusted-network posture, and this labeling are all trust-based;
§3.9 states exactly what is and is not guaranteed. This matches the trusted, single-operator
reality Fallen-8 is built for; the revisit trigger for hard isolation is an untrusted or
internet-exposed tenant.

## 2. Goals / non-goals

**Goals**

- **A tenant > instance > namespace identity on every signal.** A Fallen-8 process is configured
  at startup with a tenant id + name and an instance id + name; every metric, trace, and log it
  emits carries those (as OTel resource attributes), plus the namespace id + name for
  namespace-scoped signals. The MCP server carries the same identity for the target it fronts.
- **Push-only collection over OTLP.** Each Fallen-8 and MCP server sets its OTLP endpoint to the
  consumer's Collector. No pull, no scrape target list, no discovery service. The bundled compose
  instances are wired automatically.
- **Every user action is visible**, at three levels: REST endpoints (built-in HTTP metric by
  route), path/subgraph/analytics algorithms (spans, turned into rate/error/duration metrics by
  the Collector's spanmetrics connector), and MCP tools (a new per-tool metric + span at the
  MCP dispatch seam).
- **Metrics, traces, and logs**, all three, carrying the identity, correlatable in Grafana.
- **Dashboards as code.** Grafana dashboards, datasources, and the Collector/Prometheus/Tempo/Loki
  config are all provisioned from files in the repo, reproducible on every `env:up`.
- **On by default.** The stack comes up with `npm run env:up`, the bundled Fallen-8 and MCP push
  to it, and a first-time user sees data immediately. GPU auto-detection is unchanged.
- **Reconfigurable.** Identity, the OTLP endpoint, and sampling are environment configuration,
  changeable without code by editing env and re-running `env:up`.

**Non-goals** (each with its revisit trigger)

- **No hard, enforced tenant isolation in v1.** Labeling is trust-based (§3.9). *Revisit when an
  untrusted or internet-exposed tenant appears:* then the Collector must stamp the tenant label
  from a per-tenant ingest token/endpoint and ignore what the producer claims, and ingest needs
  TLS + per-tenant auth.
- **No remote/internet ingest hardening in v1.** The Collector's OTLP port is exposed on the
  trusted compose/host network only. *Revisit with the isolation trigger above;* §3.9 records
  what the Fallen-8 OTLP exporter can and cannot do for auth/TLS today.
- **No HA / clustering / long-term object-store backends.** Single Prometheus, single Tempo,
  single Loki, single Grafana, local volumes. *Revisit when retention or availability outgrows one
  host.*
- **No alerting/paging integrations (PagerDuty, email, etc.) in v1.** Dashboards and a small set of
  Grafana alert rules on the critical signals (`wal.degraded`, instance down) are the deliverable.
  *Revisit if an operator wants routed notifications.*
- **No auth in front of Grafana beyond its default login in v1** (trusted network). *Revisit with
  the isolation trigger.*
- **No change to the engine `fallen-8-core`.** Identity and namespace naming are hosting concerns;
  the engine keeps emitting its id-tagged instruments exactly as today (§3.4 explains the one place
  this constrains the design).

## 3. Design

### 3.1 The identity model

Three levels, each an id (stable, machine) plus a name (human, mutable):

| Level | Id | Name | Where it comes from | Where it rides |
|---|---|---|---|---|
| **Tenant** | `fallen8.tenant.id` | `fallen8.tenant.name` | startup config (new `Fallen8:Identity` section) | OTel **resource attribute** (all signals) |
| **Instance** | `fallen8.instance.id` | `fallen8.instance.name` | startup config | OTel **resource attribute** (all signals) |
| **Namespace** | `fallen8.namespace.id` (= today's `fallen8.scope.id`) | `fallen8.namespace.name` | the namespace catalog (already persisted) | metric tag + span/log attribute on namespace-scoped signals |

Tenant and instance are **per process**, so they are OTel resource attributes: set once at startup
and attached automatically to every metric, span, and log the process emits. That single mechanism
satisfies "tenant + instance id and name on every signal" without touching any instrument.

Namespace is **per engine within the process** (one process hosts many), so it cannot be a resource
attribute. Its id is already emitted on every engine metric as the `fallen8.scope.id` meter tag
(host-assigned, stable, restart-safe, persisted in `namespaces.json`); this spec keeps that and
also carries id + name on the host-originated signals that lack it today (HTTP metrics, controller
spans, logs).

**Ids default to auto-generated GUIDs when unset**, so the feature is on with zero config: a
process with no `Fallen8:Identity` gets a random instance id and a `default` tenant, and still
reports coherently. Names default to the id. An operator sets real values to get meaningful labels.

### 3.2 Collection model: push-only OTLP

Each Fallen-8 apiApp and each MCP server sets `Fallen8__Observability__Otlp__Endpoint` (MCP:
`Mcp__Observability__Otlp__Endpoint`) to the Collector's OTLP address. Push is the only trace and
log path and needs no target inventory. The Collector is the single ingest point and the seam where
enrichment (spanmetrics, resource-to-label promotion) happens. Discovery is static configuration;
there is deliberately no registration step or service discovery (rejected as machinery a trusted
fleet does not need; revisit if the fleet becomes dynamic and large).

### 3.3 Names in tags: an explicit, narrowed invariant change

The [observability](../../done/observability/) tag-hygiene invariant is, verbatim: *"no metric tag
value may originate from user input."* It is pinned by tests (`Fallen8Metrics` doc,
`NamespaceCollectionTest.cs:335`, the tag-hygiene meter tests). Carrying tenant/instance/namespace
**names** on telemetry (the chosen option) changes it. We narrow it rather than delete it:

> **New invariant:** no metric tag value may originate from **graph content** (vertex/edge labels,
> index names, property keys, filter fragments, or any user-supplied graph data). **Identity
> dimensions** (tenant, instance, namespace id and name) are the sole exception: they are
> operator-controlled, bounded-cardinality identifiers, not graph content.

Why this is honest and bounded:

- **Cardinality stays bounded.** The dimension count is (tenants x instances x namespaces), all
  operator-provisioned and small, not per-vertex or per-property. The thing the old invariant most
  guarded against (unbounded series from graph data) is untouched: labels, index names, and
  property keys still never become tags.
- **The anonymous `/metrics` leak does not apply.** This feature is push-only to a trusted
  Collector; the Prometheus scrape endpoint (`Fallen8:Observability:Prometheus:Enabled`) stays off
  in the compose default, so namespace names never appear on an anonymous endpoint. If an operator
  turns scrape on and exposes it, that is the same explicit decision the security doc already
  covers; the spec's README will call it out.
- **Tests and docs are updated in the same PR** (not silently): the tag-hygiene test carves out the
  identity dimensions and still asserts graph content never appears; `docs/observability.md`,
  `docs/security.md`, and the observability feature README get the narrowed wording.

### 3.4 The one architectural constraint: namespace name on engine-originated metrics

The engine (`fallen-8-core`) deliberately does not know its namespace name: namespacing is a hosting
concern, and the engine receives only the opaque `metricsScopeId` (the namespace id) at
construction. Two consequences make a raw "namespace name as an engine meter tag" wrong:

1. It would couple the engine to the hosting concept the codebase keeps out of it.
2. The meter tag is fixed at construction, but a namespace can be **renamed** at runtime
   (`TryRename`), which would orphan the tag.

So engine-originated metrics (commit/execute durations, WAL, checkpoint, element/index gauges) carry
the namespace **id** only, exactly as today. The **name** is attached by the Collector via an
**info-metric join**, the standard Prometheus pattern: the apiApp exports
`fallen8_namespace_info{namespace_id, namespace_name, tenant_id, instance_id} = 1`, updated on
create/rename/drop, and Grafana joins engine metrics to the name on `namespace_id`
(`... * on(namespace_id) group_left(namespace_name) fallen8_namespace_info`). Rename-safe (the info
metric updates), engine-clean (no name reaches the engine), and the dashboard still shows the name
everywhere. Host-originated signals (HTTP metrics, controller spans, logs) carry the name directly,
because the host knows it per request. This is the one place "names on everything" is delivered by a
query-time join rather than a literal series label; the honesty section and README state it plainly.

### 3.5 Producer changes: `fallen-8-core-apiApp`

- **`Fallen8:Identity` options** (`Tenant:Id`, `Tenant:Name`, `Instance:Id`, `Instance:Name`),
  bound like the existing `Fallen8:Observability` / `Fallen8:Security` options; defaults auto-fill
  as §3.1. Stamped as OTel resource attributes in the `ConfigureResource` call that today only does
  `AddService("fallen8")`.
- **Observability on by default in compose** (not in code: the code stays opt-in, the compose env
  sets the OTLP endpoint). Startup keeps logging its one posture line per exporter.
- **Namespace id + name on host-originated signals:** a small middleware resolves the request's
  namespace (from the `/ns/{name}` route or the `default` alias) and (a) enriches the built-in HTTP
  metric via `IHttpMetricsTagsFeature`, (b) tags the current Activity, and (c) opens an `ILogger`
  scope, so HTTP metrics, controller spans (`fallen8.path.search`, `fallen8.subgraph.run`, the new
  analytics span), and logs all carry namespace id + name.
- **`fallen8_namespace_info` gauge** (§3.4), maintained by `Fallen8Namespaces` on create/rename/drop.
- **Analytics spans:** a `fallen8.analytics.run` span in `AnalyticsController` tagged with the
  algorithm name and result summary, matching the existing path/subgraph span pattern (the chosen
  option). Analytics has no telemetry today.
- **OTel logs:** register the OpenTelemetry logging provider (`ILogger` to OTLP) when observability
  is enabled, so existing structured logs export with the resource identity attached. Console
  logging stays.

### 3.6 Producer changes: `fallen-8-mcp`

The MCP server emits **zero** OpenTelemetry today (verified). It gets:

- OTel wiring (metrics + traces + logs, OTLP push) with its own `Mcp:Observability` options
  mirroring the apiApp, and the **same identity** resource attributes from config (it fronts one
  target Fallen-8, so it declares that target's tenant/instance).
- **Per-tool telemetry at the one dispatch seam** (`ToolCatalog.CallAsync`): a counter
  `fallen8.mcp.tool.calls` and a histogram `fallen8.mcp.tool.duration`, tagged `tool` (the bounded
  set: get, search, paths, subgraph, analytics, mutate, namespace, admin, plugins, overview),
  `tier` (read/write/admin), and `result` (ok/error), plus a `fallen8.mcp.tool` span. Tool names are
  a fixed bounded enum, so this is invariant-safe.
- **Trace-context propagation** to the downstream REST call (OTel's `HttpClient` instrumentation), so
  a trace runs end to end: MCP tool span -> REST request span -> engine transaction span.

### 3.7 Consumer stack

New containers, all provisioned from repo files:

| Container | Role | Storage |
|---|---|---|
| **otel-collector** | single OTLP ingest; `spanmetrics` connector (per-algorithm/per-tool/per-route RED metrics from spans); promote tenant/instance resource attributes to metric labels; fan out to the three stores | none |
| **prometheus** | metrics store (receives via Collector remote-write or Prometheus OTLP receiver) | `f8-prometheus-data` volume |
| **tempo** | trace store | `f8-tempo-data` volume |
| **loki** | log store | `f8-loki-data` volume |
| **grafana** | the single pane; datasources + dashboards provisioned | `f8-grafana-data` volume |

The Collector is where the trust-based labeling and the enrichment live, and the seam where enforced
per-tenant labeling would later be added (§2 revisit trigger). Spanmetrics turns the existing and new
spans into the per-action metrics the dashboards need without extra producer counters.

### 3.8 Dashboards (provisioned)

Two dashboards for v1, both with tenant / instance / namespace filter variables:

1. **Fleet overview** (all tenants): an up/ready table across instances (from health + the
   info-metric), commit p99 per tenant, transaction queue depth, a `fallen8.wal.degraded == 1` alert
   panel (red on any instance), commit/rollback throughput, and a top-N "busiest actions" panel
   spanning REST routes, algorithms, and MCP tools.
2. **Per-tenant / per-instance drill-down:** commit + execute duration percentiles, queue depth +
   group size, checkpoint save/load duration + bytes, WAL flush duration + degraded, vertex/edge +
   index gauges, codegen cache hit rate, HTTP rate/latency by route, **per-algorithm** (path,
   subgraph, analytics) rate/latency from spanmetrics, **per-MCP-tool** call rate/latency/error rate,
   and a traces panel linking into Tempo for that instance (logs linked into Loki by the same
   identity).

Priority signals surfaced first: commit p99, queue depth, `wal.degraded`. Then checkpoint
durations/bytes, element/index gauges, codegen cache hit rate. The "which MCP tool / which algorithm"
panels are first-class, per the ask.

### 3.9 Security & isolation: what is and is not guaranteed

**Guaranteed:**

- Every signal from a correctly configured instance carries tenant/instance/namespace ids (and names
  per §3.3/§3.4), so the dashboard can filter and separate tenants.
- Graph content (labels, index names, property keys, fragments) still never becomes a metric tag
  (§3.3). `/statistics` remains the auth-gated home for schema-shaped data.
- The engine is unchanged; enabling the stack has the measured near-zero overhead of the existing
  observability feature.

**NOT guaranteed (trust-based, stated plainly):**

- **Tenant labels are self-declared.** An instance asserts its own tenant/instance identity from its
  config; the Collector believes it. A misconfigured or hostile instance can claim another tenant's
  label. There is no enforcement in v1. Hard isolation would require the Collector to derive the
  label from a per-tenant ingest token/endpoint and ignore the producer's claim (revisit trigger).
- **Ingest is trusted-network-only.** The Collector's OTLP port is open on the compose/host network
  with no auth and no TLS in v1. The Fallen-8 OTLP exporter, verified against code, has **no
  in-config auth or TLS/mTLS**: it can send a bearer token only via the standard
  `OTEL_EXPORTER_OTLP_HEADERS` environment variable (which the SDK honors although the app sets
  nothing), and TLS is only whatever an `https://` endpoint plus the default trust store give. So
  authenticated remote ingest is possible but env-var-driven and undocumented on the Fallen-8 side;
  it is out of scope for v1 and called out here rather than hidden.
- **Grafana** has only its default login; the stack assumes a trusted network.
- **"Reconfigurable at any time"** means environment configuration changed and applied on container
  recreate (`env:up`), not live hot-reload: the OTel pipeline wires at process start.

### 3.10 Compose wiring

- The stack is **always on** with `npm run env:up`. Placement: a dedicated
  `docker-compose.observability.yml` that `scripts/env-up.js` **always** includes (the same
  mechanism the GPU file uses, but unconditional), keeping the base `docker-compose.yml` readable
  while the environment still comes up and down as one unit.
- The bundled `fallen8` and `f8-mcp` services get the OTLP endpoint (`http://otel-collector:4317`)
  and a default identity (tenant `local`, instance `f8-local`) wired in, so data flows on first
  start.
- **GPU:** unchanged and orthogonal. `env:up` keeps auto-detecting the NVIDIA GPU and applying
  `docker-compose.gpu.yml` conditionally (a device reservation hard-fails on non-GPU hosts, which is
  why that file stays separate and conditional). The observability services use no GPU. This
  addresses "not sure we need a dedicated GPU variant": the variant is not a user choice, it is how
  graceful GPU auto-detection is implemented, and it keeps working untouched.
- New named volumes follow the existing explicit-name pattern (`f8-prometheus-data`, etc.). Readiness
  healthchecks on every new service, matching the repo's compose style.

## 4. Producer configuration (what an F8 / MCP needs to connect)

Fallen-8 apiApp (env / compose form):

```
Fallen8__Observability__Otlp__Endpoint=http://otel-collector:4317
Fallen8__Identity__Tenant__Id=<tenant-guid>
Fallen8__Identity__Tenant__Name=<tenant name>
Fallen8__Identity__Instance__Id=<instance-guid>
Fallen8__Identity__Instance__Name=<instance name>
```

MCP server:

```
Mcp__Observability__Otlp__Endpoint=http://otel-collector:4317
Mcp__Identity__Tenant__Id=<same tenant-guid as its target F8>
Mcp__Identity__Instance__Id=<the target instance-guid>
```

An external (non-compose) Fallen-8 joins the fleet by pointing its OTLP endpoint at the Collector's
exposed address and setting its identity. The README documents this exact block.

## 5. Impact on existing features (mandatory cross-feature sweep)

- **[observability](../../done/observability/):** this feature extends it. The tag-hygiene invariant
  is narrowed (§3.3) with tests and docs updated in the same PR; the "no multi-tenant labels"
  non-goal is now consciously retired (its own revisit trigger fired). The Prometheus scrape path is
  untouched and stays off by default.
- **[graph-namespaces](../../done/graph-namespaces/):** the namespace id (`scope.id`) and the
  name/rename semantics are consumed as-is; `fallen8_namespace_info` is additive. No engine or
  routing change.
- **[mcp-server](../../done/mcp-server/):** gains its first telemetry. The engine->REST->MCP coverage
  test surface is unaffected (no new REST endpoints); this adds observability, not tools. The MCP
  docs get an observability section.
- **OpenAPI snapshot:** no new controllers or routes on the public REST surface (identity is config,
  namespace enrichment is middleware, analytics span is internal). If any XML doc changes, the
  snapshot is regenerated per the quality gate. Expected: no snapshot change.
- **Studio UI / NL-assist dataset:** no contract change; no impact. Not retraining anything.
- **Architecture diagrams (mandatory freshness):** both the root `README.md` diagram and
  `docs/architecture.md` gain the observability stack and the push arrows from apiApp + MCP to the
  Collector, in the fixed dark + `#E2001A` style. `docs/observability.md` gets the fleet/consumer
  section and the narrowed invariant; a new `docs/*.md` page (or an expanded observability page) and
  a README "Key features" line make it discoverable.
- **Compose / GPU:** §3.10. Single-unit and GPU auto-detect preserved.

## 6. Phasing (small, reviewable steps)

1. **Producer identity (apiApp):** `Fallen8:Identity` options + resource attributes + defaults;
   tests that every signal carries tenant/instance ids. No consumer yet.
2. **Namespace propagation (apiApp):** the middleware (HTTP metric + span + log enrichment), the
   `fallen8_namespace_info` gauge, the analytics span; the narrowed tag-hygiene invariant + updated
   tests/docs.
3. **MCP telemetry:** OTel wiring, per-tool metric/span, identity, trace-context propagation; tests.
4. **Consumer stack:** the Collector + Prometheus + Tempo + Loki + Grafana containers, all config
   provisioned, spanmetrics + resource-attribute promotion, readiness checks; a dedicated compose
   file wired always-on via `env-up.js`; bundled instances auto-pushing.
5. **Dashboards + logs + docs:** the two provisioned Grafana dashboards, a small alert-rule set,
   OTel logs end to end, and all doc/diagram/README updates.

Each phase keeps the build clean, the suite green, and the single-unit + GPU compose working.

## 7. Acceptance criteria

- A fresh `npm run env:up` brings up Fallen-8, MCP, and the full observability stack; Grafana shows
  the bundled instance's metrics, traces, and logs within a minute, all tagged with tenant, instance,
  and namespace.
- Two Fallen-8 instances configured with different `Fallen8:Identity` values appear as two tenants,
  filterable and separable in the dashboards.
- The dashboards show, per tenant/instance: commit p99, queue depth, `wal.degraded`, checkpoint
  durations/bytes, element/index gauges, codegen cache hit rate, HTTP rate/latency by route,
  per-algorithm (path/subgraph/analytics) rate/latency, and per-MCP-tool call rate/latency/errors.
- An end-to-end trace exists from an MCP tool call through the REST request to the engine
  transaction span.
- Graph content (labels/index names/property keys/fragments) appears in **no** metric tag (test);
  identity dimensions do appear (test).
- Build clean (warnings-as-errors), suite green, OpenAPI snapshot unchanged (or regenerated with a
  reviewed diff), both architecture diagrams and the README updated.
- The GPU auto-detection path and the single-unit up/down flow are unchanged (verified).

## 8. Risks

- **Container count.** The default `env:up` grows by five services. Mitigated: modest images, local
  volumes, readiness gating, and honest sizing in the README (§ non-goals cap retention). This is the
  cost of "on by default so users try it immediately", which the user chose deliberately.
- **spanmetrics cardinality.** Per-route x per-algorithm x per-tenant series could grow. Mitigated by
  the bounded label sets (routes, plugin names, tool names are all enums) and by keeping namespace
  name off engine series (§3.4). Watched via a Collector limit.
- **Invariant regression.** Narrowing the tag-hygiene rule risks a future contributor adding graph
  content to a tag. Mitigated by keeping the test, only carving out the named identity dimensions.
- **Producer scope creep.** This touches two deployables. Mitigated by phasing (§6): phase 1 alone
  delivers tenant/instance identity; each later phase is independently shippable.
- **"On by default" overhead.** Every dev run now records telemetry. The measured overhead is
  noise-level (observability feature benchmark), so acceptable; re-verified in phase 1.

## 9. Keep (do not regress)

- **The engine stays dependency-clean and namespace-name-unaware** (§3.4). No `fallen-8-core` change.
- **Graph content never becomes a metric tag** (the surviving half of the invariant).
- **Single-unit compose and GPU auto-detection** (§3.10).
- **Zero-config-off in code:** the apiApp/MCP still register no OTel pipeline unless an exporter is
  configured; "on by default" is a compose decision, not a code default, so a bare `dotnet run`
  stays off.
- **The observability feature's contracts** (`/statistics`, `/status`, health probes, the existing
  metric inventory and its ids) are unchanged and additive-only.
