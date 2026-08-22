# Spec: Nahil backend

**Status: implemented and merged to `main` (2026-08-20); amended 2026-08-22, see
"Amendment" at the end.** Drafted the same day from an external
change-request list, every claim of which was verified against the code before implementing (see
the verification record below). The as-built deviations are recorded in "As built" at the end,
which is the part to read if the rest of this document and the code disagree. This file is the
LIVING record for the feature; the plan beside it is the historical sequencing note.

## Why

Fallen-8's chat and embedding providers speak the Ollama HTTP API to a local sidecar
(`http://ollama:11434` in the compose stack). **Nahil** (`nahil.dev`) is a remote,
authenticated, Ollama-compatible inference platform that routes requests to remote GPU/CPU
workers, and it becomes a SECOND place to run the same models. The two coexist per deployment
for a long time and **Ollama stays the default**; a deployment opts into Nahil by configuration
only.

Nahil is wire-compatible with Ollama (`/api/chat`, `/api/embed`, `/api/tags`, `/api/version`,
`/api/ps` take unchanged bodies), with these deltas, which are the whole feature:

1. Every route requires `Authorization: Bearer <key>`. Real Ollama has no auth.
2. A request for a catalogued model not yet resident on a worker answers `503` plus a real
   `Retry-After` while the platform pulls the model in the background. A multi-GB pull takes
   minutes.
3. A per-API-key hourly token budget answers `429` (matters for bulk embedding).
4. A per-request embedding batch cap of 64 items.
5. Workers include CPU-only machines (measured ~5 s/token), and a non-streamed response carries
   a verification inference in front of the reply. Streamed responses verify after delivery.
6. Nahil's catalog has to serve the models this project actually uses - the operator's own
   fine-tunes `phi4-f8-mini` / `phi4-f8`, and `bge-m3` at 1024 dimensions. Model names are
   configured, so whatever Nahil's catalog calls a model is what goes in the config;
   `Fallen8:Embedding:Dimension`, `IntendedMetric` and all stored vectors stay valid, and nothing
   re-embeds.

## Decisions (operator)

1. **Second backend value, not a mutated Ollama backend** (2026-08-20). `Nahil` joins the
   `Backend` selectors. It reuses the same OllamaSharp client plumbing internally, but auth, the
   503-warmup retry and 429 handling exist ONLY on the Nahil backend. `Backend=Ollama` behaviour
   stays bit-identical; no implicit semantics keyed off "is an ApiKey set".
2. **Named openly as Nahil** (2026-08-20, superseding the same day's earlier "generic naming"
   call once the name was settled and the domain bought). Config keys, the enum value, C#
   identifiers, the compose overlay, the docs page and this directory all say `Nahil`; prose says
   "Nahil (nahil.dev)". The dot is not usable in a configuration path segment, so the identifier
   is `Nahil` and the domain appears only in prose. The backend is described as Nahil-specific
   rather than as a generic gateway: anything else Ollama-compatible still happens to work, and
   the docs promise nothing about it.
3. **`POST /chat` stays buffered.** Streaming is turned on at the backend seam (Nahil then
   verifies after delivery instead of in front of it, and chunks arrive incrementally), but the
   REST response shape is unchanged and `IChatBackend` keeps its single buffered method. An
   SSE/chunked REST variant is deferred until Studio renders deltas; today no caller would
   consume it.
4. **Constants over knobs for retry.** The caller's existing budget
   (`Fallen8:Chat:TimeoutSeconds` / `Fallen8:Embedding:TimeoutSeconds`) stays the SINGLE
   authoritative deadline (the deadline rule on `OllamaHttpClientFactory` is preserved: retry
   waits run on the linked token and consume that budget). There is no separate retry-budget
   knob: a wall-clock retry budget beyond the caller's deadline could never be reached, and one
   below it would just move the honest 504/503 earlier. The per-wait clamp against a hostile
   `Retry-After` is a documented constant (60 s), not a setting.
5. **`Fallen8:Embedding:ModelName` stays `bge-m3`, untagged.** It is the identity stamp (feature
   embedding-provider FR-8) compared against stamps stored beside existing vectors, and is never
   sent to Ollama; retagging it would make every existing index identity-mismatch for zero wire
   benefit. Only `*:Ollama:Model` / `*:Nahil:Model` are request identifiers.
6. **The models stay `phi4-f8-mini` / `phi4-f8`, everywhere.** These are the operator's own
   fine-tunes and the "clean rename, no alias" decision (delegate-model-variants, 2026-07-20)
   governs their name on every backend, not just locally. The external change-request list asked
   for a rename to a different published name; that is declined - a Nahil deployment configures
   the same names a local one does, and the model name is a configured string either way.
7. **A misconfigured backend latches a 503; it does not stop the process** (as built, see below).

## Requirements

### FR-1: Backend selector

`Fallen8:Chat:Backend` accepts `Ollama | Nahil` and its catalogued `allowedValues` grow to match
(the backend factories match ordinally and throw otherwise; that throw latches as a permanent
503, so the accepted set stays load-bearing). `Fallen8:Embedding:Backend` accepts
`Onnx | LLamaSharp | Ollama | Nahil` but carries NO accepted-value set: it is `NotWritable` under
R3, and a never-writable entry structurally cannot advertise one. On that side the factory throw
remains the only guard, exactly as before.

### FR-2: Nahil options and validation

New sections mirroring the Ollama ones:

```
Fallen8:Chat:Nahil:Endpoint        NotWritable (R4, same SSRF reason as the Ollama endpoints)
Fallen8:Chat:Nahil:ApiKey          NotWritable (R8, a credential the server presents)
Fallen8:Chat:Nahil:Model           Restart (mirrors Fallen8:Chat:Ollama:Model)
Fallen8:Embedding:Nahil:Endpoint   NotWritable (R4)
Fallen8:Embedding:Nahil:ApiKey     NotWritable (R8)
Fallen8:Embedding:Nahil:Model      NotWritable (R3: the model IS the embedding function)
```

**R8 is a new never-writable rule** this feature introduces: *no credential this server
presents.* R1 covers the credential the server DEMANDS and is blanket-scoped to
`Fallen8:Security` (a key outside it citing R1 fails `AssertBlanketRule`), and neither the URL
rule (R4) nor the capability rule (R5) describes a secret. A written value redirects metered
spend; a published one leaks the key. Never-writable is the only tier that prevents both, because
its entries publish tier and reason but no value. R8 is registered in `SettingCatalogTest`'s
known-rule set and documented on the configuration docs page beside R1-R6.

Validation, per connection: `Endpoint` parses as an absolute `http`/`https` URI whose
`AbsolutePath` is `/` with no query and no fragment (a host root; `HttpClient.BaseAddress`
silently drops a path prefix, so a pathful URL must be an error, never rewritten), `Model` is
non-blank, and for Nahil `ApiKey` is non-blank. The same host-root validation applies to the
`Ollama` endpoints (a benign tightening; the shipped values are host roots). HTTPS is expected
for Nahil and documented, not enforced. **No certificate-validation bypass exists in the apiApp
(verified) and none is added.** (Two other deployables do have opt-in bypasses -
`fallen-8-mcp`'s `TlsInsecure` and the integrations provider factory - both out of scope.)

### FR-3: Bearer auth on every Nahil call, key never logged

All THREE Ollama-protocol call sites carry the key when talking to Nahil: the chat backend, the
embedding client, and the transient residency probe (`OllamaModelProbe`, built per call by
`AdminController` for `GET /config`; Nahil authenticates `/api/ps` too, and an unauthenticated
probe would 401, be swallowed to `null`, and show residency "unknown" forever with nothing in the
logs saying why). Chat and embedding carry independently configured keys. The header is set once
on the constructed `HttpClient` (`DefaultRequestHeaders.Authorization`); no call site formats it.
The key value never appears in any log line, error message, exception, or diagnostic.

### FR-4: Bounded retry on 503 and 429, Nahil only

A `DelegatingHandler` inside the Nahil clients' handler chain (composed in
`OllamaHttpClientFactory`; the plain Ollama path gets no handler and is byte-for-byte unchanged,
and the residency probe never gets one either - a warm-up wait would defeat the 3 s bound the
probe exists to keep):

- On `503`/`429`: read `Retry-After` (both delta-seconds and HTTP-date forms), wait, retry the
  identical request (buffered and re-cloned per attempt, so the body and the credential replay).
  Missing, unparseable, zero, negative or past-dated: exponential backoff with jitter from 2 s,
  capped at 30 s per wait, rather than an immediate retry that would hot-loop.
- Each individual wait is clamped to 60 s (constant, decision 4).
- Waits honour the caller's `CancellationToken`; the caller's `TimeoutSeconds` budget is the
  total bound. When it expires mid-wait the error names the model, the status Nahil kept
  answering, the attempt count and the total time waited, and the existing 504 (chat) / 503
  (embedding) status is preserved.
- One information-level log line per retry (model, wait, attempt, elapsed); no per-poll spam.
  503 and 429 stay distinguishable in logs and errors because they mean different things.
- No other 4xx is retried. Chat and embedding calls are safe to repeat (verified: both are pure
  proxies with no server-side state written before success).
- **`/api/pull`, `/api/create`, `/api/delete`, `/api/copy` are never called by product code
  (verified: the only `ollama pull` sites are the local compose provisioning scripts
  `scripts/ollama-init.sh` and `scripts/ensure-models.sh`, which run inside/against the local
  sidecar container and are absent from a Nahil deployment). No gating code is needed.**

### FR-5: Chat streaming at the backend seam

`OllamaChatBackend` sets `Stream = true` (the existing `await foreach` accumulation already
consumes chunks; the terminal done-chunk keeps supplying the stats). `Fallen8:Chat:Stream` (Bool,
Restart tier, default `true`, catalogued) is the escape hatch back to the non-streamed request
shape. Mid-stream failure is explicit and reaches **502**, not 503: the backend raises a
`ChatBackendOutputException` naming how many characters arrived, and `Fallen8ChatProvider` maps
that type to its existing output-fault path ahead of the generic catch. A stream that ends with
no terminal chunk is the same truncation and fails the same way. A CANCELLED call is not a
truncation and must not be reported as one, which needs an explicit guard because OllamaSharp's
iterator ends rather than throwing when the token trips.

### FR-6: Embedding path hardening

- The batch cap is already enforced on every path (verified: `EmbeddingController` rejects
  oversized batches with 400 on both embed routes; `DocumentIngestionService` chunks by
  `MaxBatchSize`; the semantic query path embeds one text). A test pins order preservation across
  ingestion chunking.
- `429` retry comes from FR-4. On failure mid-ingestion the report now names the chunk range that
  was not embedded and how many already were; nothing is written until every chunk has a vector,
  so the document is re-runnable rather than half-indexed. (Job-level checkpointing stays out of
  scope.)
- Dimension validation already exists and latches (verified). A test pins the message, because
  the two numbers in it are the whole diagnosis when a backend serves a different model than the
  stored vectors came from.
- The Nahil deployment profile sets `Fallen8:Embedding:MaxBatchSize=32` (Nahil's own per-request
  cap is 64; the shipped default sits exactly at the limit, 32 leaves headroom).

### FR-7: Per-request stop tokens

`ChatSpecification.options` grows an optional `stop` string array beside `temperature`, flowing
through `ChatBackendOptions` into OllamaSharp's `RequestOptions.Stop`, MERGED with the existing
temperature rather than replacing it, and omitted entirely when the caller asks for neither.
Rationale: the runtime NL assist already sends its per-kind system prompt and temperature with
every request (the baked-in `SYSTEM` in `nl-assist-finetune/train/Modelfile.template` is fallback
only, and is already committed), so stop tokens travel the same per-request road. This is the
enabler for the 14.7B tier (`phi4-f8`), whose published registry build carries neither the ChatML
template nor the `<|im_start|>`/`<|im_end|>` stop tokens of a locally built image. **The server
never defaults to the 14.7B tier (verified), so no server-side prompt or stop configuration is
added; the caveat is documented where the variant is offered.**

### FR-8: Explicit `:latest` tags on request-bound model names

`docker-compose.yml` env and the C# option defaults pin `phi4-f8-mini:latest` / `bge-m3:latest`
for the `*:Ollama:Model` keys. No code strips, appends or normalizes a tag on the way to the
request body (verified, and pinned by a test that asserts the configured string reaches the
outbound JSON verbatim; the only tag handling is `OllamaModelProbe`'s comparison tolerance, which
stays). `Fallen8:Embedding:ModelName` stays untagged (decision 5). The Studio custom-mode presets
keep bare names (browser-direct, user-owned).

### FR-9: Nahil deployment profile

`docker-compose.nahil.yml` plus docs: the local sidecar parked on an unactivated profile (a
compose overlay cannot remove a service, and nothing `depends_on` it), `Backend=Nahil` for chat
and embedding, endpoint/key/model from operator env vars (`F8_NAHIL_URL`, `F8_NAHIL_API_KEY`,
`F8_NAHIL_CHAT_MODEL`, `F8_NAHIL_EMBED_MODEL`), `Fallen8:Chat:TimeoutSeconds=600` (CPU workers
plus warm-up waits; 600 s is also exactly Studio's 10-minute editor ceiling, so higher would only
move the give-up from the server to the browser), `Fallen8:Embedding:TimeoutSeconds` unchanged at
300, `MaxBatchSize=32`. The endpoint and key have no defaults, so the overlay fails closed.
`scripts/env-up.js` applies it when `F8_NAHIL_URL` is set. CI validates it with
`docker compose config -q`, which is its only gate: no unit test reads a compose file.

### FR-10: renaming the chat model - DECLINED

The change-request list asked for the configured chat model to be renamed to a different published
registry name at cutover. Declined: these are the operator's own fine-tunes, they are named
`phi4-f8-mini` and `phi4-f8`, and the 2026-07-20 "clean rename, no alias" decision applies to
every backend rather than only to local artifacts. A Nahil deployment configures the same names a
local one does. Nothing in this feature depends on it - the model name is a configured string, so
if Nahil's catalog ever needs a different one, that is an environment variable and not a change
here.

## Verification record (change requests vs code, 2026-08-20)

| # | Request claim | Finding |
|---|---|---|
| 1 | Tag pinning; check for tag normalization | Correct; no normalization exists. `ModelName` conditional resolved: identity stamp only, stays untagged (decision 5). |
| 2 | Endpoint config, host-root, no cert bypass | Endpoints are config-bound with benign C# defaults (`localhost:11434`, capabilities default OFF, so nothing dials unless enabled; defaults stay in the options classes per house convention, not `appsettings.Development.json` as the request suggested). No cert bypass in the apiApp. Host-root validation did not exist: FR-2. |
| 3 | Browser-direct Ollama audit | Zero unauthorized browser calls. Instance mode proxies browser -> `POST /chat` -> backend. Custom mode is a deliberate, user-owned browser-direct path (their endpoint, their key, held only in the browser); the server's endpoint and key never reach a client. Inventory: `fallen-8-web-ui/src/delegate/nl/{config,generate,prompt}.ts`, `NlBackendConfig.tsx`; `11434` appears only in custom-mode presets and docs. No server-side replacement needed. |
| 4 | ApiKey, three call sites incl. probe | Confirmed exactly: `OllamaChatBackend`, `EmbeddingBackendFactory`'s `OllamaApiClient`, `OllamaModelProbe`. Scoped to the Nahil backend (decision 1); catalog tiers per FR-2. |
| 5 | Timeout values only; don't touch the factory | Correct as corrected: `Timeout.InfiniteTimeSpan` + linked-token budgets verified in code. 600 s lands in the Nahil profile (FR-9). |
| 6 | Streaming via OllamaSharp, stop suppressing | Correct: `Stream = false` with defensive chunk accumulation verified. FR-5; `POST /chat` stays buffered (decision 3). |
| 7 | 503 retry, never pull | FR-4. Deviation: no separate retry-budget/clamp knobs (decision 4); the caller's TimeoutSeconds is the budget. `/api/pull` is unreachable from product code already, so nothing needed gating. |
| 8 | Batch cap, 429, order, dimension | Cap enforced everywhere and dimension validation already latches (verified). Added: order-preservation test, 429 via FR-4, the un-embedded-range failure report, `MaxBatchSize=32` in the profile. Job-level resume out of scope. |
| 9 | 14.7B baked-ins must travel per request | Premise partly stale: the runtime already sends per-kind system prompts and temperature on every request, and the `SYSTEM` text is already committed. The server never uses the 14.7B tier by default. Remaining gap was per-request stop tokens: FR-7. |
| 10 | Rename the chat model to a different published name | DECLINED. The fine-tunes are the operator's own and stay `phi4-f8-mini` / `phi4-f8` on every backend (decision 6); the model name is configured, so no code depends on the choice. |

## As built: deviations from this spec

Recorded because the code is the source of truth and these were decided while implementing.

1. **No fail-fast startup throw** (FR-2 originally said "fail fast at startup"). The codebase has
   no startup-validation pattern and documents twice that nothing implements `IValidateOptions`;
   more importantly `Fallen8EmbeddingProvider` already chose LATCH over THROW for the same class
   of fault, with the reasoning written down ("a config typo must not turn /statistics into a
   500"). A graph database that refuses to boot because an optional chat endpoint has a typo'd
   URL is worse than one that serves graphs and answers 503 on `/chat` with the exact reason - and
   in a container, a startup throw is a crash loop that shows the operator nothing. So: validation
   happens where the connection is resolved (factory time, latched by the existing `Lazy` into a
   permanent 503 carrying the reason), PLUS a startup WARNING that reports the same problem string
   from the same `IsValid` method, so the two can never drift and the operator learns at boot
   without losing the database. Decision 7.
2. **`OllamaWarmupTimeoutException` is not an `OperationCanceledException`.** The obvious design,
   and it silently does not work: `HttpClient` replaces any cancellation leaving its handler chain
   with a `TaskCanceledException` of its own, so a subclass carrying the warm-up detail is
   discarded before a provider can read it. Caught by a test. It is now a plain `Exception` that
   keeps the cancellation as `InnerException`, and both providers catch it explicitly - calling
   `ThrowIfCancellationRequested` first, so a caller who went away still gets a cancellation and
   only a spent budget becomes a timeout.
3. **A cancelled chat call needed an explicit guard.** OllamaSharp's iterator ENDS rather than
   throwing when the token trips, so the loop completed with no terminal chunk and the truncation
   check reported a cancelled call as a 502 truncation. Also caught by a test.
4. **The mid-stream 502 needed a provider change, not just a backend change.** The original FR-5
   assumed a truncation would reach 502 on its own; in fact `Fallen8ChatProvider`'s generic catch
   turned every backend exception into a 503. A dedicated `ChatBackendOutputException` plus a
   catch ahead of the generic one is what actually delivers the promised 502.
5. **R8 is new.** FR-2 originally assigned no rule code to the two `ApiKey` keys. There was no
   non-Security credential precedent, and R1 is blanket-scoped, so a new rule was the honest
   option rather than filing a secret under the URL rule.
6. **`Fallen8:Embedding:Backend` gains no `allowedValues`** (FR-1 as first written was
   impossible): it is `NotWritable`, and never-writable entries structurally cannot carry an
   accepted set.
7. **FR-10 is declined, not deferred** (see above): no `f8-delegate` name appears anywhere.
8. **The chat backend's stream catch is narrow, and had to be.** An adversarial review pass caught
   the first version blanket-catching every non-cancellation fault as a truncation - which made the
   provider's dedicated warm-up and "backend is down" paths unreachable and turned a stopped LOCAL
   sidecar from 503 into 502, breaking this feature's central promise that the Ollama path is
   unchanged. A fault is a truncation only if tokens had already arrived; the warm-up give-up is
   excluded outright, since it belongs to the provider. Pinned by a test.
9. **No endpoint value appears in a message.** Also from the review: the validation text reaches an
   anonymous 503 body, while the catalog deliberately withholds that key's value - and an operator
   who embedded credentials in the URL would have had them disclosed. Messages name the key only.

### Known consequences, accepted rather than engineered around

- **A REST-only operator can select `Backend=Nahil` without being able to configure it.** The
  endpoint (R4) and key (R8) are never-writable by design, so a `PATCH /config` that switches the
  backend cannot supply them. It is restart-tier, so nothing breaks until a restart, after which
  `/chat` answers 503 naming the missing key and clearing the override recovers. Nahil is
  environment-configured by design (that is what the compose overlay is for); adding a validator
  that understands backend semantics would couple the write path to them for a case that already
  explains itself.
- **`GET /config` drives a residency probe to Nahil, and that route is anonymous on a keyless
  instance.** It is a `/api/ps` call, which costs no tokens, is bounded at 3 s, and only runs when
  the capability is enabled; a caller on a keyless instance can already execute arbitrary code in
  the process. Not worth a cache or a rate limit.

Also fixed in passing, because a reader would otherwise have taken them as true: the
"publishes 94 keys" comment in `Fallen8ConfigOverrides` (now 102), and two stale claims in
`fallen-8-web-ui/scripts/samples/shared.ts` (a batch size that was not the one the code uses, and
a 100 s transport timeout that `Timeout.InfiniteTimeSpan` retired).

## Impact on existing features

- **instance-config / setting catalog**: seven new keys (six Nahil + `Fallen8:Chat:Stream`) and a
  widened `Fallen8:Chat:Backend` enum. `SettingCatalogTest`'s named-rule map and known-rule set,
  `ConfigSettingsEndpointTest`'s hand-written withheld-key list and its `allowedValues`
  assertion, and `ConfigWriteEndpointTest`'s enum case all needed updating; the catalog
  equivalence and value-withholding tests picked the rest up automatically.
- **Studio Configuration panel**: renders `/config`, so the new keys appear. The provider cards
  show the model name, which the tag pinning changed, so the configuration/connect screenshots
  are stale.
- **REST contract / OpenAPI snapshot**: `ChatSpecification.options.stop` changes the schema;
  regenerated (additions only). No new routes, so `McpRestCoverageTest` is unaffected.
- **MCP**: there is no bridged chat tool - `POST /chat` is a recorded conscious deferral in
  `McpRestCoverageTest`. No MCP work and no new deferral entry.
- **embedding-provider / element-embeddings**: identity stamp, dimension, metric and stored
  vectors all unchanged (decision 5).
- **delegate-model-variants**: its no-alias decision is reaffirmed and widened - it governs the
  model's name on every backend, not just the local one. Nothing in that feature changes.
- **NL-assist dataset/eval**: no retrain; prompts, models and temperature are unchanged. No
  `RETRAIN-LOG.md` entry.
- **Docs site**: new `nahil` page; updates to `running.mdx`, `nl-assist.md`, `troubleshooting.md`
  (warm-up 503 vs unreachable 503, and the 600 s / 10-minute coincidence), `configuration.md`
  (R8), `semantic-traversal.mdx`, `studio.md`, `architecture.md` (prose + both diagrams), and a
  README "Key features" line.
- **Browser probe / engine**: untouched; every change is in `fallen-8-core-apiApp` plus docs and
  test/ops files. No engine, persistence or index change, so the browser probe is not implicated.
- **Stored queries / recipes / provider descriptors**: untouched.

## Out of scope

- An SSE/chunked `POST /chat` variant (decision 3; revisit when Studio renders deltas).
- Job-level resumable re-embedding state (revisit if a real bulk re-index hits the hourly budget;
  failures are explicit and re-runnable today).
- Calling `/api/pull`/`/api/create`/`/api/delete`/`/api/copy` in any form.
- Changing the browser custom mode (it stays user-owned and key-in-browser; pointing it at Nahil
  is unsupported since its Ollama-native transport deliberately never authenticates, per
  nl-assist FR-26.12).
- Multi-backend failover (Nahil falling back to a local sidecar); coexistence is per deployment,
  one backend per provider per instance.

## Amendment (2026-08-22): the real input ceiling, and the deployment profile

Two things landed after the merge. Both came from measuring the platform instead of reading its
metadata, which is the part worth keeping.

### A-1: `bge-m3` serves 2048 tokens per input, not the 8192 it advertises

Measured on Nahil: ~1,880 tokens answers `200`, ~2,120 answers `400`. `/api/show` reports a
context length of 8192 and it is not honoured. **The local Ollama sidecar stops at the same 2048**
(measured: `prompt_eval_count` came back as exactly 2048 for a 31,200-char input that returned
`200`), so this was never a Nahil property and a locally embedded corpus was subject to it too.

The consequence was the reason to act. Ollama's `truncate` flag defaults to **true**, meaning
*shorten anything that does not fit and answer as though it had fitted*, so an over-long chunk
returned an ordinary-looking 1024-dimension vector for only its first ~2,046 tokens. FR-6's
dimension check could not see it - the dimension was right. Nothing logged it. The chunk was
indexed, searchable, and quietly wrong about its tail.

Two changes, which only work as a pair:

- `Fallen8EmbeddingProvider` sends **`truncate: false`** on every embed request for the `Ollama`
  and `Nahil` backends, so an over-ceiling input is refused with a `503` naming the ceiling and
  the setting to lower. Carried in `EmbeddingGenerationOptions.AdditionalProperties`, which
  OllamaSharp 5.4.27's mapper binds onto `EmbedRequest.Truncate`;
  `RawRepresentationFactory` reads like the intended route and is **ignored** by that mapper
  (verified - it produced a body with no `truncate` member at all, i.e. the silent-truncation
  default). Re-check on an OllamaSharp upgrade.
- `Fallen8:Ingestion:ChunkMaxChars` **4,000 -> 3,600**, a token budget in char units: ~1,800
  tokens at the 2.0 chars/token worst case measured. Without it, `truncate: false` would convert
  silent degradation into failed ingests.

Measured `bge-m3` density: English 4.01, German 4.11, Russian 3.98, Arabic 3.56, C# identifiers
3.41, emoji 2.30, ARXML 2.23, markdown tables 2.10, punctuation-dense 2.04, Japanese 2.02,
Korean 1.67, Chinese 1.33 chars/token. Precisely: 4,000 was *inside* the ceiling for every
Latin-script, table and XML sample - by under 70 tokens at the densest (~1,980 of 2,048), which is
no margin - and already *outside* it for Korean (~2,400) and Chinese (~3,010). A CJK corpus wants
`ChunkMaxChars` near 1,800.

The `8192` in `Fallen8:Embedding:MaxTextLength` was audited and **kept**: it is chars, not tokens,
and it is the right bound for a different reason - above it, no input fits 2048 tokens even at the
most token-efficient text there is (~4.1 chars/token). Its doc comment now says so. `ChunkMinChars`
(800), `Nlp:MaxCharsPerChunk` (20,000, truncates before a sidecar with its own cap) and the sample
generator's 800-char section floor were audited and are unaffected.

Verified against a live apiApp and a live sidecar, not a mock: a 2,910-char English input returns
`200` with a 1024-dim vector; an 8,000-char markdown table (~3,800 tokens) returns `503` carrying
the ceiling, the reason and the setting to change.

### A-2: the deployment profile, and one name meaning two builds

FR-9's profile is now the overlay's default rather than a documented recipe:
`https://api.nahil.dev` for both capabilities, `phi4-f8-mini:latest` chat, `bge-m3:latest`
embeddings at Dimension 1024 / Cosine with `ModelName=bge-m3` untouched (nothing re-embeds),
`Chat:TimeoutSeconds=600`, `Stream=true` (already the default), `MaxBatchSize=32`.

- **`F8_NAHIL_URL` now defaults** to `https://api.nahil.dev`. FR-2 gave it no default so that
  selecting the backend could never be quietly aimed at localhost; a public host root cannot be,
  so the argument does not reach it. `F8_NAHIL_API_KEY` stays required with no default, so the
  overlay still fails closed. **`scripts/env-up.js` selects the overlay on either variable** - it
  keyed off `F8_NAHIL_URL` alone, which after this change would have let a deployment supply the
  one thing Nahil needs and quietly get the local sidecar.
- **`phi4-f8-mini` and `f8-delegate` both resolve on Nahil, to different weights.** Both published
  repos of this fine-tune still exist: `f8-delegate` is its pre-rename name
  (delegate-model-variants renamed it, with no local alias). Traced on this machine's volume, the
  local `phi4-f8-mini:latest` holds the **`f8-delegate` build** - `library/phi4-f8-mini`,
  `library/f8-delegate` and `stoic_hellman_728/f8-delegate` all carry model layer
  `sha256:3ab5bf48…8fef0`, one `ollama list` id (`6d4bd13b1fda`), byte-identical `--system` /
  `--template` / `--parameters`, and `stoic_hellman_728/phi4-f8-mini` was never pulled here at all.
  So on a volume built before the rename, `F8_NAHIL_CHAT_MODEL=f8-delegate:latest` keeps the output
  that deployment already had and the default moves it to the current published finetune. One line
  either way; neither is more correct. This disturbs neither FR-10 (declining to RENAME the model)
  nor delegate-model-variants' no-alias decision: nothing is renamed or aliased, a different
  catalog entry on a remote backend is named.
- That local build is now **recorded** in `nl-assist-finetune/fixtures/phi4-f8-mini/` - `ollama
  show --system` / `--template` / `--parameters` verbatim, plus the blob digests and which
  published repo they came from. Nothing in this repository reproduces it byte-for-byte, so the
  fixture is the only description of what that model was once the volume is gone.

### Amendment impact

- **Engine, REST contract, OpenAPI snapshot**: unchanged - snapshot regenerated, zero content
  diff. No new setting, so no `Fallen8SettingCatalog` entry, no Studio catalog change.
- **Tests**: full suite green (2,069 passed, 0 failed). No test pinned the old defaults;
  `EmbeddingBatchOrderTest` sets `ChunkMaxChars` explicitly and `IngestionChunkerTest` passes its
  own bound.
- **Screenshots**: none affected. `screen-configuration.png` photographs the ChangeFeed section,
  not Ingestion or Embedding; `screenshot-knowledge.spec.ts` ingests two ~150-char documents, far
  below either bound.
- **Samples**: unaffected. The largest shipped sample document section is 1,359 chars, so chunk
  boundaries are identical and no sample fixture moves.
- **NL-assist dataset/eval**: no retrain, no `RETRAIN-LOG.md` entry - the amendment changes no
  prompt, model or temperature.
- **Docs site**: the ceiling's ONE home is `semantic-traversal.mdx` (the embedding provider owns
  it, since it is not a Nahil property); `nahil.md`, `unstructured-ingestion.md` and
  `troubleshooting.md` point there. Build green, all internal links valid.
- **Browser probe**: not implicated - apiApp, docs and ops files only.

### Not done, deliberately

- **No token counter.** Capping the chunker in real `bge-m3` tokens needs its SentencePiece model,
  which is not shipped; a character bound plus a loud refusal is the honest pair, and the measured
  density table is how an operator sizes it for their corpus.
- **A single table ROW longer than `ChunkMaxChars` is still emitted whole.** A row-window always
  carries at least one body row, so the alternative is cutting a row in half. It is now a loud
  failure instead of a silent truncation, and it is documented as such.

## Nahil-side dependencies (their side, acknowledged by them)

- `options.stop` honoured on `/api/chat` (needed by FR-7 for the 14.7B tier).
- `bge-m3` at 1024 dimensions in the catalog (needed by FR-9).
- Bare-tag references resolving as `:latest` (they report this fixed; FR-8 pins tags anyway).
