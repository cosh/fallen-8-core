# Graph namespaces — implementation plan

Companion to [spec.md](./spec.md). Branch: `feature/graph-namespaces`. Every phase lands green
(build clean, full suite passing) and keeps bare-URL behavior byte-for-byte; the engine
(`fallen-8-core`) is never modified except the metrics tag (Phase 4).

## Phase 1 — Registry + scoped resolution (backward-compat proof)

The "one more level" with only `default` existing; zero observable change.

1. `Fallen8NamespacesOptions` (`Fallen8:Namespaces`, `MaxNamespaces` default 10000) +
   registration in `Program.cs`, documented per-namespace fixed costs in the XML docs.
2. `NamespaceRegistry` (apiApp singleton): extracts today's engine-factory lambda
   (`Program.cs` volatile/durable branches) into `CreateEngine(name, id)`; boots holding only
   `default` wired to the legacy paths. Name validation (`^[a-z0-9-]{1,63}$`), reserved-`default`
   rules, quota check.
3. `IFallen8` moves `AddSingleton` → `AddScoped`, resolved from `HttpContext` route value `ns`
   (absent ⇒ `default`). The force-construction at startup becomes registry boot.
4. `DurabilityLifecycleService` + `SaveGameRegistry` interactions re-pointed at the registry's
   `default` handle (no behavior change yet).
5. Tests: existing suite green unchanged (the proof); new registry unit tests (validation, quota,
   reserved default); `HostedRoutingSmokeTest` still passes on bare paths.

## Phase 2 — Route twins + namespace CRUD

1. `NamespaceRouteConvention` (`IApplicationModelConvention`): for every action with an absolute
   route and no `[InstanceLevel]`, add the `/ns/{ns:regex(...)}` twin selector. `[InstanceLevel]`
   attribute applied per spec §4.1 (save/load/tabularasa/trim, savegames, benchmark/generate,
   delegates/validate, plugin list).
2. Namespace-validation resource filter: unknown `ns` ⇒ 404 problem+json with `namespace`
   extension (via `ProblemResults`).
3. `NamespacesController`: `GET /ns`, `GET /ns/{name}`, `PUT`, `PATCH` (rename), `DELETE` (drop)
   per spec §4.2 — problem+json errors, admin-write gating, `[ProducesResponseType]` + XML docs.
   Drop: registry-remove first, engine disposal after reader drain, then directory delete
   (directory part becomes real in Phase 3).
4. OpenAPI: document-level prefix explanation, shared `ns` parameter description; regenerate
   snapshot (`pwsh scripts/update-openapi-snapshot.ps1`), update `OpenApiDocumentTest` expectations.
5. Tests: twin-vs-bare routing hits distinct engines; cross-namespace isolation (data/ids/indices/
   stored queries/change feed); CRUD status matrix (400/404/409/422 + problem shapes); security
   gating; OpenAPI snapshot.

## Phase 3 — Durability: catalog, per-namespace WAL, save games v2

1. `metadata/namespaces.json` catalog (atomic write, loud corruption, schemaVersion); create/
   rename/drop write it; boot reads it and eagerly constructs engines (`default` implicit on
   legacy paths, others under `namespaces/{id}/`).
2. Per-namespace WAL + checkpoint paths under `namespaces/{id}/`; `Fallen8DurabilityOptions`
   resolution helpers grow id-aware variants.
3. Save-game registry schema v2: per-namespace manifest in each entry; `PUT /save` saves all
   engines; `savegames/{id}/load` + `PUT /load` replace the namespace set (v1 entries ⇒
   default-only); `deleteFiles=true` covers per-namespace files; `DurabilityLifecycleService`
   generalizes (per-namespace newest-save + WAL replay, save-on-shutdown across the set).
4. Tabula rasa: drop all non-default + erase `default`; response/XML docs say instance-wide.
5. Tests: restart-without-save durability, drop-then-restart, rename persistence, save/load
   round-trip with 3 namespaces, v1-entry compat, tabula-rasa semantics, catalog corruption,
   volatile mode. Extend `HostedDurabilityLifecycleTest`, `SaveGamesEndpointTest`,
   `PersistenceHardeningTest` patterns.

## Phase 4 — Observability

1. `Fallen8Metrics` gains an optional system-generated `namespace.id` tag (engine-side, the one
   `fallen-8-core` change); registry passes the namespace id; id→name via `GET /ns`.
2. `/statistics` reports per active namespace like every scoped route; verify no double-counting
   with N engines (test with two namespaces + metric listener).

## Phase 5 — F8 Studio

1. API seam: `buildUrl`/wrappers thread the active namespace (bare for `default`, `/ns/{ns}`
   otherwise); instance-level endpoints stay bare; change feed + bulk included; api-contract tests.
2. Registry: per-instance `activeNamespace`; workspace stores re-keyed
   `f8.workspace.<instanceId>/<ns>` with legacy-store adoption as `default`; remount key
   `active.id + '/' + ns`; extend `instance-isolation.test`.
3. Router: `/q/{ns}/…` parent route for the scoped screens, flat routes redirect to
   `/q/{activeNamespace}/…`, deep-link restore + unknown-namespace recover state.
4. AppShell switcher (mock: filter, rows with counts/tags, + New, Manage…, quota footer) +
   endpoint hint `same origin → /ns/{ns}/*`.
5. Connect NAMESPACES panel: table + actions (Rename / Switch to / Drop with typed
   `ConfirmDialog`), create form with live URL preview + error strip, `default` undeletable,
   "creating" row from bulk-import job state.
6. Dialog wording for instance-wide ops (tabula rasa, save games load "replaces all namespaces",
   benchmark); scope rules ("send to canvas" stays in-namespace).
7. Playwright e2e: create → populate → switch → deep link → drop.

## Phase 6 — Docs + impact closeout

1. Feature README (living doc: usage, URL scheme, durability layout, limits); move
   `features/open/graph-namespaces/` → `features/done/`.
2. Root README + CLAUDE.md architecture note (one-line pointers); save-games /
   durability / stored-query / change-feed feature READMEs get their one-line deltas (spec §8);
   agent-host + mcp-server specs get the `/ns` note. DEBUGGING.md checked for staleness (ports/
   launch configs unchanged — expected no-op).
3. Re-run the spec §8 impact sweep against the final diff; confirm NL-assist needs no
   RETRAIN-LOG entry (bare-path check).
