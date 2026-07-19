# nl-assist-finetune

Offline pipeline that specializes the NL-assist model
([spec](../features/open/nl-assist-finetune/spec.md),
[plan](../features/open/nl-assist-finetune/plan.md)). Nothing here is required to build or
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
train/               phase 3 — requirements.txt, train-config.json, train_lora.py, merge.py, Modelfile.template
run.sh               phase 3 orchestrator (WSL2): dataset -> train -> merge -> gguf -> ollama create -> provenance
dataset/ adapter/ merged/ *.gguf Modelfile PROVENANCE.md   generated, gitignored (spec FT-5)
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

Training (phase 3) additionally needs WSL2 (Ubuntu) with an **8GB+ NVIDIA GPU** (CUDA 12.x
driver visible via `nvidia-smi`), Python 3.10+, Node.js (for the generator), and `ollama`.

## Phase 2 — generate the dataset

Templated intents grounded in the delegate contract (`type-model.json`, `snippets.ts`,
`kinds.ts`), covering all six kinds, with the built-in-vs-user-property contrast pairs,
noisy (typo'd) intents, and the same fragment spelled per parameter name (shape invariance)
the spec calls for. **Every row is compiled through `POST /delegates/validate` before it is
kept** — an invalid fragment never enters the training set. Fully deterministic.

```bash
npx tsx nl-assist-finetune/dataset-gen/generate.ts            # -> dataset/train.jsonl (+ meta)
npx tsx nl-assist-finetune/dataset-gen/generate.ts --check    # drift guard: dataset vs contract
NL_GEN_BOOTSTRAP=1 npx tsx nl-assist-finetune/dataset-gen/generate.ts   # also mine base-model phrasings
```

Each JSONL row is `{ delegateKind, intent, fragment, source, noisy, messages }`, where
`messages` is the real runtime prompt (`system`/`user` from the web UI's own prompt module)
plus the fragment as the `assistant` turn — so training matches the shipping prompt exactly
and the prompt contract lives in one place, not re-encoded in Python.

## Phase 3 — train on WSL2

From `nl-assist-finetune/` inside WSL2:

```bash
# 0. First run only: verify the assistant marker the completion-only collator keys on
#    (Phi model revisions differ) before spending GPU time.
train/.venv/bin/python train/train_lora.py --inspect     # (after ./run.sh deps creates the venv)

# 1. Whole chain: venv+deps -> dataset check/gen -> QLoRA -> merge -> GGUF -> ollama create -> PROVENANCE
./run.sh all

# or a single stage:  ./run.sh deps | dataset | train | merge | gguf | modelfile | provenance
```

`run.sh` produces the Ollama model `f8-delegate` and a `PROVENANCE.md` (base model + MIT
license, pinned tool versions, dataset hash — spec FT-7). Config, seed, and LoRA
hyperparameters live in [train/train-config.json](train/train-config.json); the same
dataset + config + seed reproduce an equivalent model (spec FT-1). If you hit OOM on 8GB,
drop `training.perDeviceBatchSize` to 1.

Point the NL assist at it by setting its `model` field to `f8-delegate` (spec FT-6) — no
Fallen-8 code changes; with nothing configured the stock base model is used as before.

## Evaluation (baseline / comparison runs)

```bash
npx tsx nl-assist-finetune/eval/baseline.ts                              # stock base model
NL_EVAL_MODEL=f8-delegate npx tsx nl-assist-finetune/eval/baseline.ts    # a fine-tuned model
npx tsx nl-assist-finetune/eval/baseline.ts --semantic                   # + FT-8 element-set gate
npx tsx nl-assist-finetune/eval/baseline.ts --rescore --semantic         # re-score recorded fragments, no model calls
```

One first-pass call per `eval/eval-set.json` row through the web UI's real prompt/format
modules, scored on compile (`POST /delegates/validate`), the row's regex semantic-proxy
checks, and performance. Resumable; raw results in `eval/results/` (gitignored). Summary
numbers go into the **run ledger** in [plan.md](../features/open/nl-assist-finetune/plan.md)
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

Shared by every script (defined in `shared/f8.ts`):
`NL_EVAL_MODEL` (default `phi4-mini`), `NL_EVAL_ENDPOINT` (default `http://localhost:11434`),
`NL_EVAL_F8` (default `http://localhost:5000`). Generator-only: `NL_GEN_BOOTSTRAP`,
`NL_GEN_OUT`. `run.sh`: `VENV`, `LLAMA_CPP`, `OLLAMA_MODEL`.
