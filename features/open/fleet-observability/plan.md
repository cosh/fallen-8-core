# Fleet observability: Implementation plan

Phased breakdown of [spec.md](./spec.md). Each phase is independently shippable, keeps the build
clean (warnings-as-errors), the suite green, and the single-unit + GPU compose working. Gates are
listed per phase; the last gate of every phase is `dotnet build` + relevant `dotnet test` green.

## Phase 0: Grounding (no product change)

Verify exact code sites and current OSS config formats before writing code (a grounding workflow
does this in parallel). Outputs: precise edit anchors for the apiApp and MCP, and verified config
skeletons for the Collector, Prometheus, Tempo, Loki, and Grafana.

## Phase 1: Producer identity (apiApp)

- `Fallen8:Identity` options class (`Tenant:Id/Name`, `Instance:Id/Name`), bound in `Program.cs`
  next to the existing options; defaults auto-fill (random instance id, `default` tenant, name = id).
- Stamp resource attributes in the OTel `ConfigureResource` block (today only `AddService("fallen8")`):
  `fallen8.tenant.id/name`, `fallen8.instance.id/name`. Keep `service.name=fallen8`.
- Tests (MSTest, `MeterListener` / `ActivityListener` + a resource assertion): every metric and span
  carries the four identity attributes; unset config yields stable auto-generated ids.
- Docs deferred to Phase 5.
- **Gate:** build + observability unit tests green.

## Phase 2: Namespace propagation, analytics span, logs, invariant (apiApp)

- **Namespace enrichment middleware:** resolve the request namespace (from the `/ns/{name}` route or
  the `default` alias) and attach id + name to (a) the built-in HTTP metric via
  `IHttpMetricsTagsFeature`, (b) `Activity.Current`, (c) an `ILogger` scope.
- **`fallen8_namespace_info` observable gauge** (value 1, tags `namespace_id`, `namespace_name`,
  `tenant_id`, `instance_id`), maintained from `Fallen8Namespaces` snapshot; rename-safe (§3.4).
- **`fallen8.analytics.run` span** in `AnalyticsController`, tagged algorithm + result summary,
  matching the path/subgraph pattern.
- **OTel logs:** register the OpenTelemetry `ILogger` provider when an exporter is configured; console
  logging untouched.
- **Narrow the tag-hygiene invariant** (§3.3): update the doc comment on `Fallen8Metrics`, the
  tag-hygiene test, and `NamespaceCollectionTest.cs:335` so identity dimensions are the allowed
  exception while graph content is still forbidden. Add a test that graph content still never tags.
- **Gate:** build + apiApp/namespace tests green; tag-hygiene test asserts graph content absent,
  identity present.

## Phase 3: MCP telemetry (fallen-8-mcp)

- OTel wiring in `fallen-8-mcp/Program.cs` (metrics + traces + logs, OTLP push), `Mcp:Observability`
  + `Mcp:Identity` options mirroring the apiApp; resource attributes from config.
- Per-tool instrument at `ToolCatalog.CallAsync`: `fallen8.mcp.tool.calls` counter +
  `fallen8.mcp.tool.duration` histogram (tags `tool`, `tier`, `result`) + a `fallen8.mcp.tool` span.
- `HttpClient` instrumentation on `Fallen8RestClient` so trace context propagates to REST (end-to-end
  MCP tool -> REST -> engine transaction trace).
- Tests: `CallAsync` records per-tool metric/span with correct tags on ok and error.
- **Gate:** build + MCP tests green.

## Phase 4: Consumer stack + compose (new files, no C# change)

- `observability/` config tree in the repo: `otel-collector-config.yaml` (OTLP receiver; spanmetrics
  connector for per-route/per-algorithm/per-tool RED metrics; promote tenant/instance resource attrs
  to metric labels; export to Prometheus + Tempo + Loki), `prometheus.yml`, `tempo.yaml`, `loki.yaml`,
  Grafana provisioning (`datasources.yaml`, `dashboards.yaml`).
- `docker-compose.observability.yml`: `otel-collector`, `prometheus`, `tempo`, `loki`, `grafana`,
  each with a readiness healthcheck, on `f8-net`, with explicit named volumes
  (`f8-prometheus-data`, `f8-tempo-data`, `f8-loki-data`, `f8-grafana-data`).
- `scripts/env-up.js`: always include the observability compose file (unconditional, unlike the GPU
  file); GPU detection unchanged.
- `docker-compose.yml`: add the OTLP endpoint + default identity env to `fallen8` and `f8-mcp` so the
  bundled instances push out of the box.
- `scripts/env-info.js`: print the Grafana URL.
- **Gate:** `npm run env:up` brings up the whole stack healthy; Grafana reachable; data arrives.

## Phase 5: Dashboards, alerts, docs, diagrams

- Two provisioned Grafana dashboards (§3.8): fleet overview + per-tenant/instance drill-down, with
  tenant/instance/namespace template variables and the `fallen8_namespace_info` join for engine-metric
  namespace names.
- A small Grafana alert-rule set: `fallen8.wal.degraded == 1`, instance down.
- Docs: expand `docs/observability.md` (fleet/consumer section + narrowed invariant), update
  `docs/security.md` (invariant wording + trust-based labeling), add the producer-config block and a
  README "Key features" line + `docs/README.md` index row.
- Architecture diagrams (mandatory freshness): root `README.md` + `docs/architecture.md` mermaid
  diagrams gain the observability stack and the push arrows, in the fixed dark + `#E2001A` style.
- Cross-feature impact recorded in the spec (§5, already drafted); MCP docs get an observability note.
- **Gate:** full `dotnet test` green, OpenAPI snapshot unchanged (or reviewed diff), a fresh
  `npm run env:up` shows both dashboards populated for the bundled instance end to end (metrics,
  traces, logs), GPU auto-detect + single-unit up/down verified.

## Done criteria

Spec §7 acceptance criteria all met; feature directory moves `features/open/fleet-observability/` ->
`features/done/fleet-observability/` when merged.
