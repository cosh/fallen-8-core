#!/usr/bin/env bash
# End-to-end fine-tune pipeline (nl-assist-finetune plan phase 3, spec Stages 1-6, FT-1).
# Runs on WSL2 (Ubuntu) with an 8GB+ NVIDIA GPU. Each stage is a function and can be run
# alone:  ./run.sh <stage>   where stage is one of
#   deps | dataset | train | merge | gguf | modelfile | provenance | all   (default: all)
# Plus an opt-in distribution stage, NOT part of `all` (feature nl-assist-feedback-loop, FL-4):
#   publish   push the produced model to a registry so other instances can pull it
#
# Prerequisites the pipeline does NOT install for you:
#   - Python 3.10+ and a CUDA 12.x driver visible in WSL2 (`nvidia-smi` works)
#   - Node.js + npx           (for the phase-2 dataset generator)
#   - ollama                  (for `ollama create`; Windows or WSL install both work)
#   - a reachable Fallen-8 apiApp with EnableDynamicCodeExecution=true, for dataset
#     validation - set NL_EVAL_F8 if it isn't http://localhost:5000
#
# Env overrides: VENV, LLAMA_CPP (existing llama.cpp checkout), NL_EVAL_F8, OLLAMA_MODEL,
# TORCH_VERSION, TORCH_INDEX, PUBLISH_REPO (the publish target, e.g. <namespace>/f8-delegate).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"
VENV="${VENV:-$HERE/train/.venv}"
DATASET="$HERE/dataset/train.jsonl"
ADAPTER="$HERE/adapter"
MERGED="$HERE/merged"
CONFIG="$HERE/train/train-config.json"
LLAMA_CPP="${LLAMA_CPP:-$HERE/llama.cpp}"
OLLAMA_MODEL="${OLLAMA_MODEL:-$(python3 -c "import json;print(json.load(open('$CONFIG'))['output']['ollamaModel'])")}"
GGUF_QUANT="$(python3 -c "import json;print(json.load(open('$CONFIG'))['output']['ggufQuant'])")"
GGUF_F16="$HERE/${OLLAMA_MODEL}.f16.gguf"
GGUF_QUANTIZED="$HERE/${OLLAMA_MODEL}.${GGUF_QUANT}.gguf"

log() { printf '\n\033[1;36m== %s ==\033[0m\n' "$*"; }
py() { "$VENV/bin/python" "$@"; }

deps() {
  log "deps: python venv + pinned toolchain"
  # Only validate/choose an interpreter when the venv must be BUILT; once it exists, pip runs
  # inside it and the system python3 version is irrelevant (so a later `./run.sh all` needn't
  # re-pass PYTHON). The toolchain needs Python >= 3.10; the distro default is often too old
  # (Ubuntu 20.04 ships 3.8, EOL) and yields cryptic per-package "No matching distribution"
  # errors, so fail early with guidance. Override the interpreter with PYTHON.
  if [ ! -d "$VENV" ]; then
    local py="${PYTHON:-python3}"
    if ! command -v "$py" >/dev/null 2>&1 || \
       ! "$py" -c 'import sys; sys.exit(0 if sys.version_info[:2] >= (3, 10) else 1)' 2>/dev/null; then
      echo "ERROR: '$py' is $("$py" --version 2>&1 | awk '{print $NF}') - this pipeline needs Python >= 3.10." >&2
      echo "Install one and re-run with it, e.g.:  PYTHON=python3.12 ./run.sh deps" >&2
      echo "  Ubuntu: sudo add-apt-repository ppa:deadsnakes/ppa && sudo apt install python3.12 python3.12-venv" >&2
      return 1
    fi
    "$py" -m venv "$VENV"
  fi
  "$VENV/bin/pip" install --upgrade pip >/dev/null
  # torch: the validated version (2.6.0) from a CUDA wheel index. A newer driver (CUDA 13.x)
  # runs these cu124 wheels fine. Override TORCH_VERSION to move torch (then bump the training
  # libs as a group - see requirements.txt) or TORCH_INDEX for a different CUDA build, e.g.
  # TORCH_VERSION=2.7.0 TORCH_INDEX=https://download.pytorch.org/whl/cu128
  "$VENV/bin/pip" install "torch==${TORCH_VERSION:-2.6.0}" --index-url "${TORCH_INDEX:-https://download.pytorch.org/whl/cu124}"
  "$VENV/bin/pip" install -r "$HERE/train/requirements.txt"
}

dataset() {
  # Reuse an existing dataset (it is deterministic, so a copy from the authoring box is
  # identical) - this lets a training-only box run `all` without Node or the apiApp. To
  # rebuild, delete it and re-run, or run the generator directly (README phase 2). Only
  # generation needs Node + a running apiApp at ${NL_EVAL_F8:-http://localhost:5000}.
  if [ -f "$DATASET" ]; then
    log "dataset: using existing $(basename "$DATASET") ($(wc -l < "$DATASET") rows)"
    return 0
  fi
  log "dataset (phase 2): generating (needs Node + apiApp)"
  (cd "$REPO" && npx tsx nl-assist-finetune/dataset-gen/generate.ts)
}

train()   { log "train (Stage 2): QLoRA -> $ADAPTER"; py "$HERE/train/train_lora.py" --dataset "$DATASET" --out "$ADAPTER"; }
merge()   { log "merge (Stage 3): adapter -> merged fp16"; py "$HERE/train/merge.py" --adapter "$ADAPTER" --out "$MERGED"; }

gguf() {
  log "gguf (Stage 4): convert + quantize to $GGUF_QUANT"
  if [ ! -d "$LLAMA_CPP" ]; then
    git clone --depth 1 https://github.com/ggml-org/llama.cpp "$LLAMA_CPP"
  fi
  "$VENV/bin/pip" install -r "$LLAMA_CPP/requirements.txt" >/dev/null
  if [ ! -x "$LLAMA_CPP/build/bin/llama-quantize" ]; then
    # CPU build on purpose: we only convert (Python) and quantize (llama-quantize, CPU) - no
    # GPU inference here - so this needs cmake + a C/C++ compiler, NOT the CUDA toolkit/nvcc.
    cmake -S "$LLAMA_CPP" -B "$LLAMA_CPP/build" -DGGML_CUDA=OFF
    cmake --build "$LLAMA_CPP/build" --config Release -j --target llama-quantize
  fi
  py "$LLAMA_CPP/convert_hf_to_gguf.py" "$MERGED" --outfile "$GGUF_F16" --outtype f16
  "$LLAMA_CPP/build/bin/llama-quantize" "$GGUF_F16" "$GGUF_QUANTIZED" "$GGUF_QUANT"
}

modelfile() {
  log "modelfile (Stage 5-6): write Modelfile + ollama create $OLLAMA_MODEL"
  sed "s#__GGUF__#$GGUF_QUANTIZED#" "$HERE/train/Modelfile.template" > "$HERE/Modelfile"
  ollama create "$OLLAMA_MODEL" -f "$HERE/Modelfile"
  echo "registered '$OLLAMA_MODEL' - point the NL assist's model field at it (spec FT-6)."
}

provenance() {
  log "provenance (FT-7): PROVENANCE.md"
  local base license versions srchash rows
  base="$(python3 -c "import json;print(json.load(open('$CONFIG'))['baseModel'])")"
  license="$(python3 -c "import json;print(json.load(open('$CONFIG'))['baseModelLicense'])")"
  versions="$("$VENV/bin/pip" freeze | grep -iE '^(torch|transformers|peft|trl|datasets|accelerate|bitsandbytes)==' || true)"
  srchash="$(python3 -c "import json;print(json.load(open('$HERE/dataset/dataset.meta.json'))['sourceHash'])" 2>/dev/null || echo '(dataset not generated)')"
  rows="$(python3 -c "import json;print(json.load(open('$HERE/dataset/dataset.meta.json'))['generatedRows'])" 2>/dev/null || echo '?')"
  cat > "$HERE/PROVENANCE.md" <<EOF
# f8-delegate provenance

Generated by run.sh. The produced adapter/GGUF is the operator's artifact on the operator's
machine; Fallen-8 ships neither weights nor a trainer (spec FT-5).

## Base model
- \`$base\` - license: **$license** (MIT-only pipeline, spec FT-7 / parent FR-26.1).

## Dataset
- Grounded in the repo's delegate contract; sourceHash \`$srchash\`, $rows validated rows.
- Every row compiled via POST /delegates/validate at generation time (spec FT-2).

## Tooling (pinned, this run)
\`\`\`
$versions
\`\`\`
- llama.cpp (GGUF convert + quantize): $LLAMA_CPP
- Quantization: $GGUF_QUANT

## Reproduce
\`\`\`
./run.sh all
\`\`\`
Same dataset + train-config.json + seed => equivalent model (spec FT-1).
EOF
  echo "wrote $HERE/PROVENANCE.md"
}

all() { deps; dataset; train; merge; gguf; modelfile; provenance; log "done. Next: evaluate (phase 4) with NL_EVAL_MODEL=$OLLAMA_MODEL"; }

# FL-4 (feature nl-assist-feedback-loop): the operator's OPT-IN distribution channel - how a
# retrained model reaches instances other than this box. F8 core still ships no weights (spec
# FT-5); this pushes YOUR artifact to YOUR registry namespace. Deliberately not in `all`.
publish() {
  log "publish (FL-4): push '$OLLAMA_MODEL' to a registry"
  if [ -z "${PUBLISH_REPO:-}" ]; then
    echo "Set PUBLISH_REPO to your registry target and re-run, e.g.:" >&2
    echo "  PUBLISH_REPO=<your-namespace>/f8-delegate ./run.sh publish" >&2
    echo "First create an ollama.com account and 'ollama login'; the namespace must match it." >&2
    return 1
  fi
  ollama cp "$OLLAMA_MODEL" "$PUBLISH_REPO"
  ollama push "$PUBLISH_REPO"
  echo "pushed '$PUBLISH_REPO'. On another instance: 'ollama pull $PUBLISH_REPO', then set the"
  echo "NL-assist model to it (or 'ollama cp $PUBLISH_REPO $OLLAMA_MODEL' to adopt it as the"
  echo "default f8-delegate). Ship $HERE/PROVENANCE.md alongside so the licence position travels (FT-7)."
}

"${1:-all}"
