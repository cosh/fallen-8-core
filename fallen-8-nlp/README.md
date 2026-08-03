# fallen-8-nlp

The semantic-layer NLP enrichment sidecar (feature nlp-gpu-tier). A small, standalone
FastAPI + [spaCy](https://spacy.io/) service that turns already-extracted chunk text into
**named entities** and **key terms** for Fallen-8's semantic layer. It never imports or calls
Fallen-8: the apiApp calls this service, gets clean JSON back, and turns entities into
`Entity` vertices linked by `mentions` edges.

It is **English-only** and runs in one of two tiers, chosen by whether the host has an NVIDIA GPU:

| Host | Model | Runtime |
| --- | --- | --- |
| No GPU (default) | `en_core_web_lg` | CPU |
| NVIDIA GPU | `en_core_web_trf` (roberta transformer) | GPU |

Both tiers return the same JSON; the transformer tier is just more accurate. Everything here is
permissive-licensed: spaCy (MIT), FastAPI (MIT), the `en_core_web_lg` / `en_core_web_trf` models
(MIT). The GPU tier additionally pulls `spacy-curated-transformers` (MIT) + `torch` (BSD) via the
trf model wheel, and the `spacy[cuda12x]` extra (`cupy`, MIT) so spaCy can use the device.

## What it does

- Extracts named entities (`doc.ents`) and noun-chunk **key terms** (`doc.noun_chunks`, with
  determiners/pronouns stripped and stopword-only fragments dropped).
- Returns per-item JSON: `{ id, language, entities: [{text,label,start,end}], keyTerms: [...] }`.
  `language` is always `"en"`.
- Structure only lives in docling; this service does no layout work.

## API

- `GET /health` -> `{ "status": "ok" }`
- `POST /enrich` -> body `{ "items": [{ "id": "c1", "text": "..." }] }`; bounded by
  `F8_NLP_MAX_ITEMS` (512) and `F8_NLP_MAX_CHARS` (40000) per item (413 over-limit).

## Run

```bash
pip install -r requirements.txt
python -m spacy download en_core_web_lg
uvicorn app.main:app --port 8100

# or the container (model baked in at build):
docker build -t fallen-8-nlp .
docker run --rm -p 8100:8100 fallen-8-nlp
```

In the compose environment the sidecar comes up behind the `ingestion` profile and Fallen-8 is
wired to it via `Fallen8:Nlp` (default on; `F8_NLP=false` opts out). A bare `dotnet run` of the
apiApp has NLP off and ingestion simply produces no entities.

### The GPU tier (accuracy on an NVIDIA host)

`npm run env:up` detects an NVIDIA GPU (`scripts/env-up.js`, `nvidia-smi`) and applies
`docker-compose.gpu.yml`, which rebuilds this sidecar on the transformer tier and runs it on the
device. `F8_GPU=0` forces CPU-only, `F8_GPU=1` forces the GPU override. There is no code change
between tiers: it is all env + build args.

To build the GPU image by hand:

```bash
docker build -t fallen-8-nlp-gpu \
  --build-arg F8_NLP_GPU=1 --build-arg F8_NLP_MODEL=en_core_web_trf fallen-8-nlp
docker run --rm --gpus all -p 8100:8100 \
  -e F8_NLP_MODEL=en_core_web_trf -e F8_NLP_PREFER_GPU=1 fallen-8-nlp-gpu
```

Build arg and env must name the SAME model (the build downloads it; the runtime loads it). The
GPU tier needs the NVIDIA Container Toolkit and a driver supporting CUDA 12; on a CPU-only host
the transformer runs but is slow, so the tier is gated on the GPU rather than offered as a
fallback.

## Test

```bash
pip install -r requirements-dev.txt
python -m spacy download en_core_web_lg
pytest
```
