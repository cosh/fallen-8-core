# Embedding out of the box — Plan

Phased so every phase builds green and is independently reviewable.

## Phase 1 — API: `embedding` on `GET /status`

- [x] `EmbeddingProviderStatsREST.From(Fallen8EmbeddingProvider)` static factory (null in →
      null out); `StatisticsController` switches to it.
- [x] `StatusREST.Embedding` property (XML-doc'd, nullable) + `AdminController` takes the
      provider as an optional ctor dependency (same pattern as `StatisticsController`) and
      fills the block.
- [x] Tests: `EmbeddingProviderTest.Status_SurfacesProviderState_OnTheCheapDiscoverySurface`
      (disabled + enabled, never triggers the lazy load); `JsonSourceGenParityTest` case
      for `StatusREST` with `Embedding` set.
- [x] Regenerate the OpenAPI snapshot (`scripts/update-openapi-snapshot.ps1`);
      addition only, as expected.

## Phase 2 — Compose: default-on Ollama embedding backend

- [x] `scripts/ollama-init.sh`: pull `bge-m3` unless `F8_EMBEDDINGS` is off (same
      degradation contract as the phi4 pulls).
- [x] `docker-compose.yml`: `F8_EMBEDDINGS` (default `true`) → `fallen8` service
      `Fallen8__Embedding__*` env (Ollama backend, bge-m3, 1024, Cosine,
      `http://ollama:11434`); flag passed to the `ollama` service for the pull gate;
      header docs updated.
- [x] `docker-compose.gpu.yml` reviewed — no change needed (embedding inference rides
      the same sidecar).

## Phase 3 — Studio: gate from `/status`, honest card copy

- [x] `types.ts`: `StatusREST.embedding?: EmbeddingProviderStatsREST | null`.
- [x] `graphShape.ts`: `embeddingProvider(shape)` replaced by `useEmbeddingProvider(instance)`
      — prefers `/status`, falls back to the shape snapshot (pre-field servers); all five
      call sites (Dashboard, Browser, Path, Subgraph, Query) switched.
- [x] `DashboardScreen` card: enabled → identity grid (unchanged); disabled → how to
      enable (`F8_EMBEDDINGS` / `Fallen8:Embedding`); unknown → only "status not
      reported by this server yet".
- [x] `SemanticBlockEditor` + Browser embeddings tab + Query screen: the "Compute the
      Graph shape" provider hint replaced by "not reported by this server".
- [x] `tests/dashboard-provider.test.tsx` (five states incl. fallback + precedence) +
      the four affected screen tests seed the provider via `/status`.

## Phase 4 — Docs + close-out

- [x] embedding-provider README: "Out of the box (docker compose)" section; Ops section
      gains `/status` as the cheap status surface.
- [x] Root README first-start note covers the bge-m3 pull + `F8_EMBEDDINGS`; CLAUDE.md
      architecture note updated.
- [x] Move `features/open/embedding-out-of-box/` → `features/done/`.
- [x] Full `dotnet build` + `dotnet test` + web-ui test run.
