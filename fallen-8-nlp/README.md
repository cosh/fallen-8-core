# fallen-8-nlp

The semantic-layer NLP enrichment sidecar (feature semantic-layer). A small, standalone
FastAPI + [spaCy](https://spacy.io/) service that turns already-extracted chunk text into
**named entities** and **key terms** for Fallen-8's semantic layer. It never imports or calls
Fallen-8: the apiApp calls this service, gets clean JSON back, and turns entities into
`Entity` vertices linked by `mentions` edges.

Everything here is permissive-licensed: spaCy (MIT), FastAPI (MIT), the `de_core_news_sm` and
`en_core_web_sm` models (MIT), langdetect (Apache-2.0).

## What it does

- Detects each item's language (German/English; overridable hint) and routes to the matching
  spaCy model.
- Extracts named entities (`doc.ents`) and noun-chunk **key terms** (`doc.noun_chunks`, with
  determiners/pronouns stripped and stopword-only fragments dropped).
- Returns per-item JSON: `{ id, language, entities: [{text,label,start,end}], keyTerms: [...] }`.
- Structure only lives in docling; this service does no layout work.

## API

- `GET /health` -> `{ "status": "ok" }`
- `POST /enrich` -> body `{ "items": [{ "id": "c1", "text": "..." }], "languageHint": "de" }`;
  bounded by `F8_NLP_MAX_ITEMS` (512) and `F8_NLP_MAX_CHARS` (40000) per item (413 over-limit).

## Run

```bash
pip install -r requirements.txt
python -m spacy download de_core_news_sm
python -m spacy download en_core_web_sm
uvicorn app.main:app --port 8100

# or the container (models baked in at build):
docker build -t fallen-8-nlp .
docker run --rm -p 8100:8100 fallen-8-nlp
```

In the compose environment the sidecar comes up behind the `ingestion` profile and Fallen-8 is
wired to it via `Fallen8:Nlp` (default on; `F8_NLP=false` opts out). A bare `dotnet run` of the
apiApp has NLP off and ingestion simply produces no entities.

### Choosing the model (accuracy vs size)

The spaCy model per language is configurable, defaulting to the small models. For a hard
domain like legal German, trade up to `md`/`lg` without touching code: set the build args (so
the larger model is baked into the image) and the matching run-time env, e.g.

```bash
docker build -t fallen-8-nlp --build-arg F8_NLP_MODEL_DE=de_core_news_lg fallen-8-nlp
docker run --rm -p 8100:8100 -e F8_NLP_MODEL_DE=de_core_news_lg fallen-8-nlp
```

Build arg and env must name the SAME model (the build downloads it; the runtime loads it). In
compose, `F8_NLP_MODEL_DE` / `F8_NLP_MODEL_EN` drive both.

## Test

```bash
pip install -r requirements-dev.txt
python -m spacy download de_core_news_sm && python -m spacy download en_core_web_sm
pytest
```
