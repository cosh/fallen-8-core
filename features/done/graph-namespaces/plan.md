# Graph namespaces — implementation plan

Companion to [spec.md](./spec.md). Branch: `feature/graph-namespaces`. Every phase lands green
(build clean, full suite passing) and keeps bare-URL behavior byte-for-byte; the engine
(`fallen-8-core`) is never modified except the metrics tag (Phase 4). Terminology per spec §1
(Fallen-8 / namespace / engine) is applied from the first commit.

## Phase 1 — Namespace collection + scoped resolution (backward-compat proof)

The "one more level" with only `default` existing; zero observable change.

1. `Fallen8NamespacesOptions` (`Fallen8:Namespaces`, `MaxNamespaces` default 10000) +
   registration in `Program.cs`, documented per-namespace fixed costs in the XML docs.
2. `Fallen8Namespaces` (apiApp singleton — the collection) + `Namespace` (name, id, engine,
   createdAt, state): extracts today's engine-factory lambda (`Program.cs` volatile/durable
   branches) into `CreateEngine(name, id)`; boots holding only `default` wired to the legacy
   paths. Name validation (`^[a-z0-9-]{1,63}$`), reserved-`default` rules, quota check.
3. `IFallen8` moves `AddSingleton` → `AddScoped`, resolved from `HttpContext` route value `ns`
   (absent ⇒ `default`). The force-construction at startup becomes collection boot.
4. `DurabilityLifecycleService` + `SaveGameRegistry` interactions re-pointed at the collection's
   `default` namespace (no behavior change yet).
5. Tests: existing suite green unchanged (the proof); new collection unit tests (validation,
   quota, reserved default); `HostedRoutingSmokeTest` still passes on bare paths.

## Phase 2 — Route twins + namespace CRUD

1. `NamespaceRouteConvention` (`IApplicationModelConvention`): for every action with an absolute
   route and no `[Fallen8Level]`, add the `/ns/{ns:regex(...)}` twin selector. `[Fallen8Level]`
   attribute applied per spec §5.1 (savegames, benchmark/generate, delegates/validate, plugin
   list; the `…/all` endpoints arrive in Phase 3 already marked).
2. Namespace-validation resource filter: unknown `ns` ⇒ 404 problem+json with `namespace`
   extension (via `ProblemResults`).
3. `NamespacesController`: `GET /ns`, `GET /ns/{name}`, `PUT`, `PATCH` (rename), `DELETE` (drop)
   per spec §5.3 — problem+json errors, admin-write gating, `[ProducesResponseType]` + XML docs.
   Drop: collection-remove first, engine disposal after reader drain, then directory delete
   (directory part becomes real in Phase 3).
4. OpenAPI: document-level prefix explanation, shared `ns` parameter description; regenerate
   snapshot (`pwsh scripts/update-openapi-snapshot.ps1`), update `OpenApiDocumentTest`
   expectations.
5. Tests: twin-vs-bare routing hits distinct engines; cross-namespace isolation (data/ids/indices/
   stored queries/change feed); CRUD status matrix (400/404/409/422 + problem shapes); security
   gating; OpenAPI snapshot.

## Phase 3 — Durability: catalog, per-namespace WAL, save/load/tabula-rasa matrix

1. `metadata/namespaces.json` catalog (atomic write, loud corruption, schemaVersion); create/
   rename/drop write it; boot reads it and eagerly constructs engines (`default` implicit on
   legacy paths, others under `namespaces/{id}/`).
2. Per-namespace WAL + checkpoint paths under `namespaces/{id}/`; `Fallen8DurabilityOptions`
   resolution helpers grow id-aware variants.
3. Save/load/tabula-rasa contract per spec §5.2:
   - `/save`, `/load`, `/tabularasa`, `/trim` become namespace-scoped (twinned; bare = `default`).
   - New `[Fallen8Level]` endpoints: `PUT /save/all` (one n-member entry; shutdown auto-save uses
     it) and `HEAD /tabularasa/all` (factory reset: drop non-default, erase `default`).
   - Save-game registry schema v2: 1..n-member `namespaces` manifest per entry; v1 entries read
     as default-only. `PUT /savegames/{id}/load` restores exactly the entry's namespaces
     (recreate dropped, replace existing, touch nothing else); `?namespace={name}` restores one
     (404 when not in the entry); `deleteFiles=true` covers per-namespace files.
   - `DurabilityLifecycleService` generalizes: per-namespace newest-entry boot + WAL replay,
     `/save/all` semantics on clean shutdown.
4. Tests: restart-without-save durability, drop-then-restart, rename persistence, per-namespace
   save entry, save-all entry, single-namespace restore leaves siblings untouched, restore
   recreates a dropped namespace, `?namespace=` not-in-entry 404, v1-entry compat, tabula-rasa
   scoped vs factory reset, catalog corruption, volatile mode. Extend
   `HostedDurabilityLifecycleTest`, `SaveGamesEndpointTest`, `PersistenceHardeningTest` patterns.

## Phase 4 — Observability

1. `Fallen8Metrics` gains an optional system-generated `namespace.id` tag (engine-side, the one
   `fallen-8-core` change); the collection passes the namespace id; id→name via `GET /ns`.
2. `/statistics` reports per active namespace like every scoped route; verify no double-counting
   with N engines (test with two namespaces + metric listener).

## Phase 5 — F8 Studio

1. API seam: `buildUrl`/wrappers thread the active namespace and **always** send `/ns/{ns}` for
   scoped calls (`default` included); Fallen-8-level endpoints (`/save/all`, `/tabularasa/all`,
   save games, benchmark) stay bare; change feed + bulk included; api-contract tests.
2. Registry: per-instance `activeNamespace`; workspace stores re-keyed
   `f8.workspace.<instanceId>/<ns>` with legacy-store adoption as `default`; remount key
   `active.id + '/' + ns`; extend `instance-isolation.test`.
3. Router: `/q/{ns}/…` parent route for the scoped screens, flat routes redirect to
   `/q/{activeNamespace}/…`, deep-link restore + unknown-namespace recover state.
4. AppShell switcher (mock: filter, rows with counts/tags, + New, Manage…, quota footer) +
   endpoint hint `same origin → /ns/{ns}/*` (shown for `default` too).
5. Connect NAMESPACES panel: table + actions (Rename / Switch to / Drop with typed
   `ConfirmDialog`), create form with live URL preview + error strip, `default` undeletable,
   "creating" row from bulk-import job state.
6. Save & erase actions per spec §8.6: Dashboard "Save namespace" vs "Save all namespaces",
   namespace-scoped tabula rasa vs factory reset; Save-games rows list contained namespaces with
   "Load entry" / "Load one namespace…"; every dialog names its blast radius ("namespace
   `flights`" vs "Fallen-8-wide — all namespaces").
7. Playwright e2e: create → populate → save → switch → drop → restore from entry.

## Phase 6 — Docs + impact closeout

1. Feature README (living doc: terminology, usage, URL scheme, save/restore matrix, durability
   layout, limits); move `features/open/graph-namespaces/` → `features/done/`.
2. Root README + CLAUDE.md architecture note (one-line pointers); save-games /
   durability / stored-query / change-feed feature READMEs get their one-line deltas (spec §9);
   agent-host + mcp-server specs get the `/ns` note. DEBUGGING.md checked for staleness (ports/
   launch configs unchanged — expected no-op).
3. Re-run the spec §9 impact sweep against the final diff; confirm NL-assist needs no
   RETRAIN-LOG entry (bare-path check).
