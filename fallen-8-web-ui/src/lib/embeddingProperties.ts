// MIT License
//
// embeddingProperties.ts
//
// Copyright (c) 2011-2026 Henning Rauch
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

import {
  EMBEDDING_PROPERTY_PREFIX as EMBEDDING_PREFIX,
  EMBEDDING_MODEL_PROPERTY_PREFIX as EMBEDDING_MODEL_PREFIX,
} from "../state/graphShape";
import { formatPropertyValue } from "./literals";
import { parseVector } from "./vector";

/** A property is reserved (embedding state) when it uses either embedding prefix. */
export function isReservedEmbeddingProperty(propertyId: string): boolean {
  return (
    propertyId.startsWith(EMBEDDING_PREFIX) || propertyId.startsWith(EMBEDDING_MODEL_PREFIX)
  );
}

/**
 * The element's own properties first, engine-written embedding state last, order otherwise
 * preserved. For a TRUNCATED preview (the element table's properties cell), which is the only
 * place this matters: a reserved marker is bookkeeping the operator did not write and cannot edit
 * here, so spending the character budget on it before their own data is backwards.
 *
 * It was not hypothetical. On a graph with embeddings, `$embeddingModel:default=bge-m3#1024#Cosine`
 * is ~44 of the ~80 available characters, and the REST egress emits properties in no guaranteed
 * order - so a movie row rendered as "year=2010, $embeddingModel:default=…, plot=A thief who
 * steals…" with `title=` cut off entirely, and the published semantic-search screenshot showed no
 * film title in any of its ten rows.
 */
export function userPropertiesFirst<T extends { propertyId: string }>(properties: T[]): T[] {
  const reserved = (p: T) => (isReservedEmbeddingProperty(p.propertyId) ? 1 : 0);
  // Array.prototype.sort is stable (spec-guaranteed), so this only ever moves reserved keys down.
  return [...properties].sort((a, b) => reserved(a) - reserved(b));
}

/** A one-line preview of a stored vector value. The REST egress sends Single[] values as
 * the bracketed string form (see AGraphElement.FormatPropertyValue), so both shapes are
 * truncated — a 1024-dim embedding must never dump raw into the table. */
export function previewVector(value: unknown): string {
  let components: unknown[] | null = null;
  if (Array.isArray(value)) {
    components = value;
  } else if (typeof value === "string" && value.trim().startsWith("[")) {
    const parsed = parseVector(value);
    if (parsed.ok) components = parsed.vector;
  }
  if (components === null) return formatPropertyValue(value);
  const head = components
    .slice(0, 4)
    .map((n) => (typeof n === "number" ? Number(n.toFixed(4)) : n))
    .join(", ");
  return `[${head}${components.length > 4 ? ", …" : ""}] (d=${components.length})`;
}
