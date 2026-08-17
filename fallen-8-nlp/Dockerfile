# MIT License. fallen-8-nlp - the semantic-layer NLP enrichment sidecar (feature nlp-gpu-tier).
# Copyright (c) 2011-2026 Henning Rauch.
#
# A small FastAPI + spaCy service, English-only and GPU-tiered. The model is installed at BUILD
# time directly from GitHub release wheels (bypassing the spaCy compatibility-table API) so the
# running container makes no network call and starts offline:
#   - CPU (default):   en_core_web_lg (MIT).
#   - GPU (F8_NLP_GPU=1): en_core_web_trf (MIT) - its wheel pulls its own backend
#     (spacy-curated-transformers) + torch - plus the spacy[cuda12x] extra (cupy) so
#     spacy.prefer_gpu() can move the transformer onto the reserved device at runtime.
# The compose GPU overlay (docker-compose.gpu-nlp.yml, applied automatically by `npm run env:up` when
# an NVIDIA GPU is detected) sets both build args below plus F8_NLP_PREFER_GPU and the device.
FROM python:3.12-slim

WORKDIR /app

# The spaCy model is a build ARG (kept in lockstep with the runtime F8_NLP_MODEL env). A larger
# model just needs its name here and the matching env at run time. F8_NLP_GPU=1 switches the
# build to the transformer tier.
ARG F8_NLP_MODEL=en_core_web_lg
ARG F8_NLP_GPU=0
ENV F8_NLP_MODEL=${F8_NLP_MODEL}

# System deps kept minimal; the CPU (lg) model is a pure-Python wheel. On the GPU build, add the
# CUDA extra (cupy) so spacy.prefer_gpu() can flip thinc onto the device; the en_core_web_trf
# model wheel itself pulls its backend (spacy-curated-transformers) + torch when downloaded, so
# cupy + torch land ONLY on the GPU image. NOTE: trf uses spacy-curated-transformers, NOT the
# classic spacy-transformers - do not install that one (wrong backend, torch/transformers conflict).
COPY requirements.txt .
# Install the model wheel directly from GitHub to avoid the spaCy compatibility-table API call
# (which rate-limits at 429 during parallel builds). URL pattern: <model>-3.8.0 for spaCy 3.8.x.
RUN pip install --no-cache-dir -r requirements.txt \
    && if [ "$F8_NLP_GPU" = "1" ]; then pip install --no-cache-dir "spacy[cuda12x]==3.8.2"; fi \
    && pip install --no-cache-dir \
    "https://github.com/explosion/spacy-models/releases/download/${F8_NLP_MODEL}-3.8.0/${F8_NLP_MODEL}-3.8.0-py3-none-any.whl"

COPY app ./app
    
EXPOSE 8100

# Single worker: the model is loaded per-process and lazy; scale with replicas, not workers, so
# each process warms its own model cache once.
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8100"]
