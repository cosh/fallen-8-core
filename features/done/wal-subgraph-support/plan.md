# WAL Subgraph Support — Plan

Companion to [spec.md](./spec.md). Move recipe-building into the create transaction → log the two
subgraph transactions → replay them in commit order, mirroring snapshot persistence exactly.

## Phase 1 — Recipe becomes a transaction concern
- Add `String SpecificationJson { get; set; }` to `CreateSubGraphTransaction` (opaque input; cleared
  in `Cleanup`).
- At the end of a successful `TryExecute` (both the root and nested branches, after
  `SubGraphCreated` is set), when `SpecificationJson` is non-empty, attach
  `SubGraphCreated.Recipe = new SubGraphRecipe { Name = Definition.Name,
  SubGraphId = SubGraphCreated.SubGraph.Id, AlgorithmPluginName = SubGraphCreated.AlgorithmPluginName,
  SourceFallen8Id = SubGraphCreated.SourceFallen8Id, SpecificationJson = SpecificationJson }`.
- `SubGraphController.CreateSubGraph`: set `tx.SpecificationJson = JsonSerializer.Serialize(
  specification, AppJsonContext.Default.SubGraphSpecification)` **before** enqueue; delete the
  post-`WaitUntilFinished` `tx.SubGraphCreated.Recipe = …` block (now built in `TryExecute`). The
  engine still never sees the API spec type — it stores the opaque string.
- Verify: existing subgraph snapshot-persistence + controller tests stay green (recipe identical).

## Phase 2 — Log the subgraph transactions
- `WalEntryType`: add `CreateSubGraph = 12`, `RemoveSubGraph = 13` (additive; do not renumber).
- `WalTransactionCodec.TryGetEntryType`: add the two cases; the `CreateSubGraph` case returns false
  when `SubGraphCreated?.Recipe == null` (delegate-only → not loggable).
- `WalTransactionCodec.SerializeEntry` / `Deserialize`:
  - `CreateSubGraph`: `WriteOptimized(JsonSerializer.Serialize(recipe, CoreJsonContext.Default.SubGraphRecipe))`
    then `WriteOptimized(SourceSubGraphName ?? "")`; decode symmetrically. Deserialize returns null
    for this type (handled in the replay loop) and exposes the decoded recipe + source-name via a
    dedicated decode method (`TryDecodeSubGraphCreate`), so the codec stays the single source of the
    payload format.
  - `RemoveSubGraph`: `WriteOptimized(SubGraphName)`; `Deserialize` returns a ready
    `RemoveSubGraphTransaction` (re-executes through the normal path).

## Phase 3 — Replay
- `ReplayWriteAheadLog`: extend the dispatch. `RemoveSubGraph` flows through the existing
  `tx.TryExecute(this)` branch (Deserialize returns the transaction). `CreateSubGraph` gets an
  explicit branch: decode → if `SubGraphRecipeCompiler == null` warn + skip; else compile → build and
  `TryExecute` a `CreateSubGraphTransaction { Definition, SourceSubGraphName, SpecificationJson }`.
  A compile failure warns + skips (recovery continues).
- Confirm `_walSuspended` suppresses re-logging (existing behaviour) and that subgraph creation does
  not advance `_currentId` (so vertex/edge id-determinism is untouched).

## Phase 4 — Tests & document
- New `WalSubGraphSupportTest`:
  - create (root) → simulate crash (drop instance, keep WAL) → new engine + compiler replays → same
    subgraph present (name, vertex/edge counts, recalculable);
  - create + remove → replays to absent;
  - nested subgraph (source is another subgraph) → replays after its source, by name;
  - delegate-only create (no `SpecificationJson`) → NOT logged, absent after replay;
  - `CreateSubGraph` entry + **no** compiler registered → skipped with warning, later entries still
    replay, no throw;
  - snapshot-paired path: Save (resets log) then WAL-log more subgraph ops then replay onto the
    snapshot;
  - torn-tail safety: a truncated trailing subgraph entry is ignored (reuse the WAL framing guard).
- Update this plan's status + note anything deferred. WAL format version stays 1.

## Status
- [x] Phase 1 — recipe becomes a transaction concern. `CreateSubGraphTransaction.SpecificationJson`
  added; `AttachRecipe()` builds the recipe at the end of a successful `TryExecute`;
  `SubGraphController` sets `SpecificationJson` before enqueue and no longer attaches the recipe
  post-`WaitUntilFinished`. Recipe fields are identical to before.
- [x] Phase 2 — log the subgraph transactions. `WalEntryType.CreateSubGraph = 12` /
  `RemoveSubGraph = 13`; `TryGetEntryType` gates `CreateSubGraph` on `SubGraphCreated?.Recipe != null`;
  `SerializeEntry` writes the recipe as `CoreJsonContext.SubGraphRecipe` JSON + `SourceSubGraphName`;
  `RemoveSubGraph` writes the name. `Deserialize` returns a ready `RemoveSubGraphTransaction`;
  `CreateSubGraph` is decoded by `TryDecodeSubGraphCreate` (returns false, never throws, on an
  unparsable-but-CRC-valid entry).
- [x] Phase 3 — replay. `ReplayWriteAheadLog` gained a `CreateSubGraph` branch →
  `ReplaySubGraphCreate` (decode → compiler check → compile → re-execute a
  `CreateSubGraphTransaction`); `RemoveSubGraph` flows through the existing `tx.TryExecute` path. Any
  problem (undecodable / no compiler / compile failure / create-false) is warned + skipped so recovery
  continues. **Unanchored-path addition:** the unanchored log replays during construction, before a
  `SubGraphRecipeCompiler` property could be set, so the `Fallen8(loggerFactory, walOptions,
  subGraphRecipeCompiler = null)` constructor gained an optional compiler that is registered before the
  log opens. The snapshot-paired path sets the compiler before `Load` as before.
- [x] Phase 4 — tests & document. `WalSubGraphSupportTest` (9 methods): snapshot-paired replay;
  snapshot-manifest + WAL both recover; unanchored create; create+remove → absent (with a kept
  control so the removal is the load-bearing difference); nested by name; delegate-only excluded;
  no-compiler skip-with-warning + later entries still replay; a THROWING custom compiler is skipped
  and recovery continues; torn-tail subgraph entry ignored (asserting the edges survive). Full suite
  **353 passed / 10 skipped**. WAL format version stays 1.

## Council fix round (all three lenses APPROVE_WITH_NITS, zero must-fix)
- **Correctness:** `ReplaySubGraphCreate` now wraps compile + re-execute in try/catch → warn+skip, so
  the documented "compile failure / throw → skip, recovery continues" guarantee holds even for a
  misbehaving custom `ISubGraphRecipeCompiler` that throws (the built-in compiler already catches
  internally). Pinned by `ThrowingCompilerAtRecovery_SubgraphEntrySkipped_RecoveryContinues`.
- **Regressions:** removed the now-unused `using NoSQL.GraphDB.Core.SubGraph;` in `SubGraphController`.
- **Tests:** `create+remove` gained a kept "control" subgraph so the logged removal is independently
  load-bearing; `torn-tail` now asserts `EdgeCount == 2` so it uniquely pins the trailing subgraph
  frame as the torn one.

## Notes
- Preserve: WAL envelope/framing/CRC, snapshot recipe-manifest format, the single-writer +
  lock-free-read invariants (this only adds two entry kinds + moves recipe-building down a layer).
- Symmetry test is the spec: a subgraph survives crash+replay iff it survives Save+Load.
- The recipe round-trips through the WAL as the SAME `CoreJsonContext.SubGraphRecipe` JSON the
  snapshot manifest uses (already covered by `JsonSourceGenParityTest`), so there is no second
  serialization to keep in sync.
