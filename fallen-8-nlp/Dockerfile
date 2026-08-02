# MIT License. fallen-8-nlp - the semantic-layer NLP enrichment sidecar (feature
# semantic-layer). Copyright (c) 2011-2026 Henning Rauch.
#
# A small FastAPI + spaCy service. The two MIT models (de_core_news_sm, en_core_web_sm) are
# downloaded at BUILD time so the running container makes no network call and starts offline.
FROM python:3.12-slim

WORKDIR /app

# System deps kept minimal; spaCy sm models are pure-Python wheels (no build toolchain needed).
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt \
    && python -m spacy download de_core_news_sm \
    && python -m spacy download en_core_web_sm

COPY app ./app

EXPOSE 8100

# Single worker: the models are loaded per-process and lazy; scale with replicas, not workers,
# so each process warms its own model cache once.
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8100"]
