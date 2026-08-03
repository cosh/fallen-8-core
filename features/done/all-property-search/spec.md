# All-property search - spec

## Problem

The Studio Query screen's **property scan** (and the engine/REST surface behind it) can only
scan **one named key at a time**: you type a property id (e.g. `age`), pick an operator, and
give a typed literal. [`GraphScan`](../../../fallen-8-core/Fallen8.Scan.cs) even throws when the
property id is blank, and the route carries the key as a required segment
(`POST /scan/graph/property/{propertyId}`).

That is the wrong tool for the newcomer's first question against an unfamiliar graph: *"does the
string `acme` appear anywhere in this data?"*. Answering it today means knowing every property
key up front and running one scan per key. There is no "search across all properties" affordance.

This is the exact follow-up the [`element-fulltext-match`](../../done/element-fulltext-match/spec.md)
feature anticipated and deferred: it lists **"a graph-wide fulltext scan (engine scan +
`POST /scan/graph/...` + MCP search mode)"** as a non-goal, with the revisit trigger *"a Studio
global search box"*. It also built the primitive this feature stands on:
`AGraphElementModel.AnyPropertyValueMatches(Func<string,bool>)`, an allocation-free walk over an
element's property values that already skips the reserved embedding entries. This feature is the
graph-wide, declarative surface over that idea, wired end to end (engine -> REST -> MCP ->
Studio).

## Decisions (locked with the requester)

| Decision | Choice | Note |
|---|---|---|
| Match rule | **Any property matches (OR)** | An element hits when at least one of its property values matches. |
| Match kind | **Contains only** | Substring match (`String.Contains`). No starts/ends/equals selector in v1. |
| Case | **Case-insensitive** | `StringComparison.OrdinalIgnoreCase`. Fixed, no toggle. |
| Value coverage | **All values, stringified** | Every non-reserved value is rendered to its invariant string form and tested, so `42` finds the int `42` and `2026` finds a date. Not string-values-only. |
| REST route | **`POST /scan/graph/properties`** (plural) | Additive sibling to the singular `.../property/{propertyId}`; singular = one key, plural = every key. Deliberately not `/scan/graph/fulltext`, which would collide conceptually with the indexed, scored `/scan/index/fulltext`. |
| Named-key path | **Untouched** | The operator + typed-literal comparators are not involved and are not changed; the contains behaviour lives only in the all-property path. |
| UI affordance | **Explicit scope toggle** | A `specific key | any property` segmented control in the property-scan row. |

This is deliberately a **cold, un-indexed, O(elements x properties) discovery scan** with no
score and no ranking. It is "good enough for initial discovery"; for hot or large-graph search,
an [index](../../done/index-workspace/README.md) is the right tool, and this page says so.

## Behaviour after the change

### 1. Engine

One new read method, alongside `GraphScan`, on the read surface
(`IFallen8Read` -> `AFallen8` -> `Fallen8` -> `AddressedFallen8`):

```csharp
Boolean GraphScanAllProperties(
    out List<AGraphElementModel> result,
    String searchTerm,
    String interestingLabel = null);
```

- Returns the live elements (in the same id-ordered, removed-filtered, label-filtered way as
  `GraphScan`) whose property set satisfies the match, and `true` when `result.Count > 0`. It
  reuses the existing parallel live-scan machinery (`FindElements(ElementSeeker, interestingLabel)`),
  so the label restrictor, the null/removed skip, and the snapshot discipline are identical to
  `GraphScan` for free.
- The per-element predicate is: **any non-reserved property value, rendered to its
  invariant-culture string form, contains `searchTerm` (OrdinalIgnoreCase).**
- A `null` or whitespace `searchTerm` returns an empty result and `false` (it never throws; the
  REST layer maps a blank term to a `400` before calling in, see below).

The value-rendering and reserved-key rules live in **one new internal member** on
`AGraphElementModel` (so property-store iteration stays encapsulated on the type that owns it,
exactly like `AnyPropertyValueMatches`):

```csharp
internal Boolean AnyStringifiedPropertyValueMatches(Func<String, Boolean> valuePredicate);
```

Semantics, kept honest:

- Walks the compact copy-on-write property store by reference (no `GetAllProperties()` snapshot),
  the same single-writer / lock-free-reader discipline as `TryGetProperty` and
  `AnyPropertyValueMatches`.
- **Reserved keys are skipped**: `$embedding:` and `$embeddingModel:` entries never reach the
  predicate (reusing `IsReservedPropertyId`), so an embedding's model stamp (a string such as
  `nomic-embed-text`) cannot false-positive a search for `text`. This is the same wall
  `element-fulltext-match` closed for the string-only member.
- **Value rendering**: a `null` value is skipped; an `IConvertible` value (string, the numeric
  types, `bool`, `DateTime`) is rendered with `CultureInfo.InvariantCulture` (aligning with the
  invariant ingest/format convention of [`property-ingestion-culture`](../../done/property-ingestion-culture/spec.md));
  a non-`IConvertible` value (a raw `float[]` / `byte[]` blob) is skipped rather than rendered to
  a noisy type name. A string value renders to itself, so this is a strict superset of the
  string-only walk.
- Distinct from the public `AnyPropertyValueMatches`: that one is string-values-only and is the
  pinned fragment-authoring primitive; this one stringifies and is engine-internal. Kept as two
  members so the delegate/NL surface is unchanged (no retrain), while the shared non-reserved
  walk is factored to avoid duplicating the loop.

### 2. REST

New action on `GraphController` (in `GraphController.Scan.cs`):

```
POST /scan/graph/properties
{
  "searchTerm": "acme",
  "label": "company",        // optional exact-match restrictor; omit to scan every label
  "resultType": "Both"       // "Vertices" | "Edges" | "Both"; default "Both"
}
=> 200 [ 12, 40, 41 ]        // JSON array of matching ids (empty when nothing matches)
=> 400                       // missing/blank searchTerm
```

- New request model `PropertySearchSpecification` (`Controllers/Model/`): `SearchTerm`
  (`[Required]`), optional `Label`, `ResultType` (default `Both`). It deliberately does **not**
  carry `operator`/`literal` (those belong to the named-key `ScanSpecification`).
- A missing body or a blank `SearchTerm` is a client error -> `400` via `ProblemResults.BadRequest`
  (consistent with [`api-error-contract`](../../done/api-error-contract/spec.md) E3), never a `500`.
- Reuses `CreateResult(elements, resultType)` for the id projection; the label restrictor is
  passed straight through to `GraphScanAllProperties` (engine-side `CheckLabel`, same exact-match
  semantics as `GraphScan` and the [`edge-type-vs-label`](../../done/edge-type-vs-label/spec.md) fix).
- Default `ResultType` is `Both` (discovery wants everything), unlike the named scan's
  `Vertices`. Contract note: `ResultTypeSpecification.Vertices` is the enum's ZERO value, so an
  omitted `resultType` in the JSON body deserializes to `Vertices` unless the model property is
  explicitly initialized to `Both` - the initializer is part of the contract, not a style choice.
- `PropertySearchSpecification` is registered in `AppJsonContext` (source-gen serialization) and
  the OpenAPI snapshot is regenerated.

### 3. MCP (engine -> REST -> MCP propagation rule)

A new REST operation must reach agents or be a reasoned deferral. It is bridged: `f8_search`
gains a mode **`properties`** (a graph-wide contains scan over every property value):

- New `mode` choice `properties`; it consumes `query` (the search term) and honours the existing
  `kind` (vertex/edge/any -> `resultType`) and `label` arguments; `operator`/`value`/`indexId`
  do not apply. The singular/plural pairing is deliberate and mnemonic - `property` = one named
  key with a typed operator, `properties` = every key by contains - and the two descriptions
  state the contrast explicitly so an agent cannot confuse them.
- Maps to `POST /scan/graph/properties` via a new bridge DTO (`PropertySearchRequest`).
- The tool description gains a one-line note distinguishing `property` (named key, typed
  operator) from `properties` (cold contains scan across all values). `McpBridgedEndpoints`,
  `McpContractTest`, and `McpRestCoverageTest` are updated so the coverage gate stays green.

### 4. Studio UI

In `QueryScreen`, the property-scan branch (`mode === "property"`) gains a scope toggle:

```
QUERY TYPE     SCOPE                    ...
[property v]   ( specific key )( any property )
```

- **specific key** (default): the current controls exactly as today (property id, operator,
  typed literal, result type). No behaviour change.
- **any property**: the property-id / operator / literal controls are replaced by a single
  **search term** text input plus the result-type selector and an optional **label** input
  (seeded from the `shape-labels` datalist). Running it calls a new
  `scanProperties(instance, { searchTerm, label?, resultType })` -> `POST /scan/graph/properties`,
  then hydrates the returned ids into the same result table (the 500-row hydration cap and
  "send to canvas" path are unchanged). Deliberate asymmetry, stated honestly: the named-key
  branch has never exposed the `label` restrictor in the UI (the wire supports it; the form does
  not send it), and this feature does not retrofit it there - discovery is where narrowing by
  label earns its input. Extending it to the named branch is a separate, trivial follow-up.
- The run button is disabled while the search term is blank (mirroring the existing "blank
  element id" guard), so an empty term never round-trips.
- Two new persisted `queryDraft` fields (`propertyScope`, `searchTerm`) join the per-instance
  store so leaving for the Canvas and returning restores the form, and `Clear` resets them
  (studio state persistence, feature [`index-workspace`](../../done/index-workspace/README.md)).
- Field help (`fieldHelp.ts`) gains entries for the scope toggle and the search term, stating
  plainly that it is a case-insensitive substring match across every property value, values are
  compared as text, and it is an un-indexed full-graph scan.

## Impact on existing features

| Feature / layer | Impact | Handling |
|---|---|---|
| [element-fulltext-match](../../done/element-fulltext-match/spec.md) | This is its deferred "graph-wide fulltext scan" non-goal, now triggered by the Studio search box | Reuse `AnyPropertyValueMatches`'s reserved-key rule and walk; add the stringified internal sibling next to it; cross-link the two specs |
| Engine read surface (`IFallen8Read`/`AFallen8`/`Fallen8`/`AddressedFallen8`) | New `GraphScanAllProperties` on all four; the mock read surfaces in `AnalyticsControllerUnitTest`/`SubGraphControllerTest` implement `IFallen8Read` | Add the method to each; update the two test mocks to delegate to inner |
| [graph-namespaces](../../done/graph-namespaces/) | Route must answer under `/ns/{ns}/...` too | `AddressedFallen8.GraphScanAllProperties` delegates to `Engine`; the namespaced route is automatic (same as every scan route) |
| [scan-result-representation](../../done/scan-result-representation/spec.md) | Result shape (id list) and `CreateResult` reused | None |
| [api-error-contract](../../done/api-error-contract/spec.md) | Blank term / missing body | `400` via `ProblemResults`, not a throw |
| `HostedRoutingSmokeTest` | Pins one region per scan route (template, binding, error shapes) | Add a `/scan/graph/properties` region; also pins that the plural literal never collides with the singular `/scan/graph/property/{propertyId}` template |
| [property-ingestion-culture](../../done/property-ingestion-culture/spec.md) | Value stringification must be culture-stable | Render with `CultureInfo.InvariantCulture`, matching ingest |
| element-embeddings / embedding-provider | Reserved `$embedding:`/`$embeddingModel:` entries must not match | Skipped via `IsReservedPropertyId` (same rule as the string-only member) |
| [index-workspace](../../done/index-workspace/README.md) (Query screen) | New scope toggle + persisted draft fields | Extend `QueryScreen`, the `queryDraft` store, `endpoints.ts`, `types.ts`; pin in `query-scans.test.tsx` and `api-contract.test.ts`; the e2e `studio.spec.ts` property-scan scenario stays green because the scope defaults to `specific key`; the store's rehydrate merge (`{...DEFAULT_QUERY_DRAFT, ...persisted}`) defaults the new fields for old persisted drafts, no migration |
| [mcp-server](../../../docs/src/content/docs/mcp-server.md) | New REST op triggers the coverage gate; the docs page enumerates the `f8_search` modes | Bridge as `f8_search` mode `properties`; update `McpBridgedEndpoints`, `McpContractTest`, `McpRestCoverageTest`, `McpReadToolsTest`; add the mode to the `f8_search` row in `mcp-server.md` |
| openapi-10 / OpenAPI snapshot | New route + XML docs | Regenerate with `scripts/update-openapi-snapshot.ps1`; expect one added operation, no removals |
| AppJsonContext / JsonSourceGenParityTest | New serialized model | Register `PropertySearchSpecification`; keep the parity test green |
| docs-site (`graph-model.mdx`, `studio.md`, `indexes.mdx`) | Property-scan story and Query row | Add an "any key" subsection to graph-model's Full-graph property scans; update the studio Query row line; keep the indexes cross-link accurate |
| README "Key features" | Property scans are covered by the existing "Graph model" bullet | Augment that bullet's wording if useful; no new page, no new bullet |
| nl-assist-finetune | No delegate/NL surface change (the new engine member is internal; the REST endpoint is not an NL target) | Reviewed: **no retrain**, no `RETRAIN-LOG.md` entry, no dataset/eval change |
| architecture diagrams | No new channel or deployable | No diagram change |
| UI screenshots | The Query screen changes | Recapture the affected `docs/src/assets` screenshots per the screenshot pipeline; update the docs page image if it shows the property-scan row |

## Non-goals (with revisit triggers)

- **A match-kind selector (starts-with / ends-with / equals) and case toggle.** Contains,
  case-insensitive, is the discovery default. The internal member already takes a
  `Func<string,bool>`, so widening later is a UI + wire-flag change, not an engine one. Revisit
  if users ask for exact/prefix matching in the box.
- **Relevance scoring, ranking, or highlighting.** This is a boolean id-set scan. Scored,
  highlighted search is the indexed `/scan/index/fulltext` path; do not duplicate it here.
  Revisit only if discovery genuinely needs ranking without an index.
- **Searching property NAMES/keys or the built-in fields (Label, id, dates).** Values only; the
  Label is available as the exact-match restrictor. Revisit if a "find a key called ..." need
  appears.
- **Exposing the stringified walk to delegate fragments / the NL model.** Kept internal to avoid
  a type-model + retrain cost; `element-fulltext-match` already made fragment stringify a
  non-goal. Revisit if a fragment prompt class needs it.
- **Values that are not `IConvertible` (raw `float[]`/`byte[]`, `Guid`, `TimeSpan`).** Skipped in
  v1 (blobs are noise; `Guid`/`TimeSpan` are rare as searchable properties). Revisit if a real
  dataset needs them.
- **Server-side paging / result caps.** Returns all matching ids, like `GraphScan`; the Studio
  hydration cap (500) still applies client-side and is surfaced. Revisit if a client is
  overwhelmed by very large id sets.
- **Index-accelerated all-property search.** The scan is intentionally cold and O(n x props); the
  docs say so and point at indexes for scale.
