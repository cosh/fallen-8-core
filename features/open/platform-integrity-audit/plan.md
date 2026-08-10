# Platform integrity audit - Plan

Companion to [spec.md](./spec.md). Feature branch: `feature/platform-integrity-audit`
(branch-only workflow, no GitHub issue/PR).

**Ordering principle.** Silent failures become loud before anything becomes faster or more
capable. Within that, hard prerequisites first: W3 before W4 (a backfill over a non-idempotent
add corrupts the index it repairs), W2 before W6 (the equal-value clause is what makes the
zero-mutation invariant assertable), W1 before anything that recommends saving more often.

**Independence.** Phases 1 to 3 are pure defect fixes on shipped surface and are worth landing
even if the *integration-runtime* feature never happens. Phases 4 to 6 are the enablement half.
Split the branch there if the two halves want different review cadences.

## Phase 0 - failing tests first

Intent: every P0 has a test that fails on `main` before any fix exists, so the fix is proven and
cannot be argued about later.

- [x] W1: a zero-length registry is silently empty (asserted, then inverted). See the spec's W1
  correction: the boot path is NOT changed, because save-games FR-8 specifies it.
- [x] W2: `PUT /graphelement/{id}/{key}` returns 202 and discards an update; an equal-value write
  bumps modificationDate and emits a change event. (The WAL-frame half is NOT asserted: a frame is
  written per transaction after the codec classifies it, and suppressing it for an all-no-op batch is
  a separate change. The invariant that matters is asserted on the modification-date and change-feed
  channels, and the runtime asserts zero write CALLS.)
- [x] W3: a lookup against a deleted index answers `200 []` (asserted, then inverted to 400 for the
  two routes whose own docs promise it). The WRITE side and the manifest drop are NOT changed - both
  are documented decisions; see the spec's W3 correction.
- [x] W5: the degraded state and a truncated recovery are reachable nowhere outside the engine
  (asserted, then exposed). The per-write signal already existed on `TransactionInformation`; what was
  missing was the instance-level block, plus the dropped-index count moved here from W3.
- [x] W6: a DateTime property does not round-trip under a non-UTC host timezone (asserted, then
  inverted; the test forces the zone, since CI and the container are both UTC). The zero-write-calls
  invariant belongs to the runtime rather than the platform and is asserted in the integration plan.
- [ ] W8: a throw inside a *SingleValueIndex* guarded region leaks the lock.
- [ ] Record each as a named test so the phase-by-phase inversions are traceable.

## Phase 1 - W1: the pointer files [S] (DONE)

Intent: the pointer to all the durable data is as durable as the data.

- [x] Route *SaveGameRegistry.Persist* and the namespace-catalog write through *DurableFileIo*, via a
  new public `ReplaceAllTextDurably` (the one home for the whole solution, not a third private copy).
- [x] ~~Move the orphan-adoption check outside the `member != null` branch~~ **DROPPED as incorrect**:
  save-games FR-8 specifies that an empty registry starts empty and a checkpoint is not loaded just
  because it exists; FR-11 makes adoption an explicit one-time `PUT /load`, and a test pins it. The
  boot path is unchanged.
- [x] Make a present-but-empty registry **and catalog** loud instead of an empty document, with a
  message that says to delete the file to start genuinely empty.
- [ ] Explicitly note in the code that this is **not**
  [crash-durability-hardening](../../done/crash-durability-hardening/) D5, so the two are never
  conflated again.
- [x] Phase 0's W1 tests invert.

## Phase 2 - W2: property replace and remove [L]

Intent: there is a property-update path, it is atomic, and re-asserting an identical value is a
true no-op.

- [ ] Engine: *SetPropertiesTransaction* with **replace** and **set-or-remove** semantics, modelled
  on *SetEmbeddingsTransaction*, over the existing *ReplaceOrAddProperty*.
- [ ] Engine: *WalEntryType.SetProperties = 19* plus its *WalTransactionCodec* classify, serialize
  and replay arm. Ordinals 1 to 18 are all in use.
- [ ] Engine: an equal-value set leaves modificationDate, the change feed and the WAL untouched.
- [ ] apiApp: the batch set-or-remove route, and `DELETE /graphelements` over the existing
  *RemoveGraphElementsTransaction* (no engine change needed on that half).
- [ ] apiApp: the singular route stops returning 202 on a rolled-back transaction.
- [ ] Fix the two wrong doc comments (*IFallen8WriterContext.SetProperty*, the singular route's
  "adds or updates").
- [ ] Retire the recorded remove-then-set limitation in *DocumentIngestionService.UpdateProperty*
  (a duplication removal, and the first proof the new primitive is right).
- [ ] Snapshot regenerate; *f8_mutate* gains ops (never new tools); coverage and contract tests
  move; *AppJsonContext* plus *JsonSourceGenParityTest* for the new DTOs.
- [ ] Extend the *transaction-atomicity* test family, including a replay case for ordinal 19.

## Phase 3 - W3, W8, W5: make the rest loud [M]

Intent: nothing that lost state reports success.

- [ ] W3: *AddOrUpdate* idempotent for an identical (key, element) pair.
- [ ] W3: a missing index is distinguishable from a genuine miss on both the write and the read
  side.
- [ ] W3: the *PersistencyFactory.SaveIndex* null-drop becomes loud.
- [ ] W3: literal-first route shape (`PUT /index/entries/{indexId}`), so an index named *vector*
  stays reachable; document the resync-equals-rebuild contract.
- [ ] W8: the roughly twelve missing *try/finally* blocks in *SingleValueIndex*. Mechanical, and
  deliberately **not** the rejected rewrite.
- [ ] W5: a durability block on `/status` (degraded, replay integrity, last checkpoint) and a
  *Durable* signal on write responses.
- [ ] Phase 0's W3, W5 and W8 tests invert.

## Phase 4 - W4: rebuild from element state [M] (REPAIR HALF DONE)

Intent: a derived index has a repair path, and it is the one the engine already ships.

**Split, per this plan's own stop-and-review trigger.** The repair half landed; the
self-maintenance half did not, and that was a deliberate call rather than running out of road. The
repair primitive is what makes a lost index RECOVERABLE, which is the correctness property the
identity model rests on. Self-maintenance is an optimisation on top of it (it removes the need to
call repair), and it changes the engine's three writer-side projection hooks, which is exactly the
kind of change that should not ride along in a commit whose value does not depend on it.

- [x] One rebuild primitive an out-of-process owner can invoke on the resync signal:
  `IndexRepair.TryRepairFromProperty` plus `POST /index/backfill/{indexId}` (literal-first). Two
  modes: add-only repair (default, idempotent, safe on every start) and exact replace.
- [x] Retire the hand-rolled entity-index sweep in *DocumentIngestionService* (the duplication
  removal, and the proof the primitive is the right shape).
- [x] It lives in the apiApp rather than the engine. "Which property backs which index" is a caller
  concern by index-lifecycle's explicit non-goal, everything it needs is already public engine
  surface, and the caller it subsumes is in the same project - so no engine interface grew a member
  and no delegating wrapper or test fake changed. An earlier draft put it on *Fallen8* and needed an
  *IFallen8Admin* member; hitting that plumbing is what surfaced the better home.
- [ ] **DEFERRED to its own change:** generalize the bound-projection gate from
  *TryGetEmbeddingName(propertyId)* to a key-bound predicate, reusing the three existing
  writer-side hooks unchanged in shape.
- [ ] **DEFERRED with it:** a property-bound dictionary index mode (backfill on create,
  writer-maintained, header-only persistence, rebuild on load), and the assertion that a rebuilt
  index equals an incrementally maintained one across creation, property write, removal and reload.
- [ ] **DEFERRED with it:** verify no embedding behaviour changed (nothing touched those hooks yet,
  so there is nothing to verify in this half).

## Phase 5 - W6: the zero-mutation invariant [M] (PLATFORM HALF DONE)

Intent: the invariant is provable, and the claim-set data model is settled in writing before
anything depends on it.

- [ ] Batch or selective element read, same DTO family as Phase 2's writes, *get_many* as an op on
  the existing tool.
- [ ] DateTime ingress becomes the inverse of egress (*RoundtripKind*), with the non-UTC-timezone
  test from Phase 0 inverting.
- [ ] Write down the forced consequences of the scalar-only, no-CAS property surface: one property
  per claimant rather than a set-valued or blob claim set, withdrawal as an idempotent property
  remove, and no reliance on read-modify-write. This is a spec deliverable, not code.
- [ ] Define the invariant on the **call** channel ("the client issues no write call") and assert
  it there.

## Phase 6 - W7: the control plane [M]

Intent: one channel decision, made before any route table is written.

- [ ] A third *SidecarHttpClient* implementation plus ordinary apiApp controllers. No forwarder,
  no new dependency.
- [ ] Works under both the all-in-one image and the unconditional split topology.
- [ ] Run history and any pending-review queue live in the **graph**, following the ingestion
  precedent, so only the imperative verbs need routes.
- [ ] Health surfaced on `/status` via the base class's cached probe.

## Phase 7 - P1 remainder [S each]

- [ ] W9: the `HEAD /trim` id-reassignment remark on the route **and** the MCP tool description;
  forward pointers on the two stale mcp-server claims.
- [ ] W10: compose profile plus all three *env-up.js* call sites, following the *F8_INGESTION*
  true-opt-out pattern.
- [ ] W11: close the coverage gate's non-described-endpoint hole.
- [ ] W12: wire the graph vocabulary into the NL-assist prompt. No RETRAIN-LOG entry.
- [ ] W13: a markdown-link test over `features/` and the docs tree.

## Phase 8 - P2, only if the branch is still healthy

- [ ] W14: transaction id on the 202 plus `GET /transaction/{id}` over the already-retained state.
- [ ] W15: the *SingleValueIndex* / *RegExIndex* reverse-map mirror, plus a benchmark case that
  would have caught it.
- [ ] W16: index-name validation.

P3 (W17 bounded WAL growth, W18 checkpoint spin) is **not** in this feature. Both are recorded in
the spec with their triggers; W17 belongs to
[hosted-durability-lifecycle](../../done/hosted-durability-lifecycle/)'s own deferred shape.

## Phase 9 - gate

- [ ] Full `dotnet test` green; build clean (warnings as errors).
- [ ] OpenAPI snapshot regenerated with a reviewed diff (additions only).
- [ ] Coverage and contract tests green; every new operation is an op on an existing MCP tool with
  a recorded decision.
- [ ] Docs-site build green and link-checked; the durability signal and any new route documented.
- [ ] One opt-in published benchmark number for batch versus loop. **No** regression gate.
- [ ] Council review; fix findings on the branch; merge with `--no-ff`; move
  `features/open/platform-integrity-audit/` to `features/done/`.

## Progress

- [ ] Phase 0 - failing tests for every P0
- [x] Phase 1 - W1 pointer-file durability + loud corrupt pointer
- [ ] Phase 2 - W2 property replace and remove
- [ ] Phase 3 - W3, W8, W5 make the rest loud
- [ ] Phase 4 - W4 rebuild from element state
- [x] Phase 5 - W6 platform half (batch read + DateTime); the zero-write-calls invariant is the runtime's
- [ ] Phase 6 - W7 control plane
- [ ] Phase 7 - P1 remainder
- [ ] Phase 8 - P2 (optional)
- [ ] Phase 9 - gate, merge, move to done

## Decision / revisit conditions

- **Every rejection in spec.md section 4 is a decision, not a backlog item.** Each carries a named
  trigger. Do not reopen one without the trigger having fired.
- **The two halves can split.** Phases 1 to 3 are defect fixes on shipped surface; phases 4 to 6
  are enablement. If the *integration-runtime* feature slips, phases 1 to 3 still ship.
- **W4 is the load-bearing bet.** If generalizing the bound-projection gate turns out to be larger
  than the bound vector index suggests, the fallback is the rebuild primitive alone (phase 4
  bullet 3) without the property-bound mode, which keeps the repair path and loses only the
  self-maintenance. Do **not** fall back to WAL ordinals; that is rejected with a trigger.
- **One ordinal only.** If a second on-disk ordinal appears necessary, stop and re-review, because
  every lens rejected the one that was proposed.
