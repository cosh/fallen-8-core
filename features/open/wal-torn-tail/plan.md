# WAL torn tail - Implementation plan

Branch `feature/wal-torn-tail` from `main`. The [spec](./spec.md) is the contract. A box is
ticked when the step is done with its evidence.

## Phase 1 - failing tests first

- [x] Eight tests in `WriteAheadLogTest` (spec section 4), run against the unfixed engine: seven
      failed, each at the assertion naming the defect. The four torn-tail tests at the cut
      offset (for example 179 expected, 204 found), the unreadable-entry test at
      `lastRecoveryTruncated`, the fail-stop and cut-failure tests at `degraded`. The
      negative-length test was added after the fix, as the one unreadable branch no test reached.
- [x] `DurabilitySignalTest`: the misnamed test renamed and given the assertions its name claimed.

## Phase 2 - the fix

- [x] `ReadEntries(Scan)` records how and where it stopped; `SealAfterReplay(Scan)` cuts a torn
      tail or trips the fence; `ReplayWriteAheadLog` calls it after every replay.
- [x] A surviving mutant showed a redundant `replayStoppedEarly` argument: an early stop always
      leaves the scan unfinished, which already takes the fencing branch. Removed, so there is one
      signal and not two.
- [x] 109 of 109 WAL and durability tests green.

## Phase 3 - mutation check

Nine mutants, one at a time, fresh backup per apply, file verified byte-identical after restore:

| Mutant | Killed by |
|---|---|
| no cut | the four torn-tail tests |
| a full-length bad-CRC entry at the end read as unreadable | `TornTail_LastEntryOfFullLengthWithABadCrc_IsCutAsAnInterruptedWrite` |
| unreadable does not fence | the unreadable, negative-length and fail-stop tests |
| unreadable not reported as truncated | the unreadable and negative-length tests |
| an abandoned scan treated as clean | `ReplayThatStopsAtAnEntryItCannotApply_FencesTheLog` |
| a failed cut does not fence | `TornTail_WhenTheCutFails_TheLogIsFencedInsteadOfAppendedBehindTheTear` |
| a negative length read as a tear | `NegativeLengthInsideTheLog_IsLeftInPlace_AndFencesTheLog` |
| the seal not called | eight tests |
| the seal's verdict dropped by the replay | the unreadable and negative-length tests |

## Phase 4 - gates

- [ ] Full suite with a trx log.
- [ ] The same WAL and durability tests on Linux, as a non-root user (the cut-failure test relies
      on a read-only file, which root ignores).
- [ ] Browser probe published and run.
- [ ] Docs site built with the link check.
- [ ] Hard-rule sweep over the added lines.

## Phase 5 - merge gate

- [ ] Review council over the branch, then a second pass over its fixes.
- [ ] Merge to `main`, push, move this record to `features/done/`.
