# MIT License
#
# enrich.py - the spaCy enrichment pipeline (feature semantic-layer): per-language NER +
# noun-chunk key terms over already-extracted chunk text. No layout work (docling owns that);
# no graph knowledge (the caller turns entities into vertices).
# Copyright (c) 2011-2026 Henning Rauch. See the repository LICENSE.

from __future__ import annotations

import os
import threading

from .models import EnrichedItem, EnrichItem, Entity

# The two shipped languages and their MIT spaCy models. Adding a language is a model install
# plus one row here (revisit trigger in the spec).
_MODEL_BY_LANGUAGE = {
    "de": "de_core_news_sm",
    "en": "en_core_web_sm",
}
_DEFAULT_LANGUAGE = os.environ.get("F8_NLP_DEFAULT_LANGUAGE", "en")

# Lazy, cached model load: spaCy model load is seconds, so load once per language on first use.
_loaded: dict = {}
_load_lock = threading.Lock()

# Key-term hygiene: drop pronoun/determiner-only chunks and trivial fragments, cap length.
_MIN_KEY_TERM_CHARS = 3
_MAX_KEY_TERM_WORDS = 6


def _load(language: str):
    model_name = _MODEL_BY_LANGUAGE[language]
    cached = _loaded.get(language)
    if cached is not None:
        return cached
    with _load_lock:
        cached = _loaded.get(language)
        if cached is None:
            import spacy

            cached = spacy.load(model_name)
            _loaded[language] = cached
        return cached


def resolve_language(text: str, hint: str | None) -> str:
    """Hint wins when it is a supported language; else detect; else the default. Detection is
    best-effort - a failure or an unsupported result falls back to the default, never errors."""
    if hint:
        normalized = hint.strip().lower()[:2]
        if normalized in _MODEL_BY_LANGUAGE:
            return normalized
    if text and text.strip():
        try:
            from langdetect import DetectorFactory, detect

            DetectorFactory.seed = 0  # deterministic detection
            detected = detect(text)
            if detected in _MODEL_BY_LANGUAGE:
                return detected
        except Exception:
            pass
    return _DEFAULT_LANGUAGE if _DEFAULT_LANGUAGE in _MODEL_BY_LANGUAGE else "en"


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


def enrich_items(items: list[EnrichItem], hint: str | None) -> list[EnrichedItem]:
    """Groups items by resolved language and runs each group through nlp.pipe. Order is
    preserved by carrying the original index alongside each doc."""
    # (index, item, language)
    routed = [(i, item, resolve_language(item.text, hint)) for i, item in enumerate(items)]
    results: list[EnrichedItem | None] = [None] * len(items)

    by_language: dict[str, list[tuple[int, EnrichItem]]] = {}
    for index, item, language in routed:
        by_language.setdefault(language, []).append((index, item))

    for language, group in by_language.items():
        nlp = _load(language)
        texts = [item.text or "" for _, item in group]
        for (index, item), doc in zip(group, nlp.pipe(texts)):
            entities = [
                Entity(text=ent.text, label=ent.label_, start=ent.start_char, end=ent.end_char)
                for ent in doc.ents
            ]
            results[index] = EnrichedItem(
                id=item.id,
                language=language,
                entities=entities,
                key_terms=_key_terms(doc),
            )

    # No item can be left unrouted (every language falls back to a loadable model).
    return [r for r in results if r is not None]
