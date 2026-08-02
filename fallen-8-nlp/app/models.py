# MIT License
#
# models.py - request/response schemas for the enrichment API (feature semantic-layer).
# Copyright (c) 2011-2026 Henning Rauch. See the repository LICENSE.

from __future__ import annotations

from pydantic import BaseModel, Field


class EnrichItem(BaseModel):
    """One unit of text to enrich - a chunk. `id` is echoed back so the caller can line
    results up with its chunks; this service holds no state and knows nothing of the graph."""

    id: str
    text: str


class EnrichRequest(BaseModel):
    items: list[EnrichItem] = Field(default_factory=list)
    # Optional ISO-639-1 hint ("de"/"en"). When absent, language is detected per item.
    language_hint: str | None = Field(default=None, alias="languageHint")

    model_config = {"populate_by_name": True}


class Entity(BaseModel):
    text: str
    # The raw spaCy label (German: PER/LOC/ORG/MISC; English: PERSON/ORG/GPE/...). The caller
    # maps/normalizes; the service does not editorialize.
    label: str
    start: int
    end: int


class EnrichedItem(BaseModel):
    id: str
    language: str
    entities: list[Entity] = Field(default_factory=list)
    key_terms: list[str] = Field(default_factory=list, serialization_alias="keyTerms")

    model_config = {"populate_by_name": True}


class EnrichResponse(BaseModel):
    items: list[EnrichedItem] = Field(default_factory=list)
