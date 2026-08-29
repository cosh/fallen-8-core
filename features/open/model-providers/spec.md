# Spec: Model providers (central backend selection + provenance)

Status: **open** - spec'd 2026-08-29, not implemented. Implementation is planned for a
`feature/model-providers` branch (see `plan.md`).

Fallen-8's two model capabilities - the chat gateway (`POST /chat`, NL assist) and the
embedding provider - each select a backend today: `Ollama` (the local sidecar) or `Nahil`.
This feature adds **OpenAI** and **Anthropic** as first-class server-side backends, gives the
deployment a **central provider selector**, and makes **which backend served a call visible
everywhere the call's output or stats are shown**.

## Why

Two observed problems (Studio screenshots, 2026-08-29, Nahil deployment):

1. **The serving backend is invisible at the point of use.** The delegate editor's NL panel
   says `this instance · local → /chat (server-selected model)` while Nahil serves the
   request - `local` is the *connection registry name*
   (`fallen-8-web-ui/src/instances/registry.ts:79-80`), not a backend. The draft stats
   (`1130→12 tok · 0.1s · 130.8 tok/s`, raw JSON) name the model but not the backend,
   because the wire cannot say it: `ChatResultREST` carries only `content`/`model`/`stats`
   (`fallen-8-core-apiApp/Controllers/Model/ChatREST.cs:105-128`). The only place the
   backend name appears is the Connect → Configuration card, three screens away.
2. **OpenAI and Anthropic exist only browser-direct, with the key in the browser.** Studio's
   custom NL mode ships presets for both (`src/delegate/nl/config.ts:86-93`), but the server
   knows nothing about them: the credential lives in browser state and every prompt leaves
   the user's machine from the browser. Moving these providers server-side puts the
   credential where Nahil's already is - in the instance's environment, never published,
   never logged - and lets every Studio feature (and MCP-driven agents using `/chat`-backed
   flows later) benefit without per-browser setup.

## Decisions (operator)

1. **Four chat backends, five embedding backends, same selector mechanism.**
   `Fallen8:Chat:Backend` widens to `Ollama | Nahil | OpenAI | Anthropic`;
   `Fallen8:Embedding:Backend` widens to `Onnx | LLamaSharp | Ollama | Nahil | OpenAI`.
   No new inheritance layer (a `Fallen8:Models:Provider` that capabilities inherit was
   considered and **rejected**: it adds a precedence rule to 102 existing settings for the
   benefit of saving one line of config; revisit if a third model capability appears).
   "Central" is delivered by the deployment selector (FR-8), the existing catalog, and the
   Configuration screen - not by new config machinery.
2. **Anthropic is chat-only.** Anthropic ships no embeddings endpoint (their docs point at
   third-party embedding providers). `Fallen8:Embedding:Backend=Anthropic` is refused at
   construction with a message naming the key, exactly like an unknown backend name today
   (`Program.cs:832-838` handles the unknown-name case; this becomes a known-but-invalid
   case with its own sentence).
3. **The presets move chat only; embeddings move only by explicit configuration.** Nahil
   was designed as "same model, same vectors, nothing re-embeds". OpenAI embeddings are a
   **different embedding function**: new identity stamp (`text-embedding-3-*`), new
   dimension (1536/3072), and every stored vector and index built under `bge-m3` reports an
   honest identity mismatch. That is not a "default" anyone should get by picking a provider
   preset. The `openai`/`anthropic` deployment presets therefore keep embeddings on the
   local sidecar (which keeps running, pulling only `bge-m3`); moving embeddings to OpenAI
   is a documented manual configuration with the re-embed consequence stated up front.
4. **Credentials stay environment-only (R8), including for OpenAI and Anthropic.** No key is
   ever writable over REST, published by `GET /config`, pasted into Studio, or logged. This
   deliberately does NOT satisfy "type the key into a Studio settings screen": a writable
   credential lets any REST caller redirect metered spend, and `GET /config` is anonymous on
   a keyless instance. The `.env` file (feature nahil-env-file) is the set-once home.
   Studio renders the key rows the way it renders Nahil's today: `not writable (R8)` with
   the reason (`SettingRow.tsx:102-106`).
5. **Provenance is stamped per response by the server, never inferred by the UI from current
   config.** A draft made under Nahil must still say Nahil after the operator switches the
   backend to Anthropic. The UI may use `/status`'s chat block for the *ambient* "requests
   will go to X" label, but every per-call stat display uses the `backend` field carried on
   that call's response.
6. **Official SDKs preferred, transport contract wins.** The Anthropic backend uses the
   official Anthropic .NET SDK against the native Messages API (the OpenAI-compatibility
   shim was considered and rejected: it is a documented migration bridge, and native usage
   stats/stop semantics are first-class). The OpenAI backend uses the official OpenAI .NET
   SDK. Both are wrapped in an `IChatBackend` implementation the way `OllamaChatBackend`
   wraps OllamaSharp. Non-negotiable transport contract, from the Nahil work:
   - the caller's `TimeoutSeconds` is the SINGLE deadline (no second SDK timeout, SDK
     auto-retries disabled; the deadline rule of `OllamaHttpClientFactory.cs:42-50`);
   - the credential is attached once at client construction and never appears in a log,
     an error message, or an exception (four enforcement points, see nahil-backend FR-3);
   - every transport-building call site takes an optional `HttpMessageHandler` so tests
     exercise the real composition (`OllamaHttpClientFactory.cs:79,91` precedent).
   If an SDK cannot honor this contract (no handler injection, unremovable retry), the
   implementer drops that provider to a thin raw-HTTP transport like the Ollama/Nahil path
   and records the reason in the plan. Exact package versions, per repo convention.
7. **No sampling parameters are sent to Anthropic.** Current Claude models (Opus 5 family
   and newer) reject `temperature`/`top_p`/`top_k` with a 400. The Anthropic backend
   ignores `ChatBackendOptions.Temperature` and documents that on the option; `stop`
   maps to `stop_sequences`. OpenAI receives temperature and stop as-is.
8. **Model names are required, not defaulted, in code - the overlays supply defaults.**
   Same fail-closed philosophy as the Nahil key: `Fallen8:Chat:OpenAI:Model` /
   `Fallen8:Chat:Anthropic:Model` / `Fallen8:Embedding:OpenAI:Model` have no code default
   and are validated at construction. The compose overlays default to `claude-opus-5`
   (Anthropic) and `gpt-4o-mini` (OpenAI, matching Studio's existing custom preset). These
   are config strings; the repo does not chase either vendor's catalog.
9. **The quality question is answered by precedent, not hand-waving.** Instance-mode drafts
   against OpenAI/Anthropic send exactly the prompt Studio already composes - the same
   prompt the browser-direct custom presets send to the same providers today. The fine-tune's
   baked system prompt (which exists only in the Ollama modelfile) is not replicated
   server-side; if generic-model draft quality turns out to need one, that is a follow-up
   with its own eval, not a silent prompt fork in this feature.

## Requirements

### FR-1: Backend selectors widen

`Fallen8:Chat:Backend` accepts `OpenAI` and `Anthropic`; `Fallen8:Embedding:Backend`
accepts `OpenAI`. Matching stays ordinal; the catalog `allowedValues` widens accordingly
(`Fallen8SettingCatalog.cs:281-282`, load-bearing because matching is ordinal).
`Backend=Ollama` and `Backend=Nahil` behaviour stays bit-identical.

### FR-2: Provider option blocks and validation

New sections, mirroring `NahilOptions`:

```
Fallen8:Chat:OpenAI:Endpoint        default https://api.openai.com   (host root, R4)
Fallen8:Chat:OpenAI:ApiKey          no default, required             (R8)
Fallen8:Chat:OpenAI:Model           no default, required             (Restart, writable)
Fallen8:Chat:Anthropic:Endpoint     default https://api.anthropic.com (host root, R4)
Fallen8:Chat:Anthropic:ApiKey       no default, required             (R8)
Fallen8:Chat:Anthropic:Model        no default, required             (Restart, writable)
Fallen8:Chat:Anthropic:MaxTokens    default 4096, bounds 256..128000 (Restart, writable)
Fallen8:Embedding:OpenAI:Endpoint   default https://api.openai.com   (host root, R4)
Fallen8:Embedding:OpenAI:ApiKey     no default, required             (R8)
Fallen8:Embedding:OpenAI:Model      no default, required             (R3, never writable)
```

`MaxTokens` exists because the Messages API requires it per request; no other provider gets
the knob. Endpoints obey the host-root rule and its no-endpoint-in-errors discipline
(`OllamaConnection.cs:114-166`); the transports build full request paths themselves, so a
host root is sufficient for both providers. Validation failures latch the capability 503
with the exact key named, process keeps running (`ChatBackendFactory.cs:36-39` behaviour).

### FR-3: Chat transports

`OpenAIChatBackend` and `AnthropicChatBackend` implement `IChatBackend`
(`Chat/IChatBackend.cs:39-48`). `ChatBackendFactory.Create` (`ChatBackendFactory.cs:43-59`)
splits its hardcoded `new OllamaChatBackend(...)` into a per-backend switch. Contract:

- **Auth**: OpenAI `Authorization: Bearer`; Anthropic `x-api-key` + `anthropic-version`.
- **Retry**: 429 (both) and Anthropic's 529 overloaded are waited out inside the caller's
  budget, honoring `Retry-After`, with the same clamp-and-jitter discipline as
  `NahilWarmupRetryHandler` (which is generalized or mirrored; 503-warming stays
  Nahil-specific). No separate retry budget - nahil-backend decision 4 governs.
- **Streaming**: `Fallen8:Chat:Stream` asks the provider to stream (SSE on both), the REST
  response stays buffered, and a stream that dies mid-answer is a detectable 502 naming how
  much arrived - the Nahil truncation behaviour (`OllamaChatBackend.cs:121-153` precedent).
- **Stats mapping**: `promptTokens`/`completionTokens` from the provider's usage object
  (OpenAI `usage.prompt_tokens`/`completion_tokens`; Anthropic `usage.input_tokens`/
  `output_tokens`); `durationMs` is wall-clock measured by the backend;
  `tokensPerSecond` derived. Missing values stay null, never invented.
- **Refusals**: an Anthropic `stop_reason: "refusal"` (or an OpenAI content-filter finish)
  returns the honest 502-family error naming the reason category, not an empty draft.

### FR-4: OpenAI embedding backend

A new branch in `EmbeddingBackendFactory` (`Embedding/EmbeddingBackendFactory.cs:45`)
returning an `IEmbeddingGenerator<String, Embedding<Single>>` over `POST /v1/embeddings`.
It obeys the existing provider contract: never truncate (inputs over the model's token
ceiling are refused, not half-embedded), `MaxBatchSize` chunks requests, dimension is
validated against `Fallen8:Embedding:Dimension` and a mismatch is a hard error. The
identity stamp rules are unchanged: switching to OpenAI means the operator sets
`ModelName`/`Dimension`/`IntendedMetric` to the new function's identity, and existing
vectors/indices report identity mismatch rather than silently mixing spaces (R3 is why
`Embedding:Backend` stays never-writable).

### FR-5: Provenance on the wire

1. `ChatResultREST` gains `backend` (string: the configured selector value, e.g. `"Nahil"`),
   set by the controller from the provider (`ChatController.cs:150-161`). `stats.raw` in
   Studio therefore carries it too.
2. `/status` and `/config` report `model` for ALL chat backends: today
   `Fallen8ChatProvider.Model => ProbeTarget?.Model` (`Fallen8ChatProvider.cs:64`) returns
   null for any backend without an `OllamaConnection`, so an OpenAI deployment would show
   `model: null`. The provider's reported model comes from options, not from the probe
   target.
3. Residency stays honest: OpenAI/Anthropic have no `/api/ps` analogue, so
   `ResolveConnection` returns null, the probe is skipped (`AdminController.cs:412-424`
   already handles this), and `resident`/`gpu` stay null ("unknown"). `loaded` keeps its
   existing lazy-construction meaning.
4. `/path` and `/subgraph` responses: when `semantic.queryText` was embedded for the
   request, the semantic summary echoes `embeddingBackend` and the identity stamp beside
   the fields it already echoes (`SubGraphSemanticSummary.cs:49-93`). Vector-in requests
   (no embed call) echo nothing new.

### FR-6: Provenance in Studio

Per decision 5, per-call displays read the response's `backend`; ambient labels read
`useStatus().chat` (already polled by every screen and rendered nowhere,
`src/api/types.ts:225-227`).

1. **NL panels' status line** (`NlAssistPanel.tsx:195-207`,
   `PluginNlAssistPanel.tsx:211-222`): the instance branch becomes live -
   `this instance · /chat → {backend} · {model}` - replacing the hardcoded
   `local → /chat (server-selected model)` and the stale `(default phi4-f8-mini …)` hint
   text (`NlBackendConfig.tsx:75-81`).
2. **Draft stats**: `statsLine` (`NlAssistPanel.tsx:327-336`) gains the backend
   (`… · 130.8 tok/s · Nahil`); the raw-stats JSON carries `backend` via the response.
   Stored per attempt, so later config changes do not rewrite history.
3. **PluginNlAssistPanel renders stats** - it already captures them and shows nothing
   (`PluginNlAssistPanel.tsx:72,134` vs `:288-296`); this asymmetry ends here.
4. **Configuration cards** stay the backend truth source; no change beyond the widened
   enum flowing through, plus the stale XML doc on `EmbeddingProviderStatsREST` ("Onnx,
   LLamaSharp or Ollama", `GraphStatisticsREST.cs:366`) finally naming all backends.
5. **SemanticQueryEditor** (`src/components/SemanticQueryEditor.tsx`) - the single edit
   point both Traverse tabs share - gains one line when text-in embedding is available:
   `embeds on this instance via {backend} · {stamp}`. The Query screen's semantic search
   gets the same label. Weak-label surfaces from the survey (Canvas find-similar,
   Integrations notices, Knowledge banners) are explicitly out of scope for this pass.

### FR-7: Central deployment selector

`F8_MODEL_PROVIDER = local | nahil | openai | anthropic` (default `local`), read by
`env-up.js` beside the existing Nahil detection (which stays: `F8_NAHIL_API_KEY` alone still
selects Nahil, backward compatible). New overlays `docker-compose.openai.yml` /
`docker-compose.anthropic.yml` follow the Nahil overlay's template: chat backend + endpoint
+ `${F8_OPENAI_API_KEY:?...}` / `${F8_ANTHROPIC_API_KEY:?...}` failing closed, model
defaulting per decision 8. Unlike the Nahil overlay they do NOT park the sidecar: it keeps
running for embeddings and pulls only `bge-m3` (the chat fine-tune pulls are skipped via the
sidecar's existing env). `.env.example` gains the provider block. This FR builds on the
`.env`-reading env scripts (feature nahil-env-file, merged 2026-08-29).

### FR-8: Catalog, tiers, and the test lattice

Ten new keys enter `Fallen8SettingCatalog` with the tiers in FR-2. Known consequences,
enumerated so nobody discovers them in CI: `SettingCatalogTest` (reflection equality,
per-key rules R3/R4/R8), `ConfigSettingsEndpointTest` (withheld-key list, `allowedValues`),
`ConfigWriteEndpointTest` (enum case), and the web UI's `config-catalog.test.ts`, which pins
EXACT section counts (`chat 9`, `embedding 21`) and exact sub-group label multisets - both
change (`chat 16`, `embedding 24`, new `OpenAI`/`Anthropic` groups).

### FR-9: MCP surface

No new tools and no change to the `/chat` deferral (agents bring their own model). Two
additions ride existing tools: the new settings flow through the already-bridged
`f8_admin get_settings`/`set_settings` automatically, and `f8_overview` gains
`chatBackend`/`embeddingBackend` strings beside its existing booleans
(`fallen-8-mcp/Tools/OverviewTool.cs:196-197`), so an agent can tell where a prompt would
go before sending one. `McpRestCoverageTest` is unaffected (no new routes).

### FR-10: Docs and diagrams

New docs page `model-providers.md`: the central story (choose a provider, where the key
lives, what moves and what deliberately does not, provenance). `nahil.md` becomes the
Nahil deep-dive it already is, linked from the new page. Updates: `running.mdx` (env table +
`F8_MODEL_PROVIDER`), `nl-assist.md`, `configuration.md`, `troubleshooting.md`, README "Key
features" line, and - since two new external channels appear - **both** architecture
diagrams (root `README.md` and `architecture.md`, house style, never mermaid defaults).

## Impact on existing features

- **nahil-backend**: untouched at runtime; `NahilWarmupRetryHandler` may be generalized
  (rename or a shared base), its tests carried along. The nahil docs page loses its
  "the settings underneath" uniqueness claim to the new central page's cross-link.
- **embedding-provider / element-embeddings**: identity stamp semantics unchanged and
  re-asserted (decision 3, FR-4). No re-embed machinery is added.
- **instance-config / configuration-surface**: FR-8's full test lattice.
- **REST contract / OpenAPI snapshot**: `ChatResultREST.backend` + semantic summary fields;
  regenerate, additions only. `ChatEndpointTest.cs:150-154` pins the response shape and
  updates with it.
- **Studio**: the FR-6 components; screenshots to recapture: `screen-delegate-editor.png`,
  `screen-configuration.png`, `screen-connect.png` (and `screen-nl-assist.png` if the
  status line is visible there).
- **NL-assist dataset/eval**: no retrain, prompts unchanged (decision 9). No RETRAIN-LOG
  entry.
- **nahil-env-file**: FR-7 builds on it (merged to main 2026-08-29); the provider keys are
  exactly the kind of value that file exists for.
- **MCP**: FR-9 only; `McpContractTest` pins routes/methods, which do not change.
- **Browser probe / engine / persistence / integrations / provider descriptors / stored
  queries**: untouched. Everything lands in `fallen-8-core-apiApp`, `fallen-8-mcp` (one
  tool payload), `fallen-8-web-ui`, compose/scripts, and docs.

## Out of scope

- Anthropic embeddings (no such API; revisit only if Anthropic ships one).
- Azure OpenAI / OpenAI-compatible gateways as tested targets (the endpoint is configurable,
  a host root is a host root; nothing beyond that is promised or tested).
- Automatic re-embedding or migration when the embedding function changes (R3 stands;
  failures are explicit, re-runs are manual and documented).
- Keys in Studio, in any form (decision 4).
- Multi-backend failover or per-request backend selection on `/chat` (one backend per
  capability per deployment, same as Nahil).
- Provenance for the NLP and docling sidecars (different pipeline, not a `Backend`).
- Changing Studio's browser-direct custom mode (it stays user-owned; the docs will say the
  server-side path is the better home for a provider key).
- Cost/pricing display of any kind (token counts stay the only spend signal).
