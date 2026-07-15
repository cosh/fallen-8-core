# Plugin Write Transactions — Plan

Companion to [spec.md](./spec.md). Deliver the core capability (mode (a)) first with a full
"no-effect" rollback guarantee; the WAL-loggable path (mode (b)) is an optional follow-on. Every
guardrail invariant is pinned by a test before the feature code lands.

GitHub issue: to be opened (label: `feature`). Branch: `feature/plugin-write-transactions`.

## Phase 0 — Baseline & guardrails

Intent: pin the invariants this feature must not break, and characterise the current closure, so any
regression fails loudly.

- [ ] Characterization test `PluginWriteBaselineTest`: assert the single-writer invariant is
      observable — a batch of enqueued transactions never interleaves (e.g. a create body captures
      `Thread.CurrentThread` and all bodies see the same writer thread), using `TestLoggerFactory.Create()`.
- [ ] Pin the WAL codec's current classification: a test asserting the exact set of types
      `WalTransactionCodec.TryGetEntryType` returns `true` for (the 11 data/lifecycle + 2 subgraph
      entries), so mode (b)'s additive change is a deliberate, reviewed diff and no built-in
      classification silently changes.
- [ ] Pin snapshot visibility semantics for a freshly created element (enqueue → `WaitUntilFinished`
      → visible to `GetAllVertices`/`TryGetGraphElement`), the baseline mode (a) must reproduce.
- [ ] Optional micro-benchmark `PluginWriteBenchmark` (`[TestCategory("Benchmark")]` + `[Ignore]`,
      per repo convention): dispatch cost of a `DelegateTransaction` doing N creates vs. an equivalent
      built-in `CreateVertices` batch. Numbers to be captured on this box.

## Phase 1 — `IFallen8WriterContext` + `DelegateTransaction` (mode (a), non-loggable)

Intent: the sanctioned write path, on the writer thread, durable via the next snapshot only.

- [ ] Add public `IFallen8WriterContext` (`fallen-8-core/Transaction/IFallen8WriterContext.cs`, MIT
      header) exposing the §3.1 surface (`CreateVertex`/`CreateVertices`/`TryCreateEdge`/`SetProperty`/
      `TryRemoveProperty`/`TryRemoveGraphElement`) — `Try*(out,…):bool` for the not-found cases.
- [ ] Add an internal implementation bound to a `Fallen8` that forwards to `CreateVertex_internal`
      (`Fallen8.cs:569`), `CreateVertices_internal` (`:587`), `CreateEdge_internal` (`:939`),
      `SetProperty_internal` (`:1034`), `RemoveProperty_internal` (`:1044`),
      `TryRemoveGraphElement_private` (`:1054`). Include an `Invalidate()` + a guard that throws
      `InvalidOperationException` if any method is called after the body returns.
- [ ] Add public sealed `DelegateTransaction : ATransaction`
      (`fallen-8-core/Transaction/DelegateTransaction.cs`): ctor `(Action<IFallen8WriterContext> body,
      string name = null)`; `TryExecute` builds the context, runs the body in `try`, invalidates the
      context in `finally`, returns `true`; on an expected plugin-signalled failure returns `false`.
      `Cleanup`/`ReleaseAfterCompletion` drop the delegate + journal.
- [ ] Confirm `DelegateTransaction` is intentionally **absent** from `WalTransactionCodec.TryGetEntryType`
      (falls to `default: return false`), so it is not WAL-logged — no codec change in this phase.
- [ ] Test: a plugin-shaped caller enqueues a `DelegateTransaction` via `IFallen8Write`; after
      `WaitUntilFinished` the elements are visible to lock-free readers with correct snapshot
      semantics; assert the body ran on the single writer thread.

## Phase 2 — Atomicity: "RolledBack ⇒ no effect"

Intent: a rolled-back delegate body leaves no observable effect for the create/property surface, and
faults route through the existing failure-reason channel.

- [ ] Add the undo journal to the context: each `CreateVertex`/`CreateVertices`/`TryCreateEdge`
      records a `TryRemoveGraphElement_private(id)` compensation (the `CreateEdgesTransaction.Rollback`
      pattern, `Transaction/CreateEdgesTransaction.cs:42-48`); each `SetProperty`/`TryRemoveProperty`
      captures the prior value first and records a restore.
- [ ] `DelegateTransaction.Rollback(Fallen8)` replays the journal in reverse. Verify
      `TransactionManager.ProcessTransaction` drives `Rollback` on both the clean-`false` and the
      exception paths (it already does — `RollbackSafely`).
- [ ] Wire `FailureReason` (`Transaction/TransactionFailureReason.cs`): a plugin can signal
      `InvalidInput`/`NotFound`; an escaped exception is classified `InternalError` by the manager
      (unchanged). No throw is allowed to fault the writer.
- [ ] Document + test the **remove** caveat (§3.3): a create/property-only body has a full no-effect
      rollback; a body that also removes elements reverts everything except the removals.
- [ ] Tests: a throwing body rolls back cleanly (no elements, no property changes visible; writer
      survives; `RolledBack` + `FailureReason` set); a plugin-signalled `false` does the same.

## Phase 3 — Mode (b): opt-in WAL-loggable plugin transactions (optional)

Intent: durability between snapshots for a plugin that supplies a serialisable descriptor + a
deterministic replay — the `wal-subgraph-support` additive pattern, generalised.

- [ ] Add `WalEntryType.PluginDelegate = 14` (additive, never renumbered — `Persistency/WalEntryType.cs`).
- [ ] Add a registration path (`WriteAheadLogOptions.RegisterPluginTransaction(key, serialize, replay)`
      or an `IWalPluginCodec` collected at construction) keyed by a stable plugin **string**, so
      third parties never collide on the type byte.
- [ ] `DelegateTransaction` opt-in: carry a serialisable **descriptor** + the registered key (never
      the `Action`). `WalTransactionCodec.TryGetEntryType` → `PluginDelegate` for an opt-in instance;
      `SerializeEntry` writes `[key][payload]`; `Deserialize`/replay looks the key up.
- [ ] Replay in `Fallen8.ReplayWriteAheadLog` (`Fallen8.cs:1359`): under `_walSuspended`, re-run the
      descriptor via a fresh `IFallen8WriterContext` in commit order; **skip with a loud warning**
      when the key is not registered at load time (mirror the no-compiler subgraph case).
- [ ] Tests: a registered plugin tx replays identically (elements/ids/properties, commit order); an
      unregistered-key entry is skipped with a warning and recovery continues; torn-tail safety
      still holds (inherited from the framing — no new framing).

## Measure & document

Intent: prove the modes and record the contract.

- [ ] Run `PluginWriteBenchmark`; record delegate-dispatch overhead vs. the built-in batch —
      to be captured on this box.
- [ ] Add `features/plugin-write-transactions/README.md` with a minimal plugin example for each mode
      and the explicit durability statement: mode (a) = *durable via the next snapshot only*; mode (b)
      = *durable in the WAL when registered*.
- [ ] Cross-link `wal-subgraph-support` (the additive-codec template), `transaction-atomicity`
      (the "RolledBack ⇒ no effect" contract this honours), and `subgraph` (the in-engine
      mutation-via-built-in-transaction precedent).

## Progress

- [x] Phase 0 — `PluginWriteTransactionsTest` pins single-writer execution, WAL-classification (mode a
      not logged → absent after WAL-only replay), and snapshot visibility. (No opt-in benchmark added;
      the capability is functional, not a perf lever.)
- [x] Phase 1 — `IFallen8WriterContext` + `DelegateTransaction` (mode (a), non-loggable), runs on the
      single writer; snapshot-durable via the WAL codec's existing `default: return true` for an
      unrecognised type (zero codec change).
- [x] Phase 2 — undo journal (create → remove, property → `RestoreProperties_internal`); throwing body →
      `Error` + `InternalError` + rollback with no create/property effect; the remove caveat is
      documented on the interface and steers reversible bulk removal to `RemoveGraphElementsTransaction`.
- [ ] Phase 3 — **DEFERRED (optional).** Mode (b) WAL-loggable plugin transactions
      (`WalEntryType.PluginDelegate` + a descriptor/replay registration API, `wal-subgraph-support`
      style). Purely additive; mode (a) delivers the core capability, so a `DelegateTransaction` is
      snapshot-durable only until this lands.
- [x] Measure & document — full suite green (448 passing).

## Decision / revisit condition

- **Opening `ATransaction` stays rejected.** This feature deliberately keeps `TryExecute`/`Rollback`
  `internal abstract` and adds capability through one new *built-in* delegate transaction instead of
  a third-party `ATransaction` subclass. Revisit only if a concrete first-party need arises for a new
  built-in transaction that *also* requires off-writer execution — which is the `non-blocking-save`
  theme, not this one, and that off-worker save was MEASURED and DEFERRED (reopen only for
  tens-of-millions-of-elements saved frequently; the delegate body here likewise stays on the writer
  thread per that same stop-at-safe-boundary guardrail).
- **Mode (b) generalises `wal-subgraph-support`.** It reuses that feature's additive-entry-type +
  serialisable-descriptor + skip-with-warning-on-missing-reconstructor pattern. If a future need
  arises to log a plugin transaction whose effect is *not* expressible as a deterministic replay,
  that reopens the mode-(b) design (mode (a) remains the safe default regardless).
- The clean rollback of **element removals** inside a delegate body is deferred (§3.3); it reopens if
  the store gains generic element resurrection.
