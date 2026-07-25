# Plugin Registration — Implementation Plan

> Companion to [spec.md](./spec.md). Branch `feature/plugin-registration` (created, from `main`).
> Design decisions settled (spec §8). GitHub issue + draft PR to open at Phase 0.
> v1 categories: **`algorithm` (path/subgraph/analytics) + `function`** (index dropped, service
> deferred).

## Landing order rationale

Engine registry first (no REST), then the compile bridge + typed REST, then the resolution seam +
the graph-function entry point (the invasive edits) with transparent-invocation and function-invoke
tests, then durability, then removal of the DLL path, then MCP + Studio (incl. NL-assist) + docs.
Each phase builds clean and keeps the full suite green.

## Phase 0 — Track setup (no code)

- [ ] Open the `feature` issue; open the draft PR referencing it (`Closes #<n>`); link the
      `features/open/plugin-registration/` docs.

## Phase 1 — Engine registry + model (no REST, no Roslyn)

Mirror `fallen-8-core/StoredQueries/` under `fallen-8-core/Plugins/`.

- [ ] `PluginCategory` (Algorithm | Function) + `PluginContract` (Path | SubGraph | Analytics |
      GraphFunction) — persisted by name.
- [ ] `PluginDefinition` (name, category, contract, sourceCode, description, createdAt) — source +
      metadata only; `JsonStringEnumConverter`; immutable by convention.
- [ ] `PluginCompileState` (Compiled | Failed | SourceOnly); `PluginEntry` (Definition + state +
      `Object Artifact` (the compiled `Type`) + diagnostics), "Compiled ⇒ non-null artifact" invariant.
- [ ] `IGraphFunction : IPlugin` (`TryInvoke(out GraphFunctionResult, IDictionary<string,object>)`) and
      `GraphFunctionResult` (references to existing `VertexModel`/`EdgeModel`; `FromElements(...)`).
- [ ] `PluginRegistry` — copy-on-write snapshot dictionary, `TryRegister`/`TryRemove` (writer-thread,
      `enforceQuota`), `ReplaceAll`/`Clear`, lock-free `TryGet`/`GetAll`/`Count`, `IsValidName`,
      `MaxCount` (default 64).
- [ ] `IPluginCompiler` engine→API bridge + `IPluginCompiler PluginCompiler { get; set; }` on engine.
- [ ] Wire onto `Fallen8` / `IFallen8Admin` / `AFallen8` / `AddressedFallen8` (per-request dispatch) /
      `Fallen8Namespaces.CreateEngine` (construct per namespace, set `MaxCount`); config
      `Fallen8PluginOptions` (`Fallen8:Plugins:MaxCount`).
- [ ] `RegisterPluginTransaction { Entry, BypassQuota }` / `RemovePluginTransaction { Name }` with
      re-check + rollback + `ReleaseAfterCompletion`, mirroring the stored-query transactions.
- [ ] Tests: registry register/remove/quota/duplicate/name-validation/snapshot-isolation;
      transaction re-check/rollback. (Artifacts are stub `Type`s — no Roslyn yet.)

## Phase 2 — Full-type compile bridge + contract validation (apiApp)

- [ ] A full-compilation-unit Roslyn path (new `PluginCodeGenerationHelper` or an addition to
      `CodeGenerationHelper`): parse user source, compile against the engine reference set, load into
      a collectible `AssemblyLoadContext`, return the loaded types. Apply the existing source-length
      compile bound.
- [ ] `PluginCompiler : IPluginCompiler` — compile, then per-contract validation: exactly one public
      non-abstract class with a public parameterless ctor implementing the contract interface;
      activates without throwing; non-empty `PluginName`; `PluginName == name`. Diagnostics in the
      `/delegates/validate` message shape.
- [ ] Register the compiler on each namespace engine at startup (where the stored-query/recipe
      compilers register).
- [ ] Tests: valid algorithm type, valid function type, zero/multiple implementors, no parameterless
      ctor, throws-on-activate, name mismatch, oversize source — each to the right diagnostic.

## Phase 3 — Typed REST + the resolution seam + function invoke

- [ ] `PluginsController` (`[ApiVersion("0.1")]`, not `[Fallen8Level]`): `POST /plugins/algorithm`,
      `POST /plugins/function`, `POST /plugins/function/{name}/invoke`, `GET /plugins`,
      `GET /plugins/{name}`, `DELETE /plugins/{name}` — full `[ProducesResponseType]`/`[Consumes]`/
      `[Produces]`/XML docs; compile-before-enqueue; gate + sensitive rate limit + body cap on the
      registration POSTs (invoke is authenticated only). DTOs: per-category registration bodies,
      `PluginSummaryREST`, `PluginDetailREST`, `GraphFunctionResultREST` (`{ vertices, edges }` via the
      existing vertex/edge DTOs).
- [ ] **Resolution seam:** `Fallen8.TryResolvePlugin<T>(name, out T)` = registry-first then
      `PluginFactory.TryFindPlugin<T>`. Route `ResolveCachedPlugin` (path/analytics/subgraph) and
      `SubGraphFactory` through it. Union the enumeration surfaces (`TryGetAvailablePlugins<T>`
      consumers, status/analytics "available" lists, `SaveGameRegistry`).
- [ ] **Function entry point:** `Fallen8.TryInvokeGraphFunction(out GraphFunctionResult, name,
      parameters)` — registry-only resolve, activate/initialize, call `TryInvoke`.
- [ ] Built-in-name-collision rejection at registration (§8.7 sub-decision).
- [ ] Gate: repurpose `EnableDynamicPluginLoading` + `DynamicPluginPolicy` for the registration POSTs.
- [ ] Tests: full REST matrix (201/400/401/403/409/413/429/500); the §4.5 gate matrix in both switch
      states; **transparent algorithm equivalence** (registered == built-in via /path //analytics
      //subgraph); **function invoke** (full-scan + index-query variants, parameters, unknown/`Failed`
      → 404, read-only); namespace isolation (ns A unresolvable/uninvokable in ns B); empty-registry
      resolution byte-identical to today (built-in tests stay green).

## Phase 4 — Durability (manifest + WAL)

- [ ] `PluginManifest` (formatVersion + definitions); `Constants.PluginManifestString`; source-gen in
      `CoreJsonContext`.
- [ ] `PersistencyFactory`: `SavePlugins` (atomic sidecar, empty ⇒ delete stale) on the recipe/
      stored-query side of the commit point; `LoadPluginDefinitions` (missing ⇒ empty; version/read
      error ⇒ loud, no plugins, never fail the load).
- [ ] `Fallen8.Persistence`: `RehydratePlugins` (eager recompile via `IPluginCompiler`; keep `Failed`
      + diagnostics on failure; `SourceOnly` with no compiler) → `PluginRegistry.ReplaceAll`.
- [ ] WAL: `WalEntryType.RegisterPlugin = 17`, `RemovePlugin = 18`; `WalTransactionCodec`
      encode/decode + `TryDecodePluginRegister`; `ReplayPluginRegister` (`BypassQuota`), remove
      skip-and-continue on failure.
- [ ] Tests: Save→fresh engine→Load round-trip + resolve/invoke; WAL register+remove+register replay
      identical; failed recompile on load/replay keeps `Failed` + recovery continues; invoke-during-
      remove; ALC unload after delete (weak-reference).

## Phase 5 — Remove the DLL path

- [ ] Delete `AdminController.UploadPlugin` (`PUT /plugin`) and its DTO/policy usage.
- [ ] Delete `PluginFactory.Assimilate`, `AddPluginSearchDirectory`, `_extraSearchDirectories`, and the
      `Program.cs` upload-directory wiring. Keep base-directory discovery (built-ins).
- [ ] `Fallen8SecurityOptions`: drop `PluginDirectory`/`ResolvePluginDirectory`; repurpose
      `EnableDynamicPluginLoading` doc to "gates source plugin registration" (default to repurpose, not
      rename — a rename ripples through config/docs/tests).
- [ ] Update the `EnginePerformanceTest` assertions referencing the `Assimilate` invalidation to the
      registry equivalent (or drop the DLL-specific ones), keeping the name-map/perf intent.
- [ ] Tests: `PUT /plugin` → 404; `Assimilate` gone; built-in BLS/DIJKSTRA/analytics/subgraph still
      resolve.

## Phase 6 — MCP + OpenAPI + Studio (incl. NL-assist) + docs (move together)

- [ ] Regenerate the OpenAPI snapshot (`scripts/update-openapi-snapshot.ps1`); review the diff
      (`/plugin` removed — deliberate; `/plugins/*` added).
- [ ] MCP: `f8_plugins` tool (Read: list/get/function-invoke; Write/Admin: delete; `code`-gated:
      register) or fold into `f8_admin`; add `/plugins/*` (incl. function invoke) to
      `McpBridgedEndpoints.All`; narrow the coverage-test deferral to `/service` only (drop `/plugin`);
      update `McpContractTest`. `McpRestCoverageTest` green.
- [ ] REST: add `POST /plugins/{category}/validate` (side-effect-free compile + contract-validate) for
      Studio.
- [ ] Studio: plugin-authoring UI beside `StoredQueriesPanel` (category/contract → scaffold →
      full-file Monaco → plugin-aware validate → register to namespace → list/inspect/delete); a
      **function runner** (parameter form → invoke → render vertices/edges); **NL-assist for whole
      types** (per-category/-contract prompt scaffolding + few-shot whole-type examples + the
      generate→validate→refine loop against the plugin-aware validate endpoint). Vitest coverage.
- [ ] Docs: new `docs/plugin-registration.md`; update `docs/plugins.md` (the DLL story is gone);
      README "Key features" entry + `docs/` index row; update `api-security-boundary` and `mcp-server`
      living docs; append a `nl-assist-finetune/RETRAIN-LOG.md` entry (whole-type authoring examples).
- [ ] Move `features/open/plugin-registration/` → `features/done/` on merge.

## Cross-surface obligation checklist (all must land in the same PR)

- [ ] Engine: `PluginRegistry`, `IGraphFunction`, transactions, resolution seam + function entry
      point, persistence, WAL.
- [ ] REST: typed registration + function invoke + plugin-aware validate; transparent algorithm
      invocation; DLL path removed; OpenAPI snapshot regenerated.
- [ ] MCP: bridged (list/get/invoke/delete/register); `McpRestCoverageTest` + `McpContractTest` green.
- [ ] Studio: authoring UI + function runner + NL-assist + validate endpoint.
- [ ] Docs + README + `docs/` index + RETRAIN-LOG.
- [ ] Quality gates: MIT header on every new file; `Try*(out,…):bool`; warnings-as-errors; no
      `Console.Write*` / `DateTime.Now` (use `DateTime.UtcNow` / `DateHelper` per allowlist); exact
      package versions; MSTest arrange/act/assert with branch coverage.

## Council merge gate

Full build clean, full suite green, OpenAPI/MCP/coverage gates green, honesty notes intact, then the
principal-council review before merge to `main` (per the repo workflow).
