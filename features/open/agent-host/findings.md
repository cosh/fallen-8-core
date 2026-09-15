# Agent host: measured findings

What was learned by running this feature's code against a real instance and a real model, rather
than by reasoning about it. Everything here is reproducible; where a number appears, it was measured
on the date given.

## 1. The shipped agent model cannot run a tool-using agent (2026-09-10, Phase 1b gate)

**This is the feature's largest open risk and it is not a defect in this repository's code.**

`phi4-mini:latest` is the only tool-capable model reachable on the configured platform. Measured
with one tool (`count_vertices`, one required string parameter), temperature 0, and repeated runs:

| shape | outcome |
|---|---|
| bare user turn, tools offered | real tool call, **4/4** |
| any system prompt + tools | writes the call as prose text, **4/4** |
| the shipped `assistant.md` prompt | invents a figure ("the default namespace contains 10,000 vertices"), **4/4** |
| persona-only system prompt ("You are a Fallen-8 graph analyst.") | prose, **4/4** |
| a system prompt that never mentions tools | "I don't have the capability", **4/4** |
| the same instructions moved into the user turn | prose, including the literal `<\|tool_call\|>` marker, **4/4** |
| an assistant acknowledgement turn before the question | prose, **4/4** |
| streamed instead of buffered | identical in every arm above |
| **sent directly to the platform, bypassing Fallen-8** | **identical in every arm above** |

Two consequences follow, and the second is the important one.

**Our chat gateway is exonerated.** The direct comparison is the whole point of it: the same bodies
sent to the platform's own API, with Fallen-8 out of the picture, behave the same way. Phase 1a's
tool mapping is correct, and so is Phase 1b's adapter.

**Spec section 3.2a is half wrong, and this file is the correction.** It says the role prompts are
load-bearing because a prompt is what makes a small model call a tool instead of fabricating a
result. Against this model the polarity is reversed: with no instructions the model calls the tool,
and with ANY instructions it fabricates. The prompts remain correct for a capable model, and that is
why they ship as written (decision 2026-09-10); but "the prompts make tool calling work" is not a
claim this feature can make about its own default deployment.

Even in the working shape the arguments are unusable: the model echoes the schema
(`{"properties":{"graphNamespace":{"type":"string"}},"type":{...}}`) instead of filling it, so the
framework refuses the invocation with "the arguments dictionary is missing a value for the required
parameter". Phase 1a recorded this shape already; Phase 1b confirms it is the norm rather than an
outlier.

**Nothing else on the platform helps.** `phi4-f8-mini:latest` (the assist fine-tune) answers **502**
to any request carrying tools. Nine other tool-capable model names were probed
(`qwen3`, `qwen2.5:7b`, `llama3.1:8b`, `llama3.2:3b`, `mistral-nemo`, `hermes3:8b`, `command-r7b`,
`granite3.1-dense:8b`, `firefunction-v2`) and all **404**. This matches what Phase 0 recorded about
the fallback ladder having no second rung.

**Status: blocked on a tool-capable model, which is a platform ask rather than a code change.** The
host, the adapter, the caps, the registry and the proxy all work; what a run produces on this model
is whatever the model does. A fabricated answer is recorded as `completed`, because the host cannot
know the text is invented: detecting that is the citation check in spec 3.2, which arrives with the
trace in Phase 2 and is the mitigation for exactly this.

*Revisit trigger:* any tool-capable model resolving on the configured platform, or a deployment
pointing `Fallen8:Chat:Backend` at OpenAI or Anthropic, whose models were not measured here because
no key for either exists in this environment.

## 2. The role prompt never reached the model (fixed)

Microsoft Agent Framework carries an agent's system prompt on `ChatOptions.Instructions`, **not in
the message list**. The adapter mapped only the messages, so every request left with the role prompt
dropped: measured at **nine prompt tokens** for a request whose prompt is 1327 characters.

The test that was supposed to cover this asserted on the `Instructions` property, which the
framework had set correctly, so it passed throughout. Fixed in `Fallen8ChatClient.Turns`, which
prepends the instructions as a leading system turn; pinned by three tests in
`AgentChatAdapterTest` that assert what the **gateway received**, and mutation-checked by dropping
the fix (two fail).

The lesson generalises and is worth keeping: a test that asserts on what a framework handed us
proves nothing about what we then did with it.

## 3. A run's setup could wedge an agent forever (fixed)

`AgentRunner`'s setup ran outside its `try`, and `Start` discarded the returned task, so any throw
there was an unobserved task exception: no log line, no state change, and the agent sat in `pending`
holding a concurrency slot for the life of the process. After four such spawns every further spawn
answered 429 with nothing in the log explaining why no agent ever ran.

The reachable thrower was `CancelAfter`, which refuses a delay past `Timer.MaxSupportedTimeout`
(about 49 days) - so `MaxRunSeconds` set to a large number meaning "effectively no cap" broke every
run on that host. Nothing validated it.

Fixed three ways, because one was not enough: the setup moved inside the `try`, an over-large cap is
clamped with a warning naming what was applied, and `Start` now observes the task and records a
failure for anything that escapes `RunAsync` itself.

## 4. A gateway that hung was reported as an agent somebody cancelled (fixed)

The adapter's own deadline produced an `OperationCanceledException`, which is exactly what a
caller's cancel produces, and the runner turns that into `state: cancelled`. So an instance that
went quiet looked like a user action.

Fixed by adopting the shared `fallen-8-rest-client` seam, which exists for precisely this
distinction and is what the other two sidecars use: `RestSendFailure.TimedOut` and
`.Unreachable` become named `Fallen8ChatException`s. Verified live: an unreachable gateway now reads
`state=failed`, `failure=The Fallen-8 chat gateway at http://127.0.0.1:1/ could not be reached`.

This also removed a hand-rolled copy of the seam's classification and made the project reference,
which was previously unused, actually load-bearing.

## 5. The documented tool allowlist could not be configured (fixed)

`Agents:Roles:<role>:Tools` is how every document for this feature spells the key. The options shape
was `Dictionary<String, List<String>>`, which binds `Agents:Roles:<role>:0`. So an operator's
allowlist bound to **nothing**, no error was raised anywhere, and the role silently kept its shipped
default. A tool allowlist that silently fails to narrow is worse than one that refuses to load.

Fixed with a nested `RoleOptions` class, and pinned by a test that goes through the real
configuration binder rather than constructing the options object by hand - which is the only way to
see this class of bug.

## 6. Smaller things, all fixed

- **A run that stopped without answering was recorded as `completed` with empty text.** Reachable
  two ways: the framework ends a turn on a tool name it does not know, and a model that only ever
  asks for tools exhausts the iteration cap. It is now `failed` with the step count, because an
  agent that said nothing has not answered.
- **A caller could name its own `tokenBudget` without limit**, so `DefaultTokenBudget` was only a
  default and the operator had no ceiling on what one caller may spend against a shared metered
  quota. Added `Agents:Limits:MaxTokenBudget` (400000), which clamps rather than refuses; the record
  a caller gets back says what it actually got.
- **`worker` and `orchestrator` were spawnable over the control plane.** A worker has nobody to
  report to, and an orchestrator's prompt commands two delegation tools this phase does not attach.
  Both are now refused with the reason and a pointer to `assistant`.
- **The prompts told the model to cite "the tool-call id shown in that call's result".** No backend
  shows a tool-call id to a model: it travels on the protocol. Asking for one is asking the model to
  invent one, in the prompt whose job is to stop invention. The citation marker is now
  `[t:<name>]`, the tool's own name.
- **Reported usage was decided on the presence of the `stats` object**, which the gateway sends on
  every answer with null fields. So "nobody measured this" became a measured zero, which is the
  exact substitution the unreported-usage flag exists to prevent. Now decided on the token fields.
- **A `?probe=true` re-probe was named in an operator-facing log line and does not exist.** Nothing
  in this phase reconnects the toolset; the message now says to restart the host.
- **`McpToolset.DisposeAsync` threw on a second call**, which is the ordinary case for a container
  singleton, and did not take its own gate, so an overlapping connect could leak a live session.
- **Two endpoint tests depended on how long Windows takes to refuse a loopback connection** (about
  two seconds; effectively instant on Linux, where they would have behaved differently). They now
  use a socket that accepts and never answers.
- **`MaxToolCallsPerRun` had no test**: deleting its enforcement left the whole suite green. It has
  two now, one of which pins the documented overshoot.
- **The tool-call cap is not exact and the doc said it was.** It is sampled between MODEL calls
  because the meter never sees an invocation, so every call in one response runs and the cap stops
  the next model call. A model asking for four tools at once overshoots a cap of two by two.
- **`.env.example` claimed the agent model falls back to the chat model.** Each overlay falls back
  to its own fixed literal, so changing the chat model leaves the agent model where it was.

## 7. Phase 2's review gate: two bounded things that were not bounded (fixed)

The sceptic review of Phase 2 found two defects of the same kind, and both had shipped with a green
suite because the tests pinned a count rather than a composition. Both were reproduced against the
real runtime before being believed.

**The event feed never dropped a lagging subscriber, it silently thinned one.** The queue was
created with `BoundedChannelFullMode.DropWrite`, on the assumption that `TryWrite` reports a full
channel. Measured: with `DropWrite`, `DropOldest` and `DropNewest`, `TryWrite` returns **true** on a
full channel and discards the event; only `FullMode.Wait` returns false. So the dispatcher's
"drop the subscriber" branch was dead code, and a subscriber that fell behind silently missed events
while its stream stayed open. That is precisely the failure the design's own doc says it prevents, and
it arrived through the option chosen to prevent it. `FullMode.Wait` is now used, and the comment on
the queue records the measurement, because the name reads like the wrong choice.

*Nothing here waits, before or after.* The only writer is `TryWrite`, which never blocks whatever the
mode is; the mode decides only what it RETURNS when full.

**The trace's drop marker accumulated instead of being one row.** Each overflow round appended a
fresh marker to the buffer and left the previous round's in place, so at steady state the buffer
alternated real steps and markers. Measured: a bound of 1000 after 3000 steps held 500 real steps and
500 markers, each reporting a different total; a bound of 1 dequeued the step just recorded, so
`Record` returned a step that was not in the trace and `ToolsCalled()` was always empty, which would
have made the grounding check dangle every citation on that host. The marker is now a single row held
outside the buffer and updated in place, which is the shape the doc had always claimed.

Two corollaries came with it. `Recorded` and `Dropped` counted markers, so the numbers a reader saw
were of the class's own bookkeeping rather than of an agent's work; markers now take no sequence
number and are not counted. And `ToolsCalled()` read the surviving buffer, so a citation to a real
early call dangled on any run long enough to overflow; tool names are now remembered outside the
bound, where the set is bounded by the number of distinct tools the MCP server advertises.

**Seven more, all fixed:** feed events were delivered outside the dispatcher's lock, so two
publishing agents could interleave and a subscriber could see seq 7 before seq 6, which with no
catch-up buffer is indistinguishable from loss; the SSE `id:` was a bare sequence while two comments
claimed it carried the host instance, so a reconnect to a restarted host read as a gap; feed frames
were serialized with the shared web-defaults options, so "absent fields omitted" was false and about
two thirds of every frame was nulls; the proxy's streaming arm had no deadline on the HEADERS phase,
so a host that accepted a connection and never answered held the caller's request open forever with
no 503; a host dying mid-stream threw `IOException`, which neither catch named, so it escaped the
controller after the response had started; the truncation marker was appended past the byte cap, so
`ArgsBytes` was the cost of a capture before its marker rather than the cost; and the trace route read
its rows and its two totals in three separate synchronizations, so one response could contradict
itself.

**One defect in the fix, found by mutation-checking it.** The new drop test read the stream until it
ended, which under the old drop mode never happens, so it WEDGED the suite instead of failing it. A
hung suite is worse than an untested one, because the failure cannot be identified.

This section claimed in its first version that every feed read in the tests was then bounded. **That
was false**, and section 10 records what it cost: the three reads in `AgentEndpointTest` were not,
one of them not even decoratively. The claim was written from the reads that had just been fixed
rather than from a sweep, which is the same mistake as trusting a stale README.

**The shipped proxy client was executed by no test at all**, which is how three of the findings
above could exist in it. Every proxy test substituted a fake for `IAgentsClient`, which is right for
testing the controller and useless for testing the client, so the per-call budget, the unreachable
arm, the caller-cancellation arm, the streaming arm's headers deadline and its mid-stream failure all
ran nowhere. `AgentsProxyClientTest` now drives the real `AgentsClient` through its own
`HttpMessageHandler` seam, and the three fixes are mutation-checked: reverting each fails exactly the
test that names it, in bounded time.

Two of those tests had to be bounded by the test's own token, for the reason the drop test did:
without the deadline they pin, the stall they set up waits forever, and a wedged suite hides the
defect it exists to report. When the bound fires the exception type is wrong, so the failure is
legible.

## 8. The rest of the gate, run by hand

Two delegated attempts at the remaining dimensions were cut short by a session usage limit (three of
seven dimensions completed on the first, none on the second). So the six outstanding dimensions were
worked through directly instead. That is a weaker instrument than an adversarial panel and the
record should say so: it is one reader rather than six with a sceptic attacking each finding. What
it found, all fixed:

- **`agentMessage` was subscribable and emitted by nothing.** `AgentJournal.Message` exists because
  the swarm phase writes worker traffic through it, but nothing calls it today, so a subscriber
  filtering on that kind would wait forever for an event that cannot arrive. That is precisely the
  failure the feed's parser-not-compiler stance prevents everywhere else. Refusing the kind would
  break a client's filter the day the swarm lands, so the difference is REPORTED instead:
  `AgentEventKinds.Emitted` beside `.Names`, both on `GET /agent/status` as `emittedKinds` and
  `acceptedKinds`, named in the filter's refusal message and in the route's own parameter doc.
  Pinned by a test that fails if the two lists stop agreeing with the code.
- **`result_text` was the one non-camelCase property on the wire**, on a type where every other
  property is camelCase. Now `resultText`.
- **Two comments in one file contradicted each other about route precedence.** One said the router
  prefers a literal segment either way, the other implied the registration order is what does it. A
  literal outranks a parameter in ASP.NET's precedence whatever the order, so there is one true
  reason and it now lives in one place.
- **`AgentRegistry.Finish` claimed journaling before the cancel keeps the ending the LAST step.** It
  does not, quite: a model call already in flight records its own step when it returns, which lands
  after the ending. Preventing that would mean holding the registry lock across an inference call.
  The comment now says a trace can carry one step past its ending.
- **`waitingForUser` is declared, treated as live, and reached by nothing.** The only thing that
  would park an agent there is a conversation, which is deferred. Recorded on the enum, so nobody
  reads the state machine and expects to see it.

Checked and found clean, recorded so the next reviewer need not redo it: package versions align
across all four deployables (`ModelContextProtocol` 1.4.1, `Microsoft.Extensions.AI` and
`.Abstractions` 10.9.0, `Microsoft.Agents.AI` 1.20.0); zero em or en dashes in any line this branch
adds; no forbidden project names anywhere; the OpenAPI snapshot is additions-only across the branch
apart from Phase 1a's six documented removals; and the spec 3.3 control-plane table matches the
routes that exist, row for row, with the one deferred row marked as deferred.

One observation rather than a finding: the apiApp's setting-catalog governance filters on sections
prefixed `Fallen8:`, and the agent host's own sections are `Agents` and `Fallen8Target`. So
`Agents:Trace:*`, `Agents:Feed:*` and `Agents:Limits:*` are covered by NO reflection gate. That is
how `fallen-8-mcp` and `fallen-8-integrations` already work, so it is consistent rather than wrong,
but it means a typo in one of the host's own option names is caught by nothing.

**What was still unrun then.** No adversarial verification pass had run over any of section 8's
findings, and no independent reader had reviewed the fix commit `53abda96` or the cross-phase
coherence questions. That gate has since run over the whole branch, including this section's own
fixes; **section 10 is what it found**, and it found nine more, six of them the same class again.

## 9. Environment traps worth remembering

`Copy-Item` preserves the source file's `LastWriteTime`. Restoring a mutated file from a backup copy
therefore leaves the restored source **older** than the object file built from the mutation, so
MSBuild skips the recompile and the mutated binary stays in place. This produced three misleading
probe runs that looked like a fix not working. Touch the file after restoring, and verify against a
control arm before believing a negative result.

A mutation script that replaces the FIRST occurrence of a pattern is not reversible when the
mutation creates a second occurrence. Reverting the `IOException` catch produced two identical catch
clauses, and the restore then repaired the wrong one: the buffered arm gained a clause it never had
and the streaming arm stayed broken. The suite caught it, but only because that arm had just gained
a test. Mutate on anchors that stay unique in both directions, and diff against `HEAD` after
restoring rather than trusting the script.

A test that pins a DEADLINE hangs when the deadline is removed, so the mutation check wedges instead
of failing. Three tests here needed their own bound for that reason. A hung suite is worse than an
untested one, because the failure cannot be identified; the repo's flake rule says the same thing.

## 10. The delegated gate, and the nine defects it found (fixed)

The gate section 8 said still had to run did run: a panel over the whole branch, 67 readers across
the dimensions, each candidate finding then handed to sceptics told to REFUTE it. 61 candidates, 44
of them surviving verification. What follows is every one that changed behaviour or a claim, with
the measurement that established it. Each fix is mutation-checked: reverting it fails exactly the
test that names it, and the mutants that proved nothing are noted where they were instructive.

**The trace could exceed its own bound, once, on every trace that ever truncates.** The marker's row
was reserved only on rounds where a marker ALREADY existed, so the round that CREATED it dropped one
step and then added a row: a bound of 5 held 6 rows after the sixth step and was correct forever
after. Every existing bound test overshot by 20 to 500 steps, so all of them measured steady state
and none the first overflow. The overflow test is now made against the ROWS a reader gets rather
than the buffer alone, and `MaxSteps` is documented as rows. A bound below 2 is floored at 2 rather
than silently doubled: `Math.Max(1, room)` had made a configured 1 hold 2 rows permanently, which
is an operator's setting quietly overridden. Reverting only the condition and leaving the new `room`
is a mutant that proves nothing, because the two are behaviourally the same; the pre-fix shape has
to be reconstructed whole.

**A dropped feed subscriber was completed but left in the table**, which is half of dropping it and
the half that shows. Its channel is full and complete forever, so every later publish re-entered the
drop branch: measured, one slow reader and eleven further events logged **eleven warnings for one
drop**, left `SubscriberCount` reporting the stream as open, and held one of
`Agents:Feed:MaxSubscribers` until the reader disposed, which a reader that has stopped reading is
precisely the one not about to do. A host would run out of subscriber slots with no stream open. The
test that "covered" the drop asserted `SubscriberCount == 0` AFTER its `using` block, so it pinned
`Dispose` while its message described the drop.

**Three SSE reads in the endpoint tests were unbounded, and section 7 of this document claimed
otherwise.** `ReadFrame` compared `DateTimeOffset.UtcNow` to a deadline around a tokenless
`ReadLineAsync`, and such a loop cannot reach its own check, because the read does not return. Nor
does `HttpClient.Timeout` cover it: measured with the timeout at five seconds under
`ResponseHeadersRead`, a content read on a silent stream had not returned after thirty, under
TestHost and a real Kestrel alike, because that timeout covers the headers phase only. The
keep-alive test had no bound at all and is the ONLY gate on the keep-alive write: removing that
write wedged the suite for over 450 seconds instead of failing. There is no `[Timeout]` in this test
project and no `--blame-hang-timeout` in CI, so nothing else would have caught it. Every read now
takes the test's own token, as the change feed's own reader already did; with the fix the keep-alive
mutant fails in 33 seconds and names itself.

**The frame-shape test did not assert the shape.** It checked that the frame contained `"id: "`.
Measured: stripping `hostInstanceId` and its separator from the writer left the test GREEN, with a
live control arm proving the binary had been rebuilt. So the half of the id that makes it useful was
unpinned, on the one test whose stated purpose is that a change-feed client can read this feed. It
now splits on the last colon as the change feed's own test does, and asserts the prefix IS this
host's instance id, read from `GET /agent/status`.

**The citation check, Phase 2's headline, was delivered by code no test executed.** Every citation
test called `GroundingCheck.Count` directly, so the counts reaching a trace step and an ending event
ran nowhere: deleting the journal's whole citation block left the suite green. The distinction the
registry promises, that no citation check is NOT zero of each, was also untested; a cancelled run is
now pinned.

The same finding also called the ORDER a false claim, because the check was recorded after the
ending step and so the tail of every checked run was `[stateChanged, citationCheck]`. **The sceptic
refuted that half**, and the reasoning holds: no wire contract says the ending is last, the spec's
one sentence about a last step is about cancellation, where there is no check and the ending IS
last, and the only reader scans the tail for citation fields and is position-agnostic by
construction. The order was swapped anyway, because the check is a statement about the final text
and the final text exists before the run is marked ended, and because the new test now PINS the
ending as last rather than leaving the tail an accident. That is a deliberate ordering decision plus
coverage, not the repair of a false claim, and the first version of this entry called it the
latter.

**The journal read the state back off the shared record.** So an ending landing between setting a
state and journaling it made the step report the TERMINAL state: a completed run whose trace said it
changed to completed twice, and a spawn step carrying `cancelled` on an agent that had been alive.
Both transitions now pass the state they SET, so what a step REPORTS no longer depends on the race.

**The reason given for journaling outside the lock was separately false**, and two verifiers
measured it: it said publishing releases a reader's continuations onto the publishing thread, and
the dispatcher creates every subscriber channel with `AllowSynchronousContinuations` false precisely
so that cannot happen, and says it relies on that. Provenance corrected too: the reason was never
true rather than made false, because the channel defaulted to false before the option was written
down.

**And the first attempt at this fix moved the journal calls INSIDE the registry lock, which was
wrong.** Both verifiers of the false-claim finding said in terms that a reader should not fix it by
moving the call, because the placement is right for reasons the comment did not give: it would nest
registry, trace and feed locks, hold the registry across every subscriber write, and, decisively,
`Publish` calls a LOGGER on the publishing thread when it drops a lagging subscriber. A log provider
is somebody else's code and may block, which is exactly why the dispatcher keeps that call outside
its OWN lock, so holding the registry across it reintroduced the hazard one level up, in the same
round that fixed the drop it fires on. The move is reverted, the three real reasons are stated once
on `TryAdmit`, and the ordering window the placement costs is documented beside them rather than
closed: it is the same one-step window `Finish` already documents for a model call in flight.

**An evicted agent would be retained by its descendants, latently.** `AgentRecord.Parent` was a
strong reference that nothing cleared, so eviction removed a record from the listing while a
descendant kept it, its bounded trace and its undisposed token source alive, transitively up the
ancestry, while `Evict`'s own comment claimed the collector took them. The stated justification for
keeping the link, that a spawn step is worth writing to an evicted parent anyway, is wrong on its
own terms: no route can read that parent's trace.

**The sceptic REFUTED this one, and was right about the part that matters.** No shipped path
supplies a parent today: the spawn route refuses a caller-supplied `parentId` with a 400, and the
orchestrator's swarm tool that will supply one is Phase 4. So `Parent` is null on every record a
deployment currently holds, only this feature's own tests build a chain, and the retention cannot
occur. The fix is kept because an eviction contract that depends on an unrelated route's validation
to be true is one route change away from being false, and Phase 4 is that change. But the first
version of this entry, and the commit that carried it, described a live leak retaining megabytes.
It is latent, the code comment and the test now say so, and this paragraph is here because getting
that wrong is the same defect class as everything above it.

**The renamed chat-model key did not fail closed, and the spec promised it did.** `Model` became
`Models:Assist` with no alias, and configuration binding ignores a key no property claims, silently.
On Ollama and Nahil, whose `Models:Assist` carries a default, an operator who missed one key during
the rename had their model REPLACED by a stock one on every request and was told nothing: a
fine-tuned assist model swapped for the sidecar's default, which is the worst shape a configuration
fault can take, because the instance keeps answering. `ChatBackendFactory.StaleModelKey` now reads
the RAW configuration, the only way to see a key that binds to nothing, and refuses the SELECTED
block by name through the same `Validate` the boot warning and the 503 already share. No new
configuration surface and no catalog entry: the key exists to be refused, not to be set.

**The role-prompt test did not pin the property that decides whether tool calling works at all.**
Property 1 was covered by `prompt.Contains("tool")` and `Contains("call")`, both of which hold from
the citation paragraph alone. Measured: deleting the ENTIRE "You have tools" paragraph from all
three shipped prompts left the test green, and that paragraph is the one Phase 0 measured as
deciding whether a call parses rather than arrives as literal text with a fabricated result. The
three claims the paragraph actually makes are now asserted in the words that carry them.

**One misleading test, no product defect.** `RemoteModelTargetTest` asserted a refusal naming
`Fallen8:Chat:OpenAI:Model`, constructing the target with a chat section key and letting the model
key default. Production never produces that sentence, because the chat factory passes the purpose
key explicitly; the default belongs to the embedding blocks, where `Fallen8:Embedding:*:Model` is
real and catalogued. Left alone, a reader would take it for the live chat contract. It now exercises
the embedding block that relies on the default, plus the chat block naming its purpose.

**Two more findings this round verified and this pass also fixed**, both documentation: `CountStep`
was documented as returning the total the runner compares against the budget, and the return value
is discarded by both call sites while the comparison happens in the meter from its own fresh reads
before the next call, which named the wrong component, the wrong moment and a consumer that does not
exist. And `CapWithMarker` promised the whole result stays inside `maxBytes` while the branch thirty
lines below returns the marker alone: measured, every cap from 1 to 31 bytes yields 32 bytes for a
500 byte input, and the threshold moves with the reported total. Both summaries now say what the
code does; the behaviour is deliberate in both cases.

**What the seventeen refutations were worth.** Two of them landed on findings this pass had already
acted on, which is the argument for the sceptic arm rather than against it: one turned a live leak
into a latent one, and one turned a false-claim repair into an ordering preference. A third said
plainly that the right fix for a false comment is the comment, and this pass had moved the code
instead, reintroducing a hazard the same round had just removed elsewhere. A reader who takes a
verified finding's own framing at face value inherits its overstatement; the verdicts have to be
read too, including the ones that agree.

**What this round says about the earlier ones.** Six of the nine are a test that passed while the
behaviour it named was absent or wrong, which is the same class section 7 found and section 2 found
before it. The pattern is specific enough to act on: an assertion written against the value the code
under test had just produced, rather than against the contract. `StringAssert.Contains(frame,
"id: ")`, `prompt.Contains("tool")`, and `SubscriberCount == 0` read after a `using` block are all
the same mistake. The cheapest guard is the one used throughout this round: break the thing on
purpose and watch the test fail with its own message.
