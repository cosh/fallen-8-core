# Spec: cloud evaluation runner

**Status: implemented** (2026-08-21). The evaluation of published NL-assist models runs on
on-demand Azure GPU hardware, as a sibling of the fine-tune runner, and the results are copied
back to the operator's box before the infrastructure is deleted.

## Why

The fine-tune runner ([`nl-assist-finetune/infra/`](../../../nl-assist-finetune/infra/)) trains,
publishes and self-destructs **unmeasured**. Measuring afterwards needs a GPU, because CPU
inference for these models is roughly 14 s/token, which is not a slow run but an unusable one. So
the 2026-07-30 verdicts recorded in
[`RETRAIN-LOG.md`](../../../nl-assist-finetune/RETRAIN-LOG.md) had to be gathered later on a
separate box, and two of them were taken on two *different* hosts, where one row's verdict
actually flipped between them. An evaluation that is hard to run is an evaluation that does not
get run, and a cross-host comparison is not a controlled one.

## What it is

Three scripts, one reused template:

| Piece | Role |
|---|---|
| `infra/eval-run.sh` | The job itself, host-agnostic: pull, evaluate, verify, summarise. Runs on the Azure VM, and equally on any box that already has a GPU, an ollama daemon and an apiApp. |
| `infra/bootstrap-eval.sh` | The VM-side wrapper: clone, toolchain, GPU driver, apiApp, then hand over to `eval-run.sh` from the clone. |
| `infra/eval-deploy.sh` | The launch box: preflight, create, wait, fetch, tear down. |
| `infra/main.bicep` | **Reused unchanged.** Every fine-tune assumption in it is already a parameter. |

A full run evaluates both published variants plus the stock base model on one GPU in one
session: `phi4-f8-mini`, `phi4-f8` and `phi4-mini`. `eval/baseline.ts --semantic` covers the
delegate rows, the whole-type plugin rows and the FT-8 element-set gate in a single invocation,
so there is nothing else to orchestrate.

## Decisions

1. **Sibling scripts, not a refactor of the proven path** (2026-08-21, operator's call). The
   eval job's VM-side needs are nearly a subset of the fine-tune job's, which argued for a
   shared `JOB=finetune|eval` concept. Rejected: that path has already lost artifacts twice, and
   its value is that it works. The cost is accepted duplication, quantified below.
2. **The launch box waits, fetches, then tears down.** The fine-tune artifact escapes to a
   registry; an eval's artifact is a 15 KB JSON file. A VM that self-destructs on success would
   destroy the only copy, which is the exact shape of the 2026-07-30 loss. So the VM is deployed
   with `DESTROY_ON_FINISH=0` and `eval-deploy.sh` deletes the resource group only after the
   results are on disk locally. `EVAL_ATTACH_RG=<rg>` re-attaches, which is the answer to "my
   laptop slept"; the VM's own `f8-teardown.timer` caps a genuinely abandoned run, on a deadline
   derived from the time budget (an hour beyond the larger of the wait and the run's worst case,
   ceiling 8 h) rather than a fixed 4 h. Because that timer is boot-relative and the wait is not,
   the launch box stops it while attached and re-arms it if it gives up.
3. **Results land in a per-run directory.** `eval/results/` is gitignored, so the existing
   `baseline-*.json` files are the only copy of the July ledger. Fetching into that directory
   would overwrite them, so results go to `eval/results/cloud-<UTC stamp>/`.
4. **The stock base model is measured in the same session** (operator's call). Roughly 10 extra
   minutes on already-paid hardware buys a same-hardware comparison instead of a cross-host one.
5. **The fine-tune job gained an opt-in post-train evaluation** (`EVAL_AFTER_TRAIN=1`,
   operator's call). This is the one place the proven path is touched: 26 added lines, none
   removed, default off, and a failure there warns rather than failing the run, because the
   models are already trained and published by that point.

## Guards, and the incident each one answers

- **A published tag that does not exist** is checked from the launch box with an auth-free
  manifest GET before any resource is created, and again on the VM before pulling gigabytes.
- **CPU fallback.** `install-prereqs.sh` installs ollama *before* the GPU driver exists, so a
  daemon inherited from that moment has no CUDA. The fine-tune job never notices, because it
  only creates and pushes models. An eval would silently serve every draft from the CPU and look
  like a hang. So `bootstrap-eval.sh` restarts the daemon after the driver is confirmed, and
  `eval-run.sh` then *proves* GPU residency per model via `ollama ps` before spending the hour.
- **A partial run reading as a measurement.** `baseline.ts` exits 0 even when every row fails,
  and it *resumes* a pre-existing results file by skipping row ids it already holds, summarising
  an empty set as `"-"`. So `eval-run.sh` deletes any prior file for the model, then asserts the
  evaluated row counts against the committed eval sets and refuses otherwise.
- **Naming drift.** Results are named after the model with `[^\w.-]` replaced by `_`, so
  evaluating `<ns>/phi4-f8-mini` would write `baseline-<ns>_phi4-f8-mini.json` and quietly stop
  being comparable with the ledger. The runner pulls the registry path and `ollama cp`s it to the
  short name, exactly as `scripts/ollama-init.sh` does for compose.

## Duplication, stated honestly

Decision 1 buys stability by paying in duplication. Copied from `bootstrap.sh` into
`bootstrap-eval.sh`: the GRID driver install (~25 lines), the teardown/trap wrapper (~25), the
clone plus toolchain (~15), the apiApp start (~8). Copied from `deploy.sh` into
`eval-deploy.sh`: the cloud-init skeleton and the deployment call with its failure diagnostics
(~70), key and repo resolution (~25). Roughly **170 duplicated lines**. Mitigations: no
*explanation* is duplicated (the GRID driver's why stays in `bootstrap.sh`, referenced by
pointer), the evaluation logic itself has exactly one home in `eval-run.sh` which both jobs
call, and the vCPU quota preflight was deliberately not copied because the deployment-failure
diagnostics name a quota error clearly.

## Impact on existing features

- **Engine, REST, OpenAPI snapshot, MCP bridge, Studio UI:** none. No product code is touched.
- **NL-assist harness (`eval/`)**: unchanged. The runner is a caller, not a modification, and
  results are written to a per-run subdirectory so no existing ledger file is disturbed.
- **`RETRAIN-LOG.md`**: no new entry. Its own rule is to log changes to the delegate-fragment
  surface the model drafts against; this changes where an evaluation runs, not what is drafted.
  The runner is, however, the mechanism that produces the per-variant verdicts the log's open
  entries are waiting for.
- **Fine-tune runner:** the additive `EVAL_AFTER_TRAIN` hook above. Default off, so an existing
  invocation behaves identically.
- **`Start-Finetune.ps1` / `RUNBOOK.md`:** updated in this change (`-Stage Eval`, plus an
  `-AttachRg` re-attach path and a runbook section).
- **Docs site:** [`nl-assist.md`](../../../docs/src/content/docs/nl-assist.md) updated. It
  documents this offline pipeline, and two of its claims went stale: the pipeline table's
  Evaluate row said "no GPU", which is the misleading half of the truth once a GPU path exists
  (it *starts* without one and then generates at ~14 s/token), and the `infra/` line described
  training only. It now also points at the runbook's evaluation-only section. The link-checked
  docs build was re-run green.
- **Architecture diagrams:** not touched, and not stale. This changes where an evaluation runs,
  not how any client reaches Fallen-8 or what ships in the deployable.
- **Root README "Key features":** no entry. That list is for user-facing features on
  <https://docs.fallen-8.com>; this is contributor tooling, like the fine-tune runner, which has
  no entry either.

## Adversarial review, 2026-08-21

A 14-agent review (five angles, then a skeptic per finding instructed to refute it) produced 47
raw findings, 37 unique; the 9 highest-severity were verified, of which **7 were confirmed and 2
refuted**. It found two defects that would have cost money or a measurement, and both were in the
parts this spec had claimed were safe.

1. **The eval VM's cost backstop was inert.** The shared `teardown.sh` discovers its resource
   group only from `/etc/f8-finetune.env`, which this job never writes, and its systemd unit
   passed no `EnvironmentFile`, so the timer-fired teardown exited 1 on empty `AZ_*` with no
   retry. Worse, `teardown.sh` exits 0 when `DESTROY_ON_FINISH != 1`, which this job sets to 0 as
   its *normal* state, so even supplying the env file would not have deleted anything. The only
   working teardown was the launch box's happy path, so a timeout, a Ctrl-C, a sleeping laptop or
   a Spot eviction left an A10 running indefinitely, while this spec, the RUNBOOK and
   `infra/README.md` all promised a 4 h cap. Fixed: `teardown.sh` reads both jobs' env files and
   honours a dedicated `F8_BACKSTOP=1` that overrides `DESTROY_ON_FINISH`, which the eval unit
   sets. Exercised locally in both directions, including that the fine-tune job's deliberate
   keep-the-VM debug mode still behaves as before.
2. **The runner deleted the previous measurement before proving it could produce a new one.**
   `evaluate()` removed `baseline-<model>.json` before the GPU assertion, so on a non-NVIDIA box
   the guaranteed CPU failure destroyed the only copy of the July ledger (`eval/results/` is
   gitignored). Fixed: the file is moved aside with a timestamp, never deleted, and only after
   GPU residency is proven.

The review also refuted a claim I had made while designing the fix: that the 4 h backstop would
delete un-fetched results. That path cannot execute, precisely because the backstop never fired.

Also fixed, in `eval-run.sh` unless noted: `grep -F` matched `phi4-f8` inside `phi4-f8-mini`, so
one model's processor was reported as the other's (now an exact `awk` match on the NAME column);
stale `line-*.json` from an earlier run were folded into this run's summary; a failed *optional*
baseline pull aborted a completed measurement of the real variants (now contained in a subshell);
a timed-out model's partial file was never staged where the launch box fetches from; the
completeness assertion never checked that the FT-8 gate produced any verdicts at all. In
`eval-deploy.sh`: `REGISTRY` was honoured locally but never reached the VM; the fetch's success
test was "the directory is non-empty"; `RUNNING` was inferred purely from marker absence, so a
dead job billed the whole window (now a `DEAD` state read from the unit); and the private key is
copied to a 0600 file, because under WSL a key on `/mnt/c` presents as 0777 and OpenSSH would
refuse it *at fetch time*, after the GPU hour is spent. In `bootstrap.sh`: the marker was written
after the eval, so a reboot mid-measurement retrained everything; `systemctl restart ollama` was
unguarded under `set -Euo pipefail`, able to abort a run whose models were already published; and
the post-train eval is capped at 30m per model to fit inside the unchanged 8 h backstop. In
`Start-Finetune.ps1`: `-AttachRg` was blocked by its own `-EvalPrefix` requirement, so the
documented recovery command failed its own preflight; a hardcoded `-EvalWaitMin 180` would have
overridden the derived budget with less than the worst case; the Eval stage blocked on
`dotnet`/`node`/`npm` it never uses; an unreachable `origin` was reported as "branch not pushed";
interpolated values containing an apostrophe are now refused rather than silently breaking the
generated shell command; and `-EvalAfterTrain` exists at all.

**The time budget.** Three numbers were hardcoded independently and did not nest: three default
models at 60m each consumed the entire 180m wait, before ~30m of GRID install, the toolchain, a
Release build and ~14 GB of pulls. `eval-deploy.sh` now derives them - models x per-model cap +
a setup allowance is the wait, and the VM's backstop is armed an hour beyond that - and prints the
arithmetic before creating anything. Deriving the backstop from the wait made the nesting check
vacuous, so it was replaced with an 8 h cost ceiling that refuses a typo'd `EVAL_WAIT_MIN=1000`
instead of silently provisioning an 18 h one.

**Not fixed:** 28 lower-severity findings were reported but not individually verified, and one
remains open by choice: a Spot eviction has no on-VM reaper at all, because a deallocated VM runs
no timers. That is now documented as a reason not to use `-Spot` for this job, rather than the
recommendation it previously was.

## Second adversarial pass, over the fixes

Because the first pass's fixes were written fast and the code spends money, a second 12-agent pass
audited the FIXES themselves: did each close its defect, did any introduce a new one, did the
shared `teardown.sh` and `bootstrap.sh` regress the proven path, and were any new claims false. 39
raw findings, 32 unique, 5 confirmed of the 8 verified. It was worth more than the first pass per
finding, because two of the defects were mine and recent.

- **A fix resurrected a hazard the first pass had refuted.** Making the backstop actually delete
  (it had been inert) revived the cross-clock problem: the timer is armed `OnBootSec` on the VM,
  the wait is invocation-relative on the launch box, and the re-attach path armed a fresh
  full-length wait knowing nothing about the VM's remaining timer. So the reaper could delete the
  group, with the results, while a re-attached waiter believed it had hours. My comment asserting
  the nesting held "BY CONSTRUCTION" was true only within one fresh invocation. Fixed: the launch
  box stops the timer on first contact and re-arms it if it gives up, falls back to clamping its
  own deadline when it cannot stop it, and treats a vanished resource group as a terminal state
  instead of an unreachable host.
- **The reaper was derived from the wait alone**, so `-EvalWaitMin 60` with four models armed a 4 h
  reaper for a 6 h run. Now derived from the larger of the wait and the worst case.
- **The keep-the-box decision did not survive a reboot.** `EVAL_AFTER_TRAIN=1` set
  `DESTROY_ON_FINISH=0` in-process only, while the same commit moved the marker to *before* the
  eval - so a reboot after a measured run hit the marker-present early exit and tore down the
  measurement. Now persisted as a marker that the early-exit path honours. The eval-failure branch
  keeps the box too: the operator asked for a measurement, and the reason it failed is on that disk.
- **The stricter fetch test could never pass for a failed run**, because a failure stages
  `partial-*.json` and never writes a summary, so the failure path reported "(nothing to fetch)"
  over real artifacts. Fetching now has three outcomes, and prints what arrived.
- **`-AttachRg` still blocked on the `Repo` scope**, whose checks exist because the VM clones a
  ref - irrelevant to a re-attach, which pulls nothing.

Also corrected from that pass: `_to_min` fed unvalidated text into `$(( ))`, so the friendly error
was unreachable for exactly the inputs it was written for; the staleness sweep cleared only
`line-*.json` and left a previous run's summary and partials to be fetched as if they described
this run; the timeout message asserted a partial set had been staged when there might be nothing to
stage; a message hardcoded "~4h" after the backstop became derived; the usage header still
documented the removed fixed default and still sold Spot; `${ADMIN_USER:-azureuser}` implied a
substitution that never happens on the VM; the apostrophe guard covered only the eval launcher; and
the post-train budget comment did not survive its own arithmetic.

Two earlier claims in this document were also wrong and are fixed above: the backstop was described
as a 4 h cap when it is derived, and the "results are not deleted until fetched" promise in the
RUNBOOK held only while the launch box is attached.

## Verification, and what remains unverified

Checked without paying for a run: all five shell scripts parse under both Git Bash and WSL
bash; `main.bicep` builds clean with zero warnings and every parameter passed by
`eval-deploy.sh` exists in it; the rendered cloud-init parses as YAML, its two base64 payloads
decode back to the real scripts with correct shebangs, and `customData` is 24 KB against ARM's
~64 KB cap; the fine-tune cloud-init still parses after the hook was added; the row-count guard
was exercised against the real July results file in both directions (complete passes and
reproduces the ledger numbers exactly, incomplete refuses); the summary stage independently
reproduced the same five failing row ids that manual analysis had found; and the Windows
preflight was run for both jobs.

**Not verified:** no Azure deployment has been performed. The GRID driver install, the model
pulls, the GPU-residency assertion against a real daemon, the SSH poll/fetch loop and the
teardown have never executed against real infrastructure. The first real run should be treated
as the first test of those paths.
