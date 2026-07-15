# Hosted Durability Lifecycle — Specification

> **Status:** Implemented (P0 architectural) — from the 2026-07 principal-architect & performance
> review. The durability engine (hardened checkpoint + opt-in WAL) existed but was **not wired into
> the hosted API**, so the running server was purely volatile: a restart or crash lost everything
> unless a client happened to call `/save`. The hosted API now persists by default (load on boot,
> WAL between snapshots, save on clean shutdown); volatile is an explicit opt-out. Periodic/threshold
> checkpointing (§3.5, Phase 4) is deferred to the write-path-throughput / crash-durability themes.

## 1. Problem / current state

The engine has a complete, tested durability story — an atomic, versioned, integrity-checked
checkpoint (`persistence-hardening` Stage A/B) and an opt-in write-ahead log that recovers committed
transactions between snapshots (`persistence-hardening` Stage D, extended by `wal-subgraph-support`).
**None of it is reachable from the hosted process.** The web app boots a volatile engine and never
loads, saves, or flushes on its own:

- The DI registration binds the WAL-less constructor: `Program.cs:87` registers
  `builder.Services.AddSingleton<IFallen8, Fallen8>()`, which resolves `Fallen8(ILoggerFactory)`
  (`Fallen8.cs:248`). That constructor never touches the WAL. The WAL-enabling overload
  `Fallen8(ILoggerFactory, WriteAheadLogOptions, ISubGraphRecipeCompiler)` (`Fallen8.cs:294`) —
  the only way to turn durability on — is **never constructed anywhere in the app**.
- There is **no `IHostedService` / `IHostApplicationLifetime` hook** in the app project (verified: no
  `AddHostedService`, `IHostedService`, `BackgroundService`, or `ApplicationStopping` reference). So
  nothing discovers and loads a checkpoint at startup, and nothing saves or flushes the WAL on
  shutdown. `Program.cs` ends at `app.Run()` (`Program.cs:115`).
- The only checkpoint-discovery logic — `AdminController.FindLatestFallen8()` (`AdminController.cs:335`)
  — is **dead code**: it is defined but never called (the only reference in the tree is its own
  declaration). It globs the base directory for `Temp.f8s` version files and picks the newest by the
  UTC/monotonic `ToBinary` stamp, but no code path invokes it.
- Durability is therefore **entirely manual and best-effort**: a client must `PUT /save`
  (`AdminController.cs:213`) before the process ends, and even then a subsequent boot does not
  reload it — a client would additionally have to `PUT /load` (`AdminController.cs:173`) with the
  exact path. A crash between manual saves loses everything committed since the last one.
- The recipe compiler is registered imperatively **after** the container is built:
  `Program.cs:100` does `app.Services.GetRequiredService<IFallen8>().SubGraphRecipeCompiler = new
  RecipeSubGraphCompiler();`. This forces the singleton to construct at that line (WAL-less today, so
  harmless), then attaches the compiler. It is set before any manual `/load`, so subgraphs rehydrate
  on the manual path — but it is attached **too late** to help an unanchored-WAL replay, which runs
  *inside* the constructor (`Fallen8.EnableWriteAheadLog` → `ReplayWriteAheadLog`, `Fallen8.cs:315`).
- No configuration surface exists: `appsettings.json` carries only logging + `AllowedHosts`, no
  storage directory, WAL path, or durability toggle.

Net effect: the default, out-of-the-box behaviour of a graph *database* server is to lose all data on
exit. That is the P0.

## 2. Goals / non-goals

**Goals**

- Make **durable operation the default** for the hosted API: on a normal boot the server discovers
  and loads the latest checkpoint, opens the paired WAL, and recovers committed transactions; on a
  clean shutdown it flushes/saves so the next boot is up to date.
- Bind a durability **configuration section** (storage directory, WAL path, volatile opt-out) from
  `appsettings.json` and register the singleton through the WAL-enabling constructor
  (`Fallen8.cs:294`) with the recipe compiler supplied **at construction** so persisted *and*
  WAL-replayed subgraphs rehydrate.
- Add an `IHostedService` that owns the load-on-start / flush-on-stop lifecycle, reusing (not
  duplicating) the existing checkpoint discovery and the existing Save/Load transactions.
- Make **volatile mode an explicit opt-out** (`Volatile: true`), never the accidental default.
- Revive or retire the dead `FindLatestFallen8` — relocate it into a reusable, testable discovery
  helper (durable mode) or delete it (if the config resolves volatile).

**Non-goals**

- **Changing the on-disk format** (the checkpoint envelope and WAL framing from `persistence-hardening`
  stay exactly as-is; this theme only *invokes* them).
- **The non-blocking-save rewrite** — moving checkpoint writing off the single writer thread was
  measured and **deferred** in `non-blocking-save/`; a save (including the shutdown save and any
  periodic checkpoint) still blocks the writer for its duration. This theme reuses the
  blocking-but-correct save and inherits that deferral; see §5 and the plan's revisit condition.
- **Distributed / replicated persistence** (single-node durability only).
- **Auth / transport hardening of the admin surface** — `/save`, `/load`, `/tabularasa` remain open;
  locking them down is `api-security-boundary/`'s concern (it shares `Program.cs`, so land order
  should be coordinated), not this theme's.

## 3. Design sketch

### 3.1 Durability configuration

Add an options type bound from a `Fallen8:Durability` config section (name illustrative), e.g.:

```jsonc
// appsettings.json
"Fallen8": {
  "Durability": {
    "StorageDirectory": null,     // default: AppContext.BaseDirectory (matches AdminController's default save dir)
    "CheckpointBaseName": "Temp.f8s", // matches AdminController._saveFile so an auto-load finds a default manual save
    "WalPath": null,              // default: <StorageDirectory>/fallen8.wal
    "Volatile": false,            // explicit opt-out; false ⇒ durable
    "SaveOnShutdown": true        // clean-shutdown final Save (else: flush WAL only)
  }
}
```

Bound to a `Fallen8DurabilityOptions` POCO via `builder.Services.Configure<>()`. Defaults are chosen
so the *durable* path is the default and so an auto-load discovers what a default `PUT /save`
already writes (`AppContext.BaseDirectory` + `Temp.f8s`).

### 3.2 WAL-enabled singleton registration

Replace the type registration at `Program.cs:87` with a factory registration:

- **Durable (default):**
  `new Fallen8(loggerFactory, new WriteAheadLogOptions(walPath), new RecipeSubGraphCompiler())`.
  Passing the compiler to the constructor (not post-build) is load-bearing: an *unanchored* WAL
  replays during construction (`Fallen8.cs:315`), and only a compiler present at that instant can
  recompile its subgraph entries.
- **Volatile (opt-out):** `new Fallen8(loggerFactory)` — the current behaviour, explicitly chosen.

Remove the imperative `SubGraphRecipeCompiler` assignment at `Program.cs:100` (folded into the
factory). `RecipeSubGraphCompiler` lives at `App/Helper/RecipeSubGraphCompiler.cs`.

### 3.3 Checkpoint discovery helper

Relocate the discovery logic out of `AdminController` into a reusable, unit-testable helper following
the repo's `Try*` convention, e.g.
`CheckpointDiscovery.TryFindLatestCheckpoint(storageDir, checkpointBaseName, out string path) : bool`.
Move the body of `FindLatestFallen8` (`AdminController.cs:335`) verbatim — the `Constants.VersionSeparator`
glob, the sidecar/temp exclusions, and the newest-by-`DateTime.FromBinary(stamp)` selection are all
correct and stay. Delete the private method from the controller. The helper is consumed by the hosted
service (and could later back an admin "auto-load" endpoint). In **volatile** mode nothing calls it,
so the dead method is genuinely retired rather than merely moved.

### 3.4 Hosted lifecycle service

Add `DurabilityLifecycleService : IHostedService`, registered with `AddHostedService<>()`, taking the
`IFallen8` singleton, `IOptions<Fallen8DurabilityOptions>`, `IHostApplicationLifetime`, and a logger.

- **`StartAsync` (load on boot):** if `Volatile`, log "volatile mode" and return. Otherwise call
  `CheckpointDiscovery.TryFindLatestCheckpoint(...)`:
  - **Found:** enqueue a `LoadTransaction { Path = path, StartServices = … }` and `WaitUntilFinished()`.
    `LoadTransaction.TryExecute` → `Fallen8.Load_internal` (`Fallen8.cs:1521`), which loads the
    snapshot and replays the **paired** WAL (the WAL↔snapshot pairing from `persistence-hardening`
    Stage D). If the load rolls back, log loudly and (per the same policy as `/load`) fail startup
    rather than silently serving an empty graph.
  - **Not found:** start clean. Any **unanchored** WAL was already replayed during construction
    (§3.2), so a crash-before-first-save is still recovered; a truly empty storage dir starts empty
    with no error.
  This runs before the host begins accepting requests, so it needs no coordination with controllers.
- **Shutdown (flush/save on stop):** register on `ApplicationStopping` (or override `StopAsync`). If
  `Volatile`, no-op. Otherwise:
  1. Stop accepting new work is handled by the host; enqueue a final `SaveTransaction { Path =
     <StorageDirectory>/<CheckpointBaseName> }` (when `SaveOnShutdown`) and `WaitUntilFinished()`, so
     a clean shutdown leaves an up-to-date snapshot and resets the WAL against it.
  2. If `SaveOnShutdown` is false (or the final save is skipped/throttled), the WAL is already durable
     per-commit — committed transactions survive regardless; the shutdown save is an optimization that
     shortens the next boot's replay, not a correctness requirement.
  The single-writer queue drains naturally: the enqueued Save is the last item, and
  `WaitUntilFinished()` returns only after it has committed (temp + fsync + atomic rename).

### 3.5 Optional periodic / threshold checkpoint hook

A periodic or write-count-threshold checkpoint (bounding WAL growth and boot-replay time) is a natural
extension but is **out of scope to fully build here** and must be **coordinated, not duplicated**, with
the write-path-throughput and crash-durability-hardening themes (which own the write-rate and
crash-frequency policy). If a minimal hook is included, it is config-gated and **off by default**, and
it enqueues the same blocking `SaveTransaction` — so it inherits the `non-blocking-save` stall (§5).

## 4. Acceptance criteria

- **Survives a clean restart (integration, `WebApplicationFactory`):** start the host over a temp
  storage directory, create vertices/edges via REST, trigger a clean shutdown (dispose the host →
  `ApplicationStopping` runs the final Save), start a **new** host over the **same** directory, and
  assert the graph (counts + a spot-checked vertex/edge with properties) survived.
- **Recovers a crash via WAL replay (integration):** with the WAL enabled, create + commit data, then
  simulate a crash by disposing/abandoning the host **without** running the clean-shutdown Save (skip
  `SaveOnShutdown`), start a new host over the same directory, and assert the committed transactions
  were recovered by WAL replay (unanchored-log replay when no snapshot exists, paired-log replay when
  one does).
- **Clean start with no checkpoint (integration):** starting over an empty storage directory starts a
  working, empty graph with no error and serves requests.
- **Volatile opt-out:** with `Volatile: true`, no checkpoint is loaded and no shutdown save occurs
  (a restart loses data by explicit choice); the dead discovery method is gone.
- **Recipe rehydration:** a persisted subgraph (recipe-bearing) is present after a restart in durable
  mode, and a WAL-replayed subgraph is recovered after a simulated crash — because the compiler is
  registered at construction (§3.2). Delegate-only subgraphs are absent after reload, matching
  snapshot/WAL semantics (`wal-subgraph-support`).
- **Config documented:** the storage-directory, checkpoint-base-name, WAL-path, `Volatile`, and
  `SaveOnShutdown` keys are documented (in this feature's README/appsettings comment).
- Full suite green; the new hosted-lifecycle tests pass and the existing engine/WAL/persistence tests
  are unaffected (the engine's WAL default stays **off** — only the hosted app opts in).

## 5. Risks

- **Shutdown save blocks the writer (non-blocking-save deferred).** The final Save runs on the single
  writer thread and stalls it for the save duration — measured in `non-blocking-save/` at 170 ms @
  100k, 433 ms @ 400k, 907 ms @ 2M elements. On a large graph this can approach ASP.NET Core's default
  shutdown timeout (30 s), which would **truncate the save**. Mitigation: the per-commit WAL is the
  durability guarantee (committed work is already fsync'd), so the shutdown Save is best-effort — if it
  cannot finish within the shutdown window it is skipped, not half-written (the atomic temp+rename
  means a truncated save never becomes the loadable checkpoint). Consider making the shutdown Save
  optional/size-gated and documenting the timeout interaction.
- **WAL-on changes write throughput (now the default).** Enabling the WAL adds a per-commit fsync to
  every data-mutating transaction. Making it the default is the correct durability posture but is a
  real throughput change; coordinate the messaging/tuning with write-path-throughput. It stays a
  single explicit config choice (`Volatile` to opt out entirely).
- **Storage path / permissions.** A configured directory that is missing or not writable must fail
  **loudly at startup** (or on first save), never silently degrade to volatile — a silent fallback
  would reintroduce the exact P0 this theme fixes.
- **Startup-load rollback.** A corrupt/unloadable checkpoint is clean-rejected by
  `persistence-hardening` Stage A; the hosted service must treat a rolled-back `LoadTransaction` as a
  startup failure (loud), not serve an empty graph as if fresh.
- **Test fidelity of "crash".** `WebApplicationFactory` restarts are in-process; a crash is simulated
  by not running the clean-shutdown save (relying on WAL durability). This exercises the WAL-replay
  path faithfully without an actual process kill.

## 6. Keep (do not regress)

- **The engine's WAL default stays OFF.** Only the *hosted app* opts in via config; every existing
  engine constructor path and the whole pre-WAL/WAL test suite keep their current behaviour
  (`persistence-hardening` Stage D, `WriteAheadLogOptions.cs` "disabled by default" contract).
- **Single-writer + lock-free reads.** Load and the shutdown/periodic Save go through the transaction
  queue on the single writer thread; reads stay lock-free over the volatile snapshot. No new mutation
  path is introduced.
- **Atomic / versioned / integrity-checked checkpoint + clean-reject** (`persistence-hardening` Stage
  A/B) and the **WAL↔snapshot pairing, torn-tail safety, and commit-order replay** (Stage D +
  `wal-subgraph-support`) are used as-is, not modified.
- **Manual `/save` and `/load` still work** and compose with the lifecycle: a manual save resets the
  WAL against the new snapshot exactly as today; the hosted service just adds automatic
  load-on-start and save-on-stop around them.
- **Blocking-but-correct save** — the deliberate `non-blocking-save` outcome; do not move the save
  off-worker as part of this theme.
