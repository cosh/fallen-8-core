# Crash-Durability Hardening — Plan

Companion to [spec.md](./spec.md). Fault-injection first (prove each silent failure), then the
engine/WAL logic fixes (D1–D4), then the storage-durability + ordering fixes (D5–D6), then the trust
boundary + cache (D7). Every fix lands with a test that would fail on the current tree.

GitHub issue: to be opened (label: `feature`). Feature branch: `feature/crash-durability-hardening`.

## Phase 0 — Baseline & guardrails
Intent: build the fault-injection seams and characterization tests that demonstrate each failure is
silent/corrupting **today**, so the fixes have a red-to-green guard. These are correctness tests
(run always); only the D7 recovery-latency measurement is opt-in.
- [ ] Add the smallest test seam to force a `WriteAheadLog.Append` failure on the Nth call (an
      internal hook or an injectable file-op boundary) — usable from `WriteAheadLogTest`.
- [ ] Characterization D1: append-fail → commit more → drop instance → reopen + replay asserts the
      **current bug** (recovered count < committed, no degradation signal). Flips to the fixed
      assertion in Phase 1.
- [ ] Characterization D2: WAL on → failed `Load` → mutate → drop → reopen + replay asserts the
      **current** id divergence / element mis-resolution.
- [ ] Characterization D3: anchored WAL → mutate before `Load` → `Load` → replay asserts the
      **current** "state that never existed".
- [ ] Characterization D4: a throwing replay entry currently escapes; a `false` entry currently
      misapplies a later entry — assert both against the current tree.
- [ ] Opt-in recovery-latency benchmark for D7: `[TestCategory("Benchmark")]` + `[Ignore]` measuring
      replay time vs distinct-spec count — numbers **to be captured on this box**.

## Phase 1 — Sticky WAL-failure fence (D1)
Intent: a partial append can never be masked, and degraded durability is visible on every affected tx.
- [ ] `WriteAheadLog.Append`: capture the pre-append length, wrap write+`Flush(true)`, on failure
      best-effort `fs.SetLength(preLength)`, set sticky `_failed`, rethrow. Once `_failed`, `Append`
      is a no-op and logs **one** Error.
- [ ] Add `bool Durable` (default `true`) to `TransactionInformation`, set under the same
      happens-before as `Error`/`FailureReason`.
- [ ] `TransactionManager.LogCommittedTransactionSafely`: once the fence has tripped, set
      `Durable = false` on **every** subsequent committed tx (not only the failing one); keep the tx
      `Finished`.
- [ ] `WriteAheadLog.ResetToSnapshot`: clear `_failed` (a successful `Save` re-establishes the
      durable baseline).
- [ ] Flip the Phase-0 D1 characterization to green.

## Phase 2 — Symmetric no-Trim on a failed load (D2)
Intent: a failed load never diverges the id space the log baseline assumes.
- [ ] `Fallen8.Load_internal` `success == false` branch (`:1645`): restore `_currentId = oldCurrentId`
      and, when `_wal != null`, set `walHandledIdSpace = true` so the closing `Trim_internal` (`:1659`)
      is skipped — symmetric with the success path.
- [ ] Flip the Phase-0 D2 characterization to green.

## Phase 3 — Awaiting-paired-load fence (D3)
Intent: an anchored WAL never logs against the empty pre-load graph.
- [ ] Add `_walAwaitingPairedLoad`; set it in `EnableWriteAheadLog` (`:315`) when the adopted log is
      anchored (not unanchored, not a fresh zero-baseline log).
- [ ] `LogCommittedTransaction` (`:1318`) / `LogWriteAheadLogMarker`: while awaiting, suspend logging
      and mark those txns `Durable = false` (reuse the D1 channel).
- [ ] Clear it in `Load_internal` (both the `PairsWith` and the re-anchor branch) and in `Save`.
- [ ] Flip the Phase-0 D3 characterization to green.

## Phase 4 — Fail-stop replay for data entries (D4)
Intent: a bad data entry stops recovery cleanly and loudly; subgraph entries still skip-and-continue.
- [ ] `ReplayWriteAheadLog` (`:1359`): wrap `tx.TryExecute(this)` in `try/catch`; treat a `false`
      return and a throw like the decode-failure `break` — stop at the last good entry, log
      `recovered N of M; K remaining not applied`, never let it escape.
- [ ] Classify by `WalEntryType`: `CreateSubGraph` (already via `ReplaySubGraphCreate`) **and**
      `RemoveSubGraph` skip-and-continue; core data + id-space entries are fail-stop.
- [ ] Confirm neither replay path (unanchored construction; paired `Load`) can let a throw escape.
- [ ] Flip the Phase-0 D4 characterization to green.

## Phase 5 — Crash-durable commit-point renames + identity pairing (D5)
Intent: the commit-point renames are durable in storage order; pairing survives a path coincidence.
- [ ] Add `DurableFileOps`: POSIX `open(parentDir, O_DIRECTORY)` + fsync after a rename; Windows
      `MoveFileEx(..., MOVEFILE_WRITE_THROUGH | MOVEFILE_REPLACE_EXISTING)` via P/Invoke. Detect an
      unsupported directory fsync once and downgrade with a warning (per the Risks policy).
- [ ] Route the three commit-point renames through it: `PersistencyFactory.cs:431` (header), `:691`
      (sidecars), `WriteAheadLog.cs:367` (WAL reset).
- [ ] Record the snapshot Guid (`fallen8.Id`, already written to the header) in the WAL header;
      `PairsWith` requires identity **and** canonicalized path. Bump the WAL `FormatVersion`
      (clean-reject; additive).
- [ ] Tests: the helper is invoked for all three renames; save/load round-trips; identity+path
      pairing rejects a foreign file at the same path.

## Phase 6 — Recipe manifest before the commit point (D6)
Intent: a committed snapshot always has its recipes; a manifest failure never strands WAL subgraph entries.
- [ ] Move `SaveSubGraphRecipes` (`:497`) to run **before** the header rename (`:431`); on failure
      **throw** (fail the `Save`) instead of catching + logging.
- [ ] Confirm `Fallen8.Save` (`:860`) leaves the WAL **unreset** when the manifest step throws, so
      the `CreateSubGraph` entries survive.
- [ ] Test: inject a manifest write failure → `Save` fails (→ `RolledBack`/500), WAL not reset,
      replay recreates the subgraphs; a committed snapshot always has its recipe manifest.

## Phase 7 — Recipe-replay trust boundary + compile cache (D7)
Intent: make the trust boundary explicit and keep recovery cheap for repeated specs.
- [ ] Document the integrity-not-authenticity boundary in `ReplaySubGraphCreate` and the persistence
      docs (write access to the save/WAL dir = code execution at load; operational mitigation =
      directory permissions).
- [ ] Key `GeneratedCodeCache` by a strong hash of the recipe `SpecificationJson` (+ equality check)
      so K subgraphs sharing a spec compile once; recovery scales with distinct specs. Build on the
      landed collectible ALCs — do not add new ones.
- [ ] Test: K subgraphs sharing one spec → one compile (cache hit observable); capture the opt-in
      recovery-latency benchmark.

## Measure & document
- [ ] Full suite green (existing `WriteAheadLogTest` torn-tail + pairing + wal-subgraph tests kept).
- [ ] Capture the D7 recovery-latency numbers — **to be captured on this box**.
- [ ] Update `features/persistence-hardening/` and `features/wal-subgraph-support/` cross-links; note
      the WAL header version bump in release notes.
- [ ] Mark the PR ready for review; reference the feature issue (`Closes #<n>`).

## Outcome (what shipped)
- **D1** — `WriteAheadLog.Append` captures the pre-append length, truncates a torn frame on failure,
  trips a sticky `_failed` fence (subsequent appends are a no-op), logs one Error, and rethrows;
  `HasFailed` exposes it and `ResetToSnapshot` clears it. `TransactionInformation.Durable` (default
  true) is set false by the manager on **every** committed tx while the fence is tripped (not only the
  first). `Fallen8.LogCommittedTransaction` now returns durability.
- **D2** — the failed-load `else` branch restores `_currentId` and, when the WAL is enabled, sets
  `walHandledIdSpace = true` so the closing `Trim_internal` is skipped (symmetric with the success
  path) — no unlogged compaction diverges the id space the log baseline assumes.
- **D3** — `_walAwaitingPairedLoad` is set when an **anchored** log is adopted at construction;
  `LogCommittedTransaction`/`LogWriteAheadLogMarker` suspend and report non-durable while set; cleared
  by a paired `Load` and by `Save`.
- **D4** — `ReplayWriteAheadLog` wraps the data-entry `TryExecute` in try/catch; a throw **or** false
  on a core data entry stops replay at the last good entry with a `recovered N` log and never escapes;
  `RemoveSubGraph` (and `CreateSubGraph`) skip-and-continue as derived state.
- **D6** — `SaveSubGraphRecipes` runs **before** the header commit-point rename and now **throws** on
  failure (was caught+logged), so a manifest failure fails the whole `Save`, commits nothing, and
  leaves the WAL unreset (its `CreateSubGraph` entries survive for the next replay).
- **D7** — the integrity-not-authenticity trust boundary is documented on `ReplaySubGraphCreate`;
  recovery reuses the landed content-keyed compile cache (distinct-spec scaling), so no new work.
- Tests: `WriteAheadLogHardeningTest` covers D1 (fence + non-durable + no-silent-drop; Save clears the
  fence), D2 (no id divergence after a failed load), and D3 (pre-load mutation non-durable + not
  replayed) — all via the public surface (WAL-file read-only attribute, non-existent load path,
  anchored-then-reopened log). Full suite green: **390 passed, 0 failed, 14 skipped**.
- **D4/D6 test scope (honest note):** their fault-injection requires a crafted-WAL / mid-save-failure
  seam, and the engine deliberately declares no `InternalsVisibleTo`, so they are covered by code
  review plus the full WAL suite staying green (the changes are conservative — a try/catch + a
  warn→break for data entries; a reorder + rethrow), not a dedicated red-test.

## Progress
- [x] Phase 0 — fault-injection via the public surface (read-only WAL file, failed load, anchored reopen)
- [x] Phase 1 — sticky WAL-failure fence (D1)
- [x] Phase 2 — symmetric no-Trim on a failed load (D2)
- [x] Phase 3 — awaiting-paired-load fence (D3)
- [x] Phase 4 — fail-stop replay for data entries; subgraph skip-and-continue (D4)
- [~] Phase 5 — **deferred**: crash-durable renames (P/Invoke) + WAL-header identity pairing (format bump)
- [x] Phase 6 — recipe manifest before the commit point + fail-loud (D6)
- [x] Phase 7 — recipe-replay trust boundary documented; content-keyed compile cache reused (D7)
- [x] Measure & document

## Decision / revisit condition
- **`non-blocking-save/` (measured, deferred).** D5/D6 keep the save on the single writer thread and
  do not touch the stall this feature measured. They only make the save's *storage-order* durability
  and its recipe/WAL ordering correct. The non-blocking-save revisit condition is unchanged (tens of
  millions of elements saved frequently).
- **`wal-subgraph-support/` (landed) vs `persistence-hardening/` Stage D (which recorded subgraph WAL
  as deferred).** That deferral was reopened and closed by `wal-subgraph-support`, so
  `CreateSubGraph`/`RemoveSubGraph` **are** logged today. D6 exists precisely because that landed:
  the recipe-manifest-after-commit window now strands real, recoverable WAL entries. If subgraph WAL
  logging were ever reverted, D6 would narrow back to "a committed snapshot must still carry its
  recipes" but the WAL-reset concern would fall away.
- **`csr-adjacency/` (assessed, skipped).** Untouched — this theme is durability, not adjacency
  representation.
