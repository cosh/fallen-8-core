# MIT License. fallen-8-nlp - the semantic-layer NLP enrichment sidecar (feature
# semantic-layer). Copyright (c) 2011-2026 Henning Rauch.
#
# A small FastAPI + spaCy service. The two MIT models (de_core_news_sm, en_core_web_sm) are
# downloaded at BUILD time so the running container makes no network call and starts offline.
FROM python:3.12-slim

WORKDIR /app

# The spaCy model per language is a build ARG so md/lg can be baked in for accuracy without a
# code change (defaults to the small models). The runtime reads the SAME names from env
# (F8_NLP_MODEL_DE/EN), so build and run stay in lockstep. A larger model just needs its name
# here and the matching env at run time.
ARG F8_NLP_MODEL_DE=de_core_news_sm
ARG F8_NLP_MODEL_EN=en_core_web_sm
ENV F8_NLP_MODEL_DE=${F8_NLP_MODEL_DE}
ENV F8_NLP_MODEL_EN=${F8_NLP_MODEL_EN}

# System deps kept minimal; spaCy sm/md models are pure-Python wheels (no build toolchain).
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt \
    && python -m spacy download ${F8_NLP_MODEL_DE} \
    && python -m spacy download ${F8_NLP_MODEL_EN}

COPY app ./app

EXPOSE 8100

# Single worker: the models are loaded per-process and lazy; scale with replicas, not workers,
# so each process warms its own model cache once.
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8100"]
