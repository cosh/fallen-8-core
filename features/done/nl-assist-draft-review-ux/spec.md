# nl-assist-draft-review-ux

Refinement of the NL-assist side panel shared by the delegate editor
([nl-assist-ux](../../done/nl-assist-ux/)) and the plugin authoring editor
([plugin-registration](../../done/plugin-registration/)), plus the local feedback capture
([nl-assist-feedback-loop](../../done/nl-assist-feedback-loop/)). Pure frontend
(`fallen-8-web-ui`); no engine, REST, or OpenAPI change.

## Problem

The panel that turns a plain-language description into a drafted fragment/plugin has three
review-ergonomics gaps:

1. **The description box is too small.** The intent `<textarea>` is `h-16` (64px) — barely two
   lines — so anything past a short phrase scrolls inside a cramped field while the operator is
   still writing the request that drives the whole draft.
2. **The draft list does not scroll and buries the newest draft.** Drafts (the results of the
   small-model run) accumulate oldest-first in a plain `<ol>` with no height cap. After a few
   generate/refine rounds the list you care about — the latest draft — is at the **bottom**,
   pushing the export affordance and, in the delegate sidebar, the rest of the panel down.
3. **A drafted-but-unrated draft is easy to lose.** The 👍/👎 capture (which feeds the
   fine-tune corpus) only matters if the operator actually judges each draft, but an unrated
   draft looks identical to a rated one, so drafts slip by unjudged.

The draft-row markup was also **duplicated verbatim** across the two panels, so any of these
fixes would otherwise have to be made (and kept in sync) twice.

## Behavior

- **Bigger description box.** The intent `<textarea>` doubles in height (`h-16` → `h-32`,
  128px) in both the delegate and plugin panels, giving a comfortable multi-line request area.
  Everything else about the field (placeholder, help, `resize-none`) is unchanged.
- **Scrollable, newest-first draft list.** The draft `<ol>` gets a fixed max height and its own
  vertical scrollbar (`max-h-64 overflow-y-auto`), and renders **newest draft on top**. Draft
  numbering is unchanged — "draft 1" is still the first one generated — only the visual order is
  reversed, so the just-produced draft (the one loaded into the editor) sits at the top where
  the eye lands. Clicking a draft still loads it; 👍/👎 and the training export are unchanged.
- **Unrated drafts stand out until judged.** A draft with no verdict renders with a left accent
  bar and a subtle tint (`border-warn` / `bg-warn`), and its 👍/👎 buttons are shown at full
  strength (not faint) inviting a decision. The moment it is rated 👍 or 👎 the highlight
  clears and the row returns to the calm baseline. Un-rating it (clicking the same verdict
  again) restores the highlight. A `data-unjudged` attribute marks these rows for styling/tests.
- **One home for the row.** The draft-row/list markup is extracted into a single shared
  `NlDraftList` component under `src/delegate/nl/`; both panels render through it. The
  panel-specific bits (label suffix — `(N error(s))` for delegates vs `(invalid)` for plugins —
  the load-button title, the per-attempt stats/raw-stats detail, and the `data-testid`
  prefixes) are passed in, so behaviour is identical and the three changes above live in exactly
  one place.

No change to what is captured or exported: the training-example JSONL shape in `feedback.ts`
is untouched, so the fine-tune corpus/consolidation contract is unaffected.

## Tests

- `tests/nl-draft-list.test.tsx` (new): the shared component in isolation — newest-first render
  order, stable "draft N" numbering independent of display order, `data-unjudged` present only
  until a verdict is set, load/rate callbacks fire with the original (generation-order) index,
  and the list carries the scroll utilities.
- `tests/delegate-editor.test.tsx` (extended): after two drafts the first list row is the newer
  draft; both rows are `data-unjudged` until rated, and rating the top one clears its flag; the
  intent textarea carries the doubled-height class. Existing gating/refine/export tests pass
  unchanged (role-name draft queries are order-independent).

## Impact on existing features

- **nl-assist-ux / plugin-registration**: the drafting/refine/validation contract (FR-6/7/8/9,
  never-auto-submit) is unchanged; this only restyles and re-orders the draft list and enlarges
  the intent box. Their specs remain the historical record; this spec is the delta.
- **nl-assist-feedback-loop**: 👍/👎 capture and the exported JSONL are byte-for-byte the same;
  making unrated drafts prominent *helps* capture without changing its format — **no
  `RETRAIN-LOG.md` entry** (no dataset/eval surface changes).
- **Engine / REST / OpenAPI snapshot / MCP**: none. No route or contract touched, so no snapshot
  regeneration and no `McpRestCoverageTest` impact.
- **Architecture diagrams / stored queries / samples**: none (no new channel or deployable).
- **Docs**: `docs/src/content/docs/studio.md` NL-assist paragraph updated to describe the
  newest-first scrollable list and the unrated-draft highlight. No screenshot depicts the
  delegate-editor NL panel today (Studio screenshots are per-screen; the editor is a modal), so
  no image is recaptured. No new root-README key-feature bullet: this refines an existing
  Studio feature rather than adding one.
  > Superseded (2026-08-18), and only the screenshot sentence above: `screen-nl-assist.png` and
  > `screen-delegate-editor.png` now do depict the panel. A modal is shootable after all, by
  > cropping the frame to the dialog's own box. Every other decision in this section stands.
