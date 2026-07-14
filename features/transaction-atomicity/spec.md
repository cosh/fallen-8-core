# Transaction Atomicity — Specification

> **Status:** Implemented — from the 2026-07 principal-architect & performance review. **P0 correctness.**
> Enforce the engine-wide invariant that a transaction whose terminal state is `RolledBack` has ZERO
> observable effect. It is violated today by the batch create/remove/property transactions, which can
> silently corrupt the `id == index` master-store invariant and leave the write-ahead log diverging
> from state clients already observed.

## 1. Problem / current state

The master store's core invariant is `id == index`: an element's `.Id` equals its slot in the
segmented store, so `TryGetVertex(id)` / `TryGetEdge(id)` resolve by direct index. It is established
by appending an element at `snap.Count` and then publishing a larger `Count` (`AppendGraphElement`,
`fallen-8-core/Fallen8.cs:350`; batch `AppendGraphElements`, `Fallen8.cs:379`). Every batch mutation
transaction can break either that invariant or the "rolled-back = no effect" contract:

**Create (id-space corruption).**
`CreateVertices_internal` (`Fallen8.cs:587`) increments `_currentId` and `VertexCount` **inside** the
per-definition loop (`Fallen8.cs:601`, `Fallen8.cs:604`) but calls `AppendGraphElements(newVertices)`
only **after** the loop (`Fallen8.cs:608`). A definition that throws mid-loop (a `null` entry — a JSON
array element can be `null`, so `aVertexDef.CreationDate` NREs at `Fallen8.cs:596`; or OOM) advances
the counters for the already-processed definitions while appending nothing, so `_currentId >
snap.Count` permanently. Thereafter a newly created element's `.Id` (drawn from the inflated
`_currentId`) no longer equals its slot index — `TryGetVertex(id)` returns the WRONG element and later
edges wire to the wrong vertex. `CreateVerticesTransaction.TryExecute` (`CreateVerticesTransaction.cs:50`)
now wraps the call in a `try/catch` that swallows the exception and returns `false`, so
`_verticesCreated` stays the empty init list and `Rollback` (`CreateVerticesTransaction.cs:42`)
compensates nothing — and because the throw never reaches the worker, the failure surfaces as a
benign-looking clean rollback (`FailureReason = None`, `Error = null`) even though the id space is now
corrupt.

`CreateEdges_internal` (`Fallen8.cs:976`) was hardened by `transaction-failure-reasons`: it now
pre-validates every endpoint up front (`Fallen8.cs:988`) and rolls back cleanly with `NotFound` if any
referenced vertex is missing/removed, and a `null` edge definition throws in that pre-pass before
anything is wired. But the **wiring loop still mutates before the append**: per edge it bumps
`_currentId` (`Fallen8.cs:1012`) and `EdgeCount` (`Fallen8.cs:1021`) and wires adjacency
(`sourceVertex.AddOutEdge` / `targetVertex.AddIncomingEdge`, `Fallen8.cs:1015`, `Fallen8.cs:1018`)
**before** the batch `AppendGraphElements(newEdges)` (`Fallen8.cs:1027`). A throw in that loop (OOM)
leaves the counters advanced, adjacency partly wired, and nothing appended — same id-space corruption
as the vertex path, plus dangling adjacency. `CreateEdgesTransaction.TryExecute`
(`CreateEdgesTransaction.cs:50`) no longer catches, so the throw reaches the worker as
`InternalError`, but `_edgesAdded` is still the empty init list, so `Rollback`
(`CreateEdgesTransaction.cs:42`) removes nothing.

**Visibility-order inconsistency.** The single-edge path is **store-then-adjacency**: append at
`Fallen8.cs:958`, then adjacency at `Fallen8.cs:964`/`Fallen8.cs:967`, so a reader can never traverse
to an edge `TryGetEdge` cannot yet resolve. The batch path is **adjacency-then-store**
(`Fallen8.cs:1015`/`1018` before the append at `Fallen8.cs:1027`), so mid-loop a reader following
`vertex.GetOutgoingEdges()` can reach an edge whose `.Id >= snap.Count` — `TryGetEdge(id)` returns
`false` for it.

**`CreateEdgeTransaction.Rollback` is a literal `//TODO`** (`CreateEdgeTransaction.cs:53`);
`AddPropertyTransaction.Rollback` is too (`AddPropertyTransaction.cs:53`). (Both are single-mutation
transactions, so a throw leaves nothing to compensate — harmless today, but they must not stay empty
once the invariant is a contract.)

**Remove / property (partial commit reported as RolledBack).**
`RemoveGraphElementsTransaction.TryExecute` (`RemoveGraphElementsTransaction.cs:50`) loops
`TryRemoveGraphElement_private` with NO batch-level `try/catch`; its `Rollback`
(`RemoveGraphElementsTransaction.cs:45`) is a NOP that defers to the per-element restore. But an
out-of-range id resolves via `GetGraphElementForMutation`, which throws
`ArgumentOutOfRangeException` (`Fallen8.cs:422`) at `Fallen8.cs:1056` — **before** the per-element
`try` at `Fallen8.cs:1067` — so that throw escapes the whole batch. Removals already applied to
earlier ids in the batch stay committed while the transaction reports `RolledBack`.

`AddPropertiesTransaction` (`AddPropertiesTransaction.cs`) is identical: no `try/catch`
(`AddPropertiesTransaction.cs:44`), NOP `Rollback` (`AddPropertiesTransaction.cs:39`). Its throw
trigger is **routine, not exotic**: `AGraphElementModel.SetProperty` throws `ArgumentException` when a
key already exists with a *different* value (`AGraphElementModel.cs:363`) — i.e. a normal batch
property **update** applies the earlier sets, then throws on the conflicting one and reports
`RolledBack` / `InternalError`.

**WAL divergence.** `TransactionManager.ProcessTransaction` appends to the write-ahead log only on the
success branch (`LogCommittedTransactionSafely`, `TransactionManager.cs:187`). That is correct in
principle — but because the batches above leave partial mutations committed under a `RolledBack`
result, those mutations are invisible to the log. After a crash, replay reproduces a state that omits
them, so recovery **diverges from what clients already observed**. The WAL (Stage D of
`persistence-hardening/`) and `wal-failure-hardening/` both rest on this invariant; today it does not
hold.

## 2. Goals / non-goals

**Goals.**
- Make "`RolledBack` ⇒ zero observable effect" a real, enforced contract for **every** transaction,
  documented on `ATransaction`.
- Batch **creates** are construct-then-commit: no counter, adjacency, or store mutation happens until
  every model in the batch has been built successfully.
- Batch **remove/property** are all-or-nothing: the whole batch is validated before any element is
  mutated, and if a later step still throws, `Rollback` genuinely undoes the applied progress.
- Preserve `id == index` under every failure path; `TryGetVertex`/`TryGetEdge` stay correct after a
  rolled-back batch.
- Standardise create-edge visibility order to **store-then-adjacency** (single and batch) so a reader
  can never traverse to an edge `TryGetEdge` cannot resolve.
- Route pre-validation failures through the existing `TransactionFailureReason` channel
  (`InvalidInput` / `NotFound` / `Conflict`) so the REST layer maps them (see
  `transaction-failure-reasons/`).
- Fill `CreateEdgeTransaction.Rollback` (and `AddPropertyTransaction.Rollback`).

**Non-goals.**
- Changing the transaction/concurrency model: mutations stay on the single writer thread; reads stay
  lock-free over the volatile snapshot. (No new locks, no MVCC.)
- Moving any work off the worker thread — off-worker save stays deferred (`non-blocking-save/`).
- Re-deciding the HTTP status boundary for out-of-range remove/property ids. `transaction-failure-reasons/`
  deliberately keeps out-of-range → `InternalError` → 500; this feature does **not** change that
  mapping. It only guarantees the batch is atomic (the earlier ids are not left committed). Whether a
  batch with an out-of-range id should instead be a clean `InvalidInput`/`NotFound` is discussed in §3
  but is optional and must not regress the load-bearing B6/500 tests.
- Changing what the WAL logs or when (`persistence-hardening/` Stage D). This feature makes the
  existing "log on success only" behaviour correct; it does not touch the log format or append point.
- Subgraph/index/service transactions (they do not touch the base `id == index` id space here).

## 3. Design sketch

**Invariant, stated in one place.** Document on `ATransaction` (near the `Rollback`/`TryExecute`
contract, `ATransaction.cs:75`): *a transaction that does not commit leaves the engine byte-for-byte as
it was before `TryExecute` ran — either `TryExecute` mutates nothing and returns `false`, or every
mutation it made is undone by `Rollback`.* `TryExecute` returning `false` (or throwing) must therefore
be safe to treat as "nothing happened."

**Batch creates → construct-then-commit** (`CreateVertices_internal`, `CreateEdges_internal`):
1. **Validate the whole batch first, without mutating.** A `null` definition (or other structurally
   invalid input) sets `FailureReason = InvalidInput` and returns `false` cleanly — no throw, so
   `CreateVerticesTransaction.TryExecute`'s swallowing `try/catch` (`CreateVerticesTransaction.cs:50`)
   is removed; a genuine OOM still propagates to the worker as `InternalError`. Edge endpoint
   pre-validation (`NotFound`) already exists (`Fallen8.cs:988`) — keep it, do not re-add it.
2. **Build all models against a LOCAL id counter** seeded from `_currentId` (`var nextId = _currentId;`
   assign each model `nextId++`), accumulating into the list. Do not touch `_currentId`, the counters,
   the store, or adjacency yet. A throw here leaves the engine untouched — no compensation needed.
3. **Commit atomically:** `AppendGraphElements(list)` (one `Count` bump), then set
   `_currentId = nextId` and `VertexCount += n` / `EdgeCount += n`. For edges, wire adjacency
   **after** the append (store-then-adjacency, matching the single-edge path) so no traversal can
   reach an unresolvable edge.
4. **Residual window (edges only):** adjacency wiring is after the append, so an OOM there could leave
   appended edges partly wired. Capture the created edges on `CreateEdgesTransaction` so its `Rollback`
   removes them via `TryRemoveGraphElement_private` (which cleanly detaches any partial adjacency and
   soft-deletes) — the created-model list must be assigned as edges are appended, not only as the
   method's return value, so a throw mid-wiring still has something to roll back.

**Batch remove/property → validate-then-apply with tracked undo.**
- **Pre-validate the whole batch** before mutating anything:
  - remove: every id is in range (`0 <= id < snap.Count`) — reject out-of-range up front rather than
    letting `GetGraphElementForMutation` throw mid-batch. (An in-range id whose slot is null/already
    removed stays a clean no-op, as today.)
  - properties: every `SetProperty` in the batch is conflict-free (no existing key set to a different
    value) — mirror the `AGraphElementModel.cs:363` check as a non-throwing pre-check
    (e.g. a `TrySetProperty(out bool wouldConflict)` or a read-only "would this conflict?" probe) so a
    conflicting update is `Conflict` (or `InvalidInput`), not a mid-batch `ArgumentException`.
- **Track applied progress and undo on a later throw.** Even after pre-validation, a step can still
  throw (OOM). Wrap the apply loop so `Rollback` can reverse exactly what was applied: for removes,
  re-clear the removed flag / restore adjacency for the ids already removed (the per-element restore in
  `TryRemoveGraphElement_private` handles a single faulting element; the batch transaction must undo
  the *earlier, already-committed* ones); for property sets, record each element's prior value (or
  prior absence) and restore it.
- Set `FailureReason` (`InvalidInput`/`NotFound`/`Conflict`) on the pre-validation reject so the
  controller maps it; a genuine post-validation fault stays `InternalError`.

**Fill the empty rollbacks.** `CreateEdgeTransaction.Rollback` (`CreateEdgeTransaction.cs:53`) removes
the created edge if one was created; `AddPropertyTransaction.Rollback` (`AddPropertyTransaction.cs:53`)
restores the single element's prior value/absence. (Single-mutation transactions are already
all-or-nothing because the one mutation either fully happens or throws before mutating; filling these
makes the invariant uniform and guards future multi-step edits.)

**Consistency with the model.** All of the above runs on the single writer thread; reads remain
lock-free. Construct-then-commit means the *common* create path needs no rollback at all (nothing is
mutated until the atomic append), which is both simpler and faster than compensating after the fact.

## 4. Acceptance criteria

For each injected mid-batch failure — (a) a `null` entry in a `CreateVertices`/`CreateEdges` batch,
(b) a conflicting property update in an `AddProperties` batch, (c) an out-of-range id in a
`RemoveGraphElements` batch, (d) an OOM-style throw after the first element is processed (simulated) —
assert:

1. **State is exactly as before the transaction:** vertex/edge counts, `GetAllVertices`/`GetAllEdges`
   contents, every element's properties and adjacency are identical to a snapshot taken immediately
   before enqueuing the failing transaction.
2. **`id == index` still holds:** after the failure, create a fresh vertex and assert its `.Id` equals
   its slot — `TryGetVertex(newId)` returns exactly that vertex — and that `TryGetVertex`/`TryGetEdge`
   for every pre-existing id still return the correct element. (Directly pins the `_currentId >
   snap.Count` corruption from §1.)
3. **The rolled-back transaction is reported honestly:** terminal state `RolledBack` with the right
   `TransactionFailureReason` (`InvalidInput` for the null definition, `Conflict`/`InvalidInput` for
   the conflicting update, and the §2-non-goal-consistent reason for the out-of-range remove), and the
   worker survives and keeps processing subsequent transactions.
4. **WAL (when enabled) contains nothing for the rolled-back tx:** with a `WriteAheadLogOptions` engine,
   the log after the failed batch has no entry for it (following `WriteAheadLogTest.cs` patterns).
5. **Crash + replay reproduces the pre-tx state:** enable the WAL, apply a committed baseline, run the
   failing batch, then load a fresh engine from the snapshot + WAL and assert the recovered graph
   equals the pre-failing-tx state (counts, elements, ids, edges/adjacency, properties).
6. **No happy-path regression:** a valid batch create/remove/property still commits, is logged, and
   `WaitUntilFinished()` observes `Finished`; the full suite stays green.

## 5. Risks

- **Property-update pre-check semantics.** Adding a non-throwing conflict probe must exactly match the
  existing `SetProperty` equality (`ValueEquals`, `AGraphElementModel.cs:361`) so it does not reclassify
  a genuine no-op (same value) as a conflict, and must be evaluated on the live copy-on-write store
  under the single writer to avoid a check/apply skew.
- **Remove-batch undo fidelity.** Reversing already-applied cascaded edge removals (a removed vertex
  detaches and soft-deletes its edges) is the subtlest undo. It must restore adjacency AND the removed
  flags AND the counters exactly; the existing per-element restore path
  (`Fallen8.cs:1167`) is the reference for the inverse operations. Cover self-loops and shared
  endpoints.
- **Behaviour change for out-of-range remove batches.** Today the earlier ids stay committed; after the
  fix they do not. Any test asserting the old partial-commit behaviour must be migrated to the
  all-or-nothing contract (this is the point of the feature). Keep the out-of-range → 500 mapping
  (B6 / `transaction-failure-reasons`) unless the optional `InvalidInput` reclassification in §3 is
  taken deliberately.
- **Performance.** Construct-then-commit adds one pre-validation pass over the batch. It is O(n) and
  cheap relative to model construction/interning; measure that batch-create/remove throughput does not
  regress (Phase 0 benchmark).

## 6. Keep (do not regress)

- **Single-writer + lock-free reads over the volatile `_snapshot`.** No new locks; the fix is a
  reordering + validation change on the writer, not a concurrency-model change.
- **`AppendGraphElements` atomic publication** (all slots written before the single `Count` bump,
  `Fallen8.cs:379`) and the single-edge **store-then-adjacency** order (`Fallen8.cs:958`→`964`).
- **`TryRemoveGraphElement_private`'s per-element self-restore + rethrow** (`Fallen8.cs:1067`–`1271`):
  a single faulting removal already restores itself; keep it as the inverse-operation reference and
  the single-remove atomicity guarantee.
- **`transaction-failure-reasons/` endpoint pre-validation** (`Fallen8.cs:988`, `NotFound`) and the
  reason channel on `TransactionInformation` — build on them, do not re-add or re-litigate.
- **The worker-survives-a-faulting-transaction guard** (`TransactionManager.cs:98`, `:144`), the
  happens-before that publishes terminal state/`Error`/`FailureReason` to a waited-on caller, M3 input
  release, and `TriggersAutoTrim` — all unchanged.
- **WAL "log on success only"** (`TransactionManager.cs:187`) and the opt-in WAL from
  `persistence-hardening/` Stage D — unchanged; this feature only makes them correct by construction.
