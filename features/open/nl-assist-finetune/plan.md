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
| 2026-07-17 | phi4-mini (stock, Q4_K_M) | FR-10 steering + trailing-prose strip | 18 | 72% | 61% | 36.9 | 0.7 | baseline |

Per kind (baseline): VertexFilter 50%/33% (compile/semantic, n=6), EdgeFilter 67%/67%
(n=3), GraphElementFilter 100%/75% (n=4), EdgePropertyFilter 100%/100% (n=2),
VertexCost 100%/100% (n=1), EdgeCost 50%/50% (n=2). Perf numbers from the CPU-only dev
box with the apiApp validator sharing the machine.

### Baseline failure-mode analysis (dataset-design input for phase 2)

1. **Invented members** (3 rows — the dominant class): `v.GetAge()` twice, and
   `((double?)weight).ValueOrDefault(1)`. The prompt's member list alone doesn't stop
   hallucinated accessors for property-flavored intents → contrast pairs must cover
   "X older than / heavier than N" phrasings mapping to `TryGetProperty`.
2. **`GetAllProperties()` dictionary misuse** (2 rows): reaches for the raw
   `ImmutableDictionary` (not referenced in the compile context → CS0012) instead of
   `TryGetProperty`. Candidate quick win outside training: drop `GetAllProperties` from
   the prompt's member list (keep it in IntelliSense).
3. **Semantic drift that compiles** (2 rows): "id greater than 100" → `v.Id < 101`
   (inverted!), "older than 30" → `GetCreationDate() < DateTime.Now.AddYears(-30)`
   (age reinterpreted as element creation date). Invisible to compile-only scoring —
   the FT-8 case, now with concrete evidence.
4. **Eval-set strictness** (1 row, fixed): `p.Equals("knows")` is correct but the
   original `==`-only regex rejected it; check widened and the run rescored
   (`--rescore`). Checks must accept semantically equivalent forms.

The FR-10 steering itself held: no draft called `TryGetProperty` for "label"/"id" —
the failure the steering targets did not reoccur in 18 rows.

## Phase 2 — dataset generator (spec Stage 1)

Templated intents over the snippet library + type model, contrast pairs (Stage 1 d),
noisy intents (Stage 1 e); every row gated through `/delegates/validate`.

## Phase 3 — training pipeline (spec Stages 2–6; requires a GPU machine)

Python LoRA script (pinned deps, committed config, seed), merge → GGUF (Q4_K_M) →
`Modelfile` → `ollama create f8-delegate`, `PROVENANCE.md` generator.

## Phase 4 — full evaluation gate (spec Stage 7 + FT-8)

Replace the static-proxy checks with the seeded-sample-graph element-set comparison for
filter kinds; strict-win gate on compile AND semantic rates vs the phase-1 baseline.

## Phase 5 — continuous improvement loop

How retraining gets better over time, sized for a self-hosted single-operator project
(no MLOps machinery; revisit if multiple contributors/machines start training):

1. **Capture real pairs, locally and opt-in.** The parent spec forbids server-side
   prompt storage/telemetry, so the flywheel input is a user-triggered export from the
   editor: a "save as training example" affordance that downloads the (kind, intent,
   final validated fragment) pair — the *final* fragment, i.e. after refine turns and
   manual edits, which is exactly the label a trainer wants. Refine transcripts (failed
   draft + diagnostics + fix) are a second corpus for correction behaviour.
2. **Grow the eval set with every new failure mode** observed in the field (so far:
   built-in-member confusion, invented members like `GetAge()`, trailing prose).
   Eval rows are permanent and never enter training data — the set only grows, so
   ledger rows stay comparable per row-subset.
3. **Retrain on named triggers**, not on a schedule: (a) ≥50 new captured pairs,
   (b) a new failure mode added to the eval set, (c) the delegate contract changed
   (type model / member surface). One command on the GPU box (phase 3), producing
   `f8-delegate:v<N>` + `PROVENANCE.md`.
4. **Gate every candidate** through the phase-4 harness: strict win on compile AND
   semantic rates vs. the current ledger champion, no regression per kind; append the
   ledger row either way (failed candidates are documentation too).
5. **Roll out by name**: `ollama create f8-delegate:vN` on the serving box; the UI's
   model field (or a bumped builtin default) picks it up — no code change (spec FT-6).

Deliberately out of scope: eval in CI (a model inference run is far too heavy for the
test suite), automated capture, hosted training. Revisit trigger: a second regular
contributor or a dedicated GPU runner.
