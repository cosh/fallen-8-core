# WAL Subgraph Support — Specification

> **Status:** Landed. Follow-up to `persistence-hardening` (the opt-in write-ahead log) and the
> `subgraph` feature. Closes the one gap the WAL codec called out explicitly:
> *"subgraph transactions are intentionally not logged"*.
>
> **Outcome.** Recipe-building moved into `CreateSubGraphTransaction` (so it is present when the WAL
> logs on the writer thread); two additive `WalEntryType` values (`CreateSubGraph = 12`,
> `RemoveSubGraph = 13`); the recipe round-trips as the same `CoreJsonContext.SubGraphRecipe` JSON the
> snapshot manifest uses; commit-order replay recreates subgraphs (nested resolved by name) and skips
> the delegate-only / no-compiler / torn cases with a warning. The unanchored log replays during
> construction, so an optional compiler was added to the WAL constructor for that path. Full suite
> **352 passed / 10 skipped**; WAL format version stays 1.

## 1. Problem / current state

The opt-in write-ahead log (`WriteAheadLog` + `WalTransactionCodec`) makes committed mutations
durable between full snapshots by appending a framed, CRC-protected entry per committed transaction
and replaying them after the paired snapshot on recovery. It logs the nine data-mutating
transactions plus the two id-space markers (Trim, TabulaRasa).

It does **not** log the two subgraph transactions — `CreateSubGraphTransaction` and
`RemoveSubGraphTransaction`. So with the WAL enabled, a crash between snapshots **loses every
subgraph created or removed since the last snapshot**, even though the same subgraphs *are* durable
across an explicit `Save`/`Load` (via the recipe manifest — `SubGraphFactory.GetPersistableRecipes`
/ `RehydrateFromRecipes`). The WAL is the only durability path that drops them.

Two things made this non-trivial (why it was deferred):

1. **A subgraph's predicates are compiled delegates, not serializable.** Only subgraphs built from a
   *declarative recipe* (`SubGraphResult.Recipe`, an opaque `SpecificationJson` + metadata) can be
   persisted; delegate-only subgraphs (`Recipe == null`) cannot — snapshot save already skips them.
2. **The recipe is attached in the API layer, after the transaction commits.** The controller sets
   `tx.SubGraphCreated.Recipe` *after* `WaitUntilFinished()` returns, but the WAL logs on the writer
   thread *during* commit (before the task completes). At log time the recipe is not yet attached, so
   the codec has nothing durable to serialize.

## 2. Design

**Symmetry with snapshot persistence is the contract.** The WAL must log exactly the subgraphs a
snapshot would persist (recipe-bearing ones), skip exactly the ones a snapshot skips (delegate-only),
and reconstruct them on replay through the same `ISubGraphRecipeCompiler` + `SubGraphFactory` path a
snapshot load uses. A subgraph must survive a crash+WAL-replay iff it survives a Save+Load.

### 2.1 Make the recipe a transaction concern (fixes the ordering gap)

- Add an optional input `String SpecificationJson` to `CreateSubGraphTransaction` — the opaque
  specification text, set by the producer (the REST controller) **before** enqueue.
- At the end of a successful `CreateSubGraphTransaction.TryExecute`, once the subgraph exists, build
  `SubGraphCreated.Recipe` from that input plus the freshly-created result
  (`Name`, `SubGraphId` = the new subgraph's id, `AlgorithmPluginName`, `SourceFallen8Id`,
  `SpecificationJson`) — **iff `SpecificationJson` is non-empty**. Delegate-only creates leave the
  input null and get no recipe, exactly as before.
- The controller stops attaching the recipe post-hoc and instead passes `SpecificationJson` on the
  transaction. The recipe produced is byte-identical to today's (same fields, same source) — it is
  simply built one layer down, so it is present **before** the WAL logs and snapshot persistence is
  unaffected.

### 2.2 Log the subgraph transactions

- Two new `WalEntryType` values, appended (never renumbered): `CreateSubGraph = 12`,
  `RemoveSubGraph = 13`.
- `WalTransactionCodec.TryGetEntryType`:
  - `CreateSubGraphTransaction` → `CreateSubGraph`, **only when `SubGraphCreated?.Recipe != null`**.
    A committed delegate-only create (no recipe) is classified *not loggable* — the same status as
    Save/Load — so the WAL never records an entry it could not replay.
  - `RemoveSubGraphTransaction` → `RemoveSubGraph` (always loggable).
- `SerializeEntry`:
  - `CreateSubGraph`: the recipe as its **JSON** (`CoreJsonContext.Default.SubGraphRecipe` — the same
    source-gen serialization the snapshot manifest uses, already covered by
    `JsonSourceGenParityTest`), followed by `SourceSubGraphName` (empty for a root subgraph). The
    recipe round-trips through the log exactly as through a snapshot.
  - `RemoveSubGraph`: the `SubGraphName`.

### 2.3 Replay

- `RemoveSubGraph` reconstructs a `RemoveSubGraphTransaction { SubGraphName = … }` and re-executes it
  through the normal path — it needs only the name.
- `CreateSubGraph` needs the recipe compiler (engine-external), so `ReplayWriteAheadLog` handles it
  explicitly (like the Trim/TabulaRasa markers): decode `(recipe, sourceSubGraphName)`; if no
  `SubGraphRecipeCompiler` is registered, **skip with a loud warning** (identical to snapshot load's
  behaviour when no compiler is registered); else `compiler.TryCompile(recipe → definition)` and
  re-execute a `CreateSubGraphTransaction { Definition, SourceSubGraphName, SpecificationJson }`.
- **Commit-order replay makes dependencies and matching correct for free.** Entries replay in commit
  order, so (a) every vertex/edge that a subgraph matched has already been re-created when the
  subgraph entry replays — the recomputed subgraph matches the identical elements; and (b) a nested
  subgraph's source already exists, resolved by its stable `SourceSubGraphName`. No id remapping /
  multi-pass rehydration (which the snapshot path needs precisely because it has no commit order) is
  required. Subgraph creation allocates no ids in the parent graph, so it does not perturb the
  vertex/edge id-determinism the existing replay relies on.
- Replay runs with `_walSuspended == true`, so re-executed subgraph transactions are not re-logged.

### 2.4 Algorithm assumption (stated, not hidden)

The only way to create a subgraph *through a transaction* (the only thing the WAL logs) is the
default Breadth-First-Search algorithm — `CreateSubGraphTransaction` uses the
`TryCreateSubGraph`/`TryCreateSubGraphFromSource` default. So replaying a create via that same
transaction path uses the same algorithm that originally produced it; the recipe's
`AlgorithmPluginName` is logged for fidelity, and replay logs a warning if it is ever not the default
(a guard against a future multi-algorithm regression). Threading a non-default algorithm through the
create transaction is a non-goal here.

## 3. Acceptance criteria

- With the WAL enabled: a `CreateSubGraphTransaction` built from a recipe and a
  `RemoveSubGraphTransaction` are both logged; a fresh engine opening the same (unpaired-with-snapshot
  and snapshot-paired) log **replays them** and ends with the identical set of registered subgraphs
  (names, element counts, recalculability) as before the simulated crash.
- A **delegate-only** subgraph create (no recipe) is **not** logged and does not appear after replay —
  matching snapshot save/load, and never producing an unreplayable entry.
- A create-then-remove within the logged window replays to *absent*; a nested subgraph replays after
  its source (resolved by name).
- With **no** compiler registered, a `CreateSubGraph` entry is skipped on replay with a warning and
  recovery continues (no throw, later entries still replay).
- Snapshot save/load of subgraphs is unchanged (the recipe is identical, just built in the
  transaction); the recipe re-attached on replay lets a subsequent `Save` persist the subgraph again.
- Full suite green; new tests pin create/remove/nested/delegate-only/no-compiler/torn-tail-safety and
  the recipe-attachment move. WAL format version stays 1 (values are purely additive).

## 4. Non-goals

- Threading a non-default subgraph algorithm through `CreateSubGraphTransaction`.
- Logging subgraph *recalculation* (`TryRecalculateSubGraph`) — it is derived state re-established by
  replaying the underlying graph mutations, not an independently logged transaction.
- Changing the WAL file envelope, framing, or the snapshot recipe-manifest format.
