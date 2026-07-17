# Studio mutations UX — spec

## Problem

The Mutations panel on the Browser screen stacks all five mutation forms (create vertex,
create edge, set property, remove property, remove element) into one dense block. Users
can't tell where one mutation ends and the next begins, fields carry terse labels with no
explanation ("source id" of what?), and the forms expose only a fraction of what the REST
API accepts: `PUT /vertex` and `PUT /edge` take a `creationDate` and a full `properties`
list, but the panel only sends a label (vertex) or endpoints + label (edge), hardcoding
`creationDate: 0` and omitting properties entirely.

The missing-explanation problem is portal-wide: no input field anywhere in F8 Studio
explains itself on hover.

## Behaviour

### 1. One tab per mutation type

The Mutations panel becomes a tabbed panel with four tabs, one per mutation shape:

- **Vertex** — create a vertex.
- **Edge** — create an edge.
- **Property** — set or remove a property on an existing element (the two share every
  input, so they share a tab; Set and Remove are separate buttons).
- **Remove** — remove a graph element (danger styling, kept alone so a destructive
  action never sits next to a create button).

Only one tab's form is visible at a time. Tab switching is client-side state; no field
values are lost when switching back and forth. The panel keeps its single success-message
and error area (per active mutation), and everything still goes out with
`waitForCompletion=true`.

### 2. Full mutation spectrum

Each tab exposes everything its REST specification accepts:

- **Vertex** (`PUT /vertex`, `VertexSpecification`): optional label, optional creation
  date, and a property list (0..n rows of property id + typed value, using the existing
  typed-literal editor).
- **Edge** (`PUT /edge`, `EdgeSpecification`): required source/target vertex ids and edge
  property id, optional label, optional creation date, and the same property list.
- **Property** (`PUT/DELETE /graphelement/{id}/{propertyId}`): element id, property id,
  typed value — unchanged surface, now on its own tab.
- **Remove** (`DELETE /graphelement/{id}`): element id — unchanged surface.

Creation date rule: the wire field is a Unix timestamp in seconds (`uint`). The input is
optional free text; empty sends `0` (today's behaviour), otherwise the value must parse as
either a non-negative integer (taken as Unix seconds) or an ISO date/time (converted to
Unix seconds). Invalid input disables the submit button and shows the standard inline
validation message.

Property rows validate like every other typed literal (`validateTypedValue`); a row with
an empty property id or an invalid value blocks submit. Duplicate property ids within one
form block submit (the wire format is a list, but the engine's property map cannot hold
two values for one key — the transaction would fail, so we surface the earlier, clearer error).

### 3. Portal-wide field help

Every labeled input in the studio gets a hover explanation:

- Help copy lives in ONE dictionary module (`src/lib/fieldHelp.ts`), keyed by concept
  (e.g. `elementId`, `sourceVertex`, `edgePropertyId`, `indexId`, `typedValue`, …), so a
  concept is explained once no matter how many screens show it. Screen-specific one-off
  fields get their own key; nothing is inlined at call sites.
- A shared `Field` component wraps label + input and carries the help as a native
  `title` on the WRAPPER (the established pattern in this codebase), so hovering the
  label or the input shows it; a `cursor-help` affordance with a subtle dotted underline
  makes the help discoverable. Checkbox labels and aria-labeled inputs use the `help(key)`
  accessor directly.
- All existing screens and shared components (`Browser`, `Query`, `Path`, `Subgraph`,
  `Analytics`, `Dashboard`, `Connect`, `SaveGames`, `Canvas`, the app-shell instance
  switcher, `TypedLiteralEditor`, stored-query controls, confirm dialog, NL-assist
  inputs) adopt the help system. No visual redesign — same `.label` typography, plus the
  help affordance.

## Non-goals

- No new REST surface and no server changes; the created element's id still isn't
  reported (202 with no body) — the existing hint to find it via bulk view stays.
- No tooltip library; native `title` is deliberate (zero dependency, works everywhere,
  matches the ~20 existing `title=` usages). Revisit only if a real accessibility
  requirement (touch devices, screen-reader-first users) shows up.
- No forms for index/subgraph/etc. mutations here — those live on their own screens and
  only gain field help, not restructuring.

## Acceptance

- Mutations panel shows four tabs; each tab shows exactly one mutation's fields.
- Creating a vertex with label + creation date + two typed properties sends the full
  `VertexSpecification`; same for edges.
- Every labeled field in the studio has a non-empty `title` explanation from the shared
  dictionary; hovering label or input shows it.
- Existing behaviour pins hold: `waitForCompletion=true` on every mutation, success
  message per action, `ErrorBox` on failure, existing e2e flows unbroken.
- Unit tests cover: tab switching preserves state, full-spec payload assembly (vertex +
  edge), creation-date parsing (empty/seconds/ISO/invalid), duplicate property-id
  rejection, and that the help dictionary has no empty entries.
