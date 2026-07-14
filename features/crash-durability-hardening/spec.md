# Crash-Durability Hardening — Specification

> **Status:** Planned (P1 durability) — from the 2026-07 principal-architect & performance review.
> The WAL/checkpoint machinery is correct when every write succeeds but fails *silently* exactly when
> it is needed: a partial append is masked, a failed load runs an unlogged compaction, a pre-load
> mutation is logged against the wrong baseline, a replay failure either misapplies later entries or
> escapes, and the rename commit points are durable in program order but not in storage order. This
> theme makes every durability failure loud and recovery-safe and makes the commit-point renames
> actually crash-durable.

This builds directly on the opt-in write-ahead log and hardened self-describing checkpoint that
landed in [persistence-hardening/](../persistence-hardening/) (do not re-propose the WAL, the
segmented store, the atomic/versioned/CRC snapshot envelope, or the torn-tail reader — build on them)
and on the subgraph logging that landed in [wal-subgraph-support/](../wal-subgraph-support/). It is
adjacent to (and will be cross-referenced by) `load-path-integrity/` and `transaction-atomicity/`.

## 1. Problem / current state

Every item below was read in the current tree and the line references verified.

| # | Issue | Location (verified) | Effect |
|---|-------|---------------------|--------|
| D1 | A WAL append failure poisons the tail but appends continue. `Append` opens `FileMode.Append`, writes one frame, `Flush(true)`, closes — a fresh handle per commit, with **no** partial-frame rollback and **no** sticky fence. A failed append can leave a torn frame; the next commit appends after it; `ReadEntries` stops at the first bad frame, so every later, individually-fsynced, **acknowledged** transaction is silently dropped on replay. Only the failing tx is marked. | `WriteAheadLog.cs:211` (`Append`), `:254` (`ReadEntries` stops at first bad frame); `Transaction/TransactionManager.cs:220` (`LogCommittedTransactionSafely` sets `txInfo.Error` on **only** the failing tx and keeps acking later ones as durable) | Silent loss of committed, acknowledged transactions after a partial append. A per-append fresh handle means a failed `Flush(true)` gives no guarantee that earlier frames reached disk while later ones did. |
| D2 | A failed `Load` runs an **unlogged** `Trim_internal` while the WAL is enabled. The `success == false` restore branch falls through to `if (!walHandledIdSpace) Trim_internal();` with no Trim marker appended (contrast auto-trim, which logs one). | `Fallen8.cs:1645` (failed-load `else`) → `:1659` (unguarded `Trim_internal`); contrast `:1920` (`MaybeAutoTrim` logs a `Trim` marker) | The post-trim id space diverges from the log baseline; every later logged entry references the reassigned id space, but replay pairs snapshot + log **without** that trim → entries resolve the wrong elements. |
| D3 | A WAL adopted at construction accepts appends before its paired snapshot is loaded. `EnableWriteAheadLog` anchors the log (an *unanchored* log replays immediately; an *anchored* one just waits) with no guard; `LogCommittedTransaction` appends whenever `!_walSuspended`, and `_walSuspended` only covers replay. | `Fallen8.cs:315` (`EnableWriteAheadLog`, anchored branch sets no guard), `:1318` (`LogCommittedTransaction` appends unconditionally) | Mutating before `Load` records ids against the **empty** initial graph in a file whose header claims the snapshot baseline → replay onto the real snapshot produces a state that never existed. |
| D4 | Replay failure policy is inconsistent. A decode failure `break`s (correct), but `tx.TryExecute == false` only **warns and continues**, and a throw (e.g. an `ArgumentOutOfRangeException` out of a mutation resolver) is **uncaught** and escapes. | `Fallen8.cs:1359` (`ReplayWriteAheadLog`), `:1384` (false → warn + continue), no `try/catch` around `TryExecute` | For a core **data** entry, continuing past a failure misapplies every later entry (silent corruption); a throw escapes the constructor (unanchored replay) or `Load` (paired replay) with a half-replayed graph already **published**. |
| D5 | Rename commit points are not crash-durable. File **contents** are fsync'd (`Flush(true)`) before rename, but the renames themselves are not, and there is **no** parent-directory fsync anywhere in the tree (verified: no `MoveFileEx`/`FlushFileBuffers`/directory fsync). | `Persistency/PersistencyFactory.cs:431` (header rename = commit point), `:691` (sidecar renames), `WriteAheadLog.cs:367` (WAL reset rename) | On POSIX a power loss can undo the header rename after `Save` returned. The load-bearing "snapshot rename durable **before** WAL reset" ordering (`Fallen8.cs:860`) holds in *program* order but not in *storage* order. |
| D6 | The subgraph recipe manifest is written **after** the commit point, its failure is **swallowed** (caught + logged, not thrown), yet `Save` still returns success and resets the WAL. Since `wal-subgraph-support` landed, `CreateSubGraph`/`RemoveSubGraph` entries **are** in the WAL — so the reset now discards recoverable subgraph work. | `PersistencyFactory.cs:431` (commit) then `:436` → `:497`/`:528` (`SaveSubGraphRecipes` catches + logs, does not throw); `Fallen8.cs:860` (`_wal.ResetToSnapshot` after `Save` returns) | A crash — or just a manifest write error — in that window yields a committed snapshot with **no** recipes **and** discarded `CreateSubGraph` WAL entries → subgraphs lost from **both** durability paths. |
| D7 | Recipe replay recompiles arbitrary C# at load via Roslyn. The CRC is integrity, not authenticity. | `Fallen8.cs:1425` (`ReplaySubGraphCreate` → `compiler.TryCompile(recipe)`) | Anyone who can write the save/WAL files gains code execution in the loading process at next start. Separately, recovery latency scales with the number of *distinct* recipe specs (one Roslyn compile each). |

The common shape: the happy path is well engineered, but each failure mode either loses
acknowledged work without a signal or diverges the on-disk state from what a subsequent replay
assumes.

## 2. Goals / non-goals

**Goals.**
- No committed, acknowledged transaction is ever silently dropped. A durability failure is **loud**
  (logged once, not per-tx spam) and **observable** (recorded on every affected
  `TransactionInformation`, not only the one that failed).
- The on-disk id space a paired snapshot+log replays against is exactly the one recorded — no
  unlogged trim, no pre-load appends against the wrong baseline.
- Replay of a **core data** entry is fail-stop: it stops cleanly at the last good entry, reports
  replayed-vs-remaining counts, and never lets an exception escape construction or `Load`.
- The three commit-point renames (snapshot header, sidecars, WAL reset) are durable in **storage**
  order, not just program order.
- A committed snapshot always carries its recipe manifest; a recipe-manifest failure never silently
  strands the `CreateSubGraph` WAL entries that could recreate the subgraphs.
- The recipe-replay trust boundary is documented, and recovery reuses a spec-hash compile cache.

**Non-goals.**
- Moving the save off the single writer thread. `non-blocking-save/` **measured** the writer-stall
  (170 ms @ 100k, 433 ms @ 400k, 907 ms @ 2M elements) and **deferred** it; the blocking-but-correct
  save is retained. D5/D6 do **not** touch that.
- CSR adjacency (`csr-adjacency/` assessed and **skipped**) and any change to the single-writer /
  lock-free-read model, the transaction model, or the WAL/snapshot framing.
- Cryptographic authenticity/signing of save files. D7 **documents** the trust boundary and points
  at the operational mitigation (directory permissions); signing is out of scope.
- No new *snapshot* format version — these are durability/ordering fixes plus one additive WAL
  header field (see D5).

## 3. Design sketch

### D1 — Sticky WAL-failure fence
- In `WriteAheadLog.Append`, capture the pre-append length (in `FileMode.Append` the stream is
  positioned at end before the write, so `fs.Length` is the pre-append length). Wrap the
  write+flush; on any failure, best-effort `fs.SetLength(preLength)` to drop a torn frame, then set a
  sticky `_failed` flag and rethrow. Once `_failed`, `Append` refuses further writes (a no-op that
  does not touch the file) and logs **one** Error — no per-commit spam.
- Add a durability signal on `TransactionInformation` — a `bool Durable` (default `true`) set under
  the same happens-before as `Error`/`FailureReason`. `TransactionManager.LogCommittedTransactionSafely`
  sets `Durable = false` on **every** committed transaction once the fence has tripped, not only the
  first failure. The transaction stays `Finished` (it is applied in memory) but the caller can see
  its log durability is degraded.
- A successful `Save` (`ResetToSnapshot`) rewrites the log fresh and **clears** the fence: the new
  snapshot is the durable baseline. This is the sanctioned recovery from a degraded WAL.

### D2 — Symmetric no-Trim on a failed load
- Make the failed-load restore symmetric with the success path. In the `success == false` branch,
  when `_wal != null`, set `walHandledIdSpace = true` so the closing `if (!walHandledIdSpace)
  Trim_internal();` is skipped — the restored `oldSnapshot` already sits in the exact id space the
  log baseline was recorded against, so it must not be reassigned. Also restore `_currentId =
  oldCurrentId` in this branch (the catch branch already does; the `else` branch currently does not),
  so a partially-advanced counter cannot survive a failed load.
- Equivalent alternative (rejected as the primary, kept as the fallback): append a `Trim` marker so
  replay reproduces the reassignment. Skipping is simpler, symmetric with the success path, and
  avoids logging a trim that never needed to happen.

### D3 — Awaiting-paired-load fence
- Add `_walAwaitingPairedLoad`. In `EnableWriteAheadLog`, set it when the adopted log is **anchored**
  (has a pairing token, i.e. it waits for its snapshot to be `Load`ed). An unanchored log replays
  during construction and never enters this state; a fresh empty log has a zero baseline and is not
  awaiting.
- While set, `LogCommittedTransaction`/`LogWriteAheadLogMarker` **suspend** logging (return early)
  and mark those transactions degraded via the D1 `Durable = false` channel — a pre-load mutation is
  applied in memory but not recorded, and the caller can see it was not durable. (Recommended usage
  remains: `Load` before you mutate.)
- Cleared by `Load_internal` (both the pairing branch and the re-anchor branch) and by `Save`. After
  the paired `Load`, replay reconstructs exactly snapshot + its own log; the discarded pre-load
  mutation was never part of any consistent state.

### D4 — Fail-stop replay for data entries; keep skip-and-continue for subgraph entries
- In `ReplayWriteAheadLog`, wrap the data-entry dispatch (`tx.TryExecute(this)`) in `try/catch` and
  treat **both** a `false` return **and** a thrown exception exactly like the existing decode-failure
  `break`: stop at the last good entry, log `recovered N of M; K remaining not applied`, and never
  let the exception escape. This holds on **both** replay paths (unanchored, during construction; and
  paired, during `Load`) so a throw can never escape the constructor or `Load` with a half-replayed
  graph published.
- **Classify by entry type.** `CreateSubGraph` already routes through `ReplaySubGraphCreate` (which
  catches internally and skips-and-continues — keep that). But `RemoveSubGraph` currently
  deserializes to a real transaction and replays through the generic `tx.TryExecute` path — under the
  new fail-stop policy it would wrongly halt recovery. The dispatch must treat **both** subgraph
  entry types (`CreateSubGraph`, `RemoveSubGraph`) as derived-state entries that skip-and-continue,
  and only the core data/id-space entries as fail-stop. Subgraphs allocate no ids in the parent
  graph, so skipping one does not perturb the id-determinism the surrounding replay relies on.

### D5 — Crash-durable commit-point renames + identity pairing
- Add a small platform helper (e.g. `DurableFileOps.CommitRename(temp, final)` +
  `PersistDirectory(dir)`):
  - **POSIX:** after the `File.Move`, `open(parentDir, O_RDONLY|O_DIRECTORY)` and `fsync` it so the
    rename's directory entry reaches disk.
  - **Windows:** perform the commit-point rename via `MoveFileEx` (P/Invoke) with
    `MOVEFILE_WRITE_THROUGH | MOVEFILE_REPLACE_EXISTING`, so the rename is write-through.
- Route the three commit-point renames through it: `PersistencyFactory.cs:431` (header), `:691`
  (sidecars), `WriteAheadLog.cs:367` (WAL reset). A platform that genuinely cannot fsync a directory
  is detected once and downgraded with a warning rather than crashing the save (see Risks).
- **Identity pairing.** The snapshot header already carries the engine Guid (`writer.Write(fallen8.Id)`).
  Record that Guid in the WAL header alongside the canonicalized path token, and require **both** to
  match in `PairsWith`: the path identifies *which* snapshot instance (saves are version-stamped, so
  successive saves differ by path), and the Guid confirms *provenance* so a foreign or overwritten
  file at the same path cannot mispair. This is additive to the existing canonicalization
  (`NormalizePathToken`), not a replacement. The WAL header gains a field, so its `FormatVersion`
  bumps (clean-reject, consistent with the snapshot format).

### D6 — Recipe manifest before the commit point
- Write the recipe manifest (temp + fsync + durable rename) **before** the header rename at
  `PersistencyFactory.cs:431`, and on manifest failure **fail the `Save`** (throw) rather than
  swallowing it. Because the header has not yet been renamed into place, failing here commits
  nothing: no header at `path` means `Load` never reads the (now-orphan) manifest, and the next
  successful `Save` overwrites it wholesale (the C6 discipline). A committed header therefore always
  has its recipe manifest already durable, and a failed `Save` leaves the WAL **unreset** — its
  `CreateSubGraph` entries survive for the next replay.
- Fuller alternative (noted, not required): fold the recipe manifest into the header's completion
  manifest so a single commit point covers it too. Larger change; the before-the-commit ordering is
  the minimal fix.

### D7 — Recipe-replay trust boundary + compile cache
- Document, in `ReplaySubGraphCreate` and the persistence docs, that the WAL/snapshot recipe CRC is
  **integrity, not authenticity**: replaying a recipe recompiles arbitrary C# via Roslyn, so write
  access to the save/WAL directory is code execution in the loading process. The mitigation is
  operational — the save directory is a trust boundary equivalent to the application binaries.
- Reuse a spec-hash compile cache (the existing `GeneratedCodeCache`, keyed by a hash of the recipe
  `SpecificationJson`) so K subgraphs sharing a spec compile **once**, and a reload that recompiles
  the same specs as a prior run hits the cache. Recovery time then scales with the number of
  *distinct* specs, not the subgraph count. Cached assemblies are already unloadable via
  `collectible-codegen-assemblies/` (landed) — build on that; do not re-propose ALCs.

## 4. Acceptance criteria

Fault-injection tests (MSTest, arrange/act/assert, `TestLoggerFactory.Create()`), each pinning a
failure that is silent today:

- **D1:** inject an append failure, then commit more transactions, drop the instance (simulated
  crash), reopen and replay → recovery includes everything up to the failure **or** clearly reports
  degradation, and **never** silently drops post-failure acknowledged entries. The fence trips once
  (one Error), every post-failure committed tx reports `Durable == false`, and a subsequent `Save`
  clears the fence.
- **D2:** with the WAL enabled, cause a `Load` to fail (foreign/corrupt file), then commit
  mutations, drop the instance, reopen and replay → the graph equals those mutations applied to the
  pre-load state, and every id resolves correctly (no phantom-trim divergence).
- **D3:** with an anchored WAL, committing before `Load` appends **no** bogus entry; after `Load`,
  the replayed state is exactly snapshot + its own log, and the pre-load mutation is reported
  non-durable.
- **D4:** a replay entry that throws **or** returns false stops recovery cleanly at the last good
  entry with a reported count (`recovered N of M`); the exception never escapes the constructor or
  `Load`; the single writer survives. A failing subgraph create/remove entry still
  skips-and-continues so later data entries replay.
- **D5:** a normal save/load round-trips; the platform helper is invoked for all three commit-point
  renames (POSIX parent-dir fsync / Windows `MoveFileEx` write-through, asserted via the seam); a
  WAL paired by identity+path does not mispair with a foreign file placed at the same path.
- **D6:** inject a recipe-manifest write failure → `Save` **fails** (maps to `RolledBack`/500), the
  header is not committed, and the WAL is **not** reset, so a subsequent replay recreates the
  subgraphs. A committed snapshot always has its recipe manifest.
- **D7:** recovery of K subgraphs sharing one spec performs **one** Roslyn compile (cache hit
  observable on the second identical spec); the trust boundary is documented. A recovery-latency
  benchmark vs distinct-spec count is opt-in.

## 5. Risks

- **Platform P/Invoke.** Parent-directory fsync and `MoveFileEx` write-through are platform-specific
  and some filesystems (network/overlay) do not honour them. The helper must detect an unsupported
  directory fsync **once** and downgrade with a warning rather than crashing every save — but a
  commit-point that silently skips its durability step defeats the point, so the tension is: fail the
  save on a *transient* durability error, downgrade-with-warning only on a *structural*
  "this filesystem cannot do it" signal. Decide and pin the policy in tests.
- **Sticky fence semantics.** A transient error (momentary disk-full) leaves the WAL degraded until
  the next `Save`. That is intentional (Save is the recovery) but must be documented so operators
  know how to clear it and are not surprised by a persistent degraded flag.
- **Fail-stop changes recovery semantics.** A mid-log data failure now **truncates** recovery at
  that point (previously it continued past a false). This is the correct safety posture — never
  misapply later entries against a diverged state — and the reported count makes the truncation
  visible; a `Save` then re-baselines. Document the behaviour change.
- **`Save` can now fail where it used to swallow (D6).** Callers must handle a `Save` failure; they
  already do (a throwing save maps to `RolledBack` + `Error` + 500). No new surface, but the
  previously-hidden error is now surfaced.
- **WAL header version bump (D5).** A pre-existing WAL from the prior header layout is reset on open
  (its entries discarded). The WAL is opt-in and re-baselined on every `Save`, so the upgrade path is
  "Save once after upgrading"; the blast radius is small. Note it in the release notes.
- **Compile-cache collisions (D7).** The spec-hash key must use a strong hash plus an equality check
  so two differing specs cannot alias to one compiled delegate.

## 6. Keep (do not regress)

- The **torn-tail** handling in `ReadEntries` — it never over-allocates from an untrusted length and
  stops at the last complete, CRC-valid frame. D1's `SetLength` rollback must not weaken it; the two
  are complementary (rollback on the writer, defensive read on the reader).
- The **snapshot ↔ log path canonicalization** pairing (`PairsWith` / `NormalizePathToken`,
  including the loud warning when a non-pairing log that still holds entries is discarded). D5
  **augments** it with an identity check; it does not remove the canonicalization or the warning.
- The **single-writer** invariant: all appends, the fence, replay, and the platform-rename helper run
  on the single writer thread (or during single-threaded construction). No new concurrency.
- The **clean-reject / magic + version + CRC** envelope discipline (persistence-hardening Stage A).
  New WAL header fields stay inside that envelope.
- The **"snapshot durable before WAL reset"** ordering in `Fallen8.Save`. D5 makes it durable in
  storage order too; it must not be inverted.
- The **blocking-but-correct save on the worker** (`non-blocking-save/` deferral). D5/D6 keep it
  there.
- The **skip-and-continue for subgraph entries** on replay (derived state). D4 keeps it; only core
  data / id-space entries become fail-stop.
- **Replay id-determinism** (baseline restore + store padding + skipped closing `Trim`). D2/D4 must
  not perturb it.
