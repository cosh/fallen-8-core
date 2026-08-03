# nlp-gpu-tier

## Summary

The semantic-layer NLP sidecar (`fallen-8-nlp`) becomes **English-only** and gains a
**GPU tier** that mirrors the existing Ollama trick: when the host has an NVIDIA GPU the sidecar
runs the `en_core_web_trf` transformer (roberta) model on the device for best-in-class English
extraction; with no GPU it runs `en_core_web_lg` on CPU. The output contract is unchanged (named
entities + noun-chunk key terms per chunk); only the model quality differs by tier. Detection is
**not new**: it reuses `scripts/env-up.js` (`nvidia-smi`, `F8_GPU=0/1` override), which already
applies `docker-compose.gpu.yml` on a GPU host. We extend that overlay with an `nlp:` block.

## Motivation

Two changes the maintainer asked for:

1. **Be best at English, drop German.** The sidecar shipped a bilingual design
   (`de_core_news_sm` + `en_core_web_sm`, per-chunk `langdetect` routing, a `languageHint`).
   The German model, the detection, and the hint are removed. One English model, no routing.
2. **Do more with a GPU, gracefully degrade without one.** Exactly the Ollama posture: a GPU
   unlocks the heavier/better path; a CPU host still works, just not as well. For spaCy that
   means the transformer model (`en_core_web_trf`) on GPU vs the CPU-friendly `en_core_web_lg`.

## Behaviour and contract

### Model tiers

| Host | Model | Runtime | Notes |
| --- | --- | --- | --- |
| No GPU (default) | `en_core_web_lg` | CPU | ~400 MB model, static vectors, good English NER/parse. Baked into the base image. |
| NVIDIA GPU | `en_core_web_trf` | GPU | roberta transformer, top English accuracy. Baked into the GPU image; the model wheel pulls `spacy-curated-transformers` + `torch`, and the build adds the `cupy` extra. |

Both models bundle `tagger` + `parser` + `attribute_ruler` + `lemmatizer` + `ner`, so
`doc.ents`, `doc.noun_chunks`, `pos_`, and `is_stop` (the attributes `enrich.py` depends on) work
in both tiers. The transformer tier changes accuracy, not the response shape.

### Selection and detection

- **One env var** `F8_NLP_MODEL` names the model (default `en_core_web_lg`). It is also a build
  ARG so the named model is baked in at build (offline start). Build ARG and runtime env stay in
  lockstep, as before.
- **`F8_NLP_GPU`** (build ARG) switches the image to the transformer tier: it bakes the trf
  model (whose wheel pulls `spacy-curated-transformers` + `torch`) and adds the `spacy[cuda12x]`
  extra (`cupy`), so `prefer_gpu()` can flip spaCy onto the device. It does NOT install the classic
  `spacy-transformers` (wrong backend for this model).
- **`F8_NLP_PREFER_GPU`** (runtime env) makes `enrich.py` call `spacy.prefer_gpu()` before load,
  moving the transformer onto the reserved device (best-effort; silently stays on CPU if no GPU
  is reachable).
- **`docker-compose.gpu.yml`** gains an `nlp:` block that sets `F8_NLP_MODEL=en_core_web_trf`
  (build + runtime), `F8_NLP_GPU=1`, `F8_NLP_PREFER_GPU=1`, and the NVIDIA device reservation.
  `scripts/env-up.js` already applies this file on a GPU host and honours `F8_GPU=0/1`; no new
  detection code.

### Wire contract (`POST /enrich`)

- **Request**: `{ items: [{ id, text }] }`. The `languageHint` field is **removed** end to end
  (sidecar request model, the C# `Fallen8NlpOptions.LanguageHint`, the `NlpClient.EnrichAsync`
  signature, and the fakes/tests). A stray `languageHint` from an older client is harmless
  (pydantic ignores unknown fields).
- **Response**: unchanged shape `{ items: [{ id, language, entities:[{text,label,start,end}],
  keyTerms:[...] }] }`. `language` is kept but pinned to the constant `"en"` (English-only); the
  C# side already ignores it, so no C# DTO change.

## Non-goals / right-sizing

- **No AMD/ROCm.** Same scope as the Ollama GPU overlay (NVIDIA only). Revisit trigger: a
  maintainer with an AMD box actually wanting it.
- **No trf-on-CPU fallback path.** The transformer runs on CPU only slowly; GPU genuinely gates
  it. A CPU host stays on `en_core_web_lg`. We do not ship a slow trf-on-CPU mode.
- **No multi-language re-introduction.** English-only is the point. `language` stays in the
  response only as an honest, cheap statement of what was processed.
- **No new REST endpoint / no OpenAPI or MCP surface change.** This is internal to the sidecar
  and its one caller; `languageHint` was server-side config, never a REST parameter.

## Impact on existing features (mandatory sweep)

- **semantic-layer (engine/REST caller).** `DocumentIngestionService` and `NlpClient` drop the
  `languageHint` argument (one call site, line ~1039). `Fallen8NlpOptions.LanguageHint` is
  removed. `NlpEnrichedItem.Language` is untouched (still parsed, still unused). The historical
  `features/done/semantic-layer/` spec/plan keep their bilingual narrative as a **historical
  record** (per the "specs are not rewritten" rule); this feature's spec is the current record.
- **Docs site.** `docs/src/content/docs/unstructured-ingestion.md` (the living semantic-layer
  page) is updated: the "entity network" section stops describing German/English routing and the
  `F8_NLP_MODEL_DE/EN` knobs, and states the English-only + CPU-`lg`/GPU-`trf` tiers. The
  `PER`/`LOC` German label examples become English (`PERSON`/`ORG`/`GPE`). The architecture
  diagrams already show a generic "NLP sidecar (spaCy)" node with no language or GPU detail, so
  they stay accurate and are **not** changed (consistent with how Ollama's GPU tier is not drawn).
- **Compose + scripts.** `docker-compose.yml` nlp service comment drops the German/`_DE`/`_EN`
  wording; `docker-compose.gpu.yml` header notes it now accelerates the NLP sidecar too;
  `scripts/env-up.js` GPU/NLP console lines mention the sidecar's model tier.
- **NL-assist fine-tune.** Unaffected: that dataset is about phi4 query translation, not spaCy
  German enrichment. No `RETRAIN-LOG.md` entry needed.
- **fallen8-deps SBOM sample (feature sample-graphs).** Dropping `langdetect` from
  `fallen-8-nlp/requirements.txt` stales the committed SBOM's `langdetect` node. Regenerating it
  is the `refresh-sbom.yml` action's job, NOT a hand edit. Caveat: that action's `paths:` trigger
  watches only `nl-assist-finetune/train/requirements.txt` among requirements files, so this edit
  will not auto-fire it; refresh via `workflow_dispatch` after merge (or widen the glob to
  `**/requirements.txt`). The Dockerfile-installed deps (models, torch, cupy, curated-transformers)
  never appeared in the SBOM (not in a scanned manifest), so the only change will be the removed
  `langdetect` node.
- **Tests.** Python: the German pytest cases are removed; English-only cases remain/added. C#:
  `NlpClientTest` German fixtures become English and the `languageHint` assertion is dropped;
  the `IngestionEndpointTest` fake client drops the `languageHint` parameter.
- **README.** The "Semantic layer" key-feature bullet is language-agnostic already; no change.

## Risks (kept honest)

- **CUDA wheel pinning** (the trf model wheel's `torch` + the `spacy[cuda12x]` `cupy-cuda12x`,
  host driver must support CUDA 12) is the single most likely thing to need a build iteration.
  The trf backend is `spacy-curated-transformers` (auto-pulled by the model wheel), NOT the
  classic `spacy-transformers`. The CPU tier is completely unaffected (no torch/cupy).
- **CI has no GPU**, so the automated suite exercises only the CPU (`lg`) path and the contract.
  The GPU image is build-validated and hand-smoke-tested.
- **Image size**: the GPU image is multiple GB (torch + cupy + trf). This is a second build
  variant selected by the same detector, matching the Ollama shape.
