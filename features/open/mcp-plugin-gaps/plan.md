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

- [x] `SchemaBuilderTest.Add_SameNameTwice_Throws_NamingTheDuplicate` (red: the second `Add`
      succeeded), plus a same-shape repeat, `IntArray` and `Num`.
- [x] `SchemaBuilder.Add` throws `InvalidOperationException` naming the duplicate; `IntArray` and
      `Num` added.
- [x] `McpToolSurfaceTest.EveryTool_Describe_DeclaresNoDuplicateSchemaName` over every registered
      tool (the shared fixture gained `PluginsTool` and `DocumentsTool`, which it had omitted) and
      all eight capability combinations.
- [x] `MutateTool`: `properties` is the object map again; the batch is `updates`; `ids` is
      `IntArray`; the legacy array-valued `properties` is still read.
- [x] `FlatSchemaChecker` with `FlatSchemaCheckerTest` (one accepted and one rejected case per
      rule, including the object-versus-array pair that was the defect).
- [x] `McpToolSurfaceTest.EveryTool_SampleCall_ValidatesAgainstItsAdvertisedSchema`: measured red
      on the unfixed tool with six violations (`properties` twice, `updates` undeclared, three
      `ids` items), green after.
- [x] `McpWriteToolsTest`: `set_properties` via `updates` and via the legacy array, both applied
      against the hosted apiApp; found and fixed the remove entry that had never worked (spec
      section 14); `remove_elements` refusing a non-integer and removing a batch.
- [x] Mutation checks: the builder's throw, the DTO's omitted value and the integer id array
      reverted in one run, each caught by its own test; a re-added duplicate declaration caught by
      the enumeration with the duplicate's name.

Risk: a tool that varies its schema by caps (`f8_plugins`, `f8_documents`, `f8_storedquery` after
phase 6) could declare a name in one branch and again in another; the eight-combination
enumeration is there for exactly that.

## Phase 2 - `f8_overview` state and the status DTO (spec 4)

Files: `fallen-8-mcp/Bridge/Dto/StatusDto.cs`, `fallen-8-mcp/Tools/OverviewTool.cs`, new
`fallen-8-unittest/McpStatusDtoParityTest.cs`, `fallen-8-unittest/McpReadToolsTest.cs`.

- [x] `McpStatusDtoParityTest`: measured red on exactly `embedding: {dimensions, model}`; the
      name computation moved to `McpTestSupport.EffectiveJsonNames`.
- [x] Rename to `ModelName`/`Dimension`; `IngestionStateDto`, `DoclingStateDto`, `NlpStateDto`
      added.
- [x] `OverviewTool.BuildStatus` emits the ten new fields (the spec's nine plus
      `ingestionFulltextIndexId`) with the absent-block rules.
- [x] The overview tests live in `McpBridgeTest` (where the overview round-trips already were):
      the live case compares against the same host's raw `/status` body; the stubbed case pins the
      exact field set for an absent and a present block.
- [x] Mutation check: the two fields pinned to the old wire names with `[JsonPropertyName]`; the
      parity test and both overview tests went red.

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
- [ ] **Pre-release operator check, recorded here when done:** on nuget.org (username menu,
      "Trusted Publishing"), the policy for `cosh/fallen-8-core` with workflow file `release.yml`
      has a scope that allows publishing NEW packages, not only new versions of existing ones, and
      its package glob (if one is set) matches `fallen-8-mcp`. A policy is scoped by owner,
      repository, workflow and scopes, never by package id; the spec's and this plan's earlier
      wording "must allow the id" meant this. Checked on 2026-10-06 that the id `fallen-8-mcp` is
      not registered on nuget.org. Until the first tagged release ships it, the docs say "from the
      first release after it was added" rather than claiming it resolves today.
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

- [x] The eight routes in `McpBridgedEndpoints`, the deferral deleted; coverage, disjointness and
      contract tests green with them.
- [x] DTOs: `IndexCreateDto`, `IndexAddDto` (also the batch item), `VectorIndexAddDto`,
      `IndexBackfillDto`; four parity pairs, green first time (the REST shapes were read before
      writing).
- [x] `IndexTool`: write tier; the ops table from spec 6; `create` false is 409, the other bare
      falses are 404, both declared as the bridge's reading; `add_vector`'s exactly-one rule is
      checked client-side.
- [x] Registered in `McpHost` next to `MutateTool`; in the shared test fixture.
- [x] Tier matrix: absent at default tiers, present with write.
- [x] `McpWriteToolsTest`: the dictionary lifecycle with `f8_search mode:index` as the witness,
      and a vector lifecycle with `mode:vector` (not `mode:semantic`, which needs a text embedding
      provider); a bound index refuses `add_vector` by design, pinned as pass-through.
- [x] Mutation check: the `false` translation bypassed on `create`; the lifecycle test went red.

## Phase 5 - `semantic`, `patterns`, path knobs (spec 7)

Files: `fallen-8-mcp/Tools/PathsTool.cs`, `fallen-8-mcp/Tools/SubgraphTool.cs`,
`fallen-8-mcp/Bridge/Dto/PathAndAnalyticsDto.cs`, `fallen-8-unittest/McpReadToolsTest.cs`.

- [x] Read the doc comments of `SemanticTraversalSpecification.EmbeddingBackend` and
      `.EmbeddingIdentity`: both are SERVER-OWNED ("whatever a client sends here is discarded
      before anything reads it"), stamped on the run that embedded a text. Forwarding the block
      raw is therefore safe; the schema description names the five agent-facing fields only.
- [x] `PathRequest` gains the three nullable members, omitted when unset; the subgraph body is a
      `JsonObject` already and gains the two blocks only when given. The byte-equality idea became
      "the three keys are absent from a knob-free body".
- [x] `PathsTool`: `semantic` (`Obj`), `maxPathWeight` (`Num`), `timeBudgetSeconds` (`Num`),
      forwarded raw; `ToolArgs.GetDouble` added.
- [x] `SubgraphTool`: `semantic` (`Obj`), `patterns` (`ObjArray`); a step carrying a fragment
      needs the code capability (403 naming `Mcp:Tools:EnableCode`); a blank fragment is not code.
- [x] Tests: paths in `McpReadToolsTest` (the fixture's embeddings make `minScore` remove and keep
      a path; the slot-conflict 400 with the server's sentence; the knobs present only when given),
      subgraph in `McpWriteToolsTest` (code-free pattern and semantic-only definition compute
      without the capability; a fragment inside a pattern is 403 without and computes with it).
- [x] Mutation check: both mutants in one run; the two semantic path tests and the pattern gate
      test went red.

## Phase 6 - `f8_storedquery` (spec 8)

Files: new `fallen-8-mcp/Tools/StoredQueryTool.cs`, `fallen-8-mcp/Bridge/Dto/` (a
`StoredQueryDto.cs` for the summary and detail), `McpHost.cs`, `McpBridgedEndpoints.cs`,
`McpRestCoverageTest.cs` (delete the `/storedquery` deferral), `McpToolSurfaceTest.cs`,
`McpReadToolsTest.cs`, `McpWriteToolsTest.cs`.

- [x] Routes into `McpBridgedEndpoints`, deferral deleted.
- [x] `StoredQueryTool`: read tier; `list`/`get` always; `delete` on write; `register` on code
      (kind `Path` or `SubGraph`, the REST spelling); the op enum and `readOnlyHint` vary by caps;
      `get` replaces `specificationJson` with a parsed `specification` object.
- [x] `McpToolSurfaceTest.StoredQuery_OpEnum_VariesByCapability`, plus the eight-combination
      enumeration covering it.
- [x] One round trip with the gates in between, as spec 8 lists them, green first time.
- [x] Mutation check: the code gate on `register` dropped; the round trip went red.

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

- [x] `mcp-server.md`: all of it as listed; the index link is `/indexes/` (no `/index-lifecycle/`
      page exists).
- [x] `DoclingClient` class summary points at `ConvertAsync`'s.
- [x] `README.md` key-features line mentions the `dnx` launch form and the plugin.
- [x] Feature README (living doc) updated: packaging sentence, the thirteen tools, the test map.
      The three other feature records contain no MCP sentence, so no pointer was owed.
- [x] Docs build green with the link checker (42 pages), from the worktree with the main tree's
      `node_modules` junctioned in.
- [x] Hard-rule sweep in Python over the added lines of every commit, zero problems each time;
      the pre-existing em dashes on untouched lines of the edited files were left as they are.
- [x] Live verification from spec 13 run on 2026-10-06 against a `dotnet run` apiApp on `:5000`
      and the MCP server on `:8090` with write, admin and code on, driven over Streamable HTTP
      (initialize, tools/list showing thirteen tools, 26 calls). The transcript is
      [live-transcript-2026-10-06.txt](./live-transcript-2026-10-06.txt): every call answered as
      pinned, including the 409 on a taken index id, the semantic block removing and keeping a
      path, the stored query compiling and running, and `/readyz` 200 with the API up and 503
      with it stopped. One observation: with the API process killed, the 503 reason is the 3 s
      timeout rather than "unreachable", so a connect to a dead localhost port stalls on this
      machine rather than being refused; the probe still answers within its deadline. The
      packed-tool check from phase 3 was NOT repeated on the final branch: the tool launch path
      (`Program.cs`, the csproj) did not change after phase 3, and every later commit ran the
      transport tests that pin it.
- [x] Spec section 14 (the "7a" of this record) added: what the implementation changed about the
      document, and the plugin handoff list confirmed against what shipped.

## When it lands

Move `features/open/mcp-plugin-gaps/` to `features/done/`, set the spec's status line, and update
the memory note on the release pipeline with the second NuGet package once the first tagged
release has carried it (not before: an unshipped package is a plan, not a fact).
