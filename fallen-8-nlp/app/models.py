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
    # English-only: there is no language hint. An unknown field from an older client is ignored.
    items: list[EnrichItem] = Field(default_factory=list)


class Entity(BaseModel):
    text: str
    # The raw spaCy English label (PERSON/ORG/GPE/LOC/DATE/...). The caller maps/normalizes; the
    # service does not editorialize.
    label: str
    start: int
    end: int


class EnrichedItem(BaseModel):
    id: str
    # Always "en" (English-only sidecar); kept so each item's record states what was processed.
    language: str
    entities: list[Entity] = Field(default_factory=list)
    key_terms: list[str] = Field(default_factory=list, serialization_alias="keyTerms")

    model_config = {"populate_by_name": True}


class EnrichResponse(BaseModel):
    items: list[EnrichedItem] = Field(default_factory=list)
