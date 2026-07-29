# nl-assist-draft-review-ux — plan

Small, self-contained frontend change. One shared component extraction plus a size tweak, then
docs. All in `fallen-8-web-ui`.

## Phase 1 — Extract the shared draft list

- New `src/delegate/nl/NlDraftList.tsx`: renders the `<ol>` of drafts.
  - Props: `testid`, `verdictTestidPrefix`, `drafts: NlDraftView[]`, `onLoad(index)`,
    `onRate(index, verdict)`.
  - `NlDraftView` = `{ valid, verdict, active, loadTitle, labelSuffix?, trailing?, below? }`
    (`trailing`/`below` are the delegate panel's inline stats + raw-stats details slots).
  - Newest-first: iterate `drafts.map((d,i)=>({d,i})).reverse()`, keep `i` for key/label/testid
    and callbacks so generation-order numbering and existing indices are preserved.
  - Scroll: `<ol className="max-h-64 space-y-1 overflow-y-auto pr-1">`.
  - Prominence: `data-unjudged` + `border-warn/60 bg-warn/5` left bar on `verdict === null`;
    transparent left border otherwise so widths line up. Verdict buttons full-strength while
    unjudged, accent/danger once chosen, faint for the not-chosen side after judging.

## Phase 2 — Wire both panels + enlarge the box

- `NlAssistPanel.tsx`: replace the inline `<ol>…</ol>` with `<NlDraftList>`; map each attempt to
  `NlDraftView` (labelSuffix `(N error(s))`, `trailing` = stats line, `below` = raw-stats
  `<details>`). Textarea `h-16` → `h-32`.
- `PluginNlAssistPanel.tsx`: same, labelSuffix `(invalid)`, no stats slots. Textarea
  `h-16` → `h-32`.

## Phase 3 — Tests

- New `tests/nl-draft-list.test.tsx` (component in isolation).
- Extend `tests/delegate-editor.test.tsx` (ordering, prominence, textarea size).

## Phase 4 — Docs + gates

- Update `docs/src/content/docs/studio.md` NL-assist paragraph.
- `npm run build` (tsc + vite) clean; `npm test` (full vitest) green;
  `npm --prefix docs run build` link-checked green.
