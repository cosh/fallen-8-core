# Graph namespaces — one Fallen-8, many isolated graphs

Status: implemented (2026-07-23; historical design record — the living doc is
[README.md](./README.md)). Supersedes
[multi-instance-host](../../open/multi-instance-host/) (decided 2026-07-23). Related:
[save-games](../save-games/), [hosted-durability-lifecycle](../hosted-durability-lifecycle/),
[crash-durability-hardening](../crash-durability-hardening/), [web-ui](../web-ui/),
[api-error-envelope](../../open/api-error-envelope/), [studio-embeddable](../../open/studio-embeddable/).
Design reference: the approved namespaces mock (top-bar switcher + Connect-screen CRUD panel,
screenshots in the feature discussion; the mock's `.dc.html` is not checked in).

## 1. Terminology (binding, used everywhere)

- **Fallen-8** — the entire collection of namespaces behind one endpoint: the process-level whole
  you connect to. Operations that affect every namespace are **Fallen-8-level**.
- **Namespace** — one named, isolated graph (vertices, edges, indices, subgraphs, stored queries,
  change feed, id space) inside a Fallen-8.
- **Fallen-8 engine** — the `Fallen8` object from `fallen-8-core`; every namespace owns exactly
  one engine.

These three words are used consistently in code, REST docs, OpenAPI remarks, feature docs, and
Studio copy. "Instance" survives only in Studio's connection registry, where an *instance* is a
saved connection to one Fallen-8.

## 2. Overview

A Fallen-8 hosts up to N isolated namespaces (configurable, default 10,000). Namespaces are
created, renamed, dropped, saved, loaded, erased, and consumed independently. Every
namespace-scoped REST route gains a real namespace-addressed twin under `/ns/{ns}/…`; bare legacy
URLs alias the reserved `default` namespace, so existing clients keep working unchanged. F8 Studio
grows a namespace switcher beside the instance selector and a CRUD panel on the Connect screen.

## 3. Decisions (fixed by review, 2026-07-23)

1. **Namespaces supersede multi-instance-host.** This feature builds the one hosting level above
   the engine (namespace collection, request-scoped resolution, addressed routes). No auth layer —
   that would be re-specced from scratch if ever needed.
2. **Real route twins, no path rewriting.** Actions keep their bare absolute routes and gain
   genuine `/ns/{ns}/…` routes (visible in routing and OpenAPI). Added by one MVC
   application-model convention rather than a hand-written second attribute on ~130 actions —
   same result as writing `[HttpGet("/ns/{ns}/vertex/{vertexIdentifier}/edges/out")]` everywhere,
   without the double-maintenance. (If a literal per-action attribute is ever preferred, the swap
   is mechanical.)
3. **Studio deep links are path-based**: `/q/{ns}/canvas` etc., matching the mock.
4. **Save, load, and tabula rasa are per-namespace operations** with explicit Fallen-8-level "all"
   variants; a single namespace can be restored out of a multi-namespace save game (§6).
5. **Studio always addresses the namespace explicitly** — `/ns/default/…` included. The bare alias
   exists for legacy clients, not for hiding the namespace.
6. **Full durability in v1**: namespaces survive restarts; WAL is per-namespace; pre-namespace
   save games load into `default`.

## 4. Model — a namespace owns one engine

**The engine does not change.** A namespace wraps an ordinary `Fallen8` engine; N namespaces are N
engine objects in one process, isolated by object graph — each engine already owns its element
store, id counter, intern table, index/service/subgraph factories, stored-query library,
change-feed dispatcher, WAL, and single-writer transaction thread. Namespacing is a hosting concern
in `fallen-8-core-apiApp`, exactly like the embedding provider. If the design starts wanting to
change `fallen-8-core`, that is the signal the concern is in the wrong layer.

- **`Fallen8Namespaces`** (apiApp singleton — the collection that *is* the Fallen-8) replaces the
  single `AddSingleton<IFallen8>` factory in `Program.cs`: `ConcurrentDictionary<string, Namespace>`
  where a `Namespace` is `{ string Name; string Id; Fallen8 Engine; DateTime CreatedAt;
  NamespaceState State }`. Each engine is constructed exactly the way the factory builds the one
  engine today (volatile vs durable branch, stored-query compiler, change-feed options,
  `StoredQueries.MaxCount`).
- **`IFallen8` becomes the addressed engine**: a non-disposable singleton dispatcher
  (`AddressedFallen8`) delegates every member to the engine named by the ambient request's `ns`
  route value (absent ⇒ `default`). Controllers are untouched — they keep injecting `IFallen8`.
  (Not a scoped DI factory registration: the container disposes `IDisposable` instances its
  factories return, which would tear an engine down at the end of every request. Engine lifetime
  belongs to `Fallen8Namespaces` alone.)
- **Reserved `default` namespace.** Always exists, cannot be renamed or dropped, owns all
  pre-namespace data, and is what bare URLs address.
- **Names** match `^[a-z0-9-]{1,63}$` and are unique per Fallen-8. Names are display/address keys
  only — on disk every namespace lives under an immutable, collection-assigned id (see §6), so
  rename is a pure metadata operation and user-supplied names never become filesystem paths
  (Windows reserved device names like `con` stay harmless).
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

## 5. REST contract

### 5.1 Addressing — route twins

- An `IApplicationModelConvention` adds, to every namespace-scoped action, a second route selector
  that prefixes the action's absolute template with `/ns/{ns}` (unconstrained: an invalid name is
  simply a registry miss, answered by the validation filter's 404 problem+json). Both
  routes bind the same action; the scoped `IFallen8` resolves from the `ns` route value.
- **Bare URL ⇒ `default`**: `/vertex` and `/ns/default/vertex` are the same operation on the same
  engine. Full backward compatibility, byte for byte.
- A resource filter validates `ns` against the collection **before** the action runs: unknown
  namespace ⇒ `404` problem+json with extension member `"namespace": "<name>"` (the stable marker
  Studio keys its recover state on).
- **Fallen-8-level actions are not twinned.** Marked `[Fallen8Level]` (consumed by the convention)
  and their OpenAPI remarks say "Fallen-8-level — all namespaces":
  `PUT /save/all`, `HEAD /tabularasa/all`, `/savegames*` (all), `/generate` + `/benchmark`,
  `/delegates/validate`, `PUT /plugin` (upload).
  Everything else — status, graph/vertex/edge, scans, indices, path, subgraph, analytics, stored
  queries, bulk import/export, change feed, embeddings, `/unittest` sample graph, **and save /
  load / tabula rasa / trim** — is namespace-scoped. (`/ns/{ns}/status` reports that namespace's
  counts; feature flags like the embedding provider state are identical across namespaces.
  Benchmark generation targets `default` via the bare alias.)

### 5.2 Save / load / tabula rasa (per-namespace + Fallen-8-level)

| Route | Scope | Behavior |
|---|---|---|
| `PUT /save` (twinned) | namespace | Checkpoints the addressed namespace; registers a save-game entry containing that one namespace. Bare `/save` = `default` — exactly today's behavior. |
| `PUT /save/all` | Fallen-8 | **New.** Checkpoints every namespace into one save-game entry (one consistent restore point). Shutdown auto-save uses this. |
| `PUT /load` (twinned) | namespace | Loads a checkpoint file path into the addressed namespace (auto-registers as `imported`, as today). |
| `PUT /savegames/{id}/load` | entry-driven | Restores **every namespace contained in the entry**: recreates dropped ones, replaces the content of existing ones, and **touches nothing else** — namespaces not in the entry are left alone. |
| `PUT /savegames/{id}/load?namespace={name}` | namespace | Restores **only that namespace** from a multi-namespace entry (404 problem+json when the entry does not contain it). |
| `HEAD /tabularasa` (twinned) | namespace | Erases the addressed namespace's content; the namespace stays registered (empty). Bare = `default`. |
| `HEAD /tabularasa/all` | Fallen-8 | **New.** Factory reset: drops every non-default namespace and erases `default`. Response docs say so plainly. |
| `HEAD /trim` (twinned) | namespace | Trims the addressed namespace's engine. |

Dropping a namespace never invalidates history: save-game entries referencing it remain valid
restore points (that is how a dropped namespace comes back).

### 5.3 Namespace CRUD

| Route | Behavior |
|---|---|
| `GET /ns` | `200` list, name-ordered: `{ namespaces: [entry…], maxNamespaces }` |
| `GET /ns/{name}` | `200` one entry; `404` unknown |
| `PUT /ns/{name}` | `201` + entry; `400` invalid name; `409` exists; `422` quota exceeded (body carries the configured limit) |
| `PATCH /ns/{name}` | body `{ "name": "new" }` → `200` + entry; `400` invalid; `404` unknown; `409` target exists or `name`/target is `default` |
| `DELETE /ns/{name}` | `204`; irreversible; deletes the live on-disk state (the WAL) — checkpoint files belong to save games and remain restore points; `404` unknown; `409` for `default` |

Entry shape: `{ name, state, vertexCount, edgeCount, createdAt }`. **No per-namespace memory
figure** — engines share one GC heap, so a per-namespace byte count would be fiction; the mock's
memory column is dropped for honesty (the Fallen-8-level number stays on `/status`).

All error responses are RFC 7807 problem+json via `ProblemResults` from day one — new endpoints do
not add plain-string sites for [api-error-envelope](../../open/api-error-envelope/) to migrate later.
Mutating CRUD (`PUT`/`PATCH`/`DELETE`) and the save/load/tabula-rasa family stay gated like the
other admin writes (authenticated when an API key is configured); reads follow the open-reads
posture.

### 5.4 Config

New options class `Fallen8NamespacesOptions`, section `Fallen8:Namespaces`:
`MaxNamespaces` (default **10000**, counts all namespaces including `default`). Parallels the
existing stored-query/change-feed/analytics ceilings.

### 5.5 OpenAPI

The twins are real routes, so the snapshot roughly doubles in paths — regenerate with
`pwsh scripts/update-openapi-snapshot.ps1` and review (additions only, plus the new `/ns` CRUD and
`…/all` endpoints). The prefix rule is explained once in the document description, not per
operation (the `ns` parameter itself carries no per-operation description).

## 6. Durability — catalog, per-namespace storage, save games

On-disk layout (under the durability storage directory):

```
metadata/namespaces.json          ← the catalog: schemaVersion, [{ id, name, createdAt }]
metadata/savegames.json           ← existing registry (schema v2, below)
namespaces/{id}/…                 ← per-namespace WAL + checkpoints (id = collection-assigned,
                                     immutable, e.g. ns-20260723-101502-3f2a)
<legacy paths>                    ← `default` keeps today's WAL/checkpoint locations unchanged
```

- **Catalog.** `namespaces.json` is the boot-time source of truth for which namespaces exist,
  written atomically (temp + rename) on create/rename/drop, corruption fails startup loudly —
  the same posture as the save-game registry. `default` is implicit (never listed as required;
  tolerated if present). Rename rewrites only the catalog — directories never move.
- **Boot.** Read the catalog, construct every engine eagerly (matching today's
  `DurabilityLifecycleService` behavior). Per namespace: load the newest save-game entry
  **containing that namespace**, then replay its WAL — the existing per-engine rules, applied per
  namespace. Lazy load/idle eviction is a named revisit trigger (boot time or memory pressure),
  not v1.
- **WAL.** Per-namespace WAL under `namespaces/{id}/`; `default` keeps the legacy path so existing
  deployments upgrade in place with zero migration. Namespace **creation/rename/drop is itself
  durable** via the catalog write (not WAL entries) — a created-but-never-saved namespace survives
  restart through its own WAL exactly as the single engine does today.
- **Save games (registry schema v2).** One Fallen-8-level registry file; each entry carries a
  per-namespace manifest: `namespaces: [{ name, location, vertexCount, edgeCount, indices,
  subGraphs, … }]` with 1..n members — a per-namespace save produces a one-member entry,
  `PUT /save/all` and the shutdown auto-save produce an n-member entry. `schemaVersion: 1` entries
  (no `namespaces` array) are read forever and mean "a `default`-only save". Load semantics per
  §5.2: entry-driven restore touches only the namespaces the entry contains, and
  `?namespace={name}` restores a single one. `DELETE /savegames/{id}?deleteFiles=true` deletes all
  the entry's per-namespace files.
- **Drop.** `DELETE /ns/{name}`: remove from the collection first (new requests 404 immediately),
  then dispose the engine — deliberately with NO in-flight drain: a request already past the
  validation filter when the drop lands may fail, mapped to the same 404 problem+json (the
  UnknownNamespaceException filter) — then delete the live WAL under `namespaces/{id}/`
  (checkpoint files belong to save games and stay).
- **Volatile mode** (`Fallen8:Durability:Volatile=true`): no catalog, no WAL — namespaces are
  in-memory only and vanish on restart, like all data in that mode.

## 7. Shared vs per-namespace (isolation audit)

Per the process-global-state audit (inherited from the superseded spec, verified against current
code): each engine is self-contained; **no process-global holds per-graph data**. Deliberately
shared across namespaces (Fallen-8-level): `PluginFactory` (process plugin discovery),
`GeneratedCodeCache` / `CodeGenerationHelper` caches (compiled delegates are graph-independent),
the embedding provider (one model in memory; vector indices stay per-namespace),
`AnalyticsRunGate` (process-wide CPU gate), the rate limiter, and the save-game registry file
(one file, schema v2).

**Metrics fix (required):** every engine creates a `Meter` named `fallen8`, so N engines would
double-count instruments. `Fallen8Metrics` gains a dimension tag whose value is the
**collection-assigned namespace id** — system-generated, so the "no tag value from user input"
invariant holds (names never become tag values). The id→name mapping is readable from `GET /ns`.

## 8. F8 Studio

All Studio work lives in `fallen-8-web-ui/` (in-repo).

1. **Top bar** (`AppShell.tsx`): a namespace switcher right of the instance select, rendered as the
   pair `INSTANCE local / NAMESPACE flights` with `{v} v · {e} e` counts; the endpoint hint becomes
   `same origin → /ns/flights/*` (`/ns/default/*` when on `default` — the namespace is never
   hidden). Dropdown per the mock: filter input, rows (name, counts, `active`/`not ready` tags),
   `+ New namespace`, `Manage…` (jumps to the Connect panel), quota footer (`12 / 10,000`) — data
   from `GET /ns` via react-query.
2. **State.** The registry (`f8.instances`) gains per-instance `activeNamespace` (default
   `"default"`), kept in sync with the URL. Namespace-scoped API wrappers thread the namespace
   through the single `buildUrl` seam and **always** send the `/ns/{ns}` prefix — `default`
   included. Fallen-8-level calls (`/save/all`, `/tabularasa/all`, save games, benchmark) have no
   prefix. Change-feed and bulk raw-fetch paths use the same seam.
3. **Remount + stores.** The screen remount key extends to `key={active.id + '/' + ns}`;
   per-namespace workspace stores are keyed `f8.workspace.<instanceId>/<ns>` (an existing
   `f8.workspace.<id>` store is adopted as the `default` namespace's store — no data loss on
   upgrade). Results, drafts, and canvas state never cross namespaces; "send to canvas" and similar
   cross-screen actions stay within the active namespace.
4. **Routing.** Namespace-scoped screens move under a parent param route:
   `/q/{ns}/dashboard|browser|query|indexes|path|subgraphs|analytics|canvas`. Fallen-8-level routes
   stay flat: `/` (Connect), `/save-games`, `/benchmarks`. Old paths (`/canvas`, bookmarks) redirect
   to `/q/{activeNamespace}/…`; a pasted link restores the namespace, falling back to `default`
   when the URL names an unknown one (after offering the recover state below).
5. **Connect screen — NAMESPACES panel** per the mock: table (state dot, name, vertices, edges,
   created, URL prefix, actions Rename / Switch to / Drop), `default` row labeled "alias of bare
   URLs" with Drop disabled, create form (pattern hint, live URL preview, Create), an error strip
   documenting the 409/404/422 semantics. No memory column (§5.3). A namespace with a running bulk
   import shows the server-reported state dot only (the mock's import-job-driven "creating" row
   with Cancel is not wired in v1 — the state field exists for a future async provisioning path).
6. **Save & erase actions.** The Dashboard offers **Save namespace** (`/ns/{ns}/save`) and
   **Save all namespaces** (`/save/all`); tabula rasa on the Dashboard is namespace-scoped ("erase
   namespace `flights`"), with the Fallen-8-level factory reset a separate, clearly-labeled action.
   The Save-games screen stays Fallen-8-level: each row shows which namespaces an entry contains,
   with **Load entry** (restores the listed namespaces) and **Load one namespace…** for
   multi-namespace entries.
7. **Drop confirmation**: reuse `ConfirmDialog` (typed-name gate) — the namespace name must be
   typed; the dialog shows counts, `DELETE /ns/{name}`, "no undo".
8. **404-on-namespace recover state**: any response carrying the `namespace` problem extension
   renders "namespace `x` is gone — recreate or switch", never a blank screen.
9. **Scope wording in dialogs**: every confirm dialog names its blast radius — "namespace
   `flights`" for scoped ops, "Fallen-8-wide — all namespaces" for `/save/all`, the factory reset,
   and benchmark; the load-entry dialog lists exactly which namespaces will be replaced.

## 9. Impact on existing features (mandatory sweep)

| Feature / asset | Impact |
|---|---|
| [multi-instance-host](../../open/multi-instance-host/) (open) | **Superseded** — status updated in its spec; auth would be re-specced from scratch (revisit trigger: untrusted caller). |
| [save-games](../save-games/) | Registry schema v2 (per-namespace manifest, 1..n members), per-namespace + all saves, entry-driven and single-namespace restore, v1 entries read as default-only. save-games has no README; the registry semantics live in this feature's README (the living doc). |
| [hosted-durability-lifecycle](../hosted-durability-lifecycle/), [crash-durability-hardening](../crash-durability-hardening/) | Lifecycle service generalizes to the catalog (per-namespace newest-entry boot, `/save/all` on shutdown); per-namespace WAL dirs; `default` keeps legacy paths (zero-migration upgrade). |
| OpenAPI snapshot (`features/done/web-ui/openapi-v0.1.json`) | ~2× paths (twins) + `/ns` CRUD + `…/all` endpoints; regenerate + review. Consumed by the web-ui contract test and the [mcp-server](../../open/mcp-server/) spec — both flagged. |
| NL-assist (`nl-assist-finetune/`) | **No retrain.** The harness and dataset use bare relative paths against a base URL; bare URLs keep aliasing `default`. No RETRAIN-LOG entry needed. Targeting a named namespace later is a one-line opt-in prefix in `shared/f8.ts`. |
| Studio ([web-ui](../web-ui/) + studio-* features) | Switcher, router restructure, per-namespace stores, CRUD panel, save/erase action split, dialog wording (§8). |
| [stored-query-library](../stored-query-library/) | Already per-engine ⇒ per-namespace for free; `MaxCount` applies per namespace (document). |
| [bulk-import-export](../bulk-import-export/), [sample-graphs](../sample-graphs/) | Routes twinned; Studio imports/samples land in the active namespace; "creating" row driven by import job state. |
| [change-feed](../change-feed/) | Dispatcher already per-engine; `/changefeed` twinned; `MaxSubscribers` applies per namespace (document). |
| [observability](../observability/) | Meter collision fix: namespace-id tag on `Fallen8Metrics` (§7). |
| [api-error-envelope](../../open/api-error-envelope/) (open) | New endpoints are problem+json from day one; its 134-site inventory is unaffected. |
| [studio-embeddable](../../open/studio-embeddable/) (open) | Orthogonal (`storageNamespace` prefixes localStorage keys); its spec carries a one-line "may pin a namespace" future-work note. |
| [agent-host](../../open/agent-host/), [mcp-server](../../open/mcp-server/) (open) | Both consume the REST contract; their specs get a one-line note that tools/agents address `/ns/{ns}/…`. |

## 10. Acceptance scenarios

1. Fresh Fallen-8 → `GET /ns` lists exactly `default` (`ready`, counts 0) with `maxNamespaces`
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
7. `PUT /ns/flights/save` → a one-member entry; `PUT /save/all` with three namespaces → one
   three-member entry. Drop `flights`, then `PUT /savegames/{id}/load?namespace=flights` → only
   `flights` is back, the other namespaces untouched; loading the full entry restores all three;
   `?namespace=missing-from-entry` → 404. A pre-upgrade (v1) save game loads into `default`.
8. `HEAD /ns/flights/tabularasa` → `flights` is empty but still listed; `HEAD /tabularasa/all` →
   only `default` remains, empty; both responses/docs name their blast radius.
9. Studio: switcher shows namespaces + quota; every scoped request carries `/ns/{ns}` (including
   `default`); switching remounts the screen and swaps workspace stores with no result/canvas
   leakage; `/q/flights/canvas` pasted into a fresh tab restores instance + namespace; unknown
   namespace in the URL → recover state (recreate or switch); drop demands the typed name.
10. OpenAPI snapshot regenerated: every namespace-scoped path has its `/ns/{ns}` twin,
    Fallen-8-level ops have none, `/ns` CRUD + `…/all` endpoints present, `paths` still ordinally
    sorted.

## 11. Testing requirements

- **Engine-adjacent (MSTest):** collection CRUD incl. quota/name validation/reserved-default;
  request-scoped resolution (bare vs twinned routes hit distinct engines — extend
  `HostedRoutingSmokeTest`); cross-namespace isolation (data, ids, indices, stored queries, change
  feed); catalog persistence (atomic write, corruption-fails-loudly, rename, drop); per-namespace
  WAL restart scenarios; save matrix (per-namespace entry, all-entry, single-namespace restore,
  entry restore leaves others untouched, v1 compat); tabula-rasa scoped vs factory reset;
  404/409/422 problem+json shapes; OpenAPI snapshot test updated; metrics tag presence. Prefer
  `WebApplicationFactory` pipeline tests; pin edge cases (63-char names, `con`, quota boundary,
  drop-while-reading, `?namespace=` not in entry).
- **Studio (vitest + Playwright):** extend `instance-isolation.test` to namespaces (store keys,
  adoption of the legacy store as `default`); api-contract tests for the always-explicit `/ns`
  prefix incl. change feed and bulk; switcher behavior; CRUD panel (validation, 409/422 rendering,
  typed drop gate, default undeletable); save/erase action scoping (namespace vs Fallen-8-level
  wording); route redirects + deep-link restore + unknown-namespace recover; one e2e scenario
  (create → populate → save → switch → drop → restore).

## 12. Non-goals / revisit triggers

- **No auth / per-namespace authorization.** Superseded MIH territory; re-spec from scratch when an
  untrusted caller appears.
- **No per-namespace resource quotas or fairness** beyond existing process-wide gates. *Trigger:*
  a noisy-neighbor incident.
- **No lazy engine loading / idle eviction; no writer-thread pooling.** Eager boot, one thread per
  namespace. *Trigger:* deployments with thousands of live namespaces, or boot time / memory
  pressure.
- **No cross-namespace queries or copies; no restore-into-a-different-name.** `?namespace=`
  restores under the entry's recorded name. *Trigger:* a real use case asks for it.
- **No `/trim/all` or `/load` bulk variants.** Only save and tabula rasa earned "all" endpoints.
  *Trigger:* an operator actually needs fleet-wide trim.
- **No async namespace provisioning** (`state` stays `ready` in v1). *Trigger:* create-from-import
  or restore-into-new-namespace lands.
- **No per-namespace memory accounting.** Shared GC heap makes it fiction. *Trigger:* .NET exposes
  a viable per-object-graph accounting primitive (unlikely) or namespaces move to process isolation.
