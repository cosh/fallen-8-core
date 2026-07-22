# Embedding out of the box — Spec

## Problem

The embedding stack ([element-embeddings](../element-embeddings/README.md) +
[embedding-provider](../embedding-provider/README.md)) is complete and correct, but
the product never completes the loop — a user who wants "type text, get embeddings" hits
three compounding dead ends:

1. **Nothing ever turns the provider on.** `appsettings.json` carries no
   `Fallen8:Embedding` block, and `docker-compose.yml` sets no `Fallen8__Embedding__*`
   variables — even though the compose environment already ships an Ollama sidecar (for
   NL-assist) that could serve an embedding model with one `ollama pull`. The only path is
   hand-editing config the product never surfaces.
2. **Provider status rides the wrong endpoint.** The status block is an O(1) config read
   (surfacing it never triggers the lazy model load — FR-9 of embedding-provider), yet it
   is exposed only on `GET /statistics`, the budgeted, rate-limited O(V+E) graph-shape
   pass that F8 Studio deliberately computes only on an explicit click. The cheap
   `GET /status` discovery surface — which Studio polls on every screen — carries
   everything *except* the provider block.
3. **Studio inherits the absurdity.** The Dashboard provider card, the Browser
   embeddings tab's text mode, and the semantic editors on Path/Subgraph/Query all gate on
   the graph-shape snapshot, so their default state is "provider status unknown — Compute
   the Graph shape (Analytics)": an instruction to run a graph-wide pass to learn a config
   value, which then invariably answers "off" with no hint how to turn it on.

## Behaviour

### 1. `GET /status` carries the provider block (REST contract)

`StatusREST` gains an optional `embedding` field with the existing
`EmbeddingProviderStatsREST` shape (enabled, backend, modelName, modelVersion, dimension,
intendedMetric, loaded). Reading it never triggers the lazy model load — same contract as
on `/statistics`.

- The block is built by one shared factory (`EmbeddingProviderStatsREST.From(provider)`);
  both controllers call it. No duplicated mapping.
- `/status` is `[AllowAnonymous]`, and that stays: the block is operator config identity,
  the same sensitivity class as the bound-index `embeddingName`/`model` identities the
  endpoint already exposes anonymously.
- `/statistics` keeps its `embedding` block unchanged — it remains the full snapshot;
  removing it would break existing consumers for no gain.

### 2. Studio gates text-in from `/status`

`embeddingProvider()` moves its source of truth from the graph-shape snapshot to the
shared `/status` cache row (`useStatus`), falling back to the shape snapshot for servers
predating the `/status` field. Consequences:

- The Dashboard card shows real status on load. Three states remain, but "unknown" now
  only means *the server predates the field* (or `/status` hasn't answered yet) — never
  "you haven't run statistics".
- The disabled state becomes actionable: it names the compose switch (`F8_EMBEDDINGS`)
  and the config section (`Fallen8:Embedding`) instead of dead-ending.
- The semantic editors and the Browser embeddings tab gate on the same live value; the
  "Compute the Graph shape" hint disappears from provider messaging. (Embedding-*name*
  suggestions still come from the shape snapshot — those really are graph data.)

Browser embeddings tab, three follow-up fixes from first real use:

- **Vector previews clamp.** The REST egress sends `Single[]` values as a bracketed
  *string* (`AGraphElement.FormatPropertyValue`), which the preview helper didn't
  recognize — a 1024-dim embedding dumped raw and blew up the layout. Both shapes now
  truncate to the first components plus `(d=N)`.
- **The tab survives a write.** A set/remove refreshes the element by re-running the
  lookup, which unmounts the detail panel while pending; tab state now lives on the
  screen, so the user stays on Embeddings and sees the result of their write.
- **Build the text from the element.** In text mode, the element's label and plain
  properties appear as checkboxes (all included by default — one Fill + Set embeds the
  whole vertex/edge); unchecking narrows to the interesting properties, and the composed
  `key: value` lines land in an editable textarea before anything is sent. Reserved
  embedding properties are never offered; the helper is provider-gated like the rest of
  text mode.

### 3. Compose enables the provider by default (`F8_EMBEDDINGS`)

`npm run env:up` yields working text-in embeddings, semantic search and GraphRAG with
zero configuration:

- The Ollama sidecar pulls **bge-m3** (MIT-licensed, like every model this environment
  pulls; 1024-dim, Cosine; the embedding-provider README's tested Ollama reference)
  alongside the phi4 models. Same degradation contract: a failed pull logs loudly and the
  daemon stays up.
- The `fallen8` service sets the `Fallen8__Embedding__*` variables for the Ollama backend
  (`Enabled=${F8_EMBEDDINGS:-true}`, `Backend=Ollama`, `ModelName=bge-m3`,
  `Dimension=1024`, `IntendedMetric=Cosine`, endpoint `http://ollama:11434`, model
  `bge-m3`).
- `F8_EMBEDDINGS=false` opts out: endpoints answer 403 again, the sidecar skips the pull,
  and the deployment posture matches today's. (Boolean-shaped like the .NET config value
  it feeds, unlike the numeric `F8_GPU` toggle.)
- No model-override knob: a different model needs a matching dimension anyway, so
  overriding means editing the env block where all the keys are visible together.
- Bare `dotnet run` (no compose) stays model-free and provider-off — unchanged.

## Non-goals

- No change to the engine (`fallen-8-core`) — this is apiApp + Studio + compose + docs.
- No new provider backends, no weight downloads by Fallen-8 itself (the sidecar pulls,
  exactly as it already does for NL-assist).
- No change to the 403/409/503 consistency contract of the provider.

## Impact on existing features

| feature / asset | impact | action |
|---|---|---|
| embedding-provider | `/status` becomes the cheap status surface; compose gains out-of-box wiring | README Ops + config sections updated here |
| element-embeddings | none to the engine contract; Studio text-mode gating source changes | README untouched (it already defers provider concerns) |
| studio-coverage / studio-semantics | provider gating moves from shape snapshot to `/status`; dashboard card copy changes | Studio code + `dashboard-provider` tests updated here |
| observability (`/statistics`) | unchanged — keeps its `embedding` block | none |
| OpenAPI snapshot | `StatusREST` gains `embedding` (addition only) | regenerate via `scripts/update-openapi-snapshot.ps1` |
| NL-assist dataset / fine-tune | none — the NL surface (semantic block, endpoints) is unchanged; provider being on by default only makes `queryText` requests succeed | no RETRAIN-LOG entry |
| persisted recipes / stored queries | none — no contract change | none |
| docker environment | new default model pull (~1.2 GB, cold volume only); `F8_EMBEDDINGS` knob | compose header + root README env docs updated here |
| DEBUGGING.md | no port/launch changes | none |
