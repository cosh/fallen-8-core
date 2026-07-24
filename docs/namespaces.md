# Namespaces

A Fallen-8 is a collection of namespaces, and a namespace is one isolated graph backed by its own Fallen-8 engine. Each namespace owns a private set of vertices, edges, indices, subgraphs, stored queries, and a change feed; nothing crosses between them. One reserved namespace, `default`, always exists and answers the bare (un-prefixed) URLs. This page covers the model, the `/ns/{name}/…` routing scheme, the management API, naming rules, and how namespaces interact with save games.

## The model

| Property | Value |
|---|---|
| Namespaces per Fallen-8 | Up to `Fallen8:Namespaces:MaxNamespaces` (default **10000**), counting `default`. A cap, not a target — each namespace runs a full engine with its own writer thread and (in durable mode) write-ahead log, so realistic fleets are dozens to hundreds. |
| Isolation | Every namespace has its own engine. Data, indices, subgraphs, and stored queries written to one are invisible to the others. |
| Reserved namespace | `default` is always present, aliases the bare URLs, and cannot be renamed or dropped. |
| Identity | Internally each namespace has an immutable, collection-assigned id (e.g. `ns-20260723-101502-3f2a`). On-disk storage and metrics are keyed by the id, never the name — which is why names are permissive and rename is a pure metadata move. The id is not exposed over REST. |

## Routing: bare and `/ns/{name}/…`

Every namespace-scoped route has a twin under `/ns/{name}/…`. A bare URL addresses the reserved `default` namespace, so `/vertex/count` and `/ns/default/vertex/count` hit the same engine. Fallen-8-level routes — namespace management itself, `PUT /save/all`, and the save-game registry (`/savegames/…`) — exist once and have no twin.

The same read, against `default` (bare) and against a `flights` namespace:

```bash
curl http://localhost:8080/vertex/count
curl http://localhost:8080/ns/flights/vertex/count
```

```powershell
Invoke-RestMethod http://localhost:8080/vertex/count
Invoke-RestMethod http://localhost:8080/ns/flights/vertex/count
```

A `/ns/{name}/…` request for a namespace that does not exist returns `404` problem+json with the offending name in the `namespace` extension member — for both reads and writes, before any mutation runs.

## Managing namespaces (REST)

These routes are Fallen-8-level: they exist once and are never themselves prefixed with `/ns/{ns}`.

| Route | Effect | Responses |
|---|---|---|
| `GET /ns` | List all namespaces (name-ordered, always includes `default`) with the `maxNamespaces` ceiling | `200` |
| `GET /ns/{name}` | Get one namespace | `200` · `404` |
| `PUT /ns/{name}` | Create a new, empty namespace | `201` · `400` invalid name · `401` · `409` name in use · `422` quota reached (body carries `maxNamespaces`) · `429` |
| `PATCH /ns/{name}` | Rename (body `{ "name": "<new>" }`) | `200` · `400` missing/invalid new name · `401` · `404` · `409` new name in use or target is `default` · `429` |
| `DELETE /ns/{name}` | Drop irreversibly | `204` · `401` · `404` · `409` target is `default` · `429` |

Create, rename, and drop require an authenticated caller and are rate-limited (`401`/`429`); see [security](security.md). A list/get entry reports:

| Field | Meaning |
|---|---|
| `name` | The URL-addressable name |
| `state` | `ready` or `creating` (always `ready` today) |
| `vertexCount` / `edgeCount` | Element counts for this namespace |
| `createdAt` | Creation time (UTC, ISO 8601) |

There is no per-namespace memory figure: engines share one GC heap, so a byte count would be fiction.

**Rename** is metadata only — the engine, its data, its id, and its on-disk locations are untouched; only the URL address changes. **Drop** removes the in-memory graph, indices, and stored queries and deletes the namespace's live on-disk state (its write-ahead log); there is no undo. Checkpoint files are *not* deleted — they belong to save-game entries and remain valid restore points (delete them with `DELETE /savegames/{id}?deleteFiles=true`).

Create, rename, and drop:

```bash
curl -X PUT http://localhost:8080/ns/flights
curl -X PATCH http://localhost:8080/ns/flights \
  -H "Content-Type: application/json" -d '{"name":"flights-eu"}'
curl -X DELETE http://localhost:8080/ns/flights-eu
```

```powershell
Invoke-RestMethod -Method Put http://localhost:8080/ns/flights
Invoke-RestMethod -Method Patch http://localhost:8080/ns/flights `
  -ContentType application/json -Body '{"name":"flights-eu"}'
Invoke-RestMethod -Method Delete http://localhost:8080/ns/flights-eu
```

## Naming rules

Names are permissive because on disk a name is only a display label, a dictionary key, and a URL path segment (the id carries identity). That last role fixes the only hard limits.

| | |
|---|---|
| **Allowed** | Any case, digits, spaces, punctuation, and Unicode; 1–63 characters. Names are case-sensitive (compared ordinally). |
| **Rejected** | Empty or whitespace-only; longer than 63 characters; leading or trailing whitespace; exactly `.` or `..`; or containing `/`, `\`, or a control character. |

So `Flights EU`, `code.repo_v2`, `fraud!(q3)#2`, `ümlaut-Ω-graphé`, and `con` are all valid; `slash/name`, `..`, `" leading"`, and a 64-character name are not. Because a name is a URL path segment, percent-encode reserved characters in the request URL — `Flights EU #2` becomes `/ns/Flights%20EU%20%232`. Kestrel decodes it before routing, and the namespace is stored and listed under the decoded name. An encoded slash (`%2F`) is rejected by Kestrel and can never round-trip.

## Save, load, and restore

Checkpoints follow namespace boundaries. `PUT /save` checkpoints the addressed namespace (it has a `/ns/{name}/…` twin); `PUT /save/all` checkpoints every namespace into one Fallen-8-level entry. Restoring with `PUT /savegames/{id}/load` replaces exactly the namespaces the entry contains — a dropped namespace is recreated, an existing one has its graph replaced, and namespaces the entry does not contain are left untouched. Add `?namespace={name}` to restore just one namespace out of the entry (`404` if the entry lacks it). Because a drop keeps checkpoint files, a dropped namespace can be brought back from a save game. The mechanics — checkpoints, the write-ahead log, durability — live in [save games](save-games.md).

## Studio

F8 Studio shows the current instance and namespace as a pair in the top bar, and its Connect screen creates, switches, and drops the namespaces of an instance. See [studio](studio.md).

## See also

- [Graph model](graph-model.md) — the elements, properties, and transactions a namespace holds
- [Save games](save-games.md) — checkpoints, the write-ahead log, `/save` / `/save/all` / `/savegames/{id}/load`
- [Stored queries](stored-queries.md) — the per-namespace, WAL-durable query library
- [Security](security.md) — the API key that gates create/rename/drop
- [Studio](studio.md) — the instance/namespace top bar and the Connect screen
- [REST API](rest-api.md) — REST conventions, the OpenAPI document, and the Scalar reference
