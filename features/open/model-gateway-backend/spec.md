# Spec: Model gateway backend

**Status: Open. Spec and plan only, nothing implemented. Drafted 2026-08-20 from an external
change-request list, every claim of which was verified against the code the same day (see the
verification record below).**

## Why

Fallen-8's chat and embedding providers speak the Ollama HTTP API to a local sidecar
(`http://ollama:11434` in the compose stack). A remote, authenticated, Ollama-compatible
inference gateway (a hosted platform that routes requests to remote GPU/CPU workers) becomes a
SECOND place to run the same models. The two coexist per deployment for a long time and
**Ollama stays the default**; a deployment opts into the gateway by configuration only.

The gateway is wire-compatible with Ollama (`/api/chat`, `/api/embed`, `/api/tags`,
`/api/version`, `/api/ps` take unchanged bodies), with these deltas, which are the whole
feature:

1. Every route requires `Authorization: Bearer <key>`. Real Ollama has no auth.
2. A request for a catalogued model not yet resident on a worker answers `503` plus a real
   `Retry-After` header while the platform pulls the model in the background. A multi-GB pull
   takes minutes.
3. A per-API-key hourly token budget answers `429` (matters for bulk embedding).
4. A per-request embedding batch cap of 64 items.
5. Workers include CPU-only machines (measured ~5 s/token), and a non-streamed response
   carries a verification inference in front of the reply. Streamed responses verify after
   delivery.
6. The catalog serves the same fine-tunes under their published registry names: the chat
   model as `f8-delegate:latest` (content digest
   `6d4bd13b1fda5a118af62702e7ae5aca0f89c5fb71dc693337429bf075280f1a`, byte-identical to the
   local `phi4-f8-mini`), and `bge-m3` at 1024 dimensions. `Fallen8:Embedding:Dimension`,
   `IntendedMetric` and all stored vectors stay valid; nothing re-embeds.

## Decisions (operator, 2026-08-20)

1. **Second backend value, not a mutated Ollama backend.** `Gateway` joins the `Backend`
   selectors. It reuses the same OllamaSharp client plumbing internally, but auth, the
   503-warmup retry and 429 handling exist ONLY on the gateway backend. `Backend=Ollama`
   behaviour stays bit-identical; no implicit semantics keyed off "is an ApiKey set".
2. **Generic naming.** The repo says "model gateway" / `Gateway` everywhere: config keys,
   enum values, docs, this directory. The feature honestly serves any authenticated
   Ollama-compatible endpoint; the concrete platform URL lives only in the operator's
   deployment env.
3. **`POST /chat` stays buffered.** Streaming is turned on at the backend seam (the gateway
   then verifies after delivery instead of in front of it, and chunks arrive incrementally),
   but the REST response shape is unchanged and `IChatBackend` keeps its single buffered
   method. An SSE/chunked REST variant is deferred until Studio renders deltas; today no
   caller would consume it.
4. **Constants over knobs for retry.** The caller's existing budget
   (`Fallen8:Chat:TimeoutSeconds` / `Fallen8:Embedding:TimeoutSeconds`) stays the SINGLE
   authoritative deadline (the deadline rule on `OllamaHttpClientFactory` is preserved:
   retry waits run on the linked token and consume that budget). There is no separate
   retry-budget knob: a wall-clock retry budget beyond the caller's deadline could never be
   reached, and one below it would just move the honest 504/503 earlier. The per-wait clamp
   against a hostile `Retry-After` is a documented constant (60 s), not a setting.
5. **`Fallen8:Embedding:ModelName` stays `bge-m3`, untagged.** It is the identity stamp
   (feature embedding-provider FR-8) compared against stamps stored beside existing vectors,
   and is never sent to Ollama; retagging it would make every existing index identity-mismatch
   for zero wire benefit. Only `*:Ollama:Model` / `*:Gateway:Model` are request identifiers.
6. **The local "no f8-delegate alias" decision (delegate-model-variants, 2026-07-20)
   stands.** It governs local artifact naming. The gateway's catalog name for the same digest
   is the published registry name, so a gateway deployment configures
   `Fallen8:Chat:Gateway:Model=f8-delegate:latest`; local profiles keep `phi4-f8-mini`.

## Requirements

### FR-1: Backend selector

`Fallen8:Chat:Backend` accepts `Ollama | Gateway`; `Fallen8:Embedding:Backend` accepts
`Onnx | LLamaSharp | Ollama | Gateway`. The catalog's `allowedValues` grow accordingly (the
backend factories match ordinally and throw otherwise; that throw latches as a permanent 503,
so the accepted set stays load-bearing). `EmbeddingBackendFactory` already documents "an
OpenAI-compatible remote backend" as its extension point; this is that case, minus the
protocol change.

### FR-2: Gateway options and startup validation

New sections mirroring the Ollama ones:

```
Fallen8:Chat:Gateway:Endpoint        NotWritable (R4, same SSRF reason as the Ollama endpoints)
Fallen8:Chat:Gateway:ApiKey          NotWritable (credential: a written value redirects spend,
                                     a published value leaks it; the withholding rule keeps it
                                     off the /config read surface)
Fallen8:Chat:Gateway:Model           Restart (mirrors Fallen8:Chat:Ollama:Model)
Fallen8:Embedding:Gateway:Endpoint   NotWritable (R4)
Fallen8:Embedding:Gateway:ApiKey     NotWritable (credential, as above)
Fallen8:Embedding:Gateway:Model      NotWritable (R3: the model IS the embedding function,
                                     mirrors Fallen8:Embedding:Ollama:Model)
```

Validation, fail-fast at startup when the corresponding provider is enabled with
`Backend=Gateway`: `Endpoint` parses as an absolute URI whose `AbsolutePath` is `/` (a host
root; `HttpClient.BaseAddress` silently drops a path prefix, so a pathful URL must be an
error, never rewritten), `ApiKey` is non-blank, `Model` is non-blank. The same host-root
validation is applied to the `Ollama` endpoints (a benign tightening; the shipped values are
host roots). HTTPS is expected for remote gateways and documented, not enforced (a
LAN-internal http gateway is the operator's call). **No certificate-validation bypass exists
today (verified) and none is added.**

### FR-3: Bearer auth on every gateway call, key never logged

All THREE Ollama-protocol call sites carry the key when talking to the gateway: the chat
backend, the embedding client, and the transient residency probe (`OllamaModelProbe`, built
per call by `AdminController` for `GET /config`; the gateway authenticates `/api/ps` too, and
an unauthenticated probe would 401, be swallowed to `null`, and show residency "unknown"
forever with nothing in the logs saying why). Chat and embedding carry independently
configured keys. The header is set once on the constructed `HttpClient`
(`DefaultRequestHeaders.Authorization`); no call site formats it. The key value never appears
in any log line, error message, exception `ToString`, or diagnostic; anything that prints
resolved options prints only whether the key is set.

### FR-4: Bounded retry on 503 and 429, gateway backend only

A `DelegatingHandler` inside the gateway clients' handler chain (composed in
`OllamaHttpClientFactory`; the plain Ollama path gets no handler and is byte-for-byte
unchanged):

- On `503`/`429`: read `Retry-After` (both delta-seconds and HTTP-date forms), wait, retry
  the identical request. Missing/unparseable header: exponential backoff with jitter from
  2 s, capped at 30 s per wait.
- Each individual wait is clamped to 60 s (constant, decision 4).
- Waits honour the caller's `CancellationToken`; the caller's `TimeoutSeconds` budget is the
  total bound (decision 4). When it expires mid-wait the existing 504 (chat) / 503
  (embedding) paths fire, and the retry state enriches the error: the model name, that the
  backend kept answering 503-warming (or 429-rate-limited; the two stay distinguishable in
  logs and errors because they mean different things), and the total time waited.
- One information-level log line per retry (model, wait, attempt, elapsed); no per-poll spam.
- No other 4xx is retried. Chat and embedding calls are safe to repeat (verified: both are
  pure proxies with no server-side state written before success).
- **`/api/pull`, `/api/create`, `/api/delete`, `/api/copy` are never called by product code
  today (verified: the only `ollama pull` sites are the local compose provisioning scripts
  `scripts/ollama-init.sh` and `scripts/ensure-models.sh`, which run inside/against the local
  sidecar container and are absent from a gateway deployment). No gating code is needed; a
  convention test pins that no product code path calls those routes.**

### FR-5: Chat streaming at the backend seam

`OllamaChatBackend` sets `Stream = true` (the existing `await foreach` accumulation already
consumes chunks; the terminal done-chunk keeps supplying the stats). `Fallen8:Chat:Stream`
(Bool, Restart tier, default `true`, catalogued) is the escape hatch back to the non-streamed
request shape. Mid-stream failure is explicit: a transport drop or an error chunk before the
done-chunk maps to the existing 502 output-error path, naming how many characters of partial
content were received; a truncated stream must never return as a complete-looking short
answer. Applies to both Ollama-protocol backends (same client code); on local Ollama the
observable result is unchanged.

### FR-6: Embedding path hardening

- The batch cap is already enforced on every path (verified: `EmbeddingController` rejects
  oversized batches with 400 on both embed routes; `DocumentIngestionService` chunks by
  `MaxBatchSize`; the semantic query path embeds one text). A test pins order preservation
  across ingestion chunking (100 items at cap 32 produce four capped requests and 100 vectors
  in input order).
- `429` retry comes from FR-4. On budget exhaustion mid-ingestion the failure is explicit and
  names what was not embedded; ingestion already fails the document loudly rather than
  storing partial silence (re-running the job is the resume story; job-level checkpointing is
  out of scope).
- Dimension validation already exists and latches (verified,
  `Fallen8EmbeddingProvider.EmbedAsync`: wrong width throws and latches the provider, output
  is never truncated or padded). A test pins the mismatch message.
- The gateway deployment profile sets `Fallen8:Embedding:MaxBatchSize=32` (the gateway's own
  per-request cap is 64; the shipped default sits exactly at the limit, 32 leaves headroom).

### FR-7: Per-request stop tokens

`ChatSpecification.options` grows an optional `stop` string array beside `temperature`,
flowing through `ChatBackendOptions` into OllamaSharp's `RequestOptions.Stop`, merged with
(not overwriting) whatever options are already sent. Rationale: the runtime NL assist already
sends its per-kind system prompt and temperature with every request (the baked-in `SYSTEM` in
`nl-assist-finetune/train/Modelfile.template` is fallback only, and is already committed), so
stop tokens travel the same per-request road. This is the enabler for the 14.7B tier
(`phi4-f8`), whose published registry build carries neither the ChatML template nor the
`<|im_start|>`/`<|im_end|>` stop tokens of a locally built image. **The server never defaults
to the 14.7B tier (verified: `phi4-f8-mini` is the shipped default, `phi4-f8` is an explicit
operator/Studio-custom choice), so no server-side prompt or stop configuration is added; the
caveat is documented where the 14.7B variant is offered.**

### FR-8: Explicit `:latest` tags on request-bound model names

`docker-compose.yml` env and the C# option defaults pin `phi4-f8-mini:latest` /
`bge-m3:latest` for the `*:Ollama:Model` keys. No code strips, appends or normalizes a tag on
the way to the request body (verified: the only tag handling is `OllamaModelProbe`'s
comparison tolerance, which stays). `Fallen8:Embedding:ModelName` stays untagged
(decision 5). The Studio custom-mode presets keep bare names (browser-direct, user-owned;
bare names resolve as `:latest` on both local Ollama and the gateway).

### FR-9: Gateway deployment profile

A compose override (`docker-compose.gateway.yml`) plus docs: no Ollama sidecar and no model
provisioning, `Backend=Gateway` for chat and embedding, endpoint/key/model from operator env
vars, `Fallen8:Chat:TimeoutSeconds=600` (CPU workers plus warm-up waits; the value is also the
ceiling on FR-4 waiting), `Fallen8:Embedding:TimeoutSeconds` stays 300 unless bulk runs show
exhaustion (a cold `bge-m3` pull can exceed it; the error is honest and the next call
succeeds), `MaxBatchSize=32`. The local compose profile is untouched. The 504 path keeps
naming the elapsed budget so a slow backend stays distinguishable from a broken one
(verified: it does today).

### FR-10: Chat model under its published name (coordinated, last)

The gateway profile configures `Fallen8:Chat:Gateway:Model=f8-delegate:latest` (decision 6).
Lands only when the platform catalog is live; revertible by configuration alone. The docs
record that `phi4-f8-mini` and `f8-delegate` are the same weights under two names (digest
above) so nobody later "fixes" the name back or pulls upstream `phi4-f8-mini` expecting the
fine-tune. After the switch, a natural-language-to-lambda request must produce C# of the same
shape as before (the baked-in system prompt travels with the image); if it does not, stop and
report upstream rather than papering over it with prompt changes. A repo-wide
`phi4-f8-mini`/`phi4-f8` sweep records renamed / left-as-local / irrelevant per hit (training
pipeline names are training-side and stay).

## Verification record (change requests vs code, 2026-08-20)

| # | Request claim | Finding |
|---|---|---|
| 1 | Tag pinning; check for tag normalization | Correct; no normalization exists. `ModelName` conditional resolved: identity stamp only, stays untagged (decision 5). |
| 2 | Endpoint config, host-root, no cert bypass | Endpoints are config-bound with benign C# defaults (`localhost:11434`, capabilities default OFF, so nothing dials unless enabled; defaults stay in the options classes per house convention, not `appsettings.Development.json` as the request suggested). No cert bypass anywhere. Host-root validation does not exist yet: FR-2. |
| 3 | Browser-direct Ollama audit | Zero unauthorized browser calls. Instance mode proxies browser -> `POST /chat` -> backend. Custom mode is a deliberate, user-owned browser-direct path (their endpoint, their key, held only in the browser); the server's endpoint and key never reach a client. Inventory: `fallen-8-web-ui/src/delegate/nl/{config,generate,prompt}.ts`, `NlBackendConfig.tsx`; `11434` appears only in custom-mode presets and docs. No server-side replacement needed. |
| 4 | ApiKey, three call sites incl. probe | Confirmed exactly: `OllamaChatBackend`, `EmbeddingBackendFactory`'s `OllamaApiClient`, `OllamaModelProbe` (transient, called from `AdminController`, swallows failures to null). Scoped to the Gateway backend (decision 1); catalog tiers per FR-2. |
| 5 | Timeout values only; don't touch the factory | Correct as corrected: `Timeout.InfiniteTimeSpan` + linked-token budgets verified in code. 600 s lands in the gateway profile (FR-9). |
| 6 | Streaming via OllamaSharp, stop suppressing | Correct: `Stream = false` with defensive chunk accumulation verified. FR-5; `POST /chat` stays buffered (decision 3), no `IChatBackend` streaming variant yet. |
| 7 | 503 retry, never pull | FR-4. Deviation: no separate retry-budget/clamp knobs (decision 4); the caller's TimeoutSeconds is the budget. `/api/pull` is unreachable from product code already; pinned by test instead of gated. |
| 8 | Batch cap, 429, order, dimension | Cap enforced everywhere and dimension validation already latches (verified). Remaining: order-preservation test, 429 via FR-4, `MaxBatchSize=32` in the profile. Job-level resume is out of scope; failures are explicit. |
| 9 | 14.7B baked-ins must travel per request | Premise partly stale: the runtime already sends per-kind system prompts and temperature on every request, and the `SYSTEM` text is already committed (`Modelfile.template`). The server never uses the 14.7B tier by default. Remaining gap is per-request stop tokens: FR-7. |
| 10 | Rename to `f8-delegate:latest` | FR-10, gateway profile only, coordinated. Local naming decision of 2026-07-20 stands (decision 6). |

## Impact on existing features

- **instance-config / setting catalog**: six new keys plus `Fallen8:Chat:Stream` and the
  widened `Backend` enums. `SettingCatalogTest` equivalence and
  `ConfigSettingsEndpointTest.GetConfig_WithholdsTheValueOfEveryNeverWritableKey` must cover
  the new NotWritable keys (extend explicitly if not automatic).
- **Studio Configuration panel**: renders `/config`, so the new keys appear; the
  configuration docs screenshots need recapture.
- **REST contract / OpenAPI snapshot**: `ChatSpecification.options.stop` (FR-7) changes the
  schema; regenerate the snapshot. No new routes.
- **MCP (engine -> REST -> MCP)**: no new REST operations, so `McpRestCoverageTest` is
  unaffected; check whether the bridged chat tool surfaces `options` and update or record a
  conscious deferral for `stop`.
- **embedding-provider / element-embeddings**: identity stamp, dimension, metric, stored
  vectors all unchanged (decision 5); their READMEs gain a one-line pointer to the gateway
  backend.
- **delegate-model-variants**: README gains the published-name note (FR-10); the local
  no-alias decision is reaffirmed, not reversed.
- **NL-assist dataset/eval**: no retrain; prompts, models and temperature are unchanged. No
  `RETRAIN-LOG.md` entry needed.
- **Docs site**: new page for the gateway backend; updates to `running.mdx`, `nl-assist.md`,
  `troubleshooting.md` (503-warming vs 503-down semantics), `configuration` page; README "Key
  features" line. `architecture.md` prose (and the sidecar label in both diagrams if it says
  "Ollama" rather than "model backend"): the model backend can now be a remote gateway; no
  new deployable, no new client channel.
- **Browser probe / engine**: untouched; every change is in `fallen-8-core-apiApp` (plus web
  UI docs strings). No engine, persistence or index change.
- **Stored queries / recipes / provider descriptors**: untouched.

## Out of scope

- An SSE/chunked `POST /chat` variant (decision 3; revisit when Studio renders deltas).
- Job-level resumable re-embedding state (revisit if a real bulk re-index hits the hourly
  budget in practice; failures are explicit and re-runnable today).
- Calling `/api/pull`/`/api/create`/`/api/delete`/`/api/copy` in any form.
- Changing the browser custom mode (it stays user-owned and key-in-browser; pointing it at an
  authenticated gateway is unsupported since its Ollama-native transport deliberately never
  authenticates, per nl-assist FR-26.12).
- Multi-backend failover (gateway falling back to local Ollama); coexistence is per
  deployment, one backend per provider per instance.

## Gateway-side dependencies (their side, acknowledged by them)

- `options.stop` honoured on `/api/chat` (needed by FR-7 for the 14.7B tier).
- `bge-m3` at 1024 dimensions in the catalog (needed by FR-9).
- Bare-tag references resolving as `:latest` (they report this fixed; FR-8 pins tags anyway).
