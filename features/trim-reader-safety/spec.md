# Trim / Reader Safety — Specification

> **Status:** Planned (P1 correctness/architecture) — from the 2026-07 principal-architect &
> performance review. Trim and auto-trim renumber live element ids **in place** underneath lock-free
> readers, and the element hash **is** that mutable id — a class of reader races and silent id-remaps.
> Make identity stable and stop renumbering on the hot path.

## 1. Problem / current state

Removal is a soft delete; a `Trim` later rebuilds the master store without the tombstones and, in the
same pass, **renumbers every survivor's id to its new dense index**:

- `Fallen8.Trim_internal` (`Fallen8.cs:1927`) gathers survivors, then loops
  `survivors[i].SetId(i)` (`Fallen8.cs:1947-1949`), writing the public `Id` field of the **shared,
  live** element objects a concurrent reader may be mid-scan over. `AGraphElementModel.SetId`
  (`AGraphElementModel.cs:431`) is a plain `Id = newId`.
- `VertexModel.GetHashCode()` (`VertexModel.cs:492`) returns that mutable `Id`, while
  `Equals`/`==`/`!=` (`VertexModel.cs:449-490`) are pure **reference** equality
  (`ReferenceEquals(this, p)`). So a vertex's hash changes the instant `Trim` renumbers it, even
  though the object's identity has not.

Two concrete failure modes fall out of the hash-is-the-mutable-id coupling:

- **Hash-keyed traversal containers corrupt mid-run.** BLS keys its frontier and visited-set on
  `VertexModel`: `HashSet<VertexModel>` (`BidirectionalLevelSynchronousSSSP.cs:127,130`) and
  `Dictionary<VertexModel, VertexPredecessor>` (`BidirectionalLevelSynchronousSSSP.cs:162-165,561-566`).
  A concurrent trim that reassigns the `Id` of a vertex **already inserted** as a key changes its
  hash bucket, so a later `Contains`/`TryGetValue` misses the entry — missed dedup ⇒ the frontier
  re-expands the same vertex (a potentially exponential BLS blow-up) or the algorithm returns a wrong
  result.
- **A held id silently points at a different element.** The REST layer returns element ids and takes
  them back on `GET /vertex/{id}` etc. After a renumber, an id a client still holds resolves to a
  *different* live element — a silent id-remap, no error.

The renumber fires with **no client action**. `MaybeAutoTrim` (`Fallen8.cs:1904`) runs after every
committed removal (`TransactionManager.cs:197-199`) and, once tombstones reach
`_autoTrimTombstoneThreshold` (`Fallen8.cs:1894`, default `100_000`), calls the full renumbering
`Trim_internal`. At the review's 10–100M-element target, 100k tombstones is 0.1–1% churn — an
ordinary steady-state that silently renumbers the whole live id space out from under readers and
REST clients.

This hazard is already on the record but was never resolved: `core-storage-representation/spec.md`
(§6 Risks, `core-storage-representation/spec.md:92-95`) flagged "the `Trim` id-renumbering
interacting with in-flight readers", and the in-place `SetId` is the first reason
`persistence-hardening`'s Stage C deferred non-blocking save (`persistence-hardening/plan.md`, Stage
C outcome, P3 point 1) and is restated in `non-blocking-save/spec.md` §1.

> **Correction to the brief.** (a) The in-place-`SetId` hazard is documented in
> `persistence-hardening/plan.md` (Stage C) and `non-blocking-save/spec.md` §1, **not**
> `memory-footprint/plan.md` (that file is 83 lines; its relevant note is M4 — "heavy fields are
> **not** freed on the removal path itself … that would break rollback and race lock-free readers",
> `memory-footprint/plan.md:32`). (b) Only **BLS** keys hash containers on `VertexModel`.
> `WeightedDijkstraShortestPath` keys on the **raw `int` vertex id** — `(int VertexId, int Hops)` and
> `source.Id` (`WeightedDijkstraShortestPath.cs:233-234,241`) — so the identity-hash fix does **not**
> protect Dijkstra; only stopping the hot-path renumber (Part B) does. (c) `EdgeModel` declares **no**
> `GetHashCode`/`Equals` override (`EdgeModel.cs`), so it already uses object identity and is stable;
> `PathElement.GetHashCode` delegates to `Edge.GetHashCode()` (`PathElement.cs:195-197`), also stable.
> The mutable-hash defect is therefore confined to `VertexModel`.

## 2. Goals / non-goals

**Goals**

- Make the element hash **identity-stable**, so no in-place `Id` change can corrupt a hash-keyed
  container mid-traversal. Zero bytes, zero hot-path cost.
- Stop renumbering live ids on the **automatic** (no-client-action) path. Auto-trim must bound
  tombstone memory **without** reassigning any surviving element's `Id`.
- Confine id renumbering to the explicit, operator-scheduled `TrimTransaction`, which a caller
  invokes knowingly, with a documented contract.
- Establish "the element id is a stable REST handle across auto-trim" as a written invariant.

**Non-goals**

- Changing the single-writer / lock-free-reader model, the segmented master store, or its
  publication ordering (build on them; see `core-storage-representation`).
- Fully decoupling id from storage slot (a per-snapshot `id → slot` directory). That is the real
  end-state for renumber-free compaction and it also constrains non-blocking save — noted as future
  work in §3, **not** built here.
- Reopening the non-blocking-save deferral (`non-blocking-save/`) or the CSR-adjacency skip
  (`csr-adjacency/`). Referenced, not re-litigated.
- Removing `TrimTransaction`'s ability to renumber/compact the id space — that stays, as an explicit
  operation.

## 3. Design sketch

Two independent parts; Part A is a one-line safety fix, Part B changes the automatic behaviour.

### Part A — identity-stable hash (now, zero-cost)

Override `VertexModel.GetHashCode()` (`VertexModel.cs:492`) to return
`RuntimeHelpers.GetHashCode(this)` (the runtime identity hash) instead of `Id`. Because
`Equals`/`==` are already reference equality, this strictly *tightens* the object contract (equal ⇒
equal hashes still holds; two distinct instances that happened to share an `Id` no longer collide),
costs zero bytes and no allocation, and removes the trim-vs-hash-container hazard for BLS's
`HashSet<VertexModel>` / `Dictionary<VertexModel, VertexPredecessor>` entirely — a renumber can no
longer move an inserted key's bucket. (Dropping the override altogether is behaviourally equivalent —
`object`'s default is the identity hash — but the explicit `RuntimeHelpers.GetHashCode(this)` states
the intent.)

Scope is `VertexModel` only: `EdgeModel` and `PathElement` are already identity/edge-reference hashed
(§1 correction) and need no change. **This does not help Dijkstra**, which keys on the raw `int`
`Id` value, not on the object hash — Dijkstra is protected only by Part B.

### Part B — bound churn without renumbering

- **Free the tombstone's heavy fields; keep the slot.** Add an internal
  `ReleaseBodyForTombstone()` on `AGraphElementModel` that nulls the property store (`_properties`,
  `AGraphElementModel.cs:76`), overridden on `VertexModel` to also null the adjacency (`_outEdges` /
  `_inEdges`, `VertexModel.cs:60,66`). Each is a **single `volatile` write**, so a lock-free reader
  observes either the prior fully-built field or `null` — never a torn value — the exact publication
  discipline that already governs property/adjacency mutation. The slot (the tombstone object at
  `index == old id`) stays, so **no id is reassigned** and `Count` / id-space are unchanged.
- **This is safe here precisely because it runs post-commit.** `MaybeAutoTrim` already runs on the
  single writer *after* a removal has committed (`TransactionManager.cs:197-199`), so the M4
  constraint — "heavy fields are not freed on the removal path itself (breaks rollback, races
  readers)" (`memory-footprint/plan.md:32`) — does not apply: the removal will not roll back, and the
  volatile publish makes the reader race benign. A reader holding a stale reference to a removed
  vertex now reads `null` adjacency instead of stale edges — consistent with removal (removed
  elements are excluded from searches and their live adjacency was already detached by the removal
  cascade).
- **Demote auto-trim to opt-in (default OFF).** Replace the unconditional `100_000` trigger with a
  disabled-by-default gate (an `_autoTrimEnabled` flag, threshold configurable) exposed through the
  admin surface. When enabled, auto-trim runs the free-fields pass above — it **never renumbers** —
  so it bounds the dominant per-tombstone memory (properties + adjacency) without touching ids. The
  WAL `Trim` marker currently emitted by the auto path (`Fallen8.cs:1917-1920`) is dropped from it:
  free-fields changes no id space, so replay needs no marker (verify replay determinism is unchanged).
- **Reserve renumbering for the explicit `TrimTransaction`.** `Trim_internal`'s survivor-compaction
  and `SetId(i)` renumber (`Fallen8.cs:1947-1959`) stay exactly as they are, reachable only through
  `TrimTransaction` (`TrimTransaction.cs:43-47`) — an operation a caller schedules knowingly.
  Document that it reassigns ids and must not run concurrently with readers/clients that hold ids.
- **Residual, stated honestly.** Freeing fields leaves a small fixed-size shell per ever-removed slot
  (id == index, so the slot persists). Reclaiming those shells *and* the id space is the explicit
  `TrimTransaction`'s job. The dominant memory (properties + adjacency) is what auto-trim bounds.
- **Future work (noted, not built): `id → slot` directory.** A per-snapshot `id → slot` map would
  let compaction reclaim slots without ever renumbering a live id, making the id a permanently stable
  handle and shrinking shells too. It is also the shared prerequisite for a correct off-worker save,
  so it **constrains `non-blocking-save/`** — hence deferred to a future theme, not this one.

## 4. Acceptance criteria

- **Identity-stable hash (Part A).** Mutating a `VertexModel.Id` does not change its `GetHashCode()`
  nor its membership in a `HashSet<VertexModel>` / key presence in a
  `Dictionary<VertexModel, _>`. A deterministic (single-threaded) test pins this and would fail
  against today's `GetHashCode() => Id`.
- **Traversal concurrent with trim.** A BLS `/path` traversal run repeatedly while a
  `TrimTransaction` executes produces consistent results with no duplicate/missed frontier entries,
  no exceptions, and no wrong-id resolution (a looped concurrency test; flaky/failing on `main`,
  deterministic after the fix).
- **Auto-trim bounds memory without id churn.** With auto-trim enabled, sustained add/remove churn
  keeps managed-heap-retained memory bounded (reuse the `MemoryFootprintTest`/`GC.GetTotalMemory(true)`
  discipline) **while every surviving element's `Id` is unchanged** before vs. after (asserted
  explicitly). Numbers to be captured on this box.
- **Id as a REST handle.** An id returned by a create and re-used in a subsequent `GET` resolves to
  the same element across auto-trim (default-off, and free-fields never renumbers).
- **No regression.** The segmented store's publication ordering is intact; explicit `TrimTransaction`
  still compacts and renumbers; full suite green.

## 5. Risks

- **Behaviour change: auto-trim off by default.** The tombstone id-space no longer self-compacts;
  callers that relied on implicit compaction must schedule an explicit `Trim`. Mitigated because
  free-fields still bounds the dominant memory, and documented as a contract change.
- **Freeing a removed element's adjacency.** A reader holding a stale reference to a removed vertex
  sees `null` adjacency rather than stale edges. Benign (removed elements are excluded from searches
  and live adjacency was already detached), but explicitly called out and covered by a test.
- **Identity-hash assumption elsewhere.** Any code assuming `vertex.GetHashCode() == vertex.Id`
  breaks. None found in-repo (`Equals` is already reference-based; no dedup relies on the id-hash);
  serialization uses the field, not the hash.
- **Explicit `TrimTransaction` still renumbers.** A client/reader holding ids across an explicit
  `Trim` is still remapped — now a knowing, operator-scheduled action instead of a silent hot-path
  event. The full remedy (id ≠ slot) is future work (§3).

## 6. Keep (do not regress)

- **Single-writer mutation and lock-free reads over the `volatile` segmented-store snapshot**, and
  the copy-on-write publication ordering (write slot/field before publishing) — the whole safety
  argument for Part B rests on this being intact (`core-storage-representation`).
- **`Trim_internal`'s reader-safety for the *explicit* path.** In-flight readers holding the
  **previous** snapshot keep a fully consistent old-id-space view and never index out of range
  (`Fallen8.cs:1954-1957`). Only the *automatic, silent, hot-path* invocation changes; the explicit
  compaction's own correctness must be preserved.
- **Reference-equality `Equals`/`==`/`!=` on `VertexModel`** (`VertexModel.cs:449-490`) — unchanged;
  Part A only makes the hash consistent with it.
- **M4's bounded-churn guarantee and its soak test** (`MemoryFootprintTest`) — the new free-fields
  mechanism must still bound growth under churn.
- **WAL replay determinism** (`persistence-hardening` Stage D) — dropping the auto-path `Trim` marker
  must not break replay (no id-space change ⇒ no marker needed); verify.
