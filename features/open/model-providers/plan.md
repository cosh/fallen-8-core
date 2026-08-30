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
  keyed off **`F8_TEST_OPENAI_API_KEY`/`F8_TEST_ANTHROPIC_API_KEY`** env vars, following the Nahil
  smoke precedent (commit 6dde4501). Corrected during implementation: this bullet originally named
  the unprefixed forms, which are the *compose* variables (see the outstanding-decisions record).

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

## Phase status

Phase 6 (deployment + docs) is **done and verified**: the two overlays, the `F8_PULL_ASSIST` gate
in `ollama-init.sh` and `ensure-models.sh`, the `F8_MODEL_PROVIDER` resolution in `env-up.js`, the
`.env.example` block, the new `model-providers.md` page plus the eight page edits, the sidebar
entry, the README key-features line and BOTH architecture diagrams. Verified by
`node --check`, `bash -n`, `docker compose config -q` on each overlay, and the link-checked docs
build.

Phases 1 to 5 were implemented in parallel on the same branch by the other owners; each verified
its own subset. The authority on whether they are complete is the merge gate below, not this note.

## Outstanding decisions deliberately left to implementation

Recorded here because this section asked for them. The transport verdicts come from runtime spikes
against the real SDK surfaces, not from reading their docs.

### SDK or raw HTTP, per provider per capability

**Both official SDKs, no raw HTTP anywhere in this feature.** Decision 6's escape hatch does not
fire: both clear the transport contract under runtime proof. Pinned exactly, in
`fallen-8-core-apiApp` only:

```
OpenAI     2.13.0
Anthropic  12.44.0
```

Neither pin disturbs the existing graph: `Microsoft.Extensions.AI.Abstractions` stays on the repo's
`10.8.0` (Anthropic's transitive `10.5.1` loses to the direct pin, no downgrade error) and
`System.ClientModel 1.14.0` arrives transitively from `OpenAI`. No `NU1605`, no `NU1608`, no
vulnerable package.

- **Anthropic chat (SDK 12.44.0).** Deciding reason is contract item 1, the single deadline: the
  SDK's own deadline is a settable `TimeSpan?` that `Timeout.InfiniteTimeSpan` verifiably disables,
  leaving the caller's linked CTS as the only thing that can cancel. Item 2 holds exactly
  (`MaxRetries = 0` produces exactly one HTTP attempt; the default `null` produces three). Item 3
  is verbatim through the `HttpClient` property. Generation stats are native
  (`Usage.InputTokens` / `.OutputTokens`).
- **OpenAI chat (SDK 2.13.0).** Deciding reason is again item 1, and here it is the bug that
  already shipped once: `System.ClientModel`'s undocumented default network deadline is **100
  seconds**, and `NetworkTimeout = Timeout.InfiniteTimeSpan` provably disarms it. Item 2 is a live
  hazard rather than a theoretical one - the default retry policy makes **four** attempts against a
  503, and `ClientRetryPolicy(maxRetries: 0)` makes exactly one, transport faults included. Item 3
  is exact: `HttpClientPipelineTransport(HttpClient)` takes our client, handler and all.
- **OpenAI embeddings (SDK 2.13.0 `EmbeddingClient` + a hand-written `IEmbeddingGenerator`
  adapter).** The deciding reason here is NOT a contract item.
  `Microsoft.Extensions.AI.OpenAI` passes items 1 to 5 and is **rejected on correctness**: it does
  not sort the response by `index`, and `Embedding<float>` carries no `Index` field to recover it,
  so a permuted response mis-assigns vectors to graph elements with no exception and no log line.
  Measured: inputs `alpha, beta, gamma` came back `0.3, 0.1, 0.2`. Element A silently gets element
  C's vector and semantic traversal returns confidently wrong neighbours. Secondary cost: taking it
  forces `OpenAI` down to 2.12.0 (`NU1608`) and
  `Microsoft.Extensions.AI.Abstractions` up to 10.9.0 (`NU1605`), both build-breaking here. The
  hand-written seam is about 40 lines, keeps both pins, sorts by `Index`, and asserts
  `Count == inputs.Count` (three inputs answered with one vector returns `Count == 1` silently
  through the raw SDK too).

Two non-negotiables, both empirically load-bearing rather than defensive:

1. **Both deadlines must be `Timeout.InfiniteTimeSpan`** - the SDK's own and the `HttpClient`'s.
   Setting only one reproduces the `TaskCanceledException` that shipped as an HTTP 500 (measured:
   SDK 400 ms with an infinite `HttpClient` throws at 413 ms; the reverse throws at 401 ms; both
   infinite completes at 2013 ms with the token never cancelled).
2. **Our retry handler goes in OUR `HttpClient` chain, never in the Anthropic SDK's `Handlers`
   list.** That shape throws `InvalidOperationException: The request message was already sent.`,
   because the SDK's passthrough handler calls `HttpClient.SendAsync`, which refuses a resent
   request. Related: a handler instance cannot be shared between clients, so a retry handler is
   never a DI singleton.

Also recorded so nobody re-spikes it: the Anthropic SDK's `Timeout` does not span the SSE body
read, so on the streaming path the SDK contributes no deadline at all and the caller's CTS is the
whole story. The `Anthropic` package ships no `net10.0` asset; its `net9.0` lib runs on net10.0 with
no issue observed. Anthropic streaming plus our retry handler was proven to interleave correctly
(events at 400 ms intervals, start at 403 ms through stop at 2828 ms).

### `NahilWarmupRetryHandler`: an abstract base, not siblings

Generalized into `RetryAfterHandler` (abstract), with `NahilWarmupRetryHandler` and
`RemoteModelRetryHandler` as sealed subclasses and `ModelRetryTimeoutException` as the shared base
of the give-up exceptions. Chosen because the Nahil tests need **zero** edits: inherited
`static`/`const` members resolve through the derived type name and `IsInstanceOfType` still holds
for a sealed subclass. Siblings would have duplicated the `Retry-After` arithmetic, the clamp, the
backoff and the request-clone logic three ways. Retry sets: OpenAI `{429}`, Anthropic
`{429, 529}`; 503-while-warming stays Nahil-specific, because it is a Nahil behaviour and not a
protocol one.

### The OpenAI client options: one home, hoisted at integration

`OpenAIChatBackend` and `OpenAIEmbeddingGenerator` were each written by their own owner, and each
arrived with its own copy of the same four-setting `OpenAIClientOptions` block plus its own
paragraph explaining it. Hoisted into `RemoteModelHttpClient.OpenAIOptions`, beside the
`HttpClient` composition, mirroring how `OllamaHttpClientFactory` is the one home for its three
Ollama-protocol call sites. Two reasons it is not merely tidier: the csproj comment beside the SDK
pins already points at `RemoteModelHttpClient` as "the one home of that composition", which was
false while half of it lived in two backends; and a provider with two capabilities has two clients,
so a second copy is exactly how one of them quietly keeps an SDK default.

Measured while doing it: before the hoist, deleting `NetworkTimeout` from the embedding generator
alone failed **no** test. The generator's deadline test proves the caller's 300 ms token ends the
call within 10 s, which a 400 ms SDK deadline also satisfies, so it could not see the difference.
`TheTransport_CarriesNoDeadlineOfItsOwn` therefore now also reads the SDK-side settings back
directly (OpenAI's `NetworkTimeout` off the composition, Anthropic's `Timeout` and `MaxRetries` off
the client) instead of only asserting `HttpClient.Timeout`, which was the only half its own
docstring claimed to cover. Read rather than timed on purpose: the defaults are 100 seconds and ten
minutes, so a test that waits for one to fire either takes that long or proves nothing.

### `F8_PULL_ASSIST`, because FR-7's claim was false

FR-7 said the new overlays skip the chat fine-tune pulls "via the sidecar's existing env". No such
variable existed. The full correction is recorded as an amendment on FR-7 in `spec.md`; the outcome
is one new gate, default on, independent of `F8_EMBEDDINGS`, covering the two mini models. The
rejected alternative was to set nothing and print an honest header about 4.8 GB of downloads nobody
uses, which is a worse product than one variable mirroring a pattern the script already has twice.

### `F8_TEST_*` for the live smokes

The smoke variables are `F8_TEST_OPENAI_API_KEY` / `F8_TEST_ANTHROPIC_API_KEY`, matching the
shipped `F8_TEST_NAHIL_API_KEY` precedent, and the smokes skip rather than fail when a variable is
absent. This is a safety decision, not a naming preference: the unprefixed forms are the *compose*
variables, so keying smokes off them would make `dotnet test` place live billed calls on any
developer machine with a working deployment. The unprefixed names stay with the overlays.

### `/path` provenance

Dropped from FR-5.4, with the reasoning recorded as an amendment on that requirement in `spec.md`.
`/subgraph` echoes `embeddingBackend` and `embeddingIdentity`; `/path` returns a bare array by
contract, so its provenance is the ambient `/status` answer.

### No `package.json` change, and why that is load-bearing

The new overlays are deliberately absent from `env:down` / `env:logs` / `env:status`. An overlay
joins those three only when it **defines a service** (`observability.yml` and `split.yml` do;
`nahil.yml` and both GPU overlays do not). Adding one would also introduce a real bug:
`${F8_OPENAI_API_KEY:?...}` is evaluated at config-parse time for *every* compose subcommand, so
`env:down` would refuse to run from any shell that does not carry the key. The reason is recorded
in a comment beside the overlay map in `env-up.js`, which is where the next reader will look.

### Three smaller calls

- **Embedding boot-time validation is in scope after all.** The shipped docs already promise "the
  same reason is logged once at startup", and leaving the embedding side silent would make that
  sentence false for `Fallen8:Embedding:Backend=Anthropic`, the one backend this feature adds
  specifically to refuse. A non-fatal `LogWarning` whose wording comes from the same validation the
  503 uses, so the two cannot drift. No unknown-name branch for `Onnx`/`LLamaSharp`: they
  legitimately resolve no remote target.
- **Anthropic's `529`** has no `HttpStatusCode` member, so the retry set casts it. Judged
  sufficient with `Explain(529)` returning "overloaded"; the cast carries a one-line comment so it
  reads as deliberate.
- **`describeSemanticSummary`** does surface `embeddingBackend`. A field nothing renders is a field
  nobody trusts, and the wire carries it either way.

### Pre-existing defects found in scope and deliberately NOT fixed

Reported rather than touched, because they belong to a separate honesty pass:

- `fallen-8-mcp/Bridge/Dto/StatusDto.cs`: `EmbeddingStateDto.Model` and `.Dimensions` can never
  bind (`/status` emits `modelName`/`modelVersion`/`dimension`/`intendedMetric`). Dead weight, and
  `Backend` is being added right beside them, so the asymmetry will read as intentional.
- `fallen-8-web-ui/src/components/ConfigurationPanel.tsx`: `ModelStatus`'s `state: "unknown"` is
  unreachable from `modelStatus`, so `StatusRow`'s `bg-warn` dot is dead code.
- `scripts/env-info.js:65` hardcodes `NL assist: http://localhost:11434 (Ollama, default model
  "phi4-f8-mini"...)`, which is already wrong under Nahil today and is wrong under two more
  providers now. Left wrong rather than half-fixed.
- `scripts/env-up.js:186` (before this change) carries an em dash in the MCP banner, against the
  repo's standing no-dash rule. Untouched: not this feature's line.
