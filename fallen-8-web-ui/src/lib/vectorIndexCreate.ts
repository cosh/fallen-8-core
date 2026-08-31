// MIT License
//
// vectorIndexCreate.ts
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

import type { EmbeddingProviderStatsREST, PropertySpecification } from "../api/types";

/**
 * What a NEW vector index is created with, in one place: two screens create one (the Indexes
 * screen's create panel and the Query screen's semantic on-ramp, feature
 * semantic-search-onramp) and the numbers are not a preference. A bound index whose dimension
 * or metric disagrees with the model writing into it is refused on every later embed and every
 * later search, so the provider is the authority and guessing it twice is how the two copies
 * would drift.
 */

/**
 * Fallbacks for a server that reports no usable provider. Not a recommendation: a shape the
 * engine accepts, so the form has something to submit while saying (at the call site) that
 * these are defaults rather than the instance's own numbers.
 */
export const VECTOR_INDEX_FALLBACK = { dimension: "384", metric: "Cosine" } as const;

export interface VectorIndexDefaults {
  /** The provider is on AND names a dimension, so its numbers are the authoritative ones. */
  providerReady: boolean;
  dimension: string;
  metric: string;
}

/** The dimension + metric a new vector index should default to on this instance. */
export function vectorIndexDefaults(
  provider: EmbeddingProviderStatsREST | null | undefined,
): VectorIndexDefaults {
  const providerReady = provider != null && provider.enabled && provider.dimension > 0;
  if (!providerReady) {
    return { providerReady, ...VECTOR_INDEX_FALLBACK };
  }
  return {
    providerReady,
    dimension: String(provider!.dimension),
    metric: provider!.intendedMetric ?? VECTOR_INDEX_FALLBACK.metric,
  };
}

/** The engine's own bounds on a vector index's dimension (VectorIndex.Initialize). */
export const VECTOR_DIMENSION_RANGE = { min: 1, max: 4096 } as const;

/**
 * Whether this dimension is one the engine will take. The number input's `min`/`max` are not
 * enough on their own: neither attribute blocks an EMPTY field, and neither is consulted at all
 * unless the button is a submit. An empty value travels as `propertyValue: ""` against
 * `System.Int32`, which the server cannot convert, so the round trip is spent to be told so.
 */
export function isValidVectorDimension(dimension: string): boolean {
  const trimmed = dimension.trim();
  if (!/^\d+$/.test(trimmed)) return false;
  const value = Number(trimmed);
  return value >= VECTOR_DIMENSION_RANGE.min && value <= VECTOR_DIMENSION_RANGE.max;
}

const literal = (propertyId: string, propertyValue: string, type: string): PropertySpecification => ({
  propertyId,
  propertyValue,
  fullQualifiedTypeName: type,
});

/**
 * The `pluginOptions` of POST /index for a vector index. Options travel as typed literals
 * (vector-index README §creation), and embeddingName/model are emitted only when set, so a raw
 * index keeps exactly the two-option shape (pinned by index-management.test.tsx).
 */
export function vectorIndexPluginOptions(options: {
  dimension: string;
  metric: string;
  embeddingName?: string;
  model?: string;
}): Record<string, PropertySpecification> {
  const embeddingName = options.embeddingName?.trim();
  const model = options.model?.trim();
  return {
    dimension: literal("dimension", options.dimension, "System.Int32"),
    metric: literal("metric", options.metric, "System.String"),
    ...(embeddingName ? { embeddingName: literal("embeddingName", embeddingName, "System.String") } : {}),
    ...(model ? { model: literal("model", model, "System.String") } : {}),
  };
}
