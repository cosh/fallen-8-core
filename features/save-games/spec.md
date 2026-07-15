# Save Games — Checkpoint Registry Specification

> **Status:** Draft, ready for implementation. Follow the feature workflow in the
> repository root `CLAUDE.md`. Builds on the durability stack
> ([features/hosted-durability-lifecycle](../hosted-durability-lifecycle/) if present,
> `DurabilityLifecycleService`, WAL) and on F8 Studio
> ([features/web-ui/spec.md](../web-ui/spec.md)).

## 1. Overview

Today a checkpoint is just a file: `PUT /save` writes it and returns a path, and on boot
the server loads whatever checkpoint discovery finds in the storage directory. Nothing
records *what* was saved, *when*, or *what it contained* — and a file dropped into the
right directory silently becomes the database.

This feature makes checkpoints first-class **save games**: every save is registered in a
persistent, human-readable metadata store with cheap KPIs; loads register unknown
checkpoints automatically; startup is driven **exclusively** by the registry (no more
directory-scan auto-load); and the registry is surfaced over REST and as a "Save games"
screen in F8 Studio.

## 2. Goals and non-goals

### Goals
- A durable historical record of every save game, with enough context (KPIs, plugins,
  indices, location, file count/size) to pick the right one later.
- Registry-driven startup: metadata decides what loads; an empty registry starts empty.
- Full lifecycle over REST + a first-class F8 Studio screen ("Save games").

### Non-goals
- Checkpoint content changes: the `.f8s` format, partitioning, and WAL semantics are
  untouched. This feature only records and selects checkpoints.
- Remote/cloud savegame storage, retention policies, or scheduled snapshots (future work).
- Multi-instance registry sharing: metadata belongs to one deployment directory.

## 3. Functional requirements

### 3.1 The metadata store
- FR-1 **Location.** A `metadata/` subdirectory of the deployment directory
  (`AppContext.BaseDirectory` by default; overridable via
  `Fallen8:Metadata:Directory`). Created on demand.
- FR-2 **Format.** A single JSON document, `metadata/savegames.json`: an array of
  entries (schema in §4), camelCase, written atomically (temp file + rename) so a crash
  mid-write never corrupts the registry. A `schemaVersion` field allows evolution.
- FR-3 **Corruption is loud.** An unreadable/unparsable `savegames.json` fails startup
  with a clear error naming the file — it is never silently ignored or overwritten
  (consistent with the crash-durability posture).

### 3.2 Recording save games
- FR-4 **Every save registers.** A successful explicit save (`PUT /save`) and the
  shutdown auto-save (`DurabilityLifecycleService.SaveOnShutdown`) append an entry with
  `trigger` `"api"` or `"shutdown"`. A failed/rolled-back save registers nothing.
- FR-5 **Cheap KPIs only.** The entry captures values the engine already has: vertex and
  edge count, used memory, the current index ids + their plugin types, the available
  index/path/service plugin lists, and the subgraph names. No full graph scans.
- FR-6 **File facts.** The resolved absolute checkpoint location, the number of files
  belonging to the save game (checkpoint + partitions + sidecars), and their total size
  in bytes, computed at registration time.
- FR-7 **Load registers unknown save games.** A successful `PUT /load` whose resolved
  path is not in the registry adds an entry with `trigger: "imported"`, KPIs measured
  from the just-loaded graph, and `savedAt` taken from the checkpoint file's last-write
  time. Loading an already-registered save game updates nothing (the historical record
  is immutable).

### 3.3 Startup behaviour (breaking change)
- FR-8 **Registry-driven boot.** On startup (durable mode), Fallen-8 reads the registry:
  - No `metadata/savegames.json`, or an empty registry → **start empty**. A checkpoint
    sitting in the storage directory is NOT loaded just because it exists.
  - Otherwise → load the entry with the newest `savedAt`.
- FR-9 **Missing files fail loudly.** If the newest entry's files are gone or unreadable,
  startup fails with an error naming the entry and its location — it does not silently
  fall back to an older entry (that would resurrect stale data unnoticed). The operator
  either restores the files or removes the entry (FR-12) and restarts.
- FR-10 **WAL semantics unchanged.** The registry replaces checkpoint *discovery*, not
  recovery: WAL entries anchored after the loaded checkpoint replay as today, and an
  unanchored WAL with an empty registry replays from empty exactly as it does now.
- FR-11 **Migration.** Existing deployments (checkpoints on disk, no metadata) start
  empty after upgrading. The documented one-time migration is loading the checkpoint via
  `PUT /load` (or the Studio screen) — FR-7 then registers it permanently. The startup
  log states this plainly when it detects checkpoint-like files but an empty registry.

### 3.4 REST surface
- FR-12 Endpoints (root-level routes, JSON, same conventions as spec web-ui §5):
  - `GET /savegames` → all entries, newest first.
  - `GET /savegames/{id}` → one entry (204 when unknown).
  - `PUT /savegames/{id}/load?waitForCompletion=true` → load that save game (400/404/500
    mapped like `PUT /load`; the graph is replaced).
  - `DELETE /savegames/{id}?deleteFiles=false` → remove the entry; with
    `deleteFiles=true` also delete the checkpoint files. 404 when unknown.
  - `PUT /save` (existing) now returns the created registry entry instead of the bare
    path string (the path is a field of the entry).
  - `PUT /load` (existing) keeps accepting arbitrary paths and auto-registers (FR-7).
- FR-13 Save/load/delete are gated like the other admin writes (authenticated when an
  API key is configured); reads follow the open-reads posture.

### 3.5 F8 Studio
- FR-14 **"Save games" screen**, top-level in the left rail directly under "Dashboard":
  a table of entries (saved at, trigger, location, vertices, edges, files, total size,
  indices/plugins summary) with per-row **Load…** (typed confirmation naming the
  instance + endpoint, FR-1d of the web-ui spec: loading replaces the graph) and
  **Delete…** (typed confirmation; checkbox for deleting files too), plus a **Save
  now** action that refreshes the table with the new entry.
- FR-15 The dashboard's Save action reflects the new response shape and links to the
  Save games screen.

## 4. Registry entry schema

```json
{
  "schemaVersion": 1,
  "saveGames": [
    {
      "id": "sg-20260715-093012-4f2a",
      "savedAt": "2026-07-15T09:30:12.412Z",
      "trigger": "api | shutdown | imported",
      "location": "C:/Fallen8/database.f8s",
      "fileCount": 9,
      "totalBytes": 73400320,
      "engineVersion": "0.9.3.0",
      "kpis": {
        "vertexCount": 1000,
        "edgeCount": 10000,
        "usedMemoryBytes": 282016350,
        "indices": [ { "indexId": "myIndex", "pluginType": "DictionaryIndex" } ],
        "availableIndexPlugins": ["DictionaryIndex", "..."],
        "availablePathPlugins": ["BLS", "DIJKSTRA"],
        "availableServicePlugins": [],
        "subGraphs": ["friends-of-trent"]
      }
    }
  ]
}
```

`id` is generated at registration (sortable timestamp prefix + random suffix) and is the
REST identifier. Unknown fields are preserved on rewrite (forward compatibility).

## 5. Non-functional requirements

- **Atomicity:** registry writes are temp-file + rename; concurrent saves are already
  serialized by the single-writer transaction model.
- **Performance:** registration adds file-stat calls and a JSON rewrite — no graph
  scans; the registry is read once at startup and on demand by the API.
- **Honesty:** sizes/counts reflect registration time; the UI labels them as "at save
  time". A registry entry is a record, not a guarantee the files still exist (FR-9
  handles the gap loudly).

## 6. Acceptance scenarios

1. Fresh instance, no metadata → starts empty. `PUT /save` → `GET /savegames` lists one
   entry with correct counts, location, file count, and non-zero size.
2. Restart → the instance boots from that newest entry (counts match), not from
   directory discovery.
3. Save twice, restart → the newer entry loads.
4. Empty registry + a checkpoint file manually placed in the storage directory →
   restart starts EMPTY and logs the migration hint.
5. `PUT /load` with a foreign checkpoint path → loads and auto-registers as
   `imported`; a second load of the same path adds no duplicate.
6. Newest entry's files deleted → restart fails loudly naming the entry; after
   `DELETE /savegames/{id}` the next restart uses the next-newest (or starts empty).
7. Studio: Save now → row appears; Load… demands the typed instance name and then the
   dashboard shows the restored counts; Delete… removes the row (files kept unless the
   checkbox was ticked).

## 7. Testing requirements

- **Backend (MSTest, pipeline where possible):** registry read/write atomicity +
  corruption failure; save registers (fields pinned); shutdown-save registers; load
  auto-registers once; startup empty-without-metadata, newest-wins, missing-files-fail;
  delete with and without `deleteFiles`; `PUT /save` response shape; source-gen parity
  for the new DTOs.
- **UI:** client route/serialization contract test entries; Save games screen component
  behaviour (confirmation gating, delete checkbox); e2e scenario 7 above against a live
  apiApp.

## 8. Deliverables and workflow

1. This spec and [plan.md](./plan.md).
2. Implementation on branch `feature/save-games` (branched after the web-ui feature
   merged, since the Studio screen builds on it), merged to `main` after review.
   Commit messages are honest and concise and do not reference an AI assistant.
