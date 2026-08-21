# Plan: cloud evaluation runner

Phases as implemented. See [spec.md](spec.md) for the contract, the decisions and the honest
list of what is still unverified.

## Phase 1 - the job, host-agnostic (done)

`infra/eval-run.sh`. Deliberately first and deliberately standalone, because it is the only
piece whose shape does not depend on how the infrastructure is organised, and because a box with
a GPU should be able to run the whole evaluation without Azure at all.

- Preflight: ollama daemon, apiApp `/status`, node and npx, the harness's own presence, and
  `npm ci` for `fallen-8-web-ui` when a fresh clone lacks it (`baseline.ts` imports the shipping
  prompt modules from there).
- Per model: registry manifest check, `ollama pull` with three attempts, `ollama cp` to the short
  name, GPU-residency assertion, delete any prior results file, then
  `NL_EVAL_MODEL=<model> npx tsx eval/baseline.ts --semantic` under a per-model `timeout`.
- Verdict: assert the evaluated delegate and plugin row counts against the committed eval sets,
  refuse a partial set, emit `line-<model>.json`, then `ollama stop` to free VRAM.
- Finally: `summary.md` and `summary.json` across all models, plus the failing row ids.

## Phase 2 - the VM-side wrapper (done)

`infra/bootstrap-eval.sh`, mirroring `bootstrap.sh`'s shape (log to `/var/log/f8-eval.log`, env
from `/etc/f8-eval.env`, EXIT-trap teardown, re-entry guard) with two deliberate differences,
both load-bearing and both documented at the top of the file: `DESTROY_ON_FINISH` defaults to 0,
and the ollama daemon is restarted **after** the GPU driver is confirmed. It writes
`/opt/f8/.eval-done` or `/opt/f8/.eval-failed` (with the exit code) so the launch box can tell
success from failure from still-running, and it hands the actual work to `eval-run.sh` from the
clone rather than re-implementing it.

## Phase 3 - the launch box (done)

`infra/eval-deploy.sh`. Reuses `main.bicep` and `teardown.sh` unchanged; passes `vmName=f8-eval`
and `osDiskSizeGb=128` (no training scratch needed). Order: require `EVAL_PREFIX`, require both
SSH key halves, verify every published tag, then create, deploy, and poll over SSH every 30 s
printing the VM's latest log line as liveness. On `DONE`: fetch into
`eval/results/cloud-<stamp>/`, require a `summary.md`, print it, delete the resource group. On
`FAILED`: print the reason and the log tail, fetch whatever exists, keep the group unless
`DESTROY_ON_FAILURE=1`. On timeout: keep everything and print the `EVAL_ATTACH_RG` command.

## Phase 4 - operator surface (done)

`Start-Finetune.ps1 -Stage Eval`, with `-EvalPrefix`, `-EvalBaselines`, `-EvalWaitMin` and
`-AttachRg`. The preflight is now scoped per job, so the eval job checks both SSH key halves and
every published tag while skipping the publish-only checks, and both jobs still get the shared
launcher probe (`az`, `az login`, `jq`, `ssh`, `curl`). Plus a `RUNBOOK.md` section and an
`infra/README.md` section.

## Phase 5 - close the loop on the fine-tune job (done)

`EVAL_AFTER_TRAIN=1` makes the fine-tune run measure the models it just built, on the same VM,
before teardown. 26 added lines in `bootstrap.sh` and `deploy.sh`, none removed, default off. A
failure warns instead of failing the run, because by that point the models are trained and
published and only the measurement is missing. The summary is printed into the run log, which is
the only copy that survives a self-destructing VM.

## Deliberately not built

- **No `eval.bicep`.** `main.bicep` already parameterises everything the eval job varies. A
  second template would be duplication for the sake of symmetry.
- **No vCPU quota preflight in `eval-deploy.sh`.** Same SKU and region as the fine-tune job,
  whose `deploy.sh` preflights it, and the ARM failure diagnostics name a quota error clearly.
- **No blob-storage result sink.** A storage account plus SAS plumbing to maintain, for a 15 KB
  file, when an SSH fetch plus a re-attach path covers the sleeping-laptop case.
- **No comparison against the previous ledger.** The runner reports what this run measured; the
  fetched directory sits next to the old `baseline-*.json` files for whatever diff the operator
  wants. Automating the comparison invites the runner to have an opinion about regressions,
  which is a judgement the RETRAIN-LOG entries exist to record deliberately.
