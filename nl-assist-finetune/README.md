# nl-assist-finetune

Offline pipeline that specializes the NL-assist model
([spec](../features/done/nl-assist-finetune/spec.md),
[plan](../features/done/nl-assist-finetune/plan.md)). Nothing here is required to build or
run Fallen-8, and no weights or datasets are ever committed (spec FT-5): the repo ships the
**generator, trainer config, Modelfile template, and eval harness** — you produce the model
on your own machine.

## State

| Phase | What | Status |
|---|---|---|
| 1 | Baseline evaluation harness (`eval/`) | done |
| 2 | Dataset generator (`dataset-gen/`), every row compile-gated | done |
| 3 | LoRA training pipeline (`train/`, `run.sh`), WSL2 + NVIDIA GPU | done |
| 4 | Semantic eval gate (FT-8: seeded-graph element-set comparison, `eval/fixture.ts`) | done |

## Layout

```
shared/f8.ts         f8Fetch (429-retry) + validate() (compile authority) + streaming ollamaChat(), shared by all scripts
dataset-gen/         phase 2 — generate.ts: contract -> validated (intent, fragment) pairs
eval/                phases 1+4 — baseline.ts (+ --semantic), fixture.ts (FT-8 gate), eval-set.json (held-out)
train/               phase 3 — requirements.txt, train-config.<variant>.json, train_lora.py, merge.py, Modelfile.template
run.sh               phase 3 orchestrator (WSL2): dataset -> train -> merge -> gguf -> ollama create -> provenance
dataset/ adapter/ merged/ *.gguf Modelfile PROVENANCE.*.md   generated, gitignored (spec FT-5)
```

## Prerequisites

A Fallen-8 apiApp is the compile authority for the generator and the eval harness. Run it
with dynamic code enabled:

```powershell
$env:Fallen8__Durability__Volatile = "true"
$env:Fallen8__Security__EnableDynamicCodeExecution = "true"
dotnet run --project fallen-8-core-apiApp
```

```bash
Fallen8__Durability__Volatile=true \
Fallen8__Security__EnableDynamicCodeExecution=true \
dotnet run --project fallen-8-core-apiApp
```

Training (phase 3) runs on **Linux with an 8GB+ NVIDIA GPU** — WSL2 (Ubuntu) on a Windows GPU
box, or a native Ubuntu box. Any modern CUDA driver works (`nvidia-smi` shows the version; a
newer driver runs the torch wheels fine). The toolchain is **Python 3.10+**, `ollama` (for
`ollama create`, and to serve a base model for the optional bootstrap + eval steps), and
`cmake` + a C/C++ compiler (`build-essential`) for the GGUF stage — that builds llama.cpp
**CPU-only**, so no CUDA toolkit/`nvcc` is required. **Heads-up on Python:** Ubuntu 26.04's
default `python3` is **3.14**, which PyTorch has no CUDA wheels for yet — so build the
*training* venv with **Python 3.13** (steps below). Dataset generation and eval are fine on 3.14.

> **Node.js and the apiApp are only for *generating* the dataset (phase 2)** and the eval
> harness. The dataset is deterministic, so a training-only box can copy `dataset/train.jsonl`
> (+ `dataset.meta.json`) from wherever it was generated and skip both — `run.sh`'s `dataset`
> stage reuses an existing file, so `./run.sh all` then needs neither.

### Install the toolchain (Ubuntu)

One script installs everything — build tools, Node 22 + tsx, .NET SDK 10, uv + **Python 3.13**,
and Ollama — and is the single home for these installs (the cloud runner in
[`infra/`](infra/README.md) runs this same script):

```bash
./install-prereqs.sh          # idempotent; uses sudo for apt, or runs as root on a VM
source ./.prereqs-env.sh      # puts dotnet + uv on PATH and exports $PY313
```

> **Why Python 3.13:** PyTorch has no CUDA wheels for Ubuntu 26.04's default **3.14**
> (`./run.sh deps` would otherwise die with `No matching distribution found for torch`), and
> 26.04 doesn't carry 3.13 in its archive — so the script fetches a standalone 3.13 via
> [uv](https://docs.astral.sh/uv/) and exports its path as `$PY313`. Pass that to the pipeline
> (`PYTHON="$PY313" ./run.sh …`). Dataset generation and eval are fine on the system 3.14. For a
> newer GPU (e.g. RTX 50-series) also set `TORCH_VERSION`/`TORCH_INDEX` (cu128 + torch ≥ 2.7).

> **On WSL2, do NOT install an NVIDIA driver inside Ubuntu.** Install the current NVIDIA driver
> on **Windows** (531+); WSL2 exposes the GPU automatically, and `nvidia-smi` then works inside
> the distro. A Linux driver installed in WSL breaks the passthrough. On a **native** Ubuntu box
> you do install it: `sudo ubuntu-drivers autoinstall`, then reboot.

### Start Ollama (prerequisite for the bootstrap + eval steps)

The install script registers a systemd service. On native Ubuntu — and on WSL2 with systemd
enabled (the default on recent releases) — start it and pull the base model:

```bash
sudo systemctl enable --now ollama       # start now + on every boot
systemctl status ollama --no-pager       # confirm it is active (listening on :11434)
ollama pull phi4-mini                     # the base the bootstrap/eval reach for
```

If `systemctl` reports *"System has not been booted with systemd as init system"* (an older
WSL2 without systemd), run the server in the background instead:

```bash
ollama serve > /tmp/ollama.log 2>&1 &     # background the daemon
ollama pull phi4-mini
```

Either way, `ollama list` should succeed and the server answers on `http://localhost:11434`
(the eval's `NL_EVAL_ENDPOINT` default).

## Phase 2 — generate the dataset

Templated intents grounded in the delegate contract (`type-model.json`, `snippets.ts`,
`kinds.ts`), covering all six kinds, with the built-in-vs-user-property contrast pairs,
noisy (typo'd) intents, and the same fragment spelled per parameter name (shape invariance)
the spec calls for. **Every row is compiled through `POST /delegates/validate` before it is
kept** — an invalid fragment never enters the training set. Fully deterministic.

Run the generator — this is the whole of phase 2:

```bash
npx tsx nl-assist-finetune/dataset-gen/generate.ts            # -> dataset/train.jsonl (+ meta)
```

The two variants below are optional and NOT part of the normal flow:

```bash
# drift guard: fail if the dataset no longer matches the delegate contract (needs no model)
npx tsx nl-assist-finetune/dataset-gen/generate.ts --check
# ALSO mine extra phrasings from a base model - needs Ollama running (ollama serve + the base
# model pulled); errors out with "Ollama is not reachable" if it isn't. Skip it for a first run.
NL_GEN_BOOTSTRAP=1 npx tsx nl-assist-finetune/dataset-gen/generate.ts
```
```powershell
npx tsx nl-assist-finetune/dataset-gen/generate.ts --check      # drift guard (needs no model)
$env:NL_GEN_BOOTSTRAP = "1"; npx tsx nl-assist-finetune/dataset-gen/generate.ts   # mine extra phrasings (needs Ollama)
```

Each JSONL row is `{ delegateKind, intent, fragment, source, noisy, messages }`, where
`messages` is the real runtime prompt (`system`/`user` from the web UI's own prompt module)
plus the fragment as the `assistant` turn — so training matches the shipping prompt exactly
and the prompt contract lives in one place, not re-encoded in Python.

## Phase 3 — train on WSL2 / Linux (NVIDIA GPU)

Training is **not** a Windows-PowerShell step: `run.sh` and the CUDA / QLoRA / GGUF toolchain
need a Linux shell with an NVIDIA GPU — WSL2 (Ubuntu) on a Windows GPU box, or a native Linux
box. Run the toolchain installer once first (Prerequisites above), then from
`nl-assist-finetune/` (bash):

> **No local GPU big enough?** The 14B `phi4-f8` needs ~16 GB VRAM. [`infra/`](infra/README.md)
> provisions a throwaway Azure A10 (on-demand), trains **both** variants in one session,
> publishes them, and deletes itself — no manual steps.

```bash
# 1. Build train/.venv + install the pinned torch/deps. PYTHON="$PY313" (from install-prereqs)
#    because 26.04's system python3 is 3.14, which has no CUDA torch wheels.
PYTHON="$PY313" ./run.sh deps

# 2. (Optional pre-flight, recommended for phi4-f8) eyeball the assistant marker + the LoRA
#    target modules the trainer keys on, before spending GPU time.
train/.venv/bin/python train/train_lora.py --inspect --config train/train-config.phi4-f8.json

# 3. Run the whole chain: deps -> dataset -> QLoRA -> merge -> GGUF -> ollama create -> PROVENANCE.
#    Mini (default): PYTHON="$PY313" ./run.sh all    |    14B: add VARIANT=phi4-f8
VARIANT=phi4-f8 PYTHON="$PY313" ./run.sh all

# or a single stage:  ./run.sh deps | dataset | train | merge | gguf | modelfile | provenance
```

> If `./run.sh deps` fails with an `ensurepip` / "python3-venv is not available" error, install
> the packages from Prerequisites, then `rm -rf train/.venv` before retrying — `deps` skips
> creation when the directory already exists, so a half-built venv must be cleared first.
>
> `deps` installs torch from the CUDA wheel index in `TORCH_INDEX` (default `cu124`), letting
> pip pick the build for your Python + driver. If torch install fails or CUDA is unavailable at
> train time, set `TORCH_INDEX` to the index matching `nvidia-smi`'s CUDA version (e.g. `cu121`).

`run.sh` produces an Ollama model named for the variant (`phi4-f8-mini` by default; set
`VARIANT=phi4-f8` for the full-Phi-4 fine-tune) plus a `PROVENANCE.<model>.md` (base model +
MIT license, pinned tool versions, dataset hash — spec FT-7). Config, seed, and LoRA
hyperparameters live in the per-variant `train/train-config.<variant>.json`; the same dataset
+ config + seed reproduce an equivalent model (spec FT-1). If you hit OOM, drop
`training.perDeviceBatchSize` (and `maxSeqLength`) — the 14B `phi4-f8` needs ~16GB+.

Point the NL assist at it by setting its `model` field to `phi4-f8-mini` (or `phi4-f8`) — no
Fallen-8 code changes (spec FT-6). The two variants and the UI selection are documented in
[features/delegate-model-variants](../features/done/delegate-model-variants/README.md).

## Distribution — share your model (feedback-loop FL-4)

A fine-tune lives only in the ollama on the box that trained it. Fallen-8 core ships no
weights (spec FT-5), so a retrained model reaches *other* instances only if **you** publish
it — your opt-in channel, not something F8 does. After `ollama login` with your ollama.com
account, publish the variant you built:

```bash
PUBLISH_REPO=<your-namespace>/phi4-f8-mini ./run.sh publish                # the default variant
PUBLISH_REPO=<your-namespace>/phi4-f8 VARIANT=phi4-f8 ./run.sh publish     # the 14B variant
```

A fresh `docker compose up` then pulls automatically: `ollama-init` pulls `$F8_DELEGATE_REPO`
(default `stoic_hellman_728/phi4-f8-mini`) and tags it `phi4-f8-mini`, so the UI default works
out of the box; set `F8_PULL_PHI4F8=1` (+ `F8_PHI4F8_REPO`) to also fetch and tag `phi4-f8`.
Point a deployment at your publisher via those vars. Without Docker, pull by hand:

```bash
ollama pull <your-namespace>/phi4-f8-mini
ollama cp   <your-namespace>/phi4-f8-mini phi4-f8-mini       # adopt the short UI name…
#   …or just set the NL-assist model field to <your-namespace>/phi4-f8-mini
```

Ship `PROVENANCE.<model>.md` alongside so the licence position (Phi-4 / Phi-4-mini MIT +
MIT-generated dataset) travels with the artifact (FT-7). Until a fine-tune is published, a
fresh deploy's default 404s and should use the stock phi4-mini preset.

## Consolidate captured feedback into training (feedback-loop FL-3)

Turn the 👍/👎 files the panel exports (FL-2) into training rows. Drop your downloaded
`f8-training-*.jsonl` into `feedback/inbox/` (or pass paths as args), then:

```bash
npx tsx nl-assist-finetune/feedback/consolidate.ts   # needs the apiApp (compile authority)
```

It keeps 👍 rows, **re-validates each fragment** (drops non-compilers), drops any whose intent
is in the held-out eval set (train/test isolation), dedupes against the existing corpus, and
appends the survivors to `dataset/captured.jsonl` — **never** touching `eval/eval-set.json`.
`./run.sh train` then reads `captured.jsonl` alongside the generated `train.jsonl`, so the next
retrain folds your real-usage feedback in; re-run the eval gate to confirm it still wins.

## Evaluation (baseline / comparison runs)

This runs on the host (Windows PowerShell or bash) — it needs a model backend (the compose
Ollama on `:11434`) and a dynamic-code apiApp, not a GPU.

```bash
npx tsx nl-assist-finetune/eval/baseline.ts                              # stock base model
NL_EVAL_MODEL=phi4-f8-mini npx tsx nl-assist-finetune/eval/baseline.ts   # a fine-tuned model
npx tsx nl-assist-finetune/eval/baseline.ts --semantic                   # + FT-8 element-set gate
npx tsx nl-assist-finetune/eval/baseline.ts --rescore --semantic         # re-score recorded fragments, no model calls
```
```powershell
npx tsx nl-assist-finetune/eval/baseline.ts                                       # stock base model
$env:NL_EVAL_MODEL = "phi4-f8-mini"; npx tsx nl-assist-finetune/eval/baseline.ts  # a fine-tuned model
npx tsx nl-assist-finetune/eval/baseline.ts --semantic                            # + FT-8 element-set gate
npx tsx nl-assist-finetune/eval/baseline.ts --rescore --semantic                  # re-score, no model calls
```

One first-pass call per `eval/eval-set.json` row through the web UI's real prompt/format
modules, scored on compile (`POST /delegates/validate`), the row's regex semantic-proxy
checks, and performance. Resumable; raw results in `eval/results/` (gitignored). Summary
numbers go into the **run ledger** in [plan.md](../features/done/nl-assist-finetune/plan.md)
so movement is visible run-over-run (performance numbers are hardware-bound — compare same
machine only). `eval/eval-set.json` is held out: never feed it to training (spec FT-4).

### FT-8 semantic gate (phase 4)

`--semantic` adds the real element-set metric (spec FT-8): [fixture.ts](eval/fixture.ts)
seeds a purpose-built fixture graph, then runs BOTH the generated and the reference
fragment through the existing `/subgraph` endpoint and compares the element sets they
select — catching drafts that **compile but select the wrong elements** (the field-example
`TryGetProperty(out string label, "label")` class), which compile-only scoring is blind to.
Filter kinds map to subgraph filters (VertexFilter/GraphElementFilter → vertices,
EdgeFilter → edges); kinds with no element-set mapping (EdgePropertyFilter, the cost kinds)
and fragments needing a VertexModel/EdgeModel-only member keep the regex proxy and are
reported "n/a" (the `semanticN` column is the applicable-row count, never hidden).

Requires a **dedicated volatile apiApp** (so the fixture is the only data). Verify the
comparator itself without a model — ref-vs-ref must pass, select-nothing negatives must be
caught:

```bash
npx tsx nl-assist-finetune/eval/fixture.ts   # self-test
```

## Env vars

Set them per shell — bash: `NAME=value cmd`; PowerShell: `$env:NAME = "value"; cmd` (persists
for the session, `Remove-Item Env:NAME` to clear). Shared by every script (defined in `shared/f8.ts`):
`NL_EVAL_MODEL` (default `phi4-mini`), `NL_EVAL_ENDPOINT` (default `http://localhost:11434`),
`NL_EVAL_F8` (default `http://localhost:5000`). Generator-only: `NL_GEN_BOOTSTRAP`,
`NL_GEN_OUT`. `run.sh`: `VARIANT` (`phi4-f8-mini` | `phi4-f8`), `VENV`, `LLAMA_CPP`,
`OLLAMA_MODEL`, `PYTHON` (interpreter used to build the venv; default `python3`), and
`TORCH_INDEX` (the CUDA wheel index torch is installed from; default `cu124`, set to e.g.
`https://download.pytorch.org/whl/cu121` for an older driver — check `nvidia-smi`'s CUDA version
against https://pytorch.org/get-started/locally/).
