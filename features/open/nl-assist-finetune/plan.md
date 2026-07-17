# NL-Assist Fine-Tuning — Implementation Plan

Branch `feature/nl-assist-finetune` (based on `feature/nl-assist-ux` — the eval harness
imports that branch's prompt module so the baseline measures the *shipping* prompt,
including the FR-10 built-in-member steering).

## Hardware reality check (recorded 2026-07-17)

The dev machine has **no CUDA GPU** and Ollama runs `phi4-mini` on CPU at ~1.3 s/token
(measured: 61 tokens in 81 s). Consequences:

- **Training (phases 3+) does not run here.** LoRA on 3.8B needs a GPU (hours) or CPU
  (days); the scripts must be portable so the pipeline runs unchanged on a GPU box.
- **Baseline evaluation is feasible but slow** (~90 s per drafted fragment) — eval sets
  are sized accordingly and the harness is resumable per row.

## Phase 1 — baseline analysis (this session)

- `nl-assist-finetune/eval/eval-set.json` — hand-authored held-out intents across all six
  kinds, each with a reference fragment and static expectations (`mustMatch` /
  `mustNotMatch` regexes). Deliberately includes built-in-member phrasings (label/id) and
  typo'd intents — the two failure classes from the §1 field example.
- `nl-assist-finetune/eval/baseline.ts` (run with `npx tsx`) — for each row: build the
  prompt with the web UI's real `buildGenerationPrompt`, one first-pass call to the local
  Ollama (temperature 0.1, no refine loop — the metric is *first-pass* quality), format,
  then score: compile via `POST /delegates/validate` plus the static expectation checks
  (a cheap semantic proxy until the FT-8 graph executor exists in phase 4). Writes a
  JSON report + per-kind console table; results are gitignored artifacts.
- Run it against stock `phi4-mini` and record the numbers here.

### Run ledger

Every evaluation run (baseline, fine-tuned candidates, prompt changes) appends a row
here, so quality and performance movement — improving or regressing — is visible
run-over-run. Quality = compile rate and semantic-proxy rate on the held-out set;
performance = mean seconds per draft and tokens/second (hardware-bound: only compare
runs from the same machine; this ledger's rows so far are the CPU-only dev box).

| date | model | prompt | n | compile | semantic proxy | s/draft | tok/s | vs. previous |
|---|---|---|---|---|---|---|---|---|
| 2026-07-17 | phi4-mini (stock, Q4_K_M) | FR-10 steering | _running_ | — | — | — | — | baseline |

## Phase 2 — dataset generator (spec Stage 1)

Templated intents over the snippet library + type model, contrast pairs (Stage 1 d),
noisy intents (Stage 1 e); every row gated through `/delegates/validate`.

## Phase 3 — training pipeline (spec Stages 2–6; requires a GPU machine)

Python LoRA script (pinned deps, committed config, seed), merge → GGUF (Q4_K_M) →
`Modelfile` → `ollama create f8-delegate`, `PROVENANCE.md` generator.

## Phase 4 — full evaluation gate (spec Stage 7 + FT-8)

Replace the static-proxy checks with the seeded-sample-graph element-set comparison for
filter kinds; strict-win gate on compile AND semantic rates vs the phase-1 baseline.
