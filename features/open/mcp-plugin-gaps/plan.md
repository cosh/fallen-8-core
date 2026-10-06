# MCP plugin gaps 2026-10-06 - Implementation plan

Branch `feature/mcp-plugin-gaps` from `main`. The [spec](./spec.md) is the contract; this file
is the order of work and the gate each phase must pass. Phases are independent except where
noted, so one that turns out wrong can be dropped without unpicking the others. A box is ticked
when the change is on the branch WITH its test and its mutation check; a box that cannot be
ticked says why in place rather than being ticked anyway.

Every code phase follows the same shape: write the failing test against the current code, confirm
it is red for the stated reason, fix, confirm green, then revert the fix once more and confirm red
(mutation check: back up per mutant, touch the restored file so MSBuild rebuilds, diff against
HEAD before moving on). Run the suite without `-v q`, so a flake has a name.

Order: phases 1 and 2 first because they are small and they fix what the plugin hits on every
call. Phase 3 (packaging) next because it has the one external dependency (the Trusted Publishing
policy) and an early local pack tells us whether the Web SDK cooperates. Phases 4 to 6 are the
bridging work. Phase 7 is the probe. Phase 8 is docs and closes the record.

## Phase 1 - the `f8_mutate` schema (spec 3)

Files: `fallen-8-mcp/Tools/SchemaBuilder.cs`, `fallen-8-mcp/Tools/MutateTool.cs`,
`fallen-8-unittest/McpToolSurfaceTest.cs`, `fallen-8-unittest/McpWriteToolsTest.cs`, new
`fallen-8-unittest/SchemaBuilderTest.cs`, new `fallen-8-unittest/FlatSchemaChecker.cs` (test
support).

- [ ] `SchemaBuilderTest.Add_SameNameTwice_Throws` (red: the second `Add` currently succeeds).
- [ ] `SchemaBuilder.Add` throws `InvalidOperationException` naming the duplicate; add `IntArray`
      and `Num`.
- [ ] `McpToolSurfaceTest.EveryTool_Describe_DeclaresNoDuplicateSchemaName` over every registered
      tool and all eight capability combinations. With the throwing builder and the unfixed
      `MutateTool` this is red on `f8_mutate` with the duplicate's name in the message; that is the
      defect, pinned.
- [ ] `MutateTool`: `properties` stays the object map; the batch becomes `updates`; `ids` is
      `IntArray`. Runtime: `set_properties` reads `updates`, falling back to an array-valued
      `properties`. The op descriptions and the tool description name `updates`.
- [ ] `FlatSchemaChecker`: validates a JSON argument object against the flat subset (`type` of
      object/string/integer/number/boolean/array, `items.type`, `enum`, `required`,
      `additionalProperties:false`, and the untyped `Any`). About sixty lines. It has its own
      test (`FlatSchemaCheckerTest`) with one accepted and one rejected case per rule, because a
      checker that accepts everything would make the next test a false green.
- [ ] `McpToolSurfaceTest.EveryTool_SampleCall_ValidatesAgainstItsAdvertisedSchema`: a table of
      one valid call per op per tool, validated with the checker against `Describe` with all caps
      on. Red on the current `f8_mutate` for `create_vertex` with object `properties` (the
      consequence section 2 of the spec names), green after.
- [ ] `McpWriteToolsTest`: `set_properties` through `updates` and through the legacy array both
      reach `PUT /graphelements/properties` with the same body.
- [ ] Mutation check: restore the overwriting `Add` and the duplicate `properties`; both new
      surface tests must go red.

Risk: a tool that varies its schema by caps (`f8_plugins`, `f8_documents`, `f8_storedquery` after
phase 6) could declare a name in one branch and again in another; the eight-combination
enumeration is there for exactly that.

## Phase 2 - `f8_overview` state and the status DTO (spec 4)

Files: `fallen-8-mcp/Bridge/Dto/StatusDto.cs`, `fallen-8-mcp/Tools/OverviewTool.cs`, new
`fallen-8-unittest/McpStatusDtoParityTest.cs`, `fallen-8-unittest/McpReadToolsTest.cs`.

- [ ] `McpStatusDtoParityTest`: for `StatusDto` and every nested DTO, each effective JSON name
      exists on the paired REST type (`StatusREST`, `IndexDescriptionREST`,
      `EmbeddingProviderStatsREST`, `ChatProviderStatsREST`, `IngestionStatsREST`,
      `DoclingStatsREST`, `NlpStatsREST`). Red on `model` and `dimensions` before the rename.
      Reuse `McpWriteDtoParityTest`'s name computation by moving it to `McpTestSupport` rather
      than copying it.
- [ ] Rename `EmbeddingStateDto.Model` to `ModelName`, `Dimensions` to `Dimension`. Add
      `IngestionStateDto`, `DoclingStateDto`, `NlpStateDto`; `StatusDto.Ingestion` and `.Nlp`.
- [ ] `OverviewTool.BuildStatus` emits the nine new fields with the absent-block rules from the
      spec.
- [ ] `McpReadToolsTest`: overview against the hosted apiApp (ingestion off, provider on) and
      against a handler returning a status body with no `ingestion`/`nlp` members. Assert the
      exact field set so a later rename cannot drop one silently.
- [ ] Mutation check: revert the rename only; the parity test goes red, the read-tools test goes
      red on `embeddingModel:null`.

## Phase 3 - the .NET tool package (spec 5)

Files: `fallen-8-mcp/fallen-8-mcp.csproj`, new `fallen-8-mcp/README.md` (package page),
`.github/workflows/release.yml`, `fallen-8-unittest/McpTransportTest.cs`.

- [x] **Spike, measured on 2026-10-06 (packed locally, launched with `dotnet dnx --yes
      --prerelease --source ./packages fallen-8-mcp --stdio` from the scratchpad directory):**
      - The pack succeeds from the Web SDK project unchanged; the package is 2.2 MB, not tens
        of MB (the framework-dependent tool carries the SDK and OTLP assemblies, no runtime).
        The Web SDK also files `appsettings.json` under `content/` and `contentFiles/`, which a
        tool install ignores; harmless, left alone.
      - `--stdio` arrives: the server answers `initialize` on stdout with protocol `2025-06-18`.
      - `appsettings.json` was NOT found: the content root was the caller's directory (the
        scratchpad), measured with a marker URL in the file that the posture line did not show.
        Fixed in `Program.cs`: both builders set `ContentRootPath = AppContext.BaseDirectory`;
        re-measured, the posture line shows the marker and the content root is the package's
        `tools/net10.0/any/`. The fix is in the host, not the workflow, as predicted.
      - Found on the way: the posture line said `transport=http` under a `--stdio` launch,
        because it printed the configured `Mcp:Transport` rather than the resolved one.
        `LogStartupPosture` now takes the resolved transport; pinned by
        `StartupPosture_NamesTheResolvedTransport_NotTheConfiguredOne` (red with the setting
        logged, green with the resolved value).
      - `dnx` caches by version, and MinVer gave both packs the same prerelease version, so the
        second measurement needed the cached package removed first. A release never hits this.
- [x] csproj: `IsPackable`, `PackAsTool`, `ToolCommandName`, `PackageId`, metadata mirroring the
      engine's, icon and a package README; the "only the engine" comment replaced. An XML
      comment cannot contain a double hyphen, so the flag is not spelled in it.
- [x] `McpTransportTest.ResolveTransport_RecognisesTheFlagAnywhere_AndDefaultsToHttp` (named for
      what it checks: flag position, case, the near-miss `--stdio-ish`, and the http default).
- [x] `release.yml` `nuget` job: a second pack line into `packages/`; job name and header comment
      list both packages and state the policy rule. The push steps are untouched (they glob).
- [ ] **Pre-release operator check, recorded here when done:** the nuget.org Trusted Publishing
      policy for `release.yml` allows the `fallen-8-mcp` id. Until the first tagged release ships
      it, the docs say "from the next release" rather than claiming it resolves today.
- [x] Mutation checks: the content-root mutant is the first spike run itself (marker not shown);
      the posture mutant (log `mcp.Transport`) fails the posture test. The `PackAsTool` mutant was
      NOT run: its stated purpose was to learn the failure wording for the docs, and the docs do
      not describe a mis-packed tool, so there is nothing for that measurement to feed. Recorded
      as skipped rather than ticked.

Risk, revised: package size is not a concern (2.2 MB). The remaining risk is the policy check
above, which no code can do.

## Phase 4 - `f8_index` (spec 6)

Files: new `fallen-8-mcp/Tools/IndexTool.cs`, `fallen-8-mcp/Bridge/Dto/WriteDto.cs` (new DTOs),
`fallen-8-mcp/Hosting/McpHost.cs` (registration), `fallen-8-unittest/McpBridgedEndpoints.cs`,
`fallen-8-unittest/McpRestCoverageTest.cs` (delete the `" /index"` deferral),
`fallen-8-unittest/McpWriteDtoParityTest.cs` (four pairs), `fallen-8-unittest/McpWriteToolsTest.cs`,
`fallen-8-unittest/McpToolSurfaceTest.cs`.

- [ ] Add the eight routes to `McpBridgedEndpoints` and delete the deferral first:
      `NoBridgedEndpoint_MatchesADeferral` and `EveryRestOperation_IsBridgedOrConsciouslyDeferred`
      are now the gate that the tool must satisfy, and `McpContractTest` confirms the eight exist
      in the snapshot with their methods.
- [ ] DTOs: `IndexCreateDto` (mirrors `PluginSpecification`, options as
      `Dictionary<String, PropertySpecDto>`), `IndexAddDto`, `IndexBatchEntryDto`,
      `VectorIndexAddDto`, `IndexBackfillDto`; parity pairs added. Red until the DTOs match
      field for field.
- [ ] `IndexTool`: write tier; the ops table from spec 6; `options` mapped through
      `ValueMapping.TryFromJson` per entry; the boolean-to-409 translation with the honest
      two-cause message; `add_vector` requires exactly one of `vector`/`propertyId` client-side
      only to produce a clear 400 (the server's 400 is also accepted as pass-through; the test
      covers the client-side message so an agent never sees two different sentences for one
      mistake).
- [ ] Register in `McpHost` next to `MutateTool`.
- [ ] `McpToolSurfaceTest`: absent at default tiers, present with write.
- [ ] `McpWriteToolsTest`: the round-trip script in spec 6, including `f8_search mode:index` over
      the index the tool created and `mode:semantic` over the VectorIndex it created.
- [ ] Mutation check: drop the `false` translation; the 409 assertions go red.

## Phase 5 - `semantic`, `patterns`, path knobs (spec 7)

Files: `fallen-8-mcp/Tools/PathsTool.cs`, `fallen-8-mcp/Tools/SubgraphTool.cs`,
`fallen-8-mcp/Bridge/Dto/PathAndAnalyticsDto.cs`, `fallen-8-unittest/McpReadToolsTest.cs`.

- [x] Read the doc comments of `SemanticTraversalSpecification.EmbeddingBackend` and
      `.EmbeddingIdentity`: both are SERVER-OWNED ("whatever a client sends here is discarded
      before anything reads it"), stamped on the run that embedded a text. Forwarding the block
      raw is therefore safe; the schema description names the five agent-facing fields only.
- [ ] DTOs gain `JsonElement? Semantic`, `Double? MaxPathWeight`, `Double? TimeBudgetSeconds`
      (paths) and `JsonElement? Semantic`, `JsonElement? Patterns` (subgraph), serialised only
      when present so an untouched call sends the same body as today (assert byte-equality of the
      body for a knob-free call in the test, so this phase cannot change existing behaviour).
- [ ] `PathsTool`: `semantic` (`Obj`), `maxPathWeight` (`Num`), `timeBudgetSeconds` (`Num`),
      forwarded raw.
- [ ] `SubgraphTool`: `semantic` (`Obj`), `patterns` (`ObjArray`); a pattern entry carrying
      `vertexFilter`, `edgeFilter` or `edgePropertyFilter` needs the code capability (403 naming
      `Mcp:Tools:EnableCode`), checked before the call.
- [ ] `McpReadToolsTest`: the five cases in spec 7, against the hosted apiApp where the server's
      behaviour is the assertion (ranking under `costBySimilarity`, the slot-conflict 400), and
      against the captured-request handler where the sent body is.
- [ ] Mutation check: stop forwarding `semantic`; the ranking test goes red. Remove the code
      check on patterns; the 403 test goes red.

## Phase 6 - `f8_storedquery` (spec 8)

Files: new `fallen-8-mcp/Tools/StoredQueryTool.cs`, `fallen-8-mcp/Bridge/Dto/` (a
`StoredQueryDto.cs` for the summary and detail), `McpHost.cs`, `McpBridgedEndpoints.cs`,
`McpRestCoverageTest.cs` (delete the `/storedquery` deferral), `McpToolSurfaceTest.cs`,
`McpReadToolsTest.cs`, `McpWriteToolsTest.cs`.

- [ ] Routes into `McpBridgedEndpoints`, deferral deleted; the governance gate is red until the
      tool exists.
- [ ] `StoredQueryTool`: read tier; `list`/`get` always; `delete` needs write; `register` needs
      code; the op enum varies by caps the way `PluginsTool.Describe` does. `get` parses
      `specificationJson` back into a JSON object.
- [ ] `McpToolSurfaceTest`: op enum per capability combination (this is where phase 1's
      eight-combination test earns its keep).
- [ ] Round-trip tests from spec 8, including `f8_paths storedQuery:<name>` over the query the
      tool registered, the compile-failure 400 carrying the compiler's sentence, 409 on a second
      register, 403 without code, 403 on delete without write.
- [ ] Mutation check: drop the code check on `register`; the 403 test goes red.

## Phase 7 - `/readyz` (spec 9)

Files: `fallen-8-mcp/Program.cs`, `fallen-8-mcp/Configuration/McpOptions.cs` (a `Readiness`
block), `fallen-8-mcp/Hosting/TransportSecurity.cs` or `Program.cs` (the exemption list),
`fallen-8-unittest/McpTransportTest.cs`, `docker-compose.yml` (healthcheck, if present).

- [x] Found: the origin/bearer exemption is a `StartsWithSegments("/healthz")` prefix match in
      `Program.cs`; the rate limiter had NO exemption, so `/healthz` was inside the 600-per-60-s
      window, and the existing `RateLimiter_RejectsBeyondTheWindow` even counted its three requests
      on `/healthz`. Both probes now carry `DisableRateLimiting()`; the limiter test counts on the
      MCP endpoint instead, which is what the limiter is for.
- [x] `/readyz` handler in `Hosting/ReadinessProbe.cs`: `GET /status` on the default namespace
      under a linked token from `Mcp:Readiness:TimeoutSeconds` (default 3, clamped by
      `OptionBounds.Seconds`); the four outcomes from spec 9. The seam reports a timeout as the
      caller's cancellation when the caller's token fired, which the linked budget is, so the probe
      catches `OperationCanceledException` while the request itself was not aborted.
- [x] `McpTransportTest`: the five cases through the hosted harness, whose factory now takes an
      optional target handler (a hanging one for the timeout, a 500, a key-rejected body, a ready
      body) and substitutes the bridge's `IHttpClientFactory`; a fifth case sends three probes with
      no bearer, a foreign origin and a one-permit window, all 200, for `/readyz` and `/healthz`.
- [x] Compose: `f8-mcp` HAD a healthcheck on `/healthz` (10 s interval) and `f8-agents` depends on
      it with `condition: service_healthy`; the check now targets `/readyz`, so the agent host
      waits for a bridge that reached its Fallen-8 with an accepted key, which is what it needs.
- [x] Mutation check: two mutants in one run, the key check neutralised and a bridge failure
      reported as ready; the key-rejected and the 500 tests went red, one each. The timeout arm
      was not mutated: its test is the only one that can pass through that catch at all.

## Phase 8 - docs, records, hard rules (spec 10, 11, 12)

Files: `docs/src/content/docs/mcp-server.md`, `fallen-8-core-apiApp/Ingestion/DoclingClient.cs`
(class summary), `README.md` (one line), `features/done/mcp-server/README.md` (tool count, test
map, packaging sentence), the one-line pointers in `features/done/index-lifecycle`,
`features/done/stored-query-library`, `features/done/element-embeddings` where they mention MCP.

- [ ] `mcp-server.md`: registry sentence replaced by the pull form; "Running it" gains the tool
      form and the two probe sentences; "Connecting a client" gains the plugin block; the tool
      table gains two rows and edits four (`f8_mutate`, `f8_overview`, `f8_paths`,
      `f8_subgraph`); "Tiers and the code capability" lists `f8_index` and the two gated ops of
      `f8_storedquery`; the configuration reference gains `Mcp:Readiness:TimeoutSeconds`.
- [ ] `DoclingClient` class summary becomes a pointer to `ConvertAsync`'s summary.
- [ ] `README.md` key-features line for the MCP server mentions the `dnx` launch form.
- [ ] Feature README (living doc) updated; pointers placed; nothing re-narrated.
- [ ] `npm --prefix docs ci && npm --prefix docs run build` green.
- [ ] Hard-rule sweep over every changed file, in Python: no em or en dash, none of the three
      forbidden external names, MIT header present on new `.cs` files, no `Console.Write*` in
      product code, no `DateTime.Now`.
- [ ] Live verification from spec 13 run once against `:5000`/`:8090`; transcripts saved for the
      PR description. The packed-tool check from phase 3 repeated against the final branch.
- [ ] Section 7a added to the spec: what the implementation changed about this document, and
      the plugin handoff list confirmed against what actually shipped.

## When it lands

Move `features/open/mcp-plugin-gaps/` to `features/done/`, set the spec's status line, and update
the memory note on the release pipeline with the second NuGet package once the first tagged
release has carried it (not before: an unshipped package is a plan, not a fact).
