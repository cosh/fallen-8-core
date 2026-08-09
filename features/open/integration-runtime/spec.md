# Integration runtime - Specification

> **Status:** Open, spec only (no implementation yet). Follow the feature workflow in the
> repository root `CLAUDE.md`. Feature branch: `feature/integration-runtime` (branch-only workflow,
> no GitHub issue/PR).
>
> **Siblings.** [integration-identity](../integration-identity/spec.md) owns the claim model,
> resolution, merge rules and reconciliation, and is the document to review hardest.
> [integration-blueprints](../integration-blueprints/spec.md) owns the provider contract and the
> three reference providers. This document owns the sidecar, its configuration and secrets, the
> channel to the browser, the scheduler, the write path, the AI surface, the conformance verifier,
> the Studio screen and the pipeline.
>
> **Hard dependency.** The P0 items in
> [platform-integrity-audit](../platform-integrity-audit/spec.md) land as phase 0 of this feature,
> on this branch. That document is the single home for their evidence, minimal fixes and the
> fifteen rejected alternatives; this spec references them by number and does not restate them.
>
> **Revision history:**
> - *2026-08-09a* - initial design, after the platform audit.

## 1. What this is, and the bar it is held to

A first-party sidecar that hosts long-lived integrations pulling data from systems on the user's
own network into a Fallen-8. A separate deployable, never part of the engine or the apiApp, modelled
on [mcp-server](../../done/mcp-server/spec.md)'s posture: own project, own Dockerfile, own GHCR
image, own config prefix, depends on a healthy *fallen8*, own healthcheck, and fleet observability
declaring the **same** tenant and instance identity as the Fallen-8 it serves.

**The goal is not three integrations. The goal is that an agent can write the fourth one without
help.** Every design choice in this document is judged against that:

- if a requirement can move out of the integration and into the runtime, it moves,
- if correctness can be enforced by a test instead of documented in prose, it is a test,
- and if a provider needs to know anything in [integration-identity](../integration-identity/spec.md),
  the contract is wrong.

The measurable form of that bar is section 9: a provider must pass an offline conformance suite,
and the minimal blueprint must fit in roughly a hundred lines. If it cannot, the contract is too
heavy and that is a finding, not a rounding error.

## 2. Fixed shape

| Thing | Value |
|---|---|
| Project | *fallen-8-integrations*, net10.0, ASP.NET Core, root namespace *NoSQL.GraphDB.Integrations* |
| Image | *ghcr.io/cosh/fallen-8-core-integrations*, **linux/amd64 and linux/arm64** |
| Compose service | *f8-integrations*, profile *integrations* |
| Container port | **8110** (free: 3000, 4317, 4318, 5001, 8080, 8081, 8090, 8100, 11434 are taken) |
| Config prefix | *Integrations__* for its own behaviour; *Fallen8Target__* for the graph it writes to |
| Opt-out | *F8_INTEGRATIONS=false* is a **true** opt-out: capability off in *fallen8* **and** the sidecar skipped by *env:up*, exactly how *F8_INGESTION* works today |
| Named volume | *f8-integrations-data* |
| Host port | **not published.** It stays on *f8-net* |

Not published is a **new posture in this compose file**, not a match for an existing one: docling
and nlp are indeed apiApp-only callers that the browser never touches, but both publish host ports
today, as does every other service. It is the right call for a container holding somebody's network
admin credential, and it is stated as new rather than dressed up as precedent.

The engine and the apiApp are otherwise untouched, apart from phase 0 and the facade in section 3.

## 3. The channel to the browser: a typed facade, not a proxy

**This deviates from the brief, on the unanimous finding of four architecture reviews, and the
reasoning is recorded here because the conclusion is not obvious.**

The brief specified that the apiApp reverse-proxies to the runtime so the browser never talks to
the credential-holding container directly. **That requirement is kept in full.** What changes is the
mechanism. The brief framed the options as YARP or hand-rolled forwarding middleware. The repository
has a third option that is already proven twice:
[SidecarHttpClient](../../../fallen-8-core-apiApp/Ingestion/SidecarHttpClient.cs), the shared typed
base behind *DoclingClient* and *NlpClient*. It owns a DNS-recycling *HttpClient* (so a restarted
sidecar container's new address is picked up rather than pinned for the process lifetime), endpoint
normalization, a cached `GET /health` probe surfaced on `/status`, and a test-handler seam.

A third implementation plus ordinary apiApp controllers gives, for free and without a new
dependency: the API key and authorization policies, CORS under the split topology, rate limiting,
the problem+json error contract, the OpenAPI document, the JSON source-gen gate, and the
engine-to-REST-to-MCP coverage gate. A forwarder inherits none of them, adds a second
serialize-deserialize hop to a surface whose measured cost is already 99.9% pipeline, and creates
exactly the route branch that **silently escapes** the coverage gate, because that gate enumerates
operations from the OpenAPI document and a non-controller endpoint is not in it.

Everything the brief required of the proxy holds: the browser never reaches the runtime, the API key
protects the credential surface, there is no CORS to the sidecar, and the facade contains **zero
provider knowledge**.

Two consequences settled here:

- **The split topology is the unconditional default.** [env-up.js](../../../scripts/env-up.js)
  always applies [docker-compose.split.yml](../../../docker-compose.split.yml), so the apiApp is
  UI-less and CORS-allow-listed to the Studio origin, and the two topologies return **different
  responses for a route that does not exist** (the SPA shell versus problem+json). Studio therefore
  discovers the surface from `/status`, never by probing a route, or the all-in-one image reports an
  unlabelled JSON parse error instead of a clean "not available".
- **Route naming.** The REST resource is **`/integration/*` (singular)**, following the repository's
  strongest naming convention (`/document`, `/vertex`, `/edge`, `/index`, `/subgraph`,
  `/storedquery`), which exists precisely so the plural or differently-named Studio route
  (`/integrations`) is not shadowed by a real API route on a full-page load. The brief said
  `/integrations/*`; taking it literally would make the natural Studio route unreachable, the same
  trap [routes.tsx](../../../fallen-8-web-ui/src/app/routes.tsx) documents three times already. This
  is a naming consequence of the fixed decision, not a relitigation of it, and it is called out
  rather than changed quietly.

## 4. Provider abstraction

A provider declares, and nothing more:

| Declares | Purpose |
|---|---|
| metadata | id, display name, version |
| configuration schema | **JSON Schema**, so Studio renders the form with no provider-specific code |
| credential descriptor | which secrets exist, their shape, and how to obtain them (text for the UI) |
| connectivity test | reach the source and report success or a usable failure, writing nothing |
| **label and property vocabulary** | the labels and property keys it will produce (section 7) |
| optional summary template | declarative, for the embedding text (section 7) |
| declared free-text fields | the only fields NLP enrichment may ever see (section 7) |
| fetch | returns a canonical **snapshot** |

A provider never touches the graph, never resolves identity, never sees a vertex id, and never
learns whether an entity it returned was created or matched.

**The snapshot document** (schema owned with the claim model, in
[integration-identity](../integration-identity/spec.md)):

```
snapshot: { schemaVersion, providerId, integrationInstanceId, capturedAt,
            sourceVersion, entities[], observations[], diagnostics[] }
```

**Observations are recorded as observations, never as assertions.** An OUI vendor lookup saying a
MAC belongs to a manufacturer is an observation: it may inform a suggestion in the UI, and it may
never trigger a merge, create a claim, or auto-configure anything.

**Providers are compiled in.** The abstraction is shaped so out-of-process or dynamically loaded
providers are addable later (the provider is reached only through its declaration and its fetch, and
the snapshot is already a serializable document), but none of that is built.

**Poll-and-snapshot must not become load-bearing.** Event-driven sources (MQTT, Zigbee2MQTT, Shelly)
are the intended third axis and are out of scope here. The constraint that keeps them addable: the
snapshot is a *set of assertions at a point in time*, and reconciliation is a set difference against
what this instance previously claimed. An event-driven provider produces the same assertions
incrementally, so it needs a way to say "this is a partial assertion, do not treat absence as
withdrawal". The snapshot schema therefore carries a **completeness** declaration from day one, even
though every v1 provider sets it to complete. That single field is what prevents reopening the
identity model later.

### 4.1 Provider catalog and instances

`GET /integration/providers` returns the catalog: metadata, configuration schema, credential
descriptor, vocabulary. **Adding a provider must require zero Studio code change.** A
provider-specific React component is a contract failure, not a UI task.

An **integration instance** has an id, display name, provider id, configuration, credentials,
schedule, enabled flag, and a **target namespace** (default *default*). Several instances of one
provider are supported and expected: two UniFi sites, three inverters. Instance-scoped identifier
types (see the identity spec) depend on the instance id being stable, so it is assigned once and
never reused.

### 4.2 Scheduler and run state

One scheduler per instance: an interval plus a manual sync now. Per-instance status: last run,
duration, elements created, updated, removed, claims withdrawn, merge candidates raised, last error.

**Run state and the pending-review queue live in the graph, not in a jobs API.** The repository has
a precedent that says so: long-running ingestion state is graph state (a status property on the
Document vertex, swept on boot, read through the ordinary document routes), and the pinned OpenAPI
snapshot has no `/jobs`, `/operations` or `/runs` family. Putting run history and merge candidates in
the graph means the entire list-and-read half needs **zero** new routes: Studio reads them with
existing scans, agents see them through existing *f8_search* and *f8_get*. Only the imperative verbs
need a route.

**Default interval is conservative, and there is no auto-save until phase 0 lands.** A one-minute
poll refreshing a few hundred elements is on the order of 345k transactions per day, the WAL only
truncates inside `PUT /save`, and boot replay is linear in mutations since the last save (audit
W17). And until W1 fixes the non-durable registry write, recommending frequent saves would multiply
a silent-total-loss window by the save rate. So: default five minutes, no automatic save, both
revisited when W1 and W17 land.

## 5. Configuration and secrets

- Configuration lives on *f8-integrations-data*. Non-secret configuration is plain JSON, readable
  and hand-editable.
- Secrets are sealed with **ASP.NET Core Data Protection**, key ring on the same volume, protected
  by a **required** *F8_INTEGRATIONS_SECRET_KEY*. If a secret store exists and that variable is
  absent, **startup fails** with a message that says exactly what to do. A missing key with no
  existing secrets is a clean first start, not a failure.
- **The API never returns a secret.** Only a *configured* boolean and a short fingerprint. Writes
  are replace-only; there is no read-back path at any tier.
- **A redaction filter runs on every logger, applied before the log line is formed**, not as a
  post-filter on a formatted string. A test drives known secret values through every code path that
  logs and asserts none appears in any sink.

**On the credential blast radius, stated plainly rather than glossed.** The apiApp's API key is
all-or-nothing by a documented decision, and per-route policies gate on operator configuration
rather than on the caller. Putting the credential surface behind that key therefore promotes the
graph key to a network-admin-credential key, and in the shipped default compose environment there is
no key at all. Scoped credentials and RBAC are rejected as machinery a single-process self-hosted
server should not grow, with the revisit trigger recorded in the audit (a second, differently
trusted human consumer of the same instance). What this feature does instead: the credential-write
routes require the key even when the instance is otherwise anonymous, and the security-posture
documentation says the rest out loud.

## 6. The write path

- **All mutation goes through the transactional graph API over REST** against
  *Fallen8Target__BaseUrl* with *Fallen8Target__ApiKey*. A caller's token is never forwarded,
  matching *f8-mcp*.
- **Bulk import is unusable** and this is verified, not assumed: it answers 409 unless the target has
  zero vertices and zero edges, and it remaps ids unconditionally. Bulk **export** is fine and is
  what the demo and per-integration views use.
- **The graph is the sole system of record.** The runtime holds an ephemeral cache and nothing else.
  Any durable sidecar state (an outbox, a sync journal, a local claim store) is rejected: it would
  silently reverse that decision and create a second authority that can disagree with the graph.
- **A re-sync of an unchanged source produces zero mutations, and the invariant is asserted on the
  write-call channel**, because an equal-value property write is observable in modificationDate, the
  change feed and the WAL (audit W2, W6). Asserting it against graph state would make the test either
  vacuous or unpassable.
- **Never key on an element id across requests.** `HEAD /trim` renumbers ids in place, unwaited and
  agent-reachable.

Phase 0 is what makes the above achievable rather than aspirational: batch property set-or-remove,
batch element remove, batch element read, the derived-index rebuild primitive, the durability signal.
Without them a sync of a few hundred devices is well over a thousand HTTP round-trips, most of them
producing no change, and there is no property-update path at all.

## 7. Which Fallen-8 services an integration should use

This table is the normative version; it is **encoded as a decision table in the graph-modeling
skill**, not restated in prose anywhere else.

| Need | Use |
|---|---|
| All mutation | transactions, nothing else |
| Claim to element resolution | the derived claim index (hot path) |
| Numeric and time-varying properties | range index |
| Names, hostnames, SSIDs | fulltext index |
| Site coordinates | spatial R-Tree |
| Topology questions | path finding |
| Semantic lookup over entities | vector index, when embeddings are on |
| Per-integration or per-kind views, demo data | subgraphs, plus bulk export |
| Queries an integration ships with itself | stored queries |
| Reacting to what other integrations wrote | change feed |
| **Never in the ingest path** | property scans, ad-hoc code fragments |

Each integration may ship **stored queries**, registered idempotently when an instance is enabled.
These become the default Studio views for that integration and the query examples in its
documentation.

## 8. Semantic and AI surface

### 8.1 Element embeddings

- A provider may declare an optional **entity summary template**, declarative and not free-form
  code, producing the text to embed ("Fronius Symo 8.2-3-M inverter, VLAN 30, garage").
- Embedding is **opt-in per provider and per instance, default off**. Embedding every client on a
  busy network by default is waste and noise.
- **Dimension and metric are read from the instance's declared embedding configuration**, which
  `GET /status` already carries (*dimension*, *intendedMetric*, anonymous, no API key needed). No
  model, dimension or metric is hardcoded anywhere.

### 8.2 Degradation is a hard requirement, expressed as a test matrix

The runtime must work correctly with *F8_EMBEDDINGS=false*, *F8_CHAT=false*, *F8_NLP=false* and
*F8_INGESTION=false*, in **any combination**. Every AI-dependent behaviour degrades to **absent**,
never to broken. Sixteen combinations, asserted as a matrix over observable behaviour, not a
paragraph.

### 8.3 NLP enrichment

Structured sources do not need entity extraction, so most integrations will use the nlp sidecar not
at all. It matters only where a source carries genuine free text: device notes, client aliases, room
or zone labels, attached documents.

- **A provider declares which fields are free text, and enrichment runs only on those.** Never
  speculatively across all properties. The conformance suite asserts that the set of fields sent for
  enrichment is exactly the declared set.
- Extracted entities and key terms follow the semantic-layer boundary in
  [integration-identity](../integration-identity/spec.md) section 6: they never enter the claim space
  at all.

### 8.4 NL assist vocabulary

Because queries are C# fragments rather than a query language, the NL-assist path can only draft
correct fragments if it knows the labels and property keys an integration produced. Each provider
declares its vocabulary and the runtime publishes it.

**Correction to the brief, verified:** the brief instructed me to feed this into the existing
schema-hinting mechanism used by nl-assist and instance-config. **The prompt has no graph-schema
injection at all** ([prompt.ts](../../../fallen-8-web-ui/src/delegate/nl/prompt.ts) is a static
delegate-contract prompt) and `GET /config` carries no vocabulary. What does exist is the *observed*
per-namespace vocabulary from `GET /statistics` (sampled label and property-key cardinalities),
which already reaches Studio's shared graph-shape cache and the MCP *f8_overview* passthrough. So
the mechanism exists one hop short of the prompt, and wiring that hop is audit W12, not a second
mechanism invented here. This feature publishes the **declared** vocabulary on the provider catalog
and consumes W12's hop; it does not build a parallel path.

Per the RETRAIN-LOG conventions this is data rather than drafted surface, so **no retrain entry**.

### 8.5 GraphRAG

The payoff is asking questions of your own network: semantic lookup to find entry points, then paths
and subgraphs from there. Shipped as **stored queries with the UniFi and Fronius blueprints**, so it
works the moment an integration is configured. This is also the public demo graph, so it is expected
to be presentable, and it degrades to the non-semantic entry points when embeddings are off.

## 9. Conformance verifier

**This matters more than the skills.** Prose drifts and agents pattern-match to whatever the
examples happen to do; a failing test is unambiguous. It lives here, in the runtime spec, because it
tests the **runtime's** contract; the blueprints are its subjects.

A snapshot validator endpoint plus a suite any candidate integration must pass:

- snapshot schema valid, *schemaVersion* honoured, *completeness* declared,
- identity claims well formed, types from the vocabulary, strength declaration agreeing with the
  vocabulary,
- **deterministic**: two runs over one fixture produce byte-identical snapshots,
- **idempotent**: an unchanged re-sync produces zero write calls,
- **claim scoping**: no writes outside its own claim set,
- **no automatic merge on weak-only claims**,
- **no merge influenced by any similarity signal**,
- declared free-text fields are the **only** fields sent for NLP enrichment,
- correct behaviour with embeddings, chat, nlp and ingestion each disabled, in any combination,
- **no secret in any log line or API response**,
- and it **runs fully offline** against recorded fixtures and a fake Fallen-8 target, so an agent can
  iterate without a live source or a live instance.

**Negative fixtures are part of the deliverable, not a nicety.** Deliberately broken sample
integrations that must fail specific **named** checks, so the verifier itself is tested. A verifier
that passes everything is worse than none, because it certifies.

All three blueprints pass the suite as regression tests.

Exposing the verifier as an MCP tool so an agent iterates in-loop is the obvious next step and is
out of scope; the design is shaped toward it (the verifier is a callable endpoint over a serializable
document, not a test-only harness).

## 10. Observability

Fleet observability in the *f8-mcp* pattern: OTLP push to the same collector, declaring the **same**
tenant and instance identity as the Fallen-8 it serves, so fleet dashboards resolve the integration
panels under that instance. Per-instance sync metrics (duration, elements written, claims withdrawn,
candidates raised, failures) with **bounded** tag sets: provider id and instance id are
operator-provisioned identity dimensions, which the narrowed tag-hygiene invariant permits. **No
graph content in any tag**, and no source-derived value ever.

## 11. Studio screen

A new screen alongside the existing Fallen-8-level screens (Connect, Save games, Benchmark), because
the runtime is one per Fallen-8 rather than one per namespace, while each instance names its own
target namespace. Route `/integrations` (plural), which is why the REST resource is singular
(section 3).

It shows runtime reachability, the provider catalog, configured instances with status, pending merge
candidates with a confirm action, and **configuration forms rendered from the provider-declared JSON
Schema**. Secrets are write-only: enter or replace, never read back.

Follows the existing conventions for instance registration and status display rather than inventing
patterns, accounts for the standalone-Studio mode (which is the default), and contains **no
provider-specific code anywhere**. Screenshots are recaptured per the standing rule.

## 12. Pipeline

- **release.yml**: one more leg on the existing image matrix, mirroring the structure exactly.
  *linux/amd64,linux/arm64* is already the default for every dotnet image, so arm64 is not a
  special case.
- **buildAndTest.yml**: build the project, run its unit tests **and the conformance suite** in the
  backend job. The suite is offline by construction, so it needs no service containers.
- **docker-compose.yml**: an *f8-integrations* service following the *f8-mcp* block, including fleet
  observability and identity wiring, the *Integrations__* block on the *fallen8* service, the named
  volume, and profile handling in *env-up.js*. Keep the commenting style of the existing services.
  The profile must be wired at **all** *env:up*, *env:down* and *env:logs* / *env:status* call sites,
  including the three hard-coded compose invocations in *package.json*, or a running sidecar is
  stranded on *down*.
- **CodeQualityTest**: the new project joins *_allProjects* and *_productProjects* so the MIT
  header, no-*Console.Write*, no-*DateTime.Now* and exact-package-version gates actually run on it.

## 13. Impact on existing features (cross-feature sweep)

- **[platform-integrity-audit](../platform-integrity-audit/spec.md)**: its P0 items are this
  feature's phase 0. Its rejections bind this feature.
- **[mcp-server](../../done/mcp-server/spec.md) and the coverage gate**: every new REST operation is
  either an **op on an existing tool** or a recorded deferral with a reason. No new MCP tools; the
  surface is deliberately small and every schema is paid for in every agent's context. If the control
  plane later needs more than about three agent-reachable verbs, that is **one** *f8_integrations*
  tool, not one per verb.
- **[semantic-layer](../../done/semantic-layer/spec.md)**: the boundary is in the identity spec, and
  the one change proposed there (subsuming its hand-rolled entity-index sweep into the shared rebuild
  primitive) touches its boot path and is proposed rather than assumed.
- **[embedding-provider](../../done/embedding-provider/spec.md) / [vector-index](../../done/vector-index/spec.md)**:
  consumed read-only through `/status` and the existing embedding routes. No change.
- **[change-feed](../../done/change-feed/spec.md)**: consumed, and its resync event becomes
  load-bearing for a first-party client. No contract change; its documentation gains a note that a
  derived-index owner must treat resync as rebuild.
- **[stored-query-library](../../done/stored-query-library/spec.md)**: integrations register stored
  queries. No change to the registry.
- **[standalone-ui](../../done/standalone-ui/spec.md) / [studio-embeddable](../studio-embeddable/spec.md)**:
  the facade keeps the single-channel property under both topologies; the new screen honours the
  embeddable seams.
- **OpenAPI snapshot**: regenerate. **Studio**: new screen plus screenshots. **NL-assist**: consumes
  W12, **no retrain entry**. **Architecture diagrams**: this adds a deployable and a new channel, so
  **both** the root README diagram and the docs-site architecture page change in the same PR, in the
  fixed dark and brand-red style.
- **Docs**: four pages (runtime, authoring, UniFi, Fronius), registered in the sidebar, plus a
  README key-feature line linking the published page.

## 14. Out of scope, recorded as future work

- Event-driven providers over MQTT (the intended third axis; the *completeness* field in section 4 is
  what keeps it addable without reopening the identity model).
- Out-of-process or dynamically loaded providers.
- A canonical cross-provider property vocabulary.
- Hosted multi-tenant operation.
- Using the chat gateway to draft an integration from a described API.
- Exposing the conformance verifier as an MCP tool so an agent iterates in-loop. The obvious next
  step; designed toward, not built.
