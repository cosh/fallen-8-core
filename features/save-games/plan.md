# Save Games — Phased Implementation Plan

> Satisfies the plan deliverable of [spec.md](./spec.md). Each phase ships its tests.

1. **Registry core.** `SaveGameRegistry` service in the apiApp (`Services/`): entry
   model + JSON persistence under `<deployment>/metadata/savegames.json` (atomic
   replace, schemaVersion, corruption = loud failure), KPI capture from a live engine,
   file-count/size measurement for a checkpoint path. Options binding
   (`Fallen8:Metadata`). *Tests:* round-trip, atomicity (temp+rename), corrupt file
   fails, KPI snapshot fields.
2. **Record on save/load.** Hook `PUT /save`, the shutdown auto-save, and `PUT /load`
   (auto-register unknown paths, no duplicates). `PUT /save` returns the entry.
   *Tests:* pipeline save→entry (fields pinned), shutdown-save registers, import-once
   semantics.
3. **Registry-driven startup.** Replace directory discovery in the boot path: empty
   registry → empty start (+ migration hint log when checkpoint-like files exist),
   else newest `savedAt` loads; missing files fail startup loudly; WAL replay
   unchanged. *Tests:* the four startup scenarios (spec §6 1–4, 6).
4. **REST surface.** `SaveGamesController`: list/get/load/delete (deleteFiles opt-in),
   OpenAPI docs, AppJsonContext + parity coverage, auth posture like the other admin
   writes. *Tests:* endpoint contract incl. 204/404 and delete variants.
5. **F8 Studio.** "Save games" rail item under Dashboard: table + Save now + Load…/
   Delete… with typed confirmations; dashboard Save uses the new response. Client
   endpoints + contract-test entries. *Tests:* component gating; e2e scenario 7.
6. **Docs + migration note.** README/durability docs updated: registry-driven startup,
   the one-time `PUT /load` migration for pre-existing checkpoints.
