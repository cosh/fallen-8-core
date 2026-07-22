# Delegate model variants (`phi4-f8-mini` / `phi4-f8`)

Two user-selectable NL-assist fine-tunes, produced by the **same** pipeline
([nl-assist-finetune](../nl-assist-finetune/)) from the **same** grounded dataset —
they differ only in the base model and the hardware they need. See [spec.md](./spec.md) and
[plan.md](./plan.md) for the contract and the phased plan.

| Model | Base | ~Size (Q4_K_M) | Runs on | Role |
|---|---|---|---|---|
| **`phi4-f8-mini`** | Phi-4-mini (3.8B) | ~2.5 GB | CPU-OK / GPU | **Default** — turnkey, what `env:up` pulls |
| **`phi4-f8`** | Phi-4 (14B) | ~9 GB | GPU (CPU impractical) | Opt-in — higher first-pass accuracy ceiling |

The stock `phi4-mini` / `phi4` bases stay selectable too. There is **no `f8-delegate` alias** —
the mini fine-tune is named only `phi4-f8-mini` (clean rename; back-compat is not a goal here).

## Choose a variant in Studio

The delegate editor's **built-in** backend defaults to `phi4-f8-mini` (zero config). To use the
larger fine-tune, switch the backend to **custom** and pick the
**"Ollama (fine-tuned phi4-f8 — GPU)"** preset (or type `phi4-f8` into the model field). The
stock bases have presets too. `phi4-f8` must be present on the Ollama host and wants a GPU.

## Train each variant (Linux + NVIDIA GPU)

Training is the one step that is **not** PowerShell-native: `run.sh` and the QLoRA/GGUF
toolchain need a Linux environment with an NVIDIA GPU — WSL2 (Ubuntu) on a Windows GPU box, or
a native Linux box — so the commands here are bash. One pipeline, selected by `VARIANT`
(default `phi4-f8-mini`); from `nl-assist-finetune/` inside that Linux/WSL2 shell:

```bash
./run.sh all                        # phi4-f8-mini (Phi-4-mini base; 8GB+ GPU)
VARIANT=phi4-f8 ./run.sh all        # phi4-f8      (full Phi-4 14B base; ~16GB+ GPU)
```

**Before spending GPU hours on `phi4-f8`,** verify the chat-template marker and the LoRA module
names for the 14B base (they differ from the mini):

```bash
train/.venv/bin/python train/train_lora.py --inspect --config train/train-config.phi4-f8.json
```

Set `responseTemplate` (and `lora.targetModules` if Phi-4 fuses `qkv_proj`/`gate_up_proj`) in
[../../../nl-assist-finetune/train/train-config.phi4-f8.json](../../../nl-assist-finetune/train/train-config.phi4-f8.json)
to match what `--inspect` prints. Each variant writes its own `PROVENANCE.<model>.md` and GGUF,
so the two never clobber each other. Evaluate + record both in the run ledger
([nl-assist-finetune/plan.md](../nl-assist-finetune/plan.md)) — the gate is a strict win
over each variant's own stock base on compile AND semantic rates.

## Serve `phi4-f8` in Docker (opt-in)

Compose pulls the mini set by default. To also serve the 14B fine-tune (this runs on the
Docker host — Windows PowerShell or bash, no GPU needed to *pull*, only to run it well):

```bash
F8_PULL_PHI4F8=1 npm run env:up             # ~9GB extra; GPU strongly recommended
F8_PULL_PHI4F8=1 scripts/ensure-models.sh   # or pre-seed the volume offline
```
```powershell
$env:F8_PULL_PHI4F8 = "1"; npm run env:up                 # ~9GB extra; GPU strongly recommended
$env:F8_PULL_PHI4F8 = "1"; bash scripts/ensure-models.sh  # or pre-seed the volume offline
```

Point `F8_DELEGATE_REPO` / `F8_PHI4F8_REPO` at your own published fine-tunes if you retrain and
publish one (`PUBLISH_REPO=<ns>/phi4-f8 VARIANT=phi4-f8 ./run.sh publish`, in the Linux/WSL2 shell).

## Status

**Done — landed 2026-07-22.** All phases complete: the config-driven pipeline, UI variant
selection, and variant-aware compose are implemented, and **both fine-tunes are trained and
published** — `stoic_hellman_728/phi4-f8-mini` and `stoic_hellman_728/phi4-f8`. Head-to-head eval
([run ledger](../nl-assist-finetune/plan.md)): both score **100%** compile and **100%** FT-8
element-set semantic; the 14B edges the mini only on the semantic-proxy (100% vs 89%) at a
GPU-only / ~9 GB / far-slower-on-CPU cost — so `phi4-f8-mini` stays the default and `phi4-f8` is
the opt-in bump. `phi4-f8` was trained + published unattended on an Azure A10 (NVadsA10v5) VM
([infra runner](../../../nl-assist-finetune/infra/)).
