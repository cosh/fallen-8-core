# WAL torn tail - Specification

> **Status:** Implemented on `feature/wal-torn-tail`, awaiting the merge gate. Found by the
> embedded-MCP evaluation of 2026-10-07 ([features/open/embedded-mcp/](../embedded-mcp/spec.md)),
> reproduced independently with the engine's public API, and fixed test-first. Present since the
> write-ahead log shipped; v0.0.41 carries it.

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
save. A crash loop is the realistic case, which is also when durability matters most.

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

## 2. Decision

The reader records where and why it stopped (`WriteAheadLog.Scan`), and
`WriteAheadLog.SealAfterReplay` runs after every replay, construction-time and paired-load alike,
before anything can be appended. Its doc comment is the code home of this rule.

| The scan ended | Meaning | Action | `lastRecoveryTruncated` |
|---|---|---|---|
| clean | every byte after the header is a complete entry | none | false |
| torn tail | one incomplete entry runs to the end of the file: fewer than 8 bytes, a length past the end, a short read, or a full-length entry with a bad CRC that ends exactly at the end | cut the file back to the last complete entry and fsync | false, it was never acknowledged |
| unreadable | an entry that cannot be read has more bytes after it, or its length is negative | keep the bytes, trip the fence | true |
| abandoned | the replay broke off at an entry it read but could not apply | keep the bytes, trip the fence | true |
| cut failed | | trip the fence | false |

The fence is the existing crash-durability-hardening D1 mechanism: commits report non-durable,
`degraded` is true, and a successful save rewrites the log and lifts it. Cutting an unreadable
region would destroy acknowledged entries, and appending behind it would repeat the defect, so the
bytes stay and the state says so.

A full-length entry with a bad CRC at the very end is a tear because the sectors of one write can
reach the disk out of order on a power loss. With bytes after it, the same failure is damage to
acknowledged history.

## 3. What it does not do

- **No resynchronisation past an unreadable entry.** A log written by an earlier version can hold
  commits behind a torn entry, and later sessions may have appended more behind those, against an
  id space that did not include them; replaying them blindly could misapply them. Such a log is
  read as a torn tail when the torn entry's length claims more bytes than remain, and the commits
  behind it are cut. They were unreachable to the versions that wrote them, and their next save
  would have discarded them.
- **No side file** for the bytes a fenced log keeps; the next save rewrites the log, as before.
- **No directory fsync** after a cut, the same deferral the engine's other file I/O documents
  (`DurableFileIo`).
- **No cross-process lock.** Two processes on one data directory remain unsupported; the
  embedded-MCP evaluation measured what that does, and the local native host owns the answer.

## 4. Tests

`WriteAheadLogTest`, region "commits after a recovery". Each restarts twice, because the defect is
invisible after one restart. Entry boundaries come from the file length after each commit, so the
tests assume nothing about the frame format, except the negative-length case, which says so.

| Test | Pins |
|---|---|
| `TornTail_CommitsAfterRecovery_SurviveTheNextRestart` | the defect itself, the exact cut offset, both flags false, the warning |
| `TornTail_FragmentShorterThanAnEntryHeader_IsCutBeforeTheNextCommit` | a tear inside a length prefix |
| `TornTail_LastEntryOfFullLengthWithABadCrc_IsCutAsAnInterruptedWrite` | the out-of-order-sectors tear |
| `TornTail_AfterAPairedLoad_CommitsSurviveTheNextRestart` | the snapshot-paired recovery path |
| `UnreadableEntryInsideTheLog_IsLeftInPlace_FencesTheLog_AndReportsTruncatedHistory` | bytes untouched, fence, truncated, a save lifts the fence |
| `NegativeLengthInsideTheLog_IsLeftInPlace_AndFencesTheLog` | the corrupt-length branch |
| `ReplayThatStopsAtAnEntryItCannotApply_FencesTheLog` | a fail-stop on a CRC-valid entry, spliced from another log |
| `TornTail_WhenTheCutFails_TheLogIsFencedInsteadOfAppendedBehindTheTear` | the cut's failure path |

`DurabilitySignalTest.ATornTail_IsDiscarded_WithoutReportingTruncatedHistory` replaces the
misnamed test and asserts what its old name only claimed.

## 5. Impact on existing features

- **Engine package:** behaviour only, no signature change; the `DurabilityState` field docs gain
  the new causes.
- **REST and the OpenAPI snapshot:** the `lastRecoveryTruncated` description gains one sentence;
  the snapshot was regenerated and that is its only change.
- **Integrations runtime:** it defers deletions when the target is degraded or truncated. That now
  also happens after a mid-log unreadable entry, which is the conservative and correct outcome. No
  code change.
- **F8 Studio:** the durability banner shows the same two fields; no change.
- **MCP:** `f8_overview` does not report durability; no change.
- **Docs:** [save-games](../../../docs/src/content/docs/save-games.mdx), the WAL row and the
  `degraded` and `lastRecoveryTruncated` rows.
- **Browser host:** the probe opens no WAL; it was run as the trim gate for the engine change.
- **NL-assist, architecture diagrams, persisted recipes:** none.
