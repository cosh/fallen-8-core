# Delegate Model Variants — Specification

> **Status:** Draft, spec only (no implementation yet). Follow the feature workflow in the
> repository root `CLAUDE.md`.
>
> **Relationship to existing features.** This extends
> [nl-assist-finetune](../../done/nl-assist-finetune/spec.md) (which *produces* the model) and is
> consumed through the [nl-assist](../../done/web-ui/nl-assist/spec.md) /
> [nl-assist-ux](../../done/nl-assist-ux/spec.md) runtime contract (which governs how the model
> is *used*). Precedence is unchanged: those specs win on production and usage respectively;
> this document owns one narrow question — **which fine-tune variant exists, what it is named,
> and how the user chooses it.** It adds no new runtime behaviour to the browser → model →
> validate flow.

## 1. Overview

Today there is exactly one fine-tune: `f8-delegate`, a LoRA of **Phi-4-mini** (3.8B, MIT). It
is turnkey — ~2.5 GB at `Q4_K_M`, acceptable on CPU, and pulled by compose on first start. Its
accuracy ceiling is bounded by a 3.8B base.

**Phi-4** (14B, MIT) is a stronger coder. A LoRA of it, trained on the *same* grounded dataset
and gated by the *same* eval, should raise first-pass compile/semantic rates further — at the
cost of ~9 GB of weights and GPU-bound inference (a 14B on CPU is impractically slow).

This feature offers **both fine-tunes as named, user-selectable delegate models**, so an
operator picks the point on the size / quality / hardware curve that fits their machine, and
**retrains** the delegate on the full Phi-4 base to produce the larger variant:

- **`phi4-f8-mini`** — LoRA of `phi4-mini`. This is today's `f8-delegate`. The zero-config
  default; runs on CPU.
- **`phi4-f8`** — LoRA of `phi4` (14B). Opt-in; a capable NVIDIA GPU is strongly recommended.

The names read as "the Phi-4 family, F8-tuned, [mini] size" — making both the base and the
tuning explicit and disambiguating the fine-tunes from the stock `phi4` / `phi4-mini` models,
which stay selectable too.

## 2. Model taxonomy and naming

| Ollama name | Base | Params | ~Size (Q4_K_M) | Inference | Role |
|---|---|---|---|---|---|
| `phi4-f8-mini` | `phi4-mini` | 3.8B | ~2.5 GB | CPU-OK / GPU | **Default**, turnkey (== today's `f8-delegate`) |
| `phi4-f8` | `phi4` | 14B | ~9 GB | GPU (CPU very slow) | Opt-in, higher accuracy ceiling |
| `phi4-mini` | — (stock) | 3.8B | ~2.5 GB | CPU-OK / GPU | Selectable base / fallback |
| `phi4` | — (stock) | 14B | ~9 GB | GPU | Selectable base |

**Back-compat.** `f8-delegate` remains a working name — the pipeline tags the mini fine-tune as
both `phi4-f8-mini` and `f8-delegate`, and the UI keeps resolving existing `f8-delegate`
configs. No stored config, published `…/f8-delegate` repo, or bookmarked model name breaks.

## 3. Goals and non-goals

### Goals
- **Two variants, one pipeline.** Produce either fine-tune from the *same* scripts and dataset,
  selected by config — no forked logic (extends FT-1's reproducibility to a variant axis).
- **User's choice, no code change.** Selecting a variant is a UI/config action, not a rebuild
  (extends FT-6).
- **A data-backed choice.** Both variants run through the existing eval gate; the run ledger
  records each *and* a cross-comparison on the same held-out set + machine, so the quality
  delta is weighed against the size/speed cost with real numbers, not vibes.
- **Turnkey default preserved.** A plain `npm run env:up` still yields the CPU-friendly mini;
  the 14B variant is never force-pulled.
- **MIT-only preserved.** Phi-4 is MIT; the blessed pipeline still admits only MIT bases
  (parent FR-26.1), and provenance is recorded per variant.

### Non-goals
- **Automatic model routing / per-intent selection / ensembling.** One chosen model per config.
- **Multi-GPU, distributed, or full fine-tuning.** QLoRA on a single device only (inherits
  nl-assist-finetune non-goals); the 14B just raises the single-device bar.
- **Shipping weights or a second base image.** The repo still ships the pipeline, not models.
- **A/B infrastructure or server-side model telemetry.** Out of scope by the parent privacy
  posture.

**Named revisit triggers** (per the project's right-sizing discipline): a dedicated multi-GPU
runner; a third+ variant that makes a registry/selector worthwhile; more than one person
training models. Until then, two variants + a config selector is the whole machine.

## 4. Functional requirements

- **DV-1 Config-driven variants.** The pipeline selects a variant by name (e.g.
  `VARIANT=phi4-f8 ./run.sh all`), reading a per-variant config that carries `baseModel`,
  `output.ollamaModel`, `responseTemplate`, `maxSeqLength`, and the LoRA/training knobs.
  `run.sh` stays a single file; no stage logic is duplicated. Each variant is reproducible
  (FT-1): same dataset + variant config + seed → an equivalent model within eval tolerance.
- **DV-2 Correct per-base prompt template.** `phi4` and `phi4-mini` open an assistant turn
  differently, so the completion-only collator's `responseTemplate` MUST match the *actual*
  base (Phi-4 has used `<|im_start|>assistant<|im_sep|>`; Phi-4-mini `<|assistant|>`). A wrong
  marker silently trains on the whole prompt. The marker is verified per base with
  `train_lora.py --inspect` and stored in that variant's config.
- **DV-3 Hardware honesty.** The 14B QLoRA VRAM floor is documented and higher than the 8 GB
  the mini used (target ~16 GB+, e.g. RTX 3090/4090). The `phi4-f8` config ships 14B-scaled
  training defaults (smaller `perDeviceBatchSize`, larger `gradAccumSteps`, gradient
  checkpointing on) and a documented OOM fallback. The docs never claim CPU training — or
  practical CPU inference — of the 14B; the mini remains the no-GPU path.
- **DV-4 Both evaluated; the choice is data-backed.** The eval harness runs each variant
  unchanged; the run ledger gains rows for `phi4-f8` and `phi4-f8-mini` plus a cross-comparison
  (same `eval-set.json`, same box). **Gate:** each fine-tune must strictly beat its *own* stock
  base on compile AND semantic rates (FT-4/FT-8), with no per-kind regression; a variant that
  regresses vs its base is a failed run and is not published (the ledger row is kept either way).
- **DV-5 User-selectable in the UI.** The delegate editor exposes the variants — `phi4-f8-mini`
  (default), `phi4-f8`, and the stock `phi4`/`phi4-mini` — as presets and/or a builtin model
  selector, without altering the browser → model → validate flow, the MIT/local posture, key
  isolation, or the privacy notice (nl-assist FR-26.*). The zero-config default stays
  `phi4-f8-mini`. A one-line hint states that `phi4-f8` must be pulled and wants a capable host.
- **DV-6 Serving is opt-in for the big model.** Compose pulls the default set
  (`phi4-mini` + `phi4-f8-mini`, tagged `f8-delegate`) on first start; `phi4-f8` (and its `phi4`
  base) are pulled only when explicitly requested (an env flag), because ~9 GB + GPU-bound is a
  bad default. `ollama-init.sh` / `ensure-models.sh` take the variant set as input; requesting
  `phi4-f8` on a CPU-only host warns it will be slow. The graceful-degradation guarantee from
  the model-env fix holds: a failed *optional* pull never crashes the Ollama endpoint.
- **DV-7 Distribution per variant.** `run.sh publish` pushes the selected variant to its own
  registry repo (`…/phi4-f8`, `…/phi4-f8-mini`), staying back-compatible with the existing
  `…/f8-delegate`. `PROVENANCE` is written per variant (base + license + tool pins + dataset
  hash), so each artifact's licence position travels with it (FT-7).
- **DV-8 MIT-only.** Only MIT-licensed bases enter the blessed pipeline (parent FR-26.1); the
  Phi-4 pin's HF licence tag is confirmed at pin time and recorded in that variant's provenance.
- **DV-9 No engine change.** `fallen-8-core` and the running apiApp are untouched. Changes are
  confined to `nl-assist-finetune/`, the web UI, compose/scripts, and docs.

## 5. Prerequisites and gaps

| # | Item | Disposition |
|---|---|---|
| DV-G1 | 14B QLoRA needs ≫8 GB VRAM — the mini's 8 GB box cannot train `phi4-f8`. | Documented floor (~16 GB+) + 14B-scaled defaults + OOM fallback; the mini stays the low-VRAM/no-GPU path (DV-3). |
| DV-G2 | Phi-4's assistant-turn marker differs from Phi-4-mini's. | `--inspect` verification is a required pre-flight in the plan; each config carries its own `responseTemplate` (DV-2). |
| DV-G3 | `phi4-f8` is ~9 GB to pull and store. | Opt-in only (DV-6); pre-seed via `ensure-models.sh`; disk cost documented. |
| DV-G4 | 14B inference on CPU is impractically slow. | UI hint + docs steer `phi4-f8` to GPU hosts; the default stays the mini (DV-4/DV-5). |
| DV-G5 | Existing users/configs reference `f8-delegate`. | Kept as a working alias for `phi4-f8-mini`; UI migration preserves stored configs (§2, DV-5). |
| DV-G6 | Two variants could drift in dataset or prompt. | One dataset and one prompt module feed both (single home); only base + hyperparameters differ per variant (DV-1). |

## 6. Testing requirements

- **Web UI (unit).** Presets include `phi4-f8` and stock `phi4`; the builtin default resolves
  to `phi4-f8-mini`; a stored `f8-delegate`/custom config still works after migration; the
  builtin resolver returns the selected builtin model. Existing NL-assist tests stay green.
- **Finetune (unit / gated).** Variant selection resolves the correct config; the dataset
  drift check still fires; every variant config carries a `responseTemplate` and an MIT base.
  Training and eval remain **gated, GPU-box, manual** — a model inference run is far too heavy
  for CI (inherited from nl-assist-finetune).
- **Compose / scripts.** The default variant set is `{phi4-mini, phi4-f8-mini}`; requesting
  `phi4-f8` adds `{phi4, phi4-f8}`; a missing *optional* model does not crash `ollama-init`
  (regression guard for the model-env graceful-degradation fix).
- **No product regression.** No code is added to `fallen-8-core` or the apiApp; the OpenAPI
  snapshot is unaffected (no controller/route change).

## 7. Deliverables and workflow

1. This spec; then [plan.md](./plan.md).
2. Implementation on `feature/delegate-model-variants`, phased (see the plan), merged to `main`
   after review. Commit messages honest and concise, no AI-assistant references. A GitHub
   issue/PR is opened at the operator's discretion (not required by this repo's flow).

## 8. Reference files

- [features/done/nl-assist-finetune/spec.md](../../done/nl-assist-finetune/spec.md) +
  [plan.md](../../done/nl-assist-finetune/plan.md) — the pipeline, eval gate, and run ledger
  this feature extends to a second variant.
- `nl-assist-finetune/train/train-config.json`, `run.sh`, `train/Modelfile.template`,
  `train/train_lora.py` — the config-driven pieces to parametrize per variant.
- `fallen-8-web-ui/src/delegate/nl/config.ts` — presets, `BUILTIN_NL_BACKEND`, and the
  zero-config default the variant selector extends.
- `docker-compose.yml`, `scripts/ollama-init.sh`, `scripts/ensure-models.sh` — the
  pull/serve path made variant-aware (opt-in for `phi4-f8`).
- Ollama library models: `phi4` (14B) and `phi4-mini` (3.8B) — the two MIT bases.
