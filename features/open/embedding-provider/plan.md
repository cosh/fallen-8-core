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

- [x] `Fallen8EmbeddingOptions` (section `Fallen8:Embedding`, spec §3.2 defaults),
  bound in `Program.cs`; `EmbeddingProvider` capability added to
  `DynamicCapabilityRequirement`/handler + `EmbeddingPolicy` (same 401/403 posture as the
  code/plugin capabilities).
- [x] `EmbeddingModelIdentity` (+ the `Stamp` string), `Fallen8EmbeddingProvider` (lazy
  DI-deferred load, `Lazy` creation-failure latching + dimension-contradiction latch,
  FR-8 output validation), DI registration with the backend switch — tests replace the
  `IEmbeddingGenerator` registration via `ConfigureTestServices` (no "Fake" config value
  needed).
- [x] Tests (`EmbeddingProviderTest`): flag off → 403 on every surface (authenticated
  caller), statistics without load; wrapper validation table (dimension latch,
  non-finite/zero-norm 502s, disabled short-circuit, creation-failure latching).

## Phase 1 — Backends (FR-3, FR-4)

Intent: swappable by config, MIT-only, none loaded until first use.

- [x] `Ollama`: `OllamaSharp` client as the generator (it implements the abstraction);
  availability-coupling error mapping (503, not latched). Pinned 5.4.10: the newer
  packages ship a consumer-side source generator requiring a newer Roslyn than the SDK
  (`ExcludeAssets=analyzers` kept as documentation of intent).
- [x] `Onnx`: `Microsoft.ML.OnnxRuntime` + `Microsoft.ML.Tokenizers` generator (bge
  family: WordPiece vocab, CLS/mean pooling, L2 normalize, `MaxTokens` truncation,
  input-name-driven tensor feeding).
- [x] `LLamaSharp`: `LLamaEmbedder` over a GGUF path (+ `Backend.Cpu` pin, serialized
  through a semaphore); README notes the volume-blob reuse + the cross-runtime honesty
  note.
- [x] Exact-pinned package references (`CodeQualityTest` green); publish-size noted in
  the README as the in-process price.
- [x] Gated live smokes per backend (`EmbeddingBackendSmokeTest`, `[Ignore]` + env vars,
  Inconclusive when absent).

## Phase 2 — Consumption + consistency contract (FR-5…FR-8)

Intent: text-in workflows end to end, hard errors on every identity crack.

- [x] `EmbeddingController`: `/embedding/element`, `/embedding/elements`,
  `/embedding/search`, `/embedding/text` — spec §3.4 status codes, rate limit, size
  limit, gating; stamps ride the same `SetEmbeddingsTransaction`
  (`EmbeddingSetDefinition.ModelStamp`, WAL-coded; a bring-your-own-vector write CLEARS
  a stale stamp so provenance never lies).
- [x] `VectorIndex` `model` identity option landed with element-embeddings Phase 3
  (persisted in the extended header); 409 enforcement on embed-to-bound-index and
  search mismatch here.
- [x] `semantic.queryText` on path/subgraph (embed once, pre-traversal/registration; 403
  when off via the same capability requirement; 400 with both `queryText` and
  `queryVector`; the resolved vector rides the subgraph recipe, so replay never needs
  the provider).
- [x] Tests (fake generator): endpoint happy paths + 400/403/404/409 table, batch
  semantics, stamp on element + WAL round-trip + clear-on-raw-overwrite, bound-index
  projection after embed, queryText traversal end-to-end, identity-match search accept.

## Phase 3 — Ops surface + docs (FR-9)

Intent: the operator can see what is running and what it costs.

- [x] `GraphStatisticsREST.embedding` (+ `AppJsonContext` + parity representative);
  never triggers the lazy load (pinned by a test). (`AppDiagnostics` spans deferred: the
  provider call sits inside request spans already; revisit with real operator demand.)
- [x] OpenAPI snapshot regenerated (additions only);
  `features/open/embedding-provider/README.md` (backend matrix, config recipes, the
  text-in GraphRAG recipe, GPU posture, extension point, publish-size note).
- [x] Tests: statistics shape with provider on/off + loaded-after-first-use, OpenAPI
  document test green.

## Phase 4 — Gate

- [ ] Full `dotnet test` green; build 0 warnings/0 errors; engine csproj diff empty.
- [ ] Council review; fixes with pinning tests; merge to `feature/scan-v2` only after
  explicit approval; `features/open/embedding-provider/` → `features/done/` at merge
  to `main`.

## Progress

- [x] Phase 0 — options, capability, wrapper, fake seam
- [x] Phase 1 — ONNX / LLamaSharp / Ollama backends
- [x] Phase 2 — endpoints, queryText, consistency contract
- [x] Phase 3 — statistics + docs
- [ ] Phase 4 — gate (council review + merge pending explicit approval)

## Decision / revisit conditions

- **One global provider** — revisit per-named-embedding profiles on a real mixed-model
  workload; the config section is shaped for it.
- **CPU-only in-process backends** — revisit GPU execution providers when embedding
  throughput is a measured bottleneck; the Ollama backend keeps the GPU today.
- **OpenAI-compatible backend docs-only** — implement on the first real request; it is a
  DI registration behind the same abstraction.
- **Publish-size split (plugin assembly)** — revisit if the native-binary weight draws
  real operator complaints.
