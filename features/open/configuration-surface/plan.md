# Configuration surface: plan

Phases are ordered so nothing is captured or published before the copy it photographs is correct.

## P1: the catalog module (pure, no React)

`fallen-8-web-ui/src/lib/configCatalog.ts`. Named for the catalog rather than for the sections,
because it also became the one home for the three per-key rules Studio derives from a descriptor:
those lived in `SettingRow.tsx`, and the surface selects and filters by the same rules, so a second
copy would have been the thing that drifted.

- `CONFIG_SECTIONS`: the ordered table from spec section 4, each entry
  `{ id, group, label, blurb, raw, flat? }`, plus the trailing `other`.
- `CONFIG_GROUPS`: the five nav headings in order, plus the trailing not-grouped-yet heading.
- `sectionOf(key)`: second segment to section, `other` when unmapped, when the key has fewer than
  three segments, or when the prefix is not `Fallen8`. Exact-case, matching the server.
- `groupSettings(settings)`: settings to `{ section, settings, groups }[]`, preserving the server's
  order inside each raw section, ordering a merged section by its declared `raw` order, omitting
  empty sections, and **never dropping a key**.
- Sub-groups from the key's own prefix (its last segment removed), the unlabelled "direct" group
  first, first-appearance order for the rest, and a two-entry label override for the prefixes whose
  last segment reads badly as a header. A `flat` section suppresses headers entirely.
- `matchesQuery(setting, query)`: key, rule and reason, each checked separately so a query cannot
  match across the boundary between two fields. Case-insensitive. Never the value.
- `CONFIG_FILTERS` and `matchesFilter(setting, filterId)`.
- Moved in from `SettingRow.tsx`: `settingTestId`, `environmentSpelling`, `isEnvironmentLocked` and
  the authority-source list behind it.

Gate: `tests/config-catalog.test.ts`, run before any component work, with the shipped key list read
out of `Fallen8SettingCatalog.cs` by `tests/shippedSettingKeys.ts` rather than copied into a fixture.
The load-bearing assertions are that grouping the shipped keys yields the same keys back, and that
none of them lands in `other`: the second is what fails when the server grows a section this taxonomy
does not map.

## P2: the surface

Two new files in `fallen-8-web-ui/src/components/`, not four: the dialog shell, the nav, the pane and
the section body are one cohesive unit replacing what is deleted from the panel, and splitting them
further would spread one story over four files.

- `ConfigurationSurface.tsx`: the Radix `Dialog` shell (`usePortalContainer()`, `.modal-overlay`,
  `panel modal-center`, `aria-describedby={undefined}`) plus the body it renders as a child of
  `Dialog.Content`, so Radix's unmount resets the section, the query and the filter for free. Owns
  those three and nothing else. Every callback it passes down is stable, so `SettingRow`'s memo keeps
  holding. It also injects an Observability entry when the instance publishes no inventory at all,
  because that section reads its values off the observability block and is exactly what an older
  server can still show.
- `ObservabilitySection.tsx`: `EnvRow`, `ObsSection`, the three groups and all three hints **moved
  verbatim** from `ConfigurationPanel.tsx`, minus the sentence that pointed at "the Settings list
  above" and minus the opening clause the section's own blurb now carries. The three writable keys
  render as `SettingRow`s inside their groups, falling back to the read-only `EnvRow` when no
  descriptor arrives; the three withheld ones stay `EnvRow`s, because an `EnvRow` shows the effective
  value a never-writable `SettingRow` deliberately does not publish. Anything else the instance
  publishes under that prefix renders in a trailing group rather than vanishing.

Visual vocabulary is assembled from what exists: `panel` / `panel-title` / `btn` / `btn-accent` /
`input` / `label` / `modal-overlay` / `modal-center`, plus the bordered-aside idiom from
`DelegateEditor`. No new CSS. Two deviations from the first draft, both deliberate: the nav is a plain
list with `aria-current` rather than `CanvasScreen`'s `role="tablist"`, because a search shows matches
from every section at once and tab semantics would then be a lie about what the pane holds; and
`.scroll-list` is NOT used inside the dialog, because its max-height is a row count that would cap the
pane far below the dialog's real height, so the pane uses the existing
`min-h-0 flex-1 overflow-y-auto` dialog idiom.

## P3: the summary card

`ConfigurationPanel.tsx` shrinks to the card plus the state it already owns.

- Keeps: `useConfig(..., { poll: !dirty })`, the draft, the write mutation with its partial-draft
  preservation, the per-instance reset (plus a new `setShowSettings(false)`), the stable row
  callbacks, the blank-numeric gate, `isNamespacePolicy`, `editable`, `writesAllowed`, and the
  provider cards, status rows, badges and observability summary.
- Deletes: the flat settings block, the second `Configure...` button, and the whole
  `ObservabilityOverlay` / `EnvRow` / `ObsSection` trio (moved, not copied).
- Adds: `config-configure`, the `open` state, an inventory count line, and the surface render.
- `SettingRow.tsx` gains exactly one thing: a testid on its environment note, so a test can stop
  walking `closest("div")?.parentElement`.

Testid ownership is one owner each, and the card keeps `configuration-panel`, `config-embedding`,
`config-chat`, `config-model-status`, `config-observability-summary`, `config-dirty`,
`config-refresh`, `config-unavailable`, `config-pending-restart` and `config-settings-summary`. The
surface owns `config-surface`, `config-section-nav`, `config-section-<id>`, `config-section-pane`,
`config-search`, `config-filter-<id>`, `config-filter-count-<id>`, `config-save`,
`config-pending-restart-detail`, `config-no-inventory`, `config-no-matches`,
`config-observability-overlay` and every `config-setting-*`. `config-settings-error` is the one
shared handle, on purpose and never at the same time: the card shows a refusal only while the surface
is closed, so closing the dialog on a `409` cannot make it look like the save landed.
`config-observability-configure` is deleted rather than renamed, so a screenshot spec that still
clicks it fails loudly instead of photographing the wrong surface.

## P4: tests

- `tests/config-catalog.test.ts` (new, pure, 34 cases): every shipped key lands in exactly one
  section; the sum equals the input; no shipped key lands in `other`; per-section counts match spec
  section 4; an unmapped section falls into `other` AND is rendered; a two-segment key, a one-segment
  key, an empty key and a foreign prefix do not throw; matching is exact-case; the server's order is
  preserved within a section; a merged section follows its declared order rather than the arrival
  order; sub-groups are correct, including a section with no direct keys and the flat sections;
  `matchesQuery` hits key, rule and reason, never the value, and not across a field boundary;
  `matchesFilter` keys never-writable on the tier and partitions the shipped inventory;
  `groupSettings(undefined)` returns empty.
- `tests/shippedSettingKeys.ts` (new, not collected): reads the shipped keys out of the C# catalog,
  and throws with a legible reason if the extraction stops finding them.
- `tests/configSurface.tsx` (new, not collected): `openConfig`, `selectSection`. Each suite keeps its
  own `renderPanel` and data factories, because their mocks differ.
- `tests/connect-config.test.tsx`: the provider and one-liner cases pass **unchanged**, which is the
  design goal. The observability case opens the surface and selects the section. New cases: the card
  carries no settings row; the dialog lands in the provided portal container; Escape leaves the page
  interactive; a search finds matches in two sections at once; the empty search state says what search
  covers; a filter narrows and counts; an unmapped section is reachable; and the observability
  fallback for an instance with no inventory.
- `tests/connect-config-editor.test.tsx`: every case gains the open step; the read-only copy assertion
  retargets from the card to the surface and additionally asserts the card does NOT carry it (one
  home); the two negative assertions are re-asserted with the surface open so they cannot pass
  vacuously; the lock-gating case navigates to both sections rather than dropping a key. New cases:
  the draft survives close and reopen; the poll stays suspended while dirty with the surface closed;
  the surface adds no second `useConfig` subscription; switching instance closes the surface and drops
  the draft; a filter never changes a row's editability; a refusal stays visible on the card after the
  surface is closed.
  The poll case asserts the `poll` option the panel passes rather than advancing a clock. Fake timers
  were tried first and do not work here: testing-library's `waitFor` and `findBy*` poll with their own
  `setInterval` and cannot see vitest's fake clock, so any query issued under it hangs. The option IS
  the contract, and the mutation check below confirms the assertion bites.
- `tests/mount-seam.test.tsx`: comment-only. That suite stubs `fetch` to throw, so the card offers no
  Configure button and the surface can never be opened there; its assertions about settings rows would
  be doubly vacuous. Say so, and point at the suites that do gate it.

## P5: e2e specs and screenshots

Order matters: the copy edits in P2/P3 and the docs edits in P6 land BEFORE any capture, because two
of the three images contain sentences this feature changes.

- `screenshot-configuration.spec.ts`: open the surface, select the section holding the written key,
  then the row guards. Replace the "checking..." gate, which becomes a no-op once the spinner is
  inside the dialog, with a positive gate on the inventory count. Shoot the page, not the card: a
  portal is not a descendant of the card's section element, so locating the card would photograph a
  summary with no settings on it.
- `screenshot-connect.spec.ts`: drop the settings-row guard, which is false by construction now, and
  replace it with the card's own guards plus an explicit assertion that NO settings row is on Connect.
  Lower the viewport from 1600 to 1200, since the tall settings list is gone.
- `screenshot-observability.spec.ts`: retarget through the single `Configure...` button and the
  Observability section. Keep the three heading assertions and the endpoint guard verbatim.
- Recapture all three images. The capture app needs an API key, the configuration-write capability, a
  FRESH metadata directory, an OTLP endpoint and both providers wired; CI never runs these specs, so
  the recapture is a manual step, and the filename ordering that seeds the restart state is stated in
  the specs rather than left implicit.

## P6: docs

- `configuration.md`: the living doc. A new "Two surfaces, and why" section covering the card, the
  sections, the search and the filters; the placement wording throughout; the split between the card's
  restart COUNT and the surface's per-key disclosure; the unsaved-edit behaviour; the image alt text.
- `studio.md`: the Connect panel inventory, the card-versus-surface split, the observability
  paragraph, and both image alts. The `## Connect` heading keeps slugging to `connect`, which
  `standalone-ui.mdx` links and the docs build link-checks.
- `observability.mdx`: placement, and the pre-existing wrong claim that the three writable
  observability keys are display-only.
- `semantic-traversal.mdx`, `embed-studio.md`: placement, and what the two embed locks now cover.

## P7: gates

`npx vitest run` (whole suite), `npx tsc -b`, `npm run build:apiapp`, `npm run build:lib`,
`npm --prefix docs run build` for the link check, and `dotnet build fallen-8-core.sln` to confirm the
solution is untouched. Then four mutation checks, because four of the new guarantees are the kind that
go green for the wrong reason: drop the dialog's portal container, un-suspend the poll, clear the draft
when the surface closes, and remove the namespace-policy lock. Each must fail the suite. Then the
council review gate on the branch.

## Run ledger

| Phase | State |
|---|---|
| P1 catalog module | done. 34 pure cases green; the drift check was mutation-verified by dropping `Nlp` from the ingestion merge, which failed 4 cases. |
| P2 surface | done. Two files rather than four; the nav width was set from the longest label after reading the captured image. |
| P3 summary card | done. `ConfigurationPanel.tsx` 519 to 390 lines, and the flat list, the second Configure button and the standalone overlay are gone. |
| P4 tests | done. 65 cases across the three config suites, 1008 in the whole suite, `tsc -b` clean. All four mutation checks fail the suite as intended. |
| P5 e2e + screenshots | done. Three specs retargeted and parse-checked; three images recaptured against an isolated app on port 17451 with a fresh metadata directory. |
| P6 docs | done. Docs build green, all internal links valid. |
| P7 gates + council | gates done: vitest, tsc, build:apiapp, build:lib, docs build, `dotnet build` (0 errors, only the pre-existing IL2026 trim warnings). Council pending. |

## Found while doing this, and fixed

- `observability.mdx` claimed the three writable observability keys were "display-only: these keys are
  startup-bound, so there is nothing to write back". They are restart-tier and writable, and the same
  page contradicted itself ten lines earlier.
- Those same three keys rendered TWICE on the Connect screen, once as read-only rows in the standalone
  observability overlay and once as editable rows in the flat list. The fold-in removes that.
- `screenshot-connect.spec.ts` asserted a settings row was visible on Connect. After this change that
  guard is false by construction, so it is replaced with guards on what the shot now documents.
- The Observability section's intro repeated its own section blurb. Caught by reading the recaptured
  image, not by any test.

## Deliberately not done

- `PluginEditor.tsx` is the one `Dialog.Portal` in the codebase with no `container` prop, which is the
  live proof that a missing container ships unnoticed. Out of scope here; worth a one-line fix of its
  own.
- `EnvRow` formats numbers with `toLocaleString()`, so those rows group in the machine's locale.
  Pre-existing, and moved unchanged; the new test asserts against `toLocaleString()` rather than a
  hard-coded comma, so it cannot pass in CI and fail on a German machine.
