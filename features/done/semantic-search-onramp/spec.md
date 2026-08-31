# Semantic search on-ramp: specification

> **Status:** implemented and merged from `feature/semantic-search-onramp`. Studio-only: no .NET
> file changed, so the OpenAPI snapshot, the MCP coverage gate, the provider-descriptor snapshot
> and the browser probe are untouched by construction. Contract points refined during
> implementation are marked *(refined)* inline; §6 records what the adversarial review changed
> and [plan.md](./plan.md) carries the phase record, the gate results and the council outcome.
>
> **Builds on:** [studio-semantics](../studio-semantics/) (the text-in search box and
> provider gating), [element-similarity-search](../element-similarity-search/) (find
> similar, index-form prefill, empty-index honesty) and
> [vector-index](../vector-index/) / [element-embeddings](../element-embeddings/)
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

The Query screen's query-type control gains a first-class **semantic** mode, sibling of the
existing modes. *(Refined: that control is a `<select>`, not a segmented button row, so the
mode is a third option rather than a third button. Converting it to buttons would have broken
twelve existing `selectOptions` call sites across three test files and a capture spec for no
gain in reachability, since the control itself is visible on open.)*

- Inputs: query text, `k` (1-1024, default 10), element kind (`any | vertex | edge`),
  optional label constraint (shape-labels datalist) - exactly the
  `EmbeddingSearchSpecification` surface. Calls `POST /embedding/search`. No new REST.
- Index selection: a selector listing only the vector indexes from the inventory, bound
  ones labelled `bound:<embeddingName>` (same source as the Indexes screen badge). When
  exactly one vector index exists it is preselected; the selector never blocks a user who
  has only one sensible choice. The stored pick is normalized rather than trusted: one that no
  longer names a vector index (deleted meanwhile, or carried from another instance) reads as
  "nothing picked yet" instead of being sent to be rejected. A server whose `/status` predates
  the inventory field gets a free-form id instead: it is not entitled to claim which indexes
  exist. See §6 for why the pick is its own field.
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
untouched: a prefill always carries a vector, so there is nothing to type). Persisted
`QueryDraft` state migrates: a stored draft with `vectorSource: "text"` re-opens as the
semantic mode with its text, k, kind and label carried over, so nobody's saved work is
dropped by the move. *(Refined: only when that form was the one actually on screen, i.e. the
draft also had `mode === "index"` and `form === "vector"`. A stale `"text"` left behind while
the operator had moved on to a property scan is not a request to reopen a semantic search, so
such a draft is left exactly where it was.)*

*(Refined, two points. First, the persisted store has no `version`/`migrate` pair; it
deep-merges through a `merge` function whose only precedent for a restructure is "reset rather
than migrate". The migration therefore lives in one exported, unit-tested function,
`migrateQueryDraft`, called from `merge`; it also normalizes a `mode` this build does not know,
which widening the union made worth guarding. Second, `k`, `kind` and `label` are SHARED with the
vector form rather than duplicated as `semantic*` fields: they are the same kNN parameters
whatever produced the query vector, and the JSX for the three is one component used by both. The
INDEX is deliberately NOT shared, for the reason recorded in §6. The visible consequence of the
sharing, stated plainly: a find-similar gesture, which sets kind and label from its source
element on purpose, also changes what the semantic mode will ask next. That is coherent with the
gesture's meaning and is the price of not keeping two copies of one question.)*

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
- *(Added during review: the on-ramp also requires that `/status` has actually ARRIVED. The
  screen's existing `inventoryKnown` flag answers the old-server question ("does this contract
  carry the field at all") and is deliberately true before the request lands, so keying the
  on-ramp on it alone made the mode assert "this instance has none yet" - and offer to create -
  while the request was still in flight, and permanently against an unreachable or unauthorized
  instance. That is the same false certainty this mode exists to remove, one level down. An
  un-arrived inventory now renders neither the on-ramp nor a claim: the picker says it has
  nothing to offer yet and points at the header's connection state. Found by an executable
  probe during the adversarial review pass, and pinned by two tests.)*

### FR-4 The Indexes screen names the concept

When the index inventory contains no `VectorIndex`, the Indexes screen carries one line
under the table: text-in semantic search needs a vector index bound to an embedding name,
with the create form below already able to make one. No new form, no duplicate create -
one sentence that turns the dead end the operator actually hit into a pointer.

*(Refined: it also requires a NON-EMPTY inventory. On an instance with no indexes at all the
paragraph above already says "create one below", so the pointer restated it in the first state a
newcomer sees; the dead end it exists for is other families present and this one absent, which
is the state the operator was actually in. See §6.10.)*

### FR-5 Docs follow the UI

- `docs/src/content/docs/studio.md`: the Query section describes the semantic mode and
  the on-ramp; the index-mode description drops the text toggle.
- `docs/src/content/docs/troubleshooting.md` "finds nothing": add the missing first rung
  (no vector index at all -> the UI now offers the create in place) and keep the rest.
- `docs/src/content/docs/vector-search.md` and `samples.md` walkthroughs: re-point the
  "semantic search" steps at the semantic mode.
- Screenshots: any capture showing the Query screen's vector/text form or the Indexes
  screen empty state is recaptured.

*(Refined: the sweep was bigger than "at minimum `query-semantic-search.png`". Three frames are
affected, two of which the draft did not anticipate, and one that it did could not be produced
here. The three were found by recapturing every candidate and letting `git status` report which
came back byte-identical, rather than by guessing.*
- *`screen-query.png` changes even though it photographs the PROPERTY mode: the query-type
  `<select>` is `w-auto`, so a third option whose label is longer than both existing ones widens
  the control and shifts the row beside it. **Recaptured.** Its committed predecessor also
  contradicted its own spec by being shot against a graph with 117 vertices where the comment
  says "empty graph on purpose"; the new frame is empty as intended.*
- *`screen-indexes.png` is NOT recaptured, and the FR-4 pointer is consequently **unphotographed**
  anywhere on the docs site. That spec captures an inventory left deliberately empty, and the
  refinement above means the pointer does not render there. Its capture spec was still hardened:
  it waited for the plugin-type control to be VISIBLE, which the free-form fallback satisfies
  before `/status` answers, so a frame could be shot with the header reading "checking" and every
  status-derived line absent; it now waits for the control to be a `SELECT`. A recapture under
  that fix came back visually identical to the committed frame (119 bytes of antialiasing), which
  is why the image is left alone: the hardening is preventative, not a repair. Photographing the
  pointer needs a capture with one non-vector index present, listed as a follow-up.*
- *`query-semantic-search.png` showed the removed toggle. **Recaptured** against a real bge-m3
  provider, which the frame needs: Movie Night's plot vectors carry the stamp `bge-m3#1024#Cosine`,
  and a different model would rank a different film even though the index declares no model and so
  only checks the dimension. The weights were already in the `f8-ollama-models` volume from an
  earlier `env:up`, so serving them was a container start rather than a download; the apiApp ran
  natively against it with the compose environment's own `Fallen8__Embedding__*` values. The new
  frame is a better one than it replaces: it shows the mode, the `bound:default` picker, the
  provenance caption naming Ollama and bge-m3, and the full ranked table, with Inception first at
  0.6111 followed by Eternal Sunshine of the Spotless Mind, Arrival and Spirited Away. The capture
  spec's own assertion (top row id === 0) is what proves the ranking rather than the picture.)*

## 3. Impact on existing features (mandatory sweep)

- **Engine / REST / OpenAPI snapshot:** untouched. No new route, no DTO change. The
  OpenAPI snapshot must show no diff; that is a gate, not a hope.
- **MCP:** untouched; `f8_search mode=semantic` already bridges `POST /embedding/search`.
  No new coverage entry needed.
- **studio-semantics (done):** its shipped placement (text toggle inside index mode)
  is superseded by FR-1/FR-2. Its spec is a historical record and is not rewritten. This one is
  historical too, so it is not the place to look either: where text-in search lives now is
  documented in [`docs/src/content/docs/studio.md`](../../../docs/src/content/docs/studio.md)
  and in the living feature doc [index-workspace/README.md](../index-workspace/README.md).
- **element-similarity-search (done):** find-similar keeps prefilling the index-mode
  vector form (explicit vector); unaffected. Its FR-5 prefill and FR-8 empty-index
  honesty are reused by FR-3/FR-1.
- **NL-assist dataset / eval:** untouched (no fragment or prompt contract change). No
  RETRAIN-LOG entry needed.
- **Persisted state:** `QueryDraft` gains the semantic mode and migrates the removed
  `vectorSource: "text"` state (FR-2); `ScanPrefill` contract unchanged.
- **Docs site + screenshots:** FR-5. Architecture diagrams: unaffected (no new channel
  or deployable). Root README: *(refined - the draft said no new bullet was needed because the
  capability was already documented. Reading the actual "Key features" list disproved that: it
  claimed "Vector search" (kNN over `float[]`, which reads as bring-your-own-vector) and "Find
  similar" (element as query), and nowhere "type words, get ranked elements". The very gap this
  feature exists to close was present in the README too, so text-in semantic search now earns
  its own one-line entry, per the repo rule that every user-facing key feature does.)*

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
- Provider off (bare `dotnet run`): semantic mode visible, its query text disabled with the
  reason; no create affordance; index-mode vector-paste unaffected.
- Existing persisted draft with `vectorSource: "text"` re-opens in semantic mode with
  text/k/kind/label intact; find-similar from an element still lands in index mode with
  the vector prefilled and the source element excluded.
- Indexes screen with indexes but no vector index shows the one-line pointer; with a vector
  index, or with no indexes at all, it does not; and on an instance whose `/status` has not
  answered, neither screen claims anything either way.
- Vitest coverage in the existing patterns (`tests/embedding-query.test.tsx`,
  `tests/query-scans.test.tsx`, `tests/index-management.test.tsx`,
  `tests/instance-isolation.test.ts`) for: mode visibility, provider gating (null vs false vs
  unanswered), on-ramp render conditions, create-then-search flow, draft migration, prefill
  hand-off unchanged, and every item in §6.

## 6. What the adversarial review changed

The implementation was reviewed by five independent lenses (correctness, regression, UI honesty,
duplication, coverage), each finding then handed to a skeptic told to refute it. Every item below
was CONFIRMED against the code, most of them by an executable probe rather than an argument, and
every one is fixed and pinned by a test. They are recorded here because several are the feature's
own thesis turned back on it: this mode exists to delete a UI that claimed something it could not
support, and the first draft did exactly that in four new places.

1. **The on-ramp claimed "this instance has none" from no evidence.** The screen's `inventoryKnown`
   flag answers "does this contract carry the field at all" and is deliberately true before
   `/status` lands, so keying the on-ramp on it made the mode assert there was no vector index, and
   offer to build one, while the request was in flight, and permanently against an unreachable or
   unauthorized instance. Now gated on the inventory having actually ARRIVED; an un-arrived one
   renders neither claim nor create.
2. **The same for the provider.** "The provider is off" is a fact from a provider block; "this
   server does not report one" is only a fact once a `/status` has come back without one. The
   sentence no longer describes an unreachable network as a configuration choice.
3. **The semantic pick shared `indexId` with the index mode**, so choosing a vector index there
   replaced the operator's index-mode selection AND silently reset its query form (the
   capabilities effect follows the index). It has its own draft field now. The on-ramp names the
   index it created rather than relying on the single-index preselect, which only holds while
   exactly one vector index exists.
4. **A find-similar exclusion followed the operator into the semantic mode**, where it dropped a
   hit from a TEXT search, spent one of their k on the over-fetch, and explained itself with a chip
   about a vector that query never had. The exclusion is now scoped to the vector form the gesture
   lands in, and is not cleared, so returning finds it intact.
5. **The on-ramp's provider note attributed the operator's own edits to the provider**, because the
   prose was copied while only the numbers were shared. It prints the provider's values, and uses
   the shared helper's `providerReady` flag to say "these are defaults" when an enabled provider
   reports no dimension - a reachable half-configuration that would otherwise have produced a
   confidently wrong 384-dimension index.
6. **An emptied dimension was submitted**, travelling as `""` against `System.Int32`. The check
   went into the shared helper, so the Indexes screen's create panel (which had the same hole) is
   fixed by the same line.
7. **An emptied k was submitted** as `k: 0`, which the engine answers 400 for; with an exclusion
   active it was worse, since the slice then rendered a good answer as no hits. Both kNN surfaces
   now refuse it, with the reason beside the field rather than only in a disabled button.
8. **The refusal message renamed itself** to whatever the id field said afterwards, blaming an id
   the server never saw. It remembers the id that was refused.
9. **The "bind embedding" field reused the Indexes screen's help**, which ends "leave empty for a
   raw index" - true there, wrong here, where the create button is gated on it. Its own key now.
10. **Two "create one below" sentences** on a fresh instance: the pointer is now shown only where
    the dead end is, other families present and this one absent.
11. **Overclaims corrected.** "Only indexes that can rank a vector" is stronger than the code:
    `indexCapabilities` errs toward every family for an unknown plugin on a pre-capabilities
    server, so such an index IS offered. The help text and `studio.md` now say "reports the vector
    family", and that fallback has a test on both screens.
12. **Stale docs swept.** `features/done/index-workspace/README.md` is a designated LIVING doc and
    still said "two modes" with `/embedding/search` reachable from the index mode.
    `docs/src/content/docs/studio.md` contradicted itself (its screen table omitted the new mode)
    and did not mention the FR-4 pointer that the recaptured screenshot shows.
13. **`query-semantic-search.png` pictured the deleted toggle** directly under prose describing
    the new mode. It was dropped from `samples.md` at review time, on the rule that a screenshot
    contradicting its own caption is worse than none, and **restored once it was recaptured
    against a real bge-m3 provider** (see FR-5).

One flagged item was deliberately NOT changed: the placeholder `— pick a vector index —` keeps its
em dashes to match the `— pick an index —` two controls away. Both reviewers who raised it noted
that changing one without the other only creates inconsistency.

