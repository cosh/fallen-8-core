# MCP plugin gaps 2026-10-06 - Specification

> **Status:** Draft spec. Branch `feature/mcp-plugin-gaps` (branch-only workflow, no issue or PR
> unless asked). Source: the review written on 2026-10-06 by the author of the Claude Code plugin
> `cosh/fallen-8-claude-plugin`, which was built against this repository's MCP server and found
> gaps only this repository can close. The review listed nine items; this spec covers items 1 to 8.
> Item 9, an embedded WebAssembly MCP host, is a feature of its own with a design note first, and
> lives in [features/open/embedded-mcp/](../embedded-mcp/spec.md).
>
> Every claim in the review was checked against the tree at `4517ee75` before it was accepted.
> Section 2 lists what the review got wrong or left out, because the review itself asked for that
> rather than for a workaround. Section 3 onward is one section per item, each with the verified
> finding, the decision, the contract, and the tests that pin it.

## 1. Summary

Two defects in what `fallen-8-mcp` advertises or binds (items 1 and 2), one health probe that
lies by omission (item 7), four places where the REST surface outgrew the MCP surface (items 4,
5 and 6: index lifecycle, the semantic and pattern blocks, stored queries), one distribution gap
(item 3: no `dnx`-launchable tool package), and three stale sentences (item 8).

None of this touches the engine. Items 2, 3 and 7 touch only `fallen-8-mcp` and its docs. Items
4, 5 and 6 add bridged routes and so edit `McpBridgedEndpoints` and the deferral list in
`McpRestCoverageTest`, which is the governance gate working as designed: three deferrals recorded
in 2026 as "operator/setup tooling" are reversed here because an agent that can write elements
but cannot create the index it searches is stuck.

What this is NOT: a change to any REST route, a change to the apiApp beyond one comment (item 8),
or the embedded host (item 9, separate feature). The apiApp's `200 false` on `POST /index` is
reported here as a REST quirk the bridge translates; changing it is out of scope.

## 2. What the review got wrong, and what it left out

The review asked for this list explicitly. Each row names the claim and what the code says.

| # | Review claim | Finding |
|---|---|---|
| 8a | `docker-compose.yml` pulls `ghcr.io/cosh/fallen-8-core-mcp` but no workflow publishes it. | **Wrong.** [release.yml:110](../../../.github/workflows/release.yml#L110) has a matrix leg for `fallen-8-core-mcp` on every `vX.Y.Z` tag. Only the sentence in [mcp-server.md:51](../../../docs/src/content/docs/mcp-server.md#L51) is stale. No workflow change. |
| V | "Update the CHANGELOG for every user-visible change." | **No CHANGELOG exists** anywhere in the tree (checked at depth 2, excluding `node_modules`). Releases carry generated notes from the tag ([release.yml](../../../.github/workflows/release.yml), header comment). User-visible changes are recorded on the docs page and in this feature record, as every other feature does. Not creating one here: that is a repository-wide decision, not this feature's. |
| 3 | "`dotnet dnx --yes fallen-8-mcp --stdio`". | Both spellings exist in the .NET 10 SDK: `dnx` is a thin launcher for `dotnet dnx`. The contract below names `dnx fallen-8-mcp --stdio` as the plugin's launch line and tests both. |
| 4 | Lists seven index routes to bridge. | **Incomplete.** The snapshot also has `PUT /index/{indexId}/batch` ([GraphController.Index.cs:253](../../../fallen-8-core-apiApp/Controllers/GraphController.Index.cs#L253)), the token-frugal way to populate an index. It is matched by the same `" /index"` deferral and is bridged too (`add_many`), because deleting that deferral makes it uncovered otherwise. |
| 4 | "the ToolCatalog tier test". | There is no test class by that name. Tier gating is pinned in `McpToolSurfaceTest` (`ListTools_DefaultTiers_ExposesOnlyReadTools`, `ListTools_WriteEnabled_ExposesWriteTools`, `CallTool_DisabledTier_IsRejectedEvenWhenNameIsKnown`). New tools extend those. |
| 6 | "register (code tier)". | `ToolTier` has three members: Read, Write, Admin ([McpTool.cs:38](../../../fallen-8-mcp/Tools/McpTool.cs#L38)). Code is a **capability** checked per op inside a tool, the way `f8_plugins` does it ([PluginsTool.cs:76](../../../fallen-8-mcp/Tools/PluginsTool.cs#L76)). `f8_storedquery` follows that pattern (section 8), so its list/get ops stay visible to read-only agents, which the review's "write tier" placement would have hidden. |
| 2 | Add `modelName` and `dimension` to the overview node. | Accepted in substance; the overview node already prefixes every embedding field (`embeddingEnabled`, `embeddingBackend`), so the new ones are `embeddingModel` and `embeddingDimension`. The DTO property rename is as the review says. |
| 9 | "the same eleven tools". | True today: Overview, Get, Search, Paths, Analytics, Plugins, Documents, Mutate, Subgraph, Namespace, Admin. After items 4 and 6 there are **thirteen**. The plugin's embedded tier (item 9) and any plugin-side tool list must track that; flagged for the plugin author in section 12. |
| 1 | "`properties` ... Only the array form survives". | **Confirmed**, and the consequence is worse than stated: the surviving schema is `additionalProperties:false`, so a client that validates arguments against `inputSchema` rejects every `create_vertex` call that carries an object `properties`. A non-validating client (Claude Code today) gets through because the runtime never looks at the schema. |

Everything else in the review was confirmed as written. The line references in the review were
all current at `4517ee75`; nothing had moved.

## 3. Item 1: the `f8_mutate` schema overwrites itself

**Finding.** [SchemaBuilder.cs:131](../../../fallen-8-mcp/Tools/SchemaBuilder.cs#L131) assigns
`_properties[name] = property`, so a second `Add` for the same name silently replaces the first
and the `required` list may carry a name twice. `MutateTool.Describe` adds `properties` as an
object at [MutateTool.cs:84](../../../fallen-8-mcp/Tools/MutateTool.cs#L84) and again as an array
at [line 92](../../../fallen-8-mcp/Tools/MutateTool.cs#L92); only the array survives. `ids` is
declared `ObjArray` at [line 93](../../../fallen-8-mcp/Tools/MutateTool.cs#L93) while the runtime
([line 301 onward](../../../fallen-8-mcp/Tools/MutateTool.cs#L301)) rejects anything but integers.
No other tool declares a duplicate name (checked by reading all eleven `Describe` methods; the
test below makes that a build fact rather than a reading).

**Decision.**

- `SchemaBuilder.Add` **throws** `InvalidOperationException` on a duplicate name. The builder is
  called once per `tools/list`, from `Describe`, so a duplicate becomes a loud failure of the
  first `tools/list` and of the test that enumerates every tool. A silent overwrite is the defect.
- The batch form of `set_properties` is renamed **`updates`** (`ObjArray`). `properties` goes
  back to the single-create object map. Runtime compatibility: `set_properties` reads `updates`,
  and when it is absent and `properties` is present AND an array, uses that, so a caller written
  against the current server keeps working. The fallback is a runtime courtesy only; the schema
  advertises `updates`.
- `SchemaBuilder` gains **`IntArray`** (items `type:integer`) and **`Num`** (`type:number`, needed
  by item 5 for `maxPathWeight`). `ids` becomes `IntArray`.

**Tests.**

- `McpToolSurfaceTest.EveryTool_Describe_DeclaresNoDuplicateSchemaName`: for every registered
  `IMcpTool` and for all eight combinations of the three capability flags (the schema varies by
  caps), `Describe` succeeds and the `properties` object has no repeated key and `required` has
  no repeated entry. Against the current code this fails on `f8_mutate` for the stated reason
  only once the builder throws; so the test is written against the builder's new contract and the
  builder change lands first (plan phase 1).
- `SchemaBuilderTest.Add_SameNameTwice_Throws`.
- `McpToolSurfaceTest.EveryTool_SampleCall_ValidatesAgainstItsAdvertisedSchema`: one sample valid
  call per op of every tool (a table in the test), validated against the tool's `inputSchema` by a
  small flat-schema checker in the test project (object with typed properties, `required`,
  `additionalProperties:false`, `enum`, `items.type`). The checker is about sixty lines and
  covers exactly the shape `SchemaBuilder` can produce, which the existing
  `Overview_Schema_IsFlatEnumDiscriminated_NoComposition` already pins. No JSON Schema package is
  added: a full validator would be a new dependency for a schema subset the repo controls.
- `McpWriteToolsTest`: `set_properties` with `updates`, and once with the legacy `properties`
  array, both reach `PUT /graphelements/properties`; `remove_elements` with a non-integer is a
  400 tool error (exists; keep).

**Docs.** The `f8_mutate` row in the tool table names `updates`.

## 4. Item 2: `f8_overview` must report ingestion, Docling and NLP state

**Finding.** [DocumentsTool.cs:86](../../../fallen-8-mcp/Tools/DocumentsTool.cs#L86) tells the
agent to consult `f8_overview` for whether ingestion is enabled, and the overview reports nothing
about it. `GET /status` carries `ingestion` and `nlp` blocks
([StatusREST.cs](../../../fallen-8-core-apiApp/Controllers/Model/StatusREST.cs), properties
`Ingestion` and `Nlp`), with the shapes in
[IngestionStatsREST.cs](../../../fallen-8-core-apiApp/Controllers/Model/IngestionStatsREST.cs)
and [NlpStatsREST.cs](../../../fallen-8-core-apiApp/Controllers/Model/NlpStatsREST.cs). The bridge
DTO ignores both. Separately, `EmbeddingStateDto` declares `Model` and `Dimensions`
([StatusDto.cs:98](../../../fallen-8-mcp/Bridge/Dto/StatusDto.cs#L98),
[line 100](../../../fallen-8-mcp/Bridge/Dto/StatusDto.cs#L100)) while the API sends `modelName`
and `dimension` ([GraphStatisticsREST.cs:377](../../../fallen-8-core-apiApp/Controllers/Model/GraphStatisticsREST.cs#L377),
[line 392](../../../fallen-8-core-apiApp/Controllers/Model/GraphStatisticsREST.cs#L392)). Those
two DTO fields have never bound to anything; nothing reads them today, which is why no test
noticed. `ChatStateDto.Model` does match `ChatProviderStatsREST.Model`.

**Decision.**

- `EmbeddingStateDto`: rename to `ModelName` and `Dimension` (nullable `Int32?` stays, since an
  absent block must not read as dimension 0).
- New `IngestionStateDto { Enabled, Docling { Configured, Reachable }, EmbeddingName,
  VectorIndexId, FulltextIndexId }` and `NlpStateDto { Enabled, Configured, Reachable }` on
  `StatusDto`, both nullable (an older target sends neither).
- Overview node gains `ingestionEnabled`, `doclingConfigured`, `doclingReachable`, `nlpEnabled`,
  `nlpReachable`, `embeddingModel`, `embeddingDimension`. The three booleans that come from an
  absent block are `false`, matching how `embeddingEnabled` already behaves; the nullable strings
  and the dimension stay `null` when unreported, for the reason `indexCount` stays absent (a
  guessed value is a fact an agent would act on). `ingestionVectorIndexId` and
  `ingestionEmbeddingName` are included too, because `f8_documents` tells the agent to bind an
  index and the overview should name the one that is bound.

**Why the binding defect could happen, and the check that stops it recurring.** The bridge's DTOs
are hand-written mirrors of REST shapes with no parity test on the READ side;
`McpWriteDtoParityTest` guards only the three write bodies. A new
**`McpStatusDtoParityTest`** checks one direction: every property on `StatusDto` and its nested
DTOs must exist on the corresponding REST type with the same effective JSON name (the test
computes names the way `McpWriteDtoParityTest` does). The MCP side is allowed to be a subset; a
field it declares that the REST side does not send is the exact defect. This is a derived check
over the whole family rather than a fix at the one site the review found.

**Tests.** The parity test above (red on `Model`/`Dimensions` before the rename, green after).
`McpReadToolsTest`: an overview against a hosted apiApp with ingestion off reports
`ingestionEnabled:false`, `doclingConfigured:false`, and a non-null `embeddingModel` when the
test host's provider is on; against a status body with no `ingestion`/`nlp` member at all the
booleans are `false` and the strings `null`.

**Docs.** The `f8_overview` row in the tool table lists the new fields; the `f8_documents`
remark about `f8_overview` becomes true.

## 5. Item 3: publish `fallen-8-mcp` as a .NET tool

**Finding.** [fallen-8-mcp.csproj:8](../../../fallen-8-mcp/fallen-8-mcp.csproj#L8) has
`IsPackable=false` with the comment "Only the engine ships as a NuGet package". The release
workflow packs only the engine
([release.yml:207](../../../.github/workflows/release.yml#L207)). The plugin's native tier
expects `dnx fallen-8-mcp --stdio`, which needs a tool package with that id on nuget.org.
`McpHost.ResolveTransport` ([McpHost.cs:58](../../../fallen-8-mcp/Hosting/McpHost.cs#L58)) honours
`--stdio` first and `Mcp__Transport=stdio` second; that order is kept.

**Decision.**

- csproj: `IsPackable=true`, `PackAsTool=true`, `ToolCommandName=fallen-8-mcp`,
  `PackageId=fallen-8-mcp` (lower-case because `dnx` resolves the package id and the plugin was
  written against that spelling; NuGet ids are case-insensitive, so this does not collide with
  anything the engine package owns). Package metadata mirrors the engine's: `Authors`,
  `PackageLicenseExpression=MIT`, `RepositoryUrl`, `PublishRepositoryUrl`, a `PackageReadmeFile`
  pointing at a short `fallen-8-mcp/README.md` written for the nuget.org package page (what it
  is, the one env var it needs, the docs link). Version comes from MinVer through
  `Directory.Build.props` as it does for the engine. The stale "only the engine" comment is
  replaced by one sentence pointing at this section.
- `appsettings.json` must ship inside the tool package and be found at runtime from the tool's
  own directory, not the current directory; the host already builds configuration from the
  content root, which for a tool is the package's `tools/` folder. Verified in phase 3 by running
  the packed tool from an unrelated directory.
- Release workflow: the `nuget` job packs `fallen-8-mcp/fallen-8-mcp.csproj` into the same
  `packages/` folder; the existing push steps already glob `packages/*.nupkg` and
  `packages/*.snupkg`, so no new push step. The job's name and the header comment list the second
  package. **Operator step, outside the repo:** the nuget.org Trusted Publishing policy for this
  workflow must allow the new package id before the first tagged release that carries it;
  otherwise the push answers 403 for that one id and the release job fails loudly, which is the
  behaviour the workflow comment already promises for credential trouble. The plan names this
  as a pre-release check, not a code change.
- The Web SDK (`Microsoft.NET.Sdk.Web`) is kept; a tool package from a Web SDK project is
  supported, but it is the one part of this item nobody here has done before, so the plan starts
  with a local pack and a `dnx --source ./packages` run before the workflow is touched.

**Contract.** `dnx fallen-8-mcp --stdio` (or `dotnet dnx --yes fallen-8-mcp --stdio`) starts the
stdio transport with `--stdio` intact (the launcher passes trailing arguments through). With no
configuration the tool bridges to `Fallen8Target:BaseUrl` default `http://localhost:8080`, which
is what the docs' configuration reference already states.

**Tests.** `McpTransportTest.ResolveTransport_StdioFlag_SurvivesLauncherArgumentOrder` pins that
`--stdio` is recognised anywhere in `args` (a launcher may prepend its own). The pack itself is
verified by the plan's phase 3 checklist (a packed tool run end to end), not by a unit test, since
packing is an MSBuild act.

**Docs.** "Running it" on the docs page gains the tool form as the first-listed way to run it
locally; it says what it bridges to and that the Claude Code plugin's native tier launches it.

## 6. Item 4: bridge the index lifecycle as `f8_index`

**Finding.** The eight `/index` routes are matched by the `" /index"` deferral at
[McpRestCoverageTest.cs:80](../../../fallen-8-unittest/McpRestCoverageTest.cs#L80) with the reason
"operator/setup tooling". Since then `f8_search mode:index` and `f8_mutate op:set_embedding`
landed, and both need an index the agent cannot create. The only MCP path that creates one is
`f8_documents op:bind`, with the fixed id `documents`. The REST quirk is real:
`CreateIndex` ([GraphController.Index.cs:175](../../../fallen-8-core-apiApp/Controllers/GraphController.Index.cs#L175))
returns the `TryCreateIndex` boolean, so a taken id and an unknown plugin type are both
`200 false`.

**Decision.** New `f8_index` tool, **write tier**, ops:

| op | REST | arguments |
|---|---|---|
| `create` | `POST /index` | `indexId`, `pluginType`, `options` (object, JSON-native values; the bridge maps each entry to a `PropertySpecification` through `ValueMapping`, so `{dimension: 384, metric: "Cosine", embeddingName: "text", model: "..."}` is what a VectorIndex reads at [VectorIndex.cs:108](../../../fallen-8-core/Index/Vector/VectorIndex.cs#L108) onward) |
| `add` | `PUT /index/{id}` | `indexId`, `id`, `key` (JSON-native) |
| `add_many` | `PUT /index/{id}/batch` | `indexId`, `entries` (array of `{id, key}`) |
| `add_vector` | `PUT /index/vector/{id}` | `indexId`, `id`, exactly one of `vector` or `propertyId` (the server's 400 for "exactly one" passes through) |
| `remove_element` | `DELETE /index/{id}/{elementId}` | `indexId`, `id` |
| `remove_key` | `DELETE /index/{id}/propertyValue` | `indexId`, `key` |
| `delete` | `DELETE /index/{id}` | `indexId` |
| `backfill` | `POST /index/backfill/{id}` | `indexId`, `propertyId`, `replace`, `prefix`, `label`; returns the `IndexRebuildREST` counts |

The argument is `indexId` rather than the review's `uniqueId`, because every other op in the tool
and `f8_search` already call it `indexId`; one name per concept.

**The `200 false` translation.** `create`, `add`, `remove_element`, `remove_key` and `delete`
all answer a bare boolean. The bridge maps `false` to an `isError` result with status **409**
and a message that states both possible causes honestly, since REST does not distinguish them:
"index not created: the id is taken or the plugin type is unknown; `availableIndexPlugins` on
`f8_overview` lists the types and `indexCount`/`detail:"statistics"` the existing ids". For
`add`/`remove_*`/`delete`, `false` means "no such index" or "the element was not in it"; the
message says which op it was and that nothing changed. Mapping a boolean to a status is a
bridge-side interpretation; the message says so in one clause so the agent can tell interpretation
from a server answer.

**Why write tier and not admin.** Creating an index is a per-namespace data structure, the same
class of act as `f8_documents op:bind` (write) and `f8_subgraph` (write). Admin is reserved for
process-level acts (load, trim, tabula rasa, settings).

**Tests.** `McpToolSurfaceTest`: `f8_index` is absent at default tiers and present with write;
schema flat, no duplicate names (section 3's test covers it). `McpWriteToolsTest`: every op
round-trips against the hosted apiApp (create a DictionaryIndex, add, add_many, search it with
`f8_search mode:index`, remove_key, remove_element, backfill with counts, delete; create a
VectorIndex with `dimension`, `set_embedding` on an element, `add_vector` by `propertyId` and by
`vector`, search `mode:semantic`); the `200 false` paths (taken id, unknown type, delete twice)
are 409 `isError` with the stated message. `McpWriteDtoParityTest` gains the pairs
`IndexAddToSpecification`/`IndexAddDto`, `VectorIndexAddSpecification`/`VectorIndexAddDto`,
`IndexBackfillSpecification`/`IndexBackfillDto`, `PluginSpecification`/`IndexCreateDto`.
`McpBridgedEndpoints` gains the eight routes; the `" /index"` deferral is deleted (the
disjointness test requires deletion, not narrowing). `McpContractTest` pins the eight against
the snapshot without change.

**Docs.** New row in the tool table; the "Tiers" section lists `f8_index` under write.

## 7. Item 5: forward `semantic`, `patterns`, `maxPathWeight`, `timeBudgetSeconds`

**Finding.** `PathSpecification` carries `MaxPathWeight`, `TimeBudgetSeconds` and `Semantic`
([PathSpecification.cs:135](../../../fallen-8-core-apiApp/Controllers/Model/PathSpecification.cs#L135),
[159](../../../fallen-8-core-apiApp/Controllers/Model/PathSpecification.cs#L159),
[203](../../../fallen-8-core-apiApp/Controllers/Model/PathSpecification.cs#L203));
`SubGraphSpecification` carries `Patterns` and `Semantic`
([SubGraphSpecification.cs:106](../../../fallen-8-core-apiApp/Controllers/Model/SubGraphSpecification.cs#L106),
[152](../../../fallen-8-core-apiApp/Controllers/Model/SubGraphSpecification.cs#L152)).
`PathsTool` forwards neither of its three ([PathsTool.cs:62](../../../fallen-8-mcp/Tools/PathsTool.cs#L62)
onward) and `SubgraphTool` forwards neither of its two
([SubgraphTool.cs:61](../../../fallen-8-mcp/Tools/SubgraphTool.cs#L61) onward). All of it is pure
data: the semantic block is `{queryVector | queryText, embeddingName, metric, minScore,
costBySimilarity}` and a pattern is `{type, patternName, vertexFilter, semanticMinScore,
direction, edgePropertyFilter, edgeFilter, minLength, maxLength}`. Two of the pattern fields
(`vertexFilter`, `edgeFilter`, `edgePropertyFilter`) are C# fragments, so a pattern carrying them
is code; the server compiles them and the MCP code capability is the agreed gate for inline code.

**Decision.**

- `f8_paths` gains `semantic` (`Obj`), `maxPathWeight` (`Num`), `timeBudgetSeconds` (`Num`).
- `f8_subgraph` gains `semantic` (`Obj`) and `patterns` (`ObjArray`).
- Both forward the block as the raw `JsonElement` into the request body; the bridge DTOs in
  `PathAndAnalyticsDto.cs` gain `JsonElement?` members so the server's own validation and its
  one-owner-per-slot 400s ([GraphController.Path.cs:239](../../../fallen-8-core-apiApp/Controllers/GraphController.Path.cs#L239),
  [245](../../../fallen-8-core-apiApp/Controllers/GraphController.Path.cs#L245)) pass through
  unchanged as 400 tool errors. The bridge does not re-validate a shape the server owns.
- `patterns` whose entries carry any of the three fragment fields require the **code**
  capability, checked in the tool before the call (403 tool error naming
  `Mcp:Tools:EnableCode`), the same rule inline `vertexFilter` follows today. Code-free patterns
  (label/type/direction/length/`semanticMinScore` only) are forwarded without it.
- `semantic.queryText` needs an embedding provider on the target; when the target has none the
  server answers its own error, which passes through. The schema description says "use
  `queryVector` when `f8_overview` reports `embeddingEnabled:false`".
- `SemanticTraversalSpecification` has two further members, `EmbeddingBackend` and
  `EmbeddingIdentity` ([SemanticTraversalSpecification.cs:116](../../../fallen-8-core-apiApp/Controllers/Model/SemanticTraversalSpecification.cs#L116),
  [127](../../../fallen-8-core-apiApp/Controllers/Model/SemanticTraversalSpecification.cs#L127)).
  Phase 5 reads their doc comments to decide whether they are client-settable; if they are
  server-stamped, the schema description for `semantic` names only the five agent-facing fields
  and the raw forward still carries whatever the agent sent, so the server stays the judge.

**Tests.** `McpReadToolsTest`: a path call with `semantic.queryVector` and `costBySimilarity`
reaches the server and ranks; `maxPathWeight` and `timeBudgetSeconds` appear in the sent body (the
bridge test's captured-request handler); a subgraph with a code-free `patterns` entry is forwarded
without the code capability, one with `vertexFilter` is 403 without it and forwarded with it; the
server's slot-conflict 400 arrives as a 400 tool error with the server's sentence.

**Docs.** The `f8_paths` and `f8_subgraph` rows name the new knobs and the code rule for patterns.

## 8. Item 6: bridge stored-query registration as `f8_storedquery`

**Finding.** The four `/storedquery` routes are deferred at
[McpRestCoverageTest.cs:82](../../../fallen-8-unittest/McpRestCoverageTest.cs#L82) as
"code-gated setup". `storedQuery` on `f8_paths` and `f8_subgraph` can only name what somebody
registered over REST; an agent cannot even list what exists. The controller
([StoredQueriesController.cs](../../../fallen-8-core-apiApp/Controllers/StoredQueriesController.cs))
answers 201 with a `StoredQuerySummaryREST` (`name, kind, description, createdAt,
compileState`), 409 on a taken name, 400 with the compiler's message on a failed compile;
`GET /storedquery/{name}` returns `StoredQueryDetailREST` adding `specificationJson` and
`compileDiagnostics`; `DELETE` is 204 or 404.

**Decision.** New `f8_storedquery` tool, **read tier**, with per-op capability checks on the
`f8_plugins` pattern:

| op | REST | capability | returns |
|---|---|---|---|
| `list` | `GET /storedquery` | read | summaries with `compileState` |
| `get` | `GET /storedquery/{name}` | read | the detail, `specificationJson` parsed back into a JSON object (an agent should not get a string of JSON), `compileDiagnostics` |
| `delete` | `DELETE /storedquery/{name}` | write | applied |
| `register` | `POST /storedquery` | **code** | the summary, with `compileState` |

`register` takes `name`, `kind` (`path` or `subgraph`), `description`, and `path`
(`{filter, cost}`) or `subgraph` (`{vertexFilter, edgeFilter, patterns}`) as objects forwarded
raw, the same `StoredQuerySpecification` the REST route takes. It is code-gated because the
body IS C# fragments; the server's 400 compile message and 409 conflict pass through. The op
enum in the schema varies by capability exactly as `f8_plugins` does, so a read-only agent sees
`list` and `get` only.

A separate tool rather than ops on `f8_plugins`: stored queries and plugins are different
registries with different REST roots, and the tool table reads by registry.

**Tests.** `McpToolSurfaceTest`: the op enum per capability combination. `McpReadToolsTest` /
`McpWriteToolsTest`: register a path query (code on), list and get show
`compileState:Compiled` and a parsed specification, `f8_paths storedQuery:<name>` uses it, delete,
get is 404; register with a non-compiling fragment is a 400 tool error carrying the compiler's
message; register twice is 409; register without code is 403; delete without write is 403.
`McpBridgedEndpoints` gains the four routes; the `/storedquery` deferral is deleted.

**Docs.** New row; the "Tiers and the code capability" section lists `register` on
`f8_storedquery` next to `register_algorithm`.

## 9. Item 7: an honest readiness probe

**Finding.** [Program.cs:179](../../../fallen-8-mcp/Program.cs#L179) answers `/healthz` with
`{"status":"ok"}` unconditionally. The origin and bearer checks at
[Program.cs:146](../../../fallen-8-mcp/Program.cs#L146) exempt `/healthz` and the OAuth metadata
path by prefix. A container orchestrator or the compose `depends_on` reads "ok" from a server whose
target is down.

**Decision.** `/healthz` stays liveness and stays as it is. New **`GET /readyz`**:

- Performs one `GET /status` against the target's default namespace through the bridge, under a
  **linked cancellation of 3 seconds** (configurable as `Mcp:Readiness:TimeoutSeconds`, clamped
  like the target timeout), independent of `Fallen8Target:TimeoutSeconds` (330 s, far too long
  for a probe).
- `/status` is chosen over `/vertex/count` because it is the one namespace-scoped route that
  answers keyless and for a not-loaded namespace, so it measures reachability and nothing else;
  and because its body says whether the bridge's credential was accepted (`apiKeyRequired`,
  `authenticated`). Its Docling and NLP reachability probes are cached server-side, so it is cheap.
- Answers `200 {"status":"ready","target":"<base url>","authenticated":true|false}` when the
  call succeeds and either no key is required or the key was accepted. Answers **503** with
  `{"status":"unready","reason":"..."}` when the call fails (the `BridgeError` title and status, or
  "timed out after 3s"), **and** when `apiKeyRequired && !authenticated`, with the reason
  "the target requires an API key and rejected the configured one", because every bridged call
  will then fail and reporting "ready" would be the same lie in a second form.
- Exempt from origin and bearer checks like `/healthz`; not rate-limited either (the limiter
  exemption is checked in phase 7, since a probe at a 10 s interval must not consume the window).
- Under stdio there is no HTTP listener, so no `/readyz`; the stdio posture line already logs the
  target at start.

**Tests.** `McpTransportTest`: `/readyz` is reachable without a bearer and with a foreign
`Origin`; 503 with the timeout reason when the target handler never answers; 503 with the bridge
status when it answers 500; 503 with the credential reason on a body with
`apiKeyRequired:true, authenticated:false`; 200 with `authenticated:true` otherwise. The test
drives it through the same hosted harness `McpTransportTest` already uses for `/healthz`.

**Docs.** "Running it" gets two sentences on `/healthz` versus `/readyz`; the compose file's
`f8-mcp` healthcheck, if one exists, moves to `/readyz` (phase 7 checks; the agents service
depends on `f8-mcp`, so a readiness-gated dependency is the point).

## 10. Item 8: three stale sentences

- [mcp-server.md:51](../../../docs/src/content/docs/mcp-server.md#L51): "No registry publishes it
  yet, so build it first" is false since the release workflow's image matrix. Replace with the
  pull form (`docker pull ghcr.io/cosh/fallen-8-core-mcp:<tag>`) and keep the build form as the
  alternative for an unreleased tree.
- [DoclingClient.cs:37](../../../fallen-8-core-apiApp/Ingestion/DoclingClient.cs#L37): the class
  summary names `POST /v1/convert/file`; the method summary at line 75 and the code at lines 112,
  130 and 157 use the async task API (`convert/file/async`, `status/poll/{id}`, `result/{id}`).
  The class summary becomes a one-line pointer to the method that owns the explanation (one home
  per explanation).
- "Connecting a client" gains a third block for the Claude Code plugin:

  ```bash
  claude plugin marketplace add cosh/fallen-8-claude-plugin
  claude plugin install fallen-8@fallen-8
  ```

  with the note that the plugin reads `FALLEN8_URL` (the MCP server's URL, `http://localhost:8090`
  for the compose environment) and that its native tier launches the tool package from item 3.
  The repository does not own the plugin; the docs page links it and states no more about its
  internals than those two facts, so a plugin change does not stale this page.

## 11. Impact on existing features

Swept per the mandatory cross-feature check: engine, REST contract, OpenAPI snapshot, Studio UI,
NL-assist dataset, feature READMEs, docs-site pages, architecture diagrams, persisted recipes.

- **Engine / REST / OpenAPI snapshot:** untouched. No route changes, so no snapshot regeneration.
- **McpRestCoverageTest / McpBridgedEndpoints / McpContractTest:** two deferrals deleted, twelve
  routes bridged (sections 6 and 8). Disjointness holds because the deferrals are deleted, not
  narrowed.
- **McpWriteDtoParityTest:** four new pairs (section 6). New `McpStatusDtoParityTest` (section 4).
- **Studio UI:** none. Studio has its own index workspace and stored-query screens over REST and
  reads nothing from the MCP server.
- **NL-assist dataset / eval:** none; the NL-assist path produces REST bodies, not MCP calls. No
  `RETRAIN-LOG.md` entry.
- **features/done/mcp-server/README.md (living doc):** the tool count (eleven to thirteen), the
  test map (two new test classes), the packaging sentence ("never an engine embedding" stays
  true; "a deployable" gains "also a .NET tool package"). One-line pointers to this record.
- **features/done/index-lifecycle, stored-query-library, element-embeddings:** each README's
  "reach it from MCP" statement, if any, gets a one-line pointer; the explanation stays here.
- **docs-site:** `mcp-server.md` (all items), and the one-line "Key features" entry for the MCP
  server in the root `README.md` gains the `dnx` launch form. `architecture.md`: no new channel
  or deployable; the MCP server's box is unchanged (item 9 is the one that changes the diagram,
  in its own feature).
- **Release pipeline:** one more pack line and the operator-side Trusted Publishing policy check
  (section 5). The memory note on the release pipeline should record the second package once it
  has shipped.
- **docker-compose.yml:** possibly the `f8-mcp` healthcheck target (section 9).
- **The plugin repository (external):** the tool count changes, `set_properties` gains `updates`,
  `f8_overview` gains fields, `/readyz` exists. Section 12 is the handoff list for its author.

## 12. Handoff to the plugin author

Not a repo artifact; the list the PR description carries so the plugin can follow:

1. Thirteen tools after this feature: `f8_index` (write) and `f8_storedquery` (read, with
   write/code ops).
2. `f8_mutate op:set_properties` batch argument is `updates`; `properties` (array) still works at
   runtime but is no longer advertised.
3. `f8_overview` reports `ingestionEnabled`, `doclingConfigured`, `doclingReachable`,
   `nlpEnabled`, `nlpReachable`, `embeddingModel`, `embeddingDimension`,
   `ingestionVectorIndexId`, `ingestionEmbeddingName`.
4. `GET /readyz` on the HTTP transport; `/healthz` is liveness only.
5. `dnx fallen-8-mcp --stdio` resolves once the first tagged release after merge is published.

## 13. Verification (every item)

- `dotnet build fallen-8-core.sln` and `dotnet test fallen-8-core.sln` green, never with `-v q`.
- Live: apiApp on `:5000` and the MCP server on `:8090` with write, admin and code on; a
  Streamable HTTP client runs `initialize`, `tools/list` (thirteen tools), and one call per changed
  tool: `f8_mutate set_properties` with `updates`; `f8_overview` on `default` showing the new
  fields; `f8_index create`, `add_many`, `f8_search mode:index`, `backfill`, `delete`, and a
  `create` on a taken id (409); `f8_paths` with `semantic.queryVector` and `maxPathWeight`;
  `f8_subgraph` with `patterns`; `f8_storedquery register`, `list`, `get`, `delete`; `curl
  /readyz` with the apiApp up (200) and stopped (503 with reason). Transcripts go in the PR
  description, per the review's request.
- Packed tool: `dotnet pack fallen-8-mcp -c Release -o ./packages` then
  `dnx --yes --source ./packages fallen-8-mcp --stdio` from an unrelated directory answers an
  `initialize` on stdin with `--stdio` honoured and `appsettings.json` found.
- Docs: `npm --prefix docs ci && npm --prefix docs run build` green (link check).
- Hard rules before commit: no em or en dash in any changed file (checked in Python, not Bash),
  none of the three forbidden external names, MIT header on every new file, exact package
  versions (no new packages are expected).

## 14. What the implementation changed about this document

Written during implementation on 2026-10-06, one entry per section whose claims the code
corrected. The sections above are not rewritten; this is the record of where they were wrong.

- **Section 3 (f8_mutate).** "`remove_elements` with a non-integer is a 400 tool error (exists;
  keep)" was false: neither `set_properties` nor `remove_elements` had any MCP test. Writing one
  found that the batch's `remove: true` entry had NEVER worked through the tool: the DTO sent an
  empty `propertyValue`, which the REST model's `[Required]` refuses (it rejects empty strings as
  well as nulls), so every remove entry was a 400. The value is now absent on the wire for a
  removal. Also, `f8_get` reports a missing element as `found:false`, not as an error; the test
  first assumed otherwise.
- **Section 4 (f8_overview).** The node also carries `ingestionFulltextIndexId`, since the
  ingestion block names it next to the vector index. The live test compares the overview with the
  same host's raw `/status` body rather than with literals, because what the test host's provider
  reports depends on whether its model is loaded. The parity test found no further phantom field:
  `IndexDto.Keys`/`Values` do exist on the REST side.
- **Section 5 (tool package).** The package is 2.2 MB, not tens of MB. Two defects were found
  by the spike and fixed in the host: the content root was the caller's directory, so
  `appsettings.json` was never loaded by a tool launch, and the posture line printed the
  configured `Mcp:Transport` rather than the resolved one, announcing `transport=http` under
  `--stdio`. An XML comment cannot hold `--stdio`, so the csproj comment spells it out in words.
- **Section 6 (f8_index).** A vector index BOUND to an embedding name refuses `add_vector` by
  design ("maintains itself; write the element embedding instead"), so the test fills an unbound
  index through `add_vector` and a bound one through `set_embedding`, and pins the refusal passing
  through. A bare `false` from `add`/`remove_*`/`delete` is reported as 404 (every cause is
  "something named here does not exist"); only `create` is 409. Both say the status is the
  bridge's reading.
- **Section 7 (semantic, patterns).** `EmbeddingBackend` and `EmbeddingIdentity` are
  server-owned and discarded on input, so the raw forward is safe. The live witness is
  `semantic.minScore` removing and keeping a path by the fixture's embeddings, not
  `costBySimilarity` ranking; the byte-equality assertion became "the three keys are absent from a
  knob-free body", which pins the same fact without a stored literal.
- **Section 8 (f8_storedquery).** The kind values are the REST contract's `Path` and `SubGraph`,
  not the lower-case spelling in the table. The tool's `readOnlyHint` follows the widest advertised
  surface: true with no write or code capability, false once `delete` or `register` is listed.
- **Section 9 (/readyz).** `/healthz` WAS inside the rate limiter, and the existing limiter test
  counted its three requests on it; both probes are exempt now and the test counts on the MCP
  endpoint. The compose file already had a `/healthz` healthcheck on `f8-mcp` with `f8-agents`
  waiting on it; it points at `/readyz` now.
- **Section 10 (docs).** The index page is `/indexes/`, there is no `/index-lifecycle/` page. The
  feature records of index-lifecycle (no README), stored-query-library and element-embeddings
  contain no MCP sentence, so no pointer was owed there.
- **Section 11.** `McpWriteDtoParityTest`'s name computation moved to `McpTestSupport` so the new
  `McpStatusDtoParityTest` shares it rather than copying it.
- **Section 12 (handoff).** Confirmed against what shipped, with two additions: the overview also
  reports `ingestionFulltextIndexId`, and `f8_storedquery register` takes `kind` as `Path` or
  `SubGraph`.
