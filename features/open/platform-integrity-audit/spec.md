# Platform integrity audit - Specification

> **Status:** Open, spec only (no implementation yet). Follow the feature workflow in the
> repository root `CLAUDE.md`. Feature branch: `feature/platform-integrity-audit` (branch-only
> workflow, no GitHub issue/PR).
>
> **How this feature came to exist.** Designing the *integration-runtime* feature (a first-party
> sidecar that pulls data from systems on the user's own network into a Fallen-8) required a
> survey of what the platform does and does not support. That survey produced thirteen claimed
> gaps. Each was then hard-verified against the code by principal engineers, reviewed
> adversarially through four independent architecture lenses (engine and storage, API and
> contract, durability and correctness, performance), and ranked twice by two product managers
> with deliberately opposed customers. This document is the ranked outcome.
>
> **Read this even if the integration runtime is cancelled.** Seven of the top items are live
> defects in already-shipped, snapshot-pinned, MCP-bridged surface. Three of them lose user data
> and return a success status while doing it. The integration runtime is what *surfaced* them,
> not what justifies them.
>
> **Revision history:**
> - *2026-08-09a* - initial ranking from the verification pass, four-lens architecture review and
>   two-PM prioritization.

## 1. Summary

An audit of the write path, the index lifecycle, the boot path and the control-plane surface,
producing one prioritized fix list. The dominant finding is a class, not an item:

> **Fallen-8's failure mode at its edges is silent success.** A property update returns
> 202 Accepted and discards the write. An index lookup against a destroyed index returns
> 200 with an empty array. A boot that cannot find its registry starts empty with a complete
> checkpoint sitting on disk, one warning in the log, and a green health probe. A write into a
> degraded WAL returns 200.

Every P0 below is an instance of that class. The engine's interior is careful: fsync before
rename, CRC envelopes, torn-tail readers, group commit with durable-before-ack, transaction
atomicity with per-step undo. The audit found that this care stops at the boundary between the
engine and everything that addresses it.

Both prioritization passes converged on the same top three items independently, and both
demoted or dropped the two items the original gap list had emphasized most (a batch index-add
route and a multi-key index lookup). The reason is in section 4.

## 2. How priority was set

Two product managers ranked independently, then their rankings were merged. Where they
disagreed on rank the more conservative position was taken; where they disagreed on *inclusion*
both positions are recorded.

**Platform PM tie-breakers, applied in order:** silent beats loud; already shipped beats
hypothetical; a workaround already hand-rolled twice in the tree is a platform gap rather than a
feature request; prefer the fix that converts a client discipline into an enforced platform
property.

**Product PM principle:** rank by (irreversibility of the loss) times (silence of the failure)
divided by cost, treating "returns a success status while doing nothing" as the highest-severity
defect class in the product, above both performance and capability. Their customer is
unattended: nobody watches a dashboard on a machine in a cupboard, and the decision that the
graph is the sole system of record removed the last backstop.

Both PMs drew an unusually generous cut line (P0 and P1 ship together). The stated reason is
that a silent failure cannot be deferred, because "later" is defined as after the user has
already lost data and cannot tell.

## 3. The ranked work items

Cost is S (under a day), M (a few days), L (a week or more).

### P0 - the correctness floor

#### W1. Registry and catalog write durability, and an honest empty boot [S]

*SaveGameRegistry.Persist* ([SaveGameRegistry.cs:188](../../../fallen-8-core-apiApp/Services/SaveGameRegistry.cs#L188))
writes the temp file with *File.WriteAllText* and then renames, with **no** *Flush(true)*. The
namespace catalog does the same ([Fallen8Namespaces.cs:651](../../../fallen-8-core-apiApp/Namespaces/Fallen8Namespaces.cs#L651)).
The engine's own [DurableFileIo.cs:66](../../../fallen-8-core/Persistency/DurableFileIo.cs#L66)
fsyncs before every rename. Rename atomicity is not content durability: a power loss can publish
a zero-length registry.

The consequence chain is fully verified. A zero-length registry reads as an **empty document**
(*IsNullOrWhiteSpace* to `new SaveGameRegistryDocument()`,
[SaveGameRegistry.cs:134](../../../fallen-8-core-apiApp/Services/SaveGameRegistry.cs#L134)). Then
*StartNamespace* takes the `member == null` branch
([DurabilityLifecycleService.cs:115](../../../fallen-8-core-apiApp/Services/DurabilityLifecycleService.cs#L115)),
logs one warning, and **starts the namespace empty**. The FR-10 crash-window orphan adoption that
would rescue exactly this case lives inside the `member != null` branch and therefore can never
run. Health is green, readiness is ready, `/status` reports zero vertices, and a complete
checkpoint with its anchored WAL sits on disk unreachable.

A truncated-but-non-empty registry is the *loud* path (it throws on invalid JSON). Only the
zero-length case is silent, and zero-length is exactly what an unflushed create-then-rename
produces.

**Fix, corrected during implementation.** The original proposal was to route both writes through
*DurableFileIo*, **move the orphan-adoption check outside the `member != null` branch**, and make a
present-but-empty registry loud. The middle item is **wrong and was not implemented**:

> **[save-games](../../done/save-games/spec.md) FR-8 specifies the empty-registry behaviour
> deliberately**, verbatim: "No `metadata/savegames.json`, or an empty registry → **start empty**. A
> checkpoint sitting in the storage directory is NOT loaded just because it exists." FR-11 makes the
> adoption an explicit one-time `PUT /load` and says the startup log must state that plainly. The code
> the review called an unreachable rescue path is the **FR-11 migration hint doing its specified job**,
> and *SaveGamesEndpointTest.Startup_EmptyRegistry_StartsEmpty* pins it. Auto-adopting would resurrect
> unregistered data unnoticed, which is the same hazard FR-9 refuses for a missing file.

So the defect is narrower than the review stated, and the narrower statement is the accurate one:
**a destroyed registry was indistinguishable from a legitimately empty one.** An absent file
honestly means "nothing was ever saved"; a present zero-length file means the pointer was
destroyed while the checkpoint it named may still be on disk. The registry's own read path already
treats invalid JSON as loud ("never silently overwritten"), so zero-length being silent was an
inconsistency inside its own contract rather than a violation of FR-8.

**What was implemented:**

1. Both writes go through a new *DurableFileIo.ReplaceAllTextDurably* (write to a GUID-unique temp,
   fsync, atomic rename), which also promoted *DurableFileIo* to public as the one home for the
   whole solution rather than letting the apiApp carry a third private copy of the discipline. The
   engine declares no *InternalsVisibleTo* by decision, so public was the available way to share it.
2. A present-but-empty registry **and** a present-but-empty namespace catalog are loud, with messages
   that say to delete the file to start genuinely empty. The catalog case is the worse of the two:
   losing it strands every non-default namespace's data directory and WAL.
3. The boot path is **unchanged**. FR-8 governs.

**Do not conflate this with [crash-durability-hardening](../../done/crash-durability-hardening/)
D5** (write-through rename via P/Invoke, WAL header identity pairing). That deferral is correct
and stays deferred. This is the ordinary content fsync the engine already performs everywhere.
Conflating them would park a two-line fix behind a P/Invoke project.

**Why rank 1 (both PMs):** it is the single point where all graph state becomes unreachable in
one step, it is silent, the fix is two lines plus a branch move, and any recommendation that the
integration runtime call `PUT /save` after each sync multiplies the exposure by the poll rate.

#### W2. Property replace and remove: one WAL ordinal, both batch routes, no-op honesty [L]

**There is no property-update path anywhere in the stack.** *AGraphElementModel.SetProperty*
([AGraphElementModel.cs:617](../../../fallen-8-core/Model/AGraphElementModel.cs#L617)) throws
*ArgumentException* when the key exists with a different value; its own comment says so: "a no-op
when the value is equal, an error when it differs (callers update via RemoveProperty followed by
SetProperty)". Therefore:

- *AddPropertiesTransaction* classifies a changed value as *TransactionFailureReason.Conflict*
  and rolls the **whole batch** back. It is add-or-must-equal, not upsert. So a batch
  property-set route built over it is a 409 generator, not a fix.
- The **already-shipped** `PUT /graphelement/{id}/{key}` cannot update an existing property. It
  rolls back with *InternalError*, which is a 500 when waited, and because *waitForCompletion*
  **defaults to false**, *AwaitAndAccept* returns **202 Accepted without inspecting the
  outcome**. The route documented as "Adds or updates a property on a graph element" silently
  discards every update and reports success.
- The repo already knows. *DocumentIngestionService.UpdateProperty* works around it with
  remove-then-set and records the cost verbatim: "ACCEPTED LIMITATION: these are two
  transactions ... a crash between them leaves it unset".
- *IFallen8WriterContext.SetProperty*'s doc comment claims it "adds or updates". It cannot
  update.

The replace primitive already exists and is unreachable from any transaction except restore:
*AGraphElementModel.ReplaceOrAddProperty* ([AGraphElementModel.cs:652](../../../fallen-8-core/Model/AGraphElementModel.cs#L652)),
"replacing any existing value in a SINGLE copy-on-write allocation".

There is also **no batch property-remove**: *WalEntryType.RemoveProperty = 7* has no plural
sibling and the only route is `DELETE /graphelement/{id}/{key}`.

**Fix:** one new engine transaction, *SetPropertiesTransaction*, with **replace** semantics and a
**set-or-remove** shape, modelled line for line on the shipped *SetEmbeddingsTransaction*, using
the existing *ReplaceOrAddProperty*, plus *WalEntryType.SetProperties = 19* and its codec
classify/serialize/replay arm. Ordinals 1 to 18 are all in use; 19 is the next, exactly as
*SetEmbeddings = 16* extended before it. Then two apiApp routes over it, plus
`DELETE /graphelements` over the existing *RemoveGraphElementsTransaction* (which, unlike the
property side, already has true all-or-nothing rollback and needs no engine change). Fix the two
wrong doc comments in the same change.

**The set-or-remove shape is not a convenience.** It is what makes a claim set expressible at
all; see W6.

**Also required, and the reason this is L not M:** an equal-value write must become a **true**
no-op. Today *SetProperty* skips the store write but still runs
`ModificationDate = DateHelper.GetModificationDate(CreationDate)` unconditionally, and the
transaction still records an undo entry, still emits a *propertySet* change event, and still
appends a WAL frame. So "re-asserting an identical value changes nothing" is currently false in
three observable channels.

**Why rank 2 (both PMs):** it is a data-loss-shaped defect on shipped, snapshot-pinned,
MCP-bridged surface, independent of any new feature. The equal-value clause is what turns "a
re-sync of an unchanged source produces zero mutations" from a client discipline into an enforced
platform property, and it is the reason the batch element read could be demoted.

#### W3. Claim-index integrity: idempotent add, loud missing index, and the resync signal [M]

Index existence is destroyed by **three ordinary shipped operations**, and both the write and
the read side answer with a success status:

1. `/tabularasa` calls *IndexFactory.DeleteAllIndices()*
   ([Fallen8.cs:627](../../../fallen-8-core/Fallen8.cs#L627) and
   [:681](../../../fallen-8-core/Fallen8.cs#L681)), which **replaces the whole map**. It deletes
   indices; it does not wipe them.
2. `PUT /load` and `/savegames/{id}/load` call *DeleteAllIndices()* on success
   ([Fallen8.Persistence.cs:807](../../../fallen-8-core/Fallen8.Persistence.cs#L807)): the loaded
   checkpoint's index set replaces the live one. Loading a save game is a shipped Studio feature.
3. *PersistencyFactory.SaveIndex* catches a per-index serialization failure, logs, returns null,
   and the caller drops null entries from the manifest. **One transient disk error writes a
   checkpoint that no longer contains the index**, and the next load comes up with every element
   intact and the index gone.

After any of the three, *AddToIndex* answers `200 false` and a lookup answers `200 []`. For a
claim-resolving client the result is not slowness: it resolves every claim against a nonexistent
index, gets empty answers, creates duplicate vertices, and the original vertices become permanent
orphans whose claims are known to nobody, so **no withdrawal will ever delete them**.

The only in-band signal is the change feed's resync event (*ResyncReasonTabulaRasa* /
*ResyncReasonLoad*). That makes an open SSE subscription on `GET /changefeed` a **hard design
constraint** on any client that owns a derived index, not an optimization.

**Fix, corrected during implementation.** Three of the four proposed changes turned out to target
deliberate, documented decisions. The rule that resolved each one: **make the implementation match
the contract the route already publishes, rather than making every route behave alike.**

1. **Idempotent *AddOrUpdate*: implemented.** The real bug, with no contract conflict. The guard
   reads the existing reverse map, so it is O(1) rather than a bucket scan.
2. **Loud missing index on the READ side: implemented for `/scan/index/all` and
   `/scan/index/range` only.** Both already document "400 Invalid specification **or index not
   found**", so the implementation was contradicting its own published contract, and
   [api-error-contract](../../done/api-error-contract/)'s governing principle is exactly that (E3
   refuses "an empty 200 masquerading as searched"; E7 refuses "an ambiguous 0 that also means zero
   edges"). **`/scan/index/fulltext` is deliberately left alone**: its doc explicitly says "a miss
   yields 204 No Content, not a 404", which is a justified decision rather than an unfixed
   ambiguity. Vector and spatial likewise keep their own documented shapes.
3. **Loud missing index on the WRITE side: NOT implemented.** *AddToIndex*'s doc states the choice
   and its reason verbatim: "false if the index or the graph element does not exist (a miss is
   reported as 200 with a false body, not a 404)". It returns a real signal; a caller must check the
   boolean. Changing it would break a documented contract to fix a caller's discipline problem.
4. **Loud *SaveIndex* null-drop: NOT implemented, and moved to W5.** That code's comment states the
   reason it swallows: "a persistable index that nonetheless fails to serialize ... must not abort
   the whole checkpoint", and it already logs at Error. Aborting the checkpoint would trade a lost
   index for a lost checkpoint, which is strictly worse. What was actually missing is a **signal**,
   so "the last checkpoint dropped an index" belongs in W5's durability block, not here.

**Route shape** stays a live constraint for W4: use **literal-first**
(`POST /index/backfill/{indexId}`), because `PUT /index/vector/{indexId}` already occupies three
segments and an index legitimately named *vector* would otherwise be silently unreachable (index
names are unvalidated caller strings). W3 adds no `/index/...` route, so it creates no collision.

**One deliberate behaviour change to flag:** `/scan/index/all` and `/scan/index/range` now answer
400 where they previously answered an empty 200. A smoke-test assertion pinned the old behaviour
with no rationale and has been inverted with the reasoning recorded in place.

**W3 is a hard prerequisite of W4.** A backfill over a non-idempotent add corrupts the index it
was meant to repair.

#### W4. Index backfill from element state: one rebuild primitive [M]

Indices never auto-maintain and never backfill: [index-lifecycle](../../done/index-lifecycle/)
states the non-goal explicitly. So a derived index over element state has no repair path, and an
out-of-process client cannot run the in-process sweep the semantic layer uses. That sweep's own
comment states the hazard: "after a hard crash an Entity vertex can outlive its index key;
without this, the next ingest would create a duplicate"
([DocumentIngestionService.cs:429](../../../fallen-8-core-apiApp/Ingestion/DocumentIngestionService.cs#L429)).
That workaround now exists **twice** in the tree (the entity sweep, and the bound vector index's
own rebuild), which by the Platform PM's third tie-breaker makes it a platform gap.

**The mechanism already exists and must be generalized, not invented.** A *bound* vector index
(created with *embeddingName*) is already a crash-durable derived index with no WAL ordinal:

- it backfills from element state at creation
  ([IndexFactory.cs:125](../../../fallen-8-core/Index/IndexFactory.cs#L125), *RebuildProjection*),
- it is maintained by the **single writer thread** after each committed mutation through three
  existing hooks: a property-write hook, an element-creation hook, and the projection itself
  ([Fallen8.Embeddings.cs:30-140](../../../fallen-8-core/Fallen8.Embeddings.cs#L30)),
- it persists **only a header** and rebuilds from element state on load,
- and it holds the exact invariant a claim index needs, quoted from the code: "the live
  projection always matches what a load-rebuild from element state would produce".

The gate is one predicate, *TryGetEmbeddingName(propertyId)*. Widening it from "bound to an
embedding name" to "bound to a property key" gives a property-bound dictionary index that
maintains itself, backfills itself, and survives a crash, with **zero new on-disk ordinals**.

**Fix:** a rebuild-from-element-state primitive, writer-ordered, exposed once (so an
out-of-process owner can invoke it on the resync signal from W3), plus the property-bound index
mode. All four architects independently endorsed this shape and independently rejected the
alternative (see section 4).

#### W5. Durability and recovery-integrity signal on the REST surface [M]

*TransactionInformation.Durable* and *DurabilityDegraded* are set by the writer and read
**nowhere** in the apiApp (zero hits). *AwaitAndAccept* and *AwaitBatch* inspect only
*TransactionState*, never *Durable*. *WalDegradedForMetrics* is internal and feeds only an OTel
gauge, so it exists only if the operator wired a collector. A truncated WAL replay logs one
error, stops, becomes an Activity tag, and readiness is still marked. *StatusREST* has no
durability field.

So a client can write into a degraded WAL and receive success for every write, then lose all of
them on the next kill; and after a truncated replay it reconciles against a silent prefix of
history. Where deletion is driven by "the last claim was withdrawn", a claim set that lost
entries makes a client delete elements another claimant still asserts, which is the one mutation
re-syncing cannot undo.

**Fix:** one durability block on `/status` (degraded flag, replay integrity, last checkpoint) and
a *Durable* signal on write responses. This is the whole D1 apparatus made reachable; the engine
already computes all of it.

#### W6. The zero-mutation invariant made provable [M]

Four verified obstacles, all of which must be settled in the spec before the test is written or
the test will be either vacuous or unpassable.

- **No batch or selective element read.** Every scan returns ids only; the only many-element
  reads are `GET /graph` and `GET /bulk/export`. To emit zero mutations a client must know
  current values before writing, so today that is one sequential GET per element or a
  whole-namespace export per poll. This is the read-side mirror of W2 and belongs with it: same
  round-trip class, same DTO family, same MCP decision (a *get_many* op on the existing
  *f8_mutate* / *f8_get* enum, not a new tool).
- **An equal-value write is observable** in modificationDate, the change feed and the WAL (see
  W2). Decide the observation channel: the invariant must be "the client issues no write call",
  which is what makes the batch read necessary.
- **DateTime ingress is not the inverse of egress.** Ingress uses *Convert.ChangeType* with
  default styles (no *RoundtripKind*); egress renders with "O". Verified by probe: a wire value
  of `2026-08-09T10:00:00.0000000Z` stores as *Kind=Local* and reads back as
  `2026-08-09T12:00:00.0000000+02:00`. The instant is preserved and persistence is faithful, so
  this is representation asymmetry rather than corruption. But a client that decides "has
  anything changed" by comparing the wire form it intends to write against the wire form it just
  read sees a difference on **every DateTime property on every sync**. It is invisible in CI and
  in the compose container (both UTC), which is precisely why a zero-mutations test would pass
  there and fail on a user's machine. Because *ToBinary*/*FromBinary* preserve Kind, the stored
  tick value is host-timezone-dependent, so moving a data volume between zones shifts the
  wall-clock reading of every DateTime property.
- **Properties are scalar-only and there is no compare-and-set.** *AllowedLiteralTypes* permits
  exactly 18 scalar types and no array or collection
  ([AllowedLiteralTypes.cs:45](../../../fallen-8-core-apiApp/Helper/AllowedLiteralTypes.cs#L45));
  arrays reach the engine only through dedicated typed routes, which is why the embedding routes
  exist. A structured value does not even survive a reload with its type: verified by probe, a
  *List&lt;String&gt;* property reads back as *JsonElement*, *TryGetProperty&lt;List&lt;String&gt;&gt;*
  then returns false, re-asserting the identical logical value rolls back, and egress reports a
  type name ingress cannot accept. *WalTransactionCodec*'s own class doc says it: "a complex
  value comes back as a JSON element on both paths". And grep for ETag/If-Match returns zero
  hits, so every write is last-writer-wins.

  **This forecloses the obvious design and forces a better one.** A set-valued or JSON-blob claim
  set would be read-modify-write with no CAS and no update primitive, so two claimants racing
  silently lose each other's claims, which means either an element nothing will ever delete or an
  element deleted while still claimed. The forced shape is **one property per claimant**
  (`claim:<instanceId>`), which makes withdrawal an idempotent *RemovePropertyTransaction*
  (it always returns true, so it is replay-safe) and makes W2's set-or-remove batch collapse a
  whole sync phase into one atomic transaction. This is a strictly better design that only
  becomes visible once the constraint is written down.

#### W7. Control plane as a typed facade, not a proxy [M]

The original gap framed this as "YARP versus hand-rolled forwarding middleware". **All four
architects rejected both options** and named a third that is already precedented twice:
[SidecarHttpClient.cs](../../../fallen-8-core-apiApp/Ingestion/SidecarHttpClient.cs), the shared
typed-facade base behind *DoclingClient* and *NlpClient*. It owns a DNS-recycling *HttpClient*
(so a restarted sidecar's new IP is picked up), endpoint normalization, a cached `GET /health`
probe surfaced on `/status`, and a test-handler seam.

A typed facade plus ordinary apiApp controllers inherits every pipeline behaviour for free (auth,
CORS under the split topology, rate limiting, problem+json, OpenAPI, the JSON source-gen gate),
adds no dependency, and does not create a route branch that silently bypasses the
engine-to-REST-to-MCP coverage gate. A forwarder inherits none of it and adds a second
serialize/deserialize hop to a surface where the measured cost is already 99.9% pipeline.

Two consequences to settle here:

- **The split topology is the unconditional default.** [env-up.js](../../../scripts/env-up.js)
  always applies [docker-compose.split.yml](../../../docker-compose.split.yml), so the apiApp is
  UI-less and CORS-allow-listed to the Studio origin. The facade must work under both that and
  the all-in-one image.
- **There is no async-job resource convention, and an existing precedent says do not invent
  one.** Long-running ingestion state is *graph* state: the document's status is a property on
  the Document vertex, swept on boot, read through ordinary document routes. The snapshot has no
  `/jobs`, `/operations` or `/runs` family. So sync-run history and any pending-review queue
  belong in the graph, where the entire list-and-read half needs **zero** new routes (Studio uses
  existing scans, agents use existing *f8_search* / *f8_get*) and only the imperative verbs need
  a route.

**Scoped credentials, RBAC, multiple API keys and per-caller quotas are rejected**; see
section 4.

### P1 - ships alongside, all small

- **W8. Index lock-leak containment [S].** *SingleValueIndex* is missing roughly twelve
  *try/finally* blocks around lock acquisition, so a throw inside a guarded region leaks the lock
  and every subsequent writer spins in an unbounded `while (...) Thread.Yield()` at full CPU
  rather than blocking. All four architects agreed this must ship **and** that it must not be
  deferred by association with the rewrite they rejected. It is mechanical.
- **W9. Truthful contracts [S].** `HEAD /trim` reassigns every element id in place
  (`survivors[i].SetId(i)`, [Fallen8.Trim.cs:128](../../../fallen-8-core/Fallen8.Trim.cs#L128)),
  is fire-and-forget, is MCP-bridged, and is documented only as "releasing unused memory". By
  contrast auto-trim is explicitly id-preserving, which makes the explicit route the sole
  renumbering path in the product. **Element ids are the only element handle the REST contract
  exposes**, so this must be stated on the route and in the MCP tool description, and any client
  binding must key on domain identifiers rather than ids. Also in scope: the two wrong doc
  comments from W2, and forward pointers on the two stale claims in
  [mcp-server](../../done/mcp-server/spec.md) (a batch endpoint now exists; analytics write-back
  now exists).
- **W10. Sidecar lifecycle in the environment [S].** Compose profile, all three *env-up.js* call
  sites (up, down, logs/status), and discoverability on `/status`, following the *F8_INGESTION*
  true-opt-out pattern exactly.
- **W11. Governance-gate hole [S].** *McpRestCoverageTest* enumerates operations from the pinned
  OpenAPI snapshot. An endpoint that is not described in the snapshot is therefore invisible to
  the gate. Close the structural hole so the engine-to-REST-to-MCP rule cannot be bypassed by
  omission.
- **W12. NL-assist graph vocabulary [S].** [prompt.ts](../../../fallen-8-web-ui/src/delegate/nl/prompt.ts)
  is a static delegate-contract prompt with **no** graph-schema injection, and `GET /config`
  carries no vocabulary. The only surface carrying observed labels and property keys is
  `GET /statistics` (sampled top-N cardinalities, API-key-gated), and it is not wired to the
  prompt. Wire it. Per the RETRAIN-LOG conventions this is data rather than drafted surface, so
  **no retrain entry**.
- **W13. Feature-path and link hygiene [S].** A markdown-link test over `features/` and the docs
  tree, which is the only machine-checkable part of the staleness problem worth having.

### P2

- **W14. Unwaited-write observability [M].** *GetTransactionState* is on the public write surface
  and *TransactionManager* deliberately retains 100,000 terminal entries so "a caller polling
  *GetTransactionState* straight after completion still finds its entry". No route exposes it,
  and *AwaitAndAccept* returns a bare 202 with no id, no Location and no body. Measured: an
  unwaited property write is 0.615 ms against 3.464 ms waited (5.6x), and 300 transactions
  enqueued with only the last awaited is 33.3 ms against 670 ms serial-waited (20x, from group
  commit). **The engine built and pays for the machinery that makes pipelined writes safe, and
  the REST surface makes it unreachable**, so any client that must confirm its writes is pinned
  to the waited floor. Returning the id on the 202 plus one `GET /transaction/{id}` unlocks it.
- **W15. Finish the index-lifecycle 3.4 reverse map [M].** *SingleValueIndex.RemoveValue* is a
  full key-set scan with a list allocation; the mirror for this class was explicitly deferred.
  It runs on the single writer, holding the index write lock, once per removed element **and**
  once per cascaded edge. At 100k keys, deleting one vertex with 20 edges costs roughly 21 ms of
  writer time with the lock held, while concurrent index writers spin at full CPU. Note the
  interaction with W3: a strong-identifier claim index is naturally one-vertex-per-key, which
  selects precisely the class whose purge is linear. Invisible in the existing benchmark, which
  measures only the dictionary class.
- **W16. Index name validation and route-shape reservation [S].** The literal-first shape from
  W3 plus a reserved-name or validation rule in *IndexFactory.TryCreateIndex*, which today takes
  the name verbatim.

### P3

- **W17. Bounded WAL growth [M].** *ResetToSnapshot* is the only truncation path and is called
  only from inside *Save*. *Fallen8DurabilityOptions* has no interval and no threshold. Replay
  re-executes every entry inline on the loading thread and is fail-stop for core data. This is a
  **documented** deferral in [hosted-durability-lifecycle](../../done/hosted-durability-lifecycle/),
  whose revisit trigger is a write-rate policy question, and an unattended continuously-writing
  first-party client is that trigger: a 60-second poll over a few hundred elements is on the
  order of 345k transactions per day, the log only grows between graceful shutdowns, boot cost is
  linear in mutations since the last save, and nothing warns. The *fallen8.wal.size* gauge exists
  and no threshold acts on it. Take that deferral's own shape: config-gated, off by default,
  enqueuing the same block. **Nothing may move checkpoint I/O off the single writer** to make
  this cheap; see section 4.
- **W18. Checkpoint and index-write spin collision [S].** *SaveIndex* is dispatched one
  *Task.Run* per index while the writer blocks on the results, and *ABucketIndex.Save* holds the
  index **read** lock across the entire in-memory serialization. Because *WriteResource* is an
  unbounded yield-spin rather than a block, a client writing index keys during a checkpoint burns
  a full core with no error and no blocked-thread signal.

## 4. Rejected outright, with revisit triggers

Recorded as decisions rather than backlog. Each was rejected by at least three of the four
lenses, and the first three were rejected unanimously.

| Rejected | Why | Revisit trigger |
|---|---|---|
| **WAL entry types for index writes** ([index-lifecycle](../../done/index-lifecycle/) 3.6) | An irreversible on-disk format commitment to make **derived** state durable, bought before its own stated prerequisite (3.5, index writes becoming single-writer transactions). The bound vector index already proves a crash-durable derived index needs no ordinal at all. Measured: nothing in the profile improves; only the rebuild is avoided, and the rebuild is milliseconds at this scale. | 3.5 has landed **and** a measured workload shows rebuild-from-element-state exceeds the boot budget; or the first index whose content is **not** derivable from element state. |
| **Retiring *AThreadSafeElement* / routing index writes through the writer** (3.5) | The stated harm is the wrong harm. The *CollisionException* cliff needs 2^31 consecutive failed acquisitions and is unreachable dead code. Measured: *AddOrUpdate* costs 1.0 microsecond **inclusive** of the lock while the HTTP request around it costs 700 microseconds; lookups plateau at 22,580/s, a ceiling set by the ASP.NET pipeline, not by two interlocked operations. Routing those writes onto the writer makes each roughly 50x more expensive and moves it onto the scarce resource. **W8's try/finally containment is not this and must not be deferred with it.** | A single index exceeds roughly 1M entries, or the observability writer-queue-depth gauge shows index-lock spin measurably delaying commits. Not "a new client shipped". |
| **A reverse proxy in the apiApp** (YARP or hand-rolled forwarding middleware) | The typed-facade precedent exists twice; a facade inherits all pipeline behaviours, adds no dependency, and keeps the coverage gate honest. A forwarder adds a second serialize/deserialize hop to a surface that is already 99.9% pipeline cost. | A sidecar must stream opaque bytes end to end to the browser (a download, or an SSE stream the apiApp cannot usefully reshape), or the sidecar surface exceeds roughly ten operations, at which point a facade becomes transcription work. |
| **Batch index-add, and an engine *AddOrUpdateRange*** | Measured: 600 lock acquire/release pairs cost 0.597 ms **total**. Collapsing them saves under a millisecond per first sync. *IIndex* is also a public plugin contract with hand-written implementers in the tree, so a new member is a breaking change. | A measured profile in which index-lock acquisition, not HTTP, is the top cost. |
| **Multi-key index lookup** | Demoted to hygiene by both PMs once W4 removes the write side and W2's equal-value clause removes the read pressure. Kept only as a candidate op on an existing route. | The cold-start resolve becomes the measured top cost of a sync. |
| **Auto-maintained property-keyed secondary indices** | Query-planner-adjacent, excluded by the *index-lifecycle* non-goal, and it would put a user-facing schema concept into the engine, which the engine deliberately does not carry. Note this is **distinct** from W4's property-**bound** index, which is opt-in at creation exactly as the bound vector index is. | A declarative index-on-property requirement arriving from two or more independent features, with a measured cost for the explicit alternative. |
| **Scoped credentials, RBAC, multiple API keys, per-caller quotas** | The all-or-nothing key is a documented, right-sized decision for a single-process self-hosted server. A sidecar owns its own caller credential exactly as *f8-mcp* already does. | A second, differently-trusted **human** consumer of the same instance: a shared read-only dashboard, or a host portal fronting instances the operator does not control. |
| **ETag / If-Match conditional writes** | Adding HTTP concurrency control to a 100-plus-operation surface for one consumer's benefit. Model the contested state as append-only graph elements instead (W6). | Two writers must mutate the **same** element property without coordination. |
| **An operations/jobs API** | The precedent says put run state in the graph (W7). | A run must be cancellable mid-flight, or must report sub-second progress, neither of which polling graph state serves. |
| **A spec-freshness detector for prose** | Unenforceable. The machine-checkable part is already covered by the pinned snapshot plus *McpContractTest* and *McpRestCoverageTest*, whose Deferrals table is itself a machine-checked registry of reasons. | None. W13's link test is the whole fix. |
| **Any durable state in a sidecar** (outbox, sync journal, local claim store, two-phase commit) | It silently reverses the ephemeral-cache decision and creates a second system of record that can disagree with the graph. | A second Fallen-8 target, or a second writer to the same namespace. |
| **Moving index population or checkpoint I/O off the single writer** | Exactly the shape [non-blocking-save](../../done/non-blocking-save/) measured and deferred. Named here because W17 invites it. | The one already recorded: tens-of-millions-of-elements graphs saved frequently. |
| **A second adjacency overlay (CSR) to speed traversals** | Rejected in [csr-adjacency/assessment.md](../../done/csr-adjacency/assessment.md); a continuously-mutated write-heavy workload is the opposite of that assessment's revisit condition. | The one already recorded. |
| **A global rate limiter on the graph-mutation and index routes** | Measured adjacency: those routes deliberately carry no *EnableRateLimiting* (the sensitive policy is on 22 actions, none of them the hot path). A global limiter would throttle the hot path this work depends on. | None; keep the current shape deliberately. |
| **A REST throughput regression gate** | [capacity-bench](../../done/capacity-bench/) deliberately ships no regression gate and that decision is right; a threshold on the noisy REST path would false-alarm. Instead add one opt-in benchmark case recording "240 property updates: 480 single round-trips versus one batch" as a published number. | None. |
| **New MCP tools for any batch operation** | The tool set is deliberately small and every schema is paid for in every agent's context on every call. Batch operations are already modelled as **ops** inside *f8_mutate*. | The control plane needs more than three verbs an agent should reach, and then it is one tool, not one per verb. |

## 5. Corrections to the original thirteen

Recorded so the audit's own errors do not propagate.

- **The batch-write gap was asymmetric, not symmetric.** A batch element-remove is a thin safe
  wrapper over an existing transaction with true rollback. A batch property-set over
  *AddPropertiesTransaction* is a 409 generator. The real defect was on the already-shipped
  singular route.
- **The claim "index writes queue on the transaction-writer thread" was wrong.** They run inline
  on the ASP.NET request thread. The round-trip count was right; the mechanism was not.
- **The index-durability framing was too narrow.** It was described as a crash problem, so its
  mitigation covered boot only. Three ordinary shipped operations destroy the index while a
  client is running.
- **The proxy question was a false dichotomy.** A third option exists and is precedented twice.
- **The 600-round-trip claim-write cost was real but was not the right thing to optimize.** The
  fix is not a faster lock or a batch index route; it is not writing index keys at all (W4).
- **The NL-assist "existing schema-hint mechanism" does not exist.** Nothing consumes a declared
  vocabulary today.
- **Two claims were understated.** Element ids are not stable across `HEAD /trim`, and the
  property surface is scalar-only with no CAS. Both change a consumer's data model, not just its
  call pattern.
- **Three of W3's four proposed changes targeted deliberate, documented decisions**, found while
  implementing them (see W3). The pattern is the same as W1's: a reviewer read a behaviour as an
  accident without checking whether the route or the code stated a reason for it. Net effect on the
  audit's credibility: of the seven P0 items, W1 and W3 both shrank substantially on contact with the
  code, and both shrank in the same direction. **Treat any remaining "make X loud" item as unproven
  until its owning contract has been read.**
- **W1's proposed boot-path change was wrong**, found while implementing it: the empty-registry
  behaviour is [save-games](../../done/save-games/spec.md) FR-8, specified and test-pinned, and the
  code the review read as a misplaced rescue path is the FR-11 migration hint. Four reviewers and both
  PMs missed it because none read the governing feature's requirements before proposing a fix to its
  boot path. The durability half was correct; see W1 for the corrected, narrower statement. **Lesson
  for the remaining items: check the owning feature's FRs before calling a behaviour a bug.**

## 6. Impact on existing features (cross-feature sweep)

- **[index-lifecycle](../../done/index-lifecycle/)**: W3, W4, W8 and W15 land inside its theme.
  Its deferrals 3.5 and 3.6 stay deferred, with the triggers in section 4 now recorded explicitly
  rather than implicitly. Its Phase 2 "SingleValueIndex/RegExIndex mirror deferred" note is what
  W15 closes.
- **[element-embeddings](../../done/element-embeddings/) and [vector-index](../../done/vector-index/)**:
  W4 generalizes their bound-projection mechanism. The embedding behaviour must not change; the
  invariant "the live projection equals a load-rebuild" is extended, not weakened.
- **[save-games](../../done/save-games/) and [hosted-durability-lifecycle](../../done/hosted-durability-lifecycle/)**:
  W1 fixes the registry write and the boot branch; W17 takes up the latter's own deferred
  threshold-checkpoint shape.
- **[crash-durability-hardening](../../done/crash-durability-hardening/)**: D5 stays deferred.
  W1 is explicitly **not** D5.
- **[transaction-atomicity](../../done/transaction-atomicity/)**: W2 adds a transaction and must
  extend its test family, including a WAL replay case for ordinal 19.
- **[api-error-contract](../../done/api-error-contract/) / [api-error-envelope](../../done/api-error-envelope/)**:
  W3's "loud missing index" and W2's honest failure mapping are within their remit.
- **[api-security-boundary](../../done/api-security-boundary/)**: unchanged. Scoped credentials
  rejected with a trigger.
- **[mcp-server](../../done/mcp-server/) and [mcp-followups](../../done/mcp-followups/)**: new
  routes are ops on existing tools, never new tools. *McpBridgedEndpoints*, the coverage test and
  the contract test all move. W11 closes the gate's structural hole. W9 corrects two stale claims
  in the mcp-server spec.
- **[unstructured-ingestion](../../done/unstructured-ingestion/) / [semantic-layer](../../done/semantic-layer/)**:
  the biggest downstream beneficiaries. W2 retires the recorded remove-then-set limitation in
  *DocumentIngestionService*; W4 retires its hand-rolled entity-index sweep. Both are duplication
  removals, not new behaviour.
- **[observability](../../done/observability/) / [fleet-observability](../../done/fleet-observability/)**:
  W5 surfaces on REST what today exists only as an OTel gauge. The tag-hygiene invariant is
  untouched.
- **OpenAPI snapshot**: regenerate for W2, W3, W4, W5, W6, W7, W14. Expect additions only.
- **JSON source-gen gate**: every new DTO needs an *AppJsonContext* registration and
  *JsonSourceGenParityTest* coverage.
- **Studio**: W5's durability signal and W7's facade surface. Any UI change recaptures its
  screenshots per the standing rule.
- **NL-assist**: W12 only, and **no RETRAIN-LOG entry** (data, not drafted surface).
- **Architecture diagrams**: only if W7 adds a deployable to the picture; then both the root
  README diagram and the docs-site architecture page, in the same change.
- **[capacity-bench](../../done/capacity-bench/) / [write-path-throughput](../../done/write-path-throughput/)**:
  one opt-in published benchmark number for the batch-versus-loop delta. No gate. The latter's
  deferred persistent-append-handle item is worth re-examining once a sequential client is a
  first-class citizen: measured, the per-group file open and close is roughly 0.6 ms, about 17%
  of a waited REST write and 32% of the WAL portion, and group commit hides it only under
  concurrency.

## 7. Test expectations

Every P0 gets a test that **fails today**. That is the acceptance bar for this feature.

- **W1**: a zero-length registry boots loud, not empty; a discoverable checkpoint with no
  registry entry is adopted; the registry file is fsynced before rename.
- **W2**: an update through the singular route no longer returns 202 while discarding; a batch
  set-or-remove is atomic across a value change; ordinal 19 round-trips through WAL replay; an
  equal-value write bumps no modificationDate, emits no change event and appends no WAL frame.
- **W3**: an add of an identical (key, element) pair does not double the bucket; a lookup and a
  write against a deleted index are distinguishable from a genuine miss; the three destruction
  paths each raise the resync signal; an index named *vector* is reachable on the new routes.
- **W4**: an index rebuilt from element state equals one maintained incrementally, over creation,
  property write, removal and reload. This is the bound vector index's existing invariant applied
  to the new mode.
- **W5**: a write into a degraded WAL is distinguishable from a durable one; `/status` reports a
  truncated replay.
- **W6**: an unchanged source issues zero write calls, asserted on the **call** channel; a
  DateTime property round-trips identically under a non-UTC host timezone (the test must set it,
  since CI and the container are both UTC).
- **W7**: the facade works under both the all-in-one and split topologies; no route bypasses the
  coverage gate.
- **W8**: a throw inside a guarded region does not leak the lock.

## 8. Keep (do not regress)

- **The single-writer invariant.** Nothing here routes new work onto the writer beyond what W4's
  precedent already does.
- **No new on-disk WAL ordinals for derived state.** One ordinal is added, for element property
  state (W2), which is not derived.
- **The engine stays free of hosting and schema concepts.** W4's binding is a property key, not
  a user-facing schema declaration.
- **The all-or-nothing API key**, and the deliberate absence of rate limiting on the hot path.
- **The small, token-frugal MCP surface**: ops on existing tools, never new tools.
- **`features/done/` specs are historical records** and are not rewritten. W9 adds forward
  pointers only.
- **The repo's gates**: warnings as errors, convention tests, the pinned snapshot, the coverage
  gate, the link-checked docs build.
