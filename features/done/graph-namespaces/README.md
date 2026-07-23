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
`/tabularasa/all`, `/generate`, `/benchmark`, `/delegates/validate`, plugin upload.

### Namespace CRUD

| Route | Behavior |
|---|---|
| `GET /ns` | list (name-ordered, always includes `default`) + `maxNamespaces` |
| `GET /ns/{name}` | one entry: `{ name, state, vertexCount, edgeCount, createdAt }` |
| `PUT /ns/{name}` | 201 create · 400 invalid (`^[a-z0-9-]{1,63}$`) · 409 exists · 422 quota (limit in body) |
| `PATCH /ns/{name}` | rename — pure metadata, the id-keyed on-disk location never moves |
| `DELETE /ns/{name}` | drop, irreversible · 409 for `default` |

No per-namespace memory figure by design: engines share one GC heap, a per-namespace byte
count would be fiction.

## Save / load / tabula rasa

| Operation | Scope |
|---|---|
| `PUT /save` (twinned) | checkpoints the addressed namespace → one-member save-game entry |
| `PUT /save/all` | checkpoints EVERY namespace → one spanning entry (the shutdown auto-save's shape) |
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

Boot is eager: the catalog names the engines to construct; each namespace loads the newest
save-game entry containing it, then replays its WAL. Namespace create/rename/drop is durable
through the catalog — a created-but-never-saved namespace survives restarts via its WAL.

## Observability

Each engine's meter carries a `fallen8.scope.id` tag (the host-assigned namespace id — never
the user-supplied name, preserving the no-user-input-in-tags invariant), so N engines report
distinguishable instruments. Map id → name via `GET /ns`.

## F8 Studio

The top bar shows the `instance / namespace` pair; scoped screens live under `/q/{ns}/…`
(deep links restore the namespace; old flat paths redirect). Studio always sends the
explicit `/ns/{ns}` prefix — `default` included — the bare alias exists for legacy clients,
not for hiding the namespace. (One exception: when the `/ns` capability probe 404s, the
server predates namespaces and Studio degrades to bare paths so the previous release keeps
working.) Workspace stores, react-query caches, and the change-feed
stream are all keyed per instance + namespace (the pre-namespace store is adopted as
`default`'s). The Connect screen carries the NAMESPACES panel (create with live URL preview,
rename, switch, typed-name drop); Save games and Benchmark stay Fallen-8-level and say so.

## Limits / revisit triggers

Each namespace owns a dedicated writer thread, an open WAL (durable mode), and metric
instruments — the 10,000 quota is a cap, not a target; realistic fleets are dozens to
hundreds (engine-side pooling is the revisit trigger for more). No auth (superseded
[multi-instance-host](../../open/multi-instance-host/) territory; re-spec on an untrusted caller),
no cross-namespace queries, no lazy engine loading, no async provisioning (`state` is always
`ready` in v1).
