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

Training (phase 3) runs on WSL2 (Ubuntu) with an **8GB+ NVIDIA GPU** (any modern CUDA driver
— `nvidia-smi` shows the version; a newer driver runs the torch wheels fine). It needs
**Python 3.10+**, `ollama` (for `ollama create`), and `cmake` + a C/C++ compiler
(`build-essential`) for the GGUF stage — that builds llama.cpp **CPU-only**, so no CUDA
toolkit/`nvcc` is required. Ubuntu 24.04 ships Python 3.12, so no extra Python install is
needed there.

> **Node.js and the apiApp are only for *generating* the dataset (phase 2).** The dataset is
> deterministic, so a training-only box can copy `dataset/train.jsonl` (+ `dataset.meta.json`)
> from wherever it was generated and skip both — `run.sh`'s `dataset` stage reuses an existing
> file, so `./run.sh all` then needs neither.

Mind the Python version: the distro default (`python3 --version`) is often too old — Ubuntu
20.04 ships Python **3.8**, which is EOL and has no wheels for the toolchain, so `./run.sh
deps` fails with "No matching distribution found". Install a supported Python + its venv
package and point `deps` at it with the `PYTHON` env var:

```bash
# python3 < 3.10 → install a newer one (deadsnakes covers 20.04/22.04):
sudo add-apt-repository -y ppa:deadsnakes/ppa
sudo apt update && sudo apt install -y python3.12 python3.12-venv
# then, in Phase 3:  PYTHON=python3.12 ./run.sh deps

# python3 already >= 3.10 → just its venv/pip packages:
sudo apt install -y python3-venv python3-pip
```

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
box. The commands below are bash. From `nl-assist-finetune/` inside that shell, in this order
(`VARIANT=phi4-f8` for the 14B variant; default is `phi4-f8-mini`):

```bash
# 1. Build the toolchain FIRST: this creates train/.venv and installs the pinned deps.
#    Nothing under train/.venv exists until this has run. If your default python3 is < 3.10,
#    prefix with a newer interpreter:  PYTHON=python3.12 ./run.sh deps
./run.sh deps

# 2. (Optional pre-flight) eyeball the assistant marker the completion-only trainer keys on
#    (Phi model revisions differ) before spending GPU time. Needs step 1 done.
train/.venv/bin/python train/train_lora.py --inspect

# 3. Run the whole chain: deps -> dataset -> QLoRA -> merge -> GGUF -> ollama create -> PROVENANCE.
#    Safe to run on its own from a clean checkout - its first stage IS `deps`, so it builds the
#    venv itself; steps 1-2 above are only useful for the pre-flight.
./run.sh all

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
[features/delegate-model-variants](../features/open/delegate-model-variants/README.md).

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
(default `stoic_hellman_728/f8-delegate`) and tags it `phi4-f8-mini`, so the UI default works
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
`NL_GEN_OUT`. `run.sh`: `VENV`, `LLAMA_CPP`, `OLLAMA_MODEL`, `PYTHON` (interpreter used to build
the venv; default `python3`, set to e.g. `python3.12` when the default is too old), and
`TORCH_INDEX` (the CUDA wheel index torch is installed from; default `cu124`, set to e.g.
`https://download.pytorch.org/whl/cu121` for an older driver — check `nvidia-smi`'s CUDA version
against https://pytorch.org/get-started/locally/).
