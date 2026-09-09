# Findings from verifying this feature

Things measured while verifying `nahil-default-backend` that are **not about it**, recorded here
because the measurement is the expensive part and it would otherwise be lost. Same intent as
[integration-run-lifecycle/findings.md](../../done/integration-run-lifecycle/findings.md), which
already records that this suite has a population of timing-sensitive tests.

## `IntegrationsResumeTest.AResumedRunIsCancellableLikeAnyOther` fails about one full-suite run in three, and could not say why

**What was seen.** One full-suite run on this branch failed a single test that the branch does not
touch:

```
Failed AResumedRunIsCancellableLikeAnyOther [34 ms]
  Assert.AreEqual failed. Expected:<0>. Actual:<1>.
  and cancelling a resumed run ends it, so its entry goes with it
  at IntegrationsResumeTest.cs:line 623
```

**The measurement** (Windows, one `dotnet test` over the whole solution per run, same machine, no
other load):

| Tree | Full-suite runs | Runs with this failure |
| --- | --- | --- |
| this branch | 3 | 1 |
| `main` | 4 | 0 |

In isolation it passes 5/5, and its whole class passes 18/18 twice. So it is load- or
order-dependent, not deterministic.

**Why it is not this branch.** Three independent reasons, in increasing strength:

1. The branch touches no integrations code, no file I/O and no spool. Its diff is the chat
   options, the model-provider retry transport, docs, one npm script and tests.
2. The same branch passed the full suite twice before the failing run, and the only change in
   between was documentation plus one doc comment.
3. The failing assertion's mechanism is in `fallen-8-integrations`, and is reachable without any
   of this branch's code being loaded (below).

**The mechanism, as far as reading gets you.** `SpoolFiles()` counts **every file** in the run
spool directory, and the spool can legitimately leave one behind on two paths that
`fallen-8-integrations/Run/RunSpool.cs` deliberately tolerates:

- `Remove` (line ~330) swallows `IOException`/`UnauthorizedAccessException` and logs "could not be
  deleted ... remove it by hand". A delete that fails leaves the `.job.json` or `.progress` file
  exactly where the assertion counts it.
- `Write` (line ~308) writes `path + ".tmp"` and then renames it over the target. `Delete` removes
  `JobPath` and `ProgressPath` and **not** a stray `.tmp`, so any write that got as far as the
  temporary and no further leaves a file no cleanup path owns.

Both are consistent with the observed count of exactly 1. Neither is distinguishable from the
other after the fact, because **this assertion does not print what was left behind** while six of
its siblings in the same file do (`"Left: " + String.Join(", ", SpoolFiles())`, lines 412, 427,
441, 459, 470, 487). That asymmetry is why a real failure produced no usable evidence.

The suite is sequential (no `[Parallelize]`, and `PluginDiscoveryDegradationTest` says so
explicitly), and the spool directory is a per-test GUID under the temp path, so cross-test
interference and in-process write contention are both ruled out. What differs between a
full-suite run and an isolated one is elapsed process time and accumulated I/O, which is enough
for a transient `IOException` on Windows.

**What was done here, and what was not.** Nothing was filtered, retried or marked flaky. The one
change made is that the assertion now prints the leftover filenames like its siblings, so the next
occurrence names the file and settles which of the two mechanisms it is. That is a diagnostic, not
a fix: it does not make the test pass, and if the flake is the `.tmp` path it is a **real defect in
the runtime** rather than a test problem, because a stray temporary is resumed-run state that no
cleanup path owns.

**The fix, when someone takes it.** Wait for one occurrence with the filename, then either
(a) retry a delete-pending `IOException` briefly in `Remove`, since the production comment already
says a surviving entry makes the next restart re-run finished work, or (b) have `Delete` also
remove `path + ".tmp"`, which is cheap and correct regardless. Both are in the integrations
feature, and doing either inside this branch would bury an unrelated runtime change in a
configuration-default commit.
