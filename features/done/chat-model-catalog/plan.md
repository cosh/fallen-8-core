# Plan: Chat model catalog

Historical sequencing note for [spec.md](spec.md). Branch: `feature/chat-model-catalog`.

## Phase 1: server catalog read

1. `fallen-8-core-apiApp/Chat/ChatModelCatalog.cs`: static helper beside `OllamaModelProbe`,
   same rules (transient transport, single 5 s budget owned here, caller cancellation
   propagates, everything else swallowed per entry, never touches the lazy backend).
   - Ollama-protocol arm takes an `OllamaConnection` (from
     `ChatBackendFactory.ResolveConnection`): `GET /api/tags`, concurrent `POST /api/show` per
     model; per-entry degradation to nulls on a failed show.
   - Remote arm takes a `RemoteModelTarget` (from `ChatBackendFactory.ResolveRemoteTarget`):
     `GET /v1/models`, OpenAI bearer vs Anthropic `x-api-key` + `anthropic-version` +
     `limit=1000`.
   - Test-injectable `HttpMessageHandler`, like the probe.
2. `ChatModelsREST` response model in `Controllers/Model/` (XML-doc'd; `backend`, `models[]`
   with `name`/`capability`/`available`/`class`).
3. `GET /chat/models` action on `ChatController` (inherits gate; adds sensitive rate limit;
   `503` via `ChatBackendFactory.Validate` reason when the backend cannot be used, `503` when
   the catalog read fails wholesale, `200` otherwise).
4. Tests (`fallen-8-unittest/ChatModelCatalogTest.cs` + endpoint additions):
   - tags + show fan-out shape; ordinal sort; per-entry show-failure degradation
   - bearer on every Nahil call including show; no Authorization header on a sidecar call
   - budget: a hung backend answers within the bound, cancellation propagates
   - OpenAI/Anthropic request shape (headers, limit) and mapping; Anthropic `id` as `name`
   - endpoint: 403 chat off, 503 misconfigured backend naming no endpoint value, 200 shape
5. Gates: `powershell -File scripts/update-openapi-snapshot.ps1` (additions only), then the
   `McpRestCoverageTest` deferral entry for `GET /chat/models`.

## Phase 2: Studio picker

1. `src/api/types.ts` + `endpoints.ts`: `ChatModelsREST` type, `getChatModels(instance)`.
2. `SettingRow`: optional `suggestions?: { value: string; label?: string }[]`; kind `string`
   with suggestions renders `<input list>` + `<datalist>`; absent suggestions changes nothing.
3. `ConfigurationSurface`: when the Chat section is open, chat enabled, config write on, and
   the active backend's model row editable, fetch once and pass filtered suggestions
   (exclude `capability === "embedding"`, keep unknowns) to exactly that row; non-200 renders
   the one-line unavailable caption.
4. Vitest: suggestions render for the active backend's row only; embedding rows never;
   free text preserved and unvalidated; fetch skipped when section closed / chat off / row
   env-declared; degradation caption on failure.

## Phase 3: docs and bookkeeping

1. `docs/src/content/docs/model-providers.md`: the single-home section (what the picker is,
   the running-backend rule, the free-text guarantee and why, the `f8-delegate` example).
2. One-line pointers: `configuration.md` (beside the writable-settings story), `nahil.md`
   ("Model names" section).
3. Docs build green (`npm --prefix docs ci && npm --prefix docs run build`).
4. Move `features/open/chat-model-catalog/` to `features/done/` with as-built deviations.

Full suite + review gate before merge. No browser-probe run needed (no engine change, nothing
`HostCapabilities`-gated is touched).
