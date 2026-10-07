# WAL torn tail - Specification

> **Status:** Implemented and merged to `main` on 2026-10-07; the findings of both passes of the
> merge gate are fixed or answered (section 6). Found by the embedded-MCP evaluation of 2026-10-07
> ([features/open/embedded-mcp/](../../open/embedded-mcp/spec.md)), reproduced independently with
> the engine's public API, and fixed test-first. Present since the write-ahead log shipped;
> v0.0.41 carries it.

## 1. The defect

A crash in the middle of a write-ahead-log append leaves an incomplete final entry. Recovery
handled the read side correctly: `WriteAheadLog.ReadEntries` stops at the last complete entry and
the replay applies everything before it. Nothing handled the bytes it stopped at. The log was
adopted as it was, and `FlushGroup` opens the file in append mode, so the next commit landed
behind the torn bytes, was fsynced and reported `Durable`. At the following restart the reader
stopped at the same torn bytes, and every commit behind them was gone, with no error, no warning
and `lastRecoveryTruncated` false.

Measured before the fix, engine only:

```
torn tail:  5 vertices committed, 3 left after the next unclean restart
control:    5 vertices committed, 5 left
```

**Exposure.** By default the server checkpoints only on a clean shutdown or an explicit save
(`Fallen8:Durability:SaveOnShutdown`, no timer), and a save rewrites the log. The loss therefore
needs an unclean stop that tears an entry, further commits, and a second unclean stop before any
save. A crash loop is the realistic case, which is also when durability matters most. With
`SaveOnShutdown=false`, whose own description promises that committed work survives, the second
stop does not even have to be unclean.

**Siblings on the same path,** found while pinning it:

- An unreadable entry in the MIDDLE of the log (a CRC failure or a negative length, with more
  entries after it) stopped the replay silently: acknowledged entries after it were not replayed,
  `lastRecoveryTruncated` stayed false, and new commits were appended behind it and lost the same
  way.
- A replay that fail-stops at an entry it cannot apply did set `lastRecoveryTruncated`, but new
  commits were still appended behind the unapplied entries, acknowledged as durable, and lost at
  the next restart.
- `DurabilitySignalTest.ARecoveryThatStopsEarly_IsReportedAsTruncated` used a torn tail and never
  asserted the flag, which a torn tail does not set. No test anywhere asserted a truncated recovery.

The merge gate found four more on the same path (section 6): a corrupt length inside the log was
read as a tear and the cut destroyed the entries behind it, an unreadable header reset the whole
file and reported a clean recovery, a zero-filled tail was read as damage, and a replay that threw
skipped the seal.

## 2. Decision

The reader records where and why it stopped (`WriteAheadLog.Scan`), and
`WriteAheadLog.SealAfterReplay` runs after every replay, construction-time and paired-load alike,
including one that throws, before anything can be appended. Its doc comment is the code home of
this rule.

The scan stops at the first bytes that are not an entry: fewer bytes than a frame, a length that
is not a positive count fitting in the file, or a CRC that does not match. What decides the
action is whether a complete entry follows anywhere after them.

| The replay ended | Meaning | Action | `lastRecoveryTruncated` |
|---|---|---|---|
| at the end of the file | every byte after the header is a complete entry | none | false |
| at bytes that are not an entry, with no complete entry after them | what a write cut off by a crash leaves: part of an entry, a full-length entry with a bad CRC (the sectors of one write can reach the disk out of order), or zeros (the new size can persist before the data) | cut the file back to the last complete entry and fsync | false, it was never acknowledged |
| at bytes that are not an entry, with a complete entry after them | damage inside the log | keep the bytes, trip the fence | true |
| at an entry it could not apply, or with a fault of its own | | keep the bytes, trip the fence | true |
| at a cut that failed | | trip the fence | false |

The header gets the same treatment at construction. If it cannot be read and the file is long
enough to hold the shortest header and the shortest entry (37 bytes), it may hold acknowledged
entries: it is kept as it is, nothing is replayed, the fence trips and the recovery is reported as
truncated. A shorter file holds no entry, and an interrupted write of a fresh log's header leaves
one, so it is reset. A Load that re-anchors a log with an unreadable header says that it discards
it; in the apiApp a namespace with a registered save game loads it at boot, so there the bytes are
kept until that Load. The code home of the header rule is `WriteAheadLog.HeaderUnreadable`.

"A complete entry after them" is a search for a frame with a positive length that fits, a payload
that begins with its own length (the serializer's header, `WalTransactionCodec.HasEntryShape`)
and a matching CRC-32. It is bounded: past 64 MiB after the stop, or 1 GiB of hashing, it answers
that entries may follow, because keeping bytes destroys nothing and cutting could.

The fence is the existing crash-durability-hardening D1 mechanism: commits report non-durable,
`degraded` is true, and a successful save rewrites the log and lifts it. Cutting damage would
destroy acknowledged entries, and appending behind it would repeat the defect, so the bytes stay
and the state says so.

**Known false alarms.** Each errs toward reporting history as lost, and destroys nothing:

- A crash inside one commit group of several entries can, by the same out-of-order-sectors
  argument, leave an unreadable entry with a complete entry of the same, never acknowledged, group
  after it.
- A torn entry whose payload carries the bytes of another frame (a byte-array property holding log
  bytes) shows that frame to the search.
- The search bounds keep a long tear.
- A damaged header in a log that holds no entries, such as an anchored one right after a save, is
  kept, because its length cannot tell it from one that does. Deciding that by searching for an
  entry would instead reset a log whose header and entries are all damaged and report nothing lost.

In each, the log is fenced until the next save and the recovery is reported as truncated until the
next one. So `lastRecoveryTruncated` means the graph MAY be a prefix of the committed history; it
errs toward true, and a client that defers deletions on it loses nothing by that.

## 3. What it does not do

- **No resynchronisation past damage.** The entries behind it were written against an id space
  that included what the damage hides, so replaying them could misapply them. A log an earlier
  version wrote, with commits appended behind a torn entry, is therefore kept and fenced and
  reported as truncated: those commits were acknowledged, and dropping them is the next save's
  decision, not the reader's.
- **No side file** for the bytes a fenced log keeps; the next save rewrites the log, as before.
- **No directory fsync** after a cut, the same deferral the engine's other file I/O documents
  (`DurableFileIo`).
- **No cross-process lock.** Two processes on one data directory remain unsupported; the
  embedded-MCP evaluation measured what that does, and the local native host owns the answer.

## 4. Tests

`WriteAheadLogTest`, region "commits after a recovery". A test that commits after a recovery
restarts once more, because such a commit is lost only at the restart after the recovery; the
others assert what the recovery reports and what it leaves on disk. Entry boundaries come from the
file length after each commit. A test that damages one field assumes what it needs and says so:
the little-endian 4-byte length at the start of a frame, the header's layout for the header tests,
and the shape of an entry for the two search-cost tests.

| Test | Pins |
|---|---|
| `TornTail_CommitsAfterRecovery_SurviveTheNextRestart` | the defect itself, the exact cut offset, both flags false, the logged size of the cut |
| `TornTail_FragmentShorterThanAnEntryHeader_IsCutBeforeTheNextCommit` | a tear inside a length prefix |
| `TornTail_LastEntryOfFullLengthWithABadCrc_IsCutAsAnInterruptedWrite` | the out-of-order-sectors tear |
| `TornTail_OfZeros_IsCutAsAnInterruptedWrite` | the size-before-data tear |
| `TornTail_OfBytesThatOnlyLookLikeLengths_IsCutAsAnInterruptedWrite` | the entry-shape check: length-like bytes are not hashed, so the hashing bound does not keep a tear |
| `TornTail_AfterAPairedLoad_CommitsSurviveTheNextRestart` | the snapshot-paired recovery path |
| `UnreadableEntryInsideTheLog_IsLeftInPlace_FencesTheLog_AndReportsTruncatedHistory` | bytes untouched, fence, truncated, a save lifts the fence |
| `UnreadableEntryInsideTheLog_AtAPairedLoad_...` | the same through the paired Load |
| `CorruptLengthInsideTheLog_...` (two cases) | a length claiming more than remains, and a negative one, each with an entry behind it: kept, not cut |
| `EntryCutShort_WithACompleteEntryBehindIt_...` | the shape an earlier version left, and the group false alarm |
| `TornTail_TooLongToSearch_..._AndReportsTruncatedHistory` | the 64 MiB bound |
| `TornTail_TooCostlyToSearch_..._AndReportsTruncatedHistory` | the 1 GiB hashing bound |
| `ReplayThatStopsAtAnEntryItCannotApply_FencesTheLog` | a fail-stop on a CRC-valid entry, and the offset the error names |
| `ReplayThatThrows_FencesTheLog_UntilASave` | the throw path, and that a save captures what recovers |
| `TornTail_WhenTheCutFails_TheLogIsFencedInsteadOfAppendedBehindTheTear` | the cut's failure path, and that nothing is appended |
| `UnreadableHeader_KeepsTheFile_ReportsLostHistory_AndFencesTheLogUntilASave` | the header fence |
| `UnreadableHeader_ALoadThatReAnchorsTheLog_SaysItDiscardsWhatTheLogHeld` | the re-anchoring warning |
| `UnreadableHeader_TooShortToHoldAnEntry_IsReset` (four cases) | the reset, up to the longest file that cannot hold an entry, to the byte of a fresh header |
| `UnreadableHeader_JustLongEnoughToHoldAnEntry_IsKeptAndFenced` | the reset rule's other edge |

`DurabilitySignalTest.ATornTail_IsDiscarded_WithoutReportingTruncatedHistory` replaces the
misnamed test and asserts what its old name only claimed. The test-side file damage lives once,
in `WalFile`.

## 5. Impact on existing features

- **Engine package:** behaviour only, no public signature change; the `DurabilityState` field
  docs gain the new causes and say "may be a prefix".
- **REST and the OpenAPI snapshot:** the `lastRecoveryTruncated` description changes; the
  snapshot was regenerated and that is its only change.
- **Integrations runtime:** it defers deletions when the target is degraded or truncated. That now
  also happens after damage inside the log or an unreadable header, which is the conservative and
  correct outcome, and no longer after a zero-filled tail. Its deferral reason said the graph "is a
  prefix of committed history"; at the user's choice it now says "may be" (`GraphWrites.cs`).
- **F8 Studio:** the durability banner shows the same two fields. Its text said the graph "is a
  prefix of the committed history" after the replay "stopped at the last good entry"; at the user's
  choice it now says "may be a prefix" and "stopped before the end of the log", which holds for every
  cause, an unreadable header included (`DurabilityNotice.tsx`, whose test now asserts the wording).
  No screenshot shows the banner.
- **apiApp shutdown save:** its failure messages said every committed transaction is in the log;
  they now say the commits acknowledged as durable are, because a fenced log's later commits are not.
- **MCP:** `f8_overview` does not report durability; no change.
- **Docs:** [save-games](../../../docs/src/content/docs/save-games.mdx), the WAL row and the
  `degraded` and `lastRecoveryTruncated` rows; [studio](../../../docs/src/content/docs/studio.md),
  the durability banner's sentence.
- **Browser host:** the probe opens no WAL; it is run as the trim gate for the engine change.
- **NL-assist, architecture diagrams, persisted recipes:** none.

## 6. Merge gate

Three reviewers (correctness, regressions, honesty), each finding re-verified against the code,
returned 23 findings. What changed:

| Finding | Change |
|---|---|
| C1, major: a corrupt length inside the log read as a tear, and the cut destroyed the entries behind it while reporting a complete recovery | the keep-or-cut decision is the search for a following entry, for every kind of unreadable bytes |
| C3, major: an unreadable header reset the file and reported a clean recovery | kept and fenced unless too short to hold an entry |
| R1 and C4: zeros read as valid empty entries, then as damage | a length must be positive; zeros are a tear |
| C2 and R2: a replay that threw skipped the seal | sealed on that path too |
| H1: a commit group torn out of order reads as damage | documented as a false alarm; "is a prefix" became "may be a prefix" |
| C5: the cut-failure test did not check the file or its own precondition | both asserted, inconclusive where the file system ignores read-only |
| R6, its first half: the negative-length test's name left out that it asserts truncation | renamed, and since merged with the oversized-length test (second pass, SH6) |
| C6 and R3: file-damage code repeated in four test classes | one `WalFile` |
| R6, its second half: no test of the fence through the paired Load | added |
| H2, H4, H6, H7, H8, H9 and R5: claims wider than the evidence, duplicated rationale | corrected here and in the code comments |
| H5: the first commit said the REST descriptions of both fields changed; only one did | recorded here |
| C7, R4 and H3: the plan and the spec disagreed on whether the probe ran | reconciled with the gate results in the plan |
| R7: DEGRADED in capitals | lowercase in the messages this feature adds; the older ones are left as they are |

### Second pass

Two reviewers on the fixes, one for correctness and regressions (SC) and one for honesty and tests
(SH), each finding checked against the code:

| Finding | Change |
|---|---|
| SC1, minor: a non-pairing Load read "no entries" at a damaged first entry and discarded the complete entries behind it without its warning | `HasEntries` counts an entry behind damage, as a recovery does; tested |
| SC2, minor: a damaged header in an empty anchored log is fenced and reported truncated | kept, for the reason in the false-alarm list of section 2 |
| SC3, nit: a replay that cannot open the log reports stopping at offset 0 | no change: that is true, and the writer logs the exception beside it |
| SC4, nit: in the apiApp the boot Load re-anchors a header-unreadable log | said in section 2 |
| SC5, nit: no test pins the stop offset at an I/O fault between entries | said below; the plan's mutation row names what the fail-stop test pins |
| SC6, nit: the shutdown save's failure messages promised every committed transaction is in the log | they say the commits acknowledged as durable are (section 5) |
| SC7, nit: a torn entry carrying another frame's bytes is another false alarm | listed in section 2 |
| SC8, nit: "each of the 100 positions" should be 101 | no change: the candidates are p = 0 to 99 |
| SH1, major: Studio, the integrations runtime and the Studio docs said "is a prefix" | reworded at the user's choice (section 5) |
| SH2: the field docs said a crash's leftovers never set the flag | past the search bounds they do, and the docs say so |
| SH3: the plan's "no cut" row counted five tests | the plan holds a mutation run of the final code |
| SH4: section 4 claimed more than the tests assert | rewritten |
| SH5: the header rule's rationale was told in five places | its home is `HeaderUnreadable`; the others point there |
| SH6: the frame check was written twice, and two tests were one test | `DeclaredPayloadLength` and `CrcMatches` serve both; one test with two cases |
| SH7: two test names left out that they assert truncation | renamed |
| SH8: the reset test passed without a reset in two cases, and its boundary was pinned from one side | it compares the file with a fresh header byte for byte; the boundary is the length that can hold a header and an entry, pinned on both sides |
| SH9: `WalFile.FlipByteAt` promised a CRC failure | corrected |
| SH10: `ReadEntries` called a header that was never readable a clean scan | `Unreadable`; unreachable today, and the one surviving mutant |
| SH11: counts and wording in the round-two commit message, the spec and the plan | corrected here; "one question decides every stop" in that commit message overstates (an apply failure and a fault stop the replay too), and the message stays as committed |

Found while fixing, and fixed: the first search looked for the entry type in the payload's first
byte, where the serializer's header sits; the new damage tests failed on it before any commit.
The payload's shape is the codec's knowledge and lives there. The stop offset in the fail-stop
error named the entry before the fault when an I/O error hit between entries; it is now set
before every read. That case is reasoned, not tested: an I/O fault between two entries cannot be
injected without a seam the engine does not have.

Found while fixing, and not part of this feature:

- A Load whose replay throws reports `RolledBack`, but the graph it leaves is the snapshot it
  loaded, not the graph from before it, and `VertexCount` is not recalculated. Measured: Alice and
  Bob committed, the Load fails, the graph holds Alice and the count says 2. With the fence a
  retried Load replays Bob again, because nothing was appended behind him.
- `Fallen8.Dispose` is not idempotent: a second call throws a `NullReferenceException` from
  `TabulaRasa_internal`.
