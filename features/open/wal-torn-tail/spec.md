# WAL torn tail - Specification

> **Status:** Implemented on `feature/wal-torn-tail`; the merge gate's findings are fixed (section
> 6). Found by the embedded-MCP evaluation of 2026-10-07
> ([features/open/embedded-mcp/](../embedded-mcp/spec.md)), reproduced independently with the
> engine's public API, and fixed test-first. Present since the write-ahead log shipped; v0.0.41
> carries it.

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

The merge gate found three more on the same path (section 6): a corrupt length inside the log was
read as a tear and the cut destroyed the entries behind it, an unreadable header reset the whole
file and reported a clean recovery, and a zero-filled tail was read as damage.

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

The header gets the same treatment at construction. If it cannot be read and the file is longer
than the shortest header, the file may hold acknowledged entries: it is kept as it is, nothing is
replayed, the fence trips and the recovery is reported as truncated. A file no longer than the
shortest header holds no entry, which is what an interrupted write of a fresh log's header leaves,
and it is reset. A Load that re-anchors a log with an unreadable header says that it discards it.

"A complete entry after them" is a search for a frame with a positive length that fits, a payload
that begins with its own length (the serializer's header, `WalTransactionCodec.HasEntryShape`)
and a matching CRC-32. It is bounded: past 64 MiB after the stop, or 1 GiB of hashing, it answers
that entries may follow, because keeping bytes destroys nothing and cutting could.

The fence is the existing crash-durability-hardening D1 mechanism: commits report non-durable,
`degraded` is true, and a successful save rewrites the log and lifts it. Cutting damage would
destroy acknowledged entries, and appending behind it would repeat the defect, so the bytes stay
and the state says so.

**Known false alarm.** A crash inside one commit group of several entries can, by the same
out-of-order-sectors argument, leave an unreadable entry with a complete entry of the same group
after it. Nothing in that group was acknowledged, but it reads as damage: the log is fenced and the
recovery reported as truncated until the next save. The search bounds can do the same. So
`lastRecoveryTruncated` means the graph MAY be a prefix of the committed history; it errs toward
true, and a client that defers deletions on it loses nothing by that.

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

`WriteAheadLogTest`, region "commits after a recovery". The cut tests restart twice, because a
commit appended behind the stop is lost only at the restart after the recovery; the others assert
the durability a commit is acknowledged with and the bytes left on disk. Entry boundaries come
from the file length after each commit. A test that damages one field assumes what it needs and
says so: the little-endian 4-byte length at the start of a frame, the header's layout for the
header tests, and the shape of an entry for the two search-cost tests.

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
| `OversizedLengthInsideTheLog_...` | a length claiming more than remains, with an entry behind it: kept, not cut |
| `NegativeLengthInsideTheLog_...` | a negative length with an entry behind it |
| `EntryCutShort_WithACompleteEntryBehindIt_...` | the shape an earlier version left, and the group false alarm |
| `TornTail_TooLongToSearch_IsLeftInPlace_AndFencesTheLog` | the 64 MiB bound |
| `TornTail_TooCostlyToSearch_IsLeftInPlace_AndFencesTheLog` | the 1 GiB hashing bound |
| `ReplayThatStopsAtAnEntryItCannotApply_FencesTheLog` | a fail-stop on a CRC-valid entry, and the offset the error names |
| `ReplayThatThrows_FencesTheLog_UntilASave` | the throw path, and that a save captures what recovers |
| `TornTail_WhenTheCutFails_TheLogIsFencedInsteadOfAppendedBehindTheTear` | the cut's failure path, and that nothing is appended |
| `UnreadableHeader_KeepsTheFile_ReportsLostHistory_AndFencesTheLogUntilASave` | the header fence |
| `UnreadableHeader_ALoadThatReAnchorsTheLog_SaysItDiscardsWhatTheLogHeld` | the re-anchoring warning |
| `UnreadableHeader_TooShortToHoldAnEntry_IsReset` (three cases) | the reset, at its exact boundary |

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
  correct outcome, and no longer after a zero-filled tail. No code change.
- **F8 Studio:** the durability banner shows the same two fields; no change.
- **MCP:** `f8_overview` does not report durability; no change.
- **Docs:** [save-games](../../../docs/src/content/docs/save-games.mdx), the WAL row and the
  `degraded` and `lastRecoveryTruncated` rows.
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
| C5 and R6: the cut-failure test did not check the file or its own precondition, and a test name left out what it asserted | both asserted, inconclusive where the file system ignores read-only; renamed |
| C6 and R3: file-damage code repeated in four test classes | one `WalFile` |
| R6, its second half: no test of the fence through the paired Load | added |
| H2, H4, H6, H7, H8, H9 and R5: claims wider than the evidence, duplicated rationale | corrected here and in the code comments |
| H5: the first commit said the REST descriptions of both fields changed; only one did | recorded here |
| C7, R4 and H3: the plan and the spec disagreed on whether the probe ran | reconciled with the gate results in the plan |
| R7: DEGRADED in capitals | lowercase in the messages this feature adds; the older ones are left as they are |

Found while fixing, and fixed: the first search looked for the entry type in the payload's first
byte, where the serializer's header sits; the new damage tests failed on it before any commit.
The payload's shape is the codec's knowledge and lives there. The stop offset in the fail-stop
error named the entry before the fault when an I/O error hit between entries; it is now set
before every read.

Found while fixing, and not part of this feature:

- A Load whose replay throws reports `RolledBack`, but the graph it leaves is the snapshot it
  loaded, not the graph from before it, and `VertexCount` is not recalculated. Measured: Alice and
  Bob committed, the Load fails, the graph holds Alice and the count says 2. With the fence a
  retried Load replays Bob again, because nothing was appended behind him.
- `Fallen8.Dispose` is not idempotent: a second call throws a `NullReferenceException` from
  `TabulaRasa_internal`.
