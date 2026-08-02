# MIT License
#
# test_enrich.py - the enrichment API (feature semantic-layer). Requires the two spaCy models
# to be installed (the Dockerfile / CI installs them); run with `pytest` in the service venv.
# Copyright (c) 2011-2026 Henning Rauch. See the repository LICENSE.

from __future__ import annotations

from fastapi.testclient import TestClient

from app.main import app

client = TestClient(app)


def _enrich(items, language_hint=None):
    payload = {"items": items}
    if language_hint is not None:
        payload["languageHint"] = language_hint
    response = client.post("/enrich", json=payload)
    assert response.status_code == 200, response.text
    return {item["id"]: item for item in response.json()["items"]}


def test_health():
    response = client.get("/health")
    assert response.status_code == 200
    assert response.json()["status"] == "ok"


def test_english_entities_and_terms():
    result = _enrich(
        [{"id": "c1", "text": "Acme Corporation opened an office in Berlin last year."}],
        language_hint="en",
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


def test_german_routing_and_entities():
    # A German sentence: detection (no hint) must route to the German model and find the org.
    result = _enrich(
        [{"id": "d1", "text": "Die Muster GmbH hat ihren Sitz in München."}],
    )
    item = result["d1"]
    assert item["language"] == "de"
    texts = {e["text"] for e in item["entities"]}
    assert any("Muster" in t for t in texts)
    assert any("München" in t for t in texts)


def test_hint_overrides_detection():
    result = _enrich([{"id": "h1", "text": "Berlin"}], language_hint="de")
    assert result["h1"]["language"] == "de"


def test_empty_text_yields_empty_records():
    result = _enrich([{"id": "e1", "text": ""}], language_hint="en")
    assert result["e1"]["entities"] == []
    assert result["e1"]["keyTerms"] == []


def test_batch_preserves_ids_and_mixed_languages():
    result = _enrich(
        [
            {"id": "a", "text": "The London Bridge is famous."},
            {"id": "b", "text": "Das Brandenburger Tor steht in Berlin."},
        ]
    )
    assert set(result.keys()) == {"a", "b"}
    assert result["a"]["language"] == "en"
    assert result["b"]["language"] == "de"


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
