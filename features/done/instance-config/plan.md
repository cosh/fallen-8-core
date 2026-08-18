# Instance configuration + unified semantic gateway — Plan

Phased implementation of [spec.md](./spec.md). Feature branch `feature/instance-config` off `main`
(never commit to main). Build clean (warnings-are-errors) and tests green at each phase boundary.

## Phase 1 — Server: chat proxy

- `Fallen8ChatOptions` (`Fallen8:Chat:*`, section constant) + DI `Configure<T>` (startup-only `IOptions`).
- `Fallen8ChatProvider` (apiApp-only): `IsEnabled`/`IsLoaded` + a lazy `OllamaSharp` chat client;
  **no** model-identity stamp, **no** fatal-validation latch (non-goal); per-call 503 on backend
  failure. Reuse the embedding Ollama endpoint by default.
- Authorization: add the `Chat` member to the capability enum **and its `switch` case** in
  `DynamicCapabilityAuthorizationHandler` (it has a `default: throw`, so a new member without a case
  throws at runtime), inject `IOptions<Fallen8ChatOptions>` into the handler, and `AddPolicy`
  (`Fallen8.Chat`) in `Program.cs` alongside the embedding policy.
- `ChatController.Chat` → `POST /chat` (`[Fallen8Level]`, `[EnableRateLimiting(SensitiveRateLimitPolicy)]`,
  `[RequestSizeLimit(1_048_576)]`, `[Consumes]`/`[Produces]`/`[ProducesResponseType]` + XML docs).
  DTOs `ChatSpecification { messages, options? }` (no `model`) and `ChatResultREST { content,
  model, stats:{promptTokens,completionTokens,durationMs,tokensPerSecond} }` via native non-streaming
  `ChatAsync`. Errors 400/401/403/429/502/503/504 (timeout from `Fallen8:Chat:TimeoutSeconds`).
  **Exclude message content from spans/logs.**
- Best-effort GPU probe helper: read the Ollama endpoint from `IOptions`, call
  `ListRunningModelsAsync()` (`/api/ps`), match the configured model, `gpu=true` iff `SizeVram>0`;
  short bounded timeout; `null` on failure/not-resident.
- Register all new DTOs in `AppJsonContext` (`[JsonSerializable]`) and extend `JsonSourceGenParityTest`.
- **Tests (MSTest):** disabled→403, rate limit→429, backend-down→503, garbled→502, timeout→504,
  happy path (loopback/fake) with stats populated, GPU probe present/absent/timeout.

## Phase 2 — Server: `/status` chat block + `GET /config`

- Add the optional `chat` block to `StatusREST` (mirror `embedding`); **leave the existing `embedding`
  block and `/statistics` untouched**. Populate `chat` (+ GPU probe) in `AdminController.Status()`.
- `ConfigREST` (+ `ChatProviderStatsREST`) reusing `EmbeddingProviderStatsREST` verbatim.
  `AdminController.Config` → `GET /config` (`[Fallen8Level]`, API-key-gated), injecting
  `IOptions<Fallen8ObservabilityOptions>` (not currently injected) for the observability view and the
  "pushing to `<endpoint>`" line. Redaction: no ApiKey; endpoint as configured.
- Register the new DTOs in `AppJsonContext` + parity test.
- **Tests:** `/config` shape + redaction (no secret leaks), observability enabled/disabled projection,
  `/status.chat` present, `/status.embedding` and `/statistics` unchanged, GPU shown/hidden.

## Phase 3 — MCP + snapshot + gates

- Regenerate the OpenAPI snapshot (`scripts/update-openapi-snapshot.ps1`); review the diff.
- MCP: add **conscious deferrals** for `POST /chat` and `GET /config` in `McpRestCoverageTest`
  (narrow exact-match rules, disjoint from bridged set). Surface the chat capability to agents by
  extending `StatusDto` + `OverviewTool` (`f8_overview`) with `chatEnabled` from `/status.chat`
  (additive). Confirm `OpenApiDocumentTest`, `McpRestCoverageTest`, `McpContractTest`, web-ui
  `api-contract.test` all pass.

## Phase 4 — Studio: config API + Connect Configuration section

- `getConfig` wrapper (in `endpoints.ts`, added to the `api-contract.test` call list) + `ConfigREST`
  TS type + `useConfig` hook (`[instance.id,'config']`).
- New `ConfigurationPanel` in ConnectScreen between Instances and `<NamespacesPanel/>`, two concerns:
  (a) **This instance** — read-only semantic (embedding + chat, GPU where reported) + Observability
  status line + Radix **Configure…** overlay (read-only values + env keys + "restart to apply");
  (b) **Model routing (your browser)** — the NL selector, labelled a global browser preference.
- Remove the embedding-provider card from `DashboardScreen`.
- **Tests:** `connect-config` render (semantic + observability, GPU shown/hidden, overlay); dashboard
  no longer shows the embedding card (update `dashboard-provider`).

## Phase 5 — Studio: NL-assist reroute + custom escape hatch

- NL store: rename `mode` `'builtin'`→`'instance'`, **bump persist version to 2 with a migration**
  (`'builtin'`→`'instance'`); update `BUILTIN_NL_BACKEND`, `effectiveNlConfig`, `isNlConfigured`,
  `usesApiKey`, `NlBackendConfig.tsx`, and the `nl-builtin-hint` testid.
- `generate.ts`: instance mode → `POST {activeInstanceBaseUrl}/chat` (combine the global NL store
  with the active instance baseUrl+key), parse `ChatResultREST.stats`; custom mode unchanged
  (browser-direct). Scope the egress notice to custom-direct only.
- Ensure both `NlAssistPanel.tsx` and `PluginNlAssistPanel.tsx` still render generation stats.
- **Tests:** transport selection (instance vs custom), persisted `'builtin'` state migrates,
  notice gating; update `nl-*` tests + e2e scenario 10.

## Phase 6 — Docs, architecture diagrams, screenshots

- Docs: `docs/studio.md` (Configuration section; NL reroute; screen-table row + Dashboard paragraph
  drop the embedding card; OLLAMA_ORIGINS/reachability lines updated), `docs/semantic-traversal.md`
  (chat sibling + config home), `docs/observability.md` (status surfacing), `docs/troubleshooting.md`
  (new default failure modes), `docs/running.md` (env table gains `F8_CHAT`), `README.md` key-feature
  reword + `docs/` index. **No standalone semantic-gateway page** (sections only).
- **Retire FR-26.11** honestly in the affected docs (surviving custom-direct guarantee stated).
- **Architecture diagrams (both):** `README.md` mermaid — relabel the `rest→sidecar` edge to
  `embeddings + chat`. `docs/architecture.md` — redirect the `studio -.->|assist calls, direct from
  browser| sidecar` edge (default path now via `rest`; custom-direct noted), rewrite the "directly …
  no model traffic passes through the engine" prose, update the `embed→sidecar` node to the semantic
  gateway. Brand style unchanged.
- **Screenshots:** redo Connect (Configuration section), Dashboard (embedding card gone), NL-assist
  panel (instance vs custom), via the built SPA + local apiApp + Playwright harness (same as the
  Studio nav-reorg screenshot flow).

## Phase 7 — Cross-feature verification

- Full `dotnet test` + web-ui suite; confirm every quality gate (warnings-as-errors, convention
  tests, OpenAPI snapshot, MCP coverage/contract, JSON parity).
- Confirm the sweep outcomes recorded in the spec (nl-assist-feedback-loop conscious note; **no
  RETRAIN-LOG entry**).
- Move `features/open/instance-config/` → `features/done/instance-config/` on merge.
