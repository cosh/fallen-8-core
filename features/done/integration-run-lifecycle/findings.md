# Findings from verifying this feature

Things measured while verifying `integration-run-lifecycle` that are **not about it**, recorded here
because the measurement is the expensive part and it would otherwise be lost.

## The full suite has a population of timing-sensitive tests that flake on constrained Linux

**What was seen.** Verifying this branch on Linux non-root (the CI posture) surfaced a single failing
test in a full-suite run: `Startup_AdoptsNewerOrphanCheckpoint_FromCrashWindow`. It is in the
save-games area, which this branch does not touch at all - the engine has no diff on this branch.

**The measurement.** Both branches, same container harness
(`mcr.microsoft.com/dotnet/sdk:10.0`, non-root, one `dotnet test` over the whole solution):

| Tree | Full-suite runs | Runs with a failure | Which test failed |
| --- | --- | --- | --- |
| this branch | 5 | 2 | `Startup_AdoptsNewerOrphanCheckpoint_FromCrashWindow` (once named, once unnamed) |
| `main` | 4 | 2 | `RoundTrip_IngestSearchGetDelete_AgainstALiveApiApp`, then `StalledSubscriber_GetsExactlyOneOverflowResync_FastSubscriberUnaffected` |

**Conclusion.** The rate is the same on both trees, and the failing test differs from run to run: the
flakiness is a property of the HARNESS, not of this branch and not of any one test. `main` alone
produced two different failures in four runs. On Windows the same full suite passed twice with zero
failures (2196 passed), and this feature's own tests passed 425/425 on Windows and 422/422 on Linux.
In isolation the save-games test passes 6/6 on Linux.

**Why these three.** All three share a shape: each depends on wall-clock timing rather than on a
signal. The save-games one sleeps 1500 ms so that one checkpoint's file timestamp is newer than
another's; the change-feed one turns on a subscriber falling behind fast enough to overflow while
another keeps up; the ingestion one round-trips against a live apiApp. Under a CPU-constrained
container running two thousand tests in one process, any of them can miss its assumption.

**Not fixed here, and not suppressed.** Fixing them means replacing three separate wall-clock
assumptions with signals, in three features this one does not own; doing it inside this branch would
bury unrelated engine and change-feed changes in an integrations commit. Nothing was filtered,
retried or marked flaky - the symptom is recorded above with its rate so the next person starts from
a baseline rather than from scratch.

**Worth knowing before acting on it:** CI runs the suite on Linux, so this is a real source of red
builds unrelated to whatever change is being tested. The cheapest first step is probably the
save-games one, whose 1500 ms sleep is the most obviously replaceable of the three.
