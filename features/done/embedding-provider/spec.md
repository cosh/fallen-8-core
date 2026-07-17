# Embedding Provider — Specification

> **Status:** Draft, spec only (no implementation yet). Follow the feature workflow in the
> repository root `CLAUDE.md`. Umbrella branch: `feature/scan-v2`; feature branch:
> `feature/embedding-provider` (GitHub issue-tracked; per-phase commits; no merge without
> explicit approval). Builds on [element-embeddings](../element-embeddings/spec.md) —
> implement that first.
>
> **Companion feature:** [element-embeddings](../element-embeddings/spec.md) is the
> *storage + dual-consumption* half (embeddings as element state, consumed by the
> `VectorIndex` and by traversal). This feature is the *generation* half: the component
> that turns text into embeddings, with a swappable backend. The storage half never
> requires this one — bring-your-own-vector stays first-class there; this one writes
> through its accessor surface. The planned [mcp-server](../../open/mcp-server/spec.md) is the
> next consumer of the same abstraction.

## 1. Overview & requirements

Fallen-8 stores and scans embeddings (`fallen-8-core/Index/Vector/VectorIndex.cs`,
element-embeddings) but has never *produced* one: the vector-index spec §2 pinned
"embedding generation inside the database — never in-engine; clients supply vectors." That
posture is correct for `fallen-8-core` and it does not change here. What changes: the
**apiApp** (Fallen-8's ASP.NET layer, `fallen-8-core-apiApp`) gains an optional,
capability-gated embedding provider, so an operator who wants text-in workflows —
embed-to-element, semantic search, semantic traversal — gets them from the box they
already run, while the engine process stays model-free by default and the engine project
stays model-free forever.

The deployment already ships inference infrastructure: the `ollama` container in
`docker-compose.yml` (official image, `phi4-mini` pulled by `ollama-init`, weights on the
`f8-ollama-models` named volume, GPU handed over by `docker-compose.gpu.yml`). That
container serves the browser-side NL assist and is one of this feature's interchangeable
backends — with an *embedding* model, not phi (a generative chat model is the wrong tool
for retrieval embeddings).

### FR summary

- FR-1 **The abstraction IS `Microsoft.Extensions.AI.IEmbeddingGenerator<string,
  Embedding<float>>`.** No new interface is defined. *(Load-bearing decision — not to be
  relitigated.)* A thin `Fallen8EmbeddingProvider` wrapper exists ONLY to add (a) required
  model-identity metadata — name, version, dimension, intended metric — and (b) index
  add-contract validation at the provider boundary. Swapping the backend is a
  configuration change, never a code change.
- FR-2 **Placement & gating.** The provider lives in `fallen-8-core-apiApp` — NOT in
  `fallen-8-core` — and is later reused by the planned mcp-server. It sits behind a
  capability flag exactly like the dynamic-code switch: `Fallen8:Embedding:Enabled`
  defaults **false**, enforced through the existing authorization machinery
  (`DynamicCapabilityRequirement` / `DynamicCapabilityAuthorizationHandler`,
  `fallen-8-core-apiApp/Security/DynamicCapabilityAuthorization.cs`) with a new
  `EmbeddingProvider` capability and policy. Off means: no model loads, nothing downloads,
  gated endpoints answer 403 (`application/problem+json`). Model load is **lazy, on first
  use** — enabling the flag alone still loads nothing.
- FR-3 **MIT-only** libraries and default weights for local providers.
- FR-4 **Interchangeable backends** behind the one abstraction (§3.3): **ONNX**
  (`Microsoft.ML.OnnxRuntime` + `Microsoft.ML.Tokenizers`; `bge-m3`, 1024-dim, or
  `bge-micro-v2`, 384-dim — both MIT; self-contained, no sidecar), **LLamaSharp** (a GGUF
  model file, reusing a blob already on the `f8-ollama-models` volume so the weights exist
  once on disk), **Ollama** (the already-shipped container's embed endpoint via
  `OllamaSharp`, which implements the abstraction natively — this couples availability to
  the Ollama container, stated explicitly), and a **documented OpenAI-compatible remote
  extension point** (config shape reserved; not implemented in v1).
- FR-5 **Consumption.** A capability-gated apiApp controller: text-in embed-to-element
  (single + batch for bulk ingestion), semantic search (text → kNN), raw text-to-vector;
  plus `queryText` on the element-embeddings `semantic` traversal block (embedded once,
  up front, before the traversal starts). Later mcp-server tools use the same abstraction.
- FR-6 **Threading.** Generation runs on request threads (or their async continuations),
  never on the single transaction-writer thread, and never blocks a commit: embed first,
  then enqueue the `SetEmbeddingsTransaction`. For semantic traversal the single up-front
  query embedding goes through the provider before the traversal starts; no per-element
  inference, ever. *(Load-bearing.)*
- FR-7 **Writes through the accessor surface.** The provider persists results as element
  embeddings via the element-embeddings write path (`SetEmbeddingsTransaction`), which
  triggers the bound-index projection when one exists. It never touches the slab or the
  property store directly.
- FR-8 **Consistency contract** (ties this spec to element-embeddings + `VectorIndex`):
  a vector index is bound to one model identity (new optional `model` plugin option);
  add-time and query-time embeddings for that index must report a matching
  name/version/dimension. The dimension the provider reports must equal the index's fixed
  dimension and the named embedding's dimension — a mismatch is a **hard error, never
  silent coercion**. Provider output must satisfy the index add contract at the provider
  boundary: finite floats, exact declared length, non-zero norm under Cosine. The model
  identity is stamped onto stored element embeddings so query-time drift is detectable.
- FR-9 **Config & ops.** Provider selection and model config via appsettings/env
  (`Fallen8:Embedding:*`); the active provider and model identity surface in
  `GET /statistics` (`GraphStatisticsREST`).

## 2. Goals / non-goals

**Goals** — FR-1 … FR-9, with the backends of FR-4 shipped and tested (fakes in CI, gated
live smokes for real models).

**Non-goals** (each with its revisit trigger)

- **No model runtime in `fallen-8-core`, no inference on the commit or traversal path, and
  the default deployment stays model-free.** *(Cross-cutting with the companion spec.)*
  The engine project gains zero packages; the apiApp's providers are dark until the flag
  is on **and** first use arrives. Never revisit in-engine.
- **No approximate/ANN index.** Exact SIMD brute-force kNN stays (vector-index §2
  trigger unchanged). *(Cross-cutting.)*
- **No re-embedding orchestration in the engine.** A model change is an external
  re-index into a new index (new embeddings under a new name or a fresh corpus walk by the
  operator/agent). The FR-8 stamps make drift *detectable*; correcting it stays outside.
  Revisit as mcp-server/skill-library material when agent usage shows the loop is common.
  *(Cross-cutting.)*
- **No OpenAI-compatible remote backend implementation in v1.** Documented extension
  point only (config shape + where the `IEmbeddingGenerator` would be registered).
  Revisit when a real operator asks; the abstraction makes it a DI registration.
- **No per-named-embedding provider selection (mixed models) in v1.** One active provider
  per process (single operator, one box — the spec-right-sizing rule). The FR-8 stamps
  keep mixed histories detectable. Revisit when a real workload runs two models
  side-by-side; the config section is already shaped for named profiles.
- **No auto-embedding on element create/update.** Embedding is an explicit call (or an
  explicit bulk pass). Revisit alongside mcp-server ingestion tooling.
- **No GPU wiring for the in-process backends in v1.** ONNX/LLamaSharp run CPU-only
  (`LLamaSharp.Backend.Cpu`); the GPU, when present, stays with the Ollama sidecar
  (`docker-compose.gpu.yml` hands it `count: all`) — two runtimes competing for one device
  is an operational footgun with no measurement behind it. Revisit (ONNX CUDA EP /
  LLamaSharp CUDA backend as config) when a real corpus shows CPU embedding throughput is
  the bottleneck. *(Resolves the GPU-coexistence open question.)*
- **No prompt-shaping/instruction templates** (e.g. bge query-vs-passage prefixes) beyond
  a single optional configured query prefix. Revisit with real retrieval-quality feedback.

## 3. Design sketch

### 3.1 The wrapper (and why it is thin)

```
fallen-8-core-apiApp/Embedding/
  EmbeddingModelIdentity.cs      name, version, dimension, intendedMetric (+ Stamp() string)
  Fallen8EmbeddingProvider.cs    the ONLY consumer-facing type
  OnnxEmbeddingGenerator.cs      IEmbeddingGenerator over OnnxRuntime + ML.Tokenizers
  LLamaSharpEmbeddingGenerator.cs IEmbeddingGenerator over LLamaSharp's LLamaEmbedder
  (Ollama needs no adapter: OllamaSharp's OllamaApiClient implements the interface)
```

`Fallen8EmbeddingProvider` wraps the configured
`IEmbeddingGenerator<string, Embedding<float>>` and does exactly two jobs (FR-1):

```csharp
public sealed class Fallen8EmbeddingProvider
{
    public EmbeddingModelIdentity Identity { get; }   // from config, validated on first use
    public Boolean IsLoaded { get; }                  // lazy: false until first generation

    /// Embeds and validates every vector against the index add contract
    /// (finite, exact Identity.Dimension length, non-zero norm when
    /// Identity.IntendedMetric is Cosine). A violation throws - hard error,
    /// never silent coercion (FR-8); the controller maps it to a 502-style
    /// problem response, because bad provider output is an upstream fault.
    public Task<Single[][]> EmbedAsync(IReadOnlyList<String> texts, CancellationToken ct);
}
```

Everything else — batching semantics, retries, telemetry — is the
`Microsoft.Extensions.AI` ecosystem's job (its `GeneratedEmbeddings`, middleware and
caching builders remain available to the operator via config later); we deliberately add
no second abstraction layer to keep swappability a config change. The wrapper is
registered as a singleton holding a `Lazy<IEmbeddingGenerator<...>>` (FR-2 lazy load;
thread-safe, single initialization; a failed load caches the failure with the reason and
answers 503 until config changes — no retry storm into a broken model path).

### 3.2 Configuration (`Fallen8:Embedding`, `Fallen8EmbeddingOptions`)

Follows the sibling options classes (`fallen-8-core-apiApp/Configuration/`, `SectionName`
const, bound in `Program.cs`):

```jsonc
"Fallen8": {
  "Embedding": {
    "Enabled": false,                  // FR-2: the capability flag; default OFF
    "Backend": "Onnx",                 // Onnx | LLamaSharp | Ollama
    "ModelName": "bge-micro-v2",       // identity (FR-8); required when Enabled
    "ModelVersion": "",                // optional free-form (quant, revision)
    "Dimension": 384,                  // required; validated against actual output
    "IntendedMetric": "Cosine",        // Cosine | DotProduct | L2
    "MaxBatchSize": 64,                // request bound, not a model property
    "MaxTextLength": 8192,             // chars per item, 400 above
    "QueryPrefix": "",                 // optional retrieval-instruction prefix
    "Onnx":       { "ModelPath": "", "VocabPath": "", "MaxTokens": 512,
                    "Pooling": "Cls", "Normalize": true },
    "LLamaSharp": { "ModelPath": "" },
    "Ollama":     { "Endpoint": "http://localhost:11434", "Model": "bge-m3" }
    // "OpenAI":  { "Endpoint": "...", "Model": "...", "ApiKey": "..." }  // reserved, v1 docs-only
  }
}
```

Weights are **never downloaded by Fallen-8** (FR-2): `ModelPath`/`VocabPath` point at
files the operator provides; the Ollama backend requires the operator to
`ollama pull bge-m3` (or any MIT embedding model) into the existing `f8-ollama-models`
volume — the same license posture as the NL-assist model (`docker-compose.yml` pulls
happen "ON THIS MACHINE from their upstream registries"). The default compose environment
is unchanged: no second model joins `ollama-init`, so `docker compose up` stays exactly as
cheap as today.

### 3.3 The three shipped backends (FR-3/FR-4)

| backend | package(s), MIT | model (MIT), dims | properties |
|---|---|---|---|
| `Onnx` | `Microsoft.ML.OnnxRuntime`, `Microsoft.ML.Tokenizers` | `bge-micro-v2` 384 / `bge-m3` 1024 | self-contained in-process; no sidecar; CPU (§2 non-goal); tokenizer from the model's vocab file; CLS pooling + L2 normalize by default (the bge contract) |
| `LLamaSharp` | `LLamaSharp` + `LLamaSharp.Backend.Cpu` | any embedding-capable GGUF | reuses a GGUF blob already on `f8-ollama-models` (Ollama stores models as content-addressed `sha256-…` blobs; the README shows how to locate one), so large weights exist **once** on disk |
| `Ollama` | `OllamaSharp` (implements `IEmbeddingGenerator` natively) | e.g. `bge-m3` | zero in-process model memory; **couples availability to the Ollama container** — if it is down, embedding endpoints answer 503 while everything else runs (stated explicitly, FR-4) |

Honesty note on cross-runtime identity: the same GGUF weights under LLamaSharp and under
the Ollama daemon are two llama.cpp builds — bit-identical output across them is **not
guaranteed** (kernel/version/threading differences). Weight reuse is a disk-space win, not
an identity guarantee; what protects correctness is FR-8 — one index, one model identity,
same provider for add and query — and the stamps that expose violations.

### 3.4 REST surface (`EmbeddingController`, versioned, OpenAPI-annotated)

All actions `[Authorize(Policy = Fallen8EmbeddingOptions.EmbeddingPolicy)]` (403 when the
flag is off — the `DynamicCapabilityAuthorizationHandler` pattern), rate-limited under
`SensitiveRateLimitPolicy`, request-size-limited like the other sensitive endpoints:

- `POST /embedding/element` — `{ "graphElementId": 42, "text": "…", "name": "default"? }`:
  embed (request thread), stamp, enqueue `SetEmbeddingsTransaction` (vector + model
  stamp in one batch — atomic), 200; 400 (text bounds, name), 404 (element), 409
  (identity mismatch with a bound index for that name, FR-8), 503 (provider down/failed).
- `POST /embedding/elements` — `{ "name"?, "items": [ { "graphElementId", "text" }, … ] }`:
  batch of ≤ `MaxBatchSize`; embeds as one provider batch, writes as one transaction —
  the bulk-ingestion path (FR-5).
- `POST /embedding/search` — `{ "indexId": "…", "text": "…", "k": 10, "kind"?, "label"? }`:
  embed once → `Fallen8.VectorIndexScan` → `VectorSearchResultREST` (same DTO as
  `POST /scan/index/vector`); 409 when the index declares a `model` identity that does not
  match the provider's (FR-8), 400/404 as on the raw scan endpoint.
- `POST /embedding/text` — `{ "texts": [ "…", … ] }` → raw vectors + the identity; for
  clients that then call the raw surfaces themselves (debug, external pipelines).
- Element-embeddings' `semantic` traversal block gains `queryText` (mutually exclusive
  with `queryVector`, 400 on both): `GraphController`/`SubGraphController` embed it once,
  before the traversal starts (FR-6), and thread the vector into the `TraversalContext`.
  `queryText` with the capability off → 403, same as the embedding endpoints.

### 3.5 The consistency contract, concretely (FR-8)

- `VectorIndex` gains a second optional creation option, `model` (opaque identity string,
  persisted in the index header alongside `embeddingName`). `POST /index` with
  `pluginOptions: { dimension, metric, embeddingName?, model? }` declares intent once.
- The provider's identity string is `EmbeddingModelIdentity.Stamp()` =
  `"{name}@{version}#{dimension}#{metric}"` (version part omitted when empty).
- Enforcement points, all **hard errors**: config `Dimension` ≠ first real output length →
  provider enters failed state (503 + log, never truncate/pad); `/embedding/element[s]`
  against a bound index whose dimension ≠ provider dimension → 409 before any write;
  `/embedding/search` and `semantic.queryText` against an index whose `model` ≠ provider
  stamp → 409; non-finite output or zero-norm-under-Cosine → the FR-1 wrapper throws (502
  problem response).
- Every provider-written element embedding carries the stamp as the element-embeddings
  sibling property (`$embeddingModel:<name>`) in the same transaction, so an operator (or
  a future re-embedding agent) can find stale vectors after a model change with a plain
  property scan.

### 3.6 Statistics & observability (FR-9)

`GraphStatisticsREST` gains an `embedding` object: `{ enabled, backend, modelName,
modelVersion, dimension, intendedMetric, loaded }` — `null` cost when disabled (no model
touch: statistics must never trigger the lazy load). Registered in `AppJsonContext` with a
`JsonSourceGenParityTest` representative; the OpenAPI snapshot is regenerated. Embedding
calls emit the existing `AppDiagnostics` span/counter pattern (duration, batch size,
failure) so the observability feature's exporters pick them up unchanged.

### 3.7 Tests (MSTest, `fallen-8-unittest`, the repo bar)

- **No live model in CI.** A deterministic `FakeEmbeddingGenerator` (seeded, text-hash →
  vector) registered through the `WebApplicationFactory` DI seam covers: both endpoints'
  happy paths, batch semantics, every 400/403/404/409/503 in §3.4/§3.5, the stamp landing
  on the element, bound-index projection after `/embedding/element`, `queryText`
  traversal end-to-end with dynamic code off, and statistics surfacing without triggering
  a load.
- Wrapper unit tests: identity validation, dimension hard-error, non-finite/zero-norm
  rejection, lazy single-load under concurrency, failed-load latching.
- **Gated live smokes** (`[TestCategory("Benchmark")]`-style opt-in + `[Ignore]`, repo
  pattern): one per backend — ONNX with a local `bge-micro-v2` file, LLamaSharp with a
  local GGUF, Ollama against a reachable endpoint — each skipping cleanly when the
  model/endpoint is absent, asserting dimension, finiteness, and cosine self-similarity
  ≈ 1.
- Convention gates: MIT headers, exact package pins (`CodeQualityTest`), OpenAPI snapshot,
  JSON parity representatives.

## 4. Acceptance criteria

- With `Fallen8:Embedding:Enabled=false` (the default): the apiApp starts with **zero**
  model-related allocations, `/embedding/*` and `semantic.queryText` answer 403, and
  `docker compose up` behaviour is unchanged — the model-free default deployment is
  provable by the statistics object and the endpoint probes.
- Switching `Backend` between `Onnx`, `LLamaSharp`, and `Ollama` requires **only**
  appsettings/env changes — pinned by tests that boot all three configurations (fakes for
  the model layer where no live model exists).
- Text in → element embedded → bound index answers kNN for that text: one call to
  `/embedding/element`, one to `/embedding/search`, no client-side vector handling —
  and the same flow via raw vectors still works with the provider disabled.
- Every FR-8 mismatch (dimension, model identity, non-finite, zero-norm) surfaces as the
  specified hard error; nothing coerces, truncates, or pads.
- `GET /statistics` reports the active provider and identity; `GET /status` is untouched.
- Full suite green; build 0 warnings/0 errors; OpenAPI snapshot regenerated (additions
  only); the engine csproj diff is **empty**.

## 5. Risks

- **Native-dependency weight.** `Microsoft.ML.OnnxRuntime` and `LLamaSharp.Backend.Cpu`
  ship large native binaries into the apiApp's publish output (tens of MB) even when the
  feature is off. Mitigation: measured in the plan; if publish size matters to an
  operator, the packages are the price of in-process backends and the Ollama backend
  carries none — documented trade-off. Revisit (a plugin-assembly split) if size draws
  real complaints.
- **Tokenizer fidelity (ONNX).** Retrieval quality depends on tokenizing exactly as the
  model was trained; `Microsoft.ML.Tokenizers` covers the bge family's WordPiece, but an
  operator pointing `ModelPath` at an arbitrary ONNX model may get silently degraded
  embeddings (valid floats, wrong meaning). Mitigation: README states the supported
  model families; the live smoke pins bge behaviour; FR-8 cannot catch semantic drift —
  stated honestly.
- **Ollama availability coupling** (FR-4, stated): embedding endpoints degrade to 503
  when the sidecar is down. Deliberate — the operator chose that backend for its zero
  in-process footprint.
- **Roslyn-style resource abuse via text batches.** Bounded: `MaxBatchSize`,
  `MaxTextLength`, the 1 MiB sensitive request limit, and the existing fixed-window rate
  limiter on every embedding action.
- **Model licensing drift.** The MIT-only rule (FR-3) binds defaults and docs; an
  operator can point at any weights they are licensed for — same posture as
  `docker-compose.yml`'s "their licenses bind the user running them."

## 6. Keep (do not regress)

- **The engine's zero-model-runtime guarantee**: `fallen-8-core.csproj` gains no package;
  no engine type references `Microsoft.Extensions.AI`. The vector-index §2 "never
  in-engine" non-goal stands verbatim.
- The element-embeddings contracts: accessor-only representation coupling, single-writer
  embedding transactions, bound-index projection semantics, bring-your-own-vector
  first-class (every raw endpoint keeps working with the provider absent or disabled).
- The security posture and its shape: default-off capability, 401/403 split, problem+json
  errors, sensitive rate limiting, request-size limits — all reusing the existing
  machinery, no parallel auth path.
- The stored-query and dynamic-code contracts: this feature adds no gated-code surface
  and does not widen `EnableDynamicCodeExecution`.
- The OpenAPI snapshot discipline and `AppJsonContext`/parity-test coverage for every new
  DTO; `/status` stays anonymous and lightweight.
