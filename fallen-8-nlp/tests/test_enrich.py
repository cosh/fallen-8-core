# MIT License
#
# test_enrich.py - the enrichment API (feature nlp-gpu-tier). Requires the English spaCy model
# to be installed (the Dockerfile / CI installs en_core_web_lg); run with `pytest` in the venv.
# Copyright (c) 2011-2026 Henning Rauch. See the repository LICENSE.

from __future__ import annotations

from fastapi.testclient import TestClient

from app.main import app

client = TestClient(app)


def _enrich(items):
    response = client.post("/enrich", json={"items": items})
    assert response.status_code == 200, response.text
    return {item["id"]: item for item in response.json()["items"]}


def test_health():
    response = client.get("/health")
    assert response.status_code == 200
    assert response.json()["status"] == "ok"


def test_english_entities_and_terms():
    result = _enrich(
        [{"id": "c1", "text": "Acme Corporation opened an office in Berlin last year."}],
    )
    item = result["c1"]
    assert item["language"] == "en"
    labels = {e["label"] for e in item["entities"]}
    texts = {e["text"] for e in item["entities"]}
    assert "Acme Corporation" in texts
    assert "Berlin" in texts
    assert labels  # some entity types were assigned
    # Noun-chunk key terms, determiner stripped.
    assert any("office" in term for term in item["keyTerms"])


def test_language_is_always_english():
    # English-only: no hint accepted, every item reports "en".
    result = _enrich([{"id": "l1", "text": "Contoso Ltd. moved to Seattle."}])
    assert result["l1"]["language"] == "en"


def test_empty_text_yields_empty_records():
    result = _enrich([{"id": "e1", "text": ""}])
    assert result["e1"]["entities"] == []
    assert result["e1"]["keyTerms"] == []


def test_batch_preserves_ids_and_order():
    result = _enrich(
        [
            {"id": "a", "text": "The London Bridge is famous."},
            {"id": "b", "text": "Microsoft is based in Redmond."},
        ]
    )
    assert set(result.keys()) == {"a", "b"}
    assert result["a"]["language"] == "en"
    assert result["b"]["language"] == "en"
    assert any("Microsoft" in e["text"] for e in result["b"]["entities"])


def test_over_item_limit_is_413(monkeypatch):
    import app.main as main

    monkeypatch.setattr(main, "_MAX_ITEMS", 1)
    response = client.post(
        "/enrich",
        json={"items": [{"id": "1", "text": "a"}, {"id": "2", "text": "b"}]},
    )
    assert response.status_code == 413


def test_over_char_limit_is_413(monkeypatch):
    import app.main as main

    monkeypatch.setattr(main, "_MAX_CHARS_PER_ITEM", 5)
    response = client.post(
        "/enrich",
        json={"items": [{"id": "1", "text": "way too many characters"}]},
    )
    assert response.status_code == 413
