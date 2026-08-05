# Audit defects: the verified bug list

A freshness audit of the 31 documentation pages against the code (2026-08-05) meant reading the implementation
behind every documented claim, which surfaced defects in the code itself. Each suspected defect then went through
an independent pass whose default posture was to refute it, so every entry below carries a citation someone read.

**35 confirmed**, 7 needing a maintainer decision, 14 refuted or duplicate, and 22 further defects found while
verifying that nobody had raised.

Severity is user impact: `critical` = data loss, silent corruption, or an auth hole. `high` = wrong results or a
broken contract. `medium` = misleading output, a wrong default, a dead-end feature. `low` = comments, logs,
cosmetics. The `Status` column is maintained as the fixes land.

## Confirmed

| ID | Sev | Defect | Status |
| --- | --- | --- | --- |
| [B04](#b04) | high | Runtime-registered Analytics plugins are unreachable: /analytics/{name} pre-checks built-ins only | **fixed** |
| [B10](#b10) | high | VertexModel.GetAllNeighbors projects in-edges through TargetVertex, yielding InDegree copies of the vertex itself instead of its i [...] | **fixed** |
| [B14](#b14) | high | DELETE /index/{indexId}/{graphElementId} silently desyncs a bound vector index | **fixed** |
| [B18](#b18) | high | REST projection renders modificationDate as 1970 + delta instead of creationDate + delta | **fixed** |
| [B25](#b25) | high | A leading Edge pattern silently ignores its own edgePropertyFilter | **fixed** |
| [B26](#b26) | high | Recalculating a parent orphans its nested children on the discarded parent instance | **fixed** |
| [B39](#b39) | high | PUT /load with a nonexistent path answers 204 and registers a phantom "newest" save game that aborts the next startup | **fixed** |
| [B01](#b01) | medium | Offline pre-seed script never pulls bge-m3, so the default-on embedding provider has no model | **fixed** |
| [B05](#b05) | medium | PUT /vertex published sample cannot deserialize (properties as object map, creationDate as ISO string) | **fixed** |
| [B06](#b06) | medium | Every documented `operator` value is a string that is neither the wire format nor a real enum member | **fixed** |
| [B07](#b07) | medium | A second <remarks> block is silently dropped from the OpenAPI description, losing the SECURITY/SEMANTIC notes on all three code en [...] | **fixed** |
| [B12](#b12) | medium | Path screen lets you build costBySimilarity + an inline vertexFilter, which the server rejects with 400 | **fixed** |
| [B16](#b16) | medium | POST /document and /document/text declare statuses (413 for MaxPages/MaxChunksPerDocument, 502) that the async pipeline can never [...] | **fixed** |
| [B17](#b17) | medium | Entity-type examples say PER/ORG/LOC, but the shipped spaCy models emit OntoNotes labels (PERSON/ORG/GPE) | **fixed** |
| [B19](#b19) | medium | Unknown or unconvertible property type on the create routes escapes as 500, not the documented 400 | **fixed** |
| [B24](#b24) | medium | A filterless POST /path still runs Roslyn and loads a collectible assembly (no "nothing to compile" short-circuit) | **fixed** |
| [B28](#b28) | medium | A runtime-registered ISubGraphAlgorithm is registerable and discoverable but not invocable | **fixed** |
| [B29](#b29) | medium | OpenAPI advertises maxPathWeight default 100 while the runtime default is unbounded | **fixed** |
| [B31](#b31) | medium | PATCH /ns/{name} commits and persists the rename before validating pluginRegistration, so a 400 can leave the namespace renamed | **fixed** |
| [B42](#b42) | medium | POST /service documents a pluginType that resolves against IService only - and no IService plugin ships at all | **fixed** |
| [B49](#b49) | medium | f8_paths advertises a closed BLS/DIJKSTRA enum, so agents cannot select a registered path plugin | **fixed** |
| [B51](#b51) | medium | env-info.js advertises OpenAPI and Scalar URLs that 404 on the compose container | **fixed** |
| [B52](#b52) | medium | /statistics leaks the R-Tree's -1 "count not supported" sentinel as a real key count | **fixed** |
| [B53](#b53) | medium | Vite dev proxy allowlist is stale: no /ns (nor /statistics, /config, /chat, /bulk, /embedding, /analytics, /storedquery, /document [...] | **fixed** |
| [B08](#b08) | low | StoredQuerySecurityMatrixTest still named/commented around a removed "switch" | **fixed** |
| [B09](#b09) | low | Subgraph compile path has no fragment/generated-source length cap, unlike /path | **fixed** |
| [B13](#b13) | low | Export 422 detail blames the type allow-list for an invalid-UTF-16 refusal | **fixed** |
| [B20](#b20) | low | TestGraphGenerator reports EdgeCount 7 for a 6-edge sample graph | **fixed** |
| [B33](#b33) | low | Subscribing to a disposed feed is reported as "Subscriber limit reached" | **fixed** |
| [B34](#b34) | low | OpenAPI info block keeps ASP.NET defaults: title is the assembly name, version is 1.0.0 | **fixed** |
| [B43](#b43) | low | DELETE /service/{key} mutates ServiceFactory.Services outside the write lock and never stops the service | **fixed** |
| [B44](#b44) | low | SampleGraphsPanel header comment wrong on tags, dataset origin and embeddings | **fixed** |
| [B45](#b45) | low | docker-compose.split.yml header claims env:up is untouched by the split overlay, which is now the env:up default | **fixed** |
| [B55](#b55) | low | Screenshot spec headers cite a docs/images/ output path that no longer exists | **fixed** |
| [B56](#b56) | low | Both launch profiles open the browser at /swagger, which is not mapped | **fixed** |

### B04: Runtime-registered Analytics plugins are unreachable: /analytics/{name} pre-checks built-ins only

`high` `analytics-plugin-reach`

POST /analytics/{algorithmName} and POST /analytics/{algorithmName}/partition/{partitionId} gate on AlgorithmExists(), which consults only PluginFactory's base-directory DLL scan (the built-ins). A plugin registered at runtime via POST /plugins/algorithm with contract Analytics lives in the per-namespace PluginRegistry as an in-memory, collectible-ALC type, so it can never be in that set and always answers 404 - even though Fallen8.TryRunAnalytics resolves the registry FIRST and would run it, and both GET /analytics/algorithms and GET /status advertise the name.

- **Proof:** fallen-8-core-apiApp/Controllers/AnalyticsController.cs:337-341 `private static Boolean AlgorithmExists(String algorithmName) => PluginFactory.TryGetAvailablePlugins<IGraphAnalyticsAlgorithm>(out var names) && names.Contains(algorithmName);` called at :176 (run) and :274 (partition members). fallen-8-core/Plugin/PluginFactory.cs:238-252 DiscoverCandidateTypes enumerates `Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll")` only, with the comment "Runtime plugins are no longer external assemblies ... they live as source in the per-namespace registry".
- **Impact:** Anyone who registers a custom analytics algorithm (the documented, compile-validated, capability-gated POST /plugins/algorithm with contract Analytics) gets a 201, sees the plugin listed in GET /analytics/algorithms and GET /status availableAnalyticsPlugins, and then gets a permanent 404 on every attempt to run it.
- **Fix:** One file: fallen-8-core-apiApp/Controllers/AnalyticsController.cs. Make AlgorithmExists an instance method that resolves against the SAME set the list endpoint already builds, so the two can never diverge (repo's "one home" rule): extract the union from GetAvailableAlgorithms into a private `IDictionary<String,String> AvailableAlgorithms()` (built-ins from PluginFactory.TryGetAvailablePluginsWithDescriptions<IGraphAnalyticsAlgorithm> unioned with `_fallen8.Plugins?.EntriesForContract(PluginContr [...]
- **Risk:** Small. AlgorithmExists is private, so no public-surface change and no fallen-8-core version bump; routes are unchanged. Because the XML remark on GET /analytics/algorithms should be corrected in the same PR, the OpenAPI snapshot MUST be regenerated (pwsh scripts/update-openapi-snapshot.ps1) - a desc [...]

### B10: VertexModel.GetAllNeighbors projects in-edges through TargetVertex, yielding InDegree copies of the vertex itself instead of its in-neighbou [...]

`high` `engine-model`

The in-edge branch of GetAllNeighbors adds edges[i].TargetVertex, but by the engine's own adjacency invariant an entry in a vertex's _inEdges is an edge whose TargetVertex IS that vertex. So the returned list is (out-neighbours) + (the vertex itself, repeated GetInDegree() times) instead of (out-neighbours) + (in-neighbours). The count is coincidentally right, the identities are wrong.

- **Proof:** fallen-8-core/Model/VertexModel.cs:344-381 - the out loop adds edges[i].TargetVertex (:362), and the in loop at :367-378 adds edges[i].TargetVertex again (:375), never SourceVertex; the comment at :350-353 says this is "preserved verbatim" from the prior representation. The invariant that makes this wrong: fallen-8-core/Fallen8.Storage.cs:320-323 (sourceVertex.AddOutEdge(edgePropertyId, outgoingEdge);
- **Impact:** Anyone using the engine as a library (or any generated/user-authored code that reaches VertexModel) who calls GetAllNeighbors on a vertex with incoming edges gets silently wrong data: self-references instead of predecessors.
- **Fix:** fallen-8-core/Model/VertexModel.cs:375 - change neighbors.Add(edges[i].TargetVertex) to neighbors.Add(edges[i].SourceVertex) inside the _inEdges loop, and rewrite the now-false comment at :350-353 to state the correct rule (out-edges project to TargetVertex, in-edges to SourceVertex - i.e. always the far endpoint). Nothing else changes: the presize at :348 (OutDegree + InDegree) stays exact, and duplicates for parallel edges/self-loops behave as before.
- **Risk:** Behaviour change on a public engine read, deliberately frozen by a comment during the adjacency-flattening storage swap - so it is a semantic change an out-of-tree embedder could theoretically have coded against (implausibly: they would be depending on self-repeats).

### B14: DELETE /index/{indexId}/{graphElementId} silently desyncs a bound vector index

`high` `index-content-binding`

A vector index bound to an embedding name is a derived projection whose membership is owned by the writer thread, and the typed add route refuses explicit adds with a 400 for exactly that reason. The generic remove-element route has no such guard: it calls idx.RemoveValue on the bound index, dropping the element's slot while the element keeps its embedding, and nothing reconciles it afterwards.

- **Proof:** fallen-8-core-apiApp/Controllers/GraphController.Index.cs:96-101 refuses an explicit add on a bound index ('is bound to embedding ... and maintains itself'); GraphController.Index.cs:277-294 (RemoveGraphElementFromIndex) looks the index up and calls idx.RemoveValue(graphElement) with no EmbeddingName check. fallen-8-core/Index/Vector/VectorIndex.cs:370-392 removes the slot regardless of EmbeddingName (RemoveSlotOf at :392).
- **Impact:** Any REST caller (curl/script/agent) doing generic index housekeeping - e.g. 'remove element X from every index' - silently removes a still-live, still-embedded element from kNN results. It hits the shipped semantic layer too: DocumentIngestionService creates its own bound vector index (fallen-8-core-apiApp/Ingestion/DocumentIngestionService.cs:706-721), so DELETE /index/<documents index>/<chunkVer [...]
- **Fix:** fallen-8-core-apiApp/Controllers/GraphController.Index.cs: in RemoveGraphElementFromIndex, right after TryGetIndex succeeds, add the mirror of the add guard - if (idx is IVectorIndex v && v.EmbeddingName != null) return ProblemResults.BadRequest(<same wording as :98-100, 'write/remove the element embedding instead'>). The action currently returns bool, so change its signature to ActionResult<bool> (ProblemResults.BadRequest returns ObjectResult, which converts implicitly;
- **Risk:** Contract change on a route documented as 200-only (a previously-succeeding call now 400s) plus an OpenAPI snapshot regen. No Studio change needed (bound indices already show no content forms) and no MCP change (index lifecycle is a recorded coverage deferral).

### B18: REST projection renders modificationDate as 1970 + delta instead of creationDate + delta

`high` `engine-model`

AGraphElementModel.ModificationDate is a DELTA in seconds since the element's creation stamp, not an absolute stamp. The REST DTO base converts it as if it were absolute, so every element's modificationDate is reported as 1970-01-01T00:00:00 plus the seconds since creation - i.e. exactly 1970-01-01T00:00:00 for any element that was never modified, regardless of its creationDate.

- **Proof:** fallen-8-core-apiApp/Controllers/Model/AGraphElement.cs:55-59 - ModificationDate = DateHelper.GetDateTimeFromUnixTimeStamp(modificationDate), with no creationDate term, while CreationDate is converted from the absolute stamp on the line above; the DTO field is documented "The date and time when the element was last modified" with example 2025-04-22T10:00:00Z (:121-130). The two and only two callers pass the raw delta field: fallen-8-core-apiApp/Controllers/Model/Vertex.cs:90 (vertex.ModificationDate) and Edge.cs:104 (edge.ModificationDate).
- **Impact:** Every REST read that projects a Vertex/Edge DTO (GET /vertex/{id}, GET /edge/{id}, GET /graphelement/{id}, GET /graph, and any other route returning these DTOs, plus the MCP tools that forward them) reports a bogus modificationDate.
- **Fix:** One line: fallen-8-core-apiApp/Controllers/Model/AGraphElement.cs:59 -> ModificationDate = DateHelper.GetDateTimeFromUnixTimeStamp(creationDate + modificationDate); (keeping the single conversion in the base, matching AGraphElementModel.GetModificationDate). Optionally tighten the ctor XML doc at :52 to say "the modification delta in seconds since creation" so the parameter's meaning is stated at the conversion site. No change needed in Vertex.cs/Edge.cs.
- **Risk:** No route, signature or XML-doc change on any controller action, so no OpenAPI snapshot regeneration and no engine public-surface bump (fallen-8-core is untouched). The wire VALUE changes for every element read - a client or e2e assertion that hard-codes 1970 for modificationDate would flip;

### B25: A leading Edge pattern silently ignores its own edgePropertyFilter

`high` `subgraph`

When patterns[0] is a fixed-length Edge pattern, level-0 seeding iterates every edge in the copied subgraph and decides membership with MatchesEdgePattern alone, which consults only Direction and pattern.Edge. pattern.EdgeProperty (the REST edgePropertyFilter) is never called, so the seeding step matches every edge type while every deeper level pre-filters with it.

- **Proof:** fallen-8-core/Algorithms/SubGraph/BreadthFirstSearchSubgraphAlgorithm.cs:641-676 - ProcessLevel0's `case EdgePattern ep` loops `subgraph.GetAllEdges()` and only calls MatchesEdgePattern for the three directions; no reference to ep.EdgeProperty anywhere in the method. :730-750 - MatchesEdgePattern checks `pattern.Direction.Equals(direction)` and `pattern.Edge`, nothing else.
- **Impact:** A caller who writes patterns `[{"type":"Edge","edgePropertyFilter":"return (p) => p == \"knows\";"}, {"type":"Vertex"}]` gets 201 and a subgraph seeded from EVERY edge type, not just `knows` - silently wrong membership, no warning, no error. On the CreateComplexGraph shape it keeps all 5 edges instead of the 2 `knows` edges.
- **Fix:** fallen-8-core/Algorithms/SubGraph/BreadthFirstSearchSubgraphAlgorithm.cs, ProcessLevel0, inside the `case EdgePattern ep` foreach at :643, add the same pre-filter the deeper levels use, before the MatchesEdgePattern chain: `if (ep.EdgeProperty != null && !ep.EdgeProperty(e.EdgePropertyId)) { continue; }`. Nothing else changes; MatchesEdgePattern stays as-is (it takes no property id, and every caller already pre-filters).
- **Risk:** Narrow behaviour change: a caller who today (accidentally) relies on the over-broad seeding gets a smaller subgraph. No contract, snapshot or public-surface change (private method, no signature change); the OpenAPI snapshot and the MCP bridge are untouched.

### B26: Recalculating a parent orphans its nested children on the discarded parent instance

`high` `subgraph`

TryRecalculateSubGraph swaps in a brand-new Fallen8 for the recalculated subgraph (id preserved via SetId) but never re-resolves any child's SourceFallen8 by SourceFallen8Id. Every nested subgraph keeps an object reference to the parent's discarded instance, so recalculating the child afterwards re-extracts from the parent's pre-recalculation snapshot - forever. Only RecalculateAllSubGraphs gets this right, and it has no REST route.

- **Proof:** fallen-8-core/SubGraph/SubGraphFactory.cs:596-605 - `Guid oldSubGraphId = outdatedSubGraphResult.SubGraph.Id; newSubGraph.SubGraph.SetId(oldSubGraphId); ... outdatedSubGraphResult.SubGraph = newSubGraph.SubGraph;` - the result object now holds a NEW engine under the OLD guid, and no other registered result is touched. :580 binds the algorithm to `outdatedSubGraphResult.SourceFallen8`, i.e. whatever instance the child was bound to at creation.
- **Impact:** Create A over REST, create B with ?fromSubGraph=A, mutate the graph, then press Recalculate on A and on B in Studio (or POST both recalculate routes). A refreshes; B returns 200 with a fresh-looking summary computed from A's PREVIOUS contents.
- **Fix:** fallen-8-core/SubGraph/SubGraphFactory.cs, in TryRecalculateSubGraph right after the swap at :605: rebind dependents to the live instance - `foreach (var dependent in _subGraphsById.Values) { if (!ReferenceEquals(dependent, outdatedSubGraphResult) && dependent.SourceFallen8Id.Equals(oldSubGraphId)) { dependent.SourceFallen8 = newSubGraph.SubGraph; } }`. That is exactly what :688-691 already does, moved to where the swap happens, so it also covers the single-subgraph route;
- **Risk:** Low blast radius: private dictionary walk inside an existing method, no signature or contract change, no snapshot regen. The dependency identity is unchanged (SourceFallen8Id already survives the swap because SetId preserves the guid), so persistence/recipe rehydration is unaffected.

### B39: PUT /load with a nonexistent path answers 204 and registers a phantom "newest" save game that aborts the next startup

`high` `persistence`

A load of a path that does not exist is a silent no-op success end-to-end: the engine returns false, the transaction still commits, the controller answers 204 and then unconditionally records the bogus path in the save-game registry with savedAt = now, which makes it the NEWEST entry for that namespace. On the next boot (any restart that did not write a newer entry, e.g. a crash/kill or SaveOnShutdown=false) DurabilityLifecycleService picks that entry, finds the file missing, and throws "…which does not exist; startup is aborted", so the host refuses to start until an operator deletes the entry. The action's own documented 400 "file not found" is unreachable.

- **Proof:** fallen-8-core/Persistency/PersistencyFactory.cs:78-87 returns false when !File.Exists(pathToSavePoint). fallen-8-core/Fallen8.Persistence.cs:723 Load_internal is void and only records a metric on !loaded (748-756); LoadCore's else branch (894-911) restores state and returns false. fallen-8-core/Transaction/LoadTransaction.cs:55-59 TryExecute calls Load_internal and returns true unconditionally, so TransactionState is never RolledBack. fallen-8-core-apiApp/Controllers/AdminController.cs:435-437 is the ONLY 400 (null body); 449-453 only maps RolledBack to 500;
- **Impact:** An operator who typos the path in PUT /load (or sends an empty saveGameLocation) gets a success response, no indication anything failed, and a poisoned registry: GET /savegames shows a phantom entry, PUT /savegames/{id}/load on it 500s, and the next unclean restart aborts startup entirely. Recovery requires knowing to DELETE /savegames/{id}.
- **Fix:** fallen-8-core-apiApp/Controllers/AdminController.cs, in Load(): after the null-body guard (line 437) add the pre-flight the sibling already has, if String.IsNullOrWhiteSpace(definition.SaveGameLocation) || !System.IO.File.Exists(definition.SaveGameLocation) return ProblemResults.BadRequest(...) naming the path.
- **Risk:** Small. Behaviour change: PUT /load with a bad/blank path now returns 400 instead of 204, that is the documented contract, so the OpenAPI snapshot does not change (no route/XML-doc edit).

### B01: Offline pre-seed script never pulls bge-m3, so the default-on embedding provider has no model

`medium` `compose-scripts`

scripts/ensure-models.sh exists so the model volume can be seeded on a networked host and the environment then started offline, but it pulls only phi4-mini, $F8_DELEGATE_REPO and (optionally) $F8_PHI4F8_REPO. The container-side entrypoint scripts/ollama-init.sh does pull bge-m3 whenever F8_EMBEDDINGS is not off, and docker-compose.yml enables the embedding provider by default and hard-declares bge-m3 as its model. An offline start after a pre-seed therefore comes up with Fallen8__Embedding__Enabled=true, /status advertising model bge-m3, and the sidecar having no such model, so every embed call fails.

- **Proof:** scripts/ensure-models.sh:4-11 ("start the environment OFFLINE later (seed once here where there is internet)"), :29 and :56-68 - the container body pulls phi4-mini, $F8_DELEGATE_REPO, $F8_PHI4F8_REPO and nothing else; the script never reads F8_EMBEDDINGS. scripts/ollama-init.sh:22 (F8_EMBEDDINGS default true) and :114-122 (ensure_base "bge-m3" unless off). docker-compose.yml:60-66 (Embedding__Enabled=${F8_EMBEDDINGS:-true}, ModelName=bge-m3, Ollama__Model=bge-m3) and :123-125 (the sidecar gets F8_EMBEDDINGS to keep it in sync).
- **Impact:** Anyone who follows the documented offline/pre-seed path (running.mdx:80-85, troubleshooting.md:39-45) and then starts without a network gets a Fallen-8 that reports embeddings enabled on /status while every text-in embed, semantic search and queryText traversal errors out at call time. The two scripts that exist for the same purpose disagree on the model set.
- **Fix:** scripts/ensure-models.sh only: (1) add F8_EMBEDDINGS="${F8_EMBEDDINGS:-true}" next to the other defaults at :23-25 and pass -e F8_EMBEDDINGS="$F8_EMBEDDINGS" in the docker run at :45-47; (2) inside the single-quoted container body add a case mirroring scripts/ollama-init.sh:115-122 - on 0|false|FALSE|no|off echo a skip line, otherwise `ollama pull bge-m3`;
- **Risk:** Shell + docs only; no C# touched, so no OpenAPI snapshot, MCP coverage or package-version impact. Behaviour change: the default pre-seed downloads ~1.2 GB more (opt out with F8_EMBEDDINGS=false, matching the sidecar).

### B05: PUT /vertex published sample cannot deserialize (properties as object map, creationDate as ISO string)

`medium` `openapi-contract`

The <remarks> sample on AddVertex sends `"properties": { "name": {...} }` (a JSON object map) and `"creationDate": "2025-04-22T00:00:00"`, but VertexSpecification.Properties is a List<PropertySpecification> (array) and CreationDate is a UInt32 Unix timestamp. Both fields fail System.Text.Json binding, so the sample that ships in the OpenAPI description 400s on copy-paste.

- **Proof:** fallen-8-core-apiApp/Controllers/GraphController.Vertex.cs:46-60 (remarks; :52 `"creationDate": "2025-04-22T00:00:00"`, :53 `"properties": {`) vs fallen-8-core-apiApp/Controllers/Model/VertexSpecification.cs:83 `public List<PropertySpecification> Properties` and :63-66 `[JsonPropertyName("creationDate")] public UInt32 CreationDate`. The published contract carries it: features/done/web-ui/openapi-v0.1.json description for PUT /vertex and PUT /ns/{ns}/vertex is exactly that sample;
- **Impact:** Anyone who copies the request sample from Scalar, the OpenAPI JSON, or a generated SDK's doc comment for the single most basic write in the product gets 400 with no hint that the sample itself is wrong.
- **Fix:** fallen-8-core-apiApp/Controllers/GraphController.Vertex.cs: replace the remarks sample body with the DTO's own correct form - `"creationDate": 1713862800` and `"properties": [ { "propertyId": "name", "propertyValue": "John Doe", "fullQualifiedTypeName": "System.String" } ]` (copy VertexSpecification.cs:36-53 verbatim so there is one shape in one place). Then regenerate the snapshot: pwsh scripts/update-openapi-snapshot.ps1.
- **Risk:** Description-only change; OpenAPI snapshot must be regenerated (two description strings change: /vertex and /ns/{ns}/vertex). No engine or wire behaviour changes. OpenApiDocumentTest's inventory comparison only pins path/method/tags, so it will not flag the diff either way.

### B06: Every documented `operator` value is a string that is neither the wire format nor a real enum member

`medium` `openapi-contract`

BinaryOperator is serialized as an integer (no JsonStringEnumConverter anywhere, and the snapshot schema is a bare `{"type":"integer"}`), and its members are Equals/Greater/GreaterOrEquals/Lower/LowerOrEquals/NotEquals. Every documented sample and <example> says `"operator": "Equal"` - wrong shape and a non-existent member name - so it 400s. Worse: because the schema emits no enum names, the document gives a client author no way at all to learn the codes.

- **Proof:** fallen-8-core/Expression/BinaryOperator.cs:33-45 (six members, first is `Equals`, no converter attribute; contrast fallen-8-core-apiApp/Controllers/Model/ResultTypeSpecification.cs:36 which does carry `[JsonConverter(typeof(JsonStringEnumConverter<ResultTypeSpecification>))]`). No global enum converter: Program.cs:448-455 AddJsonOptions only inserts AppJsonContext. Snapshot: components.schemas.BinaryOperator == {"type":"integer"} while ScanSpecification.operator carries examples ["Equal"].
- **Impact:** A REST or MCP-tool author reading the API reference cannot make a property/index scan work: the only example 400s and the schema exposes no member names, so the codes must be reverse-engineered from the Studio source.
- **Fix:** Docs-in-code only, keep the wire as integers (Studio and MCP already send 0..5): change `"operator": "Equal"` to `"operator": 0` in GraphController.Scan.cs:57 and :155, Model/ScanSpecification.cs:38-46 and :53 (`<example>0</example>`), Model/IndexScanSpecification.cs:42; and extend ScanSpecification.Operator's <summary> with the one-line mapping already in graph-model.mdx:226 (0 Equals, 1 Greater, 2 GreaterOrEquals, 3 Lower, 4 LowerOrEquals, 5 NotEquals) so the code mapping has one home reachabl [...]
- **Risk:** Descriptions/examples only; snapshot regeneration (four description strings + two schema example blocks). Nothing clients send changes. If someone instead chooses the converter route, that is an additive wire change that also alters components.schemas.BinaryOperator and would need a Studio/MCP decis [...]

### B07: A second <remarks> block is silently dropped from the OpenAPI description, losing the SECURITY/SEMANTIC notes on all three code endpoints

`medium` `openapi-contract`

.NET 10's native XML-doc reader maps only the FIRST <remarks> element to the operation description. Three actions carry two <remarks> blocks, and in every case the second one - the trust-boundary SECURITY paragraph and the SEMANTIC-traversal rules - never reaches the published document, although it is present in the compiled XML doc file.

- **Proof:** Two <remarks> per doc block (I enumerated all controller doc-comment blocks; exactly these three have >1): StoredQueriesController.cs:88-106 and :113-118; GraphController.Path.cs:44-86 and :95-118; SubGraphController.cs:98-114 and :123-146. The compiled XML has both (fallen-8-core-apiApp/bin/Debug/net10.0/fallen-8-core-apiApp.xml, RegisterStoredQuery member: the SECURITY <remarks> is right there after the <response> tags).
- **Impact:** The API reference for the three endpoints that compile and run C# in-process with full trust never says so, and the entire semantic-traversal contract (queryVector/queryText/minScore/costBySimilarity) is invisible to REST clients and to anyone generating an SDK or agent tool from the document.
- **Fix:** Merge each second <remarks> into the first (one <remarks> per action, sample last or the notes last - just one element) in fallen-8-core-apiApp/Controllers/StoredQueriesController.cs, Controllers/GraphController.Path.cs and Controllers/SubGraphController.cs. No text needs rewriting, only the surrounding `/// </remarks>` + `/// <remarks>` pair removed. Then regenerate the snapshot (three descriptions grow).
- **Risk:** Descriptions only; the snapshot diff is pure additions to three description strings. XML doc ordering relative to <response> tags does not matter to the reader. No behaviour change.

### B12: Path screen lets you build costBySimilarity + an inline vertexFilter, which the server rejects with 400

`medium` `studio-ui`

The UI treats the vertex-FILTER slot as owned by the semantic block only when minScore is on, but the server also installs an implied vertex filter when costBySimilarity is on alone. So with semantic enabled + costBySimilarity (DIJKSTRA) + no minScore, the Studio leaves filter.vertexFilter editable AND sends the fragment, and /path answers 400 "…own the same delegate slot; use one."

- **Proof:** fallen-8-web-ui/src/lib/semantic.ts:92-94 `semanticOwnsVertexFilter` = `draft.enabled && draft.minScoreEnabled` (costBySimilarity not considered); fallen-8-web-ui/src/screens/PathScreen.tsx:323-332 drives the vertexFilter DelegateSlot's `disabled` from exactly that predicate, so the slot stays editable; fallen-8-web-ui/src/lib/storedQueries.ts:72 `const semanticOwnsFilter = semantic?.minScore !== undefined;` and :77 sends `draft.vertexFilter` whenever that is false;
- **Impact:** A Studio user running a Dijkstra path with declarative cost-by-similarity plus their own vertex-filter fragment gets a 400 from a request the UI actively let them assemble (the slot is even offered as editable), with an error that reads like a server contradiction. The same pair is silently correct via minScore, so the failure looks arbitrary.
- **Fix:** fallen-8-web-ui/src/lib/semantic.ts:92-94, `return draft.enabled && (draft.minScoreEnabled || draft.costBySimilarity);` (the block owns the filter slot whenever the server would install one). fallen-8-web-ui/src/lib/storedQueries.ts:72, `const semanticOwnsFilter = semantic?.minScore !== undefined || semantic?.costBySimilarity === true;` so the fragment is omitted, not sent alongside.
- **Risk:** Small and UI-local. Behaviour change users could notice: a committed vertexFilter fragment is now dropped from the request (and the slot greys out) as soon as costBySimilarity is ticked, the same already-shipped convention as minScore, and the fragment stays in the draft, so untick restores it.

### B16: POST /document and /document/text declare statuses (413 for MaxPages/MaxChunksPerDocument, 502) that the async pipeline can never return

`medium` `documents-ingestion`

Both ingest routes declare response codes whose stated causes are enforced only on the worker thread, after the request already returned 202. The 413 description names MaxPages and MaxChunksPerDocument, which are thrown as IngestionFailedException inside ProcessJobAsync and converted into a failed Document vertex; the 502 ("embedding backend produced invalid output") is likewise only reachable in the worker's catch, so it is entirely unreachable for these two routes. The 400 description also names worker-only reasons (empty conversion, no chunks), and /document/text's 503 text names "embedding backend unavailable", also worker-only.

- **Proof:** Declared: fallen-8-core-apiApp/Controllers/DocumentController.cs:97-103 and :206-212, with [ProducesResponseType(Status502BadGateway)] at :118 and :226. Request-thread validation in DocumentIngestionService.IngestAsync covers only embed/docling/tags/replace/duplicate/ceiling/link/binding (fallen-8-core-apiApp/Ingestion/DocumentIngestionService.cs:164-245); MaxUploadBytes is the only real 413 (DocumentController.cs:135-140 for the file route, :250-256 for text).
- **Impact:** Anyone generating a client or writing an agent from the OpenAPI document implements 413/502 handling that never fires, and - worse - does NOT implement the change-feed/GET /document/{id} status check that is the only way to learn a document was rejected for page count, per-document chunk cap, or a bad embedding response.
- **Fix:** Doc-in-code only, no behaviour change. fallen-8-core-apiApp/Controllers/DocumentController.cs: (1) line 98 -> "Above Fallen8:Ingestion:MaxUploadBytes"; line 207 -> "The text exceeds Fallen8:Ingestion:MaxUploadBytes"; (2) delete the <response code="502"> lines 101 and 210 and the [ProducesResponseType(StatusCodes.Status502BadGateway)] attributes at 118 and 226 (leave /document/search's 502 at :375/:387 - it is real);
- **Risk:** OpenAPI snapshot regeneration with deliberate REMOVALS (the 502 responses) - the reviewer must accept them, which is the one case the repo's snapshot rule flags. MCP coverage is unaffected (McpRestCoverageTest keys on operations, not status codes). No engine surface change, no version bump.

### B17: Entity-type examples say PER/ORG/LOC, but the shipped spaCy models emit OntoNotes labels (PERSON/ORG/GPE)

`medium` `documents-ingestion`

The `type` filter on GET /document/entities is an exact, case-insensitive string compare against the raw spaCy label stored on the Entity vertex. Both shipped models (en_core_web_lg on CPU, en_core_web_trf on GPU) are OntoNotes-trained and emit PERSON/ORG/GPE/LOC/DATE, never PER. So the documented example value `PER` matches nothing, silently, and it reaches the OpenAPI snapshot, the MCP tool schema an agent reads, and the Studio placeholder.

- **Proof:** Labels are stored verbatim: fallen-8-nlp/app/enrich.py:82-85 (`label=ent.label_`) -> fallen-8-core-apiApp/Ingestion/DocumentIngestionService.cs:1110 ({ EntityTypeProperty, entity.Label }). The sidecar's own model documents the real label space: fallen-8-nlp/app/models.py:25-27 "The raw spaCy English label (PERSON/ORG/GPE/LOC/DATE/...)". Shipped models: fallen-8-nlp/app/enrich.py:21 default en_core_web_lg, fallen-8-nlp/Dockerfile:19 ARG F8_NLP_MODEL=en_core_web_lg, docker-compose.gpu.yml:37/40 en_core_web_trf - both OntoNotes.
- **Impact:** A user or agent that follows the example and asks for `type=PER` gets an empty list with HTTP 200 and no hint that the value is unknown, and can reasonably conclude the corpus mentions no people. Agents are the worst hit: the MCP schema string is the only label guidance they get.
- **Fix:** Replace the example label set with the real one in the four sites: DocumentController.cs:453 -> "(case-insensitive, e.g. PERSON/ORG/GPE)", DocumentEntityListREST.cs:68 -> "(the NLP label, e.g. PERSON/ORG/GPE)", fallen-8-mcp/Tools/DocumentsTool.cs:106 -> "e.g. PERSON/ORG/GPE (raw spaCy OntoNotes labels)", KnowledgeScreen.tsx:527 -> "Type filter (PERSON/ORG/GPE/...)". Then regenerate the OpenAPI snapshot (pwsh scripts/update-openapi-snapshot.ps1) for the three description strings.
- **Risk:** Small, but touches four layers: the OpenAPI snapshot must be regenerated (description-only diff, additions/edits, no removals), and KnowledgeScreen.tsx:527 is visible UI text - by the repo's own rule any UI change means the affected docs screenshots are recaptured. No contract or behaviour change;

### B19: Unknown or unconvertible property type on the create routes escapes as 500, not the documented 400

`medium` `index-content-binding`

ServiceHelper.CreateObject resolves the caller's fullQualifiedTypeName with the throwing AllowedLiteralTypes.Resolve and then calls Convert.ChangeType, both of which throw for user-supplied input (ArgumentException / FormatException / OverflowException). The four element-create actions call it through GenerateProperties with no guard, so a bad type name or an unconvertible value becomes an unhandled exception rendered as a 500 problem+json, although each action declares 400 for an invalid specification.

- **Proof:** fallen-8-core-apiApp/Helper/ServiceHelper.cs:80-83 (Convert.ChangeType + AllowedLiteralTypes.Resolve), reached via Transform (:137-142) from GenerateProperties (:102, :124). Resolve throws ArgumentException for a non-allow-listed name (fallen-8-core-apiApp/Helper/AllowedLiteralTypes.cs:106-116; the throw is pinned by fallen-8-unittest/DynamicCodeResourceLimitsTest.cs:96). Call sites with no try/catch: GraphController.Vertex.cs:86 and :133, GraphController.Edge.cs:118 and :171;
- **Impact:** A caller who mistypes a type name, omits it (the default System.Int32 then rejects any non-numeric value), or sends an out-of-range/unparsable literal gets a 500 'server error' instead of the documented 400. Generic client retry logic retries a permanently-invalid request, and the problem body says nothing about which property was wrong.
- **Fix:** fallen-8-core-apiApp/Helper/ServiceHelper.cs: add a non-throwing sibling next to CreateObject - TryCreateObject(PropertySpecification, out Object value, out String error) using AllowedLiteralTypes.TryResolve plus Convert.ChangeType(..., CultureInfo.InvariantCulture) inside the same catch set GraphController.TryConvertLiteral already uses (InvalidCastException/FormatException/OverflowException/ArgumentNullException) - and Try variants of the two GenerateProperties overloads that surface the offen [...]
- **Risk:** The three index actions change return type (ActionResult<bool> keeps the bool 200 schema; all three already declare 400 in features/done/web-ui/openapi-v0.1.json), so the snapshot must be regenerated and the direct-call tests updated: fallen-8-unittest/GraphControllerTest.cs:233, :254 and PropertyIn [...]

### B24: A filterless POST /path still runs Roslyn and loads a collectible assembly (no "nothing to compile" short-circuit)

`medium` `codegen-limits`

When a request supplies neither `filter` nor `cost`, `CreateSource` emits five factory methods with `return null;` bodies and `GeneratePathTraverser` compiles and loads that do-nothing assembly anyway. The sibling subgraph generator short-circuits the identical case. So the most common, deliberately code-free path request pays a cold Roslyn compile plus a collectible AssemblyLoadContext on every cache miss (the traverser cache has a 60 s sliding expiry).

- **Proof:** fallen-8-core-apiApp/Helper/CodeGenerationHelper.cs:247-269 - both else-branches add methods with a `null` fragment; :285-292 - `GenerateMethodSyntax` substitutes `"return null;"` for a null/blank fragment; :139 `compilation.Emit` and :152-153 `new AssemblyLoadContext(..., isCollectible: true)` + `LoadFromStream` run unconditionally. The subgraph path does short-circuit: :791-795 `if (slots.Count == 0) { return null; }`, reachable because `RegisterSlot` (:736-751) skips a blank fragment.
- **Impact:** Every code-free path query - the default REST call and the default MCP `f8_paths` call - pays a Roslyn Emit + assembly load on the first call in the process and again after any 60 s idle gap: tens to hundreds of ms of latency on an otherwise pure-read request, plus one collectible AssemblyLoadContext that lingers until GC.
- **Fix:** fallen-8-core-apiApp/Helper/CodeGenerationHelper.cs only. Add an internal `sealed class NoOpPathTraverser : IPathTraverser` (five methods returning null) with a `static readonly Instance`, and in `GeneratePathTraverser`, after the length checks (:96-110) and before `CreateSource`, short-circuit when every fragment is blank: `Filter` null-or-all-`IsNullOrWhiteSpace` AND `Cost` null-or-all-`IsNullOrWhiteSpace` -> `traverser = NoOpPathTraverser.Instance; return null;`.
- **Risk:** Low and contained. `IPathTraverser` is an engine-public interface but the new class lives in the apiApp, so no engine public-surface change and no package bump. Existing tests stay green: PathFilterArityTest.cs:80-107 uses `Filter = new PathFilterSpecification()`, whose properties default to `"retur [...]

### B28: A runtime-registered ISubGraphAlgorithm is registerable and discoverable but not invocable

`medium` `subgraph`

PUT /subgraph has no algorithm selector anywhere on the path from the request body to the factory, and the factory's registry lookup is only reachable with an explicit name that REST never supplies. Since a registered plugin's name may never equal a built-in's, the registry branch can never fire for a subgraph plugin - yet GET /status advertises it.

- **Proof:** No selector on the wire: fallen-8-core-apiApp/Controllers/Model/SubGraphSpecification.cs:56-140 declares exactly name, additionalInformation, vertexFilter, edgeFilter, patterns, storedQuery, semantic - I read the property list; the pinned snapshot agrees (features/done/web-ui/openapi-v0.1.json, SubGraphSpecification.properties = ['name','additionalInformation','vertexFilter','edgeFilter','patterns','storedQuery','semantic']).
- **Impact:** A user registers a SubGraph plugin on the Studio Plugins screen (or POST /plugins with contract SubGraph), sees it validated, listed by GET /plugins and advertised in GET /status availableSubGraphPlugins - and then has no way to run it: every PUT /subgraph, from REST, Studio and MCP alike, silently uses the built-in BFS.
- **Fix:** Thread the name that already exists in the engine end to end. 1) fallen-8-core-apiApp/Controllers/Model/SubGraphSpecification.cs: add `[JsonPropertyName("algorithm")] public String Algorithm` with an XML <summary>/<example> mirroring PathSpecification.PathAlgorithmName (default = the built-in BFS name when null/blank).
- **Risk:** Additive on the wire (a new optional property), so no client breaks - but it is a public REST-contract addition: OpenAPI snapshot regeneration is mandatory, McpContractTest/McpRestCoverageTest must stay green, and the engine's public SubGraphSpecification type gains a member (no engine public-surfac [...]

### B29: OpenAPI advertises maxPathWeight default 100 while the runtime default is unbounded

`medium` `openapi-contract`

PathSpecification.MaxPathWeight initialises to Double.MaxValue (unbounded) but carries [DefaultValue(100.0)], which is schema metadata only - System.Text.Json never applies it. The published schema therefore says `"default": 100`, so a generated client that materialises schema defaults sends a 100-weight ceiling the server would not have applied.

- **Proof:** fallen-8-core-apiApp/Controllers/Model/PathSpecification.cs:126 `[DefaultValue(100.0)]` with :131 `} = Double.MaxValue;`; the snapshot's PathSpecification.maxPathWeight has `"default": 100`. The engine treats it as a real prune bound: fallen-8-core/Algorithms/Path/ShortestPathDefinition.cs:59 `MaxPathWeight { get; set; } = Double.MaxValue` and WeightedDijkstraShortestPath.cs:157,164 pass it into the search; GraphController.Path.cs:232 copies the DTO value through. The sibling knobs are consistent (maxDepth [DefaultValue((ushort)7)] with `= 7` at :95-100;
- **Impact:** A DIJKSTRA caller on a generated client with real edge costs silently loses every path whose cumulative weight exceeds 100, and the document tells them that ceiling is the server's default so they have no reason to suspect it.
- **Fix:** fallen-8-core-apiApp/Controllers/Model/PathSpecification.cs: delete `[DefaultValue(100.0)]` at :126 (leave `<example>100.0</example>` - an example is honest, a default is not). The schema then advertises no default, matching "omit it and the bound is unbounded". Regenerate the snapshot.
- **Risk:** One attribute; snapshot loses `"default": 100` on PathSpecification.maxPathWeight. Anyone who regenerated a client from the old snapshot changes behaviour on regeneration - in the direction of matching the server. No engine change.

### B31: PATCH /ns/{name} commits and persists the rename before validating pluginRegistration, so a 400 can leave the namespace renamed

`medium` `namespaces`

In NamespacesController.Update the rename is executed (and durably written to the namespace catalog) before the "pluginRegistration" body value is parsed. A request that carries a valid new name plus an unrecognized pluginRegistration value therefore answers 400 "Invalid pluginRegistration" even though the rename already happened and survived to disk, so a caller who reasonably reads 400 as "request rejected, nothing changed" now finds the old name 404ing.

- **Proof:** fallen-8-core-apiApp/Controllers/NamespacesController.cs:169-176 runs `_namespaces.TryRename(name, specification.Name, out ns, ...)` first and sets `effectiveName = ns.Name`; only afterwards, at lines 178-184, does it call `TryParsePluginRegistration` and `return ProblemResults.Create(StatusCodes.Status400BadRequest, "Invalid pluginRegistration", ...)` with no compensating action.
- **Impact:** Anyone scripting PATCH /ns/{name} with both fields (Studio, MCP, curl, CI) who typos the override value, e.g. `{"name":"flights-eu","pluginRegistration":"on"}`, gets 400 and, believing the call was rejected, keeps addressing /ns/flights, which now 404s; the graph is intact but reachable only under flights-eu, and the change persists across restart because the catalog was rewritten.
- **Fix:** Single file, fallen-8-core-apiApp/Controllers/NamespacesController.cs, method Update: hoist the parse above the rename. Before the `if (!String.IsNullOrEmpty(specification.Name))` block at line 169, add `bool? pluginRegistration = null; if (specification.PluginRegistration != null && !TryParsePluginRegistration(specification.PluginRegistration, out pluginRegistration)) { return ProblemResults.Create(StatusCodes.Status400BadRequest, "Invalid pluginRegistration", "Expected \"enabled\", \"disabled\ [...]
- **Risk:** Very low blast radius. Routes, [ProducesResponseType] set, XML summary/remarks and response codes are unchanged, so the OpenAPI snapshot does not need regeneration and McpRestCoverageTest/McpContractTest are unaffected.

### B42: POST /service documents a pluginType that resolves against IService only - and no IService plugin ships at all

`medium` `openapi-contract`

PluginSpecification is shared by POST /index and POST /service; its example/default pluginType is "DictionaryIndex", which is correct for /index and impossible for /service, where TryAddService resolves the name as an IService. Deeper: the shipped product contains ZERO IService implementations, so POST /service returns 200 `false` for every possible body, and /status advertises the credential with an example naming plugins that do not exist.

- **Proof:** fallen-8-core-apiApp/Controllers/Model/PluginSpecification.cs:37-49 (class <example> with "pluginType": "DictionaryIndex") and :65-68 (`[DefaultValue("DictionaryIndex")] public String PluginType { get; set; } = "DictionaryIndex"`), reflected in the snapshot's PluginSpecification schema (default and example both "DictionaryIndex").
- **Impact:** A user following the only documented body for POST /service gets 200 `false` with no diagnostic, and there is no value that would work: the endpoint is a dead end whose docs suggest otherwise (the plugin-DLL upload that used to populate it was removed - AdminController.cs:726-730).
- **Fix:** Minimal and honest: give POST /service its own <remarks> in fallen-8-core-apiApp/Controllers/AdminController.cs above :701 stating that pluginType must name an IService plugin, that NO service plugins ship built-in (so the response is `false` unless one is present), and pointing at GET /status availableServicePlugins as the authoritative list; and fix Controllers/Model/StatusREST.cs:44 to show `"availableServicePlugins": []`.
- **Risk:** Description-only change plus a snapshot regeneration. Does not touch the service surface's behaviour, so it also does not resolve the underlying question of whether the surface should stay.

### B49: f8_paths advertises a closed BLS/DIJKSTRA enum, so agents cannot select a registered path plugin

`medium` `subgraph`

Two of the three parts hold. (1) f8_paths declares `algorithm` as a JSON-Schema enum limited to BLS and DIJKSTRA even though the handler forwards any string and REST resolves registered path plugins by name, so a schema-conforming agent cannot reach a registered Path plugin that Studio and REST can. (2) The PluginsTool doc comment asserts the opposite of the shipped behaviour. (3) The f8_subgraph half is NOT an MCP defect - REST has no algorithm knob either (that is B28); MCP cannot bridge what does not exist.

- **Proof:** fallen-8-mcp/Tools/PathsTool.cs:65 `.Str("algorithm", "Path algorithm.", choices: new[] { "BLS", "DIJKSTRA" })`; fallen-8-mcp/Tools/SchemaBuilder.cs:48-61 turns `choices` into `prop["enum"]`, so the constraint is advertised to the client. The handler is permissive: PathsTool.cs:113 `PathAlgorithmName = ToolArgs.GetString(arguments, "algorithm") ?? "BLS"` forwards the raw string.
- **Impact:** An agent that registers a Path plugin through f8_plugins register_algorithm (or finds one already registered) cannot run it: the tool schema forbids the name, and MCP clients that validate arguments against the advertised schema reject it - while the same plugin is selectable from Studio's path algorithm picker and from raw REST.
- **Fix:** fallen-8-mcp/Tools/PathsTool.cs:65 - drop the `choices:` argument and describe the value instead, e.g. `.Str("algorithm", "Path algorithm: 'BLS' (hop count, default), 'DIJKSTRA' (weighted), or the name of a registered Path plugin from f8_plugins/f8_overview.")`. Keep the handler as-is (already permissive) - the REST side already answers a clean error for an unknown name.
- **Risk:** Small and MCP-local. Widening an enum to a free string is additive for callers but removes a client-side guard: a typo'd algorithm name now reaches REST and comes back as an error result instead of being caught by schema validation - acceptable, and the same trade-off f8_analytics already makes.

### B51: env-info.js advertises OpenAPI and Scalar URLs that 404 on the compose container

`medium` `compose-scripts`

scripts/env-info.js, which prints after every env:up and env:status, tells the user the compose REST API serves /openapi/v0.1.json and /scalar/v0.1. The apiApp maps both only when the hosting environment is Development, and nothing in the images or the compose files sets ASPNETCORE_ENVIRONMENT, so the container runs Production and both paths 404.

- **Proof:** scripts/env-info.js:70 prints `F8 REST API: http://localhost:${f8Port} (OpenAPI: /openapi/v0.1.json, Scalar: /scalar/v0.1)`. fallen-8-core-apiApp/Program.cs:494-497 - MapOpenApi()/MapScalarApiReference() sit inside `if (app.Environment.IsDevelopment())`, with the else branch (:501-506) being the Production ProblemDetails handler; a repo-wide grep for MapOpenApi/MapScalar finds no other call site. Neither Dockerfile sets the environment: Dockerfile's ENV block sets only ASPNETCORE_URLS and the storage directory, and fallen-8-core-apiApp/Dockerfile sets none;
- **Impact:** Every first-run user is handed two URLs that immediately 404, right after being told the environment is up - the docs then have to carry a troubleshooting entry for a dead end the tooling itself created. The API reference is reachable (docs site / a bare dotnet run), so this costs trust and time, not capability.
- **Fix:** scripts/env-info.js:70 only - drop the misleading parenthetical and point at where the surface actually lives, e.g. `F8 REST API: http://localhost:${f8Port} (REST only; the OpenAPI doc + Scalar are Development-only - use a bare \`dotnet run --project fallen-8-core-apiApp\`)`. Do NOT fix it by setting ASPNETCORE_ENVIRONMENT=Development in compose: that also swaps the Production ProblemDetails handler (Program.cs:501-506) for the developer exception page, leaking stack traces on a published port.
- **Risk:** Console text in one root script; nothing consumes it programmatically (package.json:env:up/env:status just run it). No C# change, so no OpenAPI snapshot regeneration or MCP coverage impact. The troubleshooting.md entry at :170-176 stays valid and can keep the pointer.

### B52: /statistics leaks the R-Tree's -1 "count not supported" sentinel as a real key count

`medium` `openapi-contract`

IndexStatsREST.Keys is a non-nullable Int32 and StatisticsController writes CountOfKeys() raw, so a spatial index reports `keys: -1` on GET /statistics, while GET /status normalises the same sentinel to null. The two discovery surfaces disagree, and the Studio renders the -1 verbatim.

- **Proof:** fallen-8-core-apiApp/Controllers/StatisticsController.cs:163-168 (`Keys = pair.Value.CountOfKeys()`) with Controllers/Model/GraphStatisticsREST.cs:141-146 (`public Int32 Keys`); the sentinel source is fallen-8-core/Index/Spatial/Implementation/RTree/RTree.cs:1156-1159 (`public int CountOfKeys() { return -1; }`) for the plugin named "SpatialIndex" (RTree.cs:1846-1851), creatable through the normal index factory (e.g. fallen-8-unittest/CorrectnessFixesFollowupsTest.cs:270 `TryCreateIndex(out spatialIndex, "spatialIdx", "SpatialIndex", ...)`).
- **Impact:** An operator with a spatial index sees "-1" as the key count in the Studio's Graph shape panel and in /statistics JSON, and a monitoring script that sums or thresholds that field silently corrupts its own numbers - while the very same server answers null on /status.
- **Fix:** Promote AdminController's NonNegativeCount into one shared home (e.g. an internal static on Controllers/Model/IndexDescriptionREST.cs, which already owns the sentinel explanation, and have AdminController.cs:244-245 call it too - no second copy). Change Controllers/Model/GraphStatisticsREST.cs:141-151 Keys/Values to Int32? with the same one-line remark, and StatisticsController.cs:163-168 to normalise both counts through the shared helper.
- **Risk:** A response-contract change (two /statistics fields become nullable) - snapshot regeneration, Studio type + render update, and any external consumer must tolerate null (it previously received -1, which was already unusable).

### B53: Vite dev proxy allowlist is stale: no /ns (nor /statistics, /config, /chat, /bulk, /embedding, /analytics, /storedquery, /document), so the [...]

`medium` `studio-ui`

`API_PREFIXES` in vite.config.ts has not been updated since the change-feed feature, so every route family added afterwards is unproxied in dev, including `/ns`, which prefixes EVERY namespace-scoped call. The unproxied GET falls through to Vite's SPA fallback and returns index.html with status 200, so the client throws a SyntaxError from JSON.parse instead of an ApiError, and the `/ns` capability probe (which degrades only on ApiError 404) never flips namespaceSupport to false, so there is no fallback to bare paths either.

- **Proof:** fallen-8-web-ui/vite.config.ts:39-58 (18 prefixes; no `/ns`, `/statistics`, `/config`, `/chat`, `/bulk`, `/embedding`, `/analytics`, `/storedquery`, `/document`) and :126-128 (prefix-keyed proxy). Vite 7.3.6's matcher is prefix-based: node_modules/vite/dist/node/chunks/config.js:22085-22086 `context[0]==="^" ? new RegExp(context).test(url) : url.startsWith(context)`. The 200-HTML fallthrough: same file :22092-22131, `htmlFallbackMiddleware` accepts a request whose `accept` includes `*/*` (fetch's default) and rewrites it to `/index.html`.
- **Impact:** A contributor following the documented dev loop (`npm run dev` on :5173 against the apiApp on :5000) gets a Studio that connects (the bare `/status` probe is proxied) but errors on essentially every screen with a JSON parse error, namespace inventory, Browser, Path, Subgraph, Analytics, stored queries, bulk, embeddings, documents, statistics, config, chat.
- **Fix:** fallen-8-web-ui/vite.config.ts:39-58, add the missing root prefixes: `/ns`, `/statistics`, `/chat`, `/bulk`, `/embedding`, `/analytics`, `/storedquery`, `/document`. For `/config` do NOT add a bare prefix: fallen-8-web-ui/public/config.js is fetched at `/config.js` by index.html's classic script and a `url.startsWith("/config")` key would swallow it, use the regex form `"^/config$"` (Vite supports `^`-prefixed regex keys, config.js:22086).
- **Risk:** Dev-server-only; nothing shipped changes. The one trap is over-broad prefixes stealing static dev requests (the `/config.js` case above; note the pre-existing `/index` entry already shadows `/index.html`), so prefer `^…$` regex keys for short names. No OpenAPI/MCP/engine impact.

### B08: StoredQuerySecurityMatrixTest still named/commented around a removed "switch"

`low` `stale-tests-and-comments`

The class doc correctly states dynamic code execution is always on with no kill switch, but the region names, test method names and inline comments below it are left over from the removed `Fallen8:Security:EnableDynamicCodeExecution` gate, so the file describes a gate matrix that no longer exists and two of its four tests assert nothing distinct.

- **Proof:** fallen-8-unittest/StoredQuerySecurityMatrixTest.cs:46-51 ("Dynamic code execution is ALWAYS ON (there is no kill switch)") versus :90-94 ("registered while the switch was on (provisioning window) ... reproducible in a host whose switch is off"), :142 (`#region switch ON`), :145/:156 (`SwitchOn_*`), :213 ("must work with the switch OFF (the headline contract)"), :218, :222/:237 (`SwitchOff_*`), :224-225 ("no longer requires the switch"), :252 ("must stay possible while the switch is off"), :286 ("ungated by the SWITCH").
- **Impact:** No runtime impact. A contributor reading the suite believes a configuration switch exists and can waste time looking for it, or adds a "switch off" case to a matrix that has only one column; the two `SwitchOff_*` tests read as extra coverage while duplicating the `SwitchOn_*` assertions.
- **Fix:** fallen-8-unittest/StoredQuerySecurityMatrixTest.cs only, comments/names: rename `#region switch ON` (:142) to something like `#region compile-and-run surface (always on)`; rename `SwitchOn_Registration_Returns201` -> `Registration_Returns201`, `SwitchOn_InlineAndStoredAndFilterless_AllPass` -> `InlineAndStoredAndFilterless_AllPass`, `SwitchOff_FilterlessPath_Succeeds` -> `FilterlessPath_Succeeds`, `SwitchOff_ListGetDelete_AreNeverGated` -> `ListGetDelete_AreNeverGated`;
- **Risk:** Zero product-code risk. `SwitchOn_`/`SwitchOff_` appear nowhere outside this file (no CI --filter, no docs reference them; features/done/stored-query-library/README.md:128 names the CLASS only), so renaming changes only test ids in reports.

### B09: Subgraph compile path has no fragment/generated-source length cap, unlike /path

`low` `codegen-limits`

The dynamic-code-resource-limits R2 length guard is implemented only on the path generator. `TryGenerateSubGraphDefinition` -> `CompileDelegates` -> `CompileProvider` never checks a fragment against `MaxFilterFragmentLength` nor the assembled provider source against `MaxGeneratedSourceLength`, so every subgraph fragment surface (`PUT /subgraph`, a `SubGraph` stored query, a persisted-recipe recompile) reaches Roslyn with whatever the 1 MiB request-size limit allows.

- **Proof:** fallen-8-core-apiApp/Helper/CodeGenerationHelper.cs:96-110 is the only guard site (`CheckPathFragmentLengths` + `sourceCode.Length > MaxGeneratedSourceLength`), and CheckPathFragmentLengths (:179-203) reads only `PathSpecification.Filter`/`Cost`. The subgraph route is :544-614 (`TryGenerateSubGraphDefinition`, no length check anywhere; slots registered at :578-583, :663-664, :730-733 via `RegisterSlot` :736-751) -> :789-829 (`CompileDelegates`, which builds the source at :797 and goes straight to the cache/`CompileProvider`) -> :833-880 (`CompileProvider`, `compilation.Emit` at :853).
- **Impact:** No correct-results impact. An authenticated caller (rate-limited to 30 sensitive requests / 10 s, body <= 1 MiB per fallen-8-core-apiApp/Controllers/SubGraphController.cs:145-146) can hand Roslyn roughly twice the code on the subgraph surface that /path permits (~1 MiB of fragment text vs 5 x 100 000), and each distinct fragment set additionally pins its ~1 MiB generated source AS THE CACHE KEY fo [...]
- **Fix:** fallen-8-core-apiApp/Helper/CodeGenerationHelper.cs only. (1) In `TryGenerateSubGraphDefinition`, before `CompileDelegates` (after the slot list is built, e.g. right at :600), sweep every registered slot's `Code` through the existing `CheckFragment` helper and return its message on the first hit - cheapest is to check inside `RegisterSlot`'s caller or add `CheckSubGraphFragmentLengths(slots)` that iterates `slots` and calls `CheckFragment(slot.MethodName-ish name, slot.Code)`;
- **Risk:** Behaviour change on one edge: a persisted recipe or stored query that was accepted BEFORE the fix with a >100 k fragment would stop rehydrating (RecipeSubGraphCompiler.cs:80 returns false; the stored query recompile-on-load marks the entry non-invocable -> 409 on invoke).

### B13: Export 422 detail blames the type allow-list for an invalid-UTF-16 refusal

`low` `bulk-export`

JsonlGraphFormat.TryFormatValue returns a bare false for three distinct causes (null value, runtime type outside AllowedLiteralTypes, and an allow-listed String/Char carrying an unpaired surrogate), but BulkController's single 422 detail string names only the first two. An element whose System.String property holds a lone surrogate is refused with "whose value is null or of a type outside the exportable allow-list" - a statement that is false for that case, because System.String IS allow-listed (the surrogate check at JsonlGraphFormat.cs:198-205 runs only AFTER the allow-list check at :191 has passed).

- **Proof:** fallen-8-core-apiApp/Helper/JsonlGraphFormat.cs:175-178 (null -> false), :190-194 (allow-list miss -> false), :196-205 (Char.IsSurrogate / !IsWellFormedUtf16 -> false, both after the allow-list gate) - three causes, one indistinguishable bool. fallen-8-core-apiApp/Controllers/BulkController.cs:233-239 (FindNonExportableProperty only keeps the key) and :244-256, whose only detail is "Element {0} carries property '{1}' whose value is null or of a type outside the exportable allow-list;
- **Impact:** An operator exporting a graph that an embedded/library/plugin writer populated with invalid UTF-16 (unreachable through the REST write path) gets a 422 asserting the property's type is not allow-listed when it is System.String.
- **Fix:** 1) fallen-8-core-apiApp/Helper/JsonlGraphFormat.cs: add a 4-arg overload TryFormatValue(Object value, out String typeName, out String formatted, out String rejection) that sets rejection at each of the three false returns ("the value is null"; String.Format("its runtime type '{0}' is outside the exportable allow-list", type.FullName);
- **Risk:** Small but not zero: editing the 422 XML remark forces an OpenAPI snapshot regeneration, which rewrites the 422 description on BOTH /bulk/export and /ns/{ns}/bulk/export in features/done/web-ui/openapi-v0.1.json (verified those are the only two affected entries).

### B20: TestGraphGenerator reports EdgeCount 7 for a 6-edge sample graph

`low` `stale-tests-and-comments`

`GenerateSampleGraphAsync` creates exactly six edges but returns a hard-coded `SampleStats { VertexCount = 5, EdgeCount = 7 }`, and that 7 is what `PUT /unittest` writes to the operator log.

- **Proof:** fallen-8-core-apiApp/Controllers/Sample/TestGraphGenerator.cs:75-80 - six `edgesTx.AddEdge(...)` calls (alice->bob communicatesWith, alice->trent trusts, bob->trent trusts, eve->alice attacks, mallory->alice attacks, mallory->bob attacks) - versus :88 `var stats = new SampleStats() { VertexCount = 5, EdgeCount = 7 };`. Vertex count 5 is right (:46-50). The value is consumed only at fallen-8-core-apiApp/Controllers/SampleGraphController.cs:77 `_logger.LogInformation("It took {ElapsedMs}ms to create a Fallen-8 graph with {VertexCount} nodes and {EdgeCount} edges per node.", ...)`;
- **Impact:** An operator who calls `PUT /unittest` and reads the server log is told the canned graph has 7 edges; every read endpoint returns 6. Log-only, but it is a wrong number in an operator-facing message and a trap for anyone writing an assertion from the log.
- **Fix:** fallen-8-core-apiApp/Controllers/Sample/TestGraphGenerator.cs:88 - change `EdgeCount = 7` to `EdgeCount = 6`. Better (kills the class of bug without refactoring): derive both counts from the transactions, `VertexCount = verticesCreated.Count` and `EdgeCount = edgesTx.GetCreatedEdges().Count` (mirror whatever accessor `CreateEdgesTransaction` exposes; `GetCreatedVertices()` is already used at :56).
- **Risk:** Effectively none. No test asserts 7: JsonSourceGenParityTest.cs:172 and :465 build their own `SampleStats { VertexCount = 3, EdgeCount = 7 }` purely as a serialization fixture and are untouched.

### B33: Subscribing to a disposed feed is reported as "Subscriber limit reached"

`low` `change-feed`

TrySubscribe returns false for two unrelated reasons - the feed is disposed, or MaxSubscribers is reached - and the controller renders both as 503 "Subscriber limit reached" / "The maximum number of concurrent change feed subscribers (32) is reached." The engine's own `<returns>` doc is equally wrong: it names only the limit.

- **Proof:** fallen-8-core/ChangeFeed/ChangeFeedDispatcher.cs:341 `if (_disposed || _subscriptions.Count >= _options.MaxSubscribers)` -> `return false`, under a `<returns>` tag at :335 that reads "false when ChangeFeedOptions.MaxSubscribers is reached." fallen-8-core-apiApp/Controllers/ChangeFeedController.cs:147-153 maps that single false to one message, and the OpenAPI-visible remark at :102 names only "disabled ... or the concurrent subscriber limit".
- **Impact:** An operator or a Studio user who subscribes to /ns/{x}/changefeed exactly while that namespace is dropped, or while the server is shutting down, sees a 503 blaming Fallen8:ChangeFeed:MaxSubscribers. They go tune a limit that was never hit (SubscriberCount is 0 at that moment). Narrow race window, no data effect - purely a wrong diagnosis.
- **Fix:** Two files, additive only. (1) fallen-8-core/ChangeFeed/ChangeFeedDispatcher.cs: correct the `<returns>` at :335 to name both causes, and add a disposed accessor next to the existing SubscriberCount property (:112-121, same `lock (_gate)` shape): `public Boolean IsDisposed { get { lock (_gate) { return _disposed; } } }`.
- **Risk:** Touching the controller's XML docs forces `pwsh scripts/update-openapi-snapshot.ps1`; the 503 description appears twice in features/done/web-ui/openapi-v0.1.json (:744 and :5342) and both must move together or McpContractTest/the snapshot gate fails.

### B34: OpenAPI info block keeps ASP.NET defaults: title is the assembly name, version is 1.0.0

`low` `openapi-contract`

The document transformer sets only Info.Description, so Info.Title stays the framework default "<assembly> | <document name>" and Info.Version stays "1.0.0", contradicting the API's documented version 0.1.

- **Proof:** fallen-8-core-apiApp/Program.cs:89-115 AddOpenApi("v0.1") + AddDocumentTransformer sets `document.Info.Description` at :99 and nothing else (grep for `Info.` in Program.cs returns only that line); app.MapOpenApi() at :496 adds no options. The result is pinned: features/done/web-ui/openapi-v0.1.json info == {"title": "fallen-8-core-apiApp | v0.1", "description": "A Fallen-8 hosts isolated graph namespaces...", "version": "1.0.0"} while the versioning default is ApiVersion(0,1) (Program.cs:410-414).
- **Impact:** Cosmetic but public: Scalar's header and every generated SDK's package/namespace and version metadata read "fallen-8-core-apiApp 1.0.0" instead of the product name and 0.1. No code in the repo consumes info.title/version, so nothing functionally breaks.
- **Fix:** fallen-8-core-apiApp/Program.cs, inside the existing AddDocumentTransformer next to the Description assignment: `document.Info.Title = "Fallen-8 REST API";` and `document.Info.Version = "0.1";`. Regenerate the snapshot.
- **Risk:** Two lines plus a 2-line snapshot diff. Downstream generated clients renamed on next regeneration (namespace/package identifier changes) - that is the point, but worth calling out to anyone who has generated an SDK.

### B43: DELETE /service/{key} mutates ServiceFactory.Services outside the write lock and never stops the service

`low` `persistence`

The endpoint calls Dictionary.Remove directly on the public Services dictionary, bypassing the WriteResource()/FinishWriteResource() guard every other mutation on that dictionary takes, and it never calls TryStop() on the removed service. So a delete concurrent with TryAddService/StartAllServices/ShutdownAllServices, or with the writer thread's checkpoint enumeration, is an unsynchronized Dictionary mutation (torn internal state or a "collection was modified" throw inside a save), and the deleted service keeps running with no handle left to stop it.

- **Proof:** fallen-8-core-apiApp/Controllers/AdminController.cs:723: `return _fallen8.ServiceFactory.Services.Remove(key);`, no lock, no TryStop. Contrast fallen-8-core/Service/ServiceFactory.cs:52 (the dictionary is a plain public readonly Dictionary), :107-125 TryAddService (WriteResource/Services.Add/FinishWriteResource), :152-172 ShutdownAllServices and :178-196 StartAllServices (both enumerate under WriteResource), :216-240 OpenService (same).
- **Impact:** Only reachable where an IService plugin actually exists, none ships (repo-wide grep finds only a test double) and dynamic registration cannot create one, so this bites embedders/operators who drop a custom service assembly into the base directory (PluginFactory scans it: fallen-8-core/Plugin/PluginFactory.cs:240-256).
- **Fix:** fallen-8-core/Service/ServiceFactory.cs: add `public bool TryRemoveService(String serviceName)` that takes WriteResource() (throw CollisionException on failure, like the siblings), looks the service up, calls TryStop() on it, removes it from Services, and returns whether it was present, all inside try/finally FinishWriteResource(). fallen-8-core-apiApp/Controllers/AdminController.cs:723: call `_fallen8.ServiceFactory.TryRemoveService(key)` instead of Services.Remove.
- **Risk:** Additive public method on the engine (fallen-8-core), so a package version note per the repo rule, but not a breaking surface change. Behaviour change users could notice: DELETE /service now STOPS the service before dropping it (previously it kept running), that is the intended semantics but it is a [...]

### B44: SampleGraphsPanel header comment wrong on tags, dataset origin and embeddings

`low` `stale-tests-and-comments`

The file-header doc comment of the Studio samples panel makes three statements that the code below it contradicts: the tag list omits `knowledge`, datasets are described as fetched from a public GitHub raw URL when the default is same-origin `/samples`, and it claims no embedding work happens because embeddings are baked in, while a sample carrying `documents` runs a live bind-and-ingest path.

- **Proof:** fallen-8-web-ui/src/components/SampleGraphsPanel.tsx:51-59 is the comment ("filters the gallery by capability (canvas / path / analytics / semantic / spatial). Datasets are fetched from a public GitHub raw URL and ingested via /bulk/import - embeddings are baked in, so no embedding work happens here."). Contradicted by: (1) `TAG_ORDER` at :63-70, which lists canvas, path, analytics, semantic, spatial AND "knowledge";
- **Impact:** No behavioural impact. It misleads the next maintainer of the samples gallery on all three points at once - most sharply on embeddings, since someone could "simplify" the load path believing no provider is involved, and on the dataset origin, since a fix aimed at GitHub raw would be aimed at the wrong place.
- **Fix:** fallen-8-web-ui/src/components/SampleGraphsPanel.tsx:51-59, comment only: add `knowledge` to the capability list (or say "see TAG_ORDER below" so the list has one home); replace the GitHub-raw sentence with "Datasets come from `samplesBaseUrl()` (same-origin /samples by default, overridable to a remote mirror)";
- **Risk:** None: a JSDoc block comment with no runtime effect. No screenshot, docs page or e2e spec depends on it.

### B45: docker-compose.split.yml header claims env:up is untouched by the split overlay, which is now the env:up default

`low` `compose-scripts`

The overlay's header says raw `docker compose up` stays the all-in-one "and `npm run env:up` is untouched". Since dbefa31 env-up.js layers this overlay unconditionally, so env:up IS the split topology - a maintainer reading the file concludes the opposite of the actual default. The same header also points at `npm run env:split:up`, a script that no longer exists.

- **Proof:** docker-compose.split.yml:8-9 ("Raw `docker compose up` (no -f overlay) stays the all-in-one, and `npm run env:up` is untouched") and :4 ("docker compose -f docker-compose.yml -f docker-compose.split.yml up (npm run env:split:up)"). scripts/env-up.js:72-76 - comment "Split topology is the default dev environment (feature standalone-ui)" then files.push('-f', 'docker-compose.split.yml') with no condition, and :103-107 prints the UI on F8_UI_PORT 8081. package.json defines only env:up/env:down/env:logs/env:status - no env:split:up - and env:down/logs/status all pass -f docker-compose.split.yml.
- **Impact:** Contributors only. Nothing at runtime; the risk is a maintainer trusting the header and, say, editing the overlay believing env:up does not use it.
- **Fix:** docker-compose.split.yml header only: at :8-9 replace the "env:up is untouched" clause with the truth - this overlay is applied unconditionally by scripts/env-up.js, so `npm run env:up` runs the split topology; the all-in-one is what a bare `docker compose up` (no -f overlay) gives. At :4 drop the stale `(npm run env:split:up)` reference and name the real entry point (`npm run env:up`, or the explicit -f invocation for a manual run). Comment text only, no keys touched.
- **Risk:** None - YAML comments only, no service definition changes, so no image rebuild semantics change. Check docs/src/content/docs/standalone-ui + running.mdx do not repeat the same stale claim while in there (running.mdx:148-168 is already correct).

### B55: Screenshot spec headers cite a docs/images/ output path that no longer exists

`low` `stale-tests-and-comments`

Seven Playwright capture specs document their output as `docs/images/<name>.png` in the header comment while the code writes `../docs/src/assets/images/<name>.png`; `docs/images/` does not exist since the docs site move.

- **Proof:** Stale headers (grep for `docs/images` over fallen-8-web-ui/e2e): screenshot-benchmark.spec.ts:40, screenshot-canvas-style.spec.ts:36, screenshot-first-run.spec.ts:35, screenshot-observability.spec.ts:35, screenshot-savegames.spec.ts:38, screenshot-cyber-sample.spec.ts:35-37 (three files), screenshot-stored-queries.spec.ts:32-34 (three files) - the claim named five, it is seven.
- **Impact:** No behavioural impact - the captures land in the right place. Someone recapturing a screenshot from the header comment looks for the file in a directory that does not exist, or hand-creates `docs/images/` and produces an asset the Starlight build never sees.
- **Fix:** Comment-only edits in fallen-8-web-ui/e2e: change `docs/images/` to `docs/src/assets/images/` at screenshot-benchmark.spec.ts:40, screenshot-canvas-style.spec.ts:36, screenshot-first-run.spec.ts:35, screenshot-observability.spec.ts:35, screenshot-savegames.spec.ts:38, screenshot-cyber-sample.spec.ts:35-37, screenshot-stored-queries.spec.ts:32-34.
- **Risk:** None: header comments in capture-only specs; no code path, no snapshot, no docs build input changes.

### B56: Both launch profiles open the browser at /swagger, which is not mapped

`low` `stale-tests-and-comments`

`launchSettings.json` still points the auto-launched browser at `/swagger`; the app maps only the OpenAPI document and the Scalar reference (`/scalar/v0.1`), so the developer lands on a problem+json 404 or, when a built SPA is in wwwroot, the Studio shell instead of the API reference.

- **Proof:** fallen-8-core-apiApp/Properties/launchSettings.json:14-15 (`"launchBrowser": true, "launchUrl": "swagger"`) and :25-26 (`"launchUrl": "{Scheme}://{ServiceHost}:{ServicePort}/swagger"`). fallen-8-core-apiApp/Program.cs:493-497 maps only `app.MapOpenApi()` and `app.MapScalarApiReference()` (Development only); a case-insensitive grep for swagger over fallen-8-core-apiApp returns only those two launchSettings lines - no Swashbuckle package, no /swagger route.
- **Impact:** Repo developers only: F5 in Visual Studio / `dotnet run` with the default profile pops a browser on a dead URL (404 problem+json, or confusingly the Studio UI), so the API reference looks missing. No effect on any deployed instance - launchSettings.json is a dev-only file and is not published.
- **Fix:** fallen-8-core-apiApp/Properties/launchSettings.json only: line 15 `"launchUrl": "swagger"` -> `"launchUrl": "scalar/v0.1"`; line 26 -> `"launchUrl": "{Scheme}://{ServiceHost}:{ServicePort}/scalar/v0.1"`. (The WSL profile sets no launchUrl and needs no change.)
- **Risk:** None: dev-only file, not compiled, not published, no test reads it. Worth noting the Scalar UI is Development-only, and with `Fallen8:Security:ApiKey` set it answers 401 without the header (docs/src/content/docs/rest-api.mdx:26) - so the launched page is still useful only in the default keyless dev [...]

## Needs a maintainer decision

Real defects whose fix is a product or architecture choice, not a mechanical correction.

**All seven have since been decided and fixed.** The rulings, for the record:

| ID | Ruling |
| --- | --- |
| B54 | Enforce isolation: the e2e suite always launches its own volatile apiApp on `:5099` and never reuses an existing listener |
| B22 | Cooperative budget only: `timeBudgetSeconds` plus the request abort, answering `408`. No abandon-thread backstop, and the honest limit is documented |
| B35 | Always declare the `ApiKey` scheme and a document-level requirement, with `security: []` on the six anonymous operations |
| B27 | Enforce on recalculation and keep the old contents: a breach answers `409` and the subgraph goes stale rather than over budget |
| B21 | Delete the dead option; the 1 MiB per-endpoint cap stays fixed and is documented as such |
| B38 | Add `Fallen8:Security:BenchmarkMaxIterations` (default `10000`), following the Analytics precedent |
| B41 | Make `Manufacturer` virtual so a subclass can advertise its own vendor |

### B54: Test Explorer e2e run erases the default namespace of whatever listens on :5000 (no volatile guard)

`high` `studio-ui`

Both functional e2e specs erase the `default` namespace. The workspace pins `playwright.env.F8_UI_URL=http://localhost:5000`, which makes playwright's `webServer` undefined, so a Test Explorer run targets whatever already listens on :5000 and never gets `Fallen8__Durability__Volatile=true`. The CLI path has the same hole via `reuseExistingServer: true`. The hazard and its manual workaround are already documented, so the open question is whether to keep it a documented convention or enforce a guard.

- **Proof:** .vscode/fallen-8-core.code-workspace:46-48 `"playwright.env": { "F8_UI_URL": "http://localhost:5000" }`; fallen-8-web-ui/playwright.config.ts:53-64 `webServer: process.env.F8_UI_URL ? undefined : { …, reuseExistingServer: true, env: { Fallen8__Durability__Volatile: "true", Fallen8__Security__ApiKey: "e2e-key" } }`; fallen-8-web-ui/e2e/first-run.spec.ts:52-59 `eraseDefault()` types "default" into the tabula-rasa confirm; fallen-8-web-ui/e2e/studio.spec.ts:287-300 does the same in scenario 8;
- **Impact:** A contributor who starts a backend launch config on :5000 with real local data and then runs (or debugs) an e2e scenario from the Playwright Test Explorer loses that graph: tabula rasa wipes the default namespace and SaveOnShutdown persists the empty state. Same outcome from `npm run e2e` when an apiApp already occupies :5000.
- **Candidate fix:** Pick one; all are outside fallen-8-core/. (a) Status quo: keep it a documented convention (debugging.md:96-110 already warns), zero code churn, keeps relying on the contributor reading the doc first. (b) Isolate the port and stop reusing: drop or repoint .vscode/fallen-8-core.code-workspace:46-48 (e.g.

### B21: MaxSensitiveRequestBodyBytes is dead config whose XML doc promises enforcement

`medium` `stale-tests-and-comments`

`Fallen8SecurityOptions.MaxSensitiveRequestBodyBytes` is declared and bound but never read by any product code, while its XML doc states that a fragment or plugin DLL over it "is rejected with 413 before it is buffered". The enforced limit is the compile-time literal in `[RequestSizeLimit(1_048_576)]` on each sensitive action, so raising or lowering the option changes nothing and logs nothing.

- **Proof:** fallen-8-core-apiApp/Configuration/Fallen8SecurityOptions.cs:97-101 is the ONLY product-code occurrence of the name (repo-wide grep otherwise hits only cleanup-report.md:159 D9 "read nowhere ... verify (wire or delete)", docs/src/content/docs/security.mdx:106, and features/done/bulk-import-export/spec.md).
- **Impact:** An operator who reads the options type (the de-facto config reference) and sets `Fallen8:Security:MaxSensitiveRequestBodyBytes` to allow a larger plugin DLL or a bigger stored-query body still gets a hard 413 at 1 MiB, silently. Conversely someone who lowers it believes they have tightened the RCE surface and has not. A configuration knob that lies about a security limit is worse than no knob.
- **Candidate fix:** Pick one; the choice is a product call, not a mechanical edit. Option A - DELETE (smallest, matches the repo's no-dead-code stance): remove Fallen8SecurityOptions.cs:97-101 and update docs/src/content/docs/security.mdx:106 to state the 1 MiB per-endpoint limit is fixed, dropping the "looks like the knob" sentence; close cleanup-report.md D9.

### B22: No execution budget on compiled path fragments (deliberate R1 deferral; a correct fix is an architecture choice)

`medium` `codegen-limits`

The claim's facts hold: nothing bounds the execution of a compiled filter/cost delegate. `CalculateShortestPath` takes no token and runs the traversal synchronously on the request thread; neither path algorithm has a deadline, cancellation token or step budget. A fragment that never returns holds that thread for the life of the process. But this is a recorded, reasoned deferral, and the only fix that actually bounds a hostile delegate is out-of-process/WASM isolation - so choosing what to do is a product/architecture decision, not a mechanical correction.

- **Proof:** fallen-8-core-apiApp/Controllers/GraphController.Path.cs:128 (no CancellationToken parameter; `HttpContext.RequestAborted` is used only for the embedding call at :144) and :248 (`_fallen8.TryCalculateShortestPath` called synchronously). A search for `CancellationToken|Deadline|StepBudget` under fallen-8-core/Algorithms returns hits ONLY in Analytics (fallen-8-core/Algorithms/Analytics/AGraphAnalyticsAlgorithm.cs:96, :181-197 `BudgetGuard`;
- **Impact:** Against a malicious caller this adds nothing they do not already have: the documented trust model says a fragment author is trusted as the process (they could call Environment.Exit). The real, non-hypothetical victims are (a) an operator whose own fragment or an LLM-generated fragment loops or blows up algorithmically - the request never returns, the thread is gone until restart, and there is no 4 [...]
- **Decision:** Question: do we spend engine surface + hot-loop churn on a budget that provably cannot stop the thing the claim is about (a hostile delegate that never returns), in order to contain accidental runaway traversals and give callers a 408? Options: (A) Accept and keep documenting - zero risk, zero cost; an accidental infinite-loop fragment still needs a process restart, and R1 stays open pending isolation. (B) Cooperative budget only (mirror Analytics' BudgetGuard; deadline + step budget + linked RequestAborted, 408) - bounds algorithmic blow-up and cancels between delegate calls, gives a real status code and lets the client give up;
- **Candidate fix:** Decide first (see decision_needed). If the decision is (B) cooperative budget: add `CancellationToken` + `int StepBudget` to fallen-8-core/Algorithms/Path/ShortestPathDefinition.cs (no `IShortestPathAlgorithm` signature change), reuse the Analytics `BudgetGuard` idiom at the BLS frontier loop (BidirectionalLevelSynchronousSSSP.cs:172-227, and inside `GetGlobalFrontier`) and the Dijkstra dequeue + [...]

### B27: POST /subgraph/{name}/recalculate enforces no quota, so a subgraph can grow past its ceilings

`medium` `subgraph`

TryRecalculateSubGraph swaps the fresh extraction in unconditionally and never consults the quota, so a subgraph whose source grew can exceed MaxElementsPerSubGraph and push the aggregate past MaxTotalElements without the 409 the create path returns. The claim is accurate; what is NOT decided is whether recalculation should be admission-controlled at all, and with what failure semantics.

- **Proof:** fallen-8-core/SubGraph/SubGraphFactory.cs:547-615 (TryRecalculateSubGraph) contains no quota reference; a grep of the whole file finds `_quota` only at :73, :103-106 and :404-444, i.e. the field, the property and the three create-path checks (count :404, per-subgraph :428, aggregate :438). The fresh result is installed at :605 with no size check between :586 (extraction) and :605 (swap).
- **Impact:** An operator who tightened the quota to bound memory (the documented reason it exists) can still be pushed past both element ceilings by repeated recalculation after an ingest - the guard silently does not apply on that route. Nobody gets wrong query results;
- **Decision:** Should recalculation be quota-checked, and if so what happens on a breach? (a) ENFORCE, keep-old: check `newElementCount > MaxElementsPerSubGraph` and `CurrentTotalElements() - oldElementCount + newElementCount > MaxTotalElements` before the swap at :605, discard the fresh result and return false - the subgraph keeps its old (in-quota) contents and stays permanently stale until the quota is raised or it is deleted; needs a distinct failure reason so the 409 message stops lying (a reason-reporting overload like the create path's TransactionFailureReason, plus a new <response> remark -> OpenAPI snapshot regen).
- **Candidate fix:** No mechanical fix - pick an option above. Under (a) the change is confined to fallen-8-core/SubGraph/SubGraphFactory.cs (a pre-swap check in TryRecalculateSubGraph plus a reason out-param) and fallen-8-core-apiApp/Controllers/SubGraphController.cs (split the 409 message by reason, update the <response code="409"> remark), then `pwsh scripts/update-openapi-snapshot.ps1`.

### B35: The document declares no security scheme, so the API-key requirement is invisible to consumers

`medium` `openapi-contract`

The API-key credential is enforced but never described: there is no OpenApiSecurityScheme/securitySchemes anywhere, so Scalar shows no auth field and generated clients get no auth wiring. It is real, but the correct document text is a choice, because enforcement is conditional (a key is required only when Fallen8:Security:ApiKey is configured) and three operations are permanently anonymous.

- **Proof:** Enforcement: fallen-8-core-apiApp/Security/ApiKeyAuthenticationHandler.cs:73-90 (X-Api-Key header, Bearer fallback) and Program.cs:302-306 (`if (keyConfigured) o.FallbackPolicy = ...RequireAuthenticatedUser()`); with no key configured the handler returns NoResult and the service is fully open (handler doc comment :42-47). Description: Program.cs:89-115 sets only Info.Description; the snapshot's components object has exactly one member, "schemas".
- **Impact:** An SDK generated from the published document has no way to send a credential, and a Scalar user hitting a key-secured instance gets 401s with no auth field to fill in - they must find the header name in prose.
- **Decision:** What should the published document claim about auth, given enforcement is config-dependent? (a) ALWAYS declare the scheme AND a document-level security requirement, with `security: []` on the three [AllowAnonymous] operations - consumers always get auth wiring and the pinned snapshot shows it; cost: the document says auth is required even on an open dev instance, and the anonymous overrides must be maintained. (b) Declare scheme + requirement only when a key is configured - matches enforcement exactly; cost: the pinned snapshot (regenerated keyless) still hides auth entirely, so the published reference is unchanged and the defect persists for readers.
- **Candidate fix:** Add the scheme in the existing AddDocumentTransformer in fallen-8-core-apiApp/Program.cs (components.securitySchemes["ApiKey"] = ApiKey in header, name from Fallen8SecurityOptions.ApiKeyHeader with the "X-Api-Key" fallback). The decision is what the document should CLAIM.

### B38: GET /benchmark accepts any positive iterations and cannot be aborted once running

`medium` `benchmark`

The code facts in the claim are accurate: `iterations` is only rejected when non-numeric or <= 0, the timed loop is a synchronous `for` over `myIterations` with no CancellationToken anywhere, and each iteration saturates every core via Parallel.ForEach, so `GET /benchmark?iterations=2000000000` pins the host until it finishes and closing the client changes nothing. What is wrong with the CLAIM is its framing: this is not an auth hole and not a hidden bug.

- **Proof:** fallen-8-core-apiApp/Controllers/BenchmarkController.cs:148-166, `Int32.TryParse(iterations, out iterationCount)` then straight into `TryBench`, no upper bound, no HttpContext.RequestAborted, and the action is a synchronous `ActionResult<BenchmarkResultREST>` (no CancellationToken parameter). fallen-8-core-apiApp/Controllers/Benchmark/ScaleFreeNetwork.cs:230 `TryBench(out ..., int myIterations = 1000)`, signature takes no token; :245-249 rejects only `myIterations <= 0`;
- **Impact:** An operator (or the Studio itself) mistyping the iteration count wedges the instance: fallen-8-web-ui/src/screens/BenchmarkScreen.tsx:209-216 is a free-text `inputMode="numeric"` field with no min/max, and fallen-8-web-ui/src/api/endpoints.ts:224-225 `runBenchmark` passes no AbortSignal even though the client supports one (api/client.ts:180), so navigating away or closing the tab does not stop the [...]
- **Candidate fix:** The decision, then the code. Option A (accept, docs-only): nothing changes, benchmark.md already states the limitation. Cheapest; leaves the footgun and stays inconsistent with the repo's own precedent for long-running compute (fallen-8-core/Algorithms/Analytics/GraphAnalyticsDefinition.cs:41 `MaxIterationsCeiling = 10_000`, appsettings.json:43-44 Default/MaxTimeBudgetSeconds, AnalyticsController. [...]

### B41: AGraphAnalyticsAlgorithm hard-codes a non-virtual Manufacturer, so a third-party subclass is listed under the repo owner's name

`low` `engine-model`

The analytics base class - which its own doc offers to third parties as a convenience and which the plugin guide uses as THE worked example - fixes Manufacturer to the literal "Henning Rauch" as a non-virtual member, so a third-party plugin that subclasses it is advertised by GET /analytics/algorithms under the wrong vendor and cannot opt out except by abandoning the base and implementing the full IPlugin surface. This is real friction, but it is a deliberate, already-documented shape, and every way of fixing it is a product/surface choice rather than a mechanical correction.

- **Proof:** fallen-8-core/Algorithms/Analytics/AGraphAnalyticsAlgorithm.cs:65 (public String Manufacturer => "Henning Rauch";) and :58 (PluginCategory, also non-virtual) on a public abstract class (:46) whose own doc says "Third-party plugins are free to implement IGraphAnalyticsAlgorithm directly - this base is a convenience, not part of the contract." (:39-40).
- **Impact:** Only an out-of-tree analytics plugin author is affected, and only cosmetically: their algorithm shows another person's name as vendor in the /analytics/algorithms listing (and in any Studio picker that renders that description). No wrong results, no data risk, and a documented escape hatch exists. Zero impact on the five built-ins, which genuinely are authored by that vendor.
- **Candidate fix:** Pick one, all in fallen-8-core/Algorithms/Analytics/AGraphAnalyticsAlgorithm.cs:65 (and mirrored in docs/src/content/docs/plugins.md:118-122): (a) leave as is - the constraint and workaround are already documented; costs nothing, keeps the wart. (b) make it `public virtual String Manufacturer => "Henning Rauch";` - one word, built-ins unchanged, subclassers override;

## Found while FIXING, and fixed in the same batch

Two defects that only surfaced because a fix exposed them. Both are closed.

### B57: an action's `[Produces("application/json")]` silently downgraded every error body

`high`

`ProblemResults` builds every explicit error with `ContentTypes = { "application/problem+json" }`, but
`ProducesAttribute` is a result filter that REPLACES a result's content types wholesale, so any action
declaring `[Produces("application/json")]` (75 of them) served its `400`/`404`/`409` as
`application/json`. The site's own promise that "every error response is RFC 7807
`application/problem+json`" was therefore false for most of the surface, and a client could not switch
on the media type to tell an error from a payload. Endpoints without the attribute, such as
`POST /analytics/{name}`, were correct, which is why existing tests passed.

- **Fix:** one global result filter, `fallen-8-core-apiApp/Helper/ProblemDetailsContentTypeFilter.cs`,
  registered with an explicit order so it runs after `ProducesAttribute` and restores the problem media
  type when the value written is a `ProblemDetails`. Chosen over adding a second content type to 75
  `[Produces]` declarations, which would also have advertised `problem+json` as a possible SUCCESS type
  in the document, where it is never correct.
- **Trap worth remembering:** `options.Filters.Add(typeof(T))` builds the descriptor from a
  `TypeFilterAttribute` whose own `Order` is 0, so the filter type's `IOrderedFilter.Order` is ignored.
  The order must be passed at registration: `options.Filters.Add(typeof(T), order)`.

### B58: WAL replay would have rebuilt a registered-algorithm subgraph with the built-in

`high`

Closing B28 (a selectable subgraph algorithm) turned a previously-dormant guard in
`fallen-8-core/Fallen8.Persistence.cs` into a live defect: the replay path recreated a logged subgraph
with `CreateSubGraphTransaction` and never passed the recipe's `AlgorithmPluginName`, on the stated
assumption that "the transaction/REST create is BFS-only, so this never fires". After B28 that
assumption was false, so a crash would have silently resurrected a subgraph built by a different
algorithm as a breadth-first one.

- **Fix:** replay the recorded algorithm. A recipe naming a plugin that is no longer registered now
  fails the create and is warned and skipped, like any other unrecoverable recipe, instead of coming
  back as a different graph.

## Found while verifying, not previously raised

These carry the verifier's citations but have not had a second adversarial pass: strong leads, not settled
findings.

- **[high] A subgraph's Fallen8 engine is never disposed, leaking a writer thread and the whole materialized graph on every recalculation and [...]**  
  Every subgraph is a full Fallen8 instance that owns a background transaction-writer thread and a per-engine metrics Meter. TryRecalculateSubGraph replaces the instance and drops the old one without disposing it, and TryDeregisterSubGraph / DeleteAllSubGraphs only clear dictionaries.  
  *Proof:* fallen-8-core/Fallen8.cs:332-357 - the constructor used by the algorithm (`new Fallen8(_fallen8.LoggerFactory)` at fallen-8-core/Algorithms/SubGraph/BreadthFirstSearchSubgraphAlgorithm.cs:96) builds `_txManager = new TransactionManager(this)` and `Metrics = new Fallen8Metrics(this, ...)`.  
  *Fix:* Give SubGraphFactory one disposal helper and call it at the three abandonment points in fallen-8-core/SubGraph/SubGraphFactory.cs: after the swap in TryRecalculateSubGraph (:605, dispose the OLD instance), in TryDeregisterSubGraph (:503-511) and in DeleteAllSubGraphs (:532-536).
- **[medium] GET /generate has the same missing ceiling, but on MEMORY: nodeCount builds one unbounded transaction up front**  
  CreateGraph validates only 'numeric and >= 0' for nodeCount/edgeCount, then CreateScaleFreeNetworkAsync appends nodeCount entries to a single CreateVerticesTransaction's List before enqueuing it, and the uniform edge path builds nodeCount x min(edgeCount, nodeCount) edge definitions.  
  *Proof:* fallen-8-core-apiApp/Controllers/BenchmarkController.cs:94-124 (only `!Int32.TryParse || nodes < 0` and `edgesPerVertex < 0` are rejected; no upper bound, no cancellation, awaited inline in the action);  
  *Fix:* Decide it together with B38: either accept-and-document (add the honest sentence to benchmark.md's generation section, matching the existing iterations sentence), or add ceilings on nodeCount and on the product nodeCount*edgeCount in BenchmarkController.CreateGraph returning ProblemResults.BadReques [...]
- **[medium] MCP f8_documents ingest_text reports "ingested ... 0 chunk(s)" for a job that has only been queued**  
  POST /document/text answers 202 with the processing STUB summary (status "processing", chunkCount 0, pageCount null). The MCP tool renders that as the text block "ingested '{name}' as document {id}: 0 chunk(s)." - an assertion of completion with an empty result.  
  *Proof:* fallen-8-mcp/Tools/DocumentsTool.cs:277-284 reads chunkCount/documentId off the 202 body and emits the "ingested ..." sentence; ToolResults.Ok puts only that sentence in Content (fallen-8-mcp/Tools/ToolResults.cs:44-54).  
  *Fix:* In fallen-8-mcp/Tools/DocumentsTool.cs:277-284, read the status from the reply and emit e.g. "'{name}' accepted as document {id} (status {status}); poll op=get until it is `indexed`." instead of claiming chunk counts, and add "Ingestion is asynchronous: ingest_text returns a processing stub;
- **[medium] A persisted service whose TryStart() fails is silently dropped from the registry, so the next save loses it**  
  OpenService only adds the service to Services when TryStart() returns true. A service that deserialized fine but cannot start (port in use, transient dependency down) is therefore not registered at all: it vanishes from GET /service-style listings, ShutdownAllServices/StartAllServices can never reach it, and the next checkpoint omits it (PersistencyFactory.Save enumerates Services), so a transient [...]  
  *Proof:* fallen-8-core/Service/ServiceFactory.cs:228-231 (`if (service.TryStart()) { Services.Add(...); }`, no else, no warning); fallen-8-core/Persistency/PersistencyFactory.cs:389-392 + 957 (the save writes exactly what is in Services, so a missing entry is not persisted again).  
  *Fix:* Same edit as above: add the service unconditionally and treat a failed TryStart as a logged, non-fatal degraded state (LogError with the service name) rather than as a reason to forget it.
- **[medium] Benchmark screen never says which namespace it operates on, while the app header shows the active namespace switcher**  
  The Benchmark screen is deliberately Fallen-8-level (always `default`), but nothing on it says so: the header reads "current graph: N vertices · M edges", the generation note reads "Adds vertices with random out-edges ON TOP of the current graph", and the history column is just "graph".  
  *Proof:* fallen-8-web-ui/src/screens/BenchmarkScreen.tsx:119-124 ("current graph: …"), :191-195 ("ON TOP of the current graph"), :265 ("graph" column), no mention of `default` anywhere in the file;  
  *Fix:* fallen-8-web-ui/src/screens/BenchmarkScreen.tsx, say the namespace in the three places: header "default namespace: N vertices · M edges", the generation note "…on top of the DEFAULT namespace's graph, whatever namespace the switcher shows", and the history column header "default graph". Copy-only;
- **[medium] POST /index sample's pluginOptions is a flat string map, but PluginOptions values are PropertySpecification objects**  
  The remarks sample for POST /index sends `"pluginOptions": { "propertyId": "name", "type": "System.String" }`, i.e. string values, while PluginSpecification.PluginOptions is Dictionary<String, PropertySpecification>. Each value must be an object with propertyId/fullQualifiedTypeName/propertyValue, so the documented body fails model binding with 400 - the same defect class as B05/B06 at a site nobo [...]  
  *Proof:* fallen-8-core-apiApp/Controllers/GraphController.Index.cs:158-170 (sample at :161-169, `"pluginOptions": {` at :165) vs Controllers/Model/PluginSpecification.cs:70-72 `public Dictionary<String, PropertySpecification> PluginOptions` and Controllers/Model/PropertySpecification.cs:43-70 (PropertySpecif [...]  
  *Fix:* fallen-8-core-apiApp/Controllers/GraphController.Index.cs:165-168: replace the flat map with the real shape, e.g. `"pluginOptions": { "IndexProperty": { "propertyId": "IndexProperty", "fullQualifiedTypeName": "System.String", "propertyValue": "name" } }`, and regenerate the snapshot.
- **[low] A combined rename+override PATCH is still two separate catalog writes, so a failing second write leaves the rename persisted**  
  Even with B31's parse-first fix, PATCH /ns/{name} carrying both fields performs two independent persist operations. TryRename commits and writes the catalog, then TrySetPluginRegistration writes it again; if that second write throws (disk full, permissions), TrySetPluginRegistration restores only its own field and rethrows, so the request surfaces as an unhandled 500 while the rename remains appli [...]  
  *Proof:* fallen-8-core-apiApp/Namespaces/Fallen8Namespaces.cs:366-377 (rename rolls back only its own failed write, then returns true) and :408-417 (TrySetPluginRegistration restores `ns.PluginRegistrationEnabled = previous` and `throw`s, with no knowledge of the preceding rename);  
  *Fix:* Do not patch this as part of B31. If it is ever worth closing, the right shape is one collection-level method (e.g. TryUpdate(name, newName, pluginRegistration, out ns, out failure)) that mutates both fields under `_writeLock` and performs a single WriteCatalogUnlocked with a combined rollback;
- **[low] The documented 422 contract (XML remark -> OpenAPI snapshot) omits the invalid-UTF-16 cause entirely**  
  Beyond the runtime detail string the claimant raised, the export action's own <response code="422"> documentation lists only two of the three refusal causes, so the pinned OpenAPI snapshot that clients, F8 Studio and MCP agents read never mentions that an allow-listed String/Char with an unpaired surrogate is refused. A client generating from the snapshot cannot know the case exists.  
  *Proof:* fallen-8-core-apiApp/Controllers/BulkController.cs:110-112: "An element carries a property outside the exportable type allow-list (or a null value); the body names the element and property..." - no UTF-16 clause, while JsonlGraphFormat.cs:196-205 refuses on that ground.  
  *Fix:* Same edit as B13 step 3: widen the remark to "An element carries a null property value, a property whose type is outside the exportable allow-list, or a String/Char holding invalid UTF-16 (an unpaired surrogate);
- **[low] GET /analytics/algorithms ships a stale OpenAPI remark: "third-party plugins from assimilated assemblies"**  
  The XML remark on GET /analytics/algorithms tells clients third-party analytics plugins arrive "from assimilated assemblies". Assembly assimilation was removed with the plugin-upload endpoint; there is no Assimilate API any more, and the actual source of third-party entries is the per-namespace plugin registry that the method itself unions in at :105-121.  
  *Proof:* fallen-8-core-apiApp/Controllers/AnalyticsController.cs:92-94 "third-party IGraphAnalyticsAlgorithm plugins from assimilated assemblies appear here too (the same discovery as path and subgraph algorithms)." fallen-8-core/Plugin/PluginFactory.cs:335 states the assimilation API "was removed with the p [...]  
  *Fix:* Reword the remark in AnalyticsController.cs:92-94 to name the real source ("plus the addressed namespace's runtime-registered Analytics plugins - see POST /plugins/algorithm") and regenerate the OpenAPI snapshot with pwsh scripts/update-openapi-snapshot.ps1 (description-only diff at the two lines ab [...]
- **[low] AnalyticsController comment asserts registered analytics plugins resolve by name, which the code contradicts**  
  The comment justifying the registry union in the list endpoint states as fact that "a registered analytics plugin resolves by name through POST /analytics/{name}, so it must appear in the list a picker binds to". Given AlgorithmExists (B04), the premise is false: the list advertises a name the run endpoint refuses.  
  *Proof:* fallen-8-core-apiApp/Controllers/AnalyticsController.cs:105-108 (the comment) versus :337-341 (AlgorithmExists, built-ins only) and :176 / :274 (the 404 it produces).  
  *Fix:* Resolved automatically by B04's fix (the comment becomes true). If B04 were deferred instead, the comment must be corrected to say the run endpoint does not yet resolve registry entries, so the discrepancy is visible at the call site.
- **[low] ABucketIndex's CanPersist and Save/Load are non-virtual, so a derived index cannot opt out of persistence or persist its own state**  
  ABucketIndex is a public abstract class whose CanPersist is hard-wired to true and whose Save/Load serialize only the base key->bucket dictionary, all non-virtual. A derived index that carries extra state (the pattern RangeIndex hints at with its key-set cache) therefore both advertises itself as persistable and silently loses that extra state across a savegame round-trip, with no way to override [...]  
  *Proof:* fallen-8-core/Index/ABucketIndex.cs:55 (public abstract class ABucketIndex : AThreadSafeElement, IIndex), :346 (public Boolean SupportsPointEqualityLookup => true;), :348 (public Boolean CanPersist => true;), :350 (public void Save(SerializationWriter writer)) and :379 (public void Load(Serializatio [...]  
  *Fix:* Either mark CanPersist/Save/Load virtual (so a subclass can chain to base.Save/base.Load and add its own state, or declare itself non-persistable), or state the constraint on the type: extend the class doc at fallen-8-core/Index/ABucketIndex.cs:42-54 with one line saying the persistence format and C [...]
- **[low] env-info.js resolves only F8_PORT properly; the other printed ports ignore .env and their override variables**  
  env-info.js goes to real trouble to resolve the API port (running container, then process env, then the root .env file, then 8080), but the UI, Grafana and OTLP ports are taken from the process environment alone or hardcoded.  
  *Proof:* scripts/env-info.js:51-59 portFromDotEnv() hardcodes the regex to ^\s*F8_PORT\s*=; :61-62 chains it for f8Port only; :64-65 uiPort/grafanaPort read process.env only; :73 prints "OTLP ingest: localhost:4317 (gRPC) / :4318 (HTTP)" as literals.  
  *Fix:* Generalise portFromDotEnv into portFromDotEnv(name) and use it for F8_UI_PORT, F8_GRAFANA_PORT, F8_OTLP_GRPC_PORT and F8_OTLP_HTTP_PORT; print the OTLP line from those two variables instead of literals. Same file, no behaviour outside console output.
- **[low] env-up.js first-start message understates the default model pull**  
  The banner printed before compose runs says the sidecar pulls "phi4-mini + phi4-f8-mini (a few GB)". With the shipped defaults it also pulls bge-m3 (F8_EMBEDDINGS defaults true) and phi4-f8 (F8_PULL_PHI4F8 defaults 1, ~9 GB), which the docs put at over 10 GB, plus the ~4.4 GB docling image. A user sizes their disk and patience off a number that is off by roughly an order of magnitude.  
  *Proof:* scripts/env-up.js:59-63 ("pulls phi4-mini + phi4-f8-mini (a few GB)"). scripts/ollama-init.sh:19-22 defaults F8_PULL_PHI4F8=1 and F8_EMBEDDINGS=true, and :111-133 pulls phi4-mini, the mini fine-tune, bge-m3 and phi4-f8.  
  *Fix:* Update the message in scripts/env-up.js:59-63 to name the actual default set (bge-m3 + phi4-mini + phi4-f8-mini + phi4-f8, over 10 GB, F8_PULL_PHI4F8=0 / F8_EMBEDDINGS=false to trim) so it matches running.mdx. Console text only.
- **[low] A faulted dispatcher still accepts new subscribers, who then get a permanently silent "live" stream**  
  The dispatch loop's catch-all completes and clears every existing subscription so current clients reconnect, but it leaves `_disposed` false while the loop has permanently exited. Any subscribe after that point succeeds, gets a 200 text/event-stream, and receives keepalive comments forever with no event and no resync - exactly the silent degradation the comment there says it is preventing.  
  *Proof:* fallen-8-core/ChangeFeed/ChangeFeedDispatcher.cs:271-287: the catch logs, completes and clears `_subscriptions`, and returns from DispatchLoopAsync; `_disposed` is only set in Dispose (:398).  
  *Fix:* In the catch at ChangeFeedDispatcher.cs:271-287, set a sticky flag (or simply `_disposed = true` under the gate, since the dispatcher is genuinely dead) so TrySubscribe refuses afterwards and the controller answers 503 instead of handing out a dead 200 stream.
- **[low] PUT /unittest log line says "edges per node" for a total edge count**  
  The same log statement that carries the wrong EdgeCount also mislabels it: the graph has 5 vertices and 6 edges in total, but the message reads "... with {VertexCount} nodes and {EdgeCount} edges per node", which would describe a 30-edge graph.  
  *Proof:* fallen-8-core-apiApp/Controllers/SampleGraphController.cs:77 `_logger.LogInformation("It took {ElapsedMs}ms to create a Fallen-8 graph with {VertexCount} nodes and {EdgeCount} edges per node.", sw.Elapsed.TotalMilliseconds, stats.VertexCount, stats.EdgeCount);` against TestGraphGenerator.cs:46-50 (5 [...]  
  *Fix:* Drop " per node" in SampleGraphController.cs:77 ("... {VertexCount} vertices and {EdgeCount} edges"). Same one-line edit window as B20; no route or XML-doc change, so no OpenAPI snapshot regeneration.
- **[low] SampleStats is a REST DTO that is never serialized by any endpoint**  
  `SampleStats` lives under Controllers/Model with `[Required]` annotations and is registered in the source-generated JSON context (and pinned by a parity test), but no action ever returns it - `PUT /unittest` returns `Task` with no body, so the type's only consumer is a log statement.  
  *Proof:* fallen-8-core-apiApp/Controllers/SampleGraphController.cs:65 `public async Task CreateGraph()` (no IActionResult, no body) with the stats used only at :77; fallen-8-core-apiApp/AppJsonContext.cs:63 `[JsonSerializable(typeof(SampleStats))]`;  
  *Fix:* Either drop the AppJsonContext registration plus its two JsonSourceGenParityTest fixtures and demote SampleStats to an internal tuple/record, or make `PUT /unittest` actually return the stats.
- **[low] Generic PUT /index/{indexId} reports 200/true for an add a vector index silently dropped**  
  AddToIndex returns true whenever the index and the element both exist, regardless of whether the index actually filed anything. On any vector index the key coming from the generic route is a scalar (never a float[]), so VectorIndex.AddOrUpdate logs a warning and returns without indexing - and the caller is told the add succeeded.  
  *Proof:* fallen-8-core-apiApp/Controllers/GraphController.Index.cs:218-219 returns true immediately after idx.AddOrUpdate with no result check; fallen-8-core/Index/Vector/VectorIndex.cs:269-282 returns after a warning when 'key is float[]' fails;  
  *Fix:* In AddToIndex, refuse the generic add up front when the index is an IVectorIndex ('a vector key is not expressible on this route; use PUT /index/vector/{indexId}', and the bound-index message when EmbeddingName != null) - the same ActionResult<bool> shape B19's fix already gives this action, so no e [...]
- **[low] MCP invalid-op error messages list ops the caller was never offered**  
  The default branch of both consolidated tools names the full op set including write/code ops, even when EnableWrite/EnableCode are off and those ops were not advertised in the schema enum, inviting an agent to retry an op it cannot use (it then gets a 403).  
  *Proof:* fallen-8-mcp/Tools/DocumentsTool.cs:363-365 ("op must be list, get, search, binding, entities, ingest_text, delete, or bind.") and fallen-8-mcp/Tools/PluginsTool.cs:219-221, versus the cap-derived enum built at DocumentsTool.cs:67-74 and PluginsTool.cs:70-78.  
  *Fix:* Build the message from the same ops list Describe computes (or reuse it via a small helper) so the error text mirrors the advertised enum; assert it in McpDocumentsToolTest under read-only caps.
- **[low] ServiceFactory.OpenService ignores its startService flag, so the whole StartServices plumbing is dead**  
  OpenService takes Boolean startService and never reads it, it always calls TryStart(). Every caller in the chain therefore passes a flag that has no effect: LoadSpecification.StartServices (default true) -> LoadTransaction.StartServices -> Fallen8.Load_internal -> PersistencyFactory.Load -> LoadServices -> LoadAService -> OpenService.  
  *Proof:* fallen-8-core/Service/ServiceFactory.cs:212-232 (parameter unused; unconditional TryStart); fallen-8-core/Persistency/PersistencyFactory.cs:1096 (passes startService); fallen-8-core/Transaction/LoadTransaction.cs:39-43,57;  
  *Fix:* fallen-8-core/Service/ServiceFactory.cs OpenService: register the loaded service first, then start it only when startService is true, e.g. `Services.Add(serviceName, service); if (startService) { service.TryStart();
- **[low] Dev proxy prefix "/index" also captures /index.html**  
  `API_PREFIXES` matching is `url.startsWith(prefix)`, so the `/index` entry (meant for the index-management REST routes) also matches `/index.html`. A dev navigating to http://localhost:5173/index.html gets proxied to the apiApp and a 404 instead of the SPA. Same class of trap as the `/config` vs `/config.js` collision noted under B53.  
  *Proof:* fallen-8-web-ui/vite.config.ts:48 (`"/index"`) and :126-128 (prefix keys), with Vite's matcher at node_modules/vite/dist/node/chunks/config.js:22085-22086 (`url.startsWith(context)`); fallen-8-web-ui/index.html is the dev entry document.  
  *Fix:* fallen-8-web-ui/vite.config.ts, express the short/ambiguous entries as anchored regex keys (`"^/index(/|$)"`, `"^/config$"`) instead of bare prefixes; leave the unambiguous ones as-is.
- **[low] The fallen8.index.entries metric sums the R-Tree's -1 sentinel, under-reporting (and possibly going negative)**  
  Fallen8.IndexEntriesForMetrics adds CountOfKeys() for every index without guarding the negative "not supported" sentinel, so each spatial index subtracts 1 from the exported gauge; with a spatial index as the only index the gauge reads -1. Same sentinel leak as B52, one layer down and outside the REST DTOs.  
  *Proof:* fallen-8-core/Fallen8.Metrics.cs:63-79 (`total += index.CountOfKeys();` over GetIndicesSnapshot(), no sentinel guard) with fallen-8-core/Index/Spatial/Implementation/RTree/RTree.cs:1156-1159 (`return -1`);  
  *Fix:* fallen-8-core/Fallen8.Metrics.cs: skip negative counts in the loop (`var count = index.CountOfKeys(); if (count > 0) total += count;`) with a one-line comment pointing at the IIndex sentinel contract.
- **[low] A recalculation that fails for an internal reason is reported as 409 with a message naming the wrong cause**  
  POST /subgraph/{name}/recalculate maps every false from TryRecalculateSubGraph to one fixed 409 whose detail says the source graph or algorithm plugin is missing. But the same false is returned when the algorithm's extraction fails or an exception is caught, so a genuine internal fault is presented to the client as a client-side conflict with a false explanation - the opposite of the /path error c [...]  
  *Proof:* fallen-8-core-apiApp/Controllers/SubGraphController.cs:407-411 - `if (!_fallen8.SubGraphFactory.TryRecalculateSubGraph(name)) return ProblemResults.Conflict("Subgraph '{0}' cannot be recalculated (missing source graph or algorithm plugin).")`.  
  *Fix:* Add a reason-reporting internal overload of TryRecalculateSubGraph mirroring the create path's `out TransactionFailureReason` (fallen-8-core/SubGraph/SubGraphFactory.cs:174-185), and in SubGraphController.RecalculateSubGraph map Conflict/None-with-missing-metadata to 409 with the current message and [...]

## Refuted and duplicate

Recorded so they are not re-raised. Refuted means the code was read and found correct or the behaviour
deliberate, not merely that the claim was unproven.

- **B23** (refuted): wwwroot served anonymously is deliberate, required, and already documented verbatim
  - The claimant's mechanical description is accurate (UseStaticFiles at Program.cs:531 runs before UseAuthentication/UseAuthorization at :547-548, so every file under wwwroot - including /samples/*.jsonl and the wind-farm documents - is reachable without an API key), but this is the required and documented posture, not a defect.
- **B47** (duplicate of B04): Same defect as B04: AnalyticsController.AlgorithmExists ignores the plugin registry
- **B02** (refuted): Base compose declaring ingestion/NLP/OTLP on while the sidecars ride profiles is the deliberate profile design, and it degrades gracefully
  - Nothing is broken. The facts the claim cites are all true, but this is how compose profiles are meant to be used: the capability flag lives in the base service, the sidecar image is profile-gated so it is not pulled unless asked for, and scripts/env-up.js activates the profiles plus the observability overlay.
- **B03** (refuted): MCP anonymous read surface next to F8_API_KEY is an explicit, thrice-documented local-dev posture, not a silent hole
  - The mechanics the claim describes are exactly right, but "silently" is wrong: the posture is an explicit opt-in guarded by a fail-closed startup gate, called out in the compose comment, and stated in the user docs in the same words the claim uses.
- **B50** (duplicate of B45): Same stale docker-compose.split.yml header comment as B45
- **B32** (refuted): Bare ?since=<seq> being treated as the current epoch is the specified contract, not a bug
  - Nothing in the code is wrong. `epochMatches = !sinceEpoch.HasValue || sinceEpoch.Value == Epoch` implements a documented, spec'd contract: the `<epoch>:<seq>` form is epoch-guarded, the bare-`seq` form is explicitly "assumed current epoch" because the client chose not to send an epoch. The claimant read "no epoch supplied" as "epoch mismatch";
- **B30** (duplicate of B20): Same EdgeCount 7 vs six edges defect as B20
- **B11** (duplicate of B14): Bound-index removal routes not refused (same hole as B14, and one of its two routes is unreachable)
- **B15** (duplicate of B19): Index routes 500 on a bad type name / value - same unguarded ServiceHelper.CreateObject as B19
- **B46** (refuted): f8_plugins / f8_documents DestructiveHint=true is a deliberate, documented, fail-safe hint - and correct for f8_plugins
  - Not a defect. MCP annotations are explicitly untrusted confirmation-UX hints in this codebase, and the coarse per-tool annotation on the two consolidated read-tier tools is deliberate and documented. For f8_plugins it is substantively right even under read-only caps: `invoke` executes user-registered C# whose captured IFallen8 has full write reach, so the tool is not read-only in any configuration [...]
- **B40** (refuted): Startup load leaving persisted services stopped, the claimed effect cannot happen
  - The claim's plumbing facts are right (LoadTransaction.StartServices defaults to false and DurabilityLifecycleService does not set it), but the conclusion is wrong: the flag never reaches a decision. ServiceFactory.OpenService ignores its startService parameter and calls service.TryStart() unconditionally, so a startup load starts persisted services exactly like PUT /savegames/{id}/load does.
- **B36** (refuted): Editing the managed default instance is non-persisted by design and documented
  - The mechanism the claim describes is real (partialize drops the `local` record and merge re-synthesizes it with `auth {kind:"none"}` and the config.js baseUrl), but it is the deliberate, documented contract of the managed default, not a defect: the docs page the auditor was reading states that an API key typed into the managed record with Edit works for that session only and tells the user to regi [...]
- **B37** (refuted): Benchmark screen already uses the UNBOUND instance, so its counts and its benchmark describe the same (default) graph
  - The claim's premise is wrong: BenchmarkScreen does not use the namespace-bound view. It calls `useActiveInstance()`, which returns the raw registry record, and the registry never sets `namespace` (only useInstanceStore's derived view does).
- **B48** (duplicate of B28): PUT /subgraph has no algorithm field, so a registered SubGraph plugin is unreachable over REST

