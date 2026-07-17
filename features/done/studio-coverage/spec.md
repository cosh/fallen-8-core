# Studio Coverage — UX Concept Specification

> **Status:** Implemented and merged (branch `feature/studio-coverage`, 2026-07-17; see
> [plan.md](./plan.md) for the phase record). Produced by a three-lens design review —
> task-flow research, information architecture, and minimal-surface architecture — over the
> six backend capabilities that have no F8 Studio surface. Where the lenses disagreed, the
> arbitration and the dissent are recorded inline. Feature mechanics are NOT re-explained
> here; each section points to the owning feature README (one home per explanation).

## 1. Overview

Six shipped backend capabilities are invisible in F8 Studio: graph analytics, the stored
query library, bulk import/export, vector index search, the `/statistics` snapshot, and the
change feed (beyond the liveness chip). This concept places five of them and deliberately
defers one. The governing stance, unanimous across all three lenses:

- **The Studio's identity is "results land on the canvas."** Every accepted addition feeds
  the existing id-list → hydrate → `ElementTable` → send-to-canvas pipeline.
- **No screen exists to mirror an endpoint.** Each addition is an extension of an existing
  pattern; exactly one new nav destination is created, and it earns the slot (§3).
- **UI structure replaces validation.** Where the API 400s on mutually exclusive fields
  (inline-vs-stored filters, property-vs-explicit vector add), the UI uses a toggle that
  makes the invalid combination unreachable instead of an error message.
- **Honesty notes are pointers, not paragraphs.** Durability, sampling, and consistency
  caveats surface as one caption/tooltip line each; the narratives stay in the feature
  READMEs.

Net new surface: one screen (Analytics), two Dashboard additions (Stored queries panel,
interchange row), one scan kind, one source toggle on two screens, and one shared
`ElementTable` score column. Two capabilities get deliberately less: bulk import is a bare
file picker with server-relayed errors, and the change feed inspector is **deferred** with
named revisit triggers (§8).

## 2. Panel verdicts at a glance

| Capability | Verdict | Surface | Split? |
|---|---|---|---|
| `/statistics` snapshot | build first | "Graph shape" panel on the new Analytics screen + shared schema cache | placement only (§4) |
| Stored query library | build — correctness gap | source toggle + save-as on Path/Subgraph; manage panel on Dashboard | no |
| Graph analytics | build | new **Analytics** rail destination | placement (§3) |
| Vector index | build | sixth scan kind on Query + index-mgmt preset + minimal add | add-form scope (§6) |
| Bulk import/export | build minimal | second row in Dashboard Administration | import yes/no (§7) |
| Change feed inspector | **defer** | none now; tail-console design recorded for the revisit | yes — 3-way (§8) |

Ranking rationale (task-flow lens, adopted): statistics compounds — it de-risks every other
flow by closing the Studio's documented discovery gap (design doc gap G-3: all label /
property / index inputs are free-form). Stored queries are a correctness gap, not an
enhancement: on an instance with `EnableDynamicCodeExecution=false` every delegate slot on
Path and Subgraph answers 403, so the Studio's two flagship screens are dead against exactly
the hardened deployments the library was built for.

## 3. Analytics — new rail destination

**Arbitration.** The minimal-surface lens argued for an analytics panel on Query (zero new
screens); research and IA argued for a destination. Decision: **new rail item** (`∑
Analytics`, route `/analytics`, 8 → 9 items). Two reasons carried it: (a) parity — analytics
is the engine's third plugin-discovered algorithm family, and the other two (path finding,
subgraph extraction) each have a rail slot; (b) the capability carries real workspace weight
(algorithm picker, scope, budgets, partition paging, write-back disclosure) that would turn
Query into a junk drawer. The minimal-surface lens's constraint is honored inside the
screen: no job history, no scheduling, no per-algorithm wizard — the backend is
deliberately one-shot with no job store, and the UI must not fabricate state the server
refuses to keep.

Three panels, top to bottom:

**3.1 Graph shape** — see §4 (it sits first: you look at the shape before choosing an
algorithm).

**3.2 Run**
- Algorithm `<select>` fed by `GET /analytics/algorithms`; the server's description string
  renders under the select (the picker IS the discovery surface — the Dashboard does not
  gain a fourth `PluginList`).
- Scope row: `vertex label` / `edge property` inputs, datalist-fed from the schema cache,
  caption `empty = whole graph`.
- Bounds row: `max results`, `max iterations`, `time budget s`, pre-filled with server
  defaults (the Path screen's pre-filled `maxDepth` pattern).
- Known per-algorithm knobs (`DampingFactor`, `direction`, `epsilon`) as conditional
  `TypedLiteralEditor` rows.
- Write-back disclosure, collapsed by default: checkbox + property-key input (placeholder =
  the algorithm's default key) + one caption: `re-runs overwrite · snapshot-durable only
  (WAL replay drops them — re-run to restore)`. Submitting with write-back on goes through
  `ConfirmDialog` (it mutates every in-scope vertex).
- First-class error states, not generic: 429 → "a run is already in progress on this
  instance"; 408 → "budget exhausted before one full pass — raise the budget or scope down".

**3.3 Result**
- Header chips: `converged` / `iterations n` / `n ms` / warning-tone `budget exhausted`.
- Score algorithms: top-K ids hydrate into `ElementTable` with the shared **score column**
  (§9), then open-as-table / send-to-canvas as everywhere else.
- Partition algorithms: partitions table (`id · size`, largest first); each row's
  **"Members…"** pages `POST /analytics/{name}/partition/{id}` into the same table.
  Caption: `members re-run the specification — exact only on a quiescent graph`.
- After write-back: one accent line naming the property key and pointing at Browser/Canvas
  element detail (which already render properties — zero receiving-side code).

**Not added** (unanimous): run history / queueing / scheduling; convergence charts; a canvas
"color/size by property" mode. The last is the natural follow-up that makes write-back
shine on the canvas — it is a real feature deserving its own decision, not smuggled in here.

## 4. `/statistics` — "Graph shape" panel + schema cache

**Arbitration.** Research and minimal-surface placed the panel on the Dashboard; IA placed
it first on the Analytics screen ("understand the shape, then compute over it" is one mental
activity). Decision: **Analytics screen** — it keeps the Dashboard from ballooning (it also
gains the stored-queries panel and interchange row) and pairs shape with compute. The
Dashboard remains the one home for memory numbers.

- **On demand only** — a "Compute" button with caption `full O(V+E) pass — sampled above 1M
  elements`. Never fetched on mount or polled: the endpoint is budgeted and rate-limited by
  design, and navigation must not become a DoS. `computedInMs` is always displayed.
- A sampled response shows one warning chip `sampled 1:n` whose tooltip carries the
  extrapolation caveat; nothing else re-explains sampling.
- Contents reuse existing atoms: `PluginList`-style top-N lists for vertex/edge labels and
  property keys, a plain table for the index inventory (each index row gets a **"scan"**
  action that navigates to Query with `kind: index` and the id pre-filled), and a compact
  monospace degree table (`in/out/total × min·mean·p50·p90·p99·max`). No chart library.
- **The load-bearing part — `useGraphShape` schema cache.** The last computed snapshot is
  cached in the per-instance store and feeds `<datalist>` suggestions to every free-form
  identifier input: Query's `propertyId`/`indexId`, the Analytics scope row, and the export
  label filter. Free-form input always still works; no snapshot = today's behaviour. This
  closes design gap G-3 without a single new screen.

**Not added:** auto-refresh; `/metrics`/`healthz`/`readyz` surfaces (continuous telemetry is
Prometheus/Grafana's job by the observability feature's own design); degree histograms.

## 5. Stored query library — source toggle + save-as + one manage panel

Unanimous, and the highest-priority correctness item. Two surfaces, matching the two jobs:
authoring where fragments can be tested, management where the operator lives. Mechanics and
the security matrix stay in
[features/done/stored-query-library/](../../done/stored-query-library/).

**5.1 On Path and Subgraph — the source toggle.** Above the delegate slots:

```
filters   [ inline ] [ stored ]
```

- *inline* (default): today's `DelegateSlot`s, unchanged.
- *stored*: slots collapse; a `<select>` lists stored queries **of the matching kind only**,
  `Failed` entries disabled with a pointer to the Dashboard panel's diagnostics. Below the
  select, the selection's fragments render in read-only `DelegateSlot` chrome (Edit/Clear
  hidden) — you see what will run; entries are immutable by contract, and the UI does not
  pretend otherwise. Numeric bounds (`maxDepth`, `maxResults`, algorithm, subgraph `name`)
  stay editable — they are per-request by design.
- The spec builder sends `storedQuery: <name>` and omits filter/cost fields; the API's
  400-on-mix is structurally unreachable. Drafts gain `filterSource` + `storedQuery`,
  per-instance like every other draft field.

**5.2 The capture moment — "Save as stored query…"** In inline mode, beside the slots: a
small dialog (name with the pattern hint, optional description) POSTs the current committed,
already-validated fragments, then flips the toggle to *stored* with the new entry selected.
A 403 renders as exactly one sentence: `registration requires EnableDynamicCodeExecution on
this instance — invoking stored queries does not`. That sentence is the UI's entire
retelling of the security model.

**5.3 On the Dashboard — panel "Stored queries".** Table (`name · kind · state ·
registered`); rows expand to read-only fragment source, `Failed` rows show recompile
diagnostics in the `ErrorBox` idiom. Row actions: **"Open in Path"** / **"Open in
Subgraph"** (sets the target draft to stored + name and navigates) and **"Delete"** behind
`ConfirmDialog` (`Entries are immutable — to change one, delete and re-register`). **No
create button here** — registration without a place to test fragments is a worse flow than
the one Path/Subgraph provide; one authoring home.

**Not added** (unanimous): edit-in-place; a library rail item or screen with its own Monaco
editors (a second home for the Path/Subgraph editors — exactly the duplication this repo
bans); tagging/versioning.

## 6. Vector index — sixth scan kind on Query

Unanimous on the scan; split on the add form (research + IA for a minimal one,
minimal-surface against). Decision: keep the add, collapsed and small — property mode is how
existing property vectors get indexed after the fact and requires typing zero floats.

- `ScanKind` gains `"vector"` → option **"vector (kNN)"**. Conditional fields: index id
  (datalist-fed), `k` (default 10, hint `1–1024`), query vector as a paste textarea (JSON
  array or comma-separated) with a live `d=384` dimension counter so mismatches are caught
  before the 400, optional kind/label constraints with caption `constraints apply before
  top-k`.
- Results hydrate into `ElementTable` with the shared score column (§9); the header shows
  the legend straight from the response (`cosine · higher is better` / `L2 · lower is
  better`) — never re-derived client-side.
- Index management panel: creating a `VectorIndex` reveals `dimension` (required) and
  `metric` (Cosine / DotProduct / L2) fields that travel as `pluginOptions` — no generic
  per-plugin options form.
- Collapsed sub-section **"Vector add"** (single element): index, element id, and a
  `[ property | explicit ]` toggle. Property mode is the default with caption `property mode
  is WAL-recoverable — the honest default`; explicit mode swaps in the vector textarea.

**Not added** (unanimous): any embedding-generation affordance (the database never calls
embedding models; neither does the Studio — query vectors are pasted); bulk file-of-vectors
upload (no server endpoint; N client-side PUTs pretending to be atomic); t-SNE/embedding
visualisation; a GraphRAG walkthrough surface (the recipe is README content — the pieces
already compose: vector scan → canvas → expand → Path).

## 7. Bulk import/export — one row in Administration

**Arbitration.** Export was unanimous; the minimal-surface lens argued import should stay
curl-only, research and IA argued the fresh-instance moment ("get *my* graph in here" — the
only current offer is demo data) decides whether the Studio ever handles real data.
Decision: **include import**, at the minimal-surface lens's size — a bare file picker whose
error handling relays the server's words and adds nothing.

A second row inside the existing Administration panel, `border-t`-separated, captioned
`interchange (jsonl)` so checkpoints and interchange never read as the same thing:

- **"Export .jsonl"** — blob download of `GET /bulk/export` (auth headers respected). A
  collapsed `filter by label` disclosure reveals vertex/edge label inputs (datalist-fed)
  mapping to the query params. Tooltip: `internally consistent interchange — not a
  crash-consistent backup; use save games for point-in-time`.
- **"Import .jsonl…"** — file picker → POST. No ConfirmDialog: import into a non-empty graph
  is a server-enforced 409 (rendered as `target graph is not empty — Tabula rasa first, or
  import into a fresh instance`), and import into an empty graph destroys nothing. Success
  shows `verticesCreated / edgesCreated / linesRead` as stat cells; failure shows the
  problem+json `lineNumber` and committed-batch counts verbatim in `ErrorBox`, with the
  server's recovery line quoted, not paraphrased. Above some tens of MB, warn and point to
  curl (browser memory, no resumability).

**Not added** (unanimous): an import wizard (drag-and-drop, field mapping, dry-run preview —
the format is a fixed contract, there is nothing to map); progress bars over an opaque
single POST; any coupling to Save games (different guarantees).

## 8. Change feed inspector — deferred (non-goal for now)

The panel split three ways: a drawer behind the live chip (research), a Dashboard tail
console (IA), no UI at all (minimal-surface). Decision: **defer** — the value rating was the
lowest of the six (developer-debug only), the curl story (`curl -N`) is documented and
excellent, and the feed already does its Studio job invisibly (canvas liveness). A
capability whose strongest argument is "the socket already exists" does not clear the bar.

**Revisit triggers** (any one): debugging application writes via `curl -N` proves to be
recurring pain rather than occasional; a second in-Studio consumer of the feed appears; or
watching the feed while on Canvas becomes a demonstrated want. **Recorded shape for that
day** (IA design, adopted as-is): a Dashboard panel — filter row mapping 1:1 to the server
grammar, Tail/Stop toggle opening a second filtered `streamChanges` stream, rolling
500-event monospace tail with `resync` rows full-width in warning tone showing their
`reason`, element ids linking into Browser, re-tail pre-filling `since` with the last seen
`seq` so pause/resume exercises the documented catch-up contract instead of buffering
client-side. Explicitly rejected even then: retention, search, export (the buffer is
deliberately bounded and lossy; a "log" UI would be fiction), and making the LiveChip a
secret button.

## 9. Cross-cutting work

- **`ElementTable` score column** — one optional `scores?: Map<id, number>` prop, built
  once, used by the vector scan and analytics top-K. Whichever phase lands first builds it.
- **`useGraphShape` schema cache** — §4; the connective tissue that upgrades every free-form
  identifier input in the Studio.
- **Fix the Dashboard "NaN MiB" tile** — root cause confirmed: the hand-written client
  `StatusREST` type declares `freeMemory`, but the server's `StatusREST` never serializes
  such a field, so the tile renders `undefined / 1024 / 1024`. Fix: delete the phantom field
  and the tile (the honest memory numbers live in `/statistics`, §4); `usedMemory` stays.

## 10. Non-goals (whole concept)

- **New rail items beyond Analytics.** Stored queries, bulk IO, and the feed are panels or
  affordances, not destinations. Revisit only if one demonstrably outgrows its panel.
- **Any surface that duplicates an explanation.** Every caveat above is a one-line pointer;
  the owning feature README keeps the narrative.
- **Endpoint-mirror screens** (`/metrics` viewer, health screen, storedquery browser).
- **Client-side re-validation of server contracts.** Toggles make invalid combinations
  unreachable; where the server enforces (409, 403), the UI relays the server's message.
