# Instance configuration + unified semantic gateway â€” Spec

Status: **implemented** (all seven phases landed; full .NET + web-ui suites green). Shipped on
the combined `feature/studio-and-config` branch alongside the Studio nav-reorg. Revised after
an adversarial spec review (technical-correctness, cross-feature-completeness, right-sizing).

> **Decision D6 and the "no runtime config mutation" non-goal are RETIRED** (2026-08-19) by
> [writable-instance-config](../writable-instance-config/), invoking this spec's own revisit
> clause ("revisit only on a concrete operator need for live reconfiguration") with that need named.
> `PATCH /config` and a read-write Configuration panel are that feature's contract. Everything below
> is the historical record and is deliberately not rewritten.

## Summary

Give a Fallen-8 instance a small, instance-scoped **Configuration** home in F8 Studio (on the
Connect screen, between Instances and Namespaces) and promote model-serving into a single
**server-side semantic gateway**:

1. **Semantic gateway (server).** Fallen-8 already embeds text server-side
   (`POST /embedding/text` â†’ Ollama/Onnx/LLamaSharp). This feature adds the missing half â€” a
   **server-side chat proxy** (`POST /chat`) forwarding to the Ollama sidecar â€” so the
   instance is the **default gateway** for both embeddings and chat completions.
2. **Default routing through F8.** Studio's NL-assist (delegate/plugin drafting) reroutes to
   **browser â†’ Fallen-8 REST â†’ Ollama** by default. A **custom endpoint** remains a
   **browser-direct** escape hatch (Ollama-compatible, or OpenAI/Anthropic) with any API key kept
   **only in the browser**.
3. **Read-only instance config surface.** A new **`GET /config`** returns the instance's semantic +
   observability state read-only (secrets redacted). Studio renders it in a new Configuration
   section: a **Semantic providers** panel and an **Observability** status ("pushing to `<endpoint>`")
   with a read-only details overlay.
4. **GPU (best-effort).** Where the backend is Ollama, the server probes `GET /api/ps` (VRAM
   residency of the configured model) and reports GPU yes/no; otherwise GPU is simply **not shown**.

Both a server change (chat proxy, `GET /config`, `/status` `chat` capability block, GPU probe) and a
UI change (Connect Configuration section; NL-assist reroute; embedding card leaves the Dashboard).

## Decisions (from design Q&A + review)

| # | Decision | Consequence |
|---|---|---|
| D1 | **Unified server semantic provider** (embeddings + chat), Ollama-backed by default. | New `Fallen8:Chat:*` + `Fallen8ChatProvider` (apiApp-only). Embeddings unchanged. |
| D2 | **Default path = browser â†’ F8 â†’ Ollama** for NL-assist/chat. | New `POST /chat`; Studio `generate.ts` default transport switches to the F8 gateway. |
| D3 | **Custom endpoint = browser-direct**, key held in the browser. | F8 never sees third-party endpoints/keys. |
| D4 | **Remote LLMs (OpenAI/Anthropic) only via the browser-direct custom path.** | No server-held third-party keys; no server proxy to remote providers. |
| D5 | **GPU = best-effort Ollama `/api/ps`; show only when reported.** | No host GPU detection, no in-process GPU backends. |
| D6 | **Instance server config is read-only + guidance** in the UI. | `GET /config` read projection; overlay shows values + env key + "restart to apply". No `PATCH /config`. |
| D7 | **Retire the browser-only NL privacy rule (FR-26.11) for the default path.** | Docs updated honestly; the surviving guarantee is D3 (custom keys never reach F8). |
| D8 | **The server owns the chat model** (`Fallen8:Chat:Ollama:Model`); the default path has **no client model knob**. | `POST /chat` body carries no `model`; mirrors the embedding gateway's server-owned model. |
| D9 | **`GET /config` is API-key-gated** (like `/statistics`); **both `/config` and `/chat` are deferred from MCP**; agents get chat capability discovery via a `chat` block added to the already-bridged `/status`. | Resolves the config-auth-vs-MCP-bridge conflict; keeps the agent surface minimal. |

## Non-goals (right-sizing â€” single-process, self-hosted reality)

- **No runtime config mutation** (`PATCH /config`); all `Fallen8:*` stays startup-only. Revisit only
  on a concrete operator need for live reconfiguration.
- **No server-held third-party keys / no server proxy to OpenAI/Anthropic** (D3/D4).
- **No host/process GPU detection, no in-process GPU embedding backends.** GPU is best-effort from
  Ollama only (D5).
- **No streaming chat** in v1 (`POST /chat` is request/response). Revisit if the NL-assist UX
  needs token streaming.
- **No chat model-identity stamp / fatal-validation latch** on the chat provider (those exist for
  embeddings only because vectors are stored/indexed under a dimension/metric contract; a chat
  completion has none). chat provider carries only `IsEnabled`/`IsLoaded` + per-call failure.
- **No `identity`/tenant block in `ConfigREST` v1** (Studio already identifies instances via its
  registry; add only on a concrete need).
- **No per-instance NL routing preference in v1** â€” the browser NL routing choice stays a single
  global preference; instance mode simply targets the *active* instance. Revisit if operators need
  different backends per registered instance.

## Architecture

### Server: the chat provider + proxy

Mirror the embedding provider's *shape* (apiApp-only, `IOptions`-bound, lazy, process-wide
singleton) but **not** its identity/latch machinery (non-goal):

- `Fallen8ChatOptions` (`Fallen8:Chat:*`) + `Fallen8ChatProvider` â€” `IsEnabled`, `IsLoaded`, an Ollama
  chat client (`OllamaSharp.IOllamaApiClient.ChatAsync`, already on the current package set â€” no new
  NuGet). Backend `Ollama` reuses the sidecar endpoint. Disabled â‡’ `POST /chat` â†’ 403.
- `POST /chat` on a new `ChatController` (`[Fallen8Level]` â€” instance-wide, no `/ns/{ns}` twin;
  `[Fallen8Level]` exists and `NamespaceRouteConvention` skips it), gated by a new `Fallen8.Chat`
  authorization policy (enabled flag), `[EnableRateLimiting(SensitiveRateLimitPolicy)]`, and
  `[RequestSizeLimit(1_048_576)]` (reuse the `/embedding/text` pattern â€” **no new `MaxInputBytes`
  knob**).
  - **Request:** `{ messages: [{role, content}], options?: {temperature?, â€¦} }` â€” **no `model`**
    (D8; the server owns `Fallen8:Chat:Ollama:Model`).
  - **Response (`ChatResultREST`):** `{ content, model, stats: { promptTokens, completionTokens,
    durationMs, tokensPerSecond } }`. Stats come from OllamaSharp's native non-streaming
    `ChatAsync` done-response (the `Microsoft.Extensions.AI` `IChatClient` abstraction exposes token
    counts but not durations/tps, so the proxy uses the native client). This preserves the
    generation-stats surface **nl-assist-ux FR-5** depends on (both NL panels render it).
  - **Message content is excluded from spans/logs** (mirror the embedding endpoint's tag hygiene);
    prompt text is never emitted to telemetry.
  - **Errors:** 400 (empty `messages` / oversized or malformed body), 401 (no credential when a key
    is set), 403 (disabled), 429 (rate limit), 502 (Ollama returned a garbled/non-JSON response),
    503 (backend unreachable), **504 (proxy exceeded `Fallen8:Chat:TimeoutSeconds`)**.
- **Embeddings unchanged.** `POST /embedding/text` is the embedding half. No churn beyond surfacing
  embedding state under `GET /config`.

### Server: `/status` gains a `chat` capability block; `GET /config` is the operator view

- **`GET /status` (additive, non-breaking):** add an optional `chat` block mirroring the existing
  `embedding` block (`enabled`, `backend`, `model`, `loaded`, `gpu?`). This is the capability-state
  home (symmetry with embedding, which already lives on `/status`), and it lets the already-bridged
  MCP `f8_overview` expose `chatEnabled` alongside `embeddingEnabled`. **`/status`'s existing
  `embedding` block is left exactly as-is** (no "trim to loaded" â€” that was a mistaken breaking
  change; the "one home" rule governs prose, not identical JSON projections). `/statistics` is
  **not** touched (chat is unrelated to graph shape).
- **`GET /config` (NEW, `[Fallen8Level]`, API-key-gated like `/statistics`):** the operator config
  aggregate. Reuses `EmbeddingProviderStatsREST` **verbatim** (it is a NEW aggregate home, not a
  move):

```
ConfigREST {
  semantic: {
    embedding: EmbeddingProviderStatsREST,   // reused verbatim
    chat:      ChatProviderStatsREST { enabled, backend, model, loaded, gpu? },
  },
  observability: {
    otlp:       { enabled, endpoint },        // endpoint emitted as configured; never a secret
    prometheus: { enabled, requireApiKey },
    tracingSamplingRatio, statisticsElementBudget, statisticsTopN,
  },
  security: { apiKeyRequired },               // boolean only â€” never the key value
}
```

- **Redaction:** never emit `Fallen8:Security:ApiKey`. `Otlp.Endpoint` is emitted as configured
  (this codebase's OTLP options carry only an endpoint â€” no creds/headers to redact).
- **GPU probe (`gpu?`):** best-effort. Read the Ollama endpoint from `IOptions<Fallen8*Options>`
  (not via the provider, which hides its client behind `Lazy`), call
  `IOllamaApiClient.ListRunningModelsAsync()` (`/api/ps`), and set `gpu=true` when the **configured
  model** is resident with `SizeVram > 0`. **Bounded short timeout; on any failure or model-not-
  resident, `gpu` is `null` and the UI shows nothing.** It is a point-in-time residency read (Ollama
  unloads idle models), documented as such on the DTO and in the UI â€” not static config.

### Browser: default gateway + custom escape hatch (NL-assist)

- The NL store already persists `mode: 'builtin' | 'custom'` (zustand, version 1, with a migrate
  fn). **Rename `builtin` â†’ `instance`** and **bump persist version to 2** with a migration mapping
  stored `'builtin'` â†’ `'instance'` (otherwise those users silently fall through to custom-mode
  logic). Update the coupled helpers: `BUILTIN_NL_BACKEND`, `effectiveNlConfig`, `isNlConfigured`,
  `usesApiKey`, the `nl-builtin-hint` testid, and `nl-config` tests.
- `generate.ts` gains a transport selector: **instance** mode = `POST {activeInstanceBaseUrl}/chat`
  (carries the instance's API key like any F8 call; combines the *global* NL store with the *active*
  instance's baseUrl+key); **custom** mode = browser-direct (`/api/chat` Ollama, `/v1/chat/completions`
  OpenAI-compatible), key in the browser (unchanged). Instance-mode parses `ChatResultREST.stats`;
  custom-mode keeps the existing native-Ollama/OpenAI parsing.
- **Egress notice:** custom-direct keeps the "text leaves this machine" notice. Instance mode shows
  **no** egress notice â€” the prompt goes to the same instance the user already trusts with their
  entire graph (same trust boundary), whether that instance is local or remote. This rule is stated
  explicitly in the UI/docs so there is no silent-egress ambiguity.

### Studio: Connect "Configuration" section

Inserted between the Instances `<section>` and `<NamespacesPanel/>` (ConnectScreen `space-y-4`), as
**two clearly separated concerns**:

- **This instance (read-only server config)** â€” sourced from `GET /config` via a new `useConfig`
  hook: the **Semantic providers** view (embedding + chat: backend/model/â€¦/loaded, GPU where
  reported) and an **Observability** status line ("pushing metrics+traces+logs to `<endpoint>`" /
  "Prometheus at `/metrics`" / "off") with a **Configureâ€¦** Radix overlay showing values + the exact
  `Fallen8__â€¦` env keys + a "restart to apply" note (D6).
- **Model routing (your browser)** â€” the NL backend selector (instance vs custom endpoint),
  explicitly labelled a **global browser preference** (not instance config). Instance mode routes to
  the active instance's `/chat`.
- The **embedding-provider card is removed from the Dashboard** and lives here.

## REST contract (new/changed)

| Method | Path | Scope | Auth | Notes |
|---|---|---|---|---|
| `POST` | `/chat` | Fallen-8-level | API key + `Fallen8.Chat` policy; sensitive rate limit + 1 MiB cap | NEW. Server-owned model (no `model` in body). Errors 400/401/403/429/502/503/504. Content excluded from telemetry. |
| `GET` | `/config` | Fallen-8-level | API-key-gated (like `/statistics`) | NEW. Read-only aggregate (semantic + observability + apiKeyRequired). Secrets redacted. |
| `GET` | `/status` | unchanged scope | anonymous (unchanged) | ADD optional `chat` capability block mirroring `embedding`; existing fields unchanged. |
| `POST` | `/embedding/text` | unchanged | unchanged | Existing textâ†’vector proxy (embedding half). |
| `GET` | `/statistics` | unchanged | unchanged | Untouched. |

## Config keys (new)

```
Fallen8:Chat:Enabled           (bool, default false â€” 403 when off)
Fallen8:Chat:Backend           (Ollama; only Ollama in v1)
Fallen8:Chat:Ollama:Endpoint   (default reuse the embedding sidecar, http://localhost:11434)
Fallen8:Chat:Ollama:Model      (default phi4-f8-mini â€” SERVER-owned; no client override)
Fallen8:Chat:TimeoutSeconds    (proxy timeout â†’ 504 on exceed)
```

Compose: `F8_CHAT` (default true when the sidecar is present) â†’ `Fallen8__Chat__Enabled`, reusing the
existing Ollama sidecar + `F8_DELEGATE_REPO` model. GPU wiring unchanged (docker-compose.gpu.yml).
`OLLAMA_ORIGINS` is no longer needed for the *default* path (F8â†’Ollama is server-to-server); it
remains needed only for the browser-direct *custom* Ollama path.

## Security & privacy

- Default NL/chat traffic now transits the instance (D2), **retiring FR-26.11's "never through the
  instance"** for the default path. Surviving guarantee: **custom endpoints and their API keys are
  browser-direct and never reach F8** (D3/D4). Verified in review: no design path lets F8 see a
  third-party key.
- `POST /chat` is a sensitive endpoint (API-key gated when a key is set, rate-limited, size
  capped) and **excludes message content from telemetry** â€” same tag hygiene as embeddings.
- `GET /config` redacts all secrets and is API-key-gated (D9).

## Impact on existing features (mandatory cross-feature sweep)

- **embedding-provider**: conceptually generalized; **no behavioural change** to embeddings; living
  doc + `docs/semantic-traversal.md` gain a pointer to the chat sibling and the config home.
- **nl-assist / nl-assist-ux**: default transport reroutes through F8 (D2); `generate.ts`, the NL
  store (mode rename + v2 migration), `NlBackendConfig.tsx`, `NlAssistPanel.tsx` **and**
  `PluginNlAssistPanel.tsx`, and the egress-notice logic change. **nl-assist-ux FR-5 generation-stats
  must be preserved** via `ChatResultREST.stats`.
- **nl-assist-feedback-loop**: its inherited premise ("the intent never reaches a Fallen-8 instance;
  spans exclude filter text") is **retired for the default path** â€” record consciously. Feedback
  capture/consolidation stays browser-side and unchanged; `/chat` excludes prompt content from
  spans/logs so the "server never sees the intent" telemetry guarantee is preserved even though the
  request transits F8. Add this feature to the sweep in its living doc.
- **nl-assist-finetune / RETRAIN-LOG.md**: model/prompt/drafted-surface unchanged â€” transport-only.
  **No retrain entry** (conforms to RETRAIN-LOG conventions; recorded consciously).
- **MCP (engineâ†’RESTâ†’MCP rule)**: `POST /chat` and `GET /config` are **conscious deferrals**
  (one-line `Deferrals` rules in `McpRestCoverageTest`: chat gateway is Studio's model path; config
  is operator-facing). The new chat **capability** is still surfaced to agents: extend
  `StatusDto`/`OverviewTool` (`f8_overview`) with `chatEnabled` from the new `/status.chat` block
  (additive; the bridge ignores unknown fields, so it is non-breaking). Update `McpBridgedEndpoints`
  only if a bridge is added (none here); `McpContractTest` unaffected by deferrals.
- **OpenAPI snapshot** (`features/done/web-ui/openapi-v0.1.json`): regenerate via
  `scripts/update-openapi-snapshot.ps1` â€” `OpenApiDocumentTest.MatchesThePinnedSnapshotInventory`
  trips on the two new operations until regenerated. (`web-ui api-contract.test` only covers client
  calls it invokes; add `getConfig` to `endpoints.ts` + that test's call list. `generate.ts` uses a
  bespoke transport, so `/chat` is not covered by api-contract.test.)
- **JSON source-gen gate**: register `ConfigREST`, `ChatProviderStatsREST`, `ChatSpecification`,
  `ChatResultREST` (and the `/status` `chat` block type) in `AppJsonContext` with
  `[JsonSerializable]`, and extend `JsonSourceGenParityTest` coverage.
- **Studio**: Dashboard loses the embedding card; Connect gains the Configuration section; new
  `getConfig` + `ConfigREST` type + `useConfig` hook; NL transport change. Tests
  (`dashboard-provider`, new `connect-config`, `nl-*`) + e2e updated; **screenshots redone**
  (Connect, Dashboard).
- **Architecture diagrams** (mandatory, precise targets):
  - `README.md` mermaid: **only** relabel the `rest -.->|embeddings| sidecar` edge (line ~98) to
    `embeddings + chat`. (README's `studio` already points only at `rest`; there is no browserâ†’sidecar
    edge to redirect.)
  - `docs/architecture.md`: redirect/scope the `studio -.->|assist calls, direct from browser|
    sidecar` edge (line ~54) â€” the default assist path now flows studioâ†’restâ†’sidecar (keep a
    custom-direct note); rewrite the "the browser calls the model backend â€¦ directly, so no model
    traffic passes through the engine" prose (lines ~126-128); update the `embed -.-> sidecar` node
    (~59) to the semantic gateway (embeddings + chat). Brand style unchanged (`#E2001A`).
- **docs**: `docs/studio.md` â€” Configuration section + NL reroute, the screen-table row (~line 16)
  and Dashboard paragraph (~line 39) drop the embedding card, and the OLLAMA_ORIGINS/reachability
  lines (~134/143) are updated (default path no longer needs `OLLAMA_ORIGINS`). `docs/troubleshooting.md`
  â€” new default failure modes (F8 403/503 vs browser CORS/404). `docs/running.md` â€” env table gains
  `F8_CHAT`. `docs/semantic-traversal.md` â€” chat sibling + config home. `README.md` key-feature line
  (~59) reworded ("assist that runs through your instance by default, or a browser-direct custom
  backend"). `docs/delegates.md` is only a see-also link (no FR-26.11 rule) â€” light touch, not a
  "retire the rule" target. **No standalone `docs/semantic-gateway.md`** â€” sections in
  `docs/studio.md` + `docs/semantic-traversal.md` (avoid a fourth home).
- **stored-queries, subgraph, graph-namespaces, save-games, plugins, change-feed**: no impact.

## Open questions (resolved unless noted)

1. **`GET /config` auth** â†’ **API-key-gated** (like `/statistics`); MCP deferral removes the
   anonymous-bridge conflict. (Resolved, D9.)
2. **`/chat` model selection** â†’ **server-owned, no client knob** (D8). (Resolved.)
3. **Instance-mode model discovery** â†’ Studio shows the server-configured model from `GET /config`;
   **no `/api/tags` model picker in v1** (right-sized). Revisit if a picker is wanted.
4. **Feature packaging** â†’ ship as one feature `instance-config`, phased (server gateway â†’ config
   read â†’ Studio â†’ NL reroute â†’ docs/diagrams/screenshots). (Resolved.)

