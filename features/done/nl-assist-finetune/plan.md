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
runs from the same machine; the host is noted per row).

| date | model | prompt | n | compile | semantic proxy | s/draft | tok/s | vs. previous |
|---|---|---|---|---|---|---|---|---|
| 2026-07-17 | phi4-mini (stock, Q4_K_M) | FR-10 steering + trailing-prose strip | 18 | 72% | 61% | 36.9 | 0.7 | baseline |
| 2026-07-19 | phi4-mini (stock, Q4_K_M) | + FT-8 element-set gate (phase 4) | 18 | 72% | 61% | 36.9 | 0.7 | element-set semantic 45% over 11 applicable rows (< proxy < compile: the metric sees compiling-but-wrong drafts) — phase-4 champion base to beat |
| 2026-07-19 | f8-delegate v1 (LoRA, 3 epochs) | shipping prompt | 18 | 83% | 83% | — | — | RTX 3080 (perf not comparable to CPU rows). First fine-tune: beats base on compile+proxy but overfit — GraphElementFilter compile regressed 100%→75% and 3 property-threshold rows failed first-pass |
| 2026-07-20 | f8-delegate v2 (LoRA, 2 epochs) | shipping prompt | 18 | **100%** | **94%** | 0.5 | 172 | RTX 3080. FT-8 element-set semantic **100%** (11 applicable). 359-row dataset (natural comparatives + multi-condition label+prop+id + edge-weight) and 2 epochs fixed all 3 v1 compile misses and the GEF regression. Sole proxy miss: `epf-knows` drafted `StartsWith("know")` instead of `== "knows"` |
| 2026-07-21 | phi4-mini (stock, Q4_K_M) | shipping prompt | 18 | 72% | 56% | 0.6 | 166 | Ryzen box (new eval host; perf not comparable to prior rows). `--rescore --semantic`: FT-8 element-set semantic **45%** over 11 applicable rows — re-confirms the stock baseline on this machine |
| 2026-07-21 | phi4-f8-mini (== f8-delegate v2, now published `stoic_hellman_728/phi4-f8-mini`) | shipping prompt | 18 | **100%** | **89%** | 0.5 | 169 | Ryzen box, SAME host/eval as the stock row above — apples-to-apples: compile 72%→**100%**, proxy 56%→**89%** (VertexFilter 33%→100%, EdgeFilter 67%→100%, EdgeCost 50%→100%). Remaining proxy misses: `gef-field-example` (draft omits `.Id` + `TryGetProperty "age"`) and `epf-knows` (`StartsWith`-style vs `== "knows"`). `--semantic` not run this pass |
| 2026-07-22 | phi4-f8 (14B LoRA, published `stoic_hellman_728/phi4-f8`) | shipping prompt | 18 | **100%** | **100%** | 59 | 0.4 | Ryzen box CPU (14B Q4; 0.4 tok/s — perf NOT comparable to the GPU rows). `--semantic`: FT-8 element-set **100%** (11 applicable). Same host + eval-set as the `phi4-f8-mini` row above, so directly comparable: compile 100%=100%, FT-8 100%=100%, proxy **89%→100%** — the 14B fixes the mini's two proxy misses (`gef-field-example`, `epf-knows`). So the 14B's only quality edge is those 2 proxy rows, bought with GPU-only inference + ~9 GB + ~150× slower on CPU → **DV-4 choice: `phi4-f8-mini` stays the CPU-friendly default; `phi4-f8` is an opt-in marginal bump for GPU users.** Stock phi4 (14B) not separately eval'd: the fine-tune sits at the 100/100/100 ceiling so it beats-or-ties any base, and the stock→FT delta is already established by the mini rows above. Trained + published unattended on an Azure A10 (NVadsA10v5) VM |
| 2026-07-30 | phi4-f8-mini v3 (retrain: RETRAIN-LOG drain — typed-filter retarget, edge-type rows, fulltext member, 14 captured-feedback rows; published `stoic_hellman_728/phi4-f8-mini`) | shipping prompt (+ keep-every-clause variant/refine rule) | 23 | 87% | 78% | 67.5 | 0.4 | a laptop CPU (Core Ultra 9 285H, no GPU — perf NOT comparable to prior rows). Eval set grew 18→23 (3 element-fulltext rows + 2 edge-type rows), so rates aren't directly comparable either; on the shared 18 rows: compile 100%→94% (`gef-label-or` — expected: GraphElementFilter now deliberately trains at zero rows), proxy 89%→89% (`gef-field-example` FIXED vs v2, `epf-knows` still misses). `--semantic`: FT-8 element-set **88%** (15/17 applicable — typed subgraph slots made `vf-outdegree`/`ef-target-person` newly evaluable). Fulltext member learned: `vf-any-prop-contains` + `vf-value-not-name` PASS; `vf-compound-strings` proxy-misses by drafting `AnyPropertyValueMatches` for a per-field intent (semantically equal on the fixture). **The batch-1 edge-type target did NOT take: `ef-edge-type` still drafts the hallucinated `EdgeType`, `ec-edge-type-switch` reaches for `TryGetProperty`** — 8 rows among 330 didn't overcome the prior; see the RETRAIN-LOG batch-1 entry for the next iteration (type-model doc wording + row boost). Plugin eval not runnable on this host (whole-type drafts exceed any sane per-call ceiling at 0.4 tok/s; `NL_EVAL_TIMEOUT_MS` now exists) — pending on the Ryzen box, as is the 14B eval |
| 2026-07-31 | phi4-f8-mini v3 (same model, re-eval'd) | shipping prompt | 23 | 87% | 83% | 2.4 | 159.6 | Ryzen box (GPU) - same host as the 07-21/22 rows, apples-to-apples. FT-8 element-set **82%** (14/17). Shared-18 vs v2: compile 100%→94% (`gef-label-or` drafts a malformed switch - the accepted cost of GraphElementFilter training at zero rows), proxy 89%→**94%** (v3 fixes BOTH v2 misses: `gef-field-example` and `epf-knows`, whose `p.Equals("knows")` the widened check accepts). New rows: fulltext member learned (`vf-any-prop-contains`, `vf-value-not-name` pass); `vf-compound-strings` fails UNSTABLY across hosts (laptop row above: wrong-idiom-yet-semantically-equal; here: a non-compiling `GetAllProperties().TryGetValue` draft) - backend nondeterminism, same verdict. Edge-type 0/2: `e.EdgeType` hallucination persists, `ec-edge-type-switch` mixes `TryGetProperty` into the switch. Plugins **0%** (first run training whole-type rows): the mini has not learned the shape - dominant mode is `public Type PluginCategory = typeof(...)` (field `=` instead of `=>`, breaking the interface, 4/6 drafts), plus dropped usings/invented APIs |
| 2026-08-23 | phi4-mini (stock, Q4_K_M) | shipping prompt | 23 | 91% | 78% | 0.2 | 141.4 | **Azure A10 (NVadsA10v5), first cloud eval run** via `infra/eval-deploy.sh`. Both this row and the one below were measured in ONE session on ONE GPU, making them the first strictly apples-to-apples stock-vs-fine-tune pair (every earlier pair spans hosts). FT-8 element-set **71%** (17 applicable). Plugins **50%** (3/6). Failing: `vf-outdegree`, `ef-label-knows`, `ef-weight`, `ef-edge-type`, `ec-edge-type-switch`, `fn-property-equals`, `an-outdegree`, `path-skeleton` |
| 2026-08-23 | phi4-f8-mini v3 (same published model, re-eval'd) | shipping prompt | 23 | 96% | 91% | 0.2 | 142.2 | Same A10 session as the stock row above. Fine-tune vs stock on identical hardware: compile 91%->**96%**, proxy 78%->**91%**, FT-8 71%->**94%** (17 applicable), plugins 50%->**67%**. Still failing the two open RETRAIN-LOG items (`vf-compound-strings`, `ec-edge-type-switch`) plus `an-outdegree` and `path-skeleton`. **Noise finding, CORRECTED by the 08-24 row below:** three rows differ from the 07-31 row (`gef-label-or`, `epf-knows`, `ef-edge-type`) despite the SAME published model and the same prompts. That was first read as run-to-run sampling variance. It is not: two independent A10 runs (this one and 08-24) agree on all 29 rows for both models, so verdicts are reproducible on a given host and the variance is across HOSTS - different GPU, driver and backend build. Comparing like-for-like hardware, a single run IS a sound basis for closing an entry. `path-skeleton` is recorded as a failed row via the harness per-call timeout after a runaway whole-type generation (~6 min at 142 tok/s); before this run that same runaway aborted the process and discarded 27 completed rows |
| 2026-08-24 | phi4-f8 (14B, published `stoic_hellman_728/phi4-f8`) | shipping prompt | 23 | **100%** | **100%** | 0.6 | 48.5 | Azure A10, first cloud eval of the 14B, measured in the SAME session as the mini and the stock base (rows above), so all three are strictly comparable. FT-8 element-set **100%** (17 applicable): a clean sweep of the delegate set, reproducing the 07-22 Ryzen row exactly on different hardware. Plugins **67%** (4/6), and the two it misses are NOT the mini's two - it passes `path-skeleton` AND `subgraph-skeleton` (whole-type ALGORITHM drafts the mini cannot produce; the mini's `path-skeleton` runaway hit the per-call cap) while missing `fn-property-equals`, which the mini passes. Wall clock 62 s against the mini's 395 s: at 48.5 tok/s it is 3x slower per token yet finishes 6x sooner, because the mini burns ~6 min on that one runaway. **`an-outdegree` fails on all three models**, including this otherwise-perfect one, which is evidence of a CONTRACT/prompt gap rather than a capability gap - exactly what the RETRAIN-LOG 07-26 entry predicted about Analytics grounding. Bearing on DV-4: on identical hardware the 14B's edge is wider than the "2 proxy rows" the 07-22 row concluded (100/100/100 against 96/91/94, plus whole-type algorithms), though the mini stays the CPU-friendly default because the 14B needs a GPU |
| 2026-07-31 | phi4-f8 (14B, 2nd train) | shipping prompt | 23 | **100%** | **100%** | 1.6 | 21.3 | Ryzen box (GPU). FT-8 element-set **100%** (17 applicable). Ceiling HELD on the grown 23-row set: both edge-type rows pass (`EdgePropertyId ==` and the `switch` - the batch-1 class the mini still misses), all three fulltext rows (correct per-field idiom on `vf-compound-strings`), `gef-label-or`. Plugins: whole-type compile **67%** (4/6: both algorithm skeletons + 2 simple functions); misses are body-quality, not shape: `an-outdegree` invents the Analytics surface (`VertexId`, `definition.Graph`, a 1-arg `GraphAnalyticsResult` ctor), `fn-property-equals` drops `using System.Linq` and invents `.Properties` - exactly the 07-26 hardening class. DV-4 stands, sharpened: mini stays the CPU default for fragments; **plugin NL-drafting is effectively 14B-only for now**, and the edge-type + compound-strings classes need one more mini-focused iteration (see RETRAIN-LOG batch-1) |

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
