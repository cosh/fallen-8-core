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
 * more than before. The rounds below keep the same width.
 *
 * Two properties the caller relies on: the result is in the order of the ids it passed (an edge is
 * read later but still renders where it was scanned), and progress counts ATTEMPTS, so a page with
 * a deleted id still reaches its total. An aborted signal stops the work rather than degrading it.
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
  // Distinct ids in first-seen order. Results are keyed by id, so a repeated id would otherwise be
  // requested twice and emitted twice - which a caller renders as a duplicate React key. The cap
  // therefore bounds DISTINCT elements, which is what a caller asking for at most `cap` means.
  const distinct = [...new Set(ids)];
  const target = distinct.slice(0, cap);
  const capped = distinct.length > cap;
  const byId = new Map<number, VertexREST | EdgeREST>();

  // Scan order, not arrival order: an id with no result is simply absent, exactly as the
  // per-element path treats a failure or a server-reported notFound.
  const collected = () => ({
    elements: target
      .map((id) => byId.get(id))
      .filter((element): element is VertexREST | EdgeREST => element !== undefined),
    capped,
  });

  if (target.length === 0 || options.signal?.aborted) {
    return collected();
  }

  // One request for the whole page. A failure here is not fatal: fall back to the per-element path
  // below, so an older server without the batch route still hydrates.
  const batched = await getGraphElements(instance, target, options.signal).catch(() => null);

  const edgeIds: number[] = [];
  if (batched !== null) {
    for (const element of batched.elements ?? []) {
      if (element.kind === "edge") {
        edgeIds.push(element.id);
      } else {
        // A vertex projection IS a complete VertexREST: a vertex carries no adjacency in this
        // API, so nothing is missing from it (see GraphElementProjectionREST).
        byId.set(element.id, element as VertexREST);
      }
    }
  }

  // Edges (or everything, when the batch route was unavailable) one at a time, as before.
  const singly = batched === null ? target : edgeIds;

  // Attempts, not hits: an id deleted between scan and hydration must not leave the bar short of
  // its total forever.
  let attempted = target.length - singly.length;
  if (batched !== null) {
    options.onProgress?.({ done: attempted, total: target.length });
  }

  for (let start = 0; start < singly.length; start += HYDRATION_BATCH_SIZE) {
    // Checked before the round, not after: an abort that made the batch read fail must not be read
    // as "this server has no batch route" and answered with one doomed request per id.
    if (options.signal?.aborted) break;
    const batch = singly.slice(start, start + HYDRATION_BATCH_SIZE);
    const settled = await Promise.all(
      batch.map((id) => getGraphElement(instance, id, options.signal).catch(() => null)),
    );
    for (let i = 0; i < settled.length; i++) {
      const element = settled[i];
      if (element !== null) byId.set(batch[i], element);
    }
    attempted += batch.length;
    options.onProgress?.({ done: attempted, total: target.length });
  }

  return collected();
}
