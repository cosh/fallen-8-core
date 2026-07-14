# Transaction Retention & Completion Contract — Specification

> **Status:** Planned (P1 architecture) — from the 2026-07 principal-architect & performance review.
> Transaction bookkeeping grows without bound on insert-only workloads, `Trim`'s cleanup of the
> created-model lists can make a caller's `GetCreated*` throw, and the `Error` channel is overloaded
> to also mean "durability degraded". Bound retention structurally and clean up the completion
> contract.

## 1. Problem / current state

The transaction layer keeps a `ConcurrentDictionary<String, TransactionInformation>` keyed by a
per-transaction GUID string (`TransactionManager.cs:48`). Every enqueued transaction inserts an
entry (`TransactionManager.cs:303`) and nothing removes it until a `Trim`. This yields three
distinct defects plus one hot-path cost.

**R1 — Bookkeeping grows without bound on insert-only workloads.** Each transaction permanently
retains: the dictionary entry, its `TransactionInformation`, the completion `Task`, the GUID string,
and the `ATransaction` itself (which, for creates, holds its captured created-model list). The only
thing that reclaims a terminal entry is `TransactionManager.Trim()` (`TransactionManager.cs:321-347`),
whose only call sites are `Trim_internal` (`Fallen8.cs:1962`) and the WAL load path
(`Fallen8.cs:1640`). `Trim_internal` (`Fallen8.cs:1927`) is itself reached only from an explicit
`TrimTransaction`, from `Load`, and from `MaybeAutoTrim` (`Fallen8.cs:1904`) — and auto-trim fires
**only for element-removal transactions** (`ATransaction.TriggersAutoTrim` defaults `false`,
`ATransaction.cs:85`). So a server doing sustained inserts / property-writes and no removals **never
reclaims anything**: the dictionary is an ever-growing leak. Rough order of magnitude ≈ 400–600 B/tx
(dictionary node + `TransactionInformation` + `Task` + 36-char GUID string + `ATransaction` +
created-model `List`); 10M transactions ≈ several GB (the exact per-tx figure is captured by the
Phase 0 benchmark on this box). `engine-performance` **P9** widened *what* `Trim` reclaims (both
`Finished` and `RolledBack`, `TransactionManager.cs:331-334`) — but nothing *schedules* `Trim` on a
no-removal workload, so P9 does not help here. This is the structural leak.

**R2 — `Trim`'s `Cleanup()` can make a caller's `GetCreated*` throw, under a race the caller cannot
avoid.** `TransactionManager.Trim()` calls `txInfo.Transaction.Cleanup()` (`TransactionManager.cs:345`),
which nulls the captured lists (`CreateVerticesTransaction.Cleanup` sets `_verticesCreated = null`,
`CreateVerticesTransaction.cs:94`; `CreateEdgesTransaction.cs:106` likewise). But
`GetCreatedVertices()` does `ImmutableList.CreateRange(_verticesCreated)` (`CreateVerticesTransaction.cs:80`)
and `GetCreatedEdges()` the same (`CreateEdgesTransaction.cs:92`) — `CreateRange(null)` throws
`ArgumentNullException`. This **contradicts** `memory-footprint`'s documented contract
(`features/memory-footprint/spec.md:122-125`), which states the handles "read empty" after the next
`Trim` (including M4's auto-trim). And a caller cannot defend against it: an *unrelated* client's
removal can fire `MaybeAutoTrim` → `Trim_internal` → `TransactionManager.Trim()` in the window
between this caller's `WaitUntilFinished()` and its `GetCreatedVertices()`, turning a documented
"reads empty" into a thrown exception at a distance.

**R3 — the `Error` channel is overloaded.** `TransactionInformation.Error` is documented
(`TransactionInformation.cs:43-56`) as the discriminator between a genuine execution fault (`Error !=
null`, state `RolledBack`) and a clean rollback (`Error == null`). But `LogCommittedTransactionSafely`
(`TransactionManager.cs:220-237`) records a **write-ahead-log append failure** on
`txInfo.Error` (`TransactionManager.cs:235`) while the transaction stays `Finished` (committed in
memory, durability degraded). A caller that checks `Error` before `TransactionState` — exactly the
order the doc invites — misclassifies a committed-but-durability-degraded transaction as a fault.
`Error != null` no longer means "execution faulted".

**F14 — per-transaction hot-path overhead (folded in, same file).** `ProcessTransaction` /
`AddTransaction` emit an unconditional `LogInformation` per transaction at start
(`TransactionManager.cs:135`), finish (`TransactionManager.cs:178`) and enqueue
(`TransactionManager.cs:285`) — three interpolated log events on every mutation even when the level
is disabled. And `TransactionId` is materialised as `Guid.NewGuid().ToString()`
(`ATransaction.cs:34`), allocating a 36-char string for every transaction whether or not anyone ever
polls state by id (most callers wait on the `Task`).

## 2. Goals / non-goals

**Goals**
- Bound the transaction bookkeeping so an insert-only / no-removal workload reclaims terminal entries
  automatically, with a deterministic memory ceiling, without a client having to call `/trim`.
- Make `GetCreatedVertices()` / `GetCreatedEdges()` safe: never throw because an unrelated trim ran;
  a waited-on caller reading promptly gets the created models (or a documented empty), never an
  exception.
- Give durability-degraded a dedicated signal so `Error` recovers its single, documented meaning
  ("execution faulted"), and a committed-but-degraded transaction is distinguishable from a faulted
  one.
- Cut the per-transaction hot-path overhead (F14) with no behaviour change.

**Non-goals**
- Changing the transaction model: mutations stay on the single writer thread; reads stay lock-free
  over the volatile snapshot; `Try*(out,…):bool` stays the public not-found/invalid shape.
- Persisting or replaying transaction *bookkeeping* — this is in-memory completion state, not durable
  data (the WAL already persists committed *effects*; see `persistence-hardening`).
- Reopening the `non-blocking-save` deferral, the `csr-adjacency` skip, or the
  `engine-performance-followups` deferrals — none are touched here.
- A general problem+json / structured error envelope for the REST layer (that is
  `transaction-failure-reasons`, already landed; this feature adds one field beside its
  `FailureReason`).

## 3. Design sketch

### 3.1 Bounded terminal-entry retention (R1)

Add a bounded reclaimer to `TransactionManager`, driven **entirely on the single writer thread** —
where every state transition already happens (`SetTransactionState` runs inside `ProcessTransaction`,
which the worker runs via `RunSynchronously`). When a transaction reaches a terminal state
(`Finished` or `RolledBack`) in `ProcessTransaction`, push its id onto an in-memory FIFO of terminal
ids. While the FIFO count exceeds `MaxRetainedTerminalTransactions` (a configurable bound, default in
the tens of thousands — large enough that a caller polling `GetTransactionState` immediately after
completion still finds its entry), pop the oldest id and `TryRemove` it from `transactionState`.

- **O(1) amortised per commit, lock-free.** The FIFO is touched only on the worker thread (terminal
  transitions and `Trim`), so it needs no synchronisation of its own. The dictionary stays a
  `ConcurrentDictionary` purely because `AddTransaction` inserts the initial `Enqueued` entry from the
  *enqueuer's* thread and `GetTransactionState` reads from arbitrary threads.
- **Only terminal entries are counted/evicted.** In-flight (`Enqueued`) entries are inserted
  off-worker by `AddTransaction` but are bounded by the pending-queue depth and are never evicted
  while non-terminal; the unbounded set is exactly the terminal one.
- **Callers holding the returned `TransactionInformation` are unaffected.** `EnqueueTransaction`
  returns the instance (`Fallen8.cs:1663`); a caller reads `TransactionState` / `Error` /
  `FailureReason` / the new `DurabilityDegraded` directly off it. Eviction only stops the
  `GetTransactionState(txId)` *dictionary lookup* from resolving an old id (→ `NotExist`) — exactly
  what `Trim` already does for trimmed ids, so the observable contract is unchanged.
- **Coexists with `Trim`.** The explicit `Trim()` (all terminal entries) and the WAL / load `Trim`
  paths stay; after a full `Trim` the FIFO is reconciled (cleared — the dictionary's terminal entries
  are gone). WAL replay, which re-executes many transactions, self-bounds during replay and the
  closing `_txManager.Trim()` clears the remainder.
- **Alternative considered:** TTL eviction via a `(timestamp, id)` queue (evict head while older than
  a TTL). Rejected as the primary because a count bound gives a deterministic memory ceiling and
  matches "keep the last N for polling"; TTL can be layered later if a time-based policy is wanted.

### 3.2 Created-model handles never throw (R2)

Two changes, cheap-first then structural:

- **Null-safe accessors (cheap, lands first):** `GetCreatedVertices()` / `GetCreatedEdges()` return an
  empty `ImmutableList` when the backing list is `null` (e.g. `_verticesCreated is null ?
  ImmutableList<VertexModel>.Empty : ImmutableList.CreateRange(_verticesCreated)`). This alone removes
  the throw and restores `memory-footprint`'s documented "reads empty" contract, immune to the
  cross-caller race.
- **Stop nulling the created-model lists in `Cleanup()` (structural, the real fix):** the created
  elements are already referenced by the master store, so nulling `_verticesCreated` / `_edgesAdded`
  reclaims only the small `List` node array while creating the race. Bounded retention (§3.1) removes
  the *dictionary entry* — the actual leak — and once no one references the `ATransaction`, it and its
  list are collected wholesale. `Cleanup()` therefore stops nulling the created-model lists; a
  waited-on caller reading promptly gets the **actual** created models, not empty, regardless of a
  concurrent unrelated trim. (`Cleanup()` may still drop the *input* definition, which
  `ReleaseAfterCompletion` already released at commit — see `memory-footprint` M3.) The null-safe
  accessors remain as a guardrail.

### 3.3 Dedicated durability-degraded signal (R3)

- Add `public bool DurabilityDegraded { get; set; }` to `TransactionInformation` (default `false`),
  documented as: the transaction **committed** (`TransactionState.Finished`) but its write-ahead-log
  append did not reach disk, so its durability is degraded until the next full `Save` re-establishes a
  baseline. It is set in place before the task completes, under the same happens-before as
  `TransactionState` / `Error` / `FailureReason`.
- `LogCommittedTransactionSafely` sets `txInfo.DurabilityDegraded = true` (and keeps logging the WAL
  exception loudly) **instead of** `txInfo.Error = logEx`. `Error` reverts to meaning exactly
  "execution faulted" (`Error != null` ⟺ state `RolledBack` from a thrown exception), as its doc
  states.
- Coordinate the naming/semantics with `crash-durability-hardening` (a related, not-yet-landed theme)
  so an engine-level degraded-mode flag and this per-transaction flag agree. This feature does not
  depend on that theme; it only reserves the field and stops the `Error` overload.

### 3.4 Hot-path trim (F14)

- Demote the three per-transaction `LogInformation` calls (`TransactionManager.cs:135,178,285`) to
  `LogDebug`, or guard them with `_logger.IsEnabled(LogLevel.Information)` so the interpolation is
  skipped when the level is disabled. The `LogError` / `LogWarning` failure-path logs stay as-is
  (they are rare and load-bearing).
- Hold the transaction id as a `Guid` and stringify lazily. Concretely: `ATransaction` holds a
  `readonly Guid` id; logging templates take the `Guid` directly (formatted only when the level is
  enabled); the id is turned into a string only at the dictionary boundary / when a string is actually
  requested. `GetTransactionState(String txId)` keeps its public signature and resolves via
  `Guid.TryParse` (a `Try*`-style parse: an unparseable/unknown id → `NotExist`). Changing the public
  `TransactionId` field's type is a surface consideration — either key the dictionary by `Guid` and
  expose `TransactionId` as a `string` property computed on access (source-compatible, avoids the
  eager `ToString`), or bump the type and version per the repo's public-API-change convention; the
  implementer picks the least-disruptive shape and pins whichever behaviour with a test.

## 4. Acceptance criteria

- **Bounded retention:** an insert-only workload of N transactions (N well past the retention bound,
  with **no** removals so nothing auto-trims) leaves the transaction bookkeeping bounded — the
  `transactionState` entry count stays at or below `MaxRetainedTerminalTransactions` and does not grow
  with N — pinned by a characterization test, and the drop vs. the unbounded baseline captured by the
  Phase 0 benchmark on this box. A caller polling `GetTransactionState` for a *recent* transaction
  still resolves it; a long-superseded id returns `NotExist` (same as a trimmed id).
- **Handles never throw:** `GetCreatedVertices()` / `GetCreatedEdges()` after a `Trim` (explicit
  **and** a concurrent auto-trim fired by an unrelated removal between `WaitUntilFinished()` and the
  read) return a list — the created models, or empty — and **never** throw `ArgumentNullException`;
  pinned by a test that interleaves a create-then-read with a foreign removal-driven auto-trim.
- **Durability distinguishable:** a committed transaction whose WAL append failed is observable as
  `TransactionState.Finished` + `DurabilityDegraded == true` + `Error == null`, while a genuinely
  faulted transaction is `RolledBack` + `Error != null` + `DurabilityDegraded == false`; a caller can
  tell the two apart. Pinned by a test using an injected failing log sink.
- **Hot-path:** the three per-transaction info logs do not allocate/format when the level is disabled;
  the transaction id is not eagerly stringified per transaction. The reduction is captured by the
  Phase 0 benchmark; behaviour (id round-trips, `GetTransactionState` resolution) is unchanged.
- Full suite green; `transaction-failure-reasons`, `memory-footprint` (M3 input release, M4 auto-trim),
  the WAL append hook, and single-writer / lock-free-read invariants all still hold.

## 5. Risks

- **Evicting an entry a caller still wants to poll by id.** Mitigated by a generous default bound and
  by the fact that a waited-on caller reads its held `TransactionInformation` directly; the poll-by-id
  path was already best-effort (a trimmed id returns `NotExist`). Document the bound.
- **Concurrency of the FIFO.** Safe only because every mutation of terminal state and of the FIFO is
  on the single writer thread; the design must not introduce a second writer. A test asserting the
  invariant guards this.
- **Changing `memory-footprint`'s documented "reads empty" contract** (§3.2 structural fix makes a
  prompt read return the *actual* models). This is a strict improvement (a documented-but-buggy throw
  becomes correct data), but it is a behaviour change; call it out and update the `memory-footprint`
  note. If the structural change is judged out of scope, the null-safe accessors alone satisfy the
  original "reads empty" contract.
- **`TransactionId` type change** touches the public surface; the conservative property-based shape
  avoids a break, but the choice must be explicit and version-gated if the field type changes.

## 6. Keep (do not regress)

- **Single writer / lock-free reads.** All state transitions and reclamation stay on the writer
  thread; reads stay over the volatile snapshot. The FIFO adds no lock and no second writer.
- **The `engine-performance` P9 widening.** `Trim` still reclaims both `Finished` and `RolledBack`;
  bounded retention is *additive* — it schedules reclamation, it does not narrow what `Trim` removes.
- **`memory-footprint` M3 / M4.** Input is still released at commit (`ReleaseAfterCompletion`); the
  removal-driven auto-trim still fires; this feature must not re-retain the heavy input.
- **`transaction-failure-reasons`.** `FailureReason` keeps its meaning and mapping; `DurabilityDegraded`
  is a *new, orthogonal* field, not a reason value, and does not affect the rolled-back → HTTP-status
  mapping.
- **The WAL contract.** A WAL append failure must still never fault the single worker; the transaction
  still stays committed. Only *where the failure is recorded* changes (a dedicated flag, not `Error`).
- **`WaitUntilFinished` happens-before.** `DurabilityDegraded` is published under the same edge as
  `TransactionState` / `Error` / `FailureReason`, so a waited-on caller observes it.
