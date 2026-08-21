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
   laptop slept"; the VM's own `f8-teardown.timer` (4 h after boot, versus the fine-tune job's
   8 h) caps a genuinely abandoned run.
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
