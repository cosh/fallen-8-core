# canvas-color-property-default

Refinement of [studio-canvas-viz](../../done/studio-canvas-viz/). Small UI fix plus UX
improvement to the Canvas **style panel** property controls; no engine, REST, or OpenAPI
change.

## Problem

When a style control (node **color by** / **size by**, edge **color by** / **width by**)
is set to `property`, the panel is supposed to show a text field for the property key.
It was rendered but **invisible**: the picker `<select>` and the field sat in a
`flex` row where the `<select>` carried `w-28` to stay narrow. The shared `.input` class
is defined **unlayered** in `index.css` and applies `w-full`; under Tailwind v4 cascade
layers an unlayered rule outranks the `w-28` utility (which lives in `@layer utilities`),
so the select kept 100% width (and `shrink-0`), leaving the field no room, so it was
clipped off the 320px panel's right edge. The field was in the DOM (the e2e `.fill()`
still worked) but a user could not see or use it, so "color by property" looked like it
did nothing.

A second gap: the field started **blank**, and the hover help did not clearly answer "what
colors do I get?". The honest answer is that colors are auto-assigned, not hand-picked.

## Behavior

- The property field renders on its **own full-width line** directly under each picker
  (`space-y-1` stack), so it is always visible and the whole property name is readable,
  consistent with the standalone "image / emoji property" field.
- Switching a control to `property` **seeds** the field with the first property key present
  on the canvas (`defaultProperty`), so it is never blank. Switching back and forth keeps a
  value the user already typed; a non-property mode leaves the stored key untouched.
- The field stays **free text** with datalist suggestions from the canvas keys, so the user
  sets the key themselves; the seed is only a default.
- Empty canvas (no property keys) leaves the field blank with its `property id` placeholder.
- Help copy (`canvasNodeColor` / `canvasEdgeColor`, and the size/width entries) now states
  the colors are auto-assigned: each distinct value gets a stable palette color, all-numeric
  values shade along a cyan-to-pink gradient, and missing/blank values render grey.

Colour/size resolution itself is unchanged: `styleEngine.ts` already read these config
fields; this feature only fixes how the fields are presented and seeded.

## Tests

- `tests/style-panel.test.tsx` (new): seeding on switch for all four controls, blank when
  the canvas has no keys, no-overwrite of a customized key across mode switches, free-text
  replacement, datalist wiring (node vs edge), and that a non-property mode emits no
  property override.
- `e2e/screenshot-canvas-style.spec.ts` (new, capture-only, `F8_SCREENSHOT=1`): drives a
  real graph, switches color/size to property, asserts the field shows the seeded `age`,
  and writes `docs/images/screen-canvas-style.png`.
- Existing `e2e/studio.spec.ts` canvas scenario and the full vitest suite (484) pass
  unchanged.

## Impact on existing features

- **studio-canvas-viz**: the behaviour it specified (FR-1/2/3/4/8) is unchanged; this fixes
  its property field being clipped and adds the seed default. Its living README is the home
  for the styling story; this spec records only the delta.
- **Engine / REST / OpenAPI snapshot / MCP**: none. Pure frontend, no route or contract
  touched, so no `McpRestCoverageTest` / snapshot regeneration.
- **NL-assist, stored queries, architecture diagrams**: none (no new channel/deployable,
  no dataset surface).
- **Docs**: `docs/studio.md` Canvas section updated (new screenshot plus property-field and
  color explanation). No new root-README key-feature bullet: this refines the existing
  Canvas feature rather than adding one.

## Root-cause note (for future edits)

The `.input` / `w-*` override trap is Studio-wide: any `.input` element that also carries a
width utility keeps `w-full`. Left it localized here (stacking sidesteps it) rather than
moving `.input` into `@layer components`, which would silently change widths elsewhere.
Revisit only if another panel hits the same clip.
