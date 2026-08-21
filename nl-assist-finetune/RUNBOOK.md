# Runbook: a fine-tune round from a Windows box

The ordered checklist for sitting down at a machine and getting a round trained. It says
**what to do in which order**; what each thing IS lives elsewhere and is not repeated here:
[`README.md`](README.md) for the pipeline phases, [`infra/README.md`](infra/README.md) for the
Azure A10 runner, [`RETRAIN-LOG.md`](RETRAIN-LOG.md) for what a round owes.

[`Start-Finetune.ps1`](Start-Finetune.ps1) automates the Windows side of steps 3 and 4 and
blocks on the preconditions the runner cannot recover from. `-Stage Preflight` (its default)
changes nothing, so it is always safe to run first.

## 0. One-time setup on this machine

| | |
|---|---|
| Repo | a clone of this repository (the machine needs its own checkout) |
| Windows side | .NET SDK 10 and Node.js 22 - used only to run the apiApp + `consolidate.ts` in step 4 |
| A bash | a WSL2 distro (recommended) or Git Bash. `deploy.sh` runs there |
| Inside that bash | Azure CLI and `jq`, then `az login` **and** `az account set -s <subscription>` |
| Azure | NVadsA10v5 vCPU quota in the region you pass (`deploy.sh` preflights it) |
| SSH | a keypair at `~/.ssh/id_ed25519` (Windows home) |
| Ollama | an account, and the signing key at `~/.ollama/id_ed25519` whose public half is registered at <https://ollama.com/settings/keys> |

The Ollama key is what makes the run unattended: without it `deploy.sh` refuses to publish, and
a run that cannot publish would self-destruct the only copy of the models.

## 1. Sync the checkout

```powershell
git fetch origin
git status              # the branch you want trained must be pushed
```

The VM clones `REPO_REF` from **origin**, so an unpushed commit is not trained and a local
checkout that is behind means you are not looking at what will train. The preflight reports the
direction of any divergence.

## 2. Carry the field feedback (skip if you have no captures)

The judged captures and the consolidated corpus are gitignored, so they live **only** on the box
that judged them. Copy the raw `f8-training-*.jsonl` exports (e.g. from your OneDrive share
folder) to this machine and let step 4 consolidate them there.

Carry the **raw captures**, not `dataset/captured.jsonl`: `consolidate.ts` rebuilds each row's
system prompt from the current contract, so consolidating here produces rows that match the
prompt that ships today, while a copied `captured.jsonl` keeps whatever prompt it was built with.

This only works on a box whose `dataset/captured.jsonl` does not already hold those rows -
`consolidate.ts` dedupes against the existing corpus, so re-running it over the same captures
adds nothing and leaves the old prompts in place (measured: 37 captures read, 14 duplicates,
0 added). A fresh clone has no `dataset/` at all, which is the case you want. To refresh the
prompts on a box that already consolidated them, delete `dataset/captured.jsonl` first.

## 3. Preflight

```powershell
powershell -File nl-assist-finetune\Start-Finetune.ps1
```

Fix every `FAIL`. `WARN`s are judgement calls, not blockers. Two things worth reading rather
than skimming: the reported **az subscription name** (it is the one that gets billed) and the
branch line.

## 4. Consolidate and launch

```powershell
# see the exact deploy.sh invocation without creating anything:
powershell -File nl-assist-finetune\Start-Finetune.ps1 -Stage Azure -DryRun -PublishPrefix <ns>

# the real thing:
powershell -File nl-assist-finetune\Start-Finetune.ps1 -Stage Run -PublishPrefix <ns> -CapturesFrom <folder>
```

`-Stage Run` starts a volatile apiApp on :5000 as the compile authority, consolidates the
captures against it, then hands the session to `deploy.sh`: VM up, dataset generated **on the
VM** from the current contract sources, both variants trained, each published to
`<ns>/<variant>`, resource group self-deleted. Useful switches: `-Variants 'phi4-f8-mini'` for
one variant, `-Location`, `-Spot`, `-NoPublish` (then the group survives and you delete it),
`-Launcher GitBash`.

`deploy.sh` prints the ssh line for watching; the VM's own log is `/var/log/f8-finetune.log`.

## Evaluating the published models on cloud GPU

Same box, same launcher, different job. Measuring needs a GPU because CPU inference for these
models is roughly 14 s/token, and it needs the models to be published first (step 4 above).

```powershell
# what it would create, without creating it:
powershell -File nl-assist-finetune\Start-Finetune.ps1 -Stage Eval -DryRun -EvalPrefix <ns>

# the real thing - this WAITS, because the results must come down before the VM is deleted:
powershell -File nl-assist-finetune\Start-Finetune.ps1 -Stage Eval -EvalPrefix <ns>
```

It evaluates `phi4-f8-mini`, `phi4-f8` and the stock `phi4-mini` on one GPU in one session, then
copies the results into `nl-assist-finetune/eval/results/cloud-<UTC stamp>/` and deletes the
resource group. Your existing `baseline-*.json` ledger is never overwritten.

**If this box sleeps or you lose the connection**, nothing is lost and nothing is leaked: the VM
keeps running and holds the results, and the resource group is not deleted until they are
fetched. Re-attach with the resource group name the run printed:

```powershell
powershell -File nl-assist-finetune\Start-Finetune.ps1 -Stage Eval -AttachRg rg-f8-eval-xxxxxx
```

A genuinely abandoned run is capped by the VM's own teardown timer, 4 h after boot.

Useful switches: `-Variants 'phi4-f8-mini'` for one model, `-EvalBaselines ''` to skip the stock
comparison, `-Spot` (an eval is short and re-runnable, so eviction costs little), `-EvalWaitMin`.

To measure the models **as part of** a fine-tune run instead, pass `EVAL_AFTER_TRAIN=1` to
`deploy.sh`: the same evaluation runs on the training VM before teardown and its summary goes
into the run log. That log is the only copy, since that VM self-destructs.

The job itself lives in [`infra/eval-run.sh`](infra/eval-run.sh) and needs no Azure: on a box
that already has a GPU, an ollama daemon and an apiApp, run it directly.

## 5. After the run

1. Confirm both models are pushed (`ollama pull <ns>/phi4-f8-mini`, and the registry page).
2. Run the phase-4 eval (README, "Evaluation") and compare against the previous
   `eval/results/baseline-*.json`.
3. Close the `RETRAIN-LOG.md` entries this round actually absorbed, recording the measured
   verdict per variant. Entries the eval shows are still failing stay `PENDING` - that is the
   log working, not a formality.

## Before you spend the GPU hours

`RETRAIN-LOG.md` has four `PENDING` entries, and a round started today reproduces the last
round's results for most of them, because the follow-up work each entry proposed was never
wired: the `EdgePropertyId` doc in `type-model.json` still never says "type" (the missing
lexical bridge for both edge-type failures), the two held-out rows the 2026-08-18 entry asked
for are not in `eval/eval-set.json`, there are no mini-scale whole-type rows, and the
`epf-knows` eval failure is recorded in no entry at all. Two of the four entries are already
absorbed by the 14B and are **mini-only** gaps.

None of this blocks a run. It decides what the run is worth.

## Gotchas that have cost time

- **`dataset/` is gitignored**, so the VM's clone has no `train.jsonl` and regenerates it from
  the contract sources itself. No stale corpus can travel to the VM. But on a box that already
  has a `dataset/train.jsonl`, `run.sh dataset` **reuses it and nothing verifies its
  `sourceHash`** - which is why the local-GPU sequence (`-Stage Local`) begins by deleting it.
- **Port 5000 must be free** for the Consolidate stage, or pass `-ApiPort <free port>`. If
  something already answers `/status` there the script refuses to continue, because the compile
  gate decides which rows enter the corpus and a stranger's build is not a gate you chose. This
  is not theoretical: while the script was being written, a dev instance already held :5000, our
  own start died with "address already in use", the health poll passed against the stranger, and
  the consolidation reported success anyway. Use `-UseExistingApi` only when you mean it.
- **A Windows-only Azure CLI is not callable from WSL** (the extensionless shim is an MSYS
  script). Install `az` inside the distro, or use `-Launcher GitBash`.
- **`$HOME` differs** between WSL, Git Bash and Windows, so the SSH and Ollama key paths are
  resolved on the Windows side and passed to `deploy.sh` explicitly.
- **`deploy.sh` derives `REPO_URL`/`REPO_REF` from git with an `|| echo main` fallback**; under
  WSL on `/mnt/c` a dubious-ownership error silently trains `main`. The script passes both
  explicitly for that reason.
