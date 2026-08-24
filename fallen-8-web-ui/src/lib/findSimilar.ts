// MIT License
//
// findSimilar.ts
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

import type { EdgeREST, IndexDescription, VertexREST } from "../api/types";
import type { ScanPrefill } from "../state/instanceStore";
import { EMBEDDING_PROPERTY_PREFIX } from "../state/graphShape";
import { vectorQueryText } from "./embeddingProperties";

/**
 * "Find elements like this one", composed entirely on the client (feature element-similarity-search).
 *
 * There is no element-as-query mode anywhere in the product: `/embedding/search` takes text and
 * `/scan/index/vector` takes a vector, so the only way to ask "what is like THIS" is to read the
 * element's own stored embedding and search with it. That read is free - the vector is already in
 * the element's properties, which the caller has already fetched.
 *
 * The searchable index is the one BOUND to that embedding name. An unbound index holds vectors that
 * exist nowhere else, so it cannot be expected to contain this element at all, and a bound index of
 * a different name projects different vectors. No bound index means the gesture has nowhere to run,
 * which is a reason to show rather than a button to grey out silently.
 */
export interface SimilarSearch {
  prefill: ScanPrefill;
  embeddingName: string;
}

/** Every embedding this element carries that a bound vector index actually projects. */
export function similarSearchesFor(
  element: VertexREST | EdgeREST,
  indices: IndexDescription[] | null | undefined,
  isEdge: boolean,
): SimilarSearch[] {
  const inventory = indices ?? [];
  const found: SimilarSearch[] = [];

  for (const property of element.properties ?? []) {
    if (!property.propertyId.startsWith(EMBEDDING_PROPERTY_PREFIX)) continue;
    const embeddingName = property.propertyId.slice(EMBEDDING_PROPERTY_PREFIX.length);
    const vector = vectorQueryText(property.propertyValue);
    if (vector === null) continue;

    const index = inventory.find(
      (candidate) =>
        candidate.pluginType === "VectorIndex" && candidate.embeddingName === embeddingName,
    );
    if (!index) continue;

    found.push({
      embeddingName,
      prefill: {
        indexId: index.indexId,
        vectorText: vector,
        sourceElementId: element.id,
        // Inherited so a similarity search stays inside the kind of thing the source is. An
        // element with no label inherits no constraint rather than an empty one.
        label: element.label || undefined,
        kind: isEdge ? "edge" : "vertex",
      },
    });
  }

  return found;
}
