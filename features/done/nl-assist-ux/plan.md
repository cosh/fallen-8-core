# NL Assist UX — Implementation Plan

Branch `feature/nl-assist-ux`. All work in `fallen-8-web-ui/`; no server changes.

## Phase 1 — config model

- `src/delegate/nl/config.ts`: add `mode: "builtin" | "custom"` (default `builtin`),
  `BUILTIN_NL_BACKEND` constants, `effectiveNlConfig()`, `NL_PRESETS`, mode-aware
  `isNlConfigured()`, and a persist migration (`version: 1`) deriving `mode` from the
  stored endpoint (FR-4).

## Phase 2 — transport stats + probe

- `src/delegate/nl/generate.ts`: `chatWithModel` returns `{ content, stats }` where
  stats normalizes Ollama (`eval_count`, `eval_duration`, `total_duration` in ns) and
  OpenAI (`usage`) fields and keeps the raw payload. New `probeEndpoint()` for FR-2.

## Phase 3 — panel

- Extract the panel from `DelegateEditor.tsx` into `src/delegate/nl/NlAssistPanel.tsx`
  (it was already the larger half of that file) and add: mode switch + builtin status
  line (FR-1/2), preset selector + temperature field in custom config (FR-3),
  accumulated clickable attempts with active marker, clear action, and per-attempt stats
  (FR-5/6/7).
- `src/delegate/nl/prompt.ts`: `buildGenerationPrompt` takes prior drafts for the same
  intent and requests a distinct variant (FR-8).

## Phase 3b — draft quality (added after first field test)

- `src/delegate/nl/format.ts`: deterministic top-level `&&`/`||` pretty-printer applied
  to every draft before insertion (FR-9).
- `src/delegate/nl/prompt.ts` + `src/delegate/snippets.ts`: built-in `Label`/`Id`
  steering line and a combined "Label + property" few-shot snippet (FR-10).

## Phase 4 — tests + docs

- Update `tests/nl-config.test.ts`, `tests/delegate-editor.test.tsx`,
  `tests/nl-prompt.test.ts`; adjust e2e scenario 10 to the builtin-default reality.
- Update the NL-assist section of `fallen-8-web-ui/README.md` (the living usage doc).

Done when `npm test` and the web-ui build are green and the e2e suite passes.
