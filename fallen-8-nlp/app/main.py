# MIT License
#
# main.py - the FastAPI app for the semantic-layer NLP sidecar (feature semantic-layer).
# One-way: Fallen-8 calls this service; it never calls Fallen-8. Copyright (c) 2011-2026
# Henning Rauch. See the repository LICENSE.

from __future__ import annotations

import os

from fastapi import FastAPI, HTTPException

from .enrich import enrich_items
from .models import EnrichRequest, EnrichResponse

# Bounds (env-overridable) so one request cannot exhaust the service.
_MAX_ITEMS = int(os.environ.get("F8_NLP_MAX_ITEMS", "512"))
_MAX_CHARS_PER_ITEM = int(os.environ.get("F8_NLP_MAX_CHARS", "40000"))

app = FastAPI(title="fallen-8-nlp", version="0.1")


@app.get("/health")
def health() -> dict:
    return {"status": "ok"}


@app.post("/enrich", response_model=EnrichResponse, response_model_by_alias=True)
def enrich(request: EnrichRequest) -> EnrichResponse:
    if len(request.items) > _MAX_ITEMS:
        raise HTTPException(
            status_code=413,
            detail=f"{len(request.items)} items exceeds the limit of {_MAX_ITEMS}.",
        )
    for item in request.items:
        if item.text is not None and len(item.text) > _MAX_CHARS_PER_ITEM:
            raise HTTPException(
                status_code=413,
                detail=f"item '{item.id}' ({len(item.text)} chars) exceeds the "
                f"per-item limit of {_MAX_CHARS_PER_ITEM}.",
            )

    return EnrichResponse(items=enrich_items(request.items, request.language_hint))
