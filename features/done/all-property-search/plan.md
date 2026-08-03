# All-property search - plan

Phased so each phase builds clean (warnings are errors), passes the suite, and leaves the tree
in a shippable state. Layers land bottom-up (engine, then REST, then MCP, then Studio, then
docs) so every upper layer sits on tested ground.

Workflow: this is feature CODE, so it lands on a `feature/all-property-search` branch (not
directly on `main`); the spec/plan/bookkeeping docs may sit on `main`. Open the feature issue
and PR only if asked. Run the review/council gate before merge.

## Phase 1 - Engine

**Goal:** `GraphScanAllProperties` returns the right live set for a case-insensitive,
stringified, all-values contains match; blank term is empty; reserved keys never match.

- `Model/AGraphElementModel.cs`: add `internal Boolean AnyStringifiedPropertyValueMatches(Func<String,Boolean> valuePredicate)`.
  Walk the compact store by reference; skip reserved keys (`IsReservedPropertyId`) and `null`
  values; render `IConvertible` values with `CultureInfo.InvariantCulture` and pass to the
  predicate; skip non-`IConvertible` values. Factor the shared non-reserved-property walk with
  the existing `AnyPropertyValueMatches` so the loop is not duplicated.
- `Fallen8.Scan.cs`: add `GraphScanAllProperties(out result, searchTerm, interestingLabel)`.
  Blank/whitespace term -> empty list, `false`. Otherwise build the seeker
  `ge => ge.AnyStringifiedPropertyValueMatches(v => v.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))`
  and reuse `FindElements(ElementSeeker, interestingLabel)`. Return `result.Count > 0`.
- Declare on `IFallen8Read.cs` and `AFallen8.cs` (abstract); implement the namespaced delegate in
  `Namespaces/AddressedFallen8.cs`.

**Tests** (`fallen-8-unittest`, new `AllPropertyScanTest.cs`; thorough per the quality bar):
- match across multiple keys (OR): term in key A on one element, key B on another, both returned.
- case-insensitivity: `acme` matches `Acme`.
- stringified numeric/bool/date: `42` matches int `42`; `3.14` matches double `3.14` (invariant
  culture is passed explicitly, so the host culture is irrelevant - assert that too by flipping
  `CurrentCulture` to a comma-decimal culture in one test); `true` matches bool `true`
  case-insensitively; a year substring matches a `DateTime`.
- reserved-key exclusion: an element carrying only an `$embeddingModel:` stamp of `nomic-embed-text`
  is NOT returned for the term `text`.
- label restrictor: same term, `interestingLabel` narrows vertices/edges by exact label.
- result includes both vertices and edges when both match (the controller does result-type
  filtering; the engine returns all live matches).
- removed elements excluded; a `null`-valued property is a clean skip (no throw).
- blank term -> empty, `false`; no-match term -> empty, `false`.
- non-`IConvertible` value (a `float[]` under a non-reserved key) does not match and does not throw.

## Phase 2 - REST

**Goal:** `POST /scan/graph/properties` returns ids, honours label + result type, `400`s a blank
term, and is in the OpenAPI snapshot.

- `Controllers/Model/PropertySearchSpecification.cs`: `SearchTerm` (`[Required]`), optional
  `Label`, `ResultType` with an explicit initializer `= ResultTypeSpecification.Both` plus
  `[DefaultValue(ResultTypeSpecification.Both)]` - the enum's zero value is `Vertices`, so
  without the initializer an omitted `resultType` silently means `Vertices`, violating the
  contract. MIT header, XML docs, and an `<example>`.
- `Controllers/GraphController.Scan.cs`: add `GraphScanProperties([FromBody] PropertySearchSpecification)`
  with `[HttpPost("/scan/graph/properties")]`, `[Produces]`/`[ProducesResponseType]` (200 `IEnumerable<int>`,
  400), and `<summary>`/`<remarks>` with a sample request. Blank body or blank `SearchTerm`
  -> `ProblemResults.BadRequest`; otherwise `GraphScanAllProperties(out els, SearchTerm, Label)`
  then `CreateResult(els, ResultType)`.
- `AppJsonContext.cs`: register `PropertySearchSpecification`.
- Regenerate the OpenAPI snapshot: `pwsh scripts/update-openapi-snapshot.ps1`; review the diff
  (one added operation, no removals).

**Tests:**
- `GraphControllerTest.cs` (or a focused new test): ids returned for a matching term; `resultType`
  `Edges`/`Vertices`/`Both` filters correctly; an OMITTED `resultType` behaves as `Both` (pins
  the initializer); `label` narrows; blank term -> `400`; missing body -> `400`.
- `HostedRoutingSmokeTest.cs`: new `/scan/graph/properties` region following the per-route
  pattern (200 + ids for a hit, 200 + empty for a miss, 400 shapes), which also pins that the
  plural literal segment never collides with `/scan/graph/property/{propertyId}`.
- `JsonSourceGenParityTest.cs` stays green with the new model.

## Phase 3 - MCP

**Goal:** agents can run the same scan; the coverage gate stays green.

- `Bridge/Dto/SearchDto.cs`: add `PropertySearchRequest` (`searchTerm`, `label`, `resultType`).
- `Tools/SearchTool.cs`: add `properties` to the `mode` choices; a branch that reads `query`
  (blank -> 400), maps `kind` -> `resultType` and passes `label`, and POSTs to
  `scan/graph/properties`; extend the tool description to distinguish `property` (named key,
  typed operator) from `properties` (cold contains scan over all values), and widen the `query`
  argument's description (today it says "fulltext/semantic modes" - it now serves `properties`
  too).
- Update `McpBridgedEndpoints`, `McpContractTest`, and the `McpRestCoverageTest` snapshot for the
  new route.

**Tests:** `McpReadToolsTest.cs`: `properties` mode returns the expected ids for a term; blank
`query` -> 400; `kind`/`label` honoured; unknown/none -> empty page (not an error).

## Phase 4 - Studio UI

**Goal:** the scope toggle works, persists, and sends the right request; the named path is
untouched.

- `state` / `instances` store: add `propertyScope` (`"key" | "any"`, default `"key"`) and
  `searchTerm` (default `""`) to `queryDraft` and its reset. No persistence migration is needed:
  the rehydrate path already merges `{ ...DEFAULT_QUERY_DRAFT, ...persisted }`
  (`instanceStore.ts`), so an old persisted draft gets the new fields' defaults.
- `api/types.ts`: add `PropertySearchSpecification`. `api/endpoints.ts`: add
  `scanProperties(instance, spec)` -> `POST /scan/graph/properties`.
- `screens/QueryScreen.tsx`: render the `specific key | any property` toggle in the property
  branch; in `any` mode swap in the search-term input (+ optional label input, + result-type
  selector) and hide operator/literal/property-id; branch the scan mutation to `scanProperties`;
  disable run on a blank term; label the result set `all-property "<term>" (N ids)`.
- `lib/fieldHelp.ts`: add `propertyScope`, `searchTerm`, and a label-restrictor help entry
  (case-insensitive substring across all property values, compared as text, un-indexed
  full-graph scan; label = exact match).
- e2e `studio.spec.ts` scenario 4 (property scan -> table -> canvas) must stay green unchanged:
  the scope defaults to `specific key`, so the existing `scan-property` flow is untouched. Do
  not extend the e2e for the new scope; the vitest coverage above pins it.

**Tests** (`fallen-8-web-ui`):
- `tests/query-scans.test.tsx`: new "all-property search" block: toggling to `any` swaps the
  inputs; run sends `scanProperties` with `{ searchTerm, resultType, label? }`; ids hydrate into
  the table; run is blocked on a blank term; scope + term persist across unmount/remount and
  `Clear` resets them; switching back to `specific key` restores the named-key controls.
- `tests/api-contract.test.ts`: add `scanProperties` to the contract fixture.
- Confirm the true test status via the `cmd /c "... & echo EXIT=%ERRORLEVEL%"` wrapper (the
  PowerShell vitest wrapper reports a misleading exit code).

## Phase 5 - Docs and screenshots

- `docs/src/content/docs/graph-model.mdx`: under "Full-graph property scans", add an "Across
  every property (discovery search)" subsection: the `POST /scan/graph/properties` route, its
  body table (`searchTerm`, `label`, `resultType`), a worked `curl`/PowerShell example, and an
  explicit "cold O(n x props) scan, compared as text, case-insensitive, no score; use an index
  for scale" note.
- `docs/src/content/docs/graph-model.mdx`, "Using the engine as a library": add
  `GraphScanAllProperties` to the sentence listing the `IFallen8Read` reads.
- `docs/src/content/docs/studio.md`: update the Query row to mention the all-property search box.
- `docs/src/content/docs/mcp-server.md`: the tool table enumerates the `f8_search` modes
  explicitly - add `properties` to that row (this is a must, not a conditional).
- `README.md`: augment the "Graph model" key-features bullet wording if it sharpens discovery;
  no new page/bullet.
- Recapture the affected Query-screen screenshot(s) in `docs/src/assets` per the screenshot
  pipeline (isolated app + `F8_UI_URL`); refresh any doc image showing the property-scan row.
- Build the docs locally: `npm --prefix docs ci && npm --prefix docs run build` (fails on any
  broken internal link).

## Cross-feature sweep (record findings in the spec's Impact table before merge)

engine read surface + mocks, graph-namespaces route, scan-result-representation, api-error-contract,
property-ingestion-culture (invariant stringify), element/embeddings reserved-key exclusion,
index-workspace Query screen + persisted draft, mcp-server coverage gate, OpenAPI snapshot,
AppJsonContext parity, docs pages, README, nl-assist (no retrain), architecture diagrams (no
change), UI screenshots.

## Definition of done

- All five phases merged on the feature branch; build clean with warnings-as-errors; full suite
  green (engine, controller, MCP, web-ui).
- OpenAPI snapshot, MCP coverage/contract, and JSON source-gen parity gates pass.
- Docs site builds link-clean; graph-model + studio pages updated; screenshots recaptured.
- The feature directory moves from `features/open/all-property-search/` to `features/done/`.
