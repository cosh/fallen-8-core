# Canvas view controls - clear the working set, show the whole graph

## Problem

The Studio canvas is a working set: it renders what other screens send to it plus
expanded neighborhoods, and nothing more. Two gaps in controlling that working set:

1. **Clearing is incomplete.** A "Clear view" button exists (top-left toolbar strip),
   but it only resets nodes, edges, and the path overlay. The selection survives, so
   after clearing, the detail panel keeps showing the last selected element as if it
   were still on the canvas. "Clear" must mean the canvas is visibly and semantically
   empty.
2. **There is no one-click way to put the whole graph on the canvas.** Today a user
   bounces to the Browser screen, fetches elements, and sends them over. For the small
   and medium graphs the Studio is typically pointed at (samples, demos, freshly
   imported data), "show me everything" is the single most common first wish, and the
   plumbing for it already exists (`GET /graph`, the sample loader uses it).

## Decisions

- **Button label: "Show whole graph".** Verb-first like its siblings ("Clear view",
  "Expand neighbors", "Remove from view"); "whole" reads friendlier than "entire".
  Alternatives considered and rejected: "Load whole graph" (implies a persistence
  action), "Show everything" (vague about scope), "Load full graph". The clear button
  keeps its existing label "Clear view": "view" pins the view-only semantics.
- **Explicit and capped, never automatic.** The published promise ("the canvas never
  auto-loads the whole graph") stays true: the load happens only on click and is
  bounded by the canvas element cap. No auto-load on visiting the screen, ever.
- **Merge, not replace.** The load goes through the existing merge-only canvas model.
  Since everything already on the canvas comes from the same namespace, merging the
  whole graph converges to the whole graph anyway; a fresh start is "Clear view" then
  "Show whole graph". No third "replace" semantic is introduced.
- **Clear wipes content, not configuration.** Nodes, edges, path overlay, and the
  selection are content. The style panel config, result sets, and the other screens'
  drafts are configuration or foreign state and survive a clear.
- **No server changes.** `GET /graph?maxElements=` already exists and is clamped
  server-side; `GET /status` already carries `vertexCount`/`edgeCount` for honest
  truncation messaging. The feature is entirely `fallen-8-web-ui`.

## Functional requirements

- **FR-1 Complete clear.** One click on "Clear view" empties the working set: canvas
  nodes, canvas edges, path overlay, and the selection (the detail panel returns to
  its empty hint). Style config, result sets, and screen drafts are untouched. The
  button stays disabled when the canvas is already empty. No confirmation dialog: the
  action is view-only and the database is never touched (the existing microcopy rule
  keeps applying).
- **FR-2 Show whole graph.** A "Show whole graph" button in the canvas toolbar strip
  fetches `GET /graph?maxElements=<cap>` for the active instance and namespace and
  merges the result via `mergeIntoCanvas`. While in flight the button is disabled and
  shows a busy label; a failed fetch surfaces in the existing error style and leaves
  the current canvas contents intact. An empty namespace yields an empty merge, which
  is fine; the button needs no extra emptiness gating.
- **FR-3 One cap, honestly reported.** The fetch uses the shared canvas element cap
  (today 20,000, defined privately in `sampleLoader.ts` and duplicated as a literal in
  `SampleGraphsPanel.tsx`): promote the constant to one shared home and use it at all
  three call sites. REST semantics note: `maxElements` caps vertices and edges
  independently (the server clamps to 100,000 each). When the namespace's counts from
  `GET /status` exceed the cap, a persistent notice appears next to the element count,
  e.g. "showing the first 20,000 of 153,204 vertices"; the button never silently
  pretends the whole graph is visible. Edges whose endpoint vertex fell outside the
  vertex page render as stub nodes (existing `buildCanvasModel` behavior); the notice
  covers that case too.
- **FR-4 Discoverable from the empty state.** The detail panel's empty-canvas hint
  gains the new action: "Send elements here from the browser, query, path, or subgraph
  screens, or show the whole graph." It must not mention the unittest endpoint
  (standing rule: newcomers are pointed at the Sample gallery, not test scaffolding).
- **FR-5 Races stay simple.** A clear issued while a whole-graph load or a neighbor
  expansion is in flight does not cancel it; the late merge lands and a second "Clear
  view" recovers. Last action wins; no cancellation machinery.

## Non-goals

- No new REST endpoint, no paging/streaming of `GET /graph`, no engine change.
- No auto-load of any kind when the canvas opens.
- No confirmation dialog or undo for "Clear view" (see revisit triggers).
- No progressive rendering beyond the cap, no renderer-specific caps.
- No "replace" load mode.

## Impact on existing features

- **Engine / REST contract / OpenAPI snapshot:** untouched; only the existing
  `GET /graph` and `GET /status` are consumed. **MCP:** no new REST operation, so no
  bridge-or-defer decision arises.
- **Studio UI:** `CanvasScreen` and the workspace store change; the sample gallery
  loader and `SampleGraphsPanel` switch to the promoted shared cap constant (behavior
  unchanged, duplication removed).
- **Docs site:** `docs/src/content/docs/studio.md` (Canvas section) is updated in the
  same PR: the "renders exactly what you send... never auto-loads" sentence stays true
  but now names the explicit button, and both toolbar actions are documented. Canvas
  screenshots that show the toolbar strip (at least `screen-canvas-style.png`; check
  the cyber-sample shot) are recaptured.
- **NL-assist dataset/eval:** no impact; no query surface or API shape changes.
- **Root README:** no new key-feature entry; this is an increment inside the existing
  Studio feature.
- **Architecture diagrams:** unaffected; no new channel or deployable.
- **`features/done/studio-canvas-viz` and friends:** historical records, not
  rewritten; the user-facing canvas story continues to live on the docs-site page.

## Test expectations

Vitest coverage for: clear resets nodes, edges, overlay, and selection while style
config and result sets survive; whole-graph load merges without dropping existing
elements; the truncation notice appears exactly when counts exceed the cap and is
absent otherwise; busy/disabled button states; a failed fetch leaves the canvas intact
and shows the error; the cap constant has one home and all call sites use it. Extend
the Playwright suite only if a cheap assertion fits an existing spec file.

## Revisit triggers

- Users routinely point Studio at graphs beyond the cap and ask for more: add
  server-side paging to `GET /graph` or a progressive load, and only then.
- Accidental clears get reported: add an undo (restore the last working set) rather
  than a confirmation dialog.
