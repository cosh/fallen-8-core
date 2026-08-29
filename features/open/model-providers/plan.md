# Plan: Model providers (central backend selection + provenance)

Implements [spec.md](spec.md). Branch: `feature/model-providers` from `main`. Implementation
is intended for Opus; the spec's file:line references were verified against the working tree
on 2026-08-29 and are the starting points, not gospel - re-verify before editing.

Standing constraints for every phase: warnings are errors; exact package versions; MIT
headers; the credential never appears in any log/error/exception message; the caller's
`TimeoutSeconds` stays the single deadline; no em dashes in any text.

Implementation notes for the Anthropic transport: invoke the `claude-api` skill (C#) for the
current SDK bindings before writing code - the Messages API surface moved in 2025/2026
(adaptive thinking default, sampling parameters rejected on current models, `max_tokens`
required). Do not send `temperature`/`top_p`/`top_k`; map `stop` to `stop_sequences`; read
usage from `usage.input_tokens`/`usage.output_tokens`.

## Phase 1: options, catalog, validation (server, no transports yet)

- `Fallen8ChatOptions`: `OpenAIOptions` (Endpoint/ApiKey/Model), `AnthropicOptions`
  (Endpoint/ApiKey/Model/MaxTokens); `Fallen8EmbeddingOptions`: `OpenAIOptions`.
- Widen `Backend` docs + catalog `allowedValues`; ten new `Fallen8SettingCatalog` entries
  with tiers per spec FR-2 (R4 endpoints, R8 keys, R3 embedding model, Restart chat models
  + MaxTokens with bounds).
- A connection/validation equivalent for non-Ollama providers (host-root rule, required
  key + model, no endpoint quoted in errors). Decide here whether `OllamaConnection` grows
  a sibling or a small `RemoteModelConnection` abstraction replaces the
  `Fallen8ChatProvider.ProbeTarget` typing (spec FR-5.2 needs the Model leak fixed either
  way).
- `Fallen8:Embedding:Backend=Anthropic` refused with its own sentence.
- Tests: `SettingCatalogTest` (reflection equality + per-key rules),
  `ConfigSettingsEndpointTest` (withheld list + allowedValues), `ConfigWriteEndpointTest`
  (enum case), web-ui `config-catalog.test.ts` (section counts chat 16 / embedding 24, new
  sub-group labels).

## Phase 2: chat transports

- `ChatBackendFactory.Create` switches per backend; `OpenAIChatBackend` and
  `AnthropicChatBackend : IChatBackend`, official SDKs wrapped like OllamaSharp is, raw-HTTP
  fallback only if an SDK cannot honor the transport contract (record the reason here).
- Retry: generalize the 429/Retry-After discipline (Nahil keeps its 503-warming); Anthropic
  adds 529. Same clamps, one log line per wait, never the credential.
- Streaming per `Fallen8:Chat:Stream`, truncation detectable as 502.
- Stats mapping per FR-3; refusal/content-filter surfaced honestly.
- Tests: handler-seam unit tests mirroring `NahilTransportTest` (auth header, retry, budget
  exhaustion, credential never logged, endpoint never quoted); opt-in live smoke tests
  keyed off `F8_OPENAI_API_KEY`/`F8_ANTHROPIC_API_KEY` env vars, following the Nahil smoke
  precedent (commit 6dde4501).

## Phase 3: OpenAI embedding backend

- New `EmbeddingBackendFactory` branch returning an `IEmbeddingGenerator`; never-truncate,
  batch chunking, dimension validation per FR-4.
- Tests: unit with handler seam; smoke opt-in; identity-mismatch behaviour re-pinned.

## Phase 4: provenance on the wire

- `ChatResultREST.backend`; `Fallen8ChatProvider.Model` from options (the ProbeTarget null
  leak); `/path`+`/subgraph` semantic summary echoes `embeddingBackend` + stamp when
  queryText was embedded.
- Regenerate the OpenAPI snapshot (`scripts/update-openapi-snapshot.ps1`); additions only.
- `f8_overview` gains `chatBackend`/`embeddingBackend` (fallen-8-mcp).
- Tests: `ChatEndpointTest` response shape; semantic summary tests; MCP overview test.

## Phase 5: Studio

- NL panels: live status line from `useStatus().chat`; `statsLine` + raw stats carry the
  response's `backend`; `PluginNlAssistPanel` renders stats; hint text in `NlBackendConfig`
  rewritten (no hardcoded model claim).
- `SemanticQueryEditor` + Query screen embedding label.
- Tests: vitest for each surface (note the delegate-editor 5s-timeout flake baseline before
  blaming a change); `config-catalog.test.ts` already updated in Phase 1.
- Recapture screenshots: `screen-delegate-editor.png`, `screen-configuration.png`,
  `screen-connect.png`, check `screen-nl-assist.png` (docs-screenshot-capture procedure).

## Phase 6: deployment + docs

- `docker-compose.openai.yml` / `docker-compose.anthropic.yml` (sidecar keeps running,
  embeddings stay local, chat fine-tune pulls skipped); `env-up.js` reads
  `F8_MODEL_PROVIDER` (Nahil key-presence selection stays); `.env.example` provider block.
- Docs: new `model-providers.md` page + sidebar entry; updates to `running.mdx`,
  `nl-assist.md`, `nahil.md` (cross-link), `configuration.md`, `troubleshooting.md`;
  README key-features line; BOTH architecture diagrams (README + architecture.md).
- `node --check` on touched scripts (env-up.js executes compose on require - never run it
  to test).

## Gates, to run before merge

- `dotnet test fallen-8-core.sln` (full suite).
- Web UI test suite (vitest, via the cmd exit-code wrapper).
- OpenAPI snapshot diff reviewed (additions only).
- Docs build: `npm --prefix docs ci && npm --prefix docs run build` (link-checked).
- Screenshot recapture per Phase 5.
- Forbidden-strings grep over the full branch diff before merge.
- Browser probe: not required (no engine change), note it explicitly in the PR/merge notes.
- Live smokes: run once with real keys if available; they stay opt-in in CI.

## Outstanding decisions deliberately left to implementation

- SDK vs raw HTTP per provider (decision 6's escape hatch) - decide against the actual
  SDK surfaces, record here.
- Whether `NahilWarmupRetryHandler` generalizes (shared base) or gets siblings - pick
  whichever keeps the Nahil tests untouched.
