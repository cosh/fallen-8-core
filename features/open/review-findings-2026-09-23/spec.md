# Review findings 2026-09-23 - Specification

> **Status:** IMPLEMENTED on `feature/review-findings-2026-09-23`, awaiting the review gate; not
> merged. Eight commits, one per phase plus the bookkeeping, each fix mutation-checked. What the
> implementation changed about this document is in section 7a rather than edited into the sections
> above, and what the gate itself taught is in 7b; **section 7a corrects four claims made below**,
> so read it before trusting a row in section 5.
>
> Two things are deliberately NOT done and are recorded rather than dropped: `RTree`'s 15 unguarded
> lock sections, the same defect as W8 and a larger instance, which are named in a convention-test
> exemption; and the default agent model, which stays as it is because that is a platform choice
> with its own revisit trigger (section 3).
>
> Source: the validation pass of 2026-09-23 over the claimed-outstanding list (agent overclaim,
> unreviewed September code, small items, declared gaps, another session's work). Every defect
> below was found by READING the tree at 89b68b99 and none was executed, which is why each carries
> a reachability verdict rather than a severity taken on faith, and why three of those verdicts
> needed correcting once something was run. Branch-only workflow, no issue or PR unless asked.

## 1. Summary

Three code defects, one documentation overclaim, five stale records or comments, and four small
items that were declared as known gaps. The code defects are small and local; the largest single
risk in this list is still the one no code fixes, the shipped agent model, and section 3 says
what this feature does and does not do about it.

What this is NOT: a re-review of the 2400-line ARXML reader, a change of the default agent model,
or the platform-integrity audit's remaining work beyond its one mechanical item (W8).

## 2. The code defects

### 2.1 A gateway that hangs is still reported as an agent somebody cancelled

**Chain.** `Fallen8ChatClient.GetResponseAsync` links the caller's token with the target deadline
into a `budget` and hands `budget.Token` to `RestSeam.SendAsync`
([Fallen8ChatClient.cs:115](../../../fallen-8-agents/Model/Fallen8ChatClient.cs#L115)). The seam
names a timeout only `when (!cancellationToken.IsCancellationRequested)`
([RestSeam.cs:129](../../../fallen-8-rest-client/RestSeam.cs#L129)), the shape `HttpClient.Timeout`
produces. The host sets that timeout to infinite
([AgentsHost.cs:116](../../../fallen-8-agents/Hosting/AgentsHost.cs#L116)). So when the budget
fires before headers arrive, the token IS requested, the filter is false, a raw
`TaskCanceledException` leaves the client (its only cancellation catch is the success-body path at
line 170) and the runner's generic catch records `Cancelled`, "Cancelled while the host was
stopping" ([AgentRunner.cs:367](../../../fallen-8-agents/Runtime/AgentRunner.cs#L367)).

**Skeptical verdict: real, narrow by default.** The host's deadline defaults to 630 seconds,
deliberately above the gateway's own 600, so in a default deployment the gateway answers (with its
own timeout naming what to change) before the host's budget fires. The misreport needs the apiApp
to be unresponsive at the HTTP layer for the whole 630 seconds, a reverse proxy stalling between
the two, or an operator lowering `Fallen8Target:TimeoutSeconds` below `Fallen8:Chat:TimeoutSeconds`.
It is a misreport, not data loss: the run ends either way, with the wrong state and a false
sentence. It is nevertheless the exact defect the agent-host findings record as fixed and
"verified live" (section 4), and the live check covered the UNREACHABLE arm only, which does work.

**Decision: align with the seam instead of duplicating its classification.** The other two
sidecars set `HttpClient.Timeout = target.Deadline` and let the seam classify
([McpHost.cs:95](../../../fallen-8-mcp/Hosting/McpHost.cs#L95),
[GraphTargetFactory.cs:87](../../../fallen-8-integrations/Run/GraphTargetFactory.cs#L87)). The agent
host is the one that deviated. The fix: set `http.Timeout = target.Deadline` in `AgentsHost`,
delete the `budget` source in the client, and delete the now-dead cancellation catch in `Read`
(the default completion option buffers the whole body inside `SendAsync`, so a body read cannot
time out afterwards). The `AgentsHost` comment's reason for infinite, "two competing deadlines",
does not apply once the budget is gone: there is one deadline again, and it is the seam's.

Rejected: a local `catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)`
around the send. It works, but it re-creates the hand-rolled copy of the seam's classification
that finding 4 says was removed.

**Tests.** Adapter level: a handler that never answers, client timeout of a few milliseconds, the
call throws `Fallen8ChatException` whose message names `Fallen8Target:TimeoutSeconds`. Runner
level: the real adapter over the same handler, the run ends `failed`, not `cancelled`, and its
failure text is the adapter's sentence. The second test is the one that could not exist before,
because the existing seam test fakes HttpClient's exception with the token unset, a shape this host
never produced.

### 2.2 Ollama tool-call ids collide across turns, so a result names the wrong tool

**Chain.** Ollama's native protocol carries no tool-call id, so the backend synthesises one per
REPLY as `call_` plus the call's ordinal
([OllamaChatBackend.cs:354](../../../fallen-8-core-apiApp/Chat/OllamaChatBackend.cs#L354)); every
reply's first call is `call_0`. When a tool result comes back, its `tool_name` is resolved by FIRST
match over the whole conversation
([OllamaChatBackend.cs:271](../../../fallen-8-core-apiApp/Chat/OllamaChatBackend.cs#L271)). Round
one calls `count_vertices` as `call_0`; round two calls `count_edges` as `call_0`; round two's
result is sent as `tool_name: count_vertices`. The agent host forwards ids verbatim, so nothing
upstream disambiguates.

**Skeptical verdict: real, masked today.** It needs a model that calls tools across two rounds on
the Ollama transport (local Ollama or Nahil). The shipped default never reaches round one with
instructions present (agent-host findings, section 1), so no run has hit this. The moment an
operator follows the documented remedy and names a tool-capable model, every result after the
first round is misattributed. How much a given model's template cares about `tool_name` varies;
the wire is wrong regardless. The fixture supplies `"id":"call_9"`, so the synthesis path has
never executed in a test.

**Decision.** Two changes, one of which is the fix and the other cosmetic.
- Resolve a result's name against the NEAREST preceding assistant turn that carries tool calls,
  falling back to the first match. The protocol puts a result after its call, so this is correct
  for any id scheme, including a client that supplies its own ids.
- Make the synthesised id unique per assistant turn anyway (`call_{conversationLength}_{ordinal}`
  or equivalent), so a trace no longer shows `call_0` on every step. This is readability, not
  correctness, once the first change is in.

**Tests.** One request whose conversation holds two rounds with colliding ids and a result for the
second; the captured body's tool turn carries the second call's name. A reply without ids from
the stub produces distinct ids across two replies.

### 2.3 A malformed OpenAI argument string faults the whole response

**Chain.** `Parsed` returns `default(JsonElement)` when the arguments string is not JSON
([OpenAIChatBackend.cs:400](../../../fallen-8-core-apiApp/Chat/OpenAIChatBackend.cs#L400)). The
controller copies it into the non-nullable `ChatToolCallREST.Arguments`
([ChatController.cs:369](../../../fallen-8-core-apiApp/Controllers/ChatController.cs#L369)).
Serialising an undefined `JsonElement` throws `InvalidOperationException`, so the response fails
during serialisation. The doc comment above `Parsed` promises "the caller sees a call with no
arguments and can refuse it".

**Skeptical verdict: real, rare.** It needs the provider to emit invalid JSON in `arguments`,
which the SDK does not validate. The `arguments == null` branch has the same shape. Low frequency,
but the branch exists precisely to handle it and does the opposite of what it says.

**Decision.** `Parsed` returns an empty object element in both branches; one home, the comment
becomes true. The controller stays as it is.

**Test.** A stub reply with `"arguments":"{not json"` produces a 200 whose tool call carries `{}`.
The assertion must go through the controller's serialisation, because that is where the throw
lives; a test that inspects the backend result alone cannot fail the way this defect does.

## 3. The overclaim: agents are sold as working

**What is true.** The host, adapter, registry, budgets, traces and feed work. The shipped default
model (`phi4-mini:latest` on Ollama and Nahil) cannot run a tool-using agent once any instruction
text is present, and the host always sends the role prompt as a system message, so the shipped
deployment is in the failing arm of the measurement. The findings say so in section 1. Nothing a
user reads FIRST says so: not the README entry
([README.md:113](../../../README.md#L113)), not the docs page intro
([agents.md:1](../../../docs/src/content/docs/agents.md#L1)), whose caveat is a note at line 159 of
270. Two compose comments say the opposite:
[docker-compose.yml:92](../../../docker-compose.yml#L92) calls it "a stock tool-calling model" and
[docker-compose.nahil.yml:49](../../../docker-compose.nahil.yml#L49) says it "was measured calling
tools there" (it was, with a bare user turn only).

**What this feature does.** One sentence in the README entry, one sentence in the docs intro
pointing at the note (the note stays the one home for the measurement), and the two compose
comments corrected to what was measured. `.env.example` already tells the truth and is left alone.

**What this feature does NOT do, and the decision it leaves open.** It does not change the default
model. The options are:

| Option | What it means | Verdict here |
|---|---|---|
| a. Keep the default, say so up front | agents run, fabricate on the default model, and the docs say so before the user enables them | recommended now; it is the honest version of what ships |
| b. Switch the local default to a small tool-capable model | needs a fresh measurement on the sidecar (CPU inference here is unusable) and a pull-list change; does nothing for Nahil, whose catalog had no alternative | not in this feature; revisit when a model resolves |
| c. Ship no default and refuse with a 503 naming `Models:Agent` | turns an opt-in capability into an error for every local operator until they choose | considered, rejected: the capability is already off by default, and a refusal helps nobody who has no better model to name |

The model remains a platform ask. The revisit trigger in findings section 1 stands.

## 4. Stale records and comments

Each is one line to fix and each is false at HEAD.

- `features/done/agent-host/findings.md` section 4 says fixed and verified live. Correct: the
  unreachable arm was verified; the hung arm was still broken and is fixed by this feature.
- `features/done/arxml-vehicle-model/spec.md:3` says "reviewed and merged" while
  `findings.md:188` says "What remains is review". Record what actually happened: a static
  spot-check of four seams (XML hardening, reference resolution, duplicate paths, accumulating
  maps) on 2026-09-23 found them sound and found the two small items in section 5; the Ethernet and
  SOME/IP name tables, read from the standard with no export to check against, remain unreviewed
  by anyone and cannot be until an export exists. The seven commits of 2026-09-02 reached main by
  fast-forward pull with no merge commit.
- `features/open/platform-integrity-audit/spec.md:3-8` still lists W7 as pending. W7 landed:
  `IntegrationsClient : SidecarHttpClient` and `AgentsClient` are the facade it asked for. Still
  pending, verified against the tree: W8 (12 unguarded lock sections), W9 (trim still renumbers
  ids and says "releasing unused memory"), W10 half (compose profile exists, `/status` has no
  integrations field), W11 (structurally open, nothing hides today: zero ignore attributes against
  447 documented operations), W12, W13, and W15 in P2.
- Comments that describe code that no longer exists: `OpenAIChatBackend.cs:307-311` and
  `AnthropicChatBackend.cs:491-495` say a tool result's id is one "this seam does not carry"
  (false since 7d0e0805); `OllamaChatBackend.cs:233-234` says the name comes from "the turns
  before this one" while the whole list is searched; `IntegrationsOptions.cs:74-76` says a file
  "is decoded to TEXT, two bytes per character" (false since 76359480, the reader takes the
  stream); `ArxmlReader.cs:602` says an unread cluster costs "one short-name read" while the code
  materialises its whole subtree (section 5).

## 5. Smaller code items

| Item | Skeptical verdict | Decision |
|---|---|---|
| ArxmlReader materialises the whole subtree of LIN, TTCAN and J1939 clusters ([ArxmlReader.cs:419](../../../fallen-8-integrations/Providers/AutosarArxml/ArxmlReader.cs#L419)) only to record their name as unread | a memory ceiling, not a hole: a 128 MiB file that is one unread cluster is built as one XElement; bounded by `MaxFileBytes`, but the comment at line 602 promises one short-name read | `reader.Skip()` for the unread kinds after reading the name; the comment becomes true. Test: a fixture whose unread cluster contains a would-be-interesting element; it is not collected, the diagnostic still names the cluster |
| ArxmlReader comment at [774-777](../../../fallen-8-integrations/Providers/AutosarArxml/ArxmlReader.cs#L774) says a channel repeated across a cluster's VARIANTS is "the same element rather than a duplicate", but `Claim` emits a `DuplicatePath` "the file contradicts itself" for any same-file repeat | unknown which side is wrong; no fixture has two CONDITIONALs in one cluster | write that fixture first. If the diagnostic fires on a valid variant-rich file, exempt the channel claim (a repeat under the same cluster is the same element); if variants do not repeat channels that way, fix the comment |
| OpenAI replay of an assistant turn that both spoke and called tools drops the text ([OpenAIChatBackend.cs:322](../../../fallen-8-core-apiApp/Chat/OpenAIChatBackend.cs#L322)); Anthropic and Ollama keep it | low: the model loses its own prior sentence between tool rounds; only degrades multi-step reasoning slightly | verify the SDK's assistant message accepts text parts alongside tool calls; if so, keep the text, with a test on the captured body. If not, record why and leave it |
| W8: 12 of 13 guarded sections in [SingleValueIndex.cs](../../../fallen-8-core/Index/SingleValueIndex.cs) have no try/finally; a throw leaks the lock and every later writer spins on `Thread.Yield()` at full CPU | narrow: what throws inside `_idx[key] = x` is a key whose `GetHashCode`/`Equals` throws, or a corrupt save in `Load`. When it does, the symptom is a 100% CPU spin rather than an error, forever | in scope because it is mechanical and the audit's own text says it must not wait. Test: a key that throws from `GetHashCode` on first use; after the throw, a second writer completes within a bound instead of spinning |
| Four components define the `[instanceId, "namespaces"]` query with three different `enabled` predicates and identical poll and retry | react-query dedupes on the key, so the least restrictive predicate wins whenever its component is mounted; two of the three predicates are illusions. But `InstanceHealth` renders for instances that are NOT active, where no other observer is mounted, so its authorisation guard is load-bearing there | one `useNamespaces(instance, { enabled? })` next to `useStatus` in `src/state`, owning key, fetch, poll and retry; the per-site gate stays a parameter because it is a per-site fact. A convention test pins that the key literal appears only in the hook |
| `loadSbom`'s "unchanged, do not write" path has no test ([fallen8Deps.ts:176](../../../fallen-8-web-ui/scripts/samples/fallen8Deps.ts#L176)) | low frequency, high cost when it regresses: this is the branch the workflow's porcelain guard depends on, and its predecessor rewrote 11,600 lines a month | one vitest through the exported `buildFallen8Deps`: refetch env set, `fetch` stubbed to return the committed copy, `node:fs` mocked so `writeFileSync` is a spy asserted never called. No refactor |
| Copy gate threshold of four lets a three-member renamed copy pass ([CodeQualityTest.cs:739](../../../fallen-8-unittest/CodeQualityTest.cs#L739)) | measured and documented in the test itself: at three, two coincidental cross-assembly matches exist; the only tighter gate is an allowlist | no change |
| Registry observations from the spot-check: `ResultText` retained uncapped in the listing; retention knobs at 0 mean unbounded; `TryAdmit` has no disposed check | bounded by the model's own output cap; 0 is the documented "off" shape; the admission race exists only during shutdown | no change; record in the agent-host findings that the token-source lifecycle was examined on 2026-09-23 and found sound (record sources are never disposed by anyone, so the disposal race cannot occur) |
| `merge.ff` is unset | it would not have caught what happened: the September code reached main by `git pull` fast-forward and by three direct commits on main | not a code change. The remedy is branch protection on `origin/main`, a repository setting; recorded here, decided by the owner |

## 6. Non-goals

- Re-measuring the agent model or changing its default (section 3).
- A full independent review of the ARXML reader's September growth. The spot-check is recorded as
  what it was; the name tables stay unreviewable without an export.
- The audit's W9 to W13 and W15. They keep their home in the audit spec.
- Propagating a stop reason through `ChatBackendResult`. The host keys on tool-call presence and
  that is sufficient.
- Merging two consecutive `tool_result` user turns on the Anthropic wire. The API documents that
  consecutive same-role turns are combined.

## 7. Impact on existing features

Re-checked against the 33 files the branch actually touched, per the plan's phase 8. Where the
first pass was wrong the row says so.

| Layer | Impact |
|---|---|
| Engine | Wider than planned. `SingleValueIndex` (12 sections), `ServiceFactory` (1, a release in the wrong place), and `AThreadSafeElement`'s doc comment. No contract change: every edit is try/finally around an existing region plus the removal of locals that only existed to release before returning. The browser probe was RUN, not skipped, and passed 9 of 9 |
| REST contract and OpenAPI snapshot | unchanged, as predicted. No controller, route, response shape or XML doc was touched; the three chat backends sit behind `/chat` and the OpenAPI document test passes unchanged, so no regeneration |
| Studio | `useNamespaces` refactor across four call sites and five invalidation sites. Behaviour-preserving by construction (same key, same cadence, same retry, each site's own gate passed in), so no screenshot: nothing rendered differently |
| NL-assist dataset and eval | none, no retrain entry |
| MCP | none. The Ollama and OpenAI fixes are behind `/chat`, which MCP does not bridge, and no REST operation was added, so the coverage gate has nothing new to decide |
| Agent host | 2.1. One observable change: a gateway that stops answering now ends a run `failed` with a named timeout where it ended `cancelled`. Nothing asserted or documented the old text |
| Integrations runtime | 5 rows 1 and 2. One new diagnostic where there was silence (an empty or unnamed unread bus is now reported) and one false diagnostic removed. No provider descriptor changed, so the descriptor snapshot and `screen-integrations.png` are untouched |
| Docs site | `agents.md` gains a heading and an intro pointer; README entry. Build green, all internal links valid including the new anchor |
| Architecture diagrams | none. No channel, deployable or layer changed |
| Feature records | agent-host findings (4 corrected in place, new 17), arxml-vehicle-model (spec status line, new findings 10), platform-integrity-audit (status line re-derived: W7 was listed pending and had landed) |

## 7a. What the implementation changed about this spec

Recorded rather than edited into the sections above, because what was believed before the work
is part of the record. Every item here was established by running something.

- **Section 5 row 2 was wrong twice over.** It said no fixture puts two `CONDITIONAL`s in one
  cluster and that a fixture was needed to decide which side was wrong. The shipped
  `TwoChannelCluster` fixture has exactly that shape, and adding a zero-diagnostics assertion to
  the test that already used it turned it RED: the spurious `DuplicatePath` was being emitted on
  a valid extract and no assertion looked. The comment was right about AUTOSAR and the diagnostic
  was the wrong side, as the row's second option guessed.
- **The dedupe is keyed on the channel's containing list, not its name.** A name-only dedupe
  passes the restatement test and silently drops the report for the case that IS a
  contradiction, two channels of one name in a single `PHYSICAL-CHANNELS` list. Both cases now
  have a test, and the name-only version fails the second.
- **The unread-cluster skip does not need the cluster's short name.** The diagnostic is keyed by
  the ELEMENT name, so nothing inside the subtree is wanted. That made the fix smaller than
  planned, and it exposed a silence the plan had not predicted: an empty or unnamed unread
  cluster was reported as nothing at all, because the old path refused a nameless element before
  it reached the unread-bus branch. That is the half the new tests find red.
- **W8's claim family is more than twice its stated size, 28 sections against 12, and the gate
  found a site nobody had counted.** Measured by deriving acquisitions against releases across all
  seven `AThreadSafeElement` subclasses: `SingleValueIndex` 12 unguarded sections (13 release
  sites, as `AddOrUpdate` had two), `RTree` 15, `ServiceFactory` 1. The other four were already
  clean. `ServiceFactory`'s is not a missing `finally` at all: its `catch` released
  unconditionally while spanning MORE than the guarded region, so the release was right for the
  throws raised inside the lock and wrong for any raised outside it. Fixed with the rest.
  **This bullet first claimed that an unresolved plugin reached that catch and wedged the factory.
  It does not: the resolution is `Try*` all the way down. See section 9 item 2 for what the fix
  actually rests on, which is ownership rather than an incident.**
- **`RTree` is NOT fixed, and that is a decision to take rather than a thing to inherit.** Its 15
  unguarded sections are the same defect and the larger instance of it, several of them around an
  injected `IMetric` and caller geometry. W8 scoped itself to `SingleValueIndex` and the audit's
  four architects never assessed this file, so a mechanical sweep through a 2,000-line spatial
  index is a scope decision. It is named in the new gate's exemption list with this reason, so it
  is machine-visible rather than forgotten, and the exemption goes when it is fixed.
- **The W8 gate is on the RELEASE, not the acquisition.** "The guarded region opens with `try`"
  was the first shape written and it reported `IndexFactory`, whose release is correctly in a
  `finally` behind one local declaration that cannot throw. The invariant that matters is that no
  release can be jumped over. The gate is mutation-checked: against the pre-fix index it reports
  all 13 sites and spares the one that was already guarded.
- **The runner half of 2.1 is left to composition, with the reason recorded.** One test already
  pins that a throwing model call ends a run as failed carrying its reason, and the runtime
  harness builds its own chat client, so a composite test would have rebuilt the runner rather
  than driven it. The adapter test proves the exception is a named gateway failure and not a
  cancellation; the chain is those two.
- **The deadline moved rather than being duplicated.** 2.1 said the host would set
  `HttpClient.Timeout` and the client would keep its own timeout for the message it prints. That
  leaves the same number in two places that must agree. The client now takes no deadline at all
  and reads the number off the transport, so the sentence cannot drift from the bound in force.
- **2.3's residual is real and deliberate.** Fixing `Parsed` fixes the only backend that could
  produce an unset element, but `ChatToolCallREST.Arguments` still cannot express one, so any
  future backend could fault a response the same way. The guard stays at the producer, where the
  false promise was, rather than in both places.
- **Section 4's "no refactor" claim was wrong, and finding that out cost the committed SBOM.** The
  plan said the unchanged-path test could be written by mocking `node:fs`. Mocking it does NOT
  reach that module: the mock intercepted the test file's own binding, the pre-flight probe
  therefore passed, and the real write went through anyway. The result was the worst shape a test
  can have, green while destructive, and it replaced the committed 26,000-line document with a
  two-package fixture. It was restored from `HEAD` and verified byte-identical. `loadSbom` now
  takes an injected file seam, which is the shape the chat backends already use for a
  test-supplied transport, and the suite asserts the module read THROUGH that seam so a bypass is
  a failure rather than a silent write. A fourth test drives the DEFAULT seam against the real
  document, read-only, because the other three replace it and could not notice it rotting.
- **Section 5 row 5 was also half wrong.** Two of the four `enabled` predicates are load-bearing,
  not one: the app shell is the only observer of the key on the routes that hang off the root, and
  an instance health row renders for every registered instance including ones nothing else
  observes. Both keep their gate, passed as a parameter; the hook ANDs it with the instance being
  present so a caller cannot widen it into a null dereference. The poll-sharing convention test
  moved with the cadence rather than being deleted, and a new half pins that the key literal has
  exactly one home.
- **A divergence worth naming:** the Anthropic and OpenAI backends synthesise per-reply ordinals
  the same way the Ollama one did. Their protocols carry the id on the wire, so it only bites if
  a provider omits one, and neither has the nearest-preceding walk to absorb a collision. Left
  alone, said here rather than silently.

## 7b. Two things the gate itself taught

**A pre-existing, load-dependent failure in `IntegrationsResumeTest`.** The merge gate's full suite
failed once on `AResumedRunTheGraphRefused_KeepsItsEntryForTheNextStart`, at its last assertion:
the resumed run finished and did its work, and one spool file was left behind. Established before
blaming the branch, because that is the rule: the class passes 18 of 18 three times in isolation;
the same integrations code passed the full suite earlier on this branch; and the branch touches
nothing in the spool (only `ArxmlReader` and an `IntegrationsOptions` doc comment). The mechanism
is visible in the code rather than guessed: `RunSpool.Remove` deliberately catches `IOException`
and `UnauthorizedAccessException`, logs a warning naming the consequence, and does not throw, so a
transient Windows file lock leaves the entry and the test's absolute assertion sees it. The
assertion was the ONE bare one in a file where every sibling names what it found, which is why the
first occurrence said only "expected 0, actual 1". It now names the file. The strictness is
deliberately unchanged: weakening it would hide the real version of this, which is an entry the
runtime never tried to drop, and that would be suppressing the symptom instead of the cause.
Whether the swallow is the right production behaviour is a question for the integrations feature,
not this one, and it is recorded rather than answered.

**A gate that could not fail.** The first full-suite invocation here was
`dotnet test ... > file 2>&1; echo "TEST_EXIT=$?"` followed by a grep, and the surrounding task
reported exit 0 while the suite had one failure, because the exit code belonged to the grep. The
failure was found by reading the summary lines, not by the status. Any command whose purpose is to
gate has to surface the runner's own exit code, and this one was rewritten to do so.

## 8. Verification

- `dotnet build` and the full `dotnet test`, never with `-v q`.
- Web UI: `tsc`, vitest, exit code confirmed through `cmd /v:on` (the wrapper lies).
- Docs: `npm --prefix docs ci && npm --prefix docs run build`.
- Every new test mutation-checked: revert the fix, watch the test go red, restore, touch the file
  so MSBuild rebuilds it. Keep backups per mutant.
- A Python check for U+2013 and U+2014 over every changed text file, self-tested on a known-bad
  string, because the shell grep for these characters matches nothing.
- The confidential-name grep over the diff before any commit.

### What was actually run, 2026-09-24

| Gate | Result |
|---|---|
| `dotnet build` | 0 errors, 31 IL2026 warnings, the documented apiApp set |
| Full `dotnet test` | 2849 tests, 41 skipped (benchmarks), one pre-existing load-dependent failure diagnosed in 7b and then green on re-run |
| Browser probe, trimmed browser-wasm under node | 9 of 9 PASS, exit 0. Not optional: the workload is installed and this branch changes engine code, and two of its checks cross the sections phase 5 wrapped |
| Web UI `tsc -b` | exit 0 |
| Web UI vitest | 106 files, 1489 tests, exit 0 |
| Docs site build | green, and the link validator confirms the new `#the-model-you-give-it` anchor resolves |
| Dashes and arrows | 0 across all 1592 added lines of the branch, detector self-tested both ways first |
| Confidential names | 0 across the branch diff, paired with a control that matches |
| Mutation checks | 8, one per fix, each failing only its own test and for the stated reason |

The `cmd /v:on` note above did not survive contact: it misfires under this shell and printed a
banner instead of running anything. Both JavaScript gates were run through the Bash tool and their
exit codes read from `PIPESTATUS`, which is reliable here.

## 9. The review gate (2026-09-24), and what it found

Method: a code review over `main...HEAD` at high effort (ten angles, a dedup pass and a gap sweep)
returned eight findings. Every one was re-verified against the tree by hand before it was counted,
and the most consequential one by measurement. Four more came from reading the implementation
record against the code, which is where the last two gates on this repository found their worst
items too. **Nothing here is fixed yet.** That is the next step, and this section is its input.

### Confirmed, in order of what each would cost

1. **The host's deadline wiring is pinned by no test, and the record says otherwise (major,
   measured).** `AgentsHost.cs` sets `http.Timeout = target.Deadline`. Reverted to
   `InfiniteTimeSpan`, every agent test passes: 234 of 234, one skipped. The adapter test arms the
   timeout on a client it builds itself, so it pins the client's behaviour and not the host's
   wiring, and the defect 2.1 fixed returns with the suite green. Plan box 36 says this exact
   mutant was run and went red. It was not run: the mutant that was run re-armed a linked source
   inside the client, which is a different claim. The box is corrected in this commit. Fix: a
   composition test that calls `AgentsHost.AddFallen8Agents` with a configuration carrying
   `Fallen8Target:TimeoutSeconds`, resolves `IHttpClientFactory`, creates
   `AgentsHost.ChatClientName` and asserts its `Timeout` is the clamped deadline. That test is red
   on this mutant, and it is the test the plan claimed to have.
2. **The ServiceFactory story names a trigger that cannot fire (major, as a false claim).** The
   comment on its catch, section 7a above, the audit's status line and commit `3404c11c` all say
   an unresolved plugin released a lock never held and wedged the factory. `TryResolveServicePlugin`
   is Try* all the way down: `PluginRegistry.TryActivate` wraps `CreateInstance` in its own catch,
   `TryFindPlugin` returns false, `LogPluginNotFound` logs. An unresolved plugin takes the else
   branch and never reaches the catch. The only way into that catch WITHOUT the lock was the
   `CollisionException` thrown when `WriteResource()` returned false, which the audit itself
   measured as unreachable. The fix stands, because a release outside the guarded region is wrong
   by construction and the finally is where it belongs. The drama does not: there was no live
   wedge, and this feature's own rule is that a claim says which arm it verified. Fix: the comment
   states the structural fact; 7a and the audit line are corrected; the commit message cannot be
   edited and is noted here instead.
3. **The variant dedupe misses a duplicate inside a later variant (correctness, by trace).**
   `claimedChannels` remembers only the FIRST list a name was seen in, so two same-named channels
   in a LATER variant's list both read as restatements and the genuine contradiction is never
   reported. Fix: a set of parents per name; an occurrence is a restatement only when its parent is
   new to that set, and a repeat under a known parent goes to the claim. Fixture: variant A
   declares the channel once, variant B declares it twice.
4. **The lock gate cannot see a release that shares its line (gate correctness).** The regex
   requires the release to be the whole line, so `FinishWriteResource(); // released` or
   `FinishWriteResource(); return;` outside a finally is neither counted nor reported, and the floor
   of 50 still holds. Fix: match `Finish(Read|Write)Resource\(\)\s*;` anywhere on the line; the
   declarations in `AThreadSafeElement` carry no semicolon and stay excluded. Mutation-check with
   exactly that shape.
5. **The gate's exemption is a directory while its reason is a file.** `RTree.cs` is the only file
   under `Index/Spatial` with a release today. Exempt the file, so the gate widens by itself when
   another spatial index appears.
6. **Five narrations of one rationale.** The linked-source-cancels-the-caller's-token story is told
   in the chat client's constructor doc, its call-site comment, the host's wiring comment, two
   adapter-test comments, the smoke test and findings section 4. The seam's own doc on
   `RestSeam.SendAsync`'s token parameter already states the rule. Fix: the host, where the
   deadline is armed, keeps two sentences and points at the seam; every other site becomes one
   line; the history stays in findings section 4 and nowhere else.
7. **Two narrations of the derived-id rationale.** `OllamaChatBackend.ToolCallFrom` owns it and the
   test summary in `ChatToolMappingTest` repeats it. The test points; it does not retell.

### Valid and pre-existing, recommended rather than blocking

8. **The OpenAI and Anthropic backends still synthesise `call_{i}` per reply.** Recorded in 7a as
   a divergence. Reachable only if a provider omits ids, which neither does, but the change is two
   lines in each file and removes a recorded divergence, so it goes in while the files are open.

### Found by reading the record against the code

9. **Four pointers dangle the moment this directory moves to `features/done/`**: the agent-host
   findings and the arxml findings link `../../open/review-findings-2026-09-23/`, the audit spec
   links `../review-findings-2026-09-23/`, and `CodeQualityTest.cs` carries the path as a string.
   The plan says move at merge; the move has to carry these four edits.
10. **"Four times its stated size" is arithmetic that does not hold.** Twelve, fifteen and one make
    28 against 12, a factor of 2.3. In 7a and in the audit status line.
11. **Commit `76026160` says cross-file redeclaration is untouched.** Not quite: a channel restated
    by a variant in a LATER file used to increment `_redeclared` once per occurrence and now once
    per name, so the "elements already declared by an earlier file" count drops for such files. The
    new count is the right one, since a restated channel is one element, so this is a claim to
    correct rather than code. **Not pinned by a test, deliberately:** that number lives inside a
    formatted sentence, and every existing assertion on this diagnostic is on the count of
    diagnostics (one aggregate per re-declaring file) and its subject, neither of which moved.
    Asserting the number would mean matching prose, which this suite avoids everywhere else.
12. **Plan box 36 was a false tick**, per item 1, and is corrected in this commit.

### Cleared, and why

The review's own cleared list held on re-check. The twelve wraps and the ServiceFactory move are
correct against the counter arithmetic; the removed body-read catch is dead because the seam
buffers the whole response before returning; the startup probe keeps a ten second linked budget of
its own, nearer than the transport's, so the new deadline does not reach it; the Ollama walk and the
Studio hook preserve every prior key and gate. One concern of mine is not counted: the timeout
sentence would print zero seconds for an infinite `HttpClient.Timeout`, and the clamp makes that
value unreachable.
