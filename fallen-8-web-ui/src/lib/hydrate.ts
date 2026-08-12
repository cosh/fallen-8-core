// MIT License
//
// hydrate.ts
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

import { getGraphElement, getGraphElements } from "../api/endpoints";
import type { EdgeREST, VertexREST } from "../api/types";
import type { InstanceConfig } from "../instances/types";

/**
 * Scans return bare id lists (FR-8); this hydrates them into elements. Missing ids (deleted
 * between scan and hydration) are skipped, not errors.
 *
 * ONE batch read does most of the work: POST /graphelements/get answers a whole page of ids in a
 * single request, and what it returns - id, label, properties, kind - is a COMPLETE VertexREST,
 * because a vertex carries no adjacency in this API. Five hundred ids used to be twenty rounds of
 * twenty-five requests.
 *
 * Edges are the exception and are still fetched one by one. The batch route omits an edge's
 * endpoints deliberately (shipping every endpoint of every element would dominate a several-hundred
 * element payload), and the canvas cannot draw an edge without them, so an id the batch reports as
 * an edge is re-read through GET /graphelement/{id}. A result that is all vertices - which is what
 * a scan or an analytics run usually produces - costs one request; one that is all edges costs one
 * more than before. The rounds below keep the same width and the same visible progress.
 */

export const HYDRATION_BATCH_SIZE = 25;
export const HYDRATION_DEFAULT_CAP = 500;

export interface HydrationProgress {
  done: number;
  total: number;
}

export function isEdge(element: VertexREST | EdgeREST): element is EdgeREST {
  return (element as EdgeREST).sourceVertex !== undefined;
}

export async function hydrateElements(
  instance: InstanceConfig,
  ids: number[],
  options: {
    cap?: number;
    onProgress?: (progress: HydrationProgress) => void;
    signal?: AbortSignal;
  } = {},
): Promise<{ elements: (VertexREST | EdgeREST)[]; capped: boolean }> {
  const cap = options.cap ?? HYDRATION_DEFAULT_CAP;
  const target = ids.slice(0, cap);
  const elements: (VertexREST | EdgeREST)[] = [];

  if (target.length === 0) {
    return { elements, capped: ids.length > cap };
  }

  // One request for the whole page. A failure here is not fatal: fall back to the per-element path
  // below, so an older server without the batch route still hydrates.
  const batched = await getGraphElements(instance, target, options.signal).catch(() => null);

  // An id the server reports in notFound simply never appears in elements, so nothing is skipped
  // explicitly: gone is the absence of a result, exactly as the per-element path treats a failure.
  const edgeIds: number[] = [];
  if (batched !== null) {
    for (const element of batched.elements ?? []) {
      if (element.kind === "edge") {
        edgeIds.push(element.id);
      } else {
        // A vertex projection IS a complete VertexREST: a vertex carries no adjacency in this
        // API, so nothing is missing from it (see GraphElementProjectionREST).
        elements.push(element as VertexREST);
      }
    }
    options.onProgress?.({ done: elements.length, total: target.length });
  }

  // Edges (or everything, when the batch route was unavailable) one at a time, as before.
  const singly = batched === null ? target : edgeIds;
  for (let start = 0; start < singly.length; start += HYDRATION_BATCH_SIZE) {
    const batch = singly.slice(start, start + HYDRATION_BATCH_SIZE);
    const settled = await Promise.all(
      batch.map((id) => getGraphElement(instance, id, options.signal).catch(() => null)),
    );
    for (const element of settled) {
      if (element !== null) elements.push(element);
    }
    options.onProgress?.({
      done: Math.min(elements.length, target.length),
      total: target.length,
    });
    if (options.signal?.aborted) break;
  }

  options.onProgress?.({ done: Math.min(elements.length, target.length), total: target.length });
  return { elements, capped: ids.length > cap };
}
