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

- [x] Full suite with a trx log: 2864 passed, 41 skipped, 0 failed (eight more passes than `main`,
      the eight new tests).
- [x] The WAL and durability tests on Linux in the .NET 10 SDK container as uid 1000 (the
      cut-failure test relies on a read-only file, which root ignores): 108 passed, 1 skipped, 0
      failed. The skip is the Windows-only case-variant pairing test, inconclusive by design.
- [x] Browser probe published trimmed and run: passed, no trim warning.
- [x] Docs site built with the link check: complete.
- [x] Hard-rule sweep over the added lines: no dash, no forbidden name, headers present.

## Phase 5 - merge gate

- [x] Review council over the branch: three reviewers, 23 findings, each re-verified. Spec section
      6 records what changed for each.
- [x] The fixes. The unified keep-or-cut rule, the header fence, the seal on the throw path and the
      re-anchoring warning, with eleven new tests (thirteen cases) in the region and the six older
      damage sites, in four test classes, moved onto `WalFile`. The first run of the new damage tests failed on the
      search itself (it looked for the entry type where the serializer's header sits), which is
      how that was caught.
- [x] Second pass over the fixes: Phase 8.
- [ ] Merge to `main`, push, move this record to `features/done/`.

## Phase 6 - mutation check of the fixes

24 mutants of the second-round code, one at a time, a fresh backup per apply, each file verified
byte-identical after restore, no marker left in the tree afterwards. This run predates
`TornTail_OfBytesThatOnlyLookLikeLengths_IsCutAsAnInterruptedWrite`, so the counts below are of
the tests that existed then; Phase 9 is the run of the final code.

| Mutant | Killed by |
|---|---|
| no cut | the five cut tests |
| the search never finds an entry | the seven keep tests: damage at construction and at a paired Load, oversized and negative length, an entry cut short, both search bounds |
| the search always finds one | the five cut tests, the torn-tail signal test and the cut-failure test |
| a remainder shorter than a frame counts as holding one | the fragment and cut-failure tests |
| the 64 MiB bound answers no | `TornTail_TooLongToSearch_IsLeftInPlace_AndFencesTheLog` |
| no hashing bound | `TornTail_TooCostlyToSearch_IsLeftInPlace_AndFencesTheLog` |
| no entry-shape check | `TornTail_OfBytesThatOnlyLookLikeLengths_IsCutAsAnInterruptedWrite`, added because this mutant survived the first run |
| the shape check never matches | six keep tests |
| a zero length read as an entry | `TornTail_OfZeros_IsCutAsAnInterruptedWrite` |
| damage not fenced | nine tests |
| damage not reported as truncated | the seven keep tests |
| an abandoned scan treated as clean | the fail-stop and throw tests |
| a failed cut does not fence | the cut-failure test |
| the stop offset not set before a read | the fail-stop test, through the offset its error names |
| an unreadable header always reset | the two header-fence tests |
| an unreadable header never reset | the three reset cases |
| the reset boundary one byte short | the all-zeros and inverted-byte cases |
| the header fence not tripped | `UnreadableHeader_KeepsTheFile_ReportsLostHistory_AndFencesTheLogUntilASave` |
| the header recovery not reported as truncated | the two header-fence tests |
| the re-anchoring warning dropped | `UnreadableHeader_ALoadThatReAnchorsTheLog_SaysItDiscardsWhatTheLogHeld` |
| the seal not called after the loop | 14 tests |
| the seal's verdict dropped | the seven keep tests |
| the seal not called on the throw path | `ReplayThatThrows_FencesTheLog_UntilASave` |
| a zero-length candidate in the search | survives, and is equivalent: the shape check rejects a payload shorter than its own length field |

## Phase 7 - gates of the fixes

All on commit `15913986`:

- [x] Full suite with a trx log: 2877 passed, 41 skipped, 0 failed, 0 inconclusive (13 more
      passes than the first round, the 13 new cases).
- [x] The WAL and durability tests on Linux in the .NET 10 SDK container as uid 1000: 131 passed,
      1 skipped (the Windows-only case-variant pairing test), 0 failed. Neither inconclusive
      guard fired, so the read-only cut failure and the refused second open both happen there too.
- [x] Browser probe published trimmed and run: all nine checks passed, no trim warning.
- [x] Docs site built with the link check: complete, all internal links valid.
- [x] Hard-rule sweep over the added lines: no dash, no forbidden name, headers present.

## Phase 8 - second pass over the fixes

- [x] Two reviewers on the fixes, one for correctness and regressions, one for honesty and tests:
      19 findings, each checked against the code. Spec section 6, "Second pass", has the change or
      the answer for each.
- [x] The fixes: `HasEntries` counts an entry behind damage; one frame check (`DeclaredPayloadLength`,
      `CrcMatches`) for the scan and the search; the header reset boundary is the length that can
      hold a header and an entry, with its rule in `HeaderUnreadable`; the field docs name the
      search bounds; the shutdown save's messages say only what is true of a fenced log; and, at
      the user's choice, Studio, the integrations runtime and the Studio docs say "may be a
      prefix". Four new cases and one test merged from two: 135 WAL and durability tests green.

## Phase 9 - mutation check of the final code

27 mutants, one at a time, a fresh backup per apply, each file verified byte-identical after
restore. 26 killed:

| Mutant | Killed by |
|---|---|
| no cut | the six cut tests |
| the search never finds an entry | eight tests: damage at construction and at a paired Load, both corrupt lengths, an entry cut short, both search bounds, the damaged-first-entry Load warning |
| the search always finds one | the six cut tests, the torn-tail signal test and the cut-failure test |
| a remainder shorter than a frame counts as holding one | the fragment and cut-failure tests |
| the 64 MiB bound answers no | `TornTail_TooLongToSearch_...` |
| no hashing bound | `TornTail_TooCostlyToSearch_...` |
| no entry-shape check | `TornTail_OfBytesThatOnlyLookLikeLengths_...` |
| the shape check never matches | eight tests |
| a zero length read as an entry | `TornTail_OfZeros_...` |
| the CRC never checked | four tests, the older bad-CRC test among them |
| damage not fenced | nine tests |
| damage not reported as truncated | seven tests |
| an abandoned scan treated as clean | the fail-stop and throw tests |
| a failed cut does not fence | the cut-failure test |
| the stop offset never set | the fail-stop test, through the offset its error names |
| `HasEntries` ignores what follows damage | `LoadGenuinelyDifferentSnapshot_WithDamageBeforeTheUnpairedEntries_StillWarns` |
| an unreadable header always reset | three header tests |
| an unreadable header never reset | the four reset cases |
| the reset boundary one byte longer | `UnreadableHeader_JustLongEnoughToHoldAnEntry_IsKeptAndFenced` |
| the reset boundary one byte shorter | the reset case with eight bytes after the header |
| the header fence not tripped | two header tests |
| the header recovery not reported as truncated | three header tests |
| the re-anchoring warning dropped | `UnreadableHeader_ALoadThatReAnchorsTheLog_...` |
| the seal not called after the loop | 15 tests |
| the seal's verdict dropped | seven tests |
| the seal not called on the throw path | `ReplayThatThrows_FencesTheLog_UntilASave` |
| a never-readable header read as a clean scan | survives: unreachable, because nothing replays or counts a log whose header was unreadable at construction |

The banner's new assertion was checked the same way: the old wording fails it.

## Phase 10 - gates of the final code

- [ ] Full suite with a trx log.
- [ ] The WAL and durability tests and the integrations write-path tests on Linux in the .NET 10
      SDK container as uid 1000.
- [ ] Browser probe published trimmed and run.
- [ ] Web UI test suite.
- [ ] Docs site built with the link check.
- [ ] Hard-rule sweep over the added lines.
