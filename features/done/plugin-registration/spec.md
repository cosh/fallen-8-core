# Plugin Registration — Specification

> **Status:** Implemented (branch `feature/plugin-registration`, 2026-07-25). Design decisions in
> §8 were reviewed and settled before implementation; this revision reflects them. All six plan
> phases landed; full C# suite + web-ui build/tests green. Pending: principal-council review before
> merge to `main`.
>
> **One-line:** replace the DLL-upload plugin path with typed, source-based, namespace-scoped
> plugin registration — generalizing the stored-query pattern
> ([stored-query-library](../../done/stored-query-library/)) from *fragments* to *whole plugin
> types*, and adding a user-authored **graph-function** category (a stored graph procedure).

## 1. Overview & motivation

### The "from" surface (what exists today)

Fallen-8 lets a caller add a plugin at runtime by uploading a **compiled assembly**:

- `PUT /plugin` — `AdminController.UploadPlugin([FromBody] Stream dllStream)`
  ([AdminController.cs:615](../../../fallen-8-core-apiApp/Controllers/AdminController.cs#L615)).
  `[Fallen8Level]` (process-global, shared by every namespace), `[Consumes("application/octet-stream")]`,
  gated by `Fallen8SecurityOptions.DynamicPluginPolicy`
  (`Fallen8:Security:EnableDynamicPluginLoading`, default **false** → 403), rate-limited, 64 MiB cap.
- The stream is written to disk as a random `.dll` in the configured isolated plugin directory
  and `PluginFactory.Assimilate(stream, path)`
  ([PluginFactory.cs:211](../../../fallen-8-core/Plugin/PluginFactory.cs#L211)) invalidates the
  discovery cache. On the **next** plugin lookup, `DiscoverCandidateTypes` re-scans every `*.dll`
  in the base directory **plus** the registered upload directory
  (`AddPluginSearchDirectory`, [Program.cs:330](../../../fallen-8-core-apiApp/Program.cs#L330)),
  `Assembly.Load`s each, and `Activator.CreateInstance`s the eligible types — **in-process, full
  trust**.

This is the most dangerous surface in the product: an opaque, unvalidated binary running arbitrary
code with the server's full authority. It is a literal DLL-upload endpoint (the danger is not
merely "the design allows external assemblies" — the endpoint exists and does exactly that). The
XML doc already admits it is "a trust boundary, not a sandbox."

Distinct and **kept**: the Roslyn *source*-compilation path
([CodeGenerationHelper.cs](../../../fallen-8-core-apiApp/Helper/CodeGenerationHelper.cs)) behind
`/path`, `/subgraph`, `/storedquery`, `/delegates/validate`. That compiles caller-submitted C#
*source* in-process into a collectible `AssemblyLoadContext`. It is the pattern this feature
generalizes, not the surface it removes.

### The "to" surface (what this feature builds)

Plugins become **C# source authored in the browser (F8 Studio), submitted to a typed REST
registration endpoint that compiles and validates the source against a known plugin contract, and
stored scoped to the namespace they were registered under** — exactly the stored-query lifecycle,
lifted from *fragment* to *whole plugin type*:

1. Author source against a per-category scaffold in Studio (optionally NL-assisted).
2. `POST` to the category's typed registration endpoint. The server compiles with Roslyn,
   validates the compiled type satisfies the category contract, and enqueues a registration
   transaction into the target namespace's plugin registry.
3. Registration requires the dynamic-plugin gate (a provisioning window). **Invoking** an
   already-registered plugin does not — exactly like stored queries.
4. The registry is namespace-scoped and survives Save/Load and crash-recovery through the same
   manifest + WAL mechanism stored queries use.

The single generic `PUT /plugin` endpoint is replaced by a **closed, growing set of per-category
typed endpoints**. Adding a plugin category means a maintainer adds a new typed endpoint + contract
+ scaffold + validator in code — never widening a catch-all at runtime.

### Honesty note (matching the README voice for stored queries and `api-security-boundary`)

This narrows *who can introduce code* and makes registration auditable and gate-able. It is **not a
sandbox**: a registered plugin still runs in-process with full trust. The security win over
`PUT /plugin` is real but specific — the server now *sees and validates the source and the contract
it satisfies* instead of loading an opaque binary, and every registration is a typed, logged,
per-namespace transaction. It does not isolate the code. Out-of-process / WASM isolation remains the
`api-security-boundary` follow-on, unattempted here and not claimed.

## 2. The two axes (kept distinct — they behave differently)

- **Plugin *category*** (defined by maintainers, in code): each category has its own contract, its
  own validation, its own typed REST endpoint + scaffold. The set is **closed** and grows only when
  a maintainer adds one. This is the "extended with every new plugin type" surface.
- **Plugin *instance*** (registered at runtime by a user): validated C# **source** for a whole type,
  scoped to a namespace, referenced by name. This is what replaces the uploaded DLL.

Built-in plugins (algorithms compiled into `fallen-8-core.dll` and discovered via `PluginFactory`)
are unaffected — they stay exactly as they are. Only the runtime *external-assembly* path is removed.

## 3. v1 categories

Two categories ship in v1:

### 3a. `algorithm` — a runtime-registered algorithm plugin

The three existing `IPlugin` algorithm families, authored as source instead of uploaded as a DLL:

| Contract | Interface | Invoked today via |
|---|---|---|
| `Path` | `IShortestPathAlgorithm` | `POST /path/{from}/to/{to}` (`pathAlgorithmName`) |
| `SubGraph` | `ISubGraphAlgorithm` | `PUT /subgraph` (`algorithm`) |
| `Analytics` | `IGraphAnalyticsAlgorithm` | `POST /analytics/{algorithmName}` |

**Invocation is transparent — no new endpoint.** A registered algorithm resolves *by name* through
the existing endpoints, because the engine's name→plugin resolution seam (§4.4) consults the
namespace registry before the built-in `PluginFactory`.

### 3b. `function` — a stored graph function (the new category)

A user-authored graph procedure, analogous to a stored function/procedure in other databases:
author it once in the code UI, store it on the service, call it by name whenever needed.

- **Contract:** a new engine interface `IGraphFunction : IPlugin` with
  `bool TryInvoke(out GraphFunctionResult result, IDictionary<String, Object> parameters)`. The
  plugin reaches the graph through the `IFallen8` it captures in `Initialize` (as algorithm plugins
  already do) — so inside `TryInvoke` it can do a **full scan** (`GetAllVertices`/`GetAllEdges`/
  `GetAllGraphElements`) **or an index query**, driven by the call-time `parameters` bag.
- **Result:** `GraphFunctionResult` **references existing** vertices/edges in the live graph; the
  REST layer projects them with the *same DTOs* as `GET /vertex/{id}` and `GET /edge/{id}` (no deep
  copy, no data duplication). The result is a view into the graph at call time — a concurrently
  removed element simply reflects the snapshot the function read (documented read semantics).
- **Read-only in v1.** A graph function reads the graph and returns a projection; it does not
  mutate. Write-capable functions (a "stored procedure that changes the graph") are a deliberate
  later track — they would have to run on the single writer thread through a transaction, a
  materially different lifecycle. Flagged, not silently allowed.
- **Invocation needs a NEW endpoint** (unlike algorithms — there is no existing endpoint that runs
  an arbitrary named graph function): `POST /plugins/function/{name}/invoke` with the parameter
  body, returning the projected vertices/edges. Resolution is registry-only (there are no built-in
  functions). Invoking is ungated (running already-registered code, like the others); registration
  stays behind the dynamic-plugin gate.

There are **no built-in graph functions**; the category exists purely for user-authored procedures.

## 4. Design

### 4.1 Model & the authoring unit

```
fallen-8-core/Plugins/
  PluginDefinition.cs     name, category, contract, sourceCode, description, createdAt (source+meta only)
  PluginCategory.cs       Algorithm | Function          (persisted by NAME, not ordinal)
  PluginContract.cs       Path | SubGraph | Analytics | GraphFunction   (closed enum)
  PluginRegistry.cs       registry: register/remove (writer thread), snapshot reads, quota, IsValidName
  PluginEntry.cs          Definition + PluginCompileState + Artifact (compiled Type + pinned ALC) + diagnostics
  PluginCompileState.cs   Compiled | Failed | SourceOnly
  IGraphFunction.cs       new plugin contract: TryInvoke(out GraphFunctionResult, parameters)
  GraphFunctionResult.cs  references to existing vertices/edges (no deep copy)
  IPluginCompiler.cs      engine→API compile bridge (mirrors IStoredQueryCompiler)
  PluginManifest.cs       persisted JSON document (formatVersion + definitions)
fallen-8-core/Transaction/
  RegisterPluginTransaction.cs / RemovePluginTransaction.cs
fallen-8-core-apiApp/
  Controllers/PluginsController.cs
  Controllers/Model/PluginRegistration*.cs / PluginSummaryREST.cs / PluginDetailREST.cs / GraphFunctionResultREST.cs
  Helper/PluginCompiler.cs        implements IPluginCompiler (full-type Roslyn compile + contract validation)
```

**The authoring unit is a whole type, not a fragment** (the pivotal difference from stored queries).
Stored queries author *method bodies* the harness wraps into a fixed generated class
(`CodeGenerationHelper` owns the class shape). That works because a path filter/cost set *is* a fixed
set of delegate slots. But a genuine plugin's contract carries real logic —
`ISubGraphAlgorithm.TryCreateSubgraph`, `IShortestPathAlgorithm.TryCalculateShortestPath`,
`IGraphAnalyticsAlgorithm.TryRunAnalytics`, `IGraphFunction.TryInvoke` — that cannot be a
fill-in-the-blank fragment. A DLL contained a full type; its source-based replacement must let the
user author a full type.

So the user submits a complete compilation unit, e.g. a graph function:

```csharp
public sealed class NeighboursOfLabel : IGraphFunction
{
    private IFallen8 _graph;
    public string PluginName     => "NeighboursOfLabel";
    public Type   PluginCategory => typeof(IGraphFunction);
    public string Description    => "All vertices with a given label and their edges.";
    public string Manufacturer   => "acme";
    public void   Initialize(IFallen8 fallen8, IDictionary<string, object> parameter) => _graph = fallen8;
    public void   Dispose() { }

    public bool TryInvoke(out GraphFunctionResult result, IDictionary<string, object> parameters)
    {
        var label = parameters != null && parameters.TryGetValue("label", out var l) ? l as string : null;
        var vertices = _graph.GetAllVertices().Where(v => v.Label == label).ToList();
        result = GraphFunctionResult.FromElements(vertices, /* edges */ ...);
        return true;
    }
}
```

Studio provides a per-category/-contract **scaffold** (the class stub with correct usings, the
interface, the `IPlugin` members and the contract method pre-stubbed) so the user fills in the body,
not the boilerplate.

**Contract validation** (in `PluginCompiler`, per contract): compile the compilation unit against
the engine reference set; then require
1. exactly **one** public, non-abstract class with a public parameterless constructor that
   implements the contract's interface (zero → 400 "no type implements `<contract>`"; more than one
   → 400 "ambiguous");
2. it activates without throwing and returns a non-empty `PluginName`;
3. `PluginName == name` (so the persisted name and the CLR `PluginName` used at resolution never
   diverge).
Diagnostics (Roslyn errors, or the structural failures above) come back as a 400 body in the same
message shape as `/delegates/validate` and `/storedquery`.

Names: `^[A-Za-z0-9_-]{1,128}$`, ordinal. A per-namespace quota `MaxCount` (default 64, configurable
via `Fallen8:Plugins:MaxCount`) rejects registration beyond the cap with 409 — the stored-query/
subgraph-quotas pattern (pinned compiled artifacts are process memory; each holds a collectible ALC
alive).

### 4.2 REST contract

Absolute routes on a `[ApiVersion("0.1")]` controller; `/ns/{ns}/…` twins via
`NamespaceRouteConvention` (the controller is **not** `[Fallen8Level]` — the registry is
per-namespace).

| Endpoint | Gate | Behaviour |
|---|---|---|
| `POST /plugins/algorithm` | dynamic-plugin capability + sensitive rate limit + body cap | Register an algorithm plugin. Body `{ name, contract: "Path"\|"SubGraph"\|"Analytics", description?, sourceCode }`. Validate name + contract → compile + contract-validate → enqueue `RegisterPluginTransaction`, await. 201; 400 malformed/compile/contract (diagnostics in body); 401/403 gate; 409 duplicate/quota/built-in-collision; 413; 429; 500 rollback. |
| `POST /plugins/function` | same | Register a graph function. Body `{ name, description?, sourceCode }` (single contract `GraphFunction`, no discriminator). Same flow. |
| `POST /plugins/function/{name}/invoke` | authenticated (not the capability) | Run a registered function. Body: the parameter bag. 200 with `{ vertices, edges }` projected by the existing DTOs; 404 unknown/`Failed`; 400 bad parameters; 500 if `TryInvoke` faults. |
| `GET /plugins` | authenticated | List summaries (name, category, contract, description, createdAt, compileState) for the addressed namespace. 200. |
| `GET /plugins/{name}` | authenticated | Full definition **including source** and (if `Failed`) recompile diagnostics. 200 / 404. |
| `DELETE /plugins/{name}` | authenticated | Enqueue `RemovePluginTransaction`. 204 / 404 / 500 on rollback. Compiles nothing; never gated by the capability. |

Registration is **per top-level category** (`algorithm`, `function`); the algorithm route carries a
**closed contract discriminator** in the body (an unknown value is a 400, so it is typed-and-closed,
never an open catch-all). A `{category}` route parameter is deliberately not used — that reads as
the catch-all being removed.

**Transparent algorithm invocation** — no new endpoints: `POST /path/{from}/to/{to}`
(`pathAlgorithmName`), `POST /analytics/{algorithmName}`, `PUT /subgraph` (`algorithm`) resolve a
registered algorithm from the addressed namespace's registry, then built-ins (§4.4).

### 4.3 Engine registry, transactions, concurrency (the stored-query pattern, reused)

- `PluginRegistry` on `Fallen8` (declared on `IFallen8Admin`, constructed per namespace in
  `Fallen8Namespaces.CreateEngine` alongside `StoredQueries`), so it is namespace-scoped by
  construction and dispatched per-request by `AddressedFallen8`. One registry holds both categories;
  the category is a field on `PluginDefinition`, so there is one pair of transactions and one pair of
  WAL entry types, not per-category machinery.
- `RegisterPluginTransaction { Entry, BypassQuota }` / `RemovePluginTransaction { Name }` run on the
  single writer thread; the controller **compiles before enqueue** (Roslyn never occupies the writer
  thread) and hands over the already-compiled entry. The transaction re-checks duplicate/quota/
  not-found on the writer thread (TOCTOU resolves there) — exact mirror of the stored-query
  transactions.
- Reads (resolution, list/get, function invoke) are lock-free snapshot reads.
- **Invoke-during-remove:** resolution captures the entry's artifact (`Type` + its ALC) once; a
  concurrent removal either wins before resolution or the in-flight activation/use completes against
  the captured artifact — never torn. Deletion drops the entry's reference so the collectible ALC
  unloads once no instance remains referenced (`collectible-codegen-assemblies` semantics, pinned by
  a weak-reference test).

### 4.4 The resolution seam (the main architectural change and risk)

Built-in algorithm resolution is name→type via `PluginFactory.TryFindPlugin<T>` (process-global,
assembly scan). Registered plugins live in the per-namespace registry as compiled `Type`s. The
resolution call sites all sit **inside the engine** with a `Fallen8` in hand:

- `Fallen8.ResolveCachedPlugin<T>` — path (`IShortestPathAlgorithm`), analytics
  (`IGraphAnalyticsAlgorithm`), subgraph-via-engine
  ([Fallen8.cs:481](../../../fallen-8-core/Fallen8.cs#L481)).
- `SubGraphFactory` resolution ([SubGraphFactory.cs:240](../../../fallen-8-core/SubGraph/SubGraphFactory.cs#L240)).

The change: one engine-level helper `Fallen8.TryResolvePlugin<T>(name, out T)` = registry-first
(activate a `Compiled` entry whose category/contract matches `T`) then `PluginFactory.TryFindPlugin<T>`.
Route the algorithm/subgraph call sites through it. Enumeration surfaces (`TryGetAvailablePlugins<T>`
consumers, status/analytics "available" lists, `SaveGameRegistry`) **union** the namespace registry
with the built-ins.

Graph functions add a new engine entry point `Fallen8.TryInvokeGraphFunction(out GraphFunctionResult,
name, parameters)` that resolves **registry-only** (no built-ins), activates/initializes the pinned
type, and calls `TryInvoke`.

Resolution order for algorithms: **registry first, then built-ins** — but registration rejects a name
that collides with a built-in of the same category (§8.6), so shadowing never silently happens; the
ordering is only an unreachable tie-break.

This is the one genuinely invasive engine edit; it is contained (one helper + one function entry
point, routed call sites, the enumeration union) and every touched path keeps its existing behaviour
when the registry is empty.

### 4.5 Security: exact gate interaction

The `EnableDynamicCodeExecution` flag referenced in older feature docs **no longer exists in code**
(confirmed: zero `.cs` references; the stored-query/`/path`/`/subgraph` code surface is now gated by
authentication + rate-limit only). The surviving analogous switch is `EnableDynamicPluginLoading`
(`Fallen8:Security`, default false), today gating `PUT /plugin`.

> **Update (2026-07-25, post-review):** the gate default was flipped to **ON**
> (`EnableDynamicPluginLoading = true`) — consistent with the always-on dynamic-code model — and made
> **per-namespace overridable**: `PATCH /ns/{name}` accepts `pluginRegistration`
> (`enabled`/`disabled`/`inherit`), persisted on the namespace catalog (the default namespace's
> override rides on the catalog document); the authorization handler resolves the addressed
> namespace's override ahead of the global default. The "on ⇒ 201 / off ⇒ 403" matrix below still
> holds per the effective (namespace-resolved) value.

**Repurpose `EnableDynamicPluginLoading`** as the registration gate for the new typed endpoints (its
`DynamicPluginPolicy` + `DynamicCapabilityAuthorization` handler reused as-is — same default-off
"provisioning window" semantics, minus the DLL). Registration (`POST /plugins/algorithm`,
`POST /plugins/function`) keeps the declarative capability policy + sensitive rate limit + body cap.
Everything else — algorithm invocation via `/path` `/analytics` `/subgraph`, `function/{name}/invoke`,
list, get, delete — requires only authentication.

| Request | switch **on** | switch **off** |
|---|---|---|
| Register a plugin (`POST /plugins/algorithm` \| `/function`) | 201 | **403** |
| Invoke a registered algorithm (`/path`, `/analytics`, `/subgraph`) | 2xx | 2xx |
| Invoke a registered function (`/plugins/function/{name}/invoke`) | 200 | 200 |
| List / get / delete plugins | 2xx | 2xx |

Stated honestly in the controller docs: recompilation at Load/WAL-replay is **not** gated by the
switch (it gates the REST *introduction* surface, not rehydration of operator-approved state), and an
invoked plugin runs in-process with full trust.

### 4.6 Persistence (subgraph-recipe / stored-query pattern, reused)

- **Snapshot:** `PersistencyFactory` writes a `PluginManifest` JSON sidecar (new
  `Constants.PluginManifestString` suffix), source-gen via `CoreJsonContext`, atomic write, same side
  of the commit point as the recipe/stored-query manifests. Per entry: name, category, contract,
  sourceCode, description, createdAt — never compiled bytes. Empty registry deletes any stale
  manifest.
- **Load:** definitions rehydrate; with an `IPluginCompiler` registered (apiApp registers one at
  startup where it registers the stored-query/recipe compilers) each entry recompiles eagerly. A
  recompile failure keeps the entry `Failed` + diagnostics (visible in list/get; the name then
  resolves to nothing / falls through to built-in) — never a silent drop. No compiler (embedded
  engine) → `SourceOnly`.
- **WAL:** two additive `WalEntryType`s after the current max (`SetEmbeddings = 16` →
  `RegisterPlugin = 17`, `RemovePlugin = 18`; values never renumbered, format version unchanged).
  Payloads: the `PluginDefinition` JSON / the name. Replay decodes + re-executes the equivalent
  transaction (`BypassQuota` on register), skip-and-continue on a failed replay like the subgraph/
  stored-query derived entries.
- Save/Load and WAL agree by construction (the `wal-subgraph-support` symmetry contract).

## 5. MCP surface (engine → REST → MCP)

`McpRestCoverageTest` currently auto-defers anything whose path contains `/service` **or**
`/plugin`. New routes contain `/plugins`, so the substring match would silently auto-defer them —
**bridge deliberately instead**:

- **`f8_plugins` tool** (or fold into `f8_admin`):
  - Read tier: **list**, **get**, and **function-invoke** (a registered read-only function is a
    genuine agent read capability — an agent can call a stored graph function by name).
  - Write/Admin tier: **delete**.
  - `code` capability (`Mcp:Tools:EnableCode`, the existing schema-widening toggle for in-process-code
    exposure): **register** (algorithm + function).
- Update `McpBridgedEndpoints.All` with the bridged `/plugins/*` routes (including
  `POST /plugins/function/{name}/invoke`); **narrow the line-74 deferral** to `/service` only (drop
  the `/plugin` clause — `PUT /plugin` is removed) so the bridged set and the deferrals stay disjoint
  (`NoBridgedEndpoint_MatchesADeferral`).
- Update `McpContractTest` (bridged routes must exist in the regenerated snapshot).

## 6. F8 Studio (`fallen-8-web-ui`)

Extend the existing delegate editor (`src/delegate/`, Monaco C#, `/delegates/validate`
compile-check, NL-assist) into a **plugin authoring** experience beside `StoredQueriesPanel` on the
Dashboard:

- Pick a category (+ contract for algorithm) → load the scaffold into a **full-file** Monaco C#
  editor (not a single-fragment slot).
- Compile-validate via a **plugin-aware** validate endpoint — a whole-type contract check, not a
  fragment check: add `POST /plugins/{category}/validate` (compile + contract-validate, no
  registration, diagnostics back), the same side-effect-free compile Studio already relies on.
- Register to the **chosen namespace** (Studio threads the addressed namespace; plugin routes are
  namespace-scoped, unlike the `[Fallen8Level]` `/delegates/validate`).
- List / inspect (read-only source) / delete registered plugins, mirroring `StoredQueriesPanel`; and
  a **function runner** (parameter form → `POST …/invoke` → render the returned vertices/edges).
- **NL-assist is in v1** (per review): extend the existing NL panel to draft whole plugin types.
  New prompt scaffolding per category/contract (the contract interface, the reachable member surface,
  and few-shot whole-type examples) and the refine loop feeds the plugin-aware validate diagnostics
  back — the same generate→validate→refine loop the fragment editor uses. The model call stays
  browser→model (Fallen-8 never in that path). A new whole-type training corpus is required (§9).

## 7. Test bar (MSTest, `fallen-8-unittest`)

Arrange/act/assert, `TestLoggerFactory.Create()`, behaviour-pinning over happy path:

- Registration validation: name regex, unknown contract discriminator, duplicate name, quota,
  built-in-name collision, `PluginName ≠ name`.
- Contract validation: zero/multiple implementors, missing parameterless ctor, throws-on-activate,
  oversize source → 400 with the right diagnostics; a valid full-type algorithm and a valid full-type
  function each register.
- **Transparent algorithm equivalence:** a registered trivial algorithm produces the same result as
  the equivalent built-in through `/path` `/analytics` `/subgraph`.
- **Function invocation:** register a function, invoke by name with parameters, assert the projected
  vertices/edges; full-scan and index-query variants; unknown/`Failed` → 404; read-only (a function
  that tries to mutate cannot, by contract — it holds read access only in v1).
- The §4.5 gate matrix in both switch states.
- Durability: Save → fresh engine → Load reappears + recompiles + resolves/invokes; WAL
  register+remove+register replay identical; failed recompile on load/replay keeps `Failed` +
  continues.
- Concurrency: invoke-during-remove never torn; ALC unload after delete (weak-reference).
- Namespace scoping: a plugin registered in ns A is invisible/unresolvable/uninvokable in ns B.
- Removal-of-DLL-path regression: `PUT /plugin` gone (404), `Assimilate` gone; built-in discovery
  still finds BLS/DIJKSTRA/analytics/subgraph (the `EnginePerformanceTest` name-map assertions stay
  green).
- Coverage/contract: `McpRestCoverageTest` + `McpContractTest` pass with the new bridged routes;
  OpenAPI snapshot regenerated.

## 8. Decisions (settled 2026-07-25)

1. **Authoring unit per category.** **Full type** for every category (contracts carry real logic; a
   DLL was a full type). Per-category/-contract scaffold + one-implementor-of-the-contract
   validation. → §4.1.
2. **v1 categories.** **`algorithm` (path/subgraph/analytics) + `function`.** The graph-function
   category (§3b) was added on review as the priority alongside algorithms.
3. **Index category.** **Dropped permanently** (not deferred). Rationale (reviewer): a user defining
   their own indexing *structure* is "much too hardcore" — not a real user need. Built-in indices are
   unaffected; only user-authored index plugins are off the table. This also removes the live-state /
   `IFallen8Serializable` round-trip risk that index authoring would have carried.
4. **Service plugins.** **Deferred to a separate track.** `IService` is long-lived, binds resources,
   and has a start/stop/restart lifecycle + `IFallen8Serializable` — a register-source-and-pin model
   does not fit. (Write-capable graph functions are a related later track — §3b.)
5. **Namespace visibility.** **Strict per-namespace**, mirroring `StoredQueryLibrary` (one `Fallen8`
   per namespace). No shared/global runtime registration; `default` gets no special scope. Built-ins
   stay global (they are code). Register in each namespace that needs it (source is portable via
   `GET /plugins/{name}`).
6. **DLL path retirement.** **Removed outright** (`PUT /plugin`, `Assimilate`, upload directory,
   `AddPluginSearchDirectory`), not a 410 tombstone: the endpoint is default-off, is the single most
   dangerous surface, and a tombstone leaves a dead route in the OpenAPI/MCP contract for little
   value. Migration note in `docs/` + a `nl-assist-finetune/RETRAIN-LOG.md` entry.
7. **Route shape + persisted representation.** One typed route per top-level category
   (`POST /plugins/algorithm` with a closed contract enum; `POST /plugins/function`); function invoke
   at `POST /plugins/function/{name}/invoke`; uniform `GET/GET{name}/DELETE`. Persisted as
   `PluginDefinition` in a versioned `PluginManifest` sidecar + WAL types 17/18. **Built-in name
   collision:** reject registration of a name matching a built-in plugin of the same category (409),
   so resolution-order shadowing never surprises anyone.
8. **Exportable with the graph?** Plugins are **namespace checkpoint state** — they travel with
   Save/Load + WAL (like stored queries and subgraph recipes), **not** with the bulk element export.
   `GET /plugins/{name}` returns source for manual cross-instance migration.
9. **MCP.** **Bridge** list/get/function-invoke (read), delete (write/admin), register (code
   capability). → §5.
10. **NL-assist.** **In v1** (whole-type drafting; new training corpus + RETRAIN-LOG entry). → §6, §9.

## 9. Impact on existing features (mandatory cross-feature sweep)

- **`api-security-boundary` (done).** Owns `PUT /plugin`, `Fallen8SecurityOptions`,
  `DynamicPluginPolicy`, `DynamicCapabilityAuthorization`, the plugin directory. This feature
  **removes** the DLL endpoint and repurposes the flag. **Action:** update its living README/spec —
  "plugin DLL loading" is gone; `EnableDynamicPluginLoading` now gates *source* registration. A
  behaviour change to a done feature: surfaced, not silently edited; its honesty notes (trust
  boundary, not sandbox) carry over verbatim.
- **`stored-query-library` (done).** The direct template. No behaviour change; the two share a
  registry idiom, a compile-bridge idiom, and manifest/WAL discipline. Reuse and cross-link, do not
  re-narrate (the "one home per explanation" gate).
- **`PluginFactory` / `PluginCache` / built-in discovery.** Unchanged for built-ins; the resolution
  seam gains a registry-first check (§4.4). `EnginePerformanceTest` memoization/name-map + BLS/DIJKSTRA
  resolution tests stay green (base-directory discovery untouched).
- **`mcp-server` (done) + `McpRestCoverageTest` / `McpContractTest`.** New bridged routes (incl.
  function invoke) + narrowed deferral + snapshot regen (§5). Enforced gate — must move together.
- **OpenAPI snapshot** (`features/done/web-ui/openapi-v0.1.json`). `PUT /plugin` removed; `/plugins/*`
  added. Regenerate via `scripts/update-openapi-snapshot.ps1`; the removal is a *deliberate* deletion
  (call it out in the reviewed diff).
- **F8 Studio (`fallen-8-web-ui`).** Greenfield plugin-authoring UI + function runner + NL-assist for
  whole types (§6) + the plugin-aware validate endpoint. No existing screen regresses.
- **NL-assist fine-tune dataset.** Plugin authoring is whole-type-shaped, a different generation
  target from the fragment dataset. NL-assist is in v1, so a whole-type example corpus is needed →
  append a `nl-assist-finetune/RETRAIN-LOG.md` entry (not a per-feature retrain); ship the Studio NL
  panel against a general model until the fine-tune drains it.
- **`save-games` / `bulk-import-export`.** Save-games gain the `PluginManifest` sidecar. Bulk export
  unchanged (§8.8). `SaveGameRegistry`'s "available plugins" lists union registered plugins (§4.4).
- **`graph-analytics` / `subgraph` / path.** Their resolution now consults the registry first; the
  `algorithm`/`pathAlgorithmName` fields accept a registered name transparently. Docs note it.
- **`multi-instance-host` (open).** Its "shared plugin directory" sharp edge dissolves — the DLL path
  and shared upload directory are gone; registered plugins are per-namespace registry state. Note the
  interaction in that spec.

## 10. Risks

- **The resolution seam + the new function entry point (§4.4)** are the invasive engine edits.
  Mitigation: contained (one helper + one entry point + routed call sites), empty-registry behaviour
  provably identical to today, pinned by built-in resolution tests plus registry-shadowing /
  namespace-isolation / function-invoke tests.
- **Full-type compilation is a larger surface than a fragment** (arbitrary type/members vs a wrapped
  body). Honesty: it is exactly as dangerous as the DLL it replaces — full trust, in-process — but now
  *visible, contract-validated, gated, logged, and per-namespace*. Compile bounds (source length)
  apply. Not a sandbox.
- **Graph-function read semantics:** the result references live elements; concurrent removals reflect
  the read snapshot. Documented, and read-only in v1 keeps it a pure read-path.
- **Pinned artifacts are process memory** (bounded by the per-namespace quota; delete unloads the ALC).
- **"Typed endpoints + gate" read as safety.** Every doc touchpoint repeats the trust-boundary note.

## 11. Keep (do not regress)

- Built-in plugin discovery via `PluginFactory` (base-directory scan) and its P5 memoization/name-map;
  `PluginCache` algorithm caching.
- Single-writer mutation / lock-free reads; algorithm invocation and function invocation stay reads.
- `collectible-codegen-assemblies`: every compiled artifact in a collectible ALC; registry pinning must
  not block unload after delete.
- `wal-subgraph-support` symmetry: additive WAL entry types only; Save/Load and WAL always agree.
- The `api-security-boundary` honesty posture (trust boundary, not sandbox) — reproduced, never softened.
- The engine never references Roslyn; compilation stays in the apiApp behind `IPluginCompiler`.
