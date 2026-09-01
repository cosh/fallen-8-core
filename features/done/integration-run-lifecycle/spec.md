# Integration run lifecycle: many files, cancel, resume

**Status:** IMPLEMENTED and merged to `main` on 2026-08-26, from
`feature/integration-run-lifecycle` (branch-only workflow, no GitHub issue or PR). Spec written the
same day. All three parts shipped; section 9 records where the implementation departed from this
text and why, and [findings.md](findings.md) records one thing measured on the way that is not about
this feature.

Three changes to how integration runs behave, shipped as one feature because they share one
seam (the job model and the run lifecycle in `fallen-8-integrations`):

1. The AUTOSAR system extract integration accepts **many ARXML files in one run**, all
   describing one system, imported as one snapshot under one identity.
2. **Every integration run can be cancelled**, for every provider, with honest semantics
   about what a cancelled run leaves behind.
3. A run interrupted by a process or container restart **resumes where it left off**,
   without ever spooling a credential or a file's bytes.

This spec deliberately flips two recorded non-goals of
[integration-run-visibility](../../done/integration-run-visibility/spec.md) ("No
cancellation... Revisit only with a designed abort point" and "No persistence... nothing to
resume. Revisit when runs become resumable"). This is that revisit. The third recorded
non-goal in [integration-file-upload](../../done/integration-file-upload/spec.md) ("No staged
upload, no upload id, no resumable transfer. One request carries the run.") is **upheld**:
one request still carries the whole run, files included.

## 1. The problem, exactly

**One extract per run is actively destructive, not just inconvenient.** A vehicle network is
not one file: an OEM hands over one system extract per domain or bus, and the extracts
reference each other by AUTOSAR path (a frame in `chassis.arxml` carries a signal defined in
`body.arxml`). Today the provider takes exactly one file
(`AutosarArxmlProvider.cs`, single `File` setting), and because it honestly declares every
snapshot `Complete`, running the second extract under the same identity **withdraws and
deletes everything the first extract claimed** (reconciliation is doing its job on a wrong
premise). Running each extract under its own identity avoids the deletion but splits one
system into disconnected subgraphs whose cross-file references can never resolve. There is
currently no correct way to import a multi-extract system.

**A run in flight cannot be stopped.** The single-flight gate (`RunGate`) correctly refuses
a second run under one identity with HTTP 409, but the run it points at has no cancel
affordance, no route, and no token that reaches the apply phase
(`JobRunner` passes `CancellationToken.None` past the point of no return). A mistaken run,
a wrong file, or a run stuck embedding twelve thousand entities against a slow provider can
only be waited out or killed with the container.

**A restart loses hours of work unrecoverably.** All run state is memory only (`RunGate`,
`RunTracker`, the file payloads), the container is `read_only` with no volume, and the
embed phase only embeds entities the current run created or changed (`summaryDirty` in
`SnapshotApplier`). So a restart mid-embed does not merely lose progress: re-running finds
every element already written and unchanged, embeds nothing, and the only cure is clearing
the namespace and importing again. For a real extract (>12k entities, 16 per embed batch,
a local embedding provider) that is hours, lost to any `docker compose restart`.

## 2. Many files, one snapshot

- **FR-1, descriptor.** `ProviderSetting` gains `multiple` (boolean, default `false`,
  meaningful only for `kind: File`; declaring it on any other kind is a descriptor error the
  catalog refuses at startup). The ARXML provider declares `multiple: true` on its `file`
  setting and its label/help change to say one or more extracts of one system. The CSV
  provider stays single-file. The pinned descriptor snapshot and the Studio TS mirror grow
  the field; the allowed-fields lists in `ProviderDescriptorSnapshotTest` are updated
  deliberately, not loosened.
- **FR-2, wire.** The `files` map value accepts either the existing single object or an
  **ordered array** of `{name, contentBase64}` for a setting declared `multiple`. A single
  object stays valid everywhere (back-compat: it is an array of one). An array for a
  non-`multiple` setting is refused at normalize time with a named error. Order is
  preserved and meaningful (FR-4). Duplicate file names within one setting are refused
  (they would make diagnostics unattributable).
- **FR-3, provider contract.** `IJobFiles` (and the observe context) exposes the ordered
  list of payloads for a setting: count, per-file name, per-file read. The existing
  single-file accessors keep working for single-file settings and mean "the first and only
  file". The conformance seams (`RecordingFilesFactory`) count reads across all files;
  `FilesOnlyFromTheJob` and `RunsOffline` need no semantic change.
- **FR-4, ARXML merge semantics.** All files stream into **one shared path table**, in job
  order, then reference resolution runs **once** over the union. Consequences, each pinned
  by a test:
  - A cross-file reference resolves exactly like a same-file reference (frame in file A,
    signal in file B: the `carries` edge lands).
  - A path declared again in a **later file** collapses first-wins, silently per path, with
    **one aggregate diagnostic per file** naming how many paths it re-declared. This is the
    expected case: every extract carries the standard platform packages
    (`/AUTOSAR_Platform/BaseTypes/...`), and the identifier vocabulary already anticipated
    exactly this composition (`arxml-path` is instance-scoped for this reason). A duplicate
    **within** one file keeps today's per-path `arxmlDuplicatePath` diagnostic.
  - The FlexRay-cluster gate judges the union: at least one cluster across all files, not
    per file.
  - The snapshot stays `Complete`, and the source it is complete over is **the union of the
    supplied files**. A later run with fewer files therefore withdraws what only the missing
    file described. That is correct and must be documented where the file input is
    explained, because it is the sharp edge of the feature: the set of files IS the source.
  - Determinism: same files in the same order produce a byte-identical snapshot (the
    existing `Deterministic` conformance check, run against a multi-file fixture).
- **FR-5, bounds.** The per-file decoded ceiling stays (`Integrations:MaxFileBytes`,
  128 MiB). New: `Integrations:MaxJobFileBytes`, the decoded total across all files of one
  job, default 512 MiB, enforced at normalize time with the same problem+json shape as the
  per-file bound. The transport bounds rise together, derived from that total
  (base64 is 4/3, plus headroom): the apiApp proxy job route from 192 MiB to **768 MiB**
  (`JobTransportLimit`), the runtime Kestrel bound from 256 MiB to **832 MiB**
  (`TransportBound`). The stale "(48 MiB)" comment in `fallen-8-integrations/Program.cs` is
  corrected in passing. The docs gain one honest operator sentence: a full-size job peaks at
  roughly base64 plus decoded bytes in memory (about 1.2 GiB for 512 MiB of files), because
  one request carries the run and that remains the design.

  *Later (integration-file-transport):* raising the ceilings was necessary and not sufficient, and
  the lesson is worth one line here. A ceiling is only real if every hop between the person and the
  runtime can carry it, and this one derived the transport bounds from the total while leaving TWO
  hops unable to reach them: a browser cannot base64 more than about 384 MiB (a JavaScript string
  caps at 512 MiB, and the encoder holds bytes, string and request at once), and the proxy's 413 was
  raised mid-upload, so it arrived as "the runtime did not answer". Both were fixed there, together
  with a route that publishes what a job may carry so a client stops guessing.
- **FR-6, Studio.** The file field for a `multiple` setting stages a **list**: the picker
  gets the `multiple` attribute, the dropzone accepts several files at once and appends,
  each staged file shows name, size and its own remove control, and the total size is
  visible. Staging stays in-tab memory exactly as today (kept for a re-run, cleared on
  provider switch). `buildJob` emits the array form only for `multiple` settings. The
  existing 413 copy already speaks to size; it now names the total, not just "the file".

## 3. What can run in parallel, and what actually pays

The user-visible question is whether phases can overlap. The honest answer, from where the
time actually goes, is that **this feature parallelizes nothing**:

- **Parsing** N files could run concurrently, but streaming one ARXML takes seconds,
  first-wins merge order must stay deterministic for the conformance gate, and observe is
  never the phase anyone waits on. Sequential in job order.
- **Writes** go through the engine's single writer thread; concurrent REST calls would just
  queue there and interleave nondeterministically in the report. Sequential.
- **Embedding** is the wall-clock (hundreds of batches against one local provider). The
  bottleneck is the provider itself, not the loop around it: concurrent batches against one
  Ollama queue at the model. Sequential, unchanged, 16 per batch.

What actually rescues the long phase is not parallelism but **survivability**: cancel (you
can stop it) and resume (a restart does not lose it). Revisit triggers, recorded here so the
decision is not re-litigated casually: parallel parse when observe of a real multi-file job
is measured above a minute; concurrent embed batches when a measured run shows the provider
scales with concurrency (a remote backend would; local CPU inference does not).

## 4. Cancel, for every integration

- **FR-7, route.** Runtime: `POST /integration/run/{instanceId}/cancel`. Proxy:
  `POST /integrations/run/{instanceId}/cancel`, same auth policy as the rest of the
  controller, forwarded like the other routes. Answers: **202** when the signal was
  delivered to a run in flight (repeat cancels while it winds down also 202), **404** when
  no run is in flight under that identity (a finished run is not cancellable; its slot says
  what happened). The OpenAPI snapshot grows the route; the MCP coverage deferral for
  `/integrations` already covers it textually, and its reason is reviewed in the same PR so
  the wording stays true.
- **FR-8, semantics.** Three tokens, three meanings, stated once in `JobRunner` where the
  point of no return is documented today:
  - The **request token** (caller walking away) keeps meaning nothing once apply starts.
    Closing the browser still does not stop a run.
  - The **run token** is new, owned by the run's tracker slot, signalled only by the cancel
    route. The applier observes it at **safe points**: between phases, between write
    batches, between embed chunks, never mid-element. A tripped run token ends the run in a
    new terminal state `cancelled`: not `succeeded`, not `failed`, `StoppedInPhase` set,
    report carrying the counts of what really happened, error empty.
  - The **host shutdown token** (FR-14) interrupts without cancelling: it checkpoints and
    stops, leaving the run resumable. Cancel deletes the spool entry; shutdown keeps it.
- **A cancelled run never reconciles.** This is load-bearing, not a nicety: reconciliation
  withdraws `claimedBefore \ claimedNow`, and a cancelled run's `claimedNow` is missing
  every entity it never reached, so reconciling would delete healthy elements the snapshot
  still describes. A cancelled run stops all work, leaves what it already wrote (claims and
  all), and states in its report that the next completed run of this identity converges the
  graph (resolution makes the leftovers idempotent, reconciliation cleans what a fresh
  complete snapshot no longer claims). No withdrawal, no deletion, ever, on the cancel path;
  pinned by a test shaped like the existing `UnreadableSourceFails` honesty check.
- **Cancel during observe** flows the same token into the provider (it already receives
  one); a run cancelled before the snapshot exists ends `cancelled` with zero mutations.
- The single-flight gate releases when the cancelled run finishes winding down, exactly like
  any other terminal outcome, and the **409 body gains one sentence** pointing at the way
  out: "Cancel the run in flight to start another."
- **FR-9, Studio.** The run panel shows a cancel control while `running` (two-step, in
  place, like the existing destructive patterns; testid `integration-run-cancel`). A
  cancelled run renders its terminal state distinctly (stopped phase, "cancelled" wording,
  the partial report, and the one-line convergence note). The 409 error box keeps rendering
  the body verbatim, which now contains the pointer.

## 5. Resume after an interruption

- **FR-10, the spool.** New setting `Integrations:SpoolDirectory`. Empty (the default, and
  the bare `dotnet run` reality) means resume is off and behavior is exactly today's. The
  compose environment sets it to a path on a new named volume (`f8-integration-runs`),
  keeping `read_only: true` and the tmpfs; the "NO MOUNT AT ALL" comment in compose moves to
  the truth: no credential mount, no files mount, one spool volume that holds runs in
  flight and nothing else. What a spool entry may contain: the job envelope **without
  credential values and without file bytes** (provider id, identity, namespace, embed flags,
  run id, started-at), the validated snapshot once observe+validate succeeded, and the embed
  journal (FR-13). Entries are written atomically (temp file + rename), carry a format
  version, and are **deleted on every terminal outcome**: success, failure, cancel. The
  spool therefore holds at most the runs currently in flight; it is not a history.
- **FR-11, the intent record.** At accept time a tiny intent record (no snapshot yet) is
  spooled. A restart that finds only an intent record cannot resume (the files and the
  credential died with the process, by design) and instead materialises an honest terminal
  slot: interrupted before the snapshot was accepted, nothing written, nothing withdrawn,
  run it again (the Studio tab still stages the files). The record is then deleted.
- **FR-12, startup resume.** On startup the runtime scans the spool and resumes every entry
  that has a snapshot, oldest first, through the normal gate, under the **same run id**,
  with the slot marked `resumed`. Resume does **not** re-observe: the snapshot is the source
  as it was seen at capture time, and `capturedAt` is preserved. Resume re-validates the
  spooled snapshot and re-runs **resolve against the current graph**, which is what makes
  the write phases idempotent: elements the interrupted run already wrote resolve as
  existing and compare equal, so nothing is written twice. Requirement on the resolve path:
  a run that died **mid-write** (elements created, claims-index entries not yet flushed)
  must not get twins on resume; the existing repair-aware resolution and index re-assertion
  seams are the mechanism, and this crash window gets its own test. A spool entry a newer
  runtime cannot read (format version, failed re-validation) is refused into an honest
  terminal slot, never guessed at. Reconciliation runs only at the end of the **resumed**
  run, over the re-resolved `claimedNow`, so it stays correct.
- **FR-13, the embed journal.** Re-resolution is exactly wrong for the embed set: resumed
  entities compare equal, `summaryDirty` comes out empty, and everything not yet embedded
  would be skipped forever (the unrecoverable loss of section 1). So the spool journals the
  embed work **ahead of the writes that create it**: dirty entity indices are appended
  before each write batch executes, and an embed cursor advances after each embed chunk. On
  resume the embed set is the journalled entities behind the cursor, in journal order.
  Journal-ahead makes the guarantee **at-least-once**: a handful of entities near the crash
  may be embedded twice (idempotent, same text, same vector slot), but none can be skipped.
  Pinned by tests that kill the run between batch, journal and chunk boundaries.
- **FR-14, graceful shutdown.** The applier observes the host's stopping token at the same
  safe points as the run token. On shutdown it checkpoints (journal and cursor are already
  durable per chunk), stops without a terminal state, and the next start resumes. This is
  what turns `docker compose restart` from hours lost into seconds lost; the compose
  `stop_grace_period` already gives it 120 s, far more than a chunk boundary needs.
- **FR-15, transient retry.** The graph target gets a small bounded retry (3 attempts,
  short backoff) on connection-level failures, so an apiApp restart that is faster than the
  retry window does not kill a run at all. Not a retry loop, not a queue: a run whose
  target stays unreachable still fails, deletes its spool entry, and says so.
- **FR-16, surfacing.** `RunStateDto` gains `resumed` (and the `cancelled` terminal state
  from FR-8); the TS mirror follows. The Studio run panel re-attaches exactly as today
  (identity-keyed poll, same run id, so the persisted `expectedRunId` still matches) and
  shows a one-line "resumed after a restart" note. Elapsed time keeps counting from the
  original start, outage included, because that is what actually elapsed. A resumed run's
  report gains a diagnostic naming that counts cover the portion after resume.

## 6. Why this does not break "the runtime keeps nothing"

The thesis was never "no bytes on disk"; it was: no schedule, no run history, no credential
store, nothing to rotate, nothing an attacker finds later. All of that stays true. The spool
holds only runs in flight; success, failure and cancel each delete the entry the moment the
run ends; a credential is needed only through observe and is never spooled; file bytes are
never spooled (the snapshot, which was always destined for the graph, is what persists
during flight). What changes is one honest sentence in the docs: the runtime remembers a run
**in flight** across a restart, and only that, and only when the operator mounts a place for
it.

## 7. Non-goals, each with a revisit trigger

- **No parallel phase execution.** Reasons and triggers in section 3.
- **No staged or chunked upload.** One request carries the run, still. Revisit when a real
  extract set exceeds the raised transport bound.
- **No manual resume endpoint and no unbounded retry.** Startup auto-resume plus bounded
  transient retry covers the stated failure (a container or host restart). Revisit if runs
  die for reasons a restart does not cure.
- **No run queue.** The 409 stays; cancel is the designed way out it lacked.
- **No multi-file for the CSV provider.** Its source is one inventory. Revisit when a real
  inventory arrives in parts.
- **No spooled credentials or file bytes, ever.** Not a trigger, a rule.
- **No run history.** The spool is not one, and `RunTracker` retention is unchanged.

## 8. Impact on existing features

| Area | Impact |
| --- | --- |
| Engine (`fallen-8-core`) | **Untouched.** No engine change anywhere in this feature, so the browser-probe gate is not implicated. |
| REST surface (apiApp) | **One new proxy route** (`POST /integrations/run/{instanceId}/cancel`) and the raised `JobTransportLimit`. Controller docs updated; OpenAPI snapshot regenerated (additions only). |
| OpenAPI snapshot | **Grows** by the cancel route and the reworded job-route description (413 bound). Regenerate via `scripts/update-openapi-snapshot.ps1`. |
| MCP coverage gate | **Deferral already matches** (`op.Contains("/integrations")`); the reason text is reviewed in the same PR because it enumerates the routes it withholds. No bridge, no new deferral entry needed unless the reviewed wording says otherwise. |
| Provider-descriptor snapshot | **Changes** (new `multiple` field, ARXML label/help text). Regenerate via `scripts/update-provider-descriptor-snapshot.ps1`; allowed-fields lists in `ProviderDescriptorSnapshotTest` updated deliberately. |
| Docs screenshot | **Recapture `docs/src/assets/images/screen-integrations.png`** (descriptor change rule) via `F8_SCREENSHOT=1 npx playwright test e2e/screenshot-integrations.spec.ts`. |
| F8 Studio | Multi-file staging (FR-6), cancel control (FR-9), resumed note (FR-16), 409 copy arrives via the body. Component tests extended; `api-contract.test.ts` gains the cancel call and **backfills the two run-visibility calls it was already supposed to carry** (recorded gap: run-visibility's spec called for `ENDPOINT_CALLS` entries that were never added). |
| Docs site (`docs/`) | `integrations.md`: Files section rewritten for many files and the union-is-the-source edge; "what is remembered" passage updated for the spool; run section gains cancelled state and resume. `studio.md` integrations paragraph touched with the screenshot. `troubleshooting.md` "run vanished" section updated (a restart now resumes when the spool is mounted). Docs build must stay green. |
| Compose environment | New named volume `f8-integration-runs` + `Integrations__SpoolDirectory` on `f8-integrations`; `read_only` and tmpfs stay; the no-mount comment rewritten to the new truth. |
| Conformance suite | **Checks unchanged.** Multi-file is exercised through the existing checks with a multi-file ARXML fixture in the blueprint tests; cancellation and resume are runtime machinery pinned by unit tests, not provider conformance. |
| Identifier vocabulary | **Unchanged.** `arxml-path` instance scoping already anticipated multi-extract composition. |
| Architecture diagrams | **Unchanged.** No new channel, no new deployable; the volume is a compose detail, not an architecture change. |
| NL assist | **Unaffected.** No retrain entry needed. |
| Sample graphs, stored queries | **Unaffected.** |

## 9. As built

Where the implementation differs from the spec above, or learned something the spec did not know.
The spec text is left as written; this section is the correction.

**Two kinds of stop, not one.** The spec had a single run token. Implementation forced the split: a
run stopped by SHUTDOWN must keep its spool entry and report nothing, while a run stopped by an
OPERATOR must delete its entry and report `cancelled`. Both stop at the same safe points, so they
share one `RunAbort` carrying two tokens, and differ only in the exception raised
(`RunCancelledException` / `RunInterruptedException`, both `RunStoppedException`). Cancellation wins
when both fired: it is the more specific statement.

**Shutdown also aborts the source read.** Not in the spec, and found by a test that expected an
interrupted observe to be reported honestly: a container told to stop should not spend its whole
grace period finishing a parse whose result it is about to discard. Aborting a read writes nothing,
so the read token now links all three signals. A shutdown during observe is reported as an
interruption rather than falling through to the catch-all, which had called it a `source` failure and
would have sent an operator to look at a system that answered perfectly well.

**The pre-reconcile safe point has exactly one reachable window, and the first test for it was
worthless.** A mutation check (removing the check) left the "stop during the writes" test green,
because an earlier safe point catches that stop first. The check is reachable only when a stop
arrives while the LAST embedding chunk is in flight, since the embed loop looks at the stop between
chunks and finds no further iteration. `AStopArrivingDuringTheFinalEmbeddingChunk_StillDoesNotReconcile`
is the test that actually covers it, and it fails without the check.

**The embedding plan is journalled ONCE, before the writes, not per batch.** The spec said "appended
before each write batch". Simpler and stronger: the whole plan is knowable before any write, because
a created entity's summary is new by definition and the created set is known before the create call
returns. So the invariant is exact - if any element of this run was written, its journal exists - and
there is one write of it rather than N.

**A spool entry is two files, not one.** `run-<id>.job.json` (envelope plus snapshot, written twice
at most) and `run-<id>.progress.json` (the journal, rewritten per chunk). One file would have meant
rewriting a snapshot of tens of megabytes hundreds of times per run.

**The embedding cursor rides on the progress sink.** The chunk loop lives inside the graph target,
which must not learn that a spool exists; the only per-chunk signal is `IRunProgress.Advance`, so the
journal is also a progress decorator. The alternative was a second seam threaded through
`IGraphTarget`.

**The plan's order is sorted, and that is load-bearing.** `summaryDirty` is a hash set, whose
iteration order does not survive a process boundary, so the cursor would have pointed at a different
set of entities after a restart. The journal records ascending entity positions.

**`multiple` is expressed as a wire shape and a descriptor flag, and the list CONSTRUCTOR always
means the array form.** `JobFileGroup` accepts a bare object or an array and remembers which; a
single object converts implicitly, so every caller written before multi-file is untouched. A
single-element ARRAY is still refused for a single-file setting, because a client sending `[one]`
would otherwise work by accident and break the day it sent two.

**A multi-file setting's effective value is every name, joined.** The first name alone would have
made every message about the setting quietly about one file of several.

**A resumed run the graph refused keeps its entry.** Not in the spec, and found by reviewing the
finished work rather than the code: this container restarts alongside the graph and may come up first,
so a resumed run can fail purely because the graph was not answering yet - and deleting its entry then
loses the hours the spool exists to protect. Bounded at three attempts, counted on the entry, and the
last attempt is reported as the graph failure it was. This is FR-15 answered across restarts rather
than inside a run; the plan records why the in-run retry was not built.

**The api-contract backfill in the impact table was stale.** `getIntegrationRun` and
`submitIntegrationJob` were already registered in `ENDPOINT_CALLS`; only the new cancel call needed
adding. The recorded gap from `integration-run-visibility` had been closed at some point without the
record being updated.

## 10. Acceptance

1. One run with three ARXML files of one system lands one connected graph: cross-file
   edges resolve, standard-package paths collapse first-wins with one aggregate diagnostic
   per re-declaring file, and re-running the same three files is a zero-mutation no-op.
2. Re-running with two of the three files withdraws exactly what only the third described,
   and the docs state this behavior where files are explained.
3. A job whose decoded total exceeds `Integrations:MaxJobFileBytes` is refused at normalize
   time with the documented problem shape; the transport chain (proxy 768 MiB, runtime
   832 MiB) admits a maximal legal job.
4. Cancel during each phase (observe, writes, embed) ends the run `cancelled` at a safe
   point, never reconciles, never withdraws, releases the gate, keeps the partial report,
   and a follow-up full run converges the graph. Cancel with no run in flight is 404;
   repeat cancel is 202.
5. Kill the runtime mid-write and mid-embed with a spool mounted: on restart the run
   resumes under the same run id, writes nothing twice, creates no twin elements, embeds
   every journalled entity at least once, reconciles once at the true end, and the Studio
   panel re-attaches showing `resumed`.
6. Kill the runtime before validate: restart reports the honest cannot-resume slot, and
   nothing was written or withdrawn.
7. With no spool configured, behavior is bit-for-bit today's (no writes to disk at all).
8. A cancelled or completed run leaves an empty spool directory.
9. All gates green: full test suite, descriptor snapshot, OpenAPI snapshot, screenshot
   recapture, docs build, warnings-as-errors, on Linux CI as well as locally.
