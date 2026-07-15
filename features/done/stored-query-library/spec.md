# Stored Query Library — Specification

> **Status:** Implemented and merged (branch `feature/stored-query-library`, council-approved
> 2026-07-15; see [plan.md](./plan.md) for the phase record and council outcome).
>
> **Provenance:** [skill-library](../skill-library/spec.md) explicitly carved this out — its
> header note reads *"a server-side library of stored, reusable delegate/traversal definitions
> inside the engine (i.e. stored procedures) — is explicitly **not** this feature; it would be
> a separate engine feature if wanted."* This is that feature.

## 1. Overview & motivation

The dynamic query surface (`POST /path/{from}/to/{to}`, `PUT /subgraph`) accepts C# filter/cost
fragments per request, compiles them with Roslyn (`CodeGenerationHelper`), and executes the
delegates in-process. Every request re-submits its source; the compiled artifact lives only in a
sliding-expiration cache (`GeneratedCodeCache`, the subgraph provider cache); and — decisive for
security posture — accepting a request means accepting *arbitrary* code, which is why the whole
surface sits behind the `api-security-boundary` kill switch
(`Fallen8:Security:EnableDynamicCodeExecution`, default **off**).

This feature adds a server-side library of **named, validated, pre-compiled query definitions**:
a client registers a definition once (`{ name, kind, source }`), the server compiles it through
the existing `CodeGenerationHelper` path with the existing `dynamic-code-resource-limits` bounds
(compile errors → 400 with diagnostics), and thereafter the existing path/subgraph endpoints
accept a **reference by name** wherever they accept inline fragments today.

Two motivations, in order:

1. **Security synergy (headline).** With the dynamic-code kill switch **off**, inline fragments
   stay rejected — but invoking *already-stored* queries works. The operator registers a vetted
   set while the switch is on (provisioning / maintenance window), then runs day-to-day with the
   switch off: the code surface shrinks from "arbitrary C# per request" to "a closed,
   operator-approved set". Registration itself always counts as dynamic-code surface and
   requires the switch. Honesty note as everywhere in this repo: a stored query still runs
   in-process with full trust — this narrows *who can introduce* code, it is not a sandbox.
2. **Ergonomics & cost.** Clients (F8 Studio, the MCP server, scripts) stop shipping and
   re-shipping fragment strings; a stored query is compiled once at registration and stays warm
   for its registered lifetime instead of riding a 60-second sliding cache.

## 2. Goals / non-goals

**Goals**

- A new engine-side **`StoredQueryLibrary`** (`fallen-8-core/StoredQueries/`) holding
  definitions (name, kind, source JSON, description, created-at) — source + metadata only, the
  same split as subgraph recipes (the engine never compiles; the API layer does).
- **Two v1 kinds**, matching exactly the two artifacts the engine actually compiles and
  executes (see §3.1 for why not per-fragment kinds):
  - `Path` — a `{ filter, cost }` set (the `PathSpecification` fragment blocks), compiled via
    `CodeGenerationHelper.GeneratePathTraverser` into one `IPathTraverser`.
  - `SubGraph` — a subgraph pattern template (the `SubGraphSpecification` filter/pattern
    fields, minus the instance name), compiled via `TryGenerateSubGraphDefinition` into a
    `SubGraphDefinition`.
- A versioned **`StoredQueriesController`** (`POST/GET/DELETE /storedquery[…]`), annotated per
  repo conventions (`[ProducesResponseType]`, `[Consumes]`/`[Produces]`, XML docs).
- **Invocation by reference:** `PathSpecification` and `SubGraphSpecification` each gain an
  optional `storedQuery` field, mutually exclusive with their inline fragment fields.
- **Exact kill-switch contract** (§3.3): registration and inline fragments require
  `EnableDynamicCodeExecution=true`; invocation by name, listing, and deletion do not.
- **Durability following the subgraph-recipe pattern** (`wal-subgraph-support`): definitions
  survive Save/Load via a JSON manifest sidecar (persist source + metadata, recompile on load
  through a registered compiler interface); registration/removal are WAL-logged with two new
  additive `WalEntryType` values and replay in commit order.
- Registration/removal run through the **single-writer transaction pipeline**
  (`RegisterStoredQueryTransaction` / `RemoveStoredQueryTransaction`); invocation is pure
  read-path.
- Compiled artifacts live in **collectible `AssemblyLoadContext`s** exactly as today
  (`collectible-codegen-assemblies`): a stored query pins its artifact for its registered
  lifetime; deletion drops the reference so the context can unload once in-flight invocations
  finish.
- MSTest coverage per the bar in §5.

**Non-goals** (each with its revisit trigger — single-process, single-operator app; no
enterprise machinery)

- **Per-fragment kinds** (`VertexFilter`, `EdgeCost`, …) and stored predicates on the scan
  endpoints. No endpoint today accepts a lone fragment (the scan endpoints take
  operator/literal specifications, not code), so a stored bare fragment would have no executor.
  Revisit when an endpoint accepts a single fragment or a concrete client needs to mix stored
  and inline fragments in one request.
- **A direct execute endpoint** (`POST /storedquery/{name}/execute`). A stored query is not a
  self-contained runnable: a `Path` query needs `from`/`to`/bounds, a `SubGraph` template needs
  an instance name — the existing endpoints already carry exactly those parameters. Revisit
  when a kind lands whose invocation the existing endpoints cannot express.
- **In-place update / versioning.** v1 entries are immutable: delete + re-register. Revisit
  when the delete-and-re-register workflow demonstrably hurts (e.g. F8 Studio grows a stored
  query editor).
- **Parameterized fragments** (placeholders substituted at invocation). The existing numeric
  bounds (`maxDepth`, `maxResults`, `maxPathWeight`, `from`/`to`) already parameterize the
  common cases. Revisit when a real client needs a per-invocation *value inside a fragment*.
- **Per-query authorization.** F8 auth is one all-or-nothing API key
  (`api-security-boundary`); every caller may invoke every stored query. Revisit if Fallen-8
  grows multi-credential auth (also the mcp-server revisit condition).
- **Sandboxing stored queries** — never in this design; the `api-security-boundary` honesty
  note applies unchanged. Registration is the trust decision.
- **Library export/import independent of checkpoints.** `GET /storedquery/{name}` returns the
  full source, which covers manual migration. Revisit when someone actually moves queries
  between instances routinely.
- **TLS, reverse proxies, secret stores** — the deployment's job, never in-app (project
  decision).

## 3. Design

### 3.1 Model & kinds

```
fallen-8-core/StoredQueries/
  StoredQueryDefinition.cs      name, kind, specificationJson, description, createdAt
  StoredQueryKind.cs            Path | SubGraph
  StoredQueryLibrary.cs         registry: register/remove (writer thread), snapshot reads
  IStoredQueryCompiler.cs       engine→API compile bridge (mirrors ISubGraphRecipeCompiler)
  StoredQueryManifest.cs        the persisted JSON document (schemaVersion + entries)
fallen-8-core-apiApp/
  Controllers/StoredQueriesController.cs
  Controllers/Model/StoredQuerySpecification.cs / StoredQuerySummaryREST.cs / …
  Helper/StoredQueryCompiler.cs implements IStoredQueryCompiler over CodeGenerationHelper
```

Why exactly two kinds: `GeneratePathTraverser` compiles a *whole* filter+cost set into one
provider class, and `TryGenerateSubGraphDefinition` compiles *all* pattern slots into one
provider — the six `Delegates.*` fragment kinds (`DelegateValidationHelper`'s list) are never
compiled or executed in isolation. Storing what the engine actually runs keeps registration
validation identical to invocation reality.

Names: `^[A-Za-z0-9_-]{1,128}$`, compared ordinally (case-sensitive), so a name is always a
safe URL path segment. A library-wide quota `MaxStoredQueryCount` (default 256, configurable
via `Fallen8:StoredQueries:MaxCount`) rejects registration beyond the cap with 409 — the
`subgraph-quotas` pattern.

Each in-memory entry additionally carries a **compile state**: `Compiled` (artifact pinned),
or `Failed` (recompile-on-load failed; diagnostics stored). The compiled artifact is an
`IPathTraverser` or a `SubGraphDefinition`, strongly referenced by the entry for its registered
lifetime — deliberately *not* the 60-second sliding expiry of the inline caches, because a
stored query is long-lived by definition.

### 3.2 REST contract

All routes absolute (`[HttpPost("/storedquery")]` etc.) on a controller with the standard
`api/v{version:apiVersion}` route + `[ApiVersion("0.1")]`, matching `SubGraphController`.

| Endpoint | Gate | Behaviour |
|---|---|---|
| `POST /storedquery` | `DynamicCodePolicy` + sensitive rate limit + 1 MiB body cap | Register: validate name/kind/shape → **compile** (existing limits) → enqueue `RegisterStoredQueryTransaction`, `WaitUntilFinished()`. 201 with summary; 400 malformed/compile failure (Roslyn diagnostics in the body, same message shape as the `/path` 400); 401/403 per policy; 409 duplicate name or quota; 429 rate limit; 500 internal rollback. |
| `GET /storedquery` | authenticated | List summaries: name, kind, description, createdAt, compileState. 200. |
| `GET /storedquery/{name}` | authenticated | Full definition **including source** and (if Failed) recompile diagnostics. 200 / 404. |
| `DELETE /storedquery/{name}` | authenticated | Enqueue `RemoveStoredQueryTransaction`. 204 / 404 / 500 on rollback — the `DeleteSubGraph` failure-reason mapping. **Not** gated by the kill switch: removal compiles nothing and must stay possible when the switch is off. |

Registration body:

```
POST /storedquery
{
  "name": "adults-shortest",
  "kind": "Path",
  "description": "age>30 vertices, weight-by-distance",
  "path": {
    "filter":   { "vertexFilter": "return (v) => v.TryGetProperty(out int age, \"age\") && age > 30;" },
    "cost":     { "edgeCost": "return (e) => 1.0;" }
  }
}
```

Exactly one of `path` / `subGraph` must be present and must match `kind` (400 otherwise). For
`kind: "SubGraph"` the block is a `SubGraphSpecification` without the instance `name`
(`vertexFilter`, `edgeFilter`, `patterns` — the `additionalInformation` field is per instance
and not stored).

**Invocation.**

- `POST /path/{from}/to/{to}` — `PathSpecification` gains `"storedQuery": "<name>"`.
  Mutually exclusive with `filter`/`cost` (400 if both). Resolution: unknown name → 404 (the
  message names the stored query, disambiguating from the vertex 404s); kind mismatch → 400;
  compile state `Failed` → 409 with the stored diagnostics. The resolved `IPathTraverser` feeds
  `ShortestPathDefinition` exactly as a cache hit does today; the numeric bounds and
  `pathAlgorithmName` stay per-request.
- `PUT /subgraph` — `SubGraphSpecification` gains `"storedQuery": "<name>"`, mutually
  exclusive with `vertexFilter`/`edgeFilter`/`patterns`; the instance `name` stays required.
  The stored template's `SubGraphDefinition` is instantiated under the instance name. The
  created subgraph's persistable recipe (`SpecificationJson`) is the **materialized full
  specification** (template fields + instance name), so subgraph durability is untouched and —
  deliberately — a created subgraph never depends on the stored query's continued existence:
  deleting the stored query later does not orphan the subgraph's recipe.

### 3.3 Security: exact kill-switch interaction

Today `POST /path` and `PUT /subgraph` carry an endpoint-level
`[Authorize(Policy = Fallen8SecurityOptions.DynamicCodePolicy)]`, so the switch gates the whole
endpoint regardless of request shape. That is too coarse once a request can be code-free. The
gate becomes **request-shape-aware**:

- Both endpoints drop the endpoint-level capability policy; authentication is unchanged
  because the global fallback policy (api-security-boundary) governs attribute-less actions
  exactly as it governs every other endpoint — when a key is configured, an anonymous request
  is 401 before the action runs; without a key the endpoints stay open, as before. (As built,
  no `[Authorize]` attribute remains on the endpoints: adding a bare one would have *changed*
  the no-key posture, since the default policy demands an authenticated user even when the
  fallback is absent.) Inside the action, iff the request carries **any inline fragment** (a
  non-blank filter/cost string, or any subgraph filter/pattern fragment), the capability is
  checked imperatively via `IAuthorizationService.AuthorizeAsync` against the same
  `DynamicCapabilityRequirement` — failure returns the same 403 the policy produces today.
- A request that references only a `storedQuery` — or carries no fragments at all — passes
  without the capability. (Deliberate contract fix: a filterless path search stops requiring
  the dynamic-code switch; it compiles no *user-supplied* code — the first such request still
  compiles the server-generated default traverser, zero user input, cached thereafter. This
  makes the mcp-server spec's read-tier `f8_find_paths` assumption true.)
- `POST /storedquery` (registration) keeps the **declarative** endpoint-level policy, the
  sensitive rate-limit policy, and the 1 MiB body cap — it is dynamic-code surface exactly like
  `POST /delegates/validate`.
- `GET`/`DELETE /storedquery*` are not code surface; authenticated like other endpoints.

Resulting matrix (authenticated caller assumed):

| Request | switch **on** | switch **off** |
|---|---|---|
| Register stored query | 201 | **403** |
| `/path` / `/subgraph` with inline fragments | 200/201 | **403** |
| `/path` / `/subgraph` via `storedQuery` | 200/201 | 200/201 |
| `/path` with no filter/cost at all | 200 | 200 |
| List / get / delete stored queries | 2xx | 2xx |

Stated honestly in the controller docs (repo voice): **recompilation at load/WAL-replay is not
gated by the switch** — the switch gates the REST *introduction* surface, not the engine
rehydrating definitions the operator already approved. And an invoked stored query runs
in-process with full trust; the library is a provenance control, not a sandbox.

### 3.4 Engine library, transactions, concurrency

- `RegisterStoredQueryTransaction { Definition }` / `RemoveStoredQueryTransaction { Name }`
  run on the single writer thread like every mutation. The transaction re-checks the invariants
  the controller pre-checked (duplicate name → `TransactionFailureReason.Conflict`, quota →
  `QuotaExceeded`, missing name on remove → `NotFound`) so TOCTOU races resolve on the writer
  thread, mirroring `CreateSubGraphTransaction`/`RemoveSubGraphTransaction`.
- The controller compiles **before** enqueueing (validation must fail fast with diagnostics and
  never occupy the writer thread with Roslyn); the transaction receives the definition *and*
  the already-compiled artifact to publish atomically.
- Reads (`storedQuery` resolution during invocation, list/get) take a lock-free snapshot of the
  library (immutable dictionary swapped by the writer), consistent with the engine's
  single-writer / lock-free-reader model.
- **Invoke during remove:** an invocation captures the entry's compiled-artifact reference once
  at resolution; the delegates keep the collectible `AssemblyLoadContext` alive until the
  invocation returns (standard `collectible-codegen-assemblies` semantics). A concurrent
  removal therefore either wins before resolution (→ 404) or the invocation completes against
  the old artifact — never a torn state. Pinned by test.

### 3.5 Persistence (the subgraph-recipe pattern, reused)

- **Snapshot:** `PersistencyFactory` writes a `StoredQueryManifest` JSON sidecar next to the
  subgraph-recipe manifest (same `CoreJsonContext` source-gen serialization, same
  atomic-write discipline, same side of the commit point as the recipe manifest per
  `crash-durability-hardening`). Persisted per entry: name, kind, `specificationJson`,
  description, createdAt — never compiled bytes.
- **Load:** definitions rehydrate into the library; if an `IStoredQueryCompiler` is registered
  (the apiApp registers one at startup, exactly where it registers the
  `SubGraphRecipeCompiler`), each entry recompiles eagerly. A recompile failure does **not**
  drop the entry: it is kept with `compileState: Failed` + diagnostics (visible in list/get,
  invocation → 409) so an engine-upgrade-induced source break is loud and recoverable
  (delete + re-register), never a silent disappearance. With no compiler registered (embedded
  engine use), entries load as source-only — there is no invocation surface without the API
  layer anyway.
- **WAL:** two additive entry types, `RegisterStoredQuery = 14` and `RemoveStoredQuery = 15`
  (values appended, never renumbered; format version stays 1). Payloads: the definition as
  `CoreJsonContext` JSON / the name. Unlike subgraphs there is **no unloggable case** — a
  stored query *is* its serializable source. Replay decodes and re-executes the equivalent
  transaction in commit order (register-then-remove replays to absent); recompile-on-replay
  failures follow the load behaviour above (keep + mark Failed + warn), recovery continues.
- Save/Load and WAL agree by construction: a stored query survives a crash+replay iff it
  survives Save+Load — the `wal-subgraph-support` symmetry contract.

### 3.6 Interaction with the existing caches

- The inline-path `GeneratedCodeCache` (keyed on `(Filter, Cost)` per `codegen-cache-keying`)
  and the content-keyed subgraph provider cache are **untouched**: inline requests behave
  byte-identically. A stored-query invocation never consults them — it uses the entry's pinned
  artifact.
- Registration MAY reuse the content-keyed subgraph provider cache on compile (same source ⇒
  same provider), but the stored entry then holds its own strong reference so cache expiry
  cannot evict a registered query's artifact.
- The `dynamic-code-resource-limits` compile bounds (`MaxFilterFragmentLength`,
  `MaxGeneratedSourceLength`, checked before Roslyn) apply to registration unchanged; the
  deferred R1 execution budget remains deferred and is neither required nor worked around here.

## 4. Acceptance criteria

- **Round-trip.** Register a `Path` and a `SubGraph` query; invoke each by name through the
  existing endpoints; results are element-for-element identical to the same specification sent
  inline. List/get/delete behave per §3.2 (including 404/409 paths).
- **Compile failure.** A fragment with a syntax/type error registers nothing and returns 400
  carrying the Roslyn diagnostics; an oversize fragment is rejected before Roslyn runs.
- **Kill switch.** The full §3.3 matrix holds, pinned by tests: with
  `EnableDynamicCodeExecution=false`, registration and inline fragments are 403 while stored
  invocation, filterless paths, list, get, and delete all succeed.
- **Durability.** Save → fresh engine → Load: stored queries reappear, recompiled and
  invocable. WAL enabled, register + remove + register logged, simulated crash → replay ends
  with the identical library (names, kinds, compile states). A recompile failure on load/replay
  keeps the entry as `Failed` (409 on invoke, diagnostics on get) and recovery continues.
- **Decoupled subgraphs.** A subgraph instantiated from a stored template survives
  Save/Load/WAL-replay after the stored query is deleted (its recipe is self-contained).
- **Concurrency.** Concurrent invoke-during-remove never crashes or returns a torn result:
  each invocation either completes against the captured artifact or observes 404.
- **Quota.** Registration beyond `MaxStoredQueryCount` → 409; the count recovers after delete.
- **No regressions.** Inline path/subgraph behaviour, their caches, and the full existing
  suite are unchanged and green; build clean.

## 5. Test bar (MSTest, `fallen-8-unittest`)

Arrange/act/assert with `TestLoggerFactory.Create()`; behaviour-pinning over happy path:
registration validation (name regex, kind/block mismatch, duplicate, quota), compile-failure
diagnostics shape, the §3.3 kill-switch matrix (both switch states across all five request
shapes), invocation resolution (unknown name, kind mismatch, Failed state), stored-vs-inline
result equivalence, save/load round-trip, WAL replay incl. register-then-remove and
failed-recompile, the invoke-during-remove race, ALC unload after delete (weak-reference test,
per `collectible-codegen-assemblies`), and manifest corruption handled per the persistence
posture (loud, not silent).

## 6. Risks

- **Request-shape-aware authorization** replaces a declarative endpoint policy with an
  imperative check on two endpoints — the one genuinely security-sensitive edit. Mitigation:
  the check reuses the existing `DynamicCapabilityRequirement` handler via
  `IAuthorizationService` (no second source of truth), and the §3.3 matrix is pinned by tests
  in both switch states before anything else lands on top.
- **Recompile drift:** source that compiled at registration can fail after an engine upgrade
  (model API change). Handled by design (§3.5: keep + mark Failed + loud), never silent loss.
- **Pinned artifacts are process memory:** up to `MaxStoredQueryCount` collectible contexts
  stay loaded for the library's lifetime. Bounded by the quota; delete unloads.
- **False sense of safety:** "kill switch off + stored queries" could be read as sandboxing.
  Every doc touchpoint (controller remarks, README) repeats the trust-boundary honesty note.
- **Two mutually-exclusive request shapes** on `/path` and `/subgraph` complicate their
  contracts slightly; mitigated by hard 400s on mixing and OpenAPI examples for both shapes.

## 7. Keep (do not regress)

- The `api-security-boundary` posture: default-off kill switch, 401-then-403 ordering,
  sensitive rate limiting and body caps on everything that compiles.
- `dynamic-code-resource-limits` compile bounds run before Roslyn on every compile path,
  including registration.
- `codegen-cache-keying` `(Filter, Cost)` keying and the content-keyed subgraph provider cache
  for inline requests; `PathCompileCount` test semantics.
- `collectible-codegen-assemblies`: every generated assembly stays in a collectible context;
  stored-query pinning must not leak references that prevent unload after delete.
- `wal-subgraph-support` symmetry: WAL format version 1, additive entry types only, snapshot
  and WAL always agree on what is durable.
- Single-writer mutation / lock-free reads; invocation stays a read.
