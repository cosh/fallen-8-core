# Graph namespaces

**The living doc.** A Fallen-8 hosts up to N isolated graph namespaces (configurable via
`Fallen8:Namespaces:MaxNamespaces`, default 10,000). [spec.md](./spec.md) and
[plan.md](./plan.md) are the historical design records.

## Terminology (binding)

- **Fallen-8** — the entire collection of namespaces behind one endpoint. Operations that
  affect every namespace are **Fallen-8-level**.
- **Namespace** — one named, isolated graph (vertices, edges, indices, subgraphs, stored
  queries, change feed, id space) inside a Fallen-8.
- **Fallen-8 engine** — the `Fallen8` class from `fallen-8-core`; every namespace owns
  exactly one engine. The engine itself is namespace-agnostic — namespacing is a hosting
  concern in `fallen-8-core-apiApp` (`Namespaces/Fallen8Namespaces.cs`).

## URL scheme

Every namespace-scoped route exists twice — the twins are REAL attribute routes added by one
MVC convention (`NamespaceRouteConvention`), no path rewriting:

```
GET /vertex/42            ← bare = the reserved "default" namespace (full back-compat)
GET /ns/flights/vertex/42 ← the same action on the "flights" namespace
```

An unknown namespace answers `404 application/problem+json` with a `"namespace"` extension
member (Studio keys its "recreate or switch" recover state on it). Fallen-8-level routes
(marked `[Fallen8Level]`) exist once: `/ns` management, `/savegames*`, `/save/all`,
`/tabularasa/all`, `/delegates/validate`, plugin upload.

**Namespace-required routes** are the other end of the scale: scoped, twinned like any other, but
with the bare alias REFUSED rather than pointed at `default`. Marked `[NamespaceRequired]`, and only
`/generate` + `/benchmark` carry it: generation writes one graph and the benchmark reports one
graph's throughput as the caller's, and defaulting made "generated into `default` while working in
`flights`" the silent outcome. Bare ⇒ `400` "Namespace required" naming the scoped URL and carrying
**no** `namespace` extension member (there is no namespace to be missing, and that member is the
recover-state marker). The bare route stays REGISTERED rather than removed: this app serves Studio
behind `MapFallbackToFile("index.html")`, so an unrouted `/generate` would answer with the app shell
and `200`. `NamespaceValidationFilter` is the one home for all three refusals (unknown, not loaded,
namespace required).

### Namespace CRUD

| Route | Behavior |
|---|---|
| `GET /ns` | list (name-ordered, always includes `default`) + `maxNamespaces` |
| `GET /ns/{name}` | one entry: `{ name, state, vertexCount, edgeCount, createdAt }` (later features added the `pluginRegistrationEnabled` and `loadOnStartupEnabled` overrides; the counts are absent while `state` is `notLoaded`) |
| `PUT /ns/{name}` | 201 create · 400 invalid name · 409 exists · 422 quota (limit in body) |
| `PATCH /ns/{name}` | rename, pure metadata, the id-keyed on-disk location never moves (it now also carries the two overrides, applied with the rename as one atomic catalog write) |
| `DELETE /ns/{name}` | drop, irreversible · 409 for `default` |

No per-namespace memory figure by design: engines share one GC heap, a per-namespace byte
count would be fiction.

**Names are permissive.** Storage is keyed by an internal id, not the name, so a name is only
a display label, a map key, and a URL path segment — any case, spaces, punctuation, and
Unicode are allowed (up to 63 chars). The rule (`Fallen8Namespaces.IsValidName`, mirrored
client-side in `lib/namespaceName.ts`) rejects only what the URL-segment role can't survive:
`/` and `\` (an encoded slash can't round-trip through Kestrel), control characters, the
whole-name traversal tokens `.` and `..`, leading/trailing whitespace, and empty. Names are
case-sensitive (URL-path semantics). Studio and the REST client percent-encode the name in
every path, so a namespace like `Flights EU #2` addresses `/ns/Flights%20EU%20%232/…`.

## Save / load / tabula rasa

| Operation | Scope |
|---|---|
| `PUT /save` (twinned) | checkpoints the addressed namespace → one-member save-game entry |
| `PUT /save/all` | checkpoints every LOADED namespace → one spanning entry (the shutdown auto-save's shape); a not-loaded one is skipped and named in `skippedNamespaces`, so the entry can span a strict subset |
| `PUT /savegames/{id}/load` | restores exactly the entry's namespaces (recreates dropped ones, touches nothing else) |
| `PUT /savegames/{id}/load?namespace=x` | restores only `x` out of the entry |
| `HEAD /tabularasa` (twinned) | erases the addressed namespace's content (stays registered) |
| `HEAD /tabularasa/all` | factory reset: drops all non-default, erases `default` |

Save-game registry schema v2: entries carry a `namespaces` manifest (1..n members), each
keyed by the IMMUTABLE namespace id — a rename keeps the boot chain, and a recreated
namesake (fresh id) never resurrects the dropped one's saves. Pre-namespace (v1) entries
are read forever as default-only saves. A drop deletes only the
namespace's live WAL — checkpoint files belong to save games and remain valid restore
points (deleted via `DELETE /savegames/{id}?deleteFiles=true`).

Semantics note: restoring the NEWEST save of a live namespace replays its paired WAL (the
engine's crash-consistency pairing), so post-save commits survive; a recreated (dropped)
namespace restores to the entry's exact content.

## Durability layout

```
metadata/namespaces.json      ← the catalog: which namespaces exist (atomic writes, corruption fails boot loudly)
metadata/savegames.json       ← the save-game registry (schema v2)
namespaces/{id}/…             ← per-namespace WAL + default checkpoint location (id = immutable,
                                 collection-assigned — user names never become filesystem paths)
<legacy paths>                ← "default" keeps the pre-namespace locations: zero-migration upgrade
```

The catalog names the engines to construct; each loaded namespace loads the newest save-game entry
containing it, then replays its WAL. Namespace create/rename/drop is durable through the catalog: a
created-but-never-saved namespace survives restarts via its WAL.

**Superseded (2026-08):** boot was eager for every cataloged namespace, and this feature's spec
listed "no lazy engine loading" among its non-goals with the trigger "deployments with thousands of
live namespaces, or boot time / memory pressure". That trigger arrived as an operator preference and
[namespace-startup-load](../namespace-startup-load/) took the declarative half of it: each catalog
entry carries a `loadOnStartupEnabled` tri-state, `Fallen8:Namespaces:LoadOnStartup` is what it
inherits, and `Fallen8:Namespaces:StartupLoadMode` is the operator escape hatch. A namespace the boot
skips stays cataloged with no engine (residency is a property of the entry, never of membership), so
everything above about the catalog, name reservation, quota and drop still holds for it; it reports
`state: "notLoaded"` with absent counts and refuses data requests with `503`, and
`POST /ns/{name}/activate` loads it without a restart. Idle eviction remains a non-goal there. The
spec and plan in this directory are the historical records and are not rewritten.

## Observability

Each engine's meter carries a `fallen8.scope.id` tag (the host-assigned namespace id — never
the user-supplied name, preserving the no-user-input-in-tags invariant), so N engines report
distinguishable instruments. Map id → name via `GET /ns`.

## F8 Studio

The top bar shows the `instance / namespace` pair — the switcher is a rich dropdown (per
the approved mock): filter, per-namespace rows with counts and active / bare-URL-alias /
not-ready tags (a `not loaded` tag was added ahead of those by
[namespace-startup-load](../namespace-startup-load/)), an inline "+ New namespace" quick-create that switches to the newborn, a
"Manage…" jump to Connect, and the quota footer. Scoped screens live under `/q/{ns}/…`
(deep links restore the namespace; old flat paths redirect). Studio always sends the
explicit `/ns/{ns}` prefix — `default` included — the bare alias exists for legacy clients,
not for hiding the namespace. (One exception: when the `/ns` capability probe 404s, the
server predates namespaces and Studio degrades to bare paths so the previous release keeps
working.) Workspace stores, react-query caches, and the change-feed
stream are all keyed per instance + namespace (the pre-namespace store is adopted as
`default`'s). The Connect screen carries the NAMESPACES panel (create with live URL preview,
rename, switch, typed-name drop); Save games stays Fallen-8-level and says so. Benchmark started
that way and was WRONG to: it wrote into `default` whatever the switcher said, so it is now a scoped
screen at `/q/{ns}/benchmarks` (the flat `/benchmarks` redirects) reading the bound instance, and its
generation result names the namespace the server wrote.

## Limits / revisit triggers

Each LOADED namespace owns a dedicated writer thread, its resident graph, and metric instruments:
the 10,000 quota is a cap, not a target; realistic fleets are dozens to hundreds (engine-side
pooling is the revisit trigger for more). It never owns an open WAL handle, in any mode. This README
and [spec.md](./spec.md) ("Per-namespace fixed cost") both claimed one until 2026-08, and it
overstated the per-namespace cost: every append opens, fsyncs and closes
(`fallen-8-core/Persistency/WriteAheadLog.cs`). The claim stands in the spec as the historical record;
this line is the correction. No auth (superseded
[multi-instance-host](../multi-instance-host/) territory; re-spec on an untrusted caller),
no cross-namespace queries, no async provisioning (`state` is `ready` or, since
[namespace-startup-load](../namespace-startup-load/), `notLoaded`; `creating` remains unused).
"No lazy engine loading" is superseded in part (see Durability layout): a boot can skip a namespace
and it can be activated later, but nothing unloads a live engine and there is still no
load-on-first-request.
