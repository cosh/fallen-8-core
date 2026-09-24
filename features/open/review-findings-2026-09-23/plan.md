# Review findings 2026-09-23 - Implementation plan

Branch `feature/review-findings-2026-09-23` from `main` at 89b68b99 or later. Branch-only
workflow. The spec is the contract; this file is the order of work and the gate each phase must
pass. Phases are independent except where noted, so a phase that turns out wrong can be dropped
without unpicking the others. Tick a box when the fix is on the branch WITH its test and the
mutation check has been run.

Every code phase follows the same shape: write the failing test first against the current code,
confirm it is red for the stated reason, fix, confirm green, then revert the fix once more and
confirm red (mutation check; back up per mutant, touch the restored file so MSBuild rebuilds).

## Phase 1 - the hung gateway (spec 2.1)

Files: `fallen-8-agents/Hosting/AgentsHost.cs`, `fallen-8-agents/Model/Fallen8ChatClient.cs`,
`fallen-8-unittest/AgentChatAdapterTest.cs`, `fallen-8-unittest/AgentRuntimeTest.cs`.

- [x] Test first, adapter level: an `HttpMessageHandler` that awaits a never-completing task while
      honouring the token; `HttpClient.Timeout` of 50 ms; `Fallen8ChatClient.GetResponseAsync`
      throws `Fallen8ChatException` whose message contains `Fallen8Target:TimeoutSeconds`. Against
      the current code this must fail with a raw `TaskCanceledException`, which is the defect.
- [ ] **NOT done, and recorded rather than quietly dropped.** Runner level: a real
      `Fallen8ChatClient` driven through the runner. The harness builds its own
      `ScriptedChatClient`, so this would have rebuilt the runner rather than driven it, which is
      the defect shape this feature keeps finding. Left to composition instead:
      `AFailingModelCallEndsTheRunAsFailedCarryingTheReason` already pins that a throwing model
      call ends a run as `Failed` carrying its reason, and the adapter test above pins that a
      timeout is a `Fallen8ChatException` rather than a cancellation. The chain is those two.
- [x] Fix: `AgentsHost` sets `http.Timeout = target.Deadline` and its comment states the seam's
      rule once (one deadline, the seam classifies it). `Fallen8ChatClient` drops the `budget`
      source, passes the caller's token to `RestSeam.SendAsync`, and `Read` loses its
      cancellation catch and its `budget` parameter. `_timeout` stays, it is the number the
      `NoAnswer` sentence prints.
- [x] Confirm `ACallerCancellationPropagatesAsItselfRatherThanAsAGatewayFailure` still passes:
      a caller's cancel must still arrive as `TaskCanceledException`.
- [ ] **Was ticked and had not been done.** The mutant that was actually run re-armed a linked
      source inside the client, which fails the adapter test for the client's reason. Restoring
      `http.Timeout = InfiniteTimeSpan` in the HOST was measured at the gate on 2026-09-24 and
      leaves all 234 agent tests green: the production wiring is pinned by nothing. Spec section
      9 item 1 has the test that closes this.
- [x] Record: agent-host `findings.md` section 4 gets the correction from spec section 4, and a
      new section 17 records this pass (what was examined, what was found, what was not: the
      registry's lock order, memory and token-source lifecycle were read and found sound).

Risk: `HttpClient.Timeout` now also bounds the body read, which the budget did before through the
`Read` catch; that is the same bound in one place, not a new one. If the streaming path ever
stops delegating to `GetResponseAsync`, this phase's assumption breaks; a one-line assertion in
the adapter test that the streamed call goes through the same code path already exists
(`TheStreamedCallYieldsTheBufferedAnswerRatherThanThrowing`).

## Phase 2 - Ollama tool-call ids (spec 2.2)

Files: `fallen-8-core-apiApp/Chat/OllamaChatBackend.cs`, `fallen-8-unittest/ChatToolMappingTest.cs`.

- [x] Test first: one request whose conversation is user, assistant with call `call_0`
      `count_vertices`, tool result for `call_0`, assistant with call `call_0` `count_edges`,
      tool result for `call_0`; the captured body's LAST tool turn carries `tool_name`
      `count_edges`. Red today because first-match returns `count_vertices`.
- [x] Test first: the stub replies twice without ids; the two mapped calls have different ids.
      Red today because both are `call_0`. This is the first test that executes the synthesis
      branch at all; the existing fixture supplies `call_9`.
- [x] Fix: resolve a result's name from the nearest preceding assistant turn that carries tool
      calls (walk backwards from the result's own index), then fall back to the existing
      first-match search. Make the synthesised id unique per assistant turn. Correct the comment
      at lines 233-234 to describe the lookup that now exists.
- [x] Mutation check: restore first-match alone, the first test goes red; restore the per-reply
      ordinal alone, the second goes red.

## Phase 3 - OpenAI malformed arguments (spec 2.3)

Files: `fallen-8-core-apiApp/Chat/OpenAIChatBackend.cs`, `fallen-8-unittest/ChatEndpointTest.Tools.cs`
or `ChatToolMappingTest.cs`, whichever already drives the controller end to end.

- [x] Test first, THROUGH the controller and its serialisation: the stub reply carries
      `"arguments":"{not json"`; the response is 200 and the call's `arguments` is `{}`. Red today
      with the serialisation fault. If no existing fixture reaches the controller with a fake
      OpenAI server, build the smallest one; a backend-only assertion cannot fail the right way.
- [x] Fix: `Parsed` returns an empty object element in both the null and the malformed branch.
- [x] Replay check while here: verify against the shipped SDK whether an assistant message can
      carry text parts alongside tool calls. If yes, keep the text on replay
      (`OpenAIChatBackend.cs` line 322) with a captured-body test; if no, record the SDK limit in
      the comment and stop.
- [x] Correct the comments at `OpenAIChatBackend.cs:307-311` and `AnthropicChatBackend.cs:491-495`
      (a tool result's id IS carried since 7d0e0805).
- [x] Mutation check: restore `return default` alone.

## Phase 4 - the ARXML reader's two small items (spec 5, rows 1 and 2)

Files: `fallen-8-integrations/Providers/AutosarArxml/ArxmlReader.cs`,
`fallen-8-integrations/Configuration/IntegrationsOptions.cs`,
`fallen-8-unittest/IntegrationsArxmlReaderTest.cs` (and the CAN or Ethernet test file for the
variant fixture).

- [x] Test first: a fixture whose `LIN-CLUSTER` contains a `CAN-FRAME-TRIGGERING` or another
      element the reader would otherwise collect; after the read, nothing from below the cluster
      is in the element set and the unread-cluster diagnostic still names `LIN-CLUSTER`. Today the
      subtree is materialised and then dropped, so the first half may already pass; the test's
      value is holding the skip in place once it exists. If it is green before the fix, say so in
      the test comment rather than pretending it was red.
- [x] Fix: for the unread kinds, read the short name and `reader.Skip()` instead of
      `XNode.ReadFrom`. The comment at line 602 becomes true.
- [x] Fixture first for the variant question: one cluster with two `VARIANTS/CONDITIONAL`
      children each declaring the same channel. Observe: does `Claim` emit `DuplicatePath`? Then
      fix whichever side is wrong (spec 5, row 2) and pin it.
- [x] Correct `IntegrationsOptions.cs:74-76` (the file is read as a stream, not decoded to text).
- [x] Record in `features/done/arxml-vehicle-model/`: the spot-check as the review it was, the
      seams it covered, the name tables it could not, and the corrected line 3 of the spec.

## Phase 5 - W8, the twelve lock sections (spec 5, row 4)

Files: `fallen-8-core/Index/SingleValueIndex.cs`, a new or existing index test.

- [x] Test first: a key type whose `GetHashCode` throws once; `AddOrUpdate` with it throws; then
      a second `AddOrUpdate` with a plain key, run on another thread with a bounded wait,
      completes. Red today because the leaked write lock makes the second writer spin forever
      (the wait times out).
- [x] Fix: wrap the twelve unguarded sections (lines 83, 97, 117, 145, 159, 175, 189, 233, 259,
      285, 390, 407 at HEAD) in try/finally, matching the one that already exists at line 204.
      Nothing else changes.
- [x] Mutation check: remove the finally on the `AddOrUpdate` section alone.
- [x] Browser probe RUN (the workload is installed, so it was not optional): published trimmed
      and executed headless under node, 9 of 9 PASS, exit 0. It is the only gate that reaches
      this engine file on a single-threaded host, and two of its checks cross the sections
      this phase wrapped: the registered index round trip exercises `Save` and `Load`.
- [x] Update the audit spec's status line (spec section 4): W7 done, the verified pending list.

## Phase 6 - the Studio hook and the SBOM test (spec 5, rows 5 and 6)

Files: `fallen-8-web-ui/src/state/namespaces.ts` (new), the four consumers,
`fallen-8-web-ui/tests/`.

- [x] `useNamespaces(instance, { enabled? })` owning key, `listNamespaces`, `STATUS_POLL_MS`,
      `retry: 0`; default gate `instance !== null`. `AppShell`, `NamespaceScope`,
      `NamespacesPanel` take the default; `InstanceHealth` passes its authorisation gate, with a
      comment saying why that one is different. Invalidation sites keep using the key through a
      helper the hook exports, so the literal has one home.
- [x] Convention test: the string `"namespaces"` as a query-key element appears in exactly one
      file under `src/`. Mutation check by re-adding one inline definition.
- [x] SBOM test through `buildFallen8Deps`: `F8_DEPS_REFETCH=1`, `fetch` stubbed to return the
      committed copy with the three regenerated fields altered, `node:fs` mocked so
      `readFileSync` serves the committed copy and `writeFileSync` is a spy; assert never called
      and the returned SBOM equals the canonicalised committed copy. Restore env and mocks after.
- [x] `tsc` and vitest, exit codes read through `cmd /v:on`.

## Phase 7 - the overclaim, in words (spec 3)

Files: `README.md`, `docs/src/content/docs/agents.md`, `docker-compose.yml`,
`docker-compose.nahil.yml`.

- [x] README Key features Agents entry: one clause saying the shipped default model does not call
      tools once instructions are present, and that a tool-capable model must be named.
- [x] `agents.md` intro: one sentence pointing at the measured note; the note stays where it is
      and stays the one home.
- [x] `docker-compose.yml:92` and `docker-compose.nahil.yml:49`: state what was measured (calls a
      tool with a bare user turn, not with a role prompt) and point at the docs page.
- [x] Docs build green. No screenshot changes.

## Phase 8 - the gate

- [x] Full solution build and test, `-v n` or higher.
- [x] Web UI `tsc` and vitest.
- [x] Docs build.
- [x] Python dash check over every changed text file, detector self-tested first.
- [x] Confidential-name grep over the whole diff.
- [x] Impact table in the spec re-checked against the diff as it actually is.
- [ ] Move this directory to `features/done/` in the merge commit's own PR or merge, carrying the
      four pointer edits listed below; the spec's
      status line says what shipped and what was left (the model decision of spec section 3).

## The move to features/done, and the four edits it owes

Not done here, because the feature is not merged. Whoever moves this directory carries these with
it, or four pointers break silently (spec section 9 item 9):

- [ ] `features/done/agent-host/findings.md` links `../../open/review-findings-2026-09-23/spec.md`
- [ ] `features/done/arxml-vehicle-model/findings.md` links the same path
- [ ] `features/open/platform-integrity-audit/spec.md` links `../review-findings-2026-09-23/spec.md`
- [ ] `fallen-8-unittest/CodeQualityTest.cs` carries the path as a STRING in the exemption comment,
      so no link checker would catch it

## Phase 9 - what the gate found (2026-09-24)

Spec section 9 is the list, worst first. Items 1 to 7 block the merge; 8 goes in with them because
the files are open; 9 to 12 are record corrections, and 9 is owed by whoever moves this directory.
Each code fix follows the same shape as before: the failing test first, the fix, the mutant. Item 1
is the one whose test must go red on the HOST mutant this time, not on a client mutant.

## Phase 10 - what the review of the fixes found (2026-09-24)

Spec section 10 is the list, worst first. Item 1 is a record correction across four sites and is
the only one that earns a further look afterwards, because that claim has now been wrong twice, in
opposite directions. Items 2 and 3 are a gate and two tests that cannot fail as written; 4 is a
false tick; 5 to 10 are wording, a pointer, a displaced comment and two simplifications. All ten
are small. After they land, re-check item 1's four sites against `PluginFactory.Activate<T>` and
stop; a third full pass is not worth what it would find.

## Risks

- Phase 1 changes an observable state for one failure class (`cancelled` becomes `failed`). Any
  test or doc that asserted the old text for a hung gateway is wrong and should be fixed, not
  kept.
- Phase 2's backwards walk assumes results follow their calls, which the protocol guarantees and
  the agent framework produces. A client that reorders turns gets the fallback, which is today's
  behaviour.
- Phase 4's variant fixture may show the comment is right and the diagnostic is spurious on real
  extracts, which would mean valid files have been reporting "the file contradicts itself" since
  the CAN work. That is a finding to record, not to soften.
- Phase 5 touches engine code the browser host runs. Try/finally has no threading arm; the probe
  is the check, if the workload is installed.
- Phase 6 may reveal that a consumer relied on its own `enabled` predicate for a reason the
  comment does not state. Read each before deleting it.
