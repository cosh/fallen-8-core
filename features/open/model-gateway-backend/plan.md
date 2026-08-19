# Plan: Model gateway backend

Spec: [spec.md](spec.md). Branch: `feature/model-gateway-backend` (code never lands on
`main` directly). Phases are ordered so every phase leaves `main`-mergeable behaviour: the
local Ollama path stays bit-identical until an operator flips `Backend=Gateway`.

Construction sites (verified 2026-08-20): `Helper/OllamaHttpClientFactory.cs` (the one
transport builder; the deadline rule lives there and must survive every phase),
`Chat/OllamaChatBackend.cs`, `Chat/OllamaModelProbe.cs` (+ its caller in
`Controllers/AdminController.cs`), `Embedding/EmbeddingBackendFactory.cs`,
`Configuration/Fallen8ChatOptions.cs`, `Configuration/Fallen8EmbeddingOptions.cs`,
`Configuration/Fallen8SettingCatalog.cs`, `Controllers/Model/ChatREST.cs`,
`Controllers/ChatController.cs`.

## Phase 1: pin explicit tags (config only, safe now)

- `docker-compose.yml`: `Fallen8__Chat__Ollama__Model=phi4-f8-mini:latest`,
  `Fallen8__Embedding__Ollama__Model=bge-m3:latest`. `Fallen8__Embedding__ModelName` stays
  `bge-m3` (spec decision 5, identity stamp).
- Same tags on the two C# option defaults; sweep docs (`running.mdx`, `nl-assist.md`) for the
  bare names used as request identifiers and pin them; leave Studio custom-mode presets and
  training-pipeline names bare (inventory the hits in the PR description).
- Gate: full suite (`OllamaModelProbe.ModelMatches` already tolerates tags; the probe test
  must stay green), then a live compose check that chat + embedding still answer and the
  outbound bodies carry the tagged names verbatim.

## Phase 2: endpoint host-root validation

- Fail fast at startup when an enabled provider's endpoint is not an absolute URI with
  `AbsolutePath == "/"`; the message names the key and says WHY (BaseAddress drops path
  prefixes); never rewrite the URL. Applies to `*:Ollama:Endpoint` now, `*:Gateway:Endpoint`
  in phase 3.
- Confirm request URIs stay relative and scheme-agnostic (https needs no other change).
- Tests: pathful endpoint fails with the host-root message; host-root https value starts
  cleanly; disabled provider with a garbage endpoint still starts (capability off = inert).

## Phase 3: the Gateway backend

- Options: `GatewayOptions { Endpoint, ApiKey, Model }` on both option classes; factory cases
  for `Backend=Gateway` reusing `OllamaHttpClientFactory` plus
  `DefaultRequestHeaders.Authorization = Bearer <key>`; widen the two `Backend` selectors.
- Startup validation per spec FR-2 (endpoint host-root, ApiKey and Model non-blank when
  selected).
- Probe: `AdminController`'s residency probes and `Fallen8ChatProvider`'s
  endpoint/model surface become backend-aware so `/api/ps` goes to the right host WITH the
  key (an unauthenticated probe 401s and reads as permanent "unknown").
- Catalog: the six new entries at the tiers in spec FR-2, reasons in the style of the
  existing R3/R4 texts.
- Redaction audit: no log, error, or options dump prints the key; only "set / not set".
- Tests: header present with scheme+value under Gateway, absent under Ollama; distinct chat
  vs embedding keys; catalog equivalence picks up the new keys;
  `GetConfig_WithholdsTheValueOfEveryNeverWritableKey` covers both ApiKey keys and both
  Gateway endpoints; backend factory throws on unknown values unchanged.

## Phase 4: retry on 503/429 (gateway handler chain only)

- A `DelegatingHandler` composed in `OllamaHttpClientFactory` only for gateway clients:
  Retry-After (delta-seconds and HTTP-date), fallback exponential backoff with jitter (2 s
  start, 30 s cap), 60 s per-wait clamp (constant), waits on the caller's token, one
  info-level line per retry, 503 and 429 distinguishable in logs and in the surfaced error
  (warming vs rate-limited), no other status retried. No new package (no Polly; the handler
  is ~a screen of code and the repo has no existing resilience dependency).
- The handler owns NO deadline (deadline rule): budget exhaustion arrives as the caller's
  linked-token cancellation mid-wait; enrich the existing 504/503 messages with model name,
  last-seen status and total time waited.
- Tests (stub `HttpMessageHandler`): 503+Retry-After:5 twice then 200 succeeds after ~10 s
  (virtual time), HTTP-date form, missing header backoff, hostile Retry-After clamped,
  cancellation during a wait propagates, 429 wording differs from 503, a 400 is not retried.
- Convention test pinning that no product code calls `/api/pull|create|delete|copy`.

## Phase 5: chat streaming

- `Stream = true` in `OllamaChatBackend` behind `Fallen8:Chat:Stream` (Bool, Restart,
  default true, catalogued). Buffering loop stays; stats keep coming from the done-chunk.
- Mid-stream failure: no done-chunk or an error chunk maps to the 502 output-error path
  naming the partial-content length; never a truncated success.
- Tests over canned chunk sequences: normal completion with stats, chunk with missing
  fields, truncated stream, error chunk. `POST /chat` response shape unchanged
  (`ChatEndpointTest` stays green untouched).

## Phase 6: embedding hardening

- Order-preservation test: 100 items over `MaxBatchSize=32` on the ingestion path issue four
  capped requests and return 100 vectors in input order.
- Dimension-mismatch test pinning the latched message (behaviour already implemented).
- Verify the failure report on a mid-run 429/503 exhaustion names the un-embedded remainder;
  tighten the message if it does not.

## Phase 7: per-request stop tokens

- `stop` array on `ChatOptionsSpecification` -> `ChatBackendOptions.Stop` ->
  `RequestOptions.Stop`, merged with the existing temperature mapping.
- Regenerate the OpenAPI snapshot (`scripts/update-openapi-snapshot.ps1`); additions only.
- MCP sweep: no route change; decide surface-or-defer for `options.stop` on the bridged chat
  tool and record it.
- Docs: the 14.7B (`phi4-f8`) caveat where the variant is offered: the published build has no
  ChatML template or stop tokens baked in, so a gateway deployment of that tier passes
  `options.stop=["<|im_start|>","<|im_end|>"]` per request and prefers `/api/chat`.
- Tests: stop tokens present in the outbound request when supplied, absent otherwise; merge
  does not drop temperature.

## Phase 8: gateway deployment profile + docs

- `docker-compose.gateway.yml`: no `ollama` service, `Backend=Gateway` both providers,
  endpoint/key/model from env (`F8_GATEWAY_URL`, `F8_GATEWAY_API_KEY`,
  `F8_GATEWAY_CHAT_MODEL`, `F8_GATEWAY_EMBED_MODEL=bge-m3:latest`),
  `Fallen8__Chat__TimeoutSeconds=600`, `Fallen8__Embedding__MaxBatchSize=32`. Local compose
  untouched.
- Docs: new site page (gateway backend: settings, auth, warming/503 semantics, budgets),
  README "Key features" line, `running.mdx` + `troubleshooting.md` + configuration page
  updates, `architecture.md` prose (model backend = local sidecar OR remote gateway; diagram
  label only if it hard-names Ollama).
- Recapture the Studio Configuration screenshots (the panel renders the catalog, which grew).
- Gate: docs build link-checked; screenshots per the capture pipeline notes; full suite.

## Phase 9: published chat model name (coordinated, LAST)

- Only when the platform catalog is live: default `F8_GATEWAY_CHAT_MODEL=f8-delegate:latest`
  in the gateway profile; docs note that `phi4-f8-mini` == `f8-delegate` (digest
  `6d4bd13b...`), so behaviour is expected identical.
- Repo-wide `phi4-f8-mini` / `phi4-f8` sweep with a per-hit decision list (renamed /
  local-dev default / training-side / irrelevant) in the PR.
- Verify NL-to-lambda output shape parity against the gateway; on drift, stop and report
  upstream, do not adjust prompts to compensate.
- Revert path: configuration only.

## Standing gates (every phase)

`dotnet build` + `dotnet test fallen-8-core.sln` (warnings are errors; never `-v q`);
OpenAPI snapshot regen whenever `ChatREST`/controller docs change; docs build when docs
change; no engine change is planned, so the browser probe is not expected to be needed, but
run it if anything under `fallen-8-core/` is touched after all.
