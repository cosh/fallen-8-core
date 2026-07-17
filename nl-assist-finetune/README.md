# nl-assist-finetune

Offline pipeline for specializing the NL-assist model
([spec](../features/open/nl-assist-finetune/spec.md),
[plan](../features/open/nl-assist-finetune/plan.md)). Nothing here is required to build
or run Fallen-8; no weights or datasets are ever committed (spec FT-5).

Current state: **phase 1 — baseline evaluation harness**. Dataset generator and training
scripts follow (plan phases 2–3; training needs a GPU machine).

## Baseline / comparison runs

Prerequisites: Ollama running with the model under test pulled, and a local apiApp as the
compile authority:

```bash
Fallen8__Durability__Volatile=true \
Fallen8__Security__EnableDynamicCodeExecution=true \
dotnet run --project fallen-8-core-apiApp
```

```powershell
$env:Fallen8__Durability__Volatile = "true"
$env:Fallen8__Security__EnableDynamicCodeExecution = "true"
dotnet run --project fallen-8-core-apiApp
```

Then:

```bash
npx tsx nl-assist-finetune/eval/baseline.ts                        # stock phi4-mini
NL_EVAL_MODEL=f8-delegate npx tsx nl-assist-finetune/eval/baseline.ts   # a fine-tuned model
```

The harness makes one first-pass call per `eval/eval-set.json` row through the web UI's
real prompt/format modules and scores compile (via `POST /delegates/validate`), the
row's semantic-proxy checks, and performance (s/draft, tokens/s). It is resumable — rerun
after an interruption and completed rows are skipped. Raw results land in
`eval/results/` (gitignored); the summary numbers go into the **run ledger** in
[plan.md](../features/open/nl-assist-finetune/plan.md) so improvements and regressions
are visible run-over-run. Performance numbers are hardware-bound — compare runs from the
same machine only.

`eval/eval-set.json` is the held-out set: never feed it to training (spec FT-4/FT-G4).
