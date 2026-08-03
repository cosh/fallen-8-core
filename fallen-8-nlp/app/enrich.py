# MIT License
#
# enrich.py - the spaCy enrichment pipeline (feature nlp-gpu-tier): English NER + noun-chunk
# key terms over already-extracted chunk text. One English model, two tiers: en_core_web_lg on
# CPU (default), the en_core_web_trf transformer on an NVIDIA GPU (the compose GPU overlay bakes
# the trf model, reserves the device, and sets F8_NLP_PREFER_GPU). No layout work (docling owns
# that); no graph knowledge (the caller turns entities into vertices).
# Copyright (c) 2011-2026 Henning Rauch. See the repository LICENSE.

from __future__ import annotations

import os
import threading

from .models import EnrichedItem, EnrichItem, Entity

# The single English model. CONFIGURABLE (F8_NLP_MODEL) so an operator can trade accuracy for
# size without a code change; whatever is named must be installed in the image (the Dockerfile
# bakes this same name via a build ARG, so build and run stay in lockstep). The GPU compose
# overlay flips this to en_core_web_trf (transformer) and reserves the device.
_MODEL = os.environ.get("F8_NLP_MODEL", "en_core_web_lg")

# Use the GPU only when the overlay asked for it (F8_NLP_PREFER_GPU=1). spacy.prefer_gpu() is
# best-effort: it activates the GPU for the transformer pipeline when one is reachable and
# silently stays on CPU otherwise. A CPU host never sets this and never attempts a GPU.
_PREFER_GPU = os.environ.get("F8_NLP_PREFER_GPU", "0").strip().lower() in {"1", "true", "yes", "on"}

# English-only: echoed back so each item's record is explicit about what was processed.
_LANGUAGE = "en"

# Lazy, cached model load: spaCy model load is seconds (longer for the transformer), so load
# once on first use, guarded for concurrent requests.
_loaded = None
_load_lock = threading.Lock()

# Key-term hygiene: drop pronoun/determiner-only chunks and trivial fragments, cap length.
_MIN_KEY_TERM_CHARS = 3
_MAX_KEY_TERM_WORDS = 6


def _load():
    global _loaded
    if _loaded is not None:
        return _loaded
    with _load_lock:
        if _loaded is None:
            import spacy

            if _PREFER_GPU:
                # Must run before load so the transformer's weights land on the device.
                spacy.prefer_gpu()
            _loaded = spacy.load(_MODEL)
        return _loaded


def _key_terms(doc) -> list[str]:
    seen: set[str] = set()
    terms: list[str] = []
    for chunk in doc.noun_chunks:
        # Strip a leading determiner/pronoun so "the checkout service" -> "checkout service".
        tokens = [t for t in chunk if t.pos_ not in {"DET", "PRON"} and not t.is_space]
        if not tokens or len(tokens) > _MAX_KEY_TERM_WORDS:
            continue
        text = " ".join(t.text for t in tokens).strip()
        if len(text) < _MIN_KEY_TERM_CHARS:
            continue
        if all(t.is_stop or t.is_punct for t in tokens):
            continue
        key = text.casefold()
        if key not in seen:
            seen.add(key)
            terms.append(text)
    return terms


def enrich_items(items: list[EnrichItem]) -> list[EnrichedItem]:
    """Runs every item through nlp.pipe (one English model) and preserves input order."""
    nlp = _load()
    texts = [item.text or "" for item in items]
    results: list[EnrichedItem] = []
    for item, doc in zip(items, nlp.pipe(texts)):
        entities = [
            Entity(text=ent.text, label=ent.label_, start=ent.start_char, end=ent.end_char)
            for ent in doc.ents
        ]
        results.append(
            EnrichedItem(
                id=item.id,
                language=_LANGUAGE,
                entities=entities,
                key_terms=_key_terms(doc),
            )
        )
    return results
