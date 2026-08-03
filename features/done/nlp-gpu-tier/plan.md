# nlp-gpu-tier - implementation plan

Branch `feature/nlp-gpu-tier`. No GitHub issue/PR unless asked.

## Phase 1 - sidecar: English-only

1. `fallen-8-nlp/app/enrich.py` - collapse to one English model read from `F8_NLP_MODEL`
   (default `en_core_web_lg`); delete `_MODEL_BY_LANGUAGE`, `_DEFAULT_LANGUAGE`,
   `resolve_language`, and the `langdetect` import. `enrich_items(items)` (no hint) runs one
   `nlp.pipe`; `language` echoes the constant `"en"`. `_key_terms` unchanged.
2. `fallen-8-nlp/app/models.py` - remove `EnrichRequest.language_hint` (and the now-unneeded
   `populate_by_name`); refresh the `Entity.label` / `EnrichedItem.language` comments to
   English-only.
3. `fallen-8-nlp/app/main.py` - call `enrich_items(request.items)`.
4. `fallen-8-nlp/requirements.txt` - drop `langdetect`; refresh the header comment.
5. `fallen-8-nlp/tests/test_enrich.py` - drop the German + mixed-language + hint-override cases;
   keep/extend English entity + key-term + empty-text + limit cases.

## Phase 2 - sidecar: GPU tier

6. `fallen-8-nlp/Dockerfile` - single ARG-driven Dockerfile: `F8_NLP_MODEL` (default `lg`) baked
   in; `F8_NLP_GPU=1` adds the `spacy[cuda12x]` extra (`cupy`) for `prefer_gpu()`. The trf model
   wheel pulls its own backend (`spacy-curated-transformers`) + `torch`, so no transformer backend
   is pre-installed (do NOT install the classic `spacy-transformers`). Remove the DE model/ARG.
7. `enrich.py` - honour `F8_NLP_PREFER_GPU` by calling `spacy.prefer_gpu()` before `spacy.load`.

## Phase 3 - compose + scripts (reuse the detector)

8. `docker-compose.gpu.yml` - add the `nlp:` block (build args `F8_NLP_MODEL=en_core_web_trf`,
   `F8_NLP_GPU=1`; env `F8_NLP_MODEL=en_core_web_trf`, `F8_NLP_PREFER_GPU=1`; NVIDIA device
   reservation). Update the file header to say it accelerates Ollama AND the NLP sidecar.
9. `docker-compose.yml` - rewrite the `nlp` service comment (English-only, `lg` baked in, GPU
   overlay swaps to `trf`; `F8_NLP_MODEL` replaces `F8_NLP_MODEL_DE/EN`).
10. `scripts/env-up.js` - GPU/NLP console lines mention the sidecar model tier.

## Phase 4 - C# caller: drop languageHint

11. `Configuration/Fallen8NlpOptions.cs` - remove `LanguageHint`.
12. `Ingestion/NlpClient.cs` - drop the `languageHint` parameter from the interface + impl and the
    `JsonObject_EnrichRequest.LanguageHint` field.
13. `Ingestion/DocumentIngestionService.cs` - update the one call site (~line 1039).
14. `fallen-8-unittest/NlpClientTest.cs` - English fixtures, drop the `languageHint` assertion.
15. `fallen-8-unittest/IngestionEndpointTest.cs` - `FakeNlpClient.EnrichAsync` drops the
    parameter; `Language = "en"`.

## Phase 5 - docs

16. `docs/src/content/docs/unstructured-ingestion.md` - the entity-network section: English-only,
    CPU-`lg` / GPU-`trf` tiers, English label examples, `F8_NLP_MODEL`. Architecture diagrams
    unchanged (already generic).

## Verify

- `dotnet build` clean (warnings are errors), then the NLP/ingestion tests
  (`--filter "FullyQualifiedName~Nlp|FullyQualifiedName~Ingestion"`).
- `py_compile` the three sidecar modules; run `pytest` where the `lg` model is installable
  (Docker/CI). CI/Docker install the model; local Windows may skip the heavy download.
- `npm --prefix docs run build` stays green (link-checked).
- Adversarial review pass over the compose merge, the Dockerfile GPU ordering, and the
  `prefer_gpu`/cupy requirement.
