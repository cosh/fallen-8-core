# Element similarity search: specification

> **Status:** implemented and merged (2026-08-25) on branch `feature/element-similarity-search`.
> The investigation this rests on is [findings.md](findings.md) and is not repeated here;
> every code citation in this document was verified there.

## 1. Overview

An operator looking at a graph element in F8 Studio can search for elements that *mean*
something similar, instead of matching substrings against property values.

The whole embedding stack that makes this possible already ships. The embedding is element state
behind one accessor, a bound `VectorIndex` is a self-maintaining projection that materialises
itself over already-embedded elements, the integrations runtime already renders a provider's
`entitySummaryTemplate` and posts it, and text-in kNN already works from REST, the Studio Query
screen and MCP. This feature does not add a capability to that stack. It makes the stack
**reachable**, and fixes the one defect that stops it working at real size.

Three things are wrong today, in descending order of how much they cost:

1. The summary embedding write is unchunked, so any extract over 64 entities fails the run.
2. F8 Studio can never ask an integration run to embed its summaries at all.
3. "Find elements similar to this element" exists nowhere. Similarity is reachable only by
   typing words, never by pointing at a thing.

### FR summary

- **FR-1 Chunked summary write.** `Fallen8RestTarget.EmbedSummariesAsync` sends the summaries in
  chunks of at most **32** items - the smallest cap the product ships, not the 64 the apiApp
  defaults to, because `docker-compose.nahil.yml` sets 32 - summing the written count across
  chunks. A chunk that fails follows the existing rule, widened by one status: `{403, 429, 502,
  503}` degrade the write to absent with a diagnostic, anything else is a graph failure. 429 is
  in that set *because* of chunking: the route carries the sensitive-endpoint rate limit, so many
  chunks can trip a throttle one unchunked request never could. Partial success is reported
  honestly (see §4).
- **FR-2 Studio embed opt-in.** The Integrations run form carries an "embed entity summaries"
  checkbox and an optional embedding-name input, both written into the job request.
  `IntegrationJobRequest` gains `embeddingName?: string`. The checkbox is disabled, with the
  existing provider-off sentence as its title, when the embedding provider is not enabled.
- **FR-3 Template disclosure.** When a selected provider declares an `entitySummaryTemplate`
  (already on the wire), the run form shows it beside the checkbox, so what will be embedded is
  visible before the run rather than inferred after it.
- **FR-4 Honest embedded tile.** The run report's summaries-embedded tile renders a count only
  when the run asked for summaries, and "not requested" otherwise.
- **FR-5 Index form prefill.** The create-index form prefills dimension and metric from
  `status.embedding` when a provider is reported, falling back to today's constants when it is
  not, and names the provider identity beside the fields.
- **FR-6 Find similar.** A "find similar" action on an element, in the Browser Embeddings tab
  and the canvas Detail panel, reads that element's stored vector, carries it plus the source
  element's label to the Query screen's vector form, and runs kNN there. It requests `k+1` and
  removes the source element from the rendered hits.
- **FR-7 Label inheritance.** The gesture constrains the search to the source element's label by
  default, visibly and reversibly. This is not a convenience: three of seven ARXML entity kinds
  embed as bare identifiers (findings.md §"Summary text quality"), so an unconstrained
  similarity search over an ARXML graph returns identifier-shaped noise for about a third of the
  corpus.
- **FR-8 Empty-index honesty.** When the selected vector index reports zero members, the Query
  screen says so beside the Run button and renders the index's `bound:<embeddingName>`, instead
  of letting an empty 200 read as "nothing is similar".
- **FR-9 Provider-off hints.** The Indexes and Integrations screens carry the same provider-off
  sentence the five other screens already carry.
- **FR-10 MCP test.** `f8_search` modes `vector` and `semantic`, and `f8_mutate set_embedding`,
  gain coverage against the real-server fixture. No new MCP tool and no new deferral.

## 2. Goals and non-goals

**Goals** FR-1 to FR-10. No engine file changes, no new REST route, no changed DTO, so the
OpenAPI snapshot, the provider-descriptor snapshot, the MCP coverage gate and the browser probe
are all untouched.

**Non-goals**, each with its revisit trigger:

- **No server-side element-as-query mode.** `/scan/index/vector` keeps taking a vector and
  `/embedding/search` keeps taking text. FR-6 is a client gesture over the existing surface.
  *Consequence, stated plainly: the gesture is Studio-only and invisible to agents and to any
  non-Studio client.* Revisit when an agent workflow needs "similar to this element" without a
  browser, or when a second client wants it.
- **No engine-level self-exclusion.** `VectorSearchConstraint` keeps `{Kind, Label}`. FR-6 drops
  the source element client-side. Revisit only together with the previous item, because an
  `ExcludeElementId` with no server-side element-as-query mode has no caller.
- **No backfill of an already-imported graph.** `summaryDirty` stays what it is, so the
  zero-mutation invariant is untouched. Recovery is `HEAD /ns/<name>/tabularasa` on that
  namespace then re-run (the route is HEAD, not PUT), said plainly in the checkbox's help text.
  Clearing drops index definitions as well, so the bound index is recreated afterwards. Revisit when a graph is too expensive to
  re-import, at which point the shape is an explicit opt-in `reembedAll` job field defaulting
  off.
- **No per-kind or job-overridable summary template.** The ARXML template stays the single
  four-hole string, so `network`, `ecu` and `frame` stay weak for similarity and "find other
  32-bit unsigned counters like this" stays unexpressible. Revisit when someone actually wants
  structural similarity; note the template string is pinned by two tests and the descriptor
  snapshot, so it is a deliberate change with gate cost, not a tweak.
- **No score threshold and no per-hit reason text.** `k` stays the only knob. The hit table
  already shows the raw score under a metric-named header with a direction legend, and the
  hydrated element already carries its properties, so a reason column is composable client-side
  if it is ever wanted. Revisit if `k`-only truncation is reported hiding good matches.
- **No ANN index.** Unchanged from vector-index: exact SIMD brute force. The recorded extract is
  many entities, and the revisit trigger stays ~1M vectors or a measured p99 above ~100 ms.
- **No change to identity.** Similarity is not, and never becomes, an input to identity
  resolution. This feature makes similar elements *findable*; deciding two elements are the same
  thing remains outside the runtime. See §5.

## 3. The embedding name

An ARXML run writes to `default`, which is both the runtime default and the name the
document/knowledge layer binds its `documents` index to. So out of the box, integration summaries
and document chunks share one bound index and answer the same searches.

This is accepted deliberately rather than overlooked. One search over everything is the more
useful default for a single-operator graph, and FR-7's label constraint is what keeps a signal
search from being polluted by document chunks. **Revisit trigger:** a signal search observed
ranking a document chunk above a signal, at which point the Studio field already exists (FR-2)
and the fix is a dedicated name plus a second index, with no code change.

## 4. Partial failure of the summary write

Chunking introduces a state that could not previously exist: chunk 3 of 20 fails. The rule is
that an embedding is an addition to what landed and never a precondition for it, so:

- A `{403, 502, 503}` on any chunk stops the write and degrades the whole thing to absent, with
  the existing `summaryEmbeddingUnavailable` diagnostic naming the status. Chunks already
  written stay written; they are element state and are valid.
- Any other status stays a graph failure, as today.
- `report.SummariesEmbedded` counts what was actually written, not what was attempted. A number
  lower than the dirty count with a degraded diagnostic beside it is the honest report of a
  partial write, and is preferred to reporting zero for work that did happen.

## 5. Impact on existing features

Verified sweep; the detail and citations are in [findings.md](findings.md) §"Gate impact".

| Area | Impact |
| --- | --- |
| Engine (`fallen-8-core`) | None. No file changes, so the browser probe is not implicated. |
| REST contract / OpenAPI snapshot | None. No new route, no changed DTO. |
| Provider-descriptor snapshot | None. `embedSummaries` and `embeddingName` are job fields, not descriptor settings, and `entitySummaryTemplate` already exists and is already in the field allowlist. |
| MCP | No new tool, no new deferral. FR-10 adds tests only. A trap is recorded in findings.md for whoever adds a future `/embedding/` or `/index` route: the deferral rules match by prefix, so a new route in either family is silently auto-deferred under a stale reason. |
| Integrations conformance / write path | FR-1 changes the number of write calls the in-memory target records, so `IntegrationsWritePathTest` needs its expectation checked. The zero-mutation invariant must still hold: a second run over an unchanged source issues zero writes. |
| Studio API-contract sweep | Two new fields on `IntegrationJobRequest` need no `ENDPOINT_CALLS` entry. No `/embedding/elements` client is added, so the "deliberately curl territory" policy stands. |
| Screenshots | The Integrations screen gains a checkbox and the canvas Detail panel gains a button, so `screen-integrations.png` is recaptured, and the Browser/canvas and semantic-search captures are reviewed. |
| Docs site | `integrations.md` (name both job fields and inline a working create-index body), `vector-search.mdx` (add `embeddingName` to the runnable example), `studio.md` (Embeddings tab is described as set/replace/remove only; Query and Indexes screens), `troubleshooting.md` (a row for "semantic search returns nothing"). |
| Root README | The key-features list gains its one-line entry linking the live page. |
| `features/done/autosar-arxml/spec.md` | Fix the contradiction: `:204` calls semantic search "a first-class requirement, not a nice-to-have" while the impact table at `:285` records "F8 Studio, zero code change". The Studio gap is the direct consequence of that line. |
| NL-assist dataset / eval | No contract change, so no retrain entry. |
| Identity / multi-file ARXML | Untouched by design. The separate question of ingesting several extracts is recorded at the end of findings.md and is not part of this feature. |

## 6. Acceptance

1. An ARXML run of the full recorded extract (many entities) with `embedSummaries` on completes
   without a graph failure and reports a non-zero `summariesEmbedded` equal to its dirty count.
2. The same run is launchable entirely from F8 Studio, with no curl and no config edit beyond
   having the provider on.
3. A bound vector index created from the Studio Indexes form, accepting the prefilled values
   against the compose provider, reports a non-zero member count.
4. From the canvas Detail panel of signal `Odo_ST3`, "find similar" returns other odometer-like
   signals, does not return `Odo_ST3` itself, and is constrained to `signal` by default.
5. Selecting a vector index with zero members and pressing Run produces the "no members yet"
   message rather than an empty result table.
6. `dotnet test` and the Studio test suites are green, and the docs site builds with no broken
   internal link.
