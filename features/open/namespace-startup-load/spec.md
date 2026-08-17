# Namespace startup load - Specification

> **Status:** Open. Branch-only workflow (`feature/namespace-startup-load`), no GitHub issue or PR
> unless asked. This fires the revisit trigger [graph-namespaces](../../done/graph-namespaces/)
> named for itself and takes the **declarative** half of it: an operator chooses which namespaces
> a boot loads. Idle eviction stays out (see Non-goals).

## 1. Why

A Fallen-8 boots every cataloged namespace eagerly and loads each one's newest checkpoint
**sequentially** (`Services/DurabilityLifecycleService.cs`), so the slowest namespace in a fleet
sits on the critical path of every start, and its heap stays resident whether anyone reads it or
not. [graph-namespaces](../../done/graph-namespaces/spec.md) predicted this request in its own
non-goals ("No lazy engine loading / idle eviction. Trigger: deployments with thousands of live
namespaces, or boot time / memory pressure"). This is that trigger arriving as an operator
preference rather than as an incident, which is the good version of it.

## 2. The honest cost model

Not loading a namespace saves **retained heap** and **sequential load latency**, and close to
nothing else:

- about 88.0 B per vertex and 114.1 to 171.8 B per edge, plus about 4.1 KB per element for a
  1024-dimension vector index (measured, [capacity-and-performance](../../../docs/src/content/docs/capacity-and-performance.md));
- one writer thread per engine, which is cheap;
- **no** persistent write-ahead-log handle: every append opens, fsyncs and closes
  (`fallen-8-core/Persistency/WriteAheadLog.cs`). `Fallen8NamespacesOptions`' XML doc claims each
  namespace holds "an open write-ahead log"; that is wrong and this feature corrects it rather
  than repeating it.

Boot-time savings are therefore **claimed only once measured**: the plan adds a load row to
`fallen-8-bench`. Until it exists, the spec states heap and says the latency claim is unmeasured.
That is deliberate: a feature justified by a number nobody produced is how the WAL-handle
overstatement got into the codebase in the first place.

## 3. What a user defines, and where it lives

A **persisted, per-namespace tri-state**, editable at runtime, inheriting a global default. This is
byte-for-byte the shape `pluginRegistration` already ships (`Namespaces/NamespaceCatalog.cs`), so
the feature adds a field to a pattern rather than a pattern.

The owner asked to *define* this as a user, and [instance-config](../../done/instance-config/spec.md)
makes every `Fallen8:*` key startup-only and non-mutable. A configuration-only answer would
therefore mean nobody can define anything without a redeploy, which is not the request.

One operator escape hatch exists as a **mode, never a name list**: namespace names are mutable
while ids are the on-disk key (`TryRename` keeps the id), so a configured name list silently
changes meaning after a rename, and an id list is unusable by humans. The mode also matters
because the catalog is the only inventory and a malformed catalog **aborts the process** - without
a config-side override, an operator who excluded the wrong namespace would have to hand-edit the
one file whose malformation takes the server down.

## 4. The contract

| # | Rule |
|---|------|
| 4.1 | **Catalog:** `loadOnStartupEnabled` on `NamespaceCatalogEntry`; `null` means inherit. No document-level slot, because `default` is not overridable (4.9). |
| 4.2 | **Config:** `Fallen8:Namespaces:LoadOnStartup` (default `true`) is what `null` inherits. `Fallen8:Namespaces:StartupLoadMode` is `Catalog` (default), `All` (ignore every exclusion - the cold-boot lever) or `DefaultOnly` (load nothing but `default`, for when the selection itself is what is broken). |
| 4.3 | **Boot is loud:** one log line per namespace saying loaded or skipped **and why**, in the register the existing skipped-entry error already uses. A selection that loads nothing is loud, never a silent no-op. |
| 4.4 | **Residency is a property of the entry, not of membership.** A not-loaded namespace stays in the collection with no engine, so the catalog, name reservation, the quota, enumeration, droppability and the namespace-info gauge keep working unchanged. This is the load-bearing structural decision; section 7 says what breaks without it. |
| 4.5 | `Namespace.Engine` **throws** `NamespaceNotLoadedException`; `IsLoaded` and `TryGetEngine(out …)` are the branching accessors. The repo has no nullable-reference analysis, so a throw is the only fail-safe default: a dereference site the sweep missed fails diagnosably instead of `NullReferenceException`-ing. |
| 4.6 | **REST read:** a third `NamespaceState` value `notLoaded`, and `vertexCount`/`edgeCount` become **absent** rather than `0`. Zeros are not an option: the Studio dashboard branches on `vertexCount === 0` to replay the first-run walkthrough, so zeros would greet an operator with "get started" over a namespace that holds data. |
| 4.7 | **REST refusal:** `503` problem+json from one home (`NamespaceProblems.NotLoaded`), enforced pre-action in `NamespaceValidationFilter` with an exception-filter twin for the off-request path, carrying `namespace` and `namespaceState: "notLoaded"`. **Not 404** - see 7.1. |
| 4.8 | **Activation:** `POST /ns/{name}/activate` loads a not-loaded namespace into the running process. Idempotent, rate-limited, and it does **not** change the persisted policy. Named *activate* because `/ns/{ns}/load` already means "restore a checkpoint". |
| 4.9 | The reserved `default` namespace **cannot** be excluded, by catalog or by config. Every bare URL aliases it, so a Fallen-8 whose `default` is absent has no coherent answer for most of its own surface. |

## 5. The data-loss guard (normative)

**A namespace with no resident engine is never a member of a save.** Not on shutdown, not in
`PUT /save/all`, and `PUT /save` addressing one refuses.

This is normative because the trap is real and verified, not hypothetical. `StopAsync` is gated
only on `Volatile || !SaveOnShutdown` and then enqueues a `SaveTransaction` for **every** namespace
in the snapshot, with no emptiness check and without consulting the engine's own
`_walAwaitingPairedLoad` state. Two losses follow:

1. **The write-ahead log is destroyed, unrecoverably.** `Fallen8.Save` calls
   `_wal.ResetToSnapshot(...)`, which rewrites the log as a **header only**. Every post-checkpoint
   delta it held is gone, and no other artifact carries it. The bitter detail: the engine already
   computes the exact predicate that would prevent this (`_walAwaitingPairedLoad`) and `Save`
   clears it without ever reading it.
2. **The boot chain is poisoned.** Checkpoints are versioned rather than overwritten, so the real
   file survives, but an empty graph still produces a *complete, loadable* checkpoint, and
   `RegisterAll(…, "shutdown")` inserts it as the **newest** entry containing that id. The next
   boot loads the empty one. Nothing is logged.

Three enforcement points, all reading **live residency**, so a future runtime unload needs no
second guard: shutdown save, `PUT /save/all` (skipped, reported as skipped rather than as
failures), and `PUT /save` (refuses with 4.7).

Consequence, documented rather than hidden: the shutdown spanning entry then contains a strict
**subset** of the Fallen-8, so "the newest entry is my whole Fallen-8" stops being true.

## 6. What does not change

**No engine change.** The engine has no namespace concept; the only namespace-shaped thing crossing
the seam is the opaque host-assigned `metricsScopeId`. "Do not load this namespace" reduces to "do
not call `new Fallen8`", a decision taken entirely inside the apiApp - the same invariant
graph-namespaces shipped under. An optional engine hardening (`Save` refusing while
`_walAwaitingPairedLoad` is set, which would make this loss class impossible from any caller) is
deliberately **deferred**: the same re-anchor path is a legitimate "bootstrap onto a foreign
snapshot" flow today that warns rather than refuses. *Revisit trigger:* a second caller reaching
`Save` on an unpaired log.

## 7. Failure modes, stated honestly

1. **A 404 would send the operator to "Recreate (empty)".** The Studio client turns any 404
   problem+json carrying a string `namespace` extension into the recover state, whose button
   creates the namespace empty. Telling a user their populated graph is missing and offering *that*
   is the destructive wrong turn, which is why 4.7 is a 503. For the same reason `GET /ns` must
   **list** not-loaded namespaces: hiding them reaches the same recover state by absence.
2. **An excluded namespace with a rotted checkpoint no longer aborts the whole process.** A
   deliberate scoping of save-games FR-9's loud abort to namespaces actually selected for load, so
   files under a namespace nobody asked for cannot keep the server down.
3. **The spanning save entry becomes a subset** (section 5).
4. **A namespace excluded and then forgotten looks empty to nobody**, because it reports
   `notLoaded` rather than zeros (4.6).
5. **Without 4.4 the catalog entry would be erased** by the next metadata write anywhere in the
   Fallen-8: the catalog writer rebuilds the whole document from the collection it is handed, so an
   entry outside it is dropped, stranding that namespace's directory and WAL unreachable and
   un-droppable, and freeing its name to be re-minted under a second id over real data. (A
   narrower version of this bug exists today: the constructor tells the operator to repair a
   skipped entry, and the next catalog write deletes the entry it just named. This feature fixes
   that by construction.)

## 8. Decisions taken on questions the evidence left open

| Question | Decision | Rejected, and why |
|---|---|---|
| Is runtime activation in v1? | **Yes**, as its own phase after the policy works. Without it the only way back from a wrong exclusion is edit-and-restart, so the first mistake costs an outage; with it, exclusion also becomes a deliberate posture (boot fast, activate on demand). | Policy-only v1: cheaper, but it makes every refusal message, Studio branch and doc say "restart to get your data back". |
| What does the factory reset (`/tabularasa/all`) do to a not-loaded namespace? | **Drops it**, and names it in the response among what was dropped. The reset's contract is explicit and already has no undo. | Sparing them: a documented factory reset that silently leaves data behind, which the next boot resurrects after the operator believes they erased it. That is the worse surprise. |
| Does restoring a save game into an excluded namespace flip it? | **Activates it for this process AND flips the persisted policy to enabled**, reporting both in the response. | Activate only: the restored data goes invisible again at the next boot. Refuse: dead-ends a legitimate recovery during an incident behind "change policy, restart, restore". |

## 9. Impact on existing features (mandatory sweep)

| Feature / layer | Impact | Action |
|---|---|---|
| [graph-namespaces](../../done/graph-namespaces/) | Supersedes "boot is eager" and its "no lazy engine loading" non-goal; the per-namespace fixed cost now applies to **loaded** namespaces only | Edit its LIVING README; cite the spec's non-goal as superseded rather than rewriting the historical spec |
| [save-games](../../done/save-games/) | FR-9's whole-process abort scopes to selected namespaces; the spanning entry becomes a subset; restore into a not-loaded target follows 8.3 | Guard, tests, and the `save-games` docs page |
| [hosted-durability-lifecycle](../../done/hosted-durability-lifecycle/) | Both the start loop and the shutdown save loop become residency-aware; its "never silently degrade" posture governs how loud the selection is | Code + tests |
| Engine (`fallen-8-core`) | **None** (section 6) | Revisit trigger recorded |
| REST / [api-error-contract](../../done/api-error-contract/) | A new reachable 503 on every namespace-scoped operation, via the global filter | One documented home, per 4.7 |
| OpenAPI snapshot | Schema additions plus one new path (activation) | `powershell -File scripts/update-openapi-snapshot.ps1`, review the diff |
| MCP (engine → REST → MCP) | **The gate will not protect us here:** `McpRestCoverageTest` keys on `METHOD /path`, so a new *field* on the already-bridged `PATCH /ns/{name}` is invisible to it. Activation, being a new path, does trip it | `f8_overview` reports residency (read tier) as the prerequisite; activation joins the admin tier; the policy field is bridged with its own reasoning, not by omission |
| F8 Studio | A third namespace state to render, absent-capable counts, the policy affordance, and the switcher tag | Reuse the existing degraded-state vocabulary; no new visual language |
| [studio-embeddable](../../done/studio-embeddable/) | `lockNamespace` must also hide the policy control: an embed scoped to one graph must not re-plan the host's boot | One condition, pinned by a test |
| Docs site | `namespaces`, `save-games` (its Startup table is the most wrong section today), `architecture`, `running`, `capacity-and-performance`, `observability`, `studio`, `troubleshooting` | Amend in place; **no new page** - a separate page would be a third home for the namespace story |
| Screenshots | The Namespaces table gains a column; it is fully in-viewport on `screen-connect.png` | Recapture that one; the not-loaded switcher tag never occurs in a capture by construction |
| NL-assist fine-tune | No delegate kind, fragment surface, snippet or prompt changes | No `RETRAIN-LOG.md` entry |

## 10. Non-goals (right-sized, with revisit triggers)

- **No idle eviction and no unload-on-demand.** Unloading a *live* engine has to answer for
  in-flight writes, the change feed and the shutdown save, and none of that is needed to choose a
  boot set. *Revisit trigger:* memory pressure from namespaces that were loaded but went cold.
- **No parallel boot loading.** Loading the selected namespaces concurrently would attack the same
  latency, and is orthogonal to choosing them. *Revisit trigger:* a measured boot row showing the
  selected set still dominates start time.
- **No per-namespace resource budgets.** *Revisit trigger:* a fleet where one namespace's load
  starves the others.
- **No config name list** (section 3).
