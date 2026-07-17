# NL Assist UX: Built-in Backend, Presets, Stats, Draft History

> **Status:** In progress on branch `feature/nl-assist-ux`. Extends the shipped NL-assist
> feature ([features/done/web-ui/nl-assist/spec.md](../../done/web-ui/nl-assist/spec.md),
> "parent spec" below). The parent's contract (prompt assembly, validation gate, key
> isolation, MIT-only posture) is unchanged; this spec covers only the deltas.

## 1. Problem

Three usability gaps reported against the shipped editor panel:

1. **Configuration friction.** The assist starts unconfigured; the user must type an
   endpoint before first use even though the project's `docker-compose.yml` already ships
   the exact backend (Ollama on `localhost:11434` with `phi4-mini`).
2. **No generation statistics.** The model response carries token counts and timings
   (Ollama: `eval_count`, `eval_duration`, …; OpenAI-compatible: `usage`) but the UI
   discards them.
3. **Broken draft history.** Every "Draft fragment" click wipes the attempt list, so it
   perpetually shows "attempt 1"; clicking an attempt re-inserts text identical to the
   editor content, so it appears dead. Requesting several drafts of the same intent
   returns near-identical fragments (deterministic temperature 0.1) with no way to ask
   for a variant.

## 2. Functional requirements

### Backend modes

- FR-1 **Built-in mode (default).** `mode: "builtin"` uses a fixed backend — the stack
  the project ships in `docker-compose.yml`: endpoint `http://localhost:11434`, API kind
  `ollama`, model `phi4-mini`. Zero configuration; the panel is immediately usable.
  "Built-in" means *shipped by the compose setup*, not bundled into F8 — parent FR-26.2
  (nothing bundled, MIT-only) is untouched.
- FR-2 **Reachability status.** When configured, the panel probes the effective endpoint
  (Ollama: `GET /api/version`; OpenAI-compatible: `GET /v1/models`) and shows a one-line
  reachable / not-reachable status with a hint how to start the built-in stack. The probe
  is informational only; generation is never blocked on it.
- FR-3 **Custom mode.** `mode: "custom"` exposes the existing fields (endpoint, API kind,
  model, optional key for the OpenAI kind, temperature) plus a **preset** selector that
  prefills them: Ollama (local), OpenAI (`https://api.openai.com/v1`), Anthropic
  (OpenAI-compatible, `https://api.anthropic.com/v1`). Presets are convenience prefills,
  not recommendations — the parent's MIT-only blessing and the FR-26.10 leave-notice for
  non-loopback endpoints apply unchanged. Hosted endpoints may additionally be blocked by
  missing CORS headers (parent NL-G2); the config hint says so.
- FR-4 **Migration.** Persisted configs from before this feature get `mode` derived:
  non-empty stored endpoint → `custom` (fields preserved), otherwise `builtin`.

### Generation statistics

- FR-5 **Stats per attempt.** Each model call's statistics are captured and shown on its
  attempt row: a compact derived line (completion tokens, duration, tokens/s where the
  backend reports them) plus the raw provider payload behind an expandable details
  element. Nothing is sent anywhere; stats live in component state only.

### Draft history

- FR-6 **Attempts accumulate.** The attempt list persists across "Draft fragment" clicks
  for the lifetime of the editor modal; drafts are numbered continuously. A "clear"
  action empties the list.
- FR-7 **Attempts are actionable.** Clicking an attempt loads that fragment into the
  editor (re-validating as usual); the attempt matching the current editor text is
  visually marked as active.
- FR-8 **Distinct variants on re-draft.** When the intent is unchanged and prior drafts
  for it exist, the generation prompt lists them and asks for a meaningfully different
  valid variant, so "draft it again" stops returning byte-identical output at
  deterministic temperature.

## 3. Non-goals

- No new F8 server endpoints; the model call remains browser → endpoint (parent §4).
- No change to the validation gate, key isolation, or leave-notice semantics.
- No CORS proxy for hosted endpoints (parent NL-G2 stays out of scope).

## 4. Testing

- Unit: mode enablement + effective-config resolution, persisted-state migration, preset
  prefills, stats normalization (Ollama ns fields and OpenAI `usage`), variant-prompt
  assembly.
- Component: builtin default renders the usable panel with no configuration; custom mode
  with empty endpoint shows the disabled hint; attempts accumulate across runs and click
  restores a prior draft; key-field visibility per kind unchanged. Model calls and the
  reachability probe are mocked.
- E2E: parent scenario 10's "unconfigured half" becomes "builtin default": the assist
  panel is usable without configuration and the editor works normally.
