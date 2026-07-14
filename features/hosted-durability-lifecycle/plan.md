# Hosted Durability Lifecycle — Plan

Companion to [spec.md](./spec.md). Wire the existing durability engine (checkpoint + opt-in WAL) into
the hosted API so the running server persists by default. Reuse the Save/Load transactions and the
existing discovery logic — build nothing the engine already has.

GitHub issue: to be opened (label: feature).

## Phase 0 — Baseline & guardrails

Prove the P0 and pin the behaviour the fix flips, before changing wiring.

- [ ] Add `HostedDurabilityLifecycleTest` (MSTest, `TestLoggerFactory.Create()`) with a
  `WebApplicationFactory<Program>` helper that points the durability config at a fresh temp storage
  directory per test (scratch dir, cleaned up in teardown).
- [ ] **Characterization (RED → GREEN):** start the host, create data via REST, dispose the host,
  restart a new host over the **same** directory, and assert data is **GONE today** — documenting the
  volatile default. This test is migrated (not deleted) in Phase 3 to assert data **survives**.
- [ ] Opt-in `[TestCategory("Benchmark")]` + `[Ignore]` micro-benchmark measuring the shutdown-save
  stall vs. graph size (reuse the `non-blocking-save` numbers as the reference; capture fresh: "to be
  captured on this box") so the shutdown-timeout risk (spec §5) is sized, not guessed.

## Phase 1 — Durability configuration + WAL-enabled singleton

Intent: turn durable operation on by default, configurably.

- [ ] Add `Fallen8DurabilityOptions` (POCO: `StorageDirectory`, `CheckpointBaseName`, `WalPath`,
  `Volatile`, `SaveOnShutdown`) and bind it from the `Fallen8:Durability` section via
  `builder.Services.Configure<Fallen8DurabilityOptions>(...)` in `Program.cs`. Sensible defaults:
  `StorageDirectory` → `AppContext.BaseDirectory`, `CheckpointBaseName` → `"Temp.f8s"` (matches
  `AdminController._saveFile`), `WalPath` → `<StorageDirectory>/fallen8.wal`, `Volatile` → `false`,
  `SaveOnShutdown` → `true`.
- [ ] Replace the type registration at `Program.cs:87` with a factory registration:
  durable ⇒ `new Fallen8(loggerFactory, new WriteAheadLogOptions(walPath), new
  RecipeSubGraphCompiler())` (`Fallen8.cs:294`); volatile ⇒ `new Fallen8(loggerFactory)`
  (`Fallen8.cs:248`).
- [ ] Remove the imperative `SubGraphRecipeCompiler` assignment at `Program.cs:100` (now supplied to
  the constructor). Add the documented `Fallen8:Durability` block to `appsettings.json` with comments.

## Phase 2 — Checkpoint discovery helper

Intent: make discovery reusable and testable; retire the dead method.

- [ ] Add `CheckpointDiscovery.TryFindLatestCheckpoint(string storageDir, string checkpointBaseName,
  out string path) : bool` (repo `Try*` convention), moving the body of
  `AdminController.FindLatestFallen8` (`AdminController.cs:335`) verbatim — the
  `Constants.VersionSeparator` glob, sidecar/temp exclusions, and newest-by-`DateTime.FromBinary(stamp)`
  selection.
- [ ] Delete `FindLatestFallen8` from `AdminController`. (In volatile mode nothing references the
  helper — the logic is genuinely retired, not just relocated.)
- [ ] Unit-test the helper: no files ⇒ `false`; a single un-versioned file ⇒ found; multiple versioned
  files ⇒ newest by stamp; sidecar/temp files excluded.

## Phase 3 — Hosted lifecycle service (load on start, flush/save on stop)

Intent: own the boot-load / shutdown-save lifecycle around the existing transactions.

- [ ] Add `DurabilityLifecycleService : IHostedService` (ctor: `IFallen8`,
  `IOptions<Fallen8DurabilityOptions>`, `IHostApplicationLifetime`, `ILogger<>`), registered via
  `builder.Services.AddHostedService<DurabilityLifecycleService>()`.
- [ ] `StartAsync`: volatile ⇒ log + return. Else `CheckpointDiscovery.TryFindLatestCheckpoint(...)`;
  if found, enqueue `LoadTransaction { Path = path }` + `WaitUntilFinished()`
  (`LoadTransaction` → `Fallen8.Load_internal`, `Fallen8.cs:1521`, which replays the paired WAL). A
  rolled-back load ⇒ log loudly and fail startup (do not serve an empty graph). Not found ⇒ start
  clean (an unanchored WAL already replayed during construction, `Fallen8.cs:315`).
- [ ] Shutdown: register on `IHostApplicationLifetime.ApplicationStopping` (or `StopAsync`). Volatile
  ⇒ no-op. Else, when `SaveOnShutdown`, enqueue a final `SaveTransaction { Path =
  <StorageDirectory>/<CheckpointBaseName> }` + `WaitUntilFinished()` so the WAL is reset against a
  fresh snapshot; if it cannot complete within the shutdown window, skip (WAL already durable — never
  a torn save, per the atomic temp+rename). Log the outcome.
- [ ] Migrate the Phase-0 characterization test to assert data **survives** a clean restart; add the
  crash-recovery test (skip the shutdown save, restart, assert WAL replay recovered committed data);
  add the empty-directory clean-start test; add the recipe/subgraph rehydration + WAL-replay
  assertions.

## Phase 4 — (Optional) periodic / threshold checkpoint hook

Intent: bound WAL growth and boot-replay time — coordinate, do not duplicate.

- [ ] IF included: a config-gated, **off-by-default** timer/write-count trigger that enqueues the same
  blocking `SaveTransaction`. Its stall policy is shared with write-path-throughput and
  crash-durability-hardening — defer the rate/frequency policy to those themes rather than encoding it
  here. If not included this cycle, note it as the natural next increment.

## Measure & document

- [ ] Run all three integration scenarios (clean restart survives; crash recovers via WAL; empty start
  clean) plus the volatile opt-out; full suite green.
- [ ] Capture the shutdown-save stall for a representative graph size ("to be captured on this box")
  and record whether the default shutdown timeout is adequate.
- [ ] Document the `Fallen8:Durability` config keys (storage directory, checkpoint base name, WAL
  path, `Volatile`, `SaveOnShutdown`) and the durable-by-default / volatile-opt-out behaviour in this
  feature's README (or the appsettings comment block).

## Progress

- [ ] Phase 0 — baseline characterization test + shutdown-save benchmark (opt-in, ignored)
- [ ] Phase 1 — `Fallen8DurabilityOptions` + WAL-enabled factory registration; drop imperative compiler set
- [ ] Phase 2 — `CheckpointDiscovery.TryFindLatestCheckpoint` helper; delete dead `FindLatestFallen8`
- [ ] Phase 3 — `DurabilityLifecycleService` (load on start, save/flush on stop); integration tests
- [ ] Phase 4 — (optional) periodic/threshold checkpoint hook, off by default
- [ ] Measure & document — three scenarios green, stall captured, config keys documented

## Decision / revisit condition

This theme touches the **`non-blocking-save`** deferral: the shutdown save (and any Phase-4 periodic
checkpoint) runs on the single writer thread and blocks it for the save duration (measured: 170 ms @
100k, 433 ms @ 400k, 907 ms @ 2M elements). That deferral **stands** — this theme does not move the
save off-worker; it relies on the per-commit WAL for the real durability guarantee and treats the
snapshot as a best-effort compaction of the log. **Revisit** the off-worker save only under
`non-blocking-save`'s own condition (tens-of-millions of elements saved frequently), which here maps
to: the shutdown save routinely approaching the host shutdown timeout, or a periodic-checkpoint policy
(owned by write-path-throughput / crash-durability-hardening) that snapshots often enough that the
writer stall becomes a throughput ceiling. Until then, keep the blocking-but-correct save.
