# NL-Assist Feedback Loop & Model Distribution — Specification

> **Status:** spec only (no implementation). Extends [nl-assist-finetune](../../done/nl-assist-finetune/spec.md)
> with its plan's phase-5 "continuous improvement loop", plus a way to get the produced
> model to the instances that use it. Subordinate to the NL-assist runtime contract
> ([web-ui/nl-assist](../../done/web-ui/nl-assist/spec.md)): privacy, key isolation, and the
> MIT-only posture win where they overlap. Follow the feature workflow in the root `CLAUDE.md`.

## 1. Motivation

`f8-delegate` v2 scores 100% compile / 100% FT-8 element-set semantic on the held-out set,
but that set is small and hand-authored. Real usage will surface intents and phrasings we
never templated (the base model's original failures were only found by using it). We want a
loop that (a) **notices** when first-pass quality slips, (b) **captures** real correction
signal, (c) **folds** it into a better revision under the existing eval gate, and (d) gets
that revision **to the instances** that consume it — none of which exists yet.

## 2. Inherited constraints (non-negotiable; they shape the whole design)

- **No server-side prompt storage or telemetry** (parent NL-assist spec). The NL *intent*
  never reaches a Fallen-8 instance — the browser talks to the model directly — and the
  observability spans deliberately exclude filter text.
- **MIT-only; F8 ships no weights and no trainer** (FR-26.1/26.2, FT-5). The repo ships the
  pipeline; artifacts are the operator's.
- **Single self-hosted operator** is the target (nl-assist-finetune plan phase 5). No
  hosted/multi-tenant training service. Revisit triggers are named per phase.

**Consequence — the loop is two halves that never merge on the server:**

| | lives where | carries | purpose |
|---|---|---|---|
| **Signal** | server (F8), aggregate | counts/rates, **no content** | *when* to look / retrain |
| **Capture** | browser, **opt-in** | (kind, intent, fragment, 👍/👎) | *what* to retrain on |

Only the browser can pair an intent with its fragment; the server never sees the intent. So
capture is client-side and user-triggered, and the server metric is content-free. Conflating
them (server-side capture of prompts) is forbidden by §2 and is an explicit non-goal.

## 3. Architecture

```
 browser NL assist ──intent──▶ model ──draft──▶ /delegates/validate (F8)
        │  👍/👎 + "save as training example" (opt-in, local)     │ compile pass/fail
        ▼                                                          ▼
   feedback.jsonl (kind,intent,fragment,verdict)        FL-1 metric: validate_total{kind,result}
        │                                                          │ (aggregate, no content)
        ▼                                                          ▼
   FL-3 consolidate ─▶ training corpus ─▶ retrain (run.sh) ─▶ eval gate ─▶ FL-4 publish ─▶ pull
      (dedupe+revalidate, never touches eval-set.json)     (strict win)     (registry)
```

## 4. Phases (each independently shippable and right-sized)

### FL-1 — Systemic signal (server, aggregate)
A metric at `POST /delegates/validate`, reusing the [observability](../../done/observability/)
feature: a counter `f8_delegate_validate_total{delegateKind, result}` (result = `valid` |
`invalid`). No fragment text, no intent — just rates, per kind. This is the health/trigger
signal ("first-pass compile rate on VertexFilter dropped this week"), **not** training data.
It cannot see intents (they never reach F8) and must not be extended to capture content.

### FL-2 — User feedback + capture (browser, opt-in)
The NL-assist panel gains, per draft: a 👍/👎 control and a **"save as training example"**
action that appends one JSONL line — `{ delegateKind, intent, fragment, verdict, ts }` — to a
local download (the *final* fragment, after refine turns and manual edits: the label a trainer
wants). A 👎 plus the corrected fragment is the gold signal. Refine transcripts (failed draft +
diagnostics + fix) are a second, optional corpus. Nothing is sent anywhere; it is a manual,
local export (parent privacy rule). Reuses the existing draft history (nl-assist-ux FR-6/7).

### FL-3 — Consolidation tooling (operator, offline)
`nl-assist-finetune/feedback/consolidate.ts` (tsx): ingest one or more exported feedback
files → drop 👎 rows that were never corrected → **re-validate every kept fragment** through
`/delegates/validate` (a non-compiling capture never enters training) → dedupe against the
existing generated corpus → append the survivors to the training corpus in the generator's
row format (system/user/assistant `messages`). **Never writes `eval/eval-set.json`** — the
held-out set only grows by hand, so ledger rows stay comparable. Emits a per-kind coverage
delta and the retrain triggers it observed: ≥ N new pairs, a new eval failure mode, or a
delegate-contract change (schema-drift hash, FT-2).

### FL-4 — Distribution (operator → instances)
The piece that makes an improved model reach beyond the training box (and that justifies the
`f8-delegate` default). A `run.sh publish` stage pushes the produced GGUF to a registry
(`ollama push <namespace>/f8-delegate`, or a Hugging Face repo), carrying `PROVENANCE.md`.
Operators then `ollama pull <namespace>/f8-delegate`; docker-compose optionally pulls it on
init (alongside the phi4-mini base). F8 core still ships no weights — this is the *operator's*
publish channel, opt-in, MIT-clean (Phi-4-mini MIT + MIT-generated dataset).

## 5. Functional requirements

- **FB-1** The server signal is aggregate-only: label set is `{delegateKind, result}`, never
  fragment/intent text; off by default with the rest of the observability metrics.
- **FB-2** Capture is opt-in and local: no automatic upload, no server round-trip; the export
  is a user action producing a file the operator moves deliberately.
- **FB-3** Every consolidated fragment re-validates before entering the corpus; the held-out
  eval set is never modified by tooling.
- **FB-4** Retrain is trigger-based, not scheduled; the existing eval gate (compile AND FT-8
  semantic, strict win, no per-kind regression) decides publish/no-publish — unchanged.
- **FB-5** Distribution is an explicit operator publish; pulling a published model needs no
  code change (the NL-assist `model` field / builtin default already points at `f8-delegate`).
- **FB-6** Provenance travels with the artifact (base model + MIT license, tool versions,
  dataset hash) so a pulled model's licence position is auditable (FT-7).

## 6. Non-goals & revisit triggers

- **No automatic/opt-out capture, ever** — privacy. (No trigger; this is a hard line.)
- **No server-side intent↔fragment pairing** — only the client has both.
- **No hosted or multi-tenant training/serving service.** Revisit trigger: a second regular
  contributor, or a shared serving fleet rather than one operator.
- **No eval-in-CI** — a model inference run is far too heavy for the test suite (unchanged).
- **No new model-management UI** beyond the existing model field + presets; a per-user model
  picker is out until there is a real catalogue of published models to choose from.

## 7. Testing

- FL-1: a unit test asserts the metric increments `valid`/`invalid` by kind and carries **no**
  content labels; the metrics-off default is covered.
- FL-2: a web-ui test drives 👍/👎 and asserts the export line shape and that nothing is
  POSTed to any endpoint.
- FL-3: a tsx/unit test feeds mixed (compiling + non-compiling + duplicate + eval-set-overlap)
  captures and asserts only fresh compiling non-eval rows survive, and `eval-set.json` is
  untouched.
- FL-4: documented, manually-run publish/pull; not in CI.

## 8. Reference

- [nl-assist-finetune](../../done/nl-assist-finetune/) — the pipeline this feeds; plan phase 5
  is the origin of the flywheel.
- [web-ui/nl-assist](../../done/web-ui/nl-assist/spec.md) — runtime contract, privacy, key isolation.
- [observability](../../done/observability/) — the metric/tracing surface FL-1 extends.
