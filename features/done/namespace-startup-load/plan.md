# Plan - Namespace startup load

Six phases, each independently shippable and testable, in dependency order. **The guard ships
before anything can be excluded**; that ordering is the whole point, not a preference. Branch:
`feature/namespace-startup-load`.

**Status: all six phases landed (2026-08-17).** Each phase keeps its original text, with an "as
landed" note where reality differed; nothing above a note is rewritten to look prescient. What is
still open is listed at the bottom, under "Left open".

- [x] Phase 0 - residency and the data-loss guard
- [x] Phase 1 - the policy and the boot decision
- [x] Phase 2 - the REST surface
- [x] Phase 3 - activation
- [x] Phase 4 - Studio
- [x] Phase 5 - discoverability, docs and the measurement

## Phase 0 - Residency and the guard, with residency permanently true

Introduce residency as a concept and defuse the shutdown trap while nothing can yet *be*
non-resident, so the most dangerous code lands with tests and zero blast radius.

- `Namespaces/Namespace.cs`: `IsLoaded`, `TryGetEngine(out Fallen8)`, and `Engine` throwing
  `NamespaceNotLoadedException` when absent.
- `Namespaces/NamespaceNotLoadedException.cs` (new, twinning `UnknownNamespaceException.cs`) and
  `NamespaceProblems.NotLoaded(name)`.
- `Services/DurabilityLifecycleService.cs`: the start loop and the shutdown save loop skip
  non-resident namespaces, one informational line per skip naming the namespace and stating that
  its checkpoint and WAL are untouched; skipped namespaces are excluded from `RegisterAll`.
- `Controllers/AdminController.cs`: `SaveAll` skips them and reports them as **skipped** (today an
  engine-less namespace would be counted as a failure in a 500 body); `Save` refuses per 4.7.
- `Ingestion/DocumentIngestionService.cs` interrupted-document sweep, and
  `Namespaces/Fallen8Namespaces.cs` `Dispose` / dispose-under-gate / `TryDrop`: tolerate an absent
  engine, and `TryDrop` must still delete the WAL and the directory.
- **Verify:** `NotLoadedNamespace_IsNotSavedOnShutdown_AndItsWalAndCheckpointSurvive` (the single
  most important test in the feature: no new checkpoint file, the registry's newest entry for that
  id unchanged, the WAL byte-identical and **not** header-only, and a later boot with it included
  restores the original counts); `SaveAll_SkipsNotLoadedNamespaces_AndReportsThemAsSkipped`;
  `Save_AddressingANotLoadedNamespace_Refuses`; `Drop_OfANotLoadedNamespace_DeletesItsWalAndCatalogEntry`.
  The existing `ShutdownSave_SpansAllNamespaces_AndTheNextBootRestoresThem` is deliberately renamed
  to `..._SpansEveryLoadedNamespace_...` - an intentional contract change, recorded as such, not a
  test that broke.

### Phase 0 as landed, and one thing it taught

Two corrections to this plan, recorded rather than quietly absorbed:

- **`PUT /save` needed no code of its own.** It resolves through `AddressedFallen8`, so the
  throwing `Engine` accessor plus the new exception filter already produce the 503. The refusal is
  the throw, not a hand-written branch - fewer sites, same contract.
- **The first version of the guard's test proved nothing, and the mutation probe caught it.** With
  the explicit skip disabled the test stayed green, because the throwing accessor lands in the
  shutdown loop's per-namespace `catch` and skips too. The outcome was doubly protected but not
  *pinned*: the test could not tell the guard from its absence. It now also asserts the **clean
  informational path** (an `Information` skip line, and no `Error` naming that namespace), which
  fails correctly when the guard is removed and protects a real property - an operator must not see
  an error on every clean shutdown for a namespace they deliberately excluded.

**Ordering correction for Phase 1:** `NamespacesController.ToRest` reads `ns.Engine.VertexCount`
directly, so the moment Phase 1 makes exclusion reachable, `GET /ns` would answer 503 for the whole
list instead of listing the namespace. `NamespaceState.NotLoaded` and the absent-capable counts
therefore move **into Phase 1**; Phase 2 keeps the refusal filter, the PATCH field, `/status` and
the snapshot. Phase 1 is not shippable without them.

## Phase 1 - The policy and the boot decision (no REST, no UI)

- `Namespaces/NamespaceCatalog.cs`: `loadOnStartupEnabled` on the entry, re-emitted by the catalog
  writer.
- `Configuration/Fallen8NamespacesOptions.cs`: `LoadOnStartup`, `StartupLoadMode`, and the
  correction of the "an open write-ahead log" doc string; `appsettings.json`.
- `Namespaces/Fallen8Namespaces.cs`: the catalog loop decides **before** constructing an engine.
- `Services/DurabilityLifecycleService.cs`: save-games FR-9's abort scopes to selected namespaces.
- **Verify:** `Boot_SkipsAnExcludedNamespace_AndConstructsNoEngineForIt`;
  `Boot_LogsOneLinePerLoadedAndSkippedNamespace`;
  `Boot_DoesNotAbort_WhenAnExcludedNamespacesCheckpointIsMissing`;
  `Catalog_RetainsNotLoadedEntries_AcrossCreateRenameDrop` (the R2/R5 regression pin);
  `Create_OfANotLoadedNamespacesName_Conflicts`; `StartupLoadMode_All_IgnoresExclusions`;
  `StartupLoadMode_DefaultOnly_LoadsOnlyDefault`; `Default_CannotBeExcluded_ByCatalogOrConfig`.
- Ships as an operator capability through a hand-edited catalog plus config. Already useful.

### Phase 1 as landed

- The decision reports a **reason string** per namespace (`IsSelectedForStartupLoad(entry, out
  reason)`), which is what makes 4.3's "and why" a fact rather than an intention: the line names the
  namespace's own policy, the inherited default, or the mode. A boot that skips something also logs
  one summary line, and logs it at `Warning` when nothing but `default` loaded, because that shape is
  usually a stale mode rather than a choice.
- `default` carries a **fixed** `LoadOnStartupEnabled = true` rather than an inherited null, so the
  REST surface reports the policy actually in force for it instead of one it does not follow (4.9).

## Phase 2 - The REST surface

- `NamespaceState.NotLoaded`; `Controllers/Model/NamespaceREST.cs` gains
  `loadOnStartupEnabled` and makes the counts absent-capable; `NamespacesController.ToRest` works
  without an engine.
- `PATCH /ns/{name}`: the third field joins **both** the "supply at least one field" guard **and**
  the up-front validation - otherwise a rename commits and then reports rejected. The update lands
  as one `TryUpdate` under the write lock with a single catalog write (the precedent the
  audit-defects report already set for this controller).
- `NamespaceValidationFilter`: the third branch, so the refusal lands before any action touches an
  engine; plus the exception-filter twin for the off-request path.
- `GET /status` reports residency and omits derived numbers; the change-feed 503 detail gains the
  third cause; `AppJsonContext` registration.
- **Verify:** the exact 503 body pinned, and the existing 404 body asserted **byte-unchanged**; a
  PATCH round-trip; the source-gen parity test; regenerate the OpenAPI snapshot and review the
  diff. MCP in this phase: `f8_overview` reports residency (read tier), and the policy field's
  bridging is recorded with reasoning because the coverage gate cannot see a new field.

### Phase 2 as landed, and two orderings the plan got wrong

- **The Studio guards could not wait for phase 4.** The moment this phase let a real server answer
  `state: "notLoaded"` with absent counts, the SPA was already reading them: the count formatter
  threw on an absent count and the namespace-scope branch would have rendered the recover state. So
  `types.ts`, `format.ts` and the not-loaded rendering guards landed **here**, with the contract that
  made them reachable, and phase 4 built the visible affordances (the panel column, the switcher tag,
  the `NamespaceScope` branch) on top. Splitting it that way was not a preference either: shipping
  the contract before the client tolerates it is how a client crashes on a valid response.
- **An interim refusal stood in for decision 8.3 for one phase.** With no activation yet, a save-game
  restore addressing a not-loaded member had to refuse (`503`), because the alternative was a restore
  that silently skipped one member. Phase 3 **superseded** that refusal with 8.3 as specified
  (activate, flip the policy, report both), and the interim `503` came back out of the route's
  declared responses. It is recorded here rather than erased because the refusal shipped, briefly,
  and a reader of the OpenAPI history will find it.
- **`GET /status` needed an exemption from 4.7**, and exemptions rot silently, so the waiver is a
  declared attribute (`[NamespaceResidencyOptional]`) rather than a special case inside the filter,
  and a new convention test (`NamespaceResidencyConventionTest`) fails the suite if a namespace-scoped
  action neither refuses nor carries the attribute. The probe reports `namespaceState` and leaves
  every engine-derived field null, counts and index inventory included.
- **`PATCH` forced the collection's write path into one atomic update.** `TryRename` and
  `TrySetPluginRegistration` each wrote the catalog, so a two-field PATCH wrote it twice and a second
  failure left the first field persisted under a rejection. They collapsed into
  `TryUpdate(name, NamespaceUpdate …)`: one lock, one catalog write, one rollback, and an `IsEmpty`
  that is the single home of the "supply at least one field" question.

## Phase 3 - Activation

- `POST /ns/{name}/activate`: idempotent, rate-limited, does not touch the persisted policy.
- The load routine is extracted out of the hosted service, so its contract becomes "fail this
  request" rather than "abort the process", behind a **per-namespace** load gate - never the
  collection write lock, which a seconds-long load would hold against every create, rename and
  drop in the Fallen-8.
- `fallen-8-mcp` admin tier + `McpBridgedEndpoints`.
- **Verify:** activation restores the checkpoint and the WAL tail;
  `ConcurrentActivation_OfTheSameNamespace_ConstructsExactlyOneEngine` (the two-engines-on-one-WAL
  regression, R5); activation of a loaded namespace is idempotent; activation leaves the policy
  unchanged; `McpContractTest` and `McpRestCoverageTest` green after the snapshot regen.

### Phase 3 as landed

- **The whole load path became asynchronous**, which this plan did not anticipate. The apiApp forbids
  `WaitUntilFinished()` outside the hosted service (`CodeQualityTest`), and widening that allowlist
  for a request-path load would pin a thread-pool thread for seconds. So the routine returns a
  `Task`, the load awaits `TransactionInformation.Completion`, and
  `DurabilityLifecycleService.StartAsync` is now an `async` method. A monitor cannot be held across an
  await, which is why the per-namespace gate is a `SemaphoreSlim` rather than a `lock`.
- **The gate is keyed by the immutable namespace id, not the name.** The id is what the
  write-ahead-log path derives from, so it is the identity that must be unique; removing an entry for
  a LIVE namespace under contention is precisely how two callers would end up holding two different
  gates, which is the R5 race itself. The residency re-check lives INSIDE the gate, so the loser of a
  race returns `AlreadyLoaded` without constructing anything. Two review follow-ups here: a DROP now
  removes that namespace's gate (its id is retired for good and the entry is marked retired first, so
  a racing activation holding the old gate can only observe the retirement) which bounds the
  dictionary by the live namespaces rather than by everything that ever existed; and the gate's body
  re-checks that retirement BEFORE it touches the filesystem, so a drop that wins the race no longer
  has its directory and a fresh write-ahead log re-created behind it.
- **`NamespaceLoader` is the extracted home**, with the boot and activation differing only in what
  they do with its report: the boot turns a failed load into a process abort (save-games FR-9),
  activation turns the same report into one failed request. It also owns `RestoreFrom(location)`, the
  variant the save-game restore uses so an activated member is restored from THAT entry rather than
  from the namespace's own newest checkpoint.
- **Decision 8.3 lands with the policy write FIRST**, in its own pass after every member resolved. A
  cheap catalog write that fails before anything was restored keeps "nothing was restored" true; the
  other order can only ever produce a 200 claiming a policy change that did not persist. Both
  orderings are pinned rather than trusted: the write-before-load half on the LOG ORDER inside
  `EntryRestore_ContainingANotLoadedNamespace_…` (the end state cannot tell the two orders apart),
  the pass-after-the-resolve-loop half by
  `EntryRestore_WhenARecreateFails_ActivatesNothing_AndFlipsNoPolicy`, which fails a member's
  recreate with the namespace quota. Both were re-run against their mutation.
- **Activation REFUSES one case with 409** (added in review): a namespace whose directory holds
  checkpoint files that no registered save game contains. It had been answering success while
  publishing an empty engine beside those files, which is the shutdown-path data loss of spec §5
  reached through activation - once the engine is published the namespace is resident, so the guard
  stops protecting it. The distinction is a third `NamespaceRestoreOutcome`, so the boot and the
  activation can answer the same situation differently (the boot proceeds: its engine is already
  published, and that is the state the checkpoint-load adoption runs from). Pinned by
  `Activation_WithUnregisteredCheckpointFiles_Refuses_AndPublishesNoEngine` (refusal, no engine
  published, and every file in the directory byte-identical afterwards) with
  `Activation_WithNothingToRestore_Succeeds_AndReplaysTheWriteAheadLog` holding the genuinely-empty
  case on the success side; verified against the mutation that removes the distinction.
- **MCP:** activation is an `op` on the existing admin-tier `f8_admin`, not a new tool, since every
  tool's schema is paid for in every agent's context. It is admin rather than write-tier
  `f8_namespace` because it is durability work, not part of the create/rename/drop lifecycle.

## Phase 4 - Studio

- `src/api/types.ts` (third state, absent-capable counts), `endpoints.ts` (the PATCH field,
  activation).
- `NamespacesPanel`: one "at startup" column (load / skip / inherit) with the existing
  "takes effect on restart" guidance register.
- `NamespaceSwitcher`: the existing faint-dot plus tag slot, so no new visual language; the count
  formatter must stop throwing on an absent count.
- `NamespaceScope`: a third branch (`namespace-not-loaded`) in prose register, **not** the warn
  palette, offering activation - and no buttons under `lockNamespace`.
- **Verify:** extend `tests/namespaces.test.tsx` (the tag-precedence chain, dash counts, the new
  column); a **new** `NamespaceScope` describe (nothing renders that component today - do not claim
  to extend a test that does not exist); `api-contract.test.ts`; `mount-seam.test.tsx` for the
  locked embed; e2e scenario for the policy round trip (it cannot assert the startup effect, which
  needs a restart and therefore belongs in the MSTest suite).
- Recapture `screen-connect.png`, with the capture spec creating one namespace carrying a
  non-inherit policy so the new column shows a real value.

### Phase 4 as landed

- **It ran in parallel with phase 3, so the activation button is not there.** When the branch was
  written `POST /ns/{name}/activate` did not exist, and inventing a client for an absent route would
  have failed the api-contract sweep and 404'd on a click. The `NamespaceScope` branch therefore
  offers the policy-plus-restart way out and carries a TODO naming the endpoint, the insertion point
  (inside the `!lockNamespace` guard) and the sentence to delete when it is wired. The route exists
  now, so this is a real follow-up rather than a design choice; see "Left open".
  **Followed up in the same branch:** the button is wired and the TODO is gone, on exactly the terms
  the TODO named; "Left open" records what changed with it, docs included.
- **The switcher tag puts `not loaded` AHEAD of `active` and `bare-URL alias`** in the single tag
  slot, a visible change to an existing precedence chain. The reasoning is that the other two are
  already carried elsewhere (the trigger names the active namespace, the panel names the alias) while
  residency is carried nowhere else and decides whether any screen can answer at all.
- **The policy control is its own endpoint function**, not a parameter on `renameNamespace`: the
  server applies a PATCH body atomically, so a shared caller would have to send a `name` it was not
  asked to change, and a stale one would rename the namespace as a side effect of a policy edit.
- **`lockNamespace` hides the way out, not just the control**, in both the panel and the not-loaded
  branch, while the explanation itself is never hidden - an embed's user still learns why the screen
  is empty (studio-embeddable).
- The mutation-probe pass found one test that could not tell a guard from its absence: the
  `NamespaceScope` assertions passed while the inventory query was still pending. Fixed with a
  helper that waits for the query cache before asserting, then re-verified against the mutation.

## Phase 5 - Discoverability, docs and the measurement

- Amend in place: `namespaces` (the primary home, with a new startup-load subsection),
  `save-games` (its Startup table is the most wrong section today, plus the subset-entry note),
  `architecture`, `running`, `capacity-and-performance`, `observability`, `studio`,
  `troubleshooting`; `features/done/graph-namespaces/README.md` as the living doc; the root README
  namespaces entry amended in place. **No new page and no sidebar change.**
- Add the `fallen-8-bench` load row, so the boot-time claim in the spec becomes measured rather
  than asserted.
- **Gates:** `dotnet build` (warnings are errors), `dotnet test fallen-8-core.sln`, the convention
  tests, the OpenAPI snapshot diff reviewed, `npm --prefix docs run build` (link-checked), the web
  UI typecheck + vitest, and an explicit statement that `tools/browser-probe` was not
  required because nothing under `fallen-8-core` changed.

### Phase 5 as landed

- **Pages amended:** `namespaces` (the primary home: the per-namespace cost sentence, the entry-field
  table, the management table with `activate`, and a new `## Startup load` section that every other
  page links to by fragment), `save-games` (the Startup table, the `/save/all` subset rule, the
  restore's activate-and-flip behaviour, the fourth `500` cause, and the `walEnabled` row that
  carried the "open write-ahead log" claim), `capacity-and-performance` (a new
  `capacity:load` region plus a knob row), `architecture`, `running` (the two config keys and the
  escape hatch), `observability` (`namespaceState` on `/status`, `/statistics` refusing, no engine
  instruments for a namespace with no engine, the `readyz` wording), `studio`, `troubleshooting` (a
  new 503 entry), and `mcp-server`, which the sweep in the spec had missed. No new page, no sidebar
  change.
- **The bench grew a fifth family** (`load`), the only OPTIONAL one in the report schema, for a stated
  reason: it was added after schema major 1 shipped, and requiring it would make every report
  recorded before it unreadable rather than merely incomplete. The renderer says a run predates the
  measurement instead of rendering a zero, which would read as "loading a namespace is free". The
  spec's section 2 records what a local verification run measured and why no number from it is
  written into the page. The page's load-versus-save conclusion is generated INSIDE that region and
  computed from the paired rows, so an empty region cannot publish a comparison nobody measured.
- **Which gates actually ran on this branch**, since the list above says more than happened:
  `dotnet build fallen-8-core.sln` (clean, warnings are errors), the full `dotnet test` suite, the
  convention tests inside it, `npx tsc -b` and `vitest run` in `fallen-8-web-ui`, and
  `npm --prefix docs run build` (link-checked). **Playwright did NOT run**, and the feature adds no
  e2e scenario - see "Left open"; the only Playwright spec it touches is the `screen-connect.png`
  capture spec, which is a screenshot fixture rather than a test and was run by hand for the
  recapture. `tools/browser-probe` was not required: nothing under `fallen-8-core` changed.

## Left open

**Closed since**: the Studio activation button, which was the one item that had a route waiting for
it. `POST /ns/{name}/activate` is now wired end to end - `activateNamespace` in
`src/api/endpoints.ts`, `NamespaceActivationREST` in `types.ts`, and an **Activate now** primary
action inside the `!lockNamespace` guard of the not-loaded branch that invalidates the inventory
query on success, so the screen comes back by itself instead of asking for a page reload, and shows
a refusal inline. The branch's prose now names BOTH ways back and keeps them apart: activation is
for this process, the policy is for the next boot and still takes effect on restart. Pinned by the
endpoint literal in `api-contract.test.ts` and three cases in the `NamespaceScope` describe (the
round trip re-rendering loaded with no policy call, a `500` rendered inline with the namespace still
not loaded), two cases each verified against a mutation. The button's absence under `lockNamespace`
is asserted in the same describe, as part of the embed condition phase 4 had already pinned.
Phase 5's docs followed: `studio` and `troubleshooting` said the way back was the policy plus a
restart, which was true for exactly as long as the button was missing.

**Also closed since** (the review follow-ups): `screen-connect.png` was recaptured against a live
app, with the capture spec now creating a namespace that carries a non-inherit policy so the "at
startup" column shows real values; and `NamespaceProblems.NotLoaded`'s dead `extraDetail` parameter
is gone, along with its stale doc block about the superseded interim refusal.

**The council round, and the one finding that mattered.** Two independent reviewers read the whole
change set, and a third pass confirmed the fixes against the tree rather than against the reports.
The finding worth remembering: activation copied the boot path's handling of the save-games FR-11
orphan case (checkpoint files on disk that no registry entry names) and reported it as a SUCCESS, so
it published an EMPTY engine while real checkpoint files sat beside it. That is not a wrong answer,
it is the section-5 data loss reached through the front door: publication makes the namespace
resident, the guard then correctly stops protecting it, and the next clean shutdown registers the
empty graph as the newest checkpoint and resets the log to a bare header. Activation now refuses with
a `409` naming the reachable cure, publication is gated positively on one `Ready` outcome rather than
on the absence of failure, and `Namespace.AttachEngine` has exactly one caller - so a future fourth
outcome cannot silently re-open the path. Mutation-checked: without the distinction, activation
answered `200` and published an engine reporting zero vertices over a three-vertex checkpoint.

The same round also corrected two statements that were simply untrue in published docs (the factory
reset "names it in the response" - the route is a bodiless `204`, so only the log names it; and a
comparative load-versus-save conclusion printed above a table that said the run predated the
measurement), made the `503`'s instructions percent-encode namespace names that legitimately contain
spaces, closed a drop-racing-activation window that recreated the directory it had just deleted, and
pinned the BOOT arm of the three-valued outcome, which nothing had covered: the boot deliberately
keeps coming up empty over orphan files rather than refusing, which the tree confirms is pre-existing
behaviour, documented in three agreeing places with a revisit trigger in the spec, and now has a test
that fails if the condition is broadened. One of the fixes had itself split a doc comment so two
`<summary>` blocks stacked onto the wrong member; that is repaired too.

One thing remains, and it is a real follow-up rather than a decision:

1. **The published load figure is missing**, by choice: it arrives with the next `capacity` workflow
   run, because the checked-in report describes a different machine and a report generated from a
   dirty tree stamps the page "(uncommitted changes present)".

And one item from phase 4's verify list that never landed and was not recorded here until now: **no
e2e scenario for the startup-load policy round trip**. Nothing under `fallen-8-web-ui/e2e` touches
it. The vitest suite covers the panel's selector, the not-loaded rendering and activation against a
mocked client, so what an e2e would add is a real server answering the `PATCH` and the `POST`; it
still could not assert the startup effect itself, which needs a restart and therefore lives in the
MSTest suite (phase 1's boot tests), where it is covered.

Two smaller ones, recorded rather than fixed: `PUT /savegames/{id}/load` recreates dropped member
namespaces inside its resolve loop, so its "nothing was restored" claim was already imprecise for
that case before this feature (the activation pass is deliberately ordered after that loop so it does
not weaken the claim further); and if the 8.3 policy flip succeeds and the activation then fails, the
policy is left enabled, which points the direction the operator asked for and is stated in the 500
detail, but is not test-pinned because forcing a catalog-write failure mid-request needs a filesystem
fault injector this suite does not have.

A third, chosen deliberately when the review asked: an activation whose publish is refused because the
namespace was dropped mid-load may leave an empty write-ahead log and its directory behind, since the
engine's construction can re-create what the drop had just deleted. That is **documented rather than
cleaned up**, and the comment that used to claim the log was gone with the namespace now says what is
really there and the warning log names the directory. Deleting it would mean distinguishing "dropped"
from "the whole collection is disposing", where the same files still belong to a live namespace, and a
cleanup bug in that direction destroys a real write-ahead log. What is left costs disk only: nothing
was ever appended to it (the engine was never published) and a re-created namesake gets a fresh id, so
nothing can read it again.

## Risks and mitigations

| # | Risk | Mitigation | Test |
|---|---|---|---|
| R1 | Shutdown writes an empty checkpoint and truncates the WAL to a header (unrecoverable half) | Phase 0 lands the three-point guard before exclusion is possible; `Engine` throws so a missed site skips instead of NRE-ing | `NotLoadedNamespace_IsNotSavedOnShutdown_AndItsWalAndCheckpointSurvive` |
| R2 | The catalog entry is erased by the next metadata write, stranding the data | Residency is a property of the entry, not of membership (spec 4.4) | `Catalog_RetainsNotLoadedEntries_AcrossCreateRenameDrop` |
| R3 | The freed name is re-minted under a second id over real data | Same as R2 - the name stays reserved | `Create_OfANotLoadedNamespacesName_Conflicts` |
| R4 | A 404 sends the operator to "Recreate (empty)" | 503 with its own title and `namespaceState`; `GET /ns` lists not-loaded namespaces | 503 body pin + the 404 body asserted unchanged |
| R5 | Two engines on one WAL (silently non-durable commits) | No lazy load at all; activation uses a per-namespace gate | `ConcurrentActivation_OfTheSameNamespace_ConstructsExactlyOneEngine` |
| R6 | Zeroed success responses read as "healthy and empty" to a reconciling writer and to the first-run walkthrough | Counts absent, not zero; residency on `/status` and `/ns` | The absent-count pins in both suites |
