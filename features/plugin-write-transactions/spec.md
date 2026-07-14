# Plugin Write Transactions — Specification

> **Status:** Planned (P2 architecture) — from the 2026-07 principal-architect & performance review.
> Plugins can read and derive but have no sanctioned way to *mutate* the graph; give them one without
> opening the frozen transaction set or breaking the single-writer / WAL invariants.

## 1. Problem / current state (verified)

The plugin surface is deliberately open for **reading and deriving** and closed for **writing**:

- The read/derive contracts are all public and third-party-implementable: `IPlugin`
  (`Plugin/IPlugin.cs:32`), `IPathTraverser` (`Algorithms/Path/IPathTraverser.cs:28`),
  `ISubGraphAlgorithm` (`Algorithms/SubGraph/ISubGraphAlgorithm.cs:90`), and `IIndex`
  (`Index/IIndex.cs:39`). A third-party assembly can ship a new path/subgraph algorithm or index.
- The mutation vocabulary is **frozen at the built-in set**. `ATransaction.TryExecute(Fallen8)` and
  `ATransaction.Rollback(Fallen8)` are `internal abstract` (`Transaction/ATransaction.cs:75-76`) and
  take the concrete `sealed class Fallen8` (`Fallen8.cs:48`). Both facts together mean a type in
  another assembly **cannot** implement `ATransaction`: it can neither override the internal abstract
  members nor name the sealed engine type they require. Every mutation therefore goes through one of
  the ~13 built-in transactions, and a plugin can only *read* (`IFallen8Read`) or enqueue one of
  those built-ins (`IFallen8Write.EnqueueTransaction`, `IFallen8Write.cs:45`). Even the built-in
  subgraph algorithm mutates only by enqueuing built-in `CreateVertices`/`CreateEdges`/`Remove`/`Trim`
  transactions on a fresh engine it constructs (`Algorithms/SubGraph/BreathFirstSearchSubgraphAlgorithm.cs:185`),
  never by defining a new transaction.

**Why the closure is partly load-bearing (and the brief's one over-statement, corrected).** The
brief says opening `ATransaction` "would break durability/replay." Verified against the code, the
failure mode is softer but still real: the WAL codec is a **closed switch over concrete built-in
types** — `WalTransactionCodec.TryGetEntryType` (`Persistency/WalTransactionCodec.cs:60-91`) with a
`default: return false`, called from `Fallen8.LogCommittedTransaction` (`Fallen8.cs:1326-1331`), which
simply **does not append** a transaction the switch does not recognise. So an out-of-set transaction
would not *crash* the WAL or replay — it would be **silently non-durable via the log** (recoverable
only through the next full snapshot, and only insofar as its effect is standard graph elements, which
the snapshot serialises regardless of the transaction that produced them). That soft-default is
precisely what makes a non-logged escape hatch (mode (a) below) safe to add today with **zero** WAL
changes.

Net: "plugin capability is crucial" is in tension with a write path that is closed to plugins. We
want to make the trade-off explicit and provide a sanctioned escape hatch — without opening
`ATransaction` and without weakening the single-writer or WAL guarantees.

## 2. Goals / non-goals

**Goals**

- A public, sanctioned way for a plugin (anything holding `IFallen8Write`) to run a *composed*
  mutation against the **live** graph, on the single writer thread, through a stable interface —
  without the engine having to add a new built-in transaction per plugin need.
- Two honest durability modes, chosen per transaction:
  - **(a) non-WAL-loggable** (default) — simple and safe; documented as *durable only via the next
    full snapshot*, exactly the same recoverability a delegate-only subgraph already has.
  - **(b) WAL-loggable** — opt-in, for a plugin that supplies a serialisable descriptor + a
    deterministic replay, registered with the codec. Harder; optional.
- Preserve the one invariant plugins must never break: **all mutation stays on the single writer
  thread**, and a rolled-back delegate body has **no observable effect** (honour
  `transaction-atomicity`'s "RolledBack ⇒ no effect").

**Non-goals**

- Opening `ATransaction` itself. `TryExecute`/`Rollback` stay `internal abstract`; the built-in
  transactions stay sealed/internal. The escape hatch is one new *built-in* transaction whose body is
  a plugin-supplied delegate — not a third-party `ATransaction` subclass.
- Letting a plugin mutate off the writer thread (no parallel writers, no async apply).
- Serialising an arbitrary closure into the WAL. A `delegate` is not serialisable; mode (b) logs a
  plugin-supplied **descriptor**, not the `Action` (see §3.4).
- Changing the WAL file envelope/framing or the snapshot format (mode (b) is purely additive, in the
  `wal-subgraph-support` style).
- Exposing the id-space lifecycle ops (`Save`/`Load`/`Trim`/`TabulaRasa`) inside a delegate body.

## 3. Design sketch

### 3.1 `IFallen8WriterContext` — the sanctioned mutation surface

A new **public** interface (`fallen-8-core`, `NoSQL.GraphDB.Core.Transaction`) that exposes the safe
subset of the engine's `*_internal` mutation methods behind a stable contract, so a plugin never
touches `Fallen8` directly. It forwards to the existing internals:

| Context method | Forwards to | Notes |
|---|---|---|
| `VertexModel CreateVertex(uint creationDate, string label, IDictionary<string,object> props = null)` | `Fallen8.CreateVertex_internal` (`Fallen8.cs:569`) | |
| `IReadOnlyList<VertexModel> CreateVertices(IEnumerable<VertexDefinition>)` | `CreateVertices_internal` (`:587`) | batch |
| `bool TryCreateEdge(out EdgeModel edge, int sourceId, string edgePropertyId, int targetId, uint creationDate, string label = null, IDictionary<string,object> props = null)` | `CreateEdge_internal` (`:939`) | `Try*` — returns `false` (no throw) when an endpoint is missing/removed, matching `CreateEdge_internal`'s null-resolution and the `transaction-failure-reasons` `NotFound` path |
| `void SetProperty(int graphElementId, string propertyId, object value)` | `SetProperty_internal` (`:1034`) | |
| `bool TryRemoveProperty(int graphElementId, string propertyId)` | `RemoveProperty_internal` (`:1044`) | |
| `bool TryRemoveGraphElement(int graphElementId)` | `TryRemoveGraphElement_private` (`:1054`) | see §3.3 rollback caveat |

Deliberately **absent**: `Save`/`Load`/`Trim`/`TabulaRasa`. These mutate the id space or the
persistence baseline and would break WAL/snapshot ordering if interleaved inside a delegate body;
they remain lifecycle transactions the caller enqueues on their own.

The context is a thin wrapper over the `Fallen8 f8` the transaction received, plus an **undo journal**
(§3.3). It is valid **only for the duration of the delegate body** — it is invalidated (a flag
checked by every method, throwing `InvalidOperationException` if used afterwards) once the body
returns, so a plugin cannot stash it and mutate off the writer thread later.

### 3.2 `DelegateTransaction` — the escape hatch

A new **public sealed** `DelegateTransaction : ATransaction` (in `fallen-8-core`, so it legally
implements the internal abstract members):

```
public sealed class DelegateTransaction : ATransaction {
    public DelegateTransaction(Action<IFallen8WriterContext> body, string name = null);
    // TryExecute(Fallen8 f8):  create context (bound to f8 + a fresh journal),
    //                          run body inside try; on throw -> record reason, return false;
    //                          invalidate the context; return true.
    // Rollback(Fallen8 f8):    replay the journal in reverse (see §3.3).
}
```

A plugin holding `IFallen8Write` uses it exactly like any built-in:
`f8.EnqueueTransaction(new DelegateTransaction(ctx => { ... }))` then `WaitUntilFinished()`. Because
`TryExecute` runs on the `TransactionManager` worker (`Transaction/TransactionManager.cs` — the single
`Fallen8-Transaction-Writer` thread, run inline via `RunSynchronously`), **the body runs on the writer
thread** by construction; the plugin gets no other way in.

`DelegateTransaction` is **not** added to `WalTransactionCodec.TryGetEntryType`, so it falls to the
existing `default: return false` and is silently not WAL-logged — this is durability **mode (a)**, and
it needs no codec change (see §1). Its effect (standard vertices/edges/properties) is captured by the
next snapshot like any other element.

### 3.3 Atomicity: "RolledBack ⇒ no effect" via an undo journal

The engine already rolls a multi-step mutation back by *compensation*, not by snapshot swap:
`CreateEdgesTransaction.Rollback` removes each added edge via `TryRemoveGraphElement_private`
(`Transaction/CreateEdgesTransaction.cs:42-48`). `DelegateTransaction` uses the same mechanism: every
context call records a compensating action on the journal, and `Rollback` replays them in reverse.
`TransactionManager.ProcessTransaction` already calls `RollbackSafely` on **both** the clean-`false`
(throwing-body) and the internal-fault paths, so a single journal covers both.

- `CreateVertex`/`CreateVertices`/`TryCreateEdge` → compensation = `TryRemoveGraphElement_private(id)`
  of the created element (the proven pattern).
- `SetProperty`/`TryRemoveProperty` → capture the prior value (present + value, or absent) **before**
  the change; compensation restores it.
- **`TryRemoveGraphElement` — honest limitation.** Undoing a removal means resurrecting the element
  *and* re-attaching its adjacency; the soft-delete model has no generic "un-remove", so this cannot
  be cleanly compensated today. First cut: a `DelegateTransaction` whose body only creates elements
  and sets/removes **properties** has a **full** "no-effect" rollback guarantee; if the body also
  removes graph elements, the guarantee degrades to "everything except the removals is reverted."
  This is documented on the API and asserted by a test, and callers who need reversible bulk removal
  keep using the dedicated `RemoveGraphElements` transaction (which has its own tested rollback). A
  future increment can lift this once the store supports element resurrection.

### 3.4 Durability mode (b): opt-in WAL-loggable plugin transactions

For a plugin that needs durability *between* snapshots, generalise the exact additive pattern
`wal-subgraph-support` used for the subgraph recipe (a serialisable descriptor + a replay that
reruns the operation in commit order):

- One additive `WalEntryType.PluginDelegate` (next free byte after `RemoveSubGraph = 13`, i.e. `14`;
  values are fixed and never renumbered — see `Persistency/WalEntryType.cs`). A single new entry type
  covers *all* plugin transactions; they are distinguished by a **stable plugin key** in the payload,
  so third parties never collide over the `byte` type space.
- A registration API (e.g. `WriteAheadLogOptions.RegisterPluginTransaction(key, serialize, replay)` or
  a small `IWalPluginCodec { string Key; byte[] Serialize(descriptor); void Replay(IFallen8WriterContext, byte[]); }`
  the engine collects at construction). The `DelegateTransaction` opts in by carrying a serialisable
  **descriptor** + the registered key (an arbitrary `Action` is *never* logged — you cannot serialise
  a closure).
- `TryGetEntryType` returns `PluginDelegate` for an opt-in `DelegateTransaction`; `SerializeEntry`
  writes `[key][plugin payload]`; replay looks the key up in the registry and, **if the key is not
  registered at load time, skips the entry with a loud warning and continues** — identical to the
  no-compiler subgraph case (`wal-subgraph-support` §2.3). Replay runs under `_walSuspended` (so the
  re-applied ops are not re-logged) and re-executes the descriptor via a fresh `IFallen8WriterContext`
  against the padded, id-deterministic store in commit order — the same determinism contract the
  built-in replay relies on. The plugin's replay **must be deterministic** w.r.t. ids (re-issue the
  same context calls in the same order); non-determinism (wall-clock, RNG) is the plugin's
  responsibility, and mode (a) sidesteps it entirely.

Mode (b) is **optional** and can land after mode (a); mode (a) delivers the core capability.

## 4. Acceptance criteria

- A test plugin performs a composed mutation via `DelegateTransaction` enqueued on `IFallen8Write`;
  after `WaitUntilFinished()` the new elements are visible to lock-free readers (`GetAllVertices` /
  `TryGetGraphElement`) with correct snapshot semantics, and the body is proven to have run on the
  single writer thread (thread-identity / single-writer serialisation assertion).
- A **mode (a)** `DelegateTransaction` survives a full `Save`→`Load` (its elements are in the
  snapshot) but its effect is **absent** after a WAL-only replay with no intervening snapshot —
  pinned by a test and documented as the mode-(a) contract.
- A **mode (b)** registered `DelegateTransaction` replays correctly from the WAL (elements/ids/
  properties reconstruct identically, in commit order); and a `PluginDelegate` entry whose key is
  **not** registered at load time is skipped with a warning without halting recovery.
- A **throwing** delegate body (or one that returns via an internal fault) leaves **no observable
  effect** for the create/property surface (`RolledBack`, `FailureReason` set: a plugin-signalled
  `InvalidInput`/`NotFound`, or `InternalError` for an escaped exception per `transaction-failure-reasons`),
  and the single writer survives.
- Full suite green; `WalTransactionCodec`'s classification of the existing built-ins is unchanged
  (mode (b) is purely additive; WAL format version unchanged).

## 5. Risks

- **Rollback of removals is not clean** (§3.3). Mitigation: scope the strong guarantee to
  create/property bodies, document + test the remove caveat, steer bulk removal to the dedicated
  transaction.
- **Context lifetime abuse.** A plugin could try to capture the `IFallen8WriterContext` and use it
  off-thread. Mitigation: hard-invalidate after the body; every method throws if used afterwards.
- **Long delegate body blocks the writer.** A slow body stalls all other mutations for its duration
  (same class of stall measured in `non-blocking-save`). Mitigation: documented; the delegate body is
  the plugin's own CPU work on the single writer, no different from a large built-in batch.
- **Mode (b) determinism.** A non-deterministic plugin replay diverges from the pre-crash state.
  Mitigation: state the determinism contract; skip-with-warning on an unregistered key; keep mode (a)
  as the safe default.
- **Surface creep.** The context could accrete every internal method. Mitigation: keep it to the
  create/property/remove subset; lifecycle ops stay out by design.

## 6. Keep (do not regress)

- **`ATransaction.TryExecute`/`Rollback` stay `internal abstract`; built-in transactions stay
  sealed/internal.** The escape hatch is *one new built-in* whose body is a delegate — the vocabulary
  is not opened to third-party `ATransaction` subclasses.
- **Single-writer invariant.** Every mutation, including a delegate body, runs on the one
  `Fallen8-Transaction-Writer` thread; lock-free readers keep reading the volatile snapshot.
- **WAL guarantees.** Determinism (id baseline + padding + commit order), clean-reject, torn-tail
  safety, and the "logging failure never faults the writer" containment are untouched; mode (b) only
  adds an entry type, in the `wal-subgraph-support` additive style.
- **The open read/derive plugin story** (`IPlugin`/`IPathTraverser`/`ISubGraphAlgorithm`/`IIndex`)
  is unchanged.
- **`transaction-failure-reasons`.** `DelegateTransaction` sets `FailureReason` through the existing
  channel (plugin-signalled reason, or `InternalError` on an escaped exception via the manager).
