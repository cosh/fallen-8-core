# Delegate Model Variants — Implementation Plan

Branch `feature/delegate-model-variants` (off `main`). Implements
[spec.md](./spec.md). Phases are ordered so the machine-independent work (pipeline
parametrization, UI, compose) can land and be tested **without a GPU**, while the two
GPU-bound steps (train `phi4-f8`, eval both) are isolated on the GPU/WSL box.

## Hardware reality check

- **This dev box has no NVIDIA GPU.** So, as with nl-assist-finetune, the *training* of
  `phi4-f8` and the head-to-head *eval* run happen on the RTX/WSL box, not here.
- `phi4-f8-mini` (== `f8-delegate`) already exists and runs on CPU here; it stays the local
  default and the thing `npm run env:up` pulls.
- **`phi4-f8` (14B) QLoRA does not fit the 8 GB GPU the mini used.** Budget ~16 GB+ VRAM
  (RTX 3090/4090 class). Inference of the 14B is GPU-only in practice.

## Decisions taken (veto on review)

The spec bakes in these defaults so implementation is unambiguous; flag any you want changed:

1. **Default variant stays `phi4-f8-mini`** (turnkey/CPU). `phi4-f8` is opt-in.
2. **Clean rename, NO `f8-delegate` alias** (operator's decision, 2026-07-20). The mini
   fine-tune is named only `phi4-f8-mini`; back-compat is not a goal. The builtin default moves
   automatically; a hand-typed custom `f8-delegate` is the only thing that needs re-pointing.
3. **Variant selection is config-driven via a `VARIANT` env** (→ `train-config.<variant>.json`),
   not new CLI flags or forked scripts.
4. **Compose pulls the big model only behind an env flag** (e.g. `F8_DELEGATE_VARIANTS` or
   `F8_PULL_PHI4F8=1`) — never by default.

## Phase 1 — Parametrize the pipeline by variant (no GPU)

Goal: one pipeline, two configs, clean rename to `phi4-f8-mini` (no `f8-delegate` alias).

- Rename `train/train-config.json` → `train/train-config.phi4-f8-mini.json`; set
  `output.ollamaModel` to `phi4-f8-mini` (keep `baseModel: microsoft/Phi-4-mini-instruct`,
  `responseTemplate: <|assistant|>`, and the current tuned knobs unchanged).
- `run.sh`: resolve the config from a `VARIANT` (default `phi4-f8-mini`) →
  `train/train-config.$VARIANT.json`; keep the existing `CONFIG` override as an escape hatch.
  All the per-model derived paths (`OLLAMA_MODEL`, GGUF names, `PROVENANCE`) already come from
  the config — confirm they stay variant-scoped so two variants never overwrite each other's
  artifacts (e.g. write `PROVENANCE.<variant>.md`, keep per-variant GGUF filenames).
- `modelfile()`: `ollama create <ollamaModel>` only — no `f8-delegate` alias (decision #2).
- Unit-guard: a tiny test asserts that `train/train-config.*.json` each parse, carry a
  `responseTemplate`, and name an MIT `baseModel`.

## Phase 2 — `phi4-f8` base config + retrain (GPU box)

- Add `train/train-config.phi4-f8.json`: `baseModel: microsoft/phi-4`,
  `baseModelLicense: MIT` (confirm the HF tag, DV-8), `output.ollamaModel: phi4-f8`,
  `ggufQuant: Q4_K_M`, `maxSeqLength` (2048 unless the inspect shows headroom), LoRA
  `targetModules` validated against Phi-4's module names, `quantize.load4bit: true` (nf4),
  and 14B-scaled `training` (`perDeviceBatchSize: 1`, larger `gradAccumSteps`,
  `gradientCheckpointing: true`, `optim: paged_adamw_8bit`).
- **Pre-flight (DV-2):** `train/.venv/bin/python train/train_lora.py --inspect` against
  `microsoft/phi-4` to read the real assistant-turn marker; set `responseTemplate` to match
  (expected `<|im_start|>assistant<|im_sep|>`). Confirm `train_lora.py` needs no base-specific
  branching beyond the marker + config knobs; if it does, keep the branch minimal and driven
  by the config, not hard-coded per model.
- Produce the model: `VARIANT=phi4-f8 ./run.sh all` on the GPU box. If OOM, drop batch size
  and/or `maxSeqLength` per the documented fallback.

## Phase 3 — Evaluate both; extend the run ledger (GPU box)

- Run `NL_EVAL_MODEL=phi4-f8 …/eval/baseline.ts --semantic` and re-confirm `phi4-f8-mini`
  and the two stock bases on the same `eval-set.json` + same box.
- Append ledger rows (in nl-assist-finetune/plan.md's ledger, the one home for these numbers)
  for `phi4-f8` and a refreshed `phi4-f8-mini`, plus a short **cross-comparison** line: quality
  delta (compile / semantic) vs the size/speed cost, so DV-4's "data-backed choice" is real.
- Enforce the gate: each fine-tune strictly beats its own stock base, no per-kind regression.

## Phase 4 — UI variant selection (no GPU)

- `config.ts`:
  - `BUILTIN_NL_BACKEND.model` / `DEFAULT_NL_CONFIG.model` → `phi4-f8-mini`. Builtin mode
    ignores any stored model name, so every builtin user moves to `phi4-f8-mini` automatically;
    no alias and no `f8-delegate`→`phi4-f8-mini` remap (decision #2 — breaking a hand-typed
    custom config is acceptable).
  - `NL_PRESETS`: add `Ollama (fine-tuned phi4-f8 — GPU)` and `Ollama (stock phi4)`; keep the
    stock `phi4-mini` and the `phi4-f8-mini` presets.
  - Optionally a small **builtin model chooser** (mini vs phi4-f8) so users switch variant
    without leaving builtin mode; if added, it is a stored field on the builtin config only.
- `NlAssistPanel.tsx`: surface the choice + the DV-5 hint (`phi4-f8` needs a pull + a capable
  host). No change to the browser → model → validate flow or the privacy notice.
- Tests: presets/default/migration assertions (spec §6); keep the 37 existing NL tests green.

## Phase 5 — Serving/env made variant-aware (no GPU)

- `docker-compose.yml`: introduce the opt-in flag (default = mini set only). Header comment
  documents `phi4-f8` as GPU-recommended and ~9 GB.
- `scripts/ollama-init.sh`: pull the default set `{phi4-mini, phi4-f8-mini}` (tag the pulled
  mini repo locally as `phi4-f8-mini`, no `f8-delegate` tag); when `phi4-f8` is requested, also
  pull `phi4` + the `phi4-f8` repo and tag it. Preserve
  graceful degradation (a failed *optional* pull logs and keeps the daemon up — the
  model-env fix's guarantee, regression-tested).
- `scripts/ensure-models.sh`: seed whichever variant set is requested into the volume.
- `scripts/env-info.js`: report which variants are present.

## Phase 6 — Docs

- New `features/open/delegate-model-variants/README.md` (living usage doc): how to pick a
  variant in the UI, how to train each (`VARIANT=…`), and the hardware bar.
- `nl-assist-finetune/README.md`: the two variants, the `VARIANT` selector, and the 14B
  hardware note (link the run ledger — do not duplicate numbers).
- Root `README.md`: NL-assist + GPU sections mention the optional `phi4-f8` and its GPU need.
- Move `features/open/delegate-model-variants/` → `features/done/…` when all phases land, the
  build is clean, and the UI/compose/finetune-guard tests pass (per root CLAUDE.md workflow).

## Out of scope (revisit triggers named in spec §3)

Model routing/auto-selection, multi-GPU/full fine-tune, shipping weights, eval in CI. Revisit
if a dedicated GPU runner, a third variant, or a second person training appears.
