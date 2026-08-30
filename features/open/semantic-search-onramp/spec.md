# Semantic search on-ramp: specification

> **Status:** open (spec + plan, not yet implemented). Branch `feature/semantic-search-onramp`.
>
> **Builds on:** [studio-semantics](../../done/studio-semantics/) (the text-in search box and
> provider gating), [element-similarity-search](../../done/element-similarity-search/) (find
> similar, index-form prefill, empty-index honesty) and
> [vector-index](../../done/vector-index/) / [element-embeddings](../../done/element-embeddings/)
> (bound indices as self-maintaining projections). Server side is complete; this feature
> changes F8 Studio only.

## 1. Problem

Text-in semantic search shipped, and nobody can find it. The evidence is first-hand: an
operator ran an AUTOSAR integration that embedded many entity summaries, opened the
Indexes screen, saw two claim indexes and no vector index, and concluded the capability
does not exist. They then proposed building it.

Two distinct defects produce that experience:

1. **Text-in search is buried.** It exists only as a `vector | text (provider)` source
   toggle inside the Query screen's *index* mode, which renders only after the user has
   selected an existing `VectorIndex` from the inventory
   (`fallen-8-web-ui/src/screens/QueryScreen.tsx`). A user must already understand the
   index model to discover the feature whose point is that you do not need to know the
   data model to search.
2. **"Embeddings present, index absent" is a silent dead end.** A bound `VectorIndex` is
   a one-call, instantly-materialising projection over embeddings that already exist, so
   the distance from that state to working text search is one create. The UI says nothing.
   The docs handle the adjacent failure as a *troubleshooting entry* ("Semantic search
   succeeds but finds nothing", `docs/src/content/docs/troubleshooting.md`) - the classic
   sign of a UI that should have said it in place.

Deliberately NOT the problem: the index requirement itself. An index-free scan endpoint
would be a second query path over the same element state with the same answers, against
the one-home rule, to save one instant create call. Rejected; revisit trigger in §4.

## 2. Contract

### FR-1 A `semantic` query mode

The Query screen's mode row gains a first-class **semantic** mode, sibling of the
existing modes, visible the moment the screen opens:

- Inputs: query text, `k` (1-1024, default 10), element kind (`any | vertex | edge`),
  optional label constraint (shape-labels datalist) - exactly the
  `EmbeddingSearchSpecification` surface. Calls `POST /embedding/search`. No new REST.
- Index selection: a selector listing only the vector indexes from the inventory, bound
  ones labelled `bound:<embeddingName>` (same source as the Indexes screen badge). When
  exactly one vector index exists it is preselected; the selector never blocks a user who
  has only one sensible choice.
- Results reuse the existing scored-element rendering (hydration cap, `ElementTable` with
  score column, metric legend, "Send all to canvas") - no second result surface.
- Provider gating keeps the shipped idiom (`providerEnabled !== true` disables with the
  distinguished null/false sentence). The mode is always *visible*; it is never hidden,
  only disabled with the stated reason, consistent with every other embedding affordance.
- Empty-bound-index honesty (element-similarity-search FR-8) renders here unchanged.

### FR-2 Text-in moves; vector-paste stays (one home each)

The `text (provider)` source toggle is REMOVED from the index-mode vector form; semantic
mode is text-in's one home. Vector-paste (`POST /scan/index/vector`) remains the index
mode's vector form unchanged - it is the bring-your-own-vector and find-similar surface
(the `ScanPrefill` hand-off from EmbeddingsTab / canvas detail keeps landing there,
untouched). Persisted `QueryDraft` state migrates: a stored draft with
`vectorSource: "text"` re-opens as the semantic mode with its text, k, kind and label
carried over, so nobody's saved work is dropped by the move.

### FR-3 The on-ramp empty state

When semantic mode is entered and the inventory holds **zero vector indexes**, the form
area renders an on-ramp instead of a dead selector:

- One sentence of truth: semantic search ranks against a vector index bound to a named
  embedding, and none exists yet in this namespace.
- An inline create: embedding name (default `default`; datalist of names from the graph
  shape snapshot when one exists, free-form always allowed), dimension and metric
  prefilled from the provider identity (the element-similarity-search FR-5 prefill,
  reused, not duplicated - extract the shared piece if needed), index id prefilled with
  something honest like `embeddings`. Submits the same `POST /index` create the Indexes
  screen uses.
- On success: the inventory refetches, the new index is selected, the search form appears.
  The user typed a query and clicked create; nothing else.
- Gating: the inline create renders only when `providerEnabled === true` (text-in search
  is 403 without the provider, so offering the create would build a dead index for this
  mode); otherwise the standard provider-off sentence is the whole empty state.
- The server's 400/409 on create render verbatim, as everywhere else.

### FR-4 The Indexes screen names the concept

When the index inventory contains no `VectorIndex`, the Indexes screen carries one line
under the table: text-in semantic search needs a vector index bound to an embedding name,
with the create form below already able to make one. No new form, no duplicate create -
one sentence that turns the dead end the operator actually hit into a pointer.

### FR-5 Docs follow the UI

- `docs/src/content/docs/studio.md`: the Query section describes the semantic mode and
  the on-ramp; the index-mode description drops the text toggle.
- `docs/src/content/docs/troubleshooting.md` "finds nothing": add the missing first rung
  (no vector index at all -> the UI now offers the create in place) and keep the rest.
- `docs/src/content/docs/vector-search.md` and `samples.md` walkthroughs: re-point the
  "semantic search" steps at the semantic mode.
- Screenshots: any capture showing the Query screen's vector/text form or the Indexes
  screen empty state is recaptured (`query-semantic-search.png` at minimum; sweep
  `docs/src/assets/images/` for others replaying these screens).

## 3. Impact on existing features (mandatory sweep)

- **Engine / REST / OpenAPI snapshot:** untouched. No new route, no DTO change. The
  OpenAPI snapshot must show no diff; that is a gate, not a hope.
- **MCP:** untouched; `f8_search mode=semantic` already bridges `POST /embedding/search`.
  No new coverage entry needed.
- **studio-semantics (done):** its shipped placement (text toggle inside index mode)
  is superseded by FR-1/FR-2. Historical spec is not rewritten; this spec is the living
  record of where text-in search lives now.
- **element-similarity-search (done):** find-similar keeps prefilling the index-mode
  vector form (explicit vector); unaffected. Its FR-5 prefill and FR-8 empty-index
  honesty are reused by FR-3/FR-1.
- **NL-assist dataset / eval:** untouched (no fragment or prompt contract change). No
  RETRAIN-LOG entry needed.
- **Persisted state:** `QueryDraft` gains the semantic mode and migrates the removed
  `vectorSource: "text"` state (FR-2); `ScanPrefill` contract unchanged.
- **Docs site + screenshots:** FR-5. Architecture diagrams: unaffected (no new channel
  or deployable). Root README: no new key-feature bullet - this makes an existing
  documented capability reachable; the existing bullets and pages already claim it.

## 4. Non-goals, each with its revisit trigger

- **No index-free scan endpoint** ("search directly on nodes/edges"). The bound index is
  the projection; creating it is instant and the on-ramp makes that one click. Revisit if
  a read-only persona (no index-create permission) demonstrably needs text search in a
  namespace where no operator provisioned an index.
- **No NL-to-query translation** (interpreting "red bicycles owned by Anna" into
  constraints). That is NL-assist territory with its own eval loop; the deferral recorded
  in studio-semantics §5 stands.
- **No global omnibox / cross-screen search bar.** One searchable surface done well
  first; revisit if usage shows people searching from other screens.
- **No document-search changes.** The Knowledge screen's chunk search is a different,
  already-discoverable surface over `POST /document/search`.
- **No `minScore` on semantic mode.** `POST /embedding/search` has no threshold
  parameter; adding one is a REST change out of scope here. Revisit with real demand,
  as its own small feature (it would then sweep MCP coverage too).

## 5. Acceptance sketch

- Fresh namespace, provider on, embeddings written by an integration run, zero vector
  indexes: open Query -> semantic mode is visible -> the on-ramp states why there are no
  hits possible yet -> one create -> type text -> ranked hits with metric legend. No
  detour through the Indexes screen, no knowledge of dimensions required.
- Provider off (bare `dotnet run`): semantic mode visible, disabled, with the shipped
  provider-off sentence; no create affordance; index-mode vector-paste unaffected.
- Existing persisted draft with `vectorSource: "text"` re-opens in semantic mode with
  text/k/kind/label intact; find-similar from an element still lands in index mode with
  the vector prefilled and the source element excluded.
- Indexes screen with no vector index shows the one-line pointer; with one, it does not.
- Vitest coverage in the existing patterns (`tests/embedding-query.test.tsx`,
  `tests/query-scans.test.tsx`, `tests/index-management.test.tsx`) for: mode visibility,
  provider gating (null vs false), on-ramp render conditions, create-then-search flow,
  draft migration, prefill hand-off unchanged.
