# Graph namespaces — one instance, many isolated graphs

Status: open (spec + plan, ready for implementation). Supersedes
[multi-instance-host](../multi-instance-host/) (decided 2026-07-23). Related:
[save-games](../../done/save-games/), [hosted-durability-lifecycle](../../done/hosted-durability-lifecycle/),
[crash-durability-hardening](../../done/crash-durability-hardening/), [web-ui](../../done/web-ui/),
[api-error-envelope](../api-error-envelope/), [studio-embeddable](../studio-embeddable/).
Design reference: the approved namespaces mock (top-bar switcher + Connect-screen CRUD panel,
screenshots in the feature discussion; the mock's `.dc.html` is not checked in).

## 1. Overview

A Fallen-8 instance hosts up to N isolated **graph namespaces** (configurable, default 10,000). Each
namespace has its own vertices, edges, indices, subgraphs, stored queries, change feed, and id space.
Namespaces are created, renamed, dropped, and consumed independently. Every engine-scoped REST route
gains a real namespace-addressed twin under `/ns/{ns}/…`; bare legacy URLs alias the reserved
`default` namespace, so existing clients keep working unchanged. F8 Studio grows a namespace switcher
beside the instance selector and a CRUD panel on the Connect screen.

## 2. Decisions (fixed by review, 2026-07-23)

1. **Namespaces supersede multi-instance-host.** This feature builds the one hosting level above the
   engine (registry, request-scoped resolution, addressed routes). No auth layer — that would be
   re-specced from scratch if ever needed.
2. **Real route twins, no path rewriting.** Actions keep their bare absolute routes and gain genuine
   `/ns/{ns}/…` routes (visible in routing and OpenAPI). Added by one MVC application-model
   convention rather than a hand-written second attribute on ~130 actions — same result as writing
   `[HttpGet("/ns/{ns}/vertex/{vertexIdentifier}/edges/out")]` everywhere, without the
   double-maintenance. (If a literal per-action attribute is ever preferred, the swap is mechanical.)
3. **Studio deep links are path-based**: `/q/{ns}/canvas` etc., matching the mock.
4. **Full durability in v1**: a save game captures **all** namespaces; namespaces survive restarts;
   WAL is per-namespace. Pre-namespace save games load into `default`.

## 3. Model — a namespace is one engine

**The engine does not change.** A namespace is an ordinary `Fallen8` object; N namespaces are N
engine objects in one process, isolated by object graph — each already owns its element store,
id counter, intern table, index/service/subgraph factories, stored-query library, change-feed
dispatcher, WAL, and single-writer transaction thread. Namespacing is a hosting concern in
`fallen-8-core-apiApp`, exactly like the embedding provider. If the design starts wanting to change
`fallen-8-core`, that is the signal the concern is in the wrong layer.

- **`NamespaceRegistry`** (apiApp singleton) replaces the single `AddSingleton<IFallen8>` factory in
  `Program.cs`: `ConcurrentDictionary<string, NamespaceHandle>` where a handle is
  `{ string Name; string Id; Fallen8 Engine; DateTime CreatedAt; NamespaceState State }`. Each engine
  is constructed exactly the way the factory builds the one engine today (volatile vs durable branch,
  stored-query compiler, change-feed options, `StoredQueries.MaxCount`).
- **`IFallen8` becomes request-scoped**, resolved from the `ns` route value (absent ⇒ `default`).
  Controllers are untouched — they keep injecting `IFallen8`.
- **Reserved `default` namespace.** Always exists, cannot be renamed or dropped, owns all
  pre-namespace data, and is what bare URLs address.
- **Names** match `^[a-z0-9-]{1,63}$` and are unique per instance. Names are display/address keys
  only — on disk every namespace lives under an immutable, registry-assigned id (see §5), so rename
  is a pure metadata operation and user-supplied names never become filesystem paths (Windows
  reserved device names like `con` stay harmless).
- **State** is `ready | creating`. In v1 creation is synchronous and fast (an empty engine), so the
  server always reports `ready`; the enum exists so a future async provisioning path (e.g. create
  from bulk import) is not a breaking change. The mock's "creating — 42%" row is Studio-side
  rendering of a running bulk-import job, not server namespace state.

### Per-namespace fixed cost (honest limits)

Each namespace carries a dedicated writer thread, an open WAL (durable mode), and metric
instruments. The 10,000 default quota is a **cap, not a target**: realistic fleets are dozens to
hundreds. Costs are documented on the config option; if a real deployment needs thousands of
concurrently-live namespaces, engine-side pooling (shared writer scheduling, lazy WAL) is the named
revisit trigger — not v1 machinery.

## 4. REST contract

### 4.1 Addressing — route twins

- An `IApplicationModelConvention` adds, to every engine-scoped action, a second route selector that
  prefixes the action's absolute template with `/ns/{ns:regex(^[a-z0-9-]{{1,63}}$)}`. Both routes
  bind the same action; the scoped `IFallen8` resolves from the `ns` route value.
- **Bare URL ⇒ `default`**: `/vertex` and `/ns/default/vertex` are the same operation on the same
  engine. Full backward compatibility, byte for byte.
- A resource filter validates `ns` against the registry **before** the action runs: unknown
  namespace ⇒ `404` problem+json with extension member `"namespace": "<name>"` (the stable marker
  Studio keys its recover state on).
- **Instance-level actions are not twinned.** Marked `[InstanceLevel]` (consumed by the convention)
  and their OpenAPI remarks say "instance-wide, all namespaces":
  `PUT /load`, `PUT /save`, `HEAD /tabularasa`, `HEAD /trim`, `/savegames*` (all),
  `/generate` + `/benchmark`, `/delegates/validate`, `GET /plugin`.
  Everything else — status, graph/vertex/edge, scans, indices, path, subgraph, analytics, stored
  queries, bulk import/export, change feed, embeddings, `/unittest` sample graph — is
  namespace-scoped. (`GET /status` on `/ns/{ns}/status` reports that namespace's counts; feature
  flags like the embedding provider state are identical across namespaces.)
- Semantics of the instance-wide ops with namespaces: `PUT /save` / `PUT /load` snapshot/restore the
  **entire namespace set** (§5); tabula rasa **drops every non-default namespace and erases
  `default`** (its docs must say so); trim runs across all engines; benchmark generation targets
  `default` via the bare alias.

### 4.2 Namespace CRUD

| Route | Behavior |
|---|---|
| `GET /ns` | `200` list, name-ordered: `{ namespaces: [entry…], maxNamespaces }` |
| `GET /ns/{name}` | `200` one entry; `404` unknown |
| `PUT /ns/{name}` | `201` + entry; `400` invalid name; `409` exists; `422` quota exceeded (body carries the configured limit) |
| `PATCH /ns/{name}` | body `{ "name": "new" }` → `200` + entry; `400` invalid; `404` unknown; `409` target exists or `name`/target is `default` |
| `DELETE /ns/{name}` | `204`; irreversible, deletes the namespace's on-disk data; `404` unknown; `409` for `default` |

Entry shape: `{ name, state, vertexCount, edgeCount, createdAt }`. **No per-namespace memory
figure** — engines share one GC heap, so a per-namespace byte count would be fiction; the mock's
memory column is dropped for honesty (the instance-level number stays on `/status`).

All error responses are RFC 7807 problem+json via `ProblemResults` from day one — new endpoints do
not add plain-string sites for [api-error-envelope](../api-error-envelope/) to migrate later.
Mutating CRUD (`PUT`/`PATCH`/`DELETE`) is gated like the other admin writes (authenticated when an
API key is configured); reads follow the open-reads posture.

### 4.3 Config

New options class `Fallen8NamespacesOptions`, section `Fallen8:Namespaces`:
`MaxNamespaces` (default **10000**, counts all namespaces including `default`). Parallels the
existing stored-query/change-feed/analytics ceilings.

### 4.4 OpenAPI

The twins are real routes, so the snapshot roughly doubles in paths — regenerate with
`pwsh scripts/update-openapi-snapshot.ps1` and review (additions only, plus the new `/ns` CRUD).
The `ns` parameter gets one shared description via the convention; the prefix rule is explained
once in the document description, not per operation.

## 5. Durability — catalog, per-namespace storage, save games

On-disk layout (under the durability storage directory):

```
metadata/namespaces.json          ← the catalog: schemaVersion, [{ id, name, createdAt }]
metadata/savegames.json           ← existing registry (schema v2, below)
namespaces/{id}/…                 ← per-namespace WAL + checkpoints (id = registry-assigned,
                                     immutable, e.g. ns-20260723-101502-3f2a)
<legacy paths>                    ← `default` keeps today's WAL/checkpoint locations unchanged
```

- **Catalog.** `namespaces.json` is the boot-time source of truth for which namespaces exist,
  written atomically (temp + rename) on create/rename/drop, corruption fails startup loudly —
  the same posture as the save-game registry. `default` is implicit (never listed as required;
  tolerated if present). Rename rewrites only the catalog — directories never move.
- **Boot.** Read the catalog, construct every engine eagerly (matching today's
  `DurabilityLifecycleService` behavior), each restoring per the existing per-engine rules
  (registry-driven newest save game + WAL replay). Lazy load/idle eviction is a named revisit
  trigger (boot time or memory pressure), not v1.
- **WAL.** Per-namespace WAL under `namespaces/{id}/`; `default` keeps the legacy path so existing
  deployments upgrade in place with zero migration. Namespace **creation/rename/drop is itself
  durable** via the catalog write (not WAL entries) — a created-but-never-saved namespace survives
  restart through its own WAL exactly as the single instance does today.
- **Save games (registry schema v2).** A save game is an **instance-wide restore point**: `PUT
  /save` checkpoints every namespace (each engine saves under its own directory) and writes one
  registry entry whose `kpis` become per-namespace:
  `namespaces: [{ name, location, vertexCount, edgeCount, indices, subGraphs, … }]` plus
  instance-level totals. `schemaVersion: 1` entries (no `namespaces` array) are read forever and
  mean "a `default`-only save". Loading a save game **replaces the entire namespace set** with the
  entry's manifest — namespaces created after that save are dropped by the load, which the endpoint
  and Studio confirm dialogs must state. `DELETE /savegames/{id}?deleteFiles=true` deletes all the
  entry's per-namespace files.
- **Drop.** `DELETE /ns/{name}`: remove from registry first (new requests 404 immediately), then
  dispose the engine after in-flight readers drain (reusing the trim-reader-safety discipline),
  then delete `namespaces/{id}/`. Historical save-game entries referencing the dropped namespace
  remain valid restore points.
- **Volatile mode** (`Fallen8:Durability:Volatile=true`): no catalog, no WAL — namespaces are
  in-memory only and vanish on restart, like all data in that mode.

## 6. Shared vs per-namespace (isolation audit)

Per the process-global-state audit (inherited from the superseded spec, verified against current
code): each `Fallen8` is self-contained; **no process-global holds per-graph data**. Deliberately
shared across namespaces: `PluginFactory` (process plugin discovery), `GeneratedCodeCache` /
`CodeGenerationHelper` caches (compiled delegates are graph-independent), the embedding provider
(one model in memory; vector indices stay per-namespace), `AnalyticsRunGate` (process-wide CPU
gate), the rate limiter, and the save-game registry file (single instance-level file, schema v2).

**Metrics fix (required):** every engine creates a `Meter` named `fallen8`, so N engines would
double-count instruments. `Fallen8Metrics` gains a dimension tag whose value is the
**registry-assigned namespace id** — system-generated, so the "no tag value from user input"
invariant holds (names never become tag values). The id→name mapping is readable from `GET /ns`.

## 7. F8 Studio

All Studio work lives in `fallen-8-web-ui/` (in-repo).

1. **Top bar** (`AppShell.tsx`): a namespace switcher right of the instance select, rendered as the
   pair `INSTANCE local / NAMESPACE flights` with `{v} v · {e} e` counts; the endpoint hint becomes
   `same origin → /ns/flights/*` (bare hint unchanged when the active namespace is `default`).
   Dropdown per the mock: filter input, rows (name, counts, `active`/`not ready` tags), `+ New
   namespace`, `Manage…` (jumps to the Connect panel), quota footer (`12 / 10,000`) — data from
   `GET /ns` via react-query.
2. **State.** The registry (`f8.instances`) gains per-instance `activeNamespace` (default
   `"default"`), kept in sync with the URL. Namespace-scoped API wrappers thread the namespace
   through the single `buildUrl` seam: bare paths when the active namespace is `default` (keeps the
   new Studio compatible with pre-namespace servers), `/ns/{ns}` prefix otherwise. Instance-level
   calls (save games, tabula rasa, trim, benchmark, save/load) are always bare. Change-feed and
   bulk raw-fetch paths use the same seam.
3. **Remount + stores.** The screen remount key extends to `key={active.id + '/' + ns}`;
   per-namespace workspace stores are keyed `f8.workspace.<instanceId>/<ns>` (an existing
   `f8.workspace.<id>` store is adopted as the `default` namespace's store — no data loss on
   upgrade). Results, drafts, and canvas state never cross namespaces; "send to canvas" and similar
   cross-screen actions stay within the active namespace.
4. **Routing.** Namespace-scoped screens move under a parent param route:
   `/q/{ns}/dashboard|browser|query|indexes|path|subgraphs|analytics|canvas`. Instance-level routes
   stay flat: `/` (Connect), `/save-games`, `/benchmarks`. Old paths (`/canvas`, bookmarks) redirect
   to `/q/{activeNamespace}/…`; a pasted link restores the namespace, falling back to `default`
   when the URL names an unknown one (after offering the recover state below).
5. **Connect screen — NAMESPACES panel** per the mock: table (state dot, name, vertices, edges,
   created, URL prefix, actions Rename / Switch to / Drop), `default` row labeled "alias of bare
   URLs" with Drop disabled, create form (pattern hint, live URL preview, Create), an error strip
   documenting the 409/404/422 semantics. No memory column (§4.2). A namespace with a running bulk
   import renders the mock's "creating" row from the import job state, with Cancel.
6. **Drop confirmation**: reuse `ConfirmDialog` (typed-name gate) — the namespace name must be
   typed; the dialog shows counts, `DELETE /ns/{name}`, "no undo".
7. **404-on-namespace recover state**: any response carrying the `namespace` problem extension
   renders "namespace `x` is gone — recreate or switch", never a blank screen.
8. **Scope rules in dialogs**: save games / load / tabula rasa / benchmark confirm dialogs say
   "instance-wide, all namespaces"; the load-save-game dialog additionally warns that the load
   replaces the whole namespace set (§5).

## 8. Impact on existing features (mandatory sweep)

| Feature / asset | Impact |
|---|---|
| [multi-instance-host](../multi-instance-host/) (open) | **Superseded** — status updated in its spec; auth would be re-specced from scratch (revisit trigger: untrusted caller). |
| [save-games](../../done/save-games/) | Registry schema v2 (per-namespace manifest), load-replaces-all semantics, v1 entries read as default-only. README updated when this lands. |
| [hosted-durability-lifecycle](../../done/hosted-durability-lifecycle/), [crash-durability-hardening](../../done/crash-durability-hardening/) | Lifecycle service generalizes to iterate the catalog; per-namespace WAL dirs; `default` keeps legacy paths (zero-migration upgrade). |
| OpenAPI snapshot (`features/done/web-ui/openapi-v0.1.json`) | ~2× paths (twins) + `/ns` CRUD; regenerate + review. Consumed by the web-ui contract test and the [mcp-server](../mcp-server/) spec — both flagged. |
| NL-assist (`nl-assist-finetune/`) | **No retrain.** The harness and dataset use bare relative paths against a base URL; bare URLs keep aliasing `default`. No RETRAIN-LOG entry needed. Targeting a named namespace later is a one-line opt-in prefix in `shared/f8.ts`. |
| Studio ([web-ui](../../done/web-ui/) + studio-* features) | Switcher, router restructure, per-namespace stores, CRUD panel, dialog wording (§7). |
| [stored-query-library](../../done/stored-query-library/) | Already per-engine ⇒ per-namespace for free; `MaxCount` applies per namespace (document). |
| [bulk-import-export](../../done/bulk-import-export/), [sample-graphs](../../done/sample-graphs/) | Routes twinned; Studio imports/samples land in the active namespace; "creating" row driven by import job state. |
| [change-feed](../../done/change-feed/) | Dispatcher already per-engine; `/changefeed` twinned; `MaxSubscribers` applies per namespace (document). |
| [observability](../../done/observability/) | Meter collision fix: namespace-id tag on `Fallen8Metrics` (§6). |
| [api-error-envelope](../api-error-envelope/) (open) | New endpoints are problem+json from day one; its 134-site inventory is unaffected. |
| [studio-embeddable](../studio-embeddable/) (open) | Orthogonal (`storageNamespace` prefixes localStorage keys); its `InstanceConfig` note gains "may pin a namespace" as future work. |
| [agent-host](../agent-host/), [mcp-server](../mcp-server/) (open) | Both consume the REST contract; their specs get a one-line note that tools/agents address `/ns/{ns}/…`. |

## 9. Acceptance scenarios

1. Fresh instance → `GET /ns` lists exactly `default` (`ready`, counts 0) with `maxNamespaces`
   10000; all bare routes behave as today.
2. `PUT /ns/flights` → 201; `PUT /ns/flights` again → 409; creating beyond a configured
   `MaxNamespaces=3` → 422 naming the limit; `PUT /ns/Flights!` → 400.
3. Vertices created via `/ns/flights/vertex` are invisible to `/vertex` (i.e. `default`) and to
   `/ns/scratch/…`; ids are independent per namespace; `/ns/flights/status` and `GET /ns` report
   per-namespace counts.
4. `/ns/missing/vertex` → 404 problem+json with `"namespace": "missing"`.
5. `PATCH /ns/flights` → `fl-eu` : old URLs 404, new URLs serve the same data, catalog rewritten,
   on-disk directory unchanged; renaming or dropping `default` → 409.
6. Durable mode: create `flights`, add data, **restart without saving** → `flights` and its data
   are back (catalog + WAL). Drop `flights`, restart → still gone, directory deleted.
7. `PUT /save` with three namespaces → one registry entry, per-namespace manifest; drop one
   namespace, load that save game → all three back exactly as saved. A pre-upgrade (v1) save game
   loads into `default`.
8. Tabula rasa → only `default` remains, empty; response/docs say instance-wide.
9. Studio: switcher shows namespaces + quota; switching remounts the screen and swaps workspace
   stores with no result/canvas leakage; `/q/flights/canvas` pasted into a fresh tab restores
   instance + namespace; unknown namespace in the URL → recover state (recreate or switch);
   drop demands the typed name.
10. OpenAPI snapshot regenerated: every engine-scoped path has its `/ns/{ns}` twin, instance-level
    ops have none, `/ns` CRUD present, `paths` still ordinally sorted.

## 10. Testing requirements

- **Engine-adjacent (MSTest):** registry CRUD incl. quota/name validation/reserved-default;
  request-scoped resolution (bare vs twinned routes hit distinct engines — extend
  `HostedRoutingSmokeTest`); cross-namespace isolation (data, ids, indices, stored queries, change
  feed); catalog persistence (atomic write, corruption-fails-loudly, rename, drop); per-namespace
  WAL restart scenarios; save-game v2 round-trip + v1 compat + load-replaces-set; tabula-rasa
  semantics; 404/409/422 problem+json shapes; OpenAPI snapshot test updated; metrics tag presence.
  Prefer `WebApplicationFactory` pipeline tests; pin edge cases (63-char names, `con`, quota
  boundary, drop-while-reading).
- **Studio (vitest + Playwright):** extend `instance-isolation.test` to namespaces (store keys,
  adoption of the legacy store as `default`); api-contract tests for the `/ns` prefix seam incl.
  change feed and bulk; switcher behavior; CRUD panel (validation, 409/422 rendering, typed drop
  gate, default undeletable); route redirects + deep-link restore + unknown-namespace recover;
  confirm-dialog wording for instance-wide ops; one e2e scenario (create → populate → switch →
  drop).

## 11. Non-goals / revisit triggers

- **No auth / per-namespace authorization.** Superseded MIH territory; re-spec from scratch when an
  untrusted caller appears.
- **No per-namespace resource quotas or fairness** beyond existing process-wide gates. *Trigger:*
  a noisy-neighbor incident.
- **No lazy engine loading / idle eviction; no writer-thread pooling.** Eager boot, one thread per
  namespace. *Trigger:* deployments with thousands of live namespaces, or boot time / memory
  pressure.
- **No cross-namespace queries or copies.** *Trigger:* a real use case asks for it.
- **No async namespace provisioning** (`state` stays `ready` in v1). *Trigger:* create-from-import
  or restore-into-new-namespace lands.
- **No per-namespace memory accounting.** Shared GC heap makes it fiction. *Trigger:* .NET exposes
  a viable per-object-graph accounting primitive (unlikely) or namespaces move to process isolation.
