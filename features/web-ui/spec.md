# Web UI ("F8 Studio") — Specification

> **Status:** Draft, ready for design hand-off. This spec, together with the generated
> API contract [openapi-v0.1.json](./openapi-v0.1.json), is the complete input for the
> agent designing and implementing the UI. The implementing agent's first deliverables
> are a UX design document (`design.md`) and the phased [plan.md](./plan.md) — neither
> exists yet. Follow the feature workflow in the repository root `CLAUDE.md`.

## 1. Overview

F8 Studio is a browser-based frontend for one or more running Fallen-8 instances. It
lets a user:

1. **Browse** the instance — status, counts, plugins, indices, and the individual
   vertices/edges with their typed properties and adjacency.
2. **Query** it — property/index/range/fulltext/spatial scans, shortest paths
   (BLS/Dijkstra), and pattern-matched subgraphs.
3. **Visualize** results on an interactive graph canvas with expand-on-demand.
4. **Author the C# delegate fragments** that path (BLS/Dijkstra) and subgraph queries
   accept, in an embedded Monaco editor with IntelliSense (completions, hovers,
   signature help), compile diagnostics, and a natural-language assist that drafts a
   fragment from a plain-language description (FR-26).
5. **Work across multiple instances** — register several Fallen-8 endpoints (local,
   staging, production) and move between them without losing per-instance context.

The delegate editor is the signature feature: Fallen-8's query filters are not a query
language but compiled C# lambdas, and today only users who read the engine source can
write them. The UI must make that authoring experience first-class. It is not a
destination of its own, though — the editor exists for the path finder (BLS/Dijkstra)
and the subgraph studio, and comes into play when a search is too complex for the
plain form inputs. Every path and subgraph query must also work with all fragment
slots left empty (empty = match everything, §6.1); writing C# is the escalation, not
the entry point.

## 2. Goals and non-goals

### Goals
- A single-page application covering the full REST surface described in
  [openapi-v0.1.json](./openapi-v0.1.json) (44 paths, 49 operations).
- Read/query/visualize as the primary loop; graph mutation (create vertex/edge, add or
  remove properties, delete elements) as a secondary, fully supported capability.
- One reusable delegate-editor component used everywhere a code fragment is accepted.
- Instance administration: save, load, trim, tabula rasa, demo-data generation — with
  confirmation UX proportional to destructiveness.
- The UI ships in this repository (new top-level folder `fallen-8-web-ui/`) and is
  developed against a locally running `fallen-8-core-apiApp`.

### Non-goals
- A graph query language (Cypher/Gremlin) or visual query builder beyond the linear
  subgraph pattern sequence the API already models.
- Authentication/authorization. The API has none; the UI is a developer/operator tool
  for trusted networks. The design should note where auth would slot in later.
- Multi-user collaboration, saved server-side workspaces, or change streaming (the API
  has no notification channel; see gap G-5).
- Cross-instance operations: no federated queries, no data copy/diff between instances.
  Multi-instance support (FR-1a–FR-1c) is about *connecting and switching*, not about
  combining data from two instances in one query or canvas.
- Mobile layouts. Desktop-first; responsive down to ~1280 px is sufficient.

## 3. Users and UX surface

Two personas: the **developer** integrating Fallen-8 into an application (needs to
inspect data, prototype filters/paths, copy working fragments into code) and the
**operator** running an instance (needs status, save/load, and occasional data surgery).

Screen inventory the design must cover (naming is indicative, not binding):

| Screen | Purpose |
|---|---|
| Connect / Instances | Register, edit, and remove named instances; health overview across all of them; pick the active instance |
| Dashboard | Counts, memory, available index/path/service plugins; admin actions |
| Element browser | List/inspect vertices & edges, typed properties, walk adjacency |
| Query workspace | All scan types; results as tables; hydrate & send to canvas |
| Path finder | Source/target pickers, algorithm options, delegate slots, path results |
| Subgraph studio | List/create/inspect/recalculate/delete subgraphs; pattern builder |
| Graph canvas | Render/expand/style graph data; selection drives a detail panel |
| Delegate editor | Shared Monaco component; no standalone screen — opens in context from a fragment slot in the path finder or subgraph studio |

## 4. Functional requirements

### 4.1 Instances & dashboard
- FR-1a **Instance registry.** The user registers any number of named instances (name +
  base URL), persisted in local storage, with edit/remove. The connect screen shows a
  health overview of all registered instances (reachability + counts via `GET /status`,
  polled lazily), so a dead endpoint is visible before switching to it.
- FR-1b **Instance switching.** Exactly one instance is *active* per workspace context;
  switching is a first-class, always-visible control (not buried in settings). Every
  screen is unambiguously labeled with the instance it shows — a user must never
  mistake production data for local data. The design decides between a single global
  switcher and per-tab/workspace instance binding, but switching must preserve each
  instance's own context (FR-1c) rather than resetting the app.
- FR-1c **Per-instance state isolation.** Browser state is scoped per instance:
  canvas contents, query-workspace inputs, path/subgraph drafts, and result sets belong
  to the instance they came from and reappear when switching back. Editor snippets and
  UI preferences are global. Mixing elements from two instances in one canvas is
  invalid (ids are only unique per instance) and must be structurally impossible.
- FR-1d Destructive admin actions (FR-3) name the target instance in their
  confirmation prompt ("Erase all data on **prod-eu (http://…)**?").
- FR-2 Dashboard shows vertex/edge counts, used memory, and the three plugin lists from
  `StatusREST`; refresh on demand.
- FR-3 Admin actions: `PUT /save` (shows returned path), `PUT /load`, `HEAD /trim`,
  `HEAD /tabularasa`. Tabula rasa and load are destructive and require an explicit
  typed confirmation. Save/load must surface a 500 (rolled-back transaction) as failure.
- FR-4 Demo data: expose `GET /generate` (sample graph) and `GET /benchmark` as a
  "playground" affordance so an empty instance is usable in one click.

### 4.2 Element browser
- FR-5 Fetch and display a vertex (`GET /vertex/{id}`), edge (`GET /edge/{id}`), or
  either (`GET /graphelement/{id}`) with label, creation/modification dates, and typed
  properties.
- FR-6 Adjacency navigation: outgoing/incoming edge-property lists
  (`GET /vertex/{id}/edges/out|in`), per-property edge id lists
  (`…/edges/out|in/{edgePropertyId}`), degrees (`…/indegree|outdegree|…/degree`), and
  edge endpoints (`GET /edge/{id}/source|target`). One click from any element to its
  neighbors.
- FR-7 Bulk view via `GET /graph?maxElements=N`. The API truncates silently (default
  1000) and offers no paging — the UI must show an explicit "truncated at N" indicator
  whenever the returned count equals the requested cap.

### 4.3 Query workspace
- FR-8 Property scan (`POST /scan/graph/property/{propertyId}`), index scan
  (`POST /scan/index/all`), range scan (`POST /scan/index/range`), fulltext
  (`POST /scan/index/fulltext`, rendering highlights), spatial
  (`POST /scan/index/spatial`). Scans return bare id lists; the UI hydrates them via
  the element endpoints (batched, capped, with progress).
- FR-9 Typed literal input: everywhere the API takes
  `{ value, fullQualifiedTypeName }`, the UI provides a type-aware editor (at minimum
  `System.String`, `System.Int32`, `System.Int64`, `System.Double`, `System.Boolean`,
  `System.DateTime`) instead of free-text JSON.
- FR-10 Index management: create (`POST /index`), add element (`PUT /index/{id}`),
  remove key/element, delete index. Index ids are needed by scans, so the workspace
  lists known indices (see gap G-3 for discovery limits).
- FR-11 Every result set has "open as table" and "send to canvas" actions.

### 4.4 Path finder
- FR-12 `POST /path/{from}/to/{to}` with algorithm selection (`BLS` hop-count vs
  `DIJKSTRA` weighted — the UI explains that BLS ignores costs and `maxPathWeight`,
  and that Dijkstra's `maxResults` is the K in K-shortest paths), plus `maxDepth`,
  `maxResults`, `maxPathWeight` inputs with the API defaults pre-filled.
- FR-13 Five delegate slots (three filters, two costs — see §6) via the shared editor.
  Every slot is optional and starts empty (empty = match everything / no custom cost);
  a plain source→target query with no fragments is the common case and the slots are
  presented as the advanced tier of the form, for searches the basic inputs cannot
  express.
  Because the path endpoint swallows compile errors (returns an empty list and only
  logs the diagnostics — see `GraphController.CalculateShortestPath`), the UI must
  validate fragments **before** submitting (gap G-2). An empty result after successful
  validation is presented as "no paths found", never as an error.
- FR-14 Path results render as an ordered element list and as a canvas overlay
  (path elements highlighted against their neighborhood).

### 4.5 Subgraph studio
- FR-15 Full lifecycle: `PUT /subgraph` (create, incl. `fromSubGraph` nesting),
  `GET /subgraph` (list), `GET /subgraph/{name}` (summary), `GET /subgraph/{name}/graph`
  (contents → canvas), `POST /subgraph/{name}/recalculate`, `DELETE /subgraph/{name}`.
- FR-16 Pattern builder: an ordered sequence editor for `Vertex` / `Edge` /
  `VariableLengthEdge` steps with direction, min/max length (API caps max length at
  100), and optional per-step delegate slots (same empty-means-match-all default as
  FR-13). The sequence must alternate vertex ↔ edge starting
  with a vertex (a level-0 edge is also legal); the UI enforces this client-side and
  still surfaces the server's 400 if it disagrees.
- FR-17 Create returns 201 with a summary; 400 carries Roslyn diagnostics (map into the
  editors); 404 = missing `fromSubGraph` source; 409 = name conflict or quota
  (subgraph count / materialized-element ceiling) — each gets a distinct, actionable
  message. An **empty** subgraph is a valid 201 outcome, not an error.

### 4.6 Graph canvas
- FR-18 Renders vertices/edges from any source (bulk graph, hydrated scan results, path
  results, subgraph contents); supports incremental **expand-on-demand** (fetch a
  selected vertex's edges via FR-6 endpoints) instead of whole-graph loads.
- FR-19 Layouts (at least force-directed + one deterministic alternative), styling by
  label (stable color assignment), zoom/pan, node/edge selection driving the detail
  panel, and removal of elements from the view (not the database).
- FR-20 Target scale: interactive at 5 000 rendered elements; degrade gracefully (e.g.
  hide edge labels, cluster) rather than freeze beyond that.

### 4.7 Mutations
- FR-21 Create vertex (`PUT /vertex`), create edge (`PUT /edge`), add/update property
  (`PUT /graphelement/{id}/{propertyId}`), remove property, remove element — all with
  `?waitForCompletion=true` so rollbacks surface as 4xx/5xx instead of a fire-and-forget
  202. Forms reuse the FR-9 typed-value editor.

## 5. API contract and conventions

[openapi-v0.1.json](./openapi-v0.1.json) (OpenAPI 3.1, captured from a running
Development instance at `/openapi/v0.1.json`) is the source of truth for routes,
schemas, and status codes. Conventions that shape the client:

- **Routes are root-level** (`/graph`, `/status`, `/path/{from}/to/{to}`) — *not* under
  `/api/v0.1/` — because every action template starts with `/`, overriding the
  versioned controller route. Do not "correct" paths against the controller attributes.
- **JSON is camelCase**; typed literals travel as
  `{ "value"|"propertyValue": …, "fullQualifiedTypeName": "System.…" }`.
- **Mutations are transactional and asynchronous**: 202 Accepted by default;
  `waitForCompletion=true` makes rollbacks observable (400/404/409/500 mapped from the
  transaction failure reason). The UI always waits.
- **Lookups return 200-with-null / 204** for missing elements rather than 404 in
  several places; treat an empty body as "not found".
- ⚠️ The request **samples embedded in the OpenAPI document are partly stale**: they
  come from XML doc comments and include two-parameter edge filters like
  `return (e,d) => …;` which do **not** compile against the real delegate types. §6 of
  this spec is authoritative for fragment signatures; ignore contradicting samples.

## 6. Delegate authoring — contract and editor requirements

### 6.1 Fragment model

A fragment is a **C# method body that returns a lambda**, e.g.
`return (v) => v.Label == "person";`. The server wraps each fragment in a generated
class and compiles it with Roslyn (`fallen-8-core-apiApp/Helper/CodeGenerationHelper.cs`),
with usings `System`, `System.Linq`, `NoSQL.GraphDB.Core.Model` (subgraph fragments
additionally `NoSQL.GraphDB.Core.Algorithms`). A null/empty fragment means "match
everything". Returning `false` from a filter **excludes** the element.

Authoritative signatures (`fallen-8-core/Algorithms/Delegates.cs`):

| Used by | REST field | Delegate | Lambda shape |
|---|---|---|---|
| Path | `filter.vertexFilter` | `VertexFilter` | `(VertexModel v) => bool` |
| Path | `filter.edgeFilter` | `EdgeFilter` | `(EdgeModel e) => bool` |
| Path | `filter.edgePropertyFilter` | `EdgePropertyFilter` | `(string p) => bool` |
| Path | `cost.vertexCost` | `VertexCost` | `(VertexModel v) => double` |
| Path | `cost.edgeCost` | `EdgeCost` | `(EdgeModel e) => double` |
| Subgraph | `vertexFilter`, `edgeFilter` (top level) | `GraphElementFilter` | `(AGraphElementModel ge) => bool` |
| Subgraph | pattern `graphElementFilter` | `GraphElementFilter` | `(AGraphElementModel ge) => bool` |
| Subgraph | pattern `vertexFilter` | `VertexFilter` | `(VertexModel v) => bool` |
| Subgraph | pattern `edgeFilter` | `EdgeFilter` | `(EdgeModel e) => bool` |
| Subgraph | pattern `edgePropertyFilter` | `EdgePropertyFilter` | `(string p) => bool` |

### 6.2 Type surface for IntelliSense

The completion model covers exactly what a fragment can touch
(`fallen-8-core/Model/`):

- **`AGraphElementModel`** (base of both): `Id : int` (field), `Label : string`
  (field), `GetCreationDate() : DateTime`, `GetModificationDate() : DateTime`,
  `GetPropertyCount() : int`, `GetAllProperties() : ImmutableDictionary<string,object>`,
  `TryGetProperty<T>(out T result, string propertyId) : bool`.
- **`VertexModel`**: `GetInDegree()/GetOutDegree() : uint`,
  `GetAllNeighbors() : List<VertexModel>`,
  `GetIncomingEdgeIds()/GetOutgoingEdgeIds() : List<string>`,
  `OutEdges`/`InEdges : IReadOnlyDictionary<string, IReadOnlyList<EdgeModel>>`,
  `TryGetOutEdge/TryGetInEdge(out IReadOnlyList<EdgeModel>, string edgePropertyId) : bool`.
- **`EdgeModel`**: `SourceVertex`/`TargetVertex : VertexModel` (readonly fields),
  `EdgePropertyId : string`, plus the inherited base members.

The canonical property-access idiom to teach via snippets:
`return (v) => v.TryGetProperty(out int age, "age") && age > 30;`

### 6.3 Editor requirements
- FR-22 Monaco-based component parameterized by delegate kind; per-kind parameter
  snippet on open, snippet library of common filters, completions/hovers/signature help
  from a **static type model** generated from the classes in §6.2 (checked into the UI
  project; regeneration documented).
- FR-23 Compile validation with diagnostics rendered as in-editor markers. Preferred
  architecture is the hybrid: static client-side IntelliSense + a server-side
  validation endpoint (gap G-2). A full C# language server (OmniSharp over WebSocket)
  may be evaluated in the design but must not be the only diagnostics path.
- FR-24 Diagnostic positions must be mapped from the server's generated-class
  coordinates back to the user's fragment (the wrapping adds a namespace/class/method
  preamble; see `CreateSource`/`BuildProviderSource`). Off-by-N squiggles are a bug.
- FR-25 Submitting a path query with a fragment that fails validation is blocked
  client-side (because of the FR-13 error-swallowing behaviour); subgraph 400
  diagnostics are mapped back into the originating editor slot.
- FR-26 **Natural-language assist.** Wherever the delegate editor appears, the user can
  describe the intent in plain language ("only persons older than 30") and have an
  SLM/LLM draft the fragment. Requirements:
  - The generation context includes the slot's delegate kind and lambda shape (§6.1),
    the available usings, and the §6.2 type surface including the `TryGetProperty`
    idiom — so generated code targets the real API, not hallucinated members.
  - Generated code is inserted into the editor as ordinary editable text and goes
    through the same validation gate (FR-23/FR-25) before any submission; validation
    failures feed their diagnostics back into a refine/regenerate loop. Generated
    fragments are never sent to the query endpoints unvalidated.
  - The model backend is pluggable and user-configured — a local SLM (e.g. an
    Ollama/llama.cpp endpoint) or a hosted LLM API. With no backend configured, the
    assist affordance is hidden or disabled with a hint; the editor is fully usable
    without it.
  - Where a hosted provider is configured, the UI states plainly that prompt text and
    any included context leave the machine.

## 7. Backend prerequisites and gaps

Backend work is in scope for this feature where marked; everything else gets a
documented UI fallback.

| # | Gap | Disposition |
|---|---|---|
| G-1 | `Program.cs` configures **no CORS and no static-file serving** — a browser app cannot call the API cross-origin today. | **In scope.** Serving the built SPA from the apiApp (preferred — one deployable) covers the same-origin case, but multi-instance support (FR-1a) makes cross-origin calls unavoidable: the app served by instance A must call instances B and C. The apiApp therefore needs a (configurable) CORS policy either way; the plan specifies its defaults. Dev-mode proxying is the implementing agent's choice. |
| G-2 | No way to compile-check a fragment without side effects; `/path` swallows diagnostics entirely. | **In scope.** Add `POST /delegates/validate` to the apiApp: request `{ delegateKind, fragment }`, response `{ valid, diagnostics: [{ line, column, id, message, severity }] }` with positions already mapped to fragment coordinates (FR-24). MSTest coverage per repo conventions. |
| G-3 | No discovery of labels, property ids, edge-property ids, or existing index ids. | **Fallback.** Derive from sampled/loaded elements client-side and let users type free-form. Propose (do not build) a `GET /schema/summary` endpoint in the design doc. |
| G-4 | No pagination on `/graph` or scan results. | **Fallback.** Expand-on-demand canvas + `maxElements` + explicit truncation indicators (FR-7). |
| G-5 | No change-notification channel. | **Fallback.** Manual/interval refresh; note WebSocket push as future work. |
| G-6 | No natural-language→delegate facility exists anywhere in the stack (FR-26). | **In scope, UI-led.** The design doc decides the architecture — browser→provider calls with a user-supplied key/endpoint, a browser-side SLM, or a thin apiApp proxy endpoint — and specifies how the generation prompt is assembled from §6.1/§6.2. Whatever the choice, the feature degrades gracefully to a plain editor when no model is configured, and configuration (endpoint, key, model name) lives in the global UI settings. |

## 8. Non-functional requirements

- **Performance:** dashboard interactive < 1 s against a local instance; canvas
  interactive at 5 000 elements (FR-20); editor validation round-trip < 500 ms.
- **Error surfacing:** every failed request shows status + server message (never a
  silent console error); rolled-back transactions are failures (FR-3, FR-21);
  truncations are visible (FR-7).
- **State:** the instance registry, editor snippets, and canvas styling persist in
  local storage; per-instance context (FR-1c) is keyed by instance and survives
  switching and reloads; no server-side UI state.
- **Accessibility:** keyboard navigation for tables/forms; canvas interactions have
  non-pointer equivalents where feasible; color choices legible in light and dark.
- **Security posture:** no auth (see non-goals); the connect screen states the
  trusted-network assumption. Delegate fragments are arbitrary code **executed by the
  server** — the UI does not pretend otherwise and the docs say so plainly. That
  applies unchanged to NL-generated fragments (FR-26): generated code is reviewed and
  validated in the editor like hand-written code, and any model API key configured for
  the assist is stored locally and never sent to a Fallen-8 instance.
- **Browser support:** current evergreen Chrome/Edge/Firefox/Safari.

## 9. Acceptance scenarios

1. Connect to a fresh local instance → dashboard shows zero counts and plugin lists.
   Register a second instance; the connect screen shows both with live health; switch
   between them and verify every screen names the active instance, each keeps its own
   canvas/query context, and a stopped instance is flagged unreachable in the overview.
2. One click generates the sample graph; dashboard counts update.
3. Browse vertex → typed properties render; neighbors reachable in one click; degrees
   match the API.
4. Property scan (`Equal`, typed literal) → id results hydrate into a table → sent to
   canvas → expand-on-demand grows the neighborhood.
5. Path query with a `vertexFilter` written in the editor: completions offer
   `TryGetProperty`; an introduced syntax error produces a marker on the correct line
   and blocks submission; fixing it and submitting renders paths and overlays them.
6. Dijkstra with an `edgeCost` fragment returns weight-ordered paths whose
   `totalWeight` the UI displays; the same query under BLS shows weight 0 and the UI
   explains why.
7. Create a subgraph with the three-step pattern from the subgraph spec; 201 summary
   appears; contents render on the canvas; recalculate after adding data changes the
   counts; a deliberately broken fragment yields diagnostics inside the right editor
   slot.
8. Tabula rasa demands typed confirmation; afterwards the dashboard shows zero counts.
9. Stopping the API server yields a clear disconnected state, not a blank screen.
10. With a model backend configured, describing a filter in plain language in any
    delegate slot produces a fragment in the editor that passes validation and runs;
    with no backend configured, the assist control is absent/disabled with a hint and
    the editor works normally.

## 10. Testing requirements

- **Backend (this repo, MSTest):** `POST /delegates/validate` — valid fragment per
  delegate kind, syntax error (position mapping asserted, not just non-empty), semantic
  error (unknown member), empty/oversized fragment, unknown kind.
- **UI unit:** API client (route/serialization correctness against the OpenAPI file),
  diagnostic-position mapping, typed-literal editor conversions, truncation detection,
  per-instance state scoping (FR-1c: two registered instances never share or leak
  canvas/query context).
- **UI component:** delegate editor (completions from the static model, marker
  rendering), pattern-sequence builder validation, NL assist (generation-prompt
  construction per delegate kind asserting §6.1/§6.2 context is included, insertion
  into the editor, disabled state without a configured backend — model calls mocked).
- **End-to-end (against a live apiApp with generated sample data):** scenarios 1–8
  above, plus the disconnected state (9). Scenario 10 runs only where a model backend
  is available in the test environment; the unconfigured half of it is always testable.

## 11. Deliverables and workflow

1. `features/web-ui/design.md` — personas, information architecture, screen wireframes
   (ASCII or Mermaid), key flows, empty/loading/error states, tech-stack decision
   (SPA framework, data layer, and graph-rendering library — compare at least
   Cytoscape.js, Sigma.js, and a force-graph option at the FR-20 scale), the G-1
   hosting decision with rationale, and the G-6 model-backend decision for the
   natural-language assist (FR-26).
2. `features/web-ui/plan.md` — phased implementation; suggested order: connect +
   dashboard + browser → query workspace → canvas → delegate editor with static
   IntelliSense → G-2 validation endpoint + diagnostics mapping → NL assist
   (FR-26/G-6) → subgraph studio → mutations/polish. Each phase names its tests.
3. Implementation in `fallen-8-web-ui/` plus the G-1/G-2 backend changes in
   `fallen-8-core-apiApp/`, on branch `feature/web-ui`, tracked by a GitHub issue
   labeled `feature`, delivered via PR referencing the issue. Commit messages and PR
   text are honest and concise and do not reference an AI assistant.

## 12. Reference files

- [openapi-v0.1.json](./openapi-v0.1.json) — the API contract (regenerate by running
  the apiApp in Development and fetching `/openapi/v0.1.json`).
- `fallen-8-core/Algorithms/Delegates.cs` — delegate signatures (authoritative).
- `fallen-8-core/Model/AGraphElementModel.cs`, `VertexModel.cs`, `EdgeModel.cs` — the
  IntelliSense type surface.
- `fallen-8-core-apiApp/Helper/CodeGenerationHelper.cs` — fragment wrapping and
  compilation (drives FR-24 position mapping).
- `fallen-8-core-apiApp/Controllers/` — behavioural nuance the OpenAPI file cannot
  express (error swallowing in `CalculateShortestPath`, rollback→status mapping in
  `RolledBackResult`, subgraph failure mapping).
- [features/subgraph/spec.md](../subgraph/spec.md) — the subgraph domain model behind
  the pattern builder (§4.5).
