# Embedding Provider — Plan

Companion to [spec.md](./spec.md). A capability-gated, lazily-loaded
`IEmbeddingGenerator<string, Embedding<float>>` in the apiApp with three interchangeable
backends (ONNX, LLamaSharp, Ollama), consumed by typed embed/search endpoints and the
`semantic.queryText` traversal path, writing element embeddings through the
element-embeddings surface. Umbrella branch `feature/scan-v2`; feature branch
`feature/embedding-provider` (after element-embeddings merges to the umbrella);
GitHub issue-tracked; per-phase commits; no merge without explicit approval.

Ordering principle: gate + wrapper + fake first (every consumer testable without a model),
then the real backends behind the seam, then the consuming endpoints and the consistency
contract, then ops surface + docs. Every phase ends build-clean with the full suite green.

## Phase 0 — Options, capability, wrapper, fake (FR-1, FR-2)

Intent: the whole feature dark by default, and a test seam that needs no model.

- [ ] `Fallen8EmbeddingOptions` (section `Fallen8:Embedding`, spec §3.2 defaults),
  bound in `Program.cs`; `EmbeddingProvider` capability added to
  `DynamicCapabilityRequirement`/handler + `EmbeddingPolicy`.
- [ ] `EmbeddingModelIdentity` (+ `Stamp()`), `Fallen8EmbeddingProvider` (lazy load,
  failed-load latching, FR-8 output validation), DI registration with the backend
  switch — `"Fake"` backend registered from tests via the `WebApplicationFactory` seam.
- [ ] Tests: flag off → 403 on a probe endpoint, no load on startup or statistics;
  wrapper validation table (dimension, non-finite, zero-norm, latching, concurrent
  single-load).

## Phase 1 — Backends (FR-3, FR-4)

Intent: swappable by config, MIT-only, none loaded until first use.

- [ ] `Ollama`: `OllamaSharp` client as the generator (it implements the abstraction);
  availability-coupling error mapping (503).
- [ ] `Onnx`: `Microsoft.ML.OnnxRuntime` + `Microsoft.ML.Tokenizers` generator (bge
  family: WordPiece vocab, CLS pooling, L2 normalize, `MaxTokens` truncation), batch
  loop.
- [ ] `LLamaSharp`: `LLamaEmbedder` over a GGUF path (+ `Backend.Cpu` pin); README note
  on locating a blob on the `f8-ollama-models` volume + the cross-runtime honesty note.
- [ ] Exact-pinned package references (`CodeQualityTest` green); publish-size measured
  and recorded in the README.
- [ ] Gated live smokes per backend (skip cleanly when model/endpoint absent).

## Phase 2 — Consumption + consistency contract (FR-5…FR-8)

Intent: text-in workflows end to end, hard errors on every identity crack.

- [ ] `EmbeddingController`: `/embedding/element`, `/embedding/elements`,
  `/embedding/search`, `/embedding/text` — spec §3.4 status codes, rate limit, size
  limit, gating; stamps written in the same `SetEmbeddingsTransaction`.
- [ ] `VectorIndex` optional `model` identity option (persisted with the header);
  409 enforcement on embed-to-bound-index and search/queryText mismatch.
- [ ] `semantic.queryText` on path/subgraph (embed once, pre-traversal; 403 when off;
  400 with both `queryText` and `queryVector`).
- [ ] Tests (fake generator): every endpoint happy path + every 400/403/404/409/503,
  batch semantics, stamp on element, bound-index projection after embed, queryText
  traversal with dynamic code off, raw-vector flows still green with provider disabled.

## Phase 3 — Ops surface + docs (FR-9)

Intent: the operator can see what is running and what it costs.

- [ ] `GraphStatisticsREST.embedding` (+ `AppJsonContext` + parity representative);
  never triggers the lazy load; `AppDiagnostics` spans/counters for embedding calls.
- [ ] OpenAPI snapshot regenerated; `features/open/embedding-provider/README.md`
  (backend matrix, config recipes per backend, the GraphRAG text-in recipe, GPU
  posture, OpenAI-compatible extension point).
- [ ] Tests: statistics shape with provider on/off, OpenAPI document test green.

## Phase 4 — Gate

- [ ] Full `dotnet test` green; build 0 warnings/0 errors; engine csproj diff empty.
- [ ] Council review; fixes with pinning tests; merge to `feature/scan-v2` only after
  explicit approval; `features/open/embedding-provider/` → `features/done/` at merge
  to `main`.

## Progress

- [ ] Phase 0 — options, capability, wrapper, fake seam
- [ ] Phase 1 — ONNX / LLamaSharp / Ollama backends
- [ ] Phase 2 — endpoints, queryText, consistency contract
- [ ] Phase 3 — statistics + docs
- [ ] Phase 4 — gate

## Decision / revisit conditions

- **One global provider** — revisit per-named-embedding profiles on a real mixed-model
  workload; the config section is shaped for it.
- **CPU-only in-process backends** — revisit GPU execution providers when embedding
  throughput is a measured bottleneck; the Ollama backend keeps the GPU today.
- **OpenAI-compatible backend docs-only** — implement on the first real request; it is a
  DI registration behind the same abstraction.
- **Publish-size split (plugin assembly)** — revisit if the native-binary weight draws
  real operator complaints.
