# NL-Assist Feedback Loop & Distribution — Implementation Plan

Branch `feature/nl-assist-feedback-loop` from `main`. Four phases; each lands independently
and is useful on its own, so we can stop after any of them. Keep it right-sized — the
non-goals in the spec (§6) are guardrails, not TODOs.

## Phase FL-1 — systemic signal (smallest; do first)
- Add a counter `f8_delegate_validate_total{delegateKind, result}` in the apiApp's
  `/delegates/validate` path, wired through the existing observability meter (off by default
  with the other metrics). No fragment/intent labels.
- Test: increments by kind + result, no content labels, respects the metrics-off default.
- Value on its own: you can watch first-pass compile rate per kind in the wild — the "when to
  retrain" trigger — with zero privacy cost.

## Phase FL-2 — feedback + capture (browser, opt-in)
- NlAssistPanel: 👍/👎 per draft; a "save as training example" action that appends
  `{delegateKind, intent, fragment, verdict, ts}` to a downloaded JSONL. Reuse the draft
  history store. No network calls.
- Test (vitest): the export line shape; asserts nothing is POSTed.
- Value on its own: you start banking real correction pairs immediately, before any tooling.

## Phase FL-3 — consolidation tooling (operator, offline)
- `nl-assist-finetune/feedback/consolidate.ts`: ingest exported JSONL → keep corrected 👍/
  fixed rows → re-validate each via `/delegates/validate` → dedupe vs the generated corpus →
  append survivors (in the generator's `messages` row format) to the training corpus. NEVER
  touches `eval/eval-set.json`. Prints per-kind coverage delta + observed retrain triggers.
- Test: mixed input (compiling/non-compiling/dupe/eval-overlap) → only fresh compiling
  non-eval rows survive; eval set untouched.
- Then retrain with the existing `run.sh` + eval gate (already built).

## Phase FL-4 — distribution
- `run.sh publish`: `ollama push <namespace>/f8-delegate` (or an HF upload), carrying
  PROVENANCE.md; document the `ollama pull` operators run; optional docker-compose pull.
- This is what makes the `f8-delegate` default (kept, per the scope decision) work on
  instances other than the training box.
- Not in CI (needs registry creds + a real artifact); documented + manually run.

## Sequencing note
FL-1 and FL-2 are independent and cheap — either can go first. FL-3 depends on FL-2's export
format. FL-4 is independent of the feedback half and can be done whenever you want to share a
revision. Recommend FL-1 → FL-2 → FL-4 → FL-3 (signal + capture + get v2 shared first, build
the consolidation tool once real captures exist to test it against).
