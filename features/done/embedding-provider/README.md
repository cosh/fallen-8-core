# Embedding Provider — Usage

Text-in workflows for Fallen-8: a capability-gated, lazily-loaded embedding provider in the
apiApp behind `Microsoft.Extensions.AI.IEmbeddingGenerator<string, Embedding<float>>`, with
three interchangeable MIT backends. The engine (`fallen-8-core`) gains no model runtime —
a bare `dotnet run` stays model-free with the provider off.

## Out of the box (docker compose)

The compose environment enables the provider by default (feature embedding-out-of-box):
the Ollama sidecar it already ships pulls **bge-m3** (MIT, 1024-dim, Cosine) and the
`fallen8` service is wired to it, so `npm run env:up` gives working `/embedding/*`
endpoints, semantic search and `queryText` traversal with zero configuration. Opt out
with `F8_EMBEDDINGS=false` (skips the model pull, provider answers 403 again). The env
block in `docker-compose.yml` is the reference for swapping the model — ModelName and
Dimension must match what the sidecar serves.

## Configuration (`Fallen8:Embedding`)

```jsonc
"Fallen8": {
  "Embedding": {
    "Enabled": true,                   // default false: endpoints answer 403, nothing loads
    "Backend": "Onnx",                 // Onnx | LLamaSharp | Ollama - THE backend swap
    "ModelName": "bge-micro-v2",       // the identity (stamped, declared, compared)
    "ModelVersion": "",
    "Dimension": 384,                  // hard-validated against real output
    "IntendedMetric": "Cosine",
    "MaxBatchSize": 64,
    "MaxTextLength": 8192,
    "QueryPrefix": "",                 // optional retrieval prefix for QUERY-time embeddings
    "Onnx":       { "ModelPath": "C:/models/bge-micro-v2.onnx", "VocabPath": "C:/models/vocab.txt" },
    "LLamaSharp": { "ModelPath": "/var/lib/docker/volumes/f8-ollama-models/_data/blobs/sha256-..." },
    "Ollama":     { "Endpoint": "http://localhost:11434", "Model": "bge-m3" }
  }
}
```

Weights are **never downloaded by Fallen-8** — paths point at operator-provided files.
Model load is lazy (first use), and a failed load latches with its reason (503) instead of
retry-storming.

## The backend matrix

| backend | in-process? | weights from | notes |
|---|---|---|---|
| `Onnx` | yes (CPU) | an ONNX export + WordPiece vocab you provide (bge family is the tested reference: CLS pooling, L2 normalize) | self-contained; tokenizer fidelity matters — stick to the bge family unless you verify |
| `LLamaSharp` | yes (CPU) | an embedding-capable GGUF — typically a blob already on the `f8-ollama-models` volume (`ollama pull` once, weights exist ONCE on disk) | same weights ≠ bit-identical output vs. the Ollama daemon (two llama.cpp builds); the identity contract is what protects correctness |
| `Ollama` | no | the compose-shipped container (`ollama pull bge-m3`) | zero in-process memory; **couples availability to the container** — embedding endpoints answer 503 while it is down, everything else keeps running |

GPU: the in-process backends are CPU-only in v1; the GPU, when present, stays with the
Ollama sidecar (`docker-compose.gpu.yml`). An OpenAI-compatible remote backend is a
documented extension point (one more case in `EmbeddingBackendFactory`, config shape
reserved under `Fallen8:Embedding:OpenAI`).

## Endpoints (all 403 while disabled)

```bash
# Text -> element embedding (+ model stamp, one atomic transaction; a bound index projects):
curl -sf -X POST http://localhost:5000/embedding/element \
     -H "Content-Type: application/json" \
     -d '{ "graphElementId": 42, "text": "a red bicycle" }'

# Bulk: one provider batch, one transaction:
curl -sf -X POST http://localhost:5000/embedding/elements \
     -H "Content-Type: application/json" \
     -d '{ "items": [ { "graphElementId": 1, "text": "..." }, { "graphElementId": 2, "text": "..." } ] }'

# Semantic search (embed once -> exact kNN; scores identical to /scan/index/vector):
curl -sf -X POST http://localhost:5000/embedding/search \
     -H "Content-Type: application/json" \
     -d '{ "indexId": "embeddings", "text": "red bicycles", "k": 10, "kind": "vertex" }'

# Raw vectors for client-side pipelines:
curl -sf -X POST http://localhost:5000/embedding/text \
     -H "Content-Type: application/json" \
     -d '{ "texts": ["a red bicycle"] }'
```

And the whole GraphRAG loop in two calls, text end to end — semantic traversal takes
`queryText` (embedded once, before the traversal starts; mutually exclusive with
`queryVector`):

```bash
curl -sf -X POST http://localhost:5000/path/1/to/9 \
     -H "Content-Type: application/json" \
     -d '{ "semantic": { "queryText": "red bicycles", "minScore": 0.7 } }'
```

## The consistency contract (hard errors, never coercion)

- Declared `Dimension` ≠ real output length → the provider latches unavailable (503).
- Embed-to-element against a bound index of another dimension → 409 before any write.
- `/embedding/search` against an index whose dimension differs → 409; whose declared
  `model` identity (the index `model` creation option) differs from the provider's stamp
  (`name[@version]#dimension#metric`) → 409.
- Non-finite or zero-norm-under-Cosine backend output → 502.
- Every provider-written embedding carries the stamp (`$embeddingModel:<name>` next to the
  vector, same transaction); a bring-your-own-vector overwrite **clears** it, so the stamp
  always tells the truth about provenance. A model change is an external re-index (new
  index, re-embed) — the stamps make stale vectors findable with a property scan.

## Ops

- `GET /status` AND `GET /statistics` → `embedding: { enabled, backend, modelName,
  modelVersion, dimension, intendedMetric, loaded }` — reading it never triggers the lazy
  load. `/status` is the cheap discovery surface (feature embedding-out-of-box): clients
  learn the provider state without the budgeted graph-shape pass, and F8 Studio gates its
  text-in controls on it.
- Live smokes per backend: `fallen-8-unittest/EmbeddingBackendSmokeTest.cs` (opt-in, repo
  gated-test pattern; set `F8_TEST_ONNX_*` / `F8_TEST_GGUF_*` / `F8_TEST_OLLAMA_*`).
- Publish-size note: `Microsoft.ML.OnnxRuntime` and `LLamaSharp.Backend.Cpu` ship native
  binaries (tens of MB) into the publish output even when the feature is off — the price of
  in-process backends; the Ollama backend carries none.
