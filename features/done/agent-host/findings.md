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
were of the class's own bookkeeping rather than of an agent's work; markers now consume no sequence
number of their own and are not counted. They do CARRY one, borrowed: the sequence of the last step
they report on, so the marker and the step after it read as consecutive. This said "take no
sequence number", which the code doc now contradicts in terms, and trimming the marker's `Seq` on
the strength of it would break that consecutive-sequence contract. And `ToolsCalled()` read the surviving buffer, so a citation to a real
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
adds; no forbidden project names anywhere; and the OpenAPI snapshot is additions-only across the
branch apart from Phase 1a's six documented removals.

**One item in that list was itself wrong**, which is worth leaving on the record rather than
quietly deleting. It claimed the spec 3.3 control-plane table "matches the routes that exist, row
for row". It did not: every row spelled the route `/api/v0.1/agents...` while the shipped surface
is unversioned `/agents...`, because the controller's actions carry absolute templates that discard
the class-level version prefix. Seven of the eight rows were unreachable as written, and the table
contradicted its own prose four lines above it. The check compared the ROWS to each other and to
the deferral note rather than to the snapshot, which is the only thing that could have caught it.
Section 13 records the fix; the lesson is that "verified row for row" has to name what the rows
were compared AGAINST.

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

## 11. The coverage half of the same gate (fixed)

The remaining verified findings were all one shape: shipped code that no test executed, several of
it code added to fix a recorded incident. Each fix below is a test, and each is mutation-checked by
breaking the thing it names.

- **The spawn step on the PARENT's trace was gated by nothing.** `childId` appeared nowhere in the
  test project, and deleting the block left all 152 agent tests green. Its stated purpose is that a
  swarm is readable from the orchestrator's own trace, so the phase that makes it load-bearing is
  Phase 4; it is pinned before then rather than after.
- **The out-of-range run-cap clamp had no test, and neither did `Rescue`.** Both were added for a
  recorded incident: `CancelAfter` refuses a delay past about 49 days, so a `MaxRunSeconds` an
  operator meant as "no cap" threw where no catch could turn it into an ending, and the agent sat at
  `pending` holding a concurrency slot for the life of the process with nothing in the log. Across
  the whole suite `MaxRunSeconds` was only ever 1 or 300, both far below the armable maximum, so the
  branch ran nowhere. `Rescue` had zero references, and its own doc ("it should never fire, and that
  is exactly why it exists") is the argument for pinning it. It is reached now through the one
  stretch of a run that its own try does not cover, and removing it reproduces the incident's exact
  signature: *the agent stayed at pending, holding its slot with nothing recorded*.
- **The feed's `agents=` filter never ran through the route.** `kinds=` was covered end to end;
  `agents=` had only the proxy forwarding a literal string and `AgentFeedFilter.Admits` called
  directly, so nothing connected the query parameter to the filter applied at publish. Binding it to
  a key nobody sends leaves both of those green while a subscriber asking for one agent receives the
  whole host's traffic, which is also how that subscriber then gets dropped for lagging.
- **The writer's reaction to an ended subscription ran nowhere.** The unit tests assert the
  SUBSCRIPTION returns null and stop there. Turning the writer's `break` into a `continue` is the
  spin loop itself, and it is now a failing test rather than a green suite.
- **The trace route's own headline case was untested.** It is named for saying whether a trace is
  whole and only ever asserted the whole case, because nothing set `Agents:Trace:MaxSteps` over
  HTTP: `dropped > 0` was produced by no test, so the drop marker's serialization was covered
  nowhere. Both halves are pinned now, including that recorded minus dropped equals the real rows
  handed back.

**One of these could not be fully closed, and the test says so rather than implying otherwise.** The
detail route's documented 20-step tail cannot be pinned over HTTP in this phase: no model is
reachable from these tests, so a run produces about four steps and the cap is unreachable. What is
pinned is the tail-versus-whole distinction on a deliberately truncated trace. Handing back the
whole trace instead of the tail therefore still survives mutation, and that is recorded here because
a coverage limit nobody writes down reads afterwards as coverage.

**A defect in this pass, found by mutation-checking it.** The first version of the writer-exit test
overran a subscriber's queue to produce the null. That looked simpler and was wrong: the writer
drains the channel into the response as fast as it is filled, so whether the queue ever overflows is
a race. It passed three times, then failed under an unrelated mutation, which is how it was caught.
Disposing the dispatcher reaches the same null deterministically, and the baseline was then measured
three times before the mutants ran. The repo's flake rule is the reason this is in the record
instead of quietly rewritten.

## 12. The last five, and where the gate stands

- **`GET /agent/status` reported `lastSeenBackend` and `lastSeenModel`**, two sibling scalars, where
  spec 3.2 and the 3.3 status row both name `lastSeen { backend, model }`. A consumer written from either read
  `chat.lastSeen.backend` and got nothing on a host that had served many steps. The code was
  changed rather than the spec, because the nested shape is also the truer one: the two are one fact
  about one step, and its absence says "nothing has served a step yet" once instead of twice. The
  HTTP test could only ever reach the ABSENT case, since no model is reachable from these tests, so
  the shape is pinned where the value exists: renaming it back leaves the route test green, which is
  how the wrong shape survived two reviews.
- **The posture route omitted `maxTokenBudget`**, the one cap that silently rewrites a caller's
  request: it CLAMPS rather than refuses, so a client pre-validating a spawn form had no way to
  learn it and a user saw a budget the run never got, while the route documented itself as reporting
  the caps a run is held to.
- **The chat gateway's reachability is ONE startup probe and nothing refreshes it**, while the proxy
  documented it as the first thing to read when an agent fails. In the case the probe's own doc
  calls ordinary, compose starting the host before the instance answers, it reported `unreachable`
  for the life of the container while every agent ran fine. It now carries the moment it ran, the
  route says what it is, and a reader is pointed at what IS live. Refreshing it from the adapter was
  rejected: the probe and the last completion are two different facts, and the type's own doc says
  conflating them would let a host that has never run an agent report a model. The state and its
  timestamp are one immutable object behind one volatile field, which is what the adapter already
  does for its provenance and for the same reason, a mismatched pair being worse than a stale one.
- **The `Emitted` guard did not observe the code it was described as pinning.** Both a commit
  message and this document said it "fails when the two lists stop matching the code"; it checked a
  subset relation between two hand-written lists and pinned their counts. So the one drift direction
  that was explicitly worried about, the swarm starting to call `AgentJournal.Message` while
  `Emitted` still omits `agentMessage`, failed nothing, and `GET /agent/status` would have told every
  client that `agentMessage` cannot arrive while it was arriving. It now reads the product sources
  for a caller of that method, in the same spirit as the convention gate, and fails in BOTH
  directions: advertising a kind nothing emits, and emitting one that is not advertised.
- **The spawn step kind is overloaded and said so nowhere.** It is the first step of every trace
  ("this agent was spawned") and also a row on a parent's trace ("this agent spawned that one"), told
  apart by `childId` and by nothing else, so a client rendering the documented kind showed "a1-17
  spawned nothing" as the first row of every agent that spawned nothing.
- **The per-step provenance rule was narrated in full at four sites**, and the byte-cap rule at two.
  `TraceStep.Backend` and `CapWithMarker` own them; the others are pointers now, which is the
  repository's own rule and the reason a later change to either will not leave three sites asserting
  the old one.

**Where the gate stands.** Of the 61 candidates the delegated panel produced, 44 survived
verification and all 44 are now addressed: fixed, or reframed where a sceptic was right that the
finding overstated its case. The 17 refutations were worth reading too, and section 10 records what
two of them cost. **Two things are still open and named rather than implied:** the detail route's
20-step tail cannot be pinned over HTTP until a model is reachable from a test (section 11), and no
independent reader has reviewed THIS round of fixes, which is the same gap section 8 recorded about
its own. The pattern across three gates is stable enough to state plainly: the defect this feature
produces most is a test that passes while the behaviour it names is absent, and the cheapest guard
against it is to break the thing on purpose and watch the test fail with its own message.

## 13. The doc-drift cluster, and a review OF the fixes

Two things belong here that were only in commit messages. The first is the doc-drift and
false-claim cluster the gate found: nineteen items, fixed in one pass, recorded in
`cc0664e7` and nowhere in this document, which is why section 8's pointer had nothing to resolve
to. The headline ones: the 3.3 control-plane table spelled every route `/api/v0.1/agents...` while
the shipped surface is unversioned, so seven of its eight rows were unreachable as written; section
7's impact table still claimed eight proxied routes in four places after one was deferred, and
offered a per-route gate entry where the prefix rule shipped; the status line said "spec only (no
implementation yet)" with four phases landed; `CLAUDE.md` described two REST-only deployables and
never mentioned this one; the capability gate's 403 was asserted unconditionally at four sites in
the branch whose plan says the repo's docs widely get exactly that wrong; and the premise spec
3.2a's own amendment reversed was still stated as fact at five product sites, one of them shipping
verbatim in the published OpenAPI document.

**Then the fixes were reviewed, which is what section 12 said was still owed.** Five readers over
the five fix commits, each candidate handed to a sceptic. Twelve of the sceptics died on a session
limit, so twelve candidates were verified by hand instead, against the code, and that is a weaker
instrument worth naming. What it found, all fixed, and note how many are defects IN the fixes:

- **The stale-key guard refused a key the published REST contract still told operators to write.**
  Three apiApp sites named `Fallen8:Chat:<Backend>:Model` as the live write target for a name chosen
  from `GET /chat/models`, two of them landing in the shipped OpenAPI document. Phase 1a's rename
  sweep missed them; the guard turned that miss into an outage, because an operator following the
  documented instruction would brick chat on the next restart. **The worst defect of the round, and
  this round created it.**
- **`TryAdmit`'s decisive reason was contradicted 55 lines below it, in the same method.** The doc
  names a blocking log provider as why journal calls stay outside the registry lock, and the
  token-budget clamp was calling `_logger.LogInformation` inside it. The clamp now remembers and
  reports after the release; nothing logs under `_gate`.
- **The stale-key guard's WIRING was executed by no test.** Its unit test hands `Validate` its own
  in-memory configuration, so it can only prove the pure function refuses. De-wiring the controller
  left that test green while both 503s and the boot warning vanished. A hosted test now covers it,
  and writing it found a second defect: the first version asked for `/api/v0.1/chat/models`, read
  the 200 that answered as the guard being skipped, and nearly produced a report that the fix did
  not work.
- **A test comment still said the registry journals under its own lock**, which was true for exactly
  one commit before the move was reverted. It was the last artifact in the tree saying so.
- **"Seven of the printed limits treat a non-positive value as off" undercounted by one**, and the
  eighth was the one still printing a raw `0`. `DefaultTokenBudget` switches off too: a spawn naming
  no budget takes it, and the meter enforces only a budget above zero, so a default of 0 is no token
  cap at all. The test hard-coded the same undercount and set seven limits, deliberately leaving
  that one alone.
- **Spec 3.3 re-asserted the capability-gate mechanism the same commit was deleting from four code
  sites as false**, and in a form that contradicts itself: a caller challenged before the capability
  is read would make the KEYED instance answer 401 too. Two readers found this independently.
- **The window past an ending is bounded by the writers in flight, not by one step.** A cancel during
  a model call lets that call journal one, and the tool call its response asked for journal another,
  because a call already sent to the graph is deliberately not undone.
- **`ChatGatewayPosture` stored its pair atomically and then exposed it as two reads**, which moves
  the seam rather than closing it. One accessor returns both, and the status route uses it.
- Smaller, all real: the spawn kind's "first step of every trace" against the shutdown race the same
  round documented; three sites claiming spec 3.3 names `lastSeen { backend, model }` after this
  branch rewrote that row to prose (the row names the shape again, which is the more useful half);
  section 7's "markers take no sequence number" against the code doc that now says they carry a
  borrowed one; plan.md's "only the metrics box is outstanding" when three boxes are open and one is
  not metrics work; a Phase 3 tick claiming wall clock on `agentStateChanged`, where
  `AgentEvent.DurationMs` is set on endings and tool calls and nowhere else; `CLAUDE.md` grouping
  this host under deployables that reach the graph over REST, which it does not do at all, and
  asserting a container posture that has no Dockerfile yet; the MCP deferral for `GET /chat/models`
  justified by the retired key; and a harness knob nothing passed, left behind when the test that
  needed it was rewritten.

**What this round is evidence for.** Nine of the fourteen are defects introduced or left by the
fixes themselves, and three of those are a fix whose own comment or doc is now false. The rule the
earlier sections arrived at needs one addition: after fixing a false claim, grep the tree for the
claim, not just the file. Two of the worst here (the retired key, the re-asserted gate mechanism)
were a correct fix applied at one site while the same sentence lived on at three or four others.

## 14. The merge gate (2026-09-19), and what it found

**How it ran.** A council of eight read-only reviewers, one dimension each (swarm, metrics,
packaging, Studio, published claims, regressions, test quality, security), over the whole branch at
`dce4e70b` with the weight on the eight commits after `22d3a650` that no gate had seen. Six
reviewers returned 38 candidates. The two others (metrics, regressions), the clerk that merges
duplicates and all 64 verification agents died on the account's usage limit, so **every candidate
below was verified by hand from the code at HEAD**, refute first, the way the panel was instructed
to. The two lost dimensions were covered where their work is mechanical: the old-key sweep of the
tree (clean, with a positive control), the container built from the repository root and run
read-only with a `/tmp` tmpfs (up, `curl` present for the healthcheck, `/health` 200, an unreachable
MCP server leaving the host up with zero tools and a status route that says so), and compose
resolved with the `agents` profile (every variable bound, no `ports:`, `f8-mcp` in the base file).
The metrics reviewer's ground was partly walked by the test-quality reviewer (F27, F29, F32) and the
claims reviewer (F18).

**Verdict: HOLD.** Nothing on its own is a blocker in the sense of data loss or an unauthenticated
hole the sibling sidecars do not already have. But nineteen findings are real and rated major, and
the council's rule is that a branch merges when its findings are fixed and the fixes re-reviewed.
The shapes are the two every earlier section predicted: a claim stated somewhere other than the
code, and a test whose assertion is satisfied by the failure it names.

### Swarm correctness (major)

- **F01. An orchestrator that ends any way but a cancel orphans its live workers.** Only
  `TryCancel` cascades; `Finish` records the ending and touches no child. Workers are admitted after
  their orchestrator, so their deadlines are later, and an orchestrator awaiting slow workers hits
  `MaxRunSeconds` first, every time: it ends `budgetExceeded`, its workers run on for up to 1800 s
  each, holding three of four slots and spending their budgets for a composer that is gone. Same
  outcome when the model answers without awaiting, or trips its step or token budget after
  spawning. `AgentState.Cancelled`'s doc ("by its orchestrator going away") promises the cascade the
  code does not perform, and no record calls the orphan deliberate. Fix: cascade on every terminal
  transition of an agent with live children, or record the orphan as a decision with its sizing
  consequence; pin with an orchestrator at `MaxRunSeconds = 1` awaiting a 30 s worker.
- **F02. A spawn racing a cancel creates a worker no cascade reaches.** `TryAdmit` checks that the
  parent EXISTS, not that it is live, and a finished orchestrator is retained for an hour;
  `TryCancel` snapshots `Descendants` once. This class's own doc says a tool call already
  dispatched when a cancel lands still runs, so `spawn_worker` can admit a worker after the
  snapshot, or after the orchestrator is already `Cancelled`, and it runs to its own ending with a
  cancelled parent. Fix: refuse a parent that is not live, and sweep until `LiveChildren` is empty.
- **F03 (with F16, F28). A refused `spawn_worker` is journaled as `success: true`.** `Spawn`
  returns the cap refusal as ordinary text, and `AgentRunner.Invoke` records any normal return as a
  success, so the trace step, the `toolCalled` event and `f8a.agents.tool.calls{success=true}` all
  report a breached cap as a call that worked. agents.md, spec 3.5, plan.md and the `Spawn` doc
  comment all say `success: false`; the test on the path never asserts `Success`. Decide the
  contract once, correct every site, and assert the flag in both cap tests.

### Packaging and deployment

- **F06 (major). `f8-agents` waits for `fallen8` but not for `f8-mcp`.** Both sidecars start the
  instant the instance is healthy; the host connects to the MCP server ONCE with a 15 s budget and
  nothing re-probes it. A slow cold start leaves every agent for the life of the container with no
  tools, and the fix is one `depends_on` line, since `f8-mcp` has a healthcheck.
- **F07 (major). The agent-model pull is justified three times by a deployment that cannot
  exist.** `docker-compose.yml`, `.env.example` and `ollama-init.sh` say the pull matters "on a
  hosted-chat deployment that still wants local agents"; there is ONE `Fallen8:Chat:Backend` for
  both purposes, so agents on an OpenAI or Anthropic instance are served by that provider. On those
  overlays `F8_AGENTS=true` downloads a multi-gigabyte model nothing will ever request, and the
  comment tells the operator their agents run locally while they are metered.
- **F08 (major). `ollama-init.sh` names the Nahil key.** The Ollama sidecar's own script tells an
  operator wanting a tool-capable agent model to set `Fallen8__Chat__Nahil__Models__Agent`; the
  sidecar serves chat only when the backend is Ollama, whose key is the Ollama one. Following the
  comment is a silent no-op.
- **F09 (minor).** `env-up.js` prints "an agent that can only read cannot be talked into a write"
  while checking only the write and admin tiers; the code tier alone compiles operator-supplied C#
  into the engine process.
- **F10 (minor).** `running.mdx`'s list of published images omits `-agents`.
- **F11 (minor).** `F8_AGENTS` is compared case-sensitively by both scripts and case-insensitively by
  the instance's Boolean binder, so `F8_AGENTS=True` opens the `/agents` routes with no sidecar while
  `env:up` says they refuse.

### Tests that cannot fail (major)

- **F04 (with F33).** No swarm test sets `MaxRunSeconds`; the only cancel-inside-await case goes
  through the cascade, which completes `Task.WhenAll` by itself. Delete `.WaitAsync(cancellationToken)`
  from `Await` and the suite stays green while an orchestrator's deadline stops applying inside
  `await_workers`.
- **F13.** The per-purpose card rows and their "not set" state have no test at all: no fixture
  carries `agentModel`, nothing asserts "model (agent)". Swap the two values and everything passes.
- **F29.** The one test that drives `AgentsMetrics` through `AgentJournal` guards itself with
  `recorded.All.Count > 0`, and the observable gauge satisfies that alone. Delete the three
  `_metrics?.X(...)` calls in the journal and the test named for the wiring stays green.
- **F30.** The budget-separation test asserts the orchestrator's tokens are `< 5000`; charging the
  worker's 15 tokens to it gives 60, which is also `< 5000`. Only the worker's `TokenBudget` is
  pinned. Assert the exact split.
- **F31.** Neither typed-result test asserts that the worker's ANSWER reaches the orchestrator:
  they check the id and the substring "state". Drop `Result = worker.ResultText` and the swarm tests
  pass with an orchestrator composing from nothing.

### Claims that are false at HEAD

- **F17 (major).** agents.md: "Every non-positive value above switches its cap off". Four keys in
  that table are floored at 1 instead (`Fallen8Target:TimeoutSeconds`, `Agents:Mcp:ConnectTimeoutSeconds`,
  `Agents:Feed:KeepAliveSeconds`, `Agents:Feed:MaxQueuedEvents`), so an operator who sets the
  target timeout to 0 for "no deadline" gives every model call one second and every agent fails on
  its first step. The startup line prints the eight `Limits` values and none of the swarm, trace or
  feed caps, so "the startup line says unlimited" does not hold for them either.
- **F18 (major).** agents.md: "no agent name and no agent id reaches a metric tag". The host's own
  meter honours it. The runner passes the caller's `name` (or the unbounded id) as the framework
  agent's `Name`; the framework names its span `invoke_agent {name}`; `env:up` always applies the
  observability overlay, whose spanmetrics connector keys series on `span.name`. Every run is a new
  Prometheus series in the shipped stack. Pass the role, a closed set, to the framework and keep the
  caller's name on the record and the feed.
- **F19 (major).** agents.md, spec 3.8 and `AgentsOptions` say an empty `Agents:Roles:<role>:Tools`
  means every advertised tool. `RoleCatalog.Load` falls back to the SHIPPED allowlist on empty, so
  the orchestrator keeps `f8_overview` and nothing widens it short of listing every tool.
- **F27 (major).** `TheFrameworksOwnGenAiTelemetryNamesAreWhatTheExporterRegisters` asserts a
  constant equals its own literal and observes no framework telemetry; `AgentsMetrics.cs` and
  `AgentsObservability.cs` say the name is "measured rather than assumed, and a test pins it".
  Whether the second `AddMeter` does anything at all is settled by the observing test the fix has to
  add, not by either comment.
- **F34 with F35 (major).** The stated reason the host authenticates nobody is false. The Dockerfile
  says "the only caller that can reach this listener is the apiApp on f8-net"; spec 3.7,
  `AgentsController`, `Fallen8AgentsOptions` and `AgentsOptions` derive "no second auth story" from
  it. Every member of `f8-net` can reach it (the instance, both sidecars, Ollama, and with their
  profiles docling and the NLP sidecar, which process untrusted input), and it holds the instance's
  key and the MCP bearer. The startup line then logs "This port is not published", a compose
  property the process cannot see, and prints it unchanged under a bare `dotnet run` on `0.0.0.0`.
  The posture itself is the integrations runtime's, so it is a house convention and not a new hole;
  the CLAIMS about it are this branch's. Correct them everywhere, log only what the process knows,
  and add the host to `security.mdx` beside the MCP paragraph as the second credential-holding
  listener the API key does not close.
- **F36 (major).** `POST /agents` is the one body-taking sensitive route with no `[RequestSizeLimit]`
  on either hop, and `task` and `name` are the only captures in this feature with no byte cap: an
  authenticated caller can retain 200 agents of 29 MB each for an hour, every `GET /agents` returns
  all of it, and `name` rides on every feed frame. `security.mdx` states the 1 MiB invariant with two
  named exceptions; this is an unnamed third.
- **D1 (major, found before the council).** `GET /agents/status` reports neither `maxSwarmDepth` nor
  `maxWorkersPerOrchestrator` in `limits`, while agents.md says it reports "the caps a run is held
  to". The status test asserts a hand-picked field list, so it could not notice. This is the class
  section 12 already fixed once for `maxTokenBudget`.
- **F05 with F20 (major, textual).** Comments that describe the swarm as a future phase: the
  `IsSpawnableByACaller` summary says the orchestrator is refused and the body returns true;
  `RoleCatalog` line 88 says "the registry implements" the swarm tools (`SwarmTools` does);
  `AgentRecord.Parent`'s doc says "No shipped path supplies a parent today" and calls the swarm tool
  Phase 4; `Children()` carries two `<summary>` blocks, the first stale; `AgentEndpoints` and
  `AgentFeed` still call the swarm a later phase that will emit `agentMessage`.
- **F26 (minor).** agents.md and `Fallen8AgentsOptions` say the feed "takes none" of
  `TimeoutSeconds`; `AgentsClient` applies it to the feed's headers phase and its interface doc
  already corrected the sentence once. The section 13 rule, grep the tree for the claim, was not
  applied.
- **F12 (minor).** The card decides "not set" with `??`; the write validator accepts `""` for a
  string key and `ResolveModel` passes it through, so the one state the row exists to reveal
  renders as a blank cell, and the comment above it says otherwise.
- **F32 (minor).** `AgentsMetrics.Observe`'s doc says a negative reading is never published and a
  faulting source produces no sample; the code publishes any value and reports 0 on a fault, which
  its own test asserts.
- **F14 (minor).** Two comments in the picker test say Nahil ships the agent model empty. It does
  not; only OpenAI and Anthropic do.
- **F21, F22, F23 (minor).** The feature README's status line still says phase 5 in progress;
  running.mdx and the root README still say "every feature is on" of an environment where this one
  is off by default; semantic-traversal.mdx links the word "agent" in the tools section to the MCP
  server page instead of `/agents/`.
- **F37, F38 (minor).** `security.mdx`, the one home for the capability posture, counts four
  switches and lists neither Integrations nor Agents, and states the unconditional 403 this branch
  corrected elsewhere; `DynamicCapabilityAuthorization`'s class docs assert the same unconditional
  pairing forty lines above the enum doc that corrects it.
- **F24, F25 (minor).** The feature README narrates Quickstart, Roles and Security posture at the
  same length as the docs page, and the two already disagree (README on roles versus agents.md 173);
  the REST-family convention test's literal class excludes `?`, so a graph route written with a
  query string escapes the pin the docs say cannot be escaped quietly.

### Not counted

- **F15.** The off-state cards' "answers 403" copy predates this branch (July) and is the
  repository-wide 401/403 wording debt; worth fixing while `ConfigurationPanel.tsx` is open, not a
  gate finding.
- **F16, F28** are F03; **F33** is F04; **F20** is folded into F05.
- The container runs as uid 0, as both sibling sidecars do: a house matter, recorded here and not
  charged to this branch.

**Two things about the gate itself worth keeping.** First, a workflow whose verification stage dies
reports its candidates in the wrong bucket: the script's `refuted` array held 38 items marked
`unverified` and its `confirmed` array was empty, which a reader in a hurry would take as a clean
bill. Read the failure list before the result. Second, reviewers cite wrong line numbers while
being right about the claim: F35 pointed at line 432 of a 357-line file, and the sentence was at
213. Verify the claim, not the citation.

## 15. What the gate's findings were fixed with (2026-09-19)

Every finding in section 14 is closed. Eleven commits, each one cluster, each mutation-checked:
the seven harnesses written for this round define 44 mutants, every one applied and killed, and four
more were applied by hand on the Studio side, where there is no harness table to point at. Where a
mutant survived, that is recorded below rather than left out. The count is of the harness tables,
because those are preserved and the per-mutant run log is not. This line has now been wrong twice:
it said 46 by counting three harnesses from the implementation phase, which predate the gate, and
then 40 by leaving out the harness for the F37 gate that a later commit in this same round added.
The per-cluster numbers below add up to the total, which is the only reason a third error would be
visible. The design for each cluster was written by a read-only reviewer per cluster and then
applied by hand, which is how the two measurements that changed a decision were caught.

**Two designs were refuted by measurement before they were applied.** F18's suggested fix was to
hand the framework the ROLE instead of the caller's name, which bounds the span name. Probing all
four shapes of (Id, Name) showed the library builds `invoke_agent {name}({id})` and generates a GUID
when no id is supplied, so no choice bounds it: the id is in that name unconditionally. The fix
therefore has two halves, one in the emitter and one in the shipped collector, and the published
sentence says what still travels. And F27's claim that the second `AddMeter` prevented dropping the
library's token accounting is backwards: all four of the library's GenAI instruments publish on the
source name the runner passes, so the registration was a no-op and the constant naming the library
default is gone.

### The fixes, by finding

- **F01, F02 (swarm lifecycle).** Every ending cascades to the live workers it orphans, Completed
  included, because the orchestrator prompt says nobody else reads a worker's output. Admission
  refuses a parent that has ended, in the same lock section that writes an ending, which is what
  makes the cascade complete in one downward pass with no sweep. `Finish` is a facade over one
  ending path, so the cancel path and every other path cannot drift apart again. Seven mutants.
- **F03, F16, F28 (a refusal recorded as a success).** A typed `ToolRefusal`: the invoker journals
  it as a failed call and hands its message to the framework as an ordinary result. Measured from
  the pinned package: throwing would end the RUN on the fourth consecutive breach, because
  `MaximumConsecutiveErrorsPerRequest` is 3 by default, so the four `success: false` claims stay
  true and a breach still cannot end a run. The spawn tool needs an identity `MarshalResult` or the
  refusal arrives as JSON and is recorded as work. Five mutants.
- **F04, F33 (the await's own deadline).** A test with an orchestrator whose deadline fires while it
  awaits a worker nothing will ever finish, which is the only arrangement where the await's token
  decides the outcome. The cancel test's gate moved to the orchestrator's second model call, because
  a worker is in the registry before `spawn_worker` returns.
- **F05, F20, F26, F32, F38, F21, F22, F23, F24 (text).** Corrected at every site, including the one
  in the published OpenAPI document (regenerated, one line). F32 moved the doc rather than the code,
  because a sign check on a live count is unreachable and a gauge that publishes nothing reads like
  a scrape failure. The feature README's three duplicated sections are pointers now.
- **F37 (the half that was missed).** Its code half landed with the cluster above: the capability
  requirement's class doc no longer asserts that a gated endpoint needs an authenticated caller
  unconditionally. Its published half did not, and nothing caught that until a pass over section
  14's finding ids found F37 named nowhere in this section. `security.mdx` counted four capability
  switches where the authorization layer enforces six, promised an unconditional 403, and listed
  neither of the two the gate was about. It now counts six, says which refusal a keyed and a keyless
  instance give, and carries a row for the integrations and agents switches. Checked against the
  code rather than the page it corrects: six policies take a capability requirement, five of the
  five flags behind them have no initializer and nothing in `appsettings.json` sets one, so a bare
  run really does answer 401 for all five. The count is the one part of that page a test can hold,
  so a convention test now holds it: the page's number against the enum the authorization layer
  switches on. Four mutants, killed. A seventh capability with the page left alone fails the suite,
  and so does removing the count from the sentence, which is the way this gate could have been
  worse than no gate.
- **F06 to F11 (packaging).** One `depends_on` edge for the MCP server, whose handshake is one-shot;
  the pull rationale corrected at three sites and the wrong backend key at one; `F8_AGENTS` parsed
  the same way by all three consumers, with an unknown value refused; the code tier counted in the
  read-only reassurance; the `-agents` image added to the published list.
- **F12 to F15 (Studio).** A blank model is normalised to no model at the one funnel every reader
  goes through, and the card trims too for version tolerance. The per-purpose rows have four tests.
  Four mutants, including the partial fix that only catches the empty string.
- **F17, F19, D1 (caps and allowlists).** The status route reports every cap on the options type,
  pinned by reflection rather than a hand-picked list; the four floored keys are documented as
  floored and the deadline in force is what is printed and reported; an allowlist can be widened on
  purpose with `*` and no longer by a typo, because blank entries are dropped BEFORE the
  was-anything-configured decision. Nine mutants.
- **F18, F27 (telemetry).** See above. Verified end to end against the running collector: a span
  named `invoke_agent MARKERNAME(a1789-99)` yields one Prometheus series named `invoke_agent` with
  no caller text and no id, while a control span the transform does not match keeps its full name,
  which is what makes that zero mean something. Tempo keeps the full name. Three mutants, one of
  them the refuted role-only fix.
- **F25, F29, F30, F31 (tests that could not fail).** The exact token split, the deserialized worker
  result with five distinct numbers, the three named instruments plus a test that drives the SHIPPED
  service graph, and a route-literal regex that tolerates a query string and self-tests against the
  shapes that escaped it. Seven mutants.
- **F34, F35, F36 (posture).** The trust boundary has one code home and one published home, and
  every site that derived a conclusion from the false version points at them, including four in the
  integrations sibling carrying the same sentence. The startup line states its bind and warns on a
  non-loopback one. `POST /agents` carries the ordinary 1 MiB bound, the host bounds its own body
  above the proxy's, and task, name and appendix are bounded per field. Nine mutants.

### What was NOT fixed, and why

- **F15** is fixed only in the two Studio cards it names. The unconditional-403 wording is a
  repository-wide debt (dozens of sites listed in the cluster's design) and the gate itself recorded
  it as not counted; sweeping it belongs to its own change.
- **`UseProvidedChatClientAsIs` remains unpinned.** Setting it to false passes every test in the
  repository, because the library's outer loop finds every call already resolved by the inner one.
  The runner's comment claimed two loops would each invoke every call; that is measurably false and
  the comment now says which part no test covers. The flag stays for the reason the library
  documents.
- **The host's Kestrel body bound is pinned as WIRING, not enforcement**, because the test server
  has no request-body-size feature. The integrations sibling's identical bound is pinned by nothing
  at all.
- **`AddAgentsObservability` is still exercised by no test**, so the exporter's meter and source
  registrations are argued rather than observed. The same gap exists for the apiApp and the MCP
  server.
- **Two eviction tests were rewritten rather than kept**, because the F01 cascade makes their
  arrangement unreachable. The reasoning written around that was wrong, and section 16 records the
  measurement: what is unreachable is a surviving LIVE child, because admission refuses a terminal
  parent. An evicted parent with a surviving TERMINAL child is routine, since the cascade stamps an
  orchestrator's ending before the workers it stops, so the orchestrator is the older finished
  record and the trim takes it first. The second arm of `Evict`'s parent clearing is therefore a
  live path, not a guard, and both it and this entry said otherwise.

### One process failure worth the same space as a finding

A mutation harness whose `restore` puts back every file it ever backed up will, run again later,
put a stale backup over newer work. It did: one restore reverted two files to their state five
commits earlier, wiping the typed-refusal contract and a byte-bound check out of the working tree.
The build error named a missing method, which was luck; a comment-only revert would have committed
silently. Each harness now restores only the named mutant's file, and the rule that caught it is
worth keeping: after a mutation round, diff the tree against HEAD and account for every changed
file, then run the FULL suite rather than a filter, because that is what proves no other cluster's
work went with it.

## 16. The review of the fixes, and what it found (2026-09-19)

The council's rule is that a branch merges when its findings are fixed AND the fixes are
re-reviewed, so section 15 closed half of it. This is the other half: six reviewers over the eleven
fix commits, one per lens, each told to refute its own suspicions and to say what it did to try.

They returned **15 majors and 26 minors**, every one with a measurement behind it rather than a
reading. That is a poor result for the fixes and a good one for the rule: three of the majors are
the same defect the fixes were written to remove, surviving on a path the fix did not cover.

### What the lenses were, and what each one cost the branch

- **Swarm lifecycle.** The only lens that confirmed its subject. It drove 2000 rounds of a
  grandchild spawn racing its grandparent's ending (the repo's own race test reaches depth 1 only)
  and found no survivor, 2000 rounds of two threads ending one agent with exactly one event each
  time, and a 50-worker cascade with every worker terminal. Four minors, all around the edges: the
  failure text, the missing exception boundary, and two comments stating invariants the code does
  not have.
- **The typed refusal.** Two majors. An MCP tool reports failure IN its result, so every graph call
  an agent makes was still recorded, published and counted as work, which is the exact defect F03
  fixed one path over. And the citable set took any call with a tool name, so a run whose every
  spawn was refused could cite those names and score fully grounded.
- **Bounds, caps and allowlists.** Two majors. The feed reported a keep-alive the stream floors, and
  a role key naming no role was ignored, so a misspelled role name left the role it was meant to
  narrow holding every advertised tool.
- **The tests.** Two majors. The body-bound pin named two proxy routes where there were three, and
  the third shipped unbounded; the appendix byte bound had no test on either path.
- **Text and claims.** Six majors. The claim this branch corrected in a dozen places survived in ten
  more, the unconditional 403 in three more, and four sentences were simply false.
- **Telemetry and packaging.** Three majors. A reboot could leave the host toolless for the life of
  the process while reporting healthy; a hosted-chat overlay pulled a model nothing would ask for;
  and the MCP tier checks were case-sensitive against values the server binds case insensitively.

### What that means about the first round

Two patterns, both worth naming rather than filing.

**A fix applied at one site is not a fix.** The refusal work, the 403 wording and the trust-boundary
sentence were each corrected where the gate pointed and left standing where it did not. The gate's
own findings are samples, not inventories, and section 15 read them as inventories: its sentence
"every site that derived a conclusion from the false version points at them" was false when written,
by ten sites.

**A number in prose drifts faster than anyone believes.** The mutant count in section 15 was wrong
twice, and a doc count was wrong through two features. Both are now held by something: the per-
cluster numbers add to the total, and a convention test pins the capability count against the enum.

### What is fixed, and what is recorded instead

Every major and every minor is closed, in ten commits, except these, which are recorded because
they are judgements rather than omissions:

- **The cascade's own exception boundary is a backstop no test reaches**, and its mutant survives on
  purpose. Every throw site inside the publication is handled where it happens, so the outer catch
  is unreachable today; it is kept because the failure it prevents is permanent and the cost is one
  try block. The comment says exactly this, which is the difference between a guard and a claim.
- **The compare-exchange that keeps four concurrent agents to one reconnect attempt is argued, not
  pinned.** The test drives it sequentially, because a deterministic two-thread arrangement needs a
  handshake seam this host does not have.
- **`AddAgentsObservability` is still exercised by no test**, unchanged from section 15.
- **The unconditional-403 wording is now corrected everywhere this branch touches**, which is not
  the same as everywhere: the repository-wide debt recorded in section 14 is smaller but not gone.

### Two process failures, both of which nearly landed

**A per-file mutation backup goes stale.** Section 15 recorded a blanket `restore` putting old files
over newer work, and the fix was to restore only the named mutant's file. That was not enough: the
backup was still created once per FILE, on the first apply, so editing that file between rounds left
a stale copy that the next restore put back. It silently removed a cancellation boundary and turned
a surviving mutant into a killed one, which is the worst possible direction for that error. Backups
are keyed per mutant and refreshed at every apply now, and the rule that caught it is the same one
as last time: diff the tree against HEAD after a round and account for every changed file.

**Editing a drafted patch broke two sentences.** Four of the text corrections were drafted by
read-only reviewers and verified by independent ones, which caught two false claims before they
landed. Then I edited the drafts myself, and two of my edits replaced text that spanned a line
break, dropping the words "is the only" from one operator-facing message and "the compose" from
another. Neither the verifier (it ran before my edit) nor the JavaScript parser (both were still
valid strings) could see it. What found it was reading the diff of every line I had changed, which
is now the last step before a commit rather than an optional one.
