// MIT License
//
// connectPaths.ts
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

import type { EdgeREST, PathREST } from "../api/types";

/**
 * Pure logic for the Canvas "Connect" tab (feature canvas-find-connect): pairwise shortest-path
 * discovery over the canvas vertices, and the selective add/remove bookkeeping that keeps the
 * canvas honest. Kept DOM-free so every decision below is unit-tested without a render.
 *
 * BLS expands frontiers over incoming AND outgoing edges, so a->b finds the same connection as
 * b->a; Connect therefore runs ONE query per UNORDERED pair (lower id as source), never both
 * directions. See features/open/canvas-find-connect/spec.md.
 */

/**
 * The most pairs a single Connect run will search. Above this the run is refused, not silently
 * truncated: a partial pairwise sweep would report "no path" for pairs it never tried, which is
 * a lie. 500 pairs is ~32 vertices in all-scope. One tuning home.
 */
export const CONNECT_PAIR_CAP = 500;

/** How many pair path-searches run concurrently in one batched round (bounds in-flight fetches). */
export const CONNECT_BATCH_SIZE = 8;

/** The canvas element ids at the moment a Connect run starts - the baseline every path is diffed against. */
export interface CanvasBaseline {
  nodes: Set<number>;
  edges: Set<number>;
}

/** The vertices and edges a path would introduce over the run baseline (its endpoints excluded). */
export interface IntroducedSets {
  nodeIds: Set<number>;
  edgeIds: Set<number>;
}

/**
 * Every unordered pair of the given vertex ids as `[lowId, highId]`, in ascending, deterministic
 * order. Duplicate ids collapse and self-pairs are excluded, so N distinct ids yield exactly
 * N*(N-1)/2 pairs.
 */
export function buildPairs(vertexIds: number[]): [number, number][] {
  const unique = [...new Set(vertexIds)].sort((a, b) => a - b);
  const pairs: [number, number][] = [];
  for (let i = 0; i < unique.length; i++) {
    for (let j = i + 1; j < unique.length; j++) {
      pairs.push([unique[i], unique[j]]);
    }
  }
  return pairs;
}

/** How many unordered pairs `vertexCount` DISTINCT vertices produce (N*(N-1)/2, never negative). */
export function pairCount(vertexCount: number): number {
  return vertexCount < 2 ? 0 : (vertexCount * (vertexCount - 1)) / 2;
}

/**
 * What a path would ADD to the canvas: its vertices and edges minus everything already in the
 * baseline. A pair's endpoints are baseline vertices by construction, so a direct-edge path
 * introduces just the edge; a multi-hop path introduces its intermediate vertices and hop edges.
 */
export function introducedSets(path: PathREST, baseline: CanvasBaseline): IntroducedSets {
  const nodeIds = new Set<number>();
  const edgeIds = new Set<number>();
  for (const el of path.pathElements) {
    if (!baseline.nodes.has(el.sourceVertexId)) nodeIds.add(el.sourceVertexId);
    if (!baseline.nodes.has(el.targetVertexId)) nodeIds.add(el.targetVertexId);
    if (!baseline.edges.has(el.edgeId)) edgeIds.add(el.edgeId);
  }
  return { nodeIds, edgeIds };
}

/**
 * What actually LEAVES the canvas when `target` is retracted: the elements it introduced that no
 * OTHER still-added path also introduced. A shared intermediate therefore survives until its last
 * claiming path is removed. `others` must exclude `target` itself (pass the remaining added paths).
 */
export function removalSet(target: IntroducedSets, others: IntroducedSets[]): IntroducedSets {
  const claimedNodes = new Set<number>();
  const claimedEdges = new Set<number>();
  for (const other of others) {
    for (const id of other.nodeIds) claimedNodes.add(id);
    for (const id of other.edgeIds) claimedEdges.add(id);
  }
  return {
    nodeIds: new Set([...target.nodeIds].filter((id) => !claimedNodes.has(id))),
    edgeIds: new Set([...target.edgeIds].filter((id) => !claimedEdges.has(id))),
  };
}

/**
 * Canvas edges synthesized from a path's hops (id, endpoints, type). The same shape the Path
 * screen's overlay builds; label/props are null here because a path element carries only the
 * edge's type, not its full property set (a later real merge fills them in). The caller filters
 * these to the ids it actually wants to add.
 */
export function synthesizeEdges(path: PathREST): EdgeREST[] {
  return path.pathElements.map((el) => ({
    id: el.edgeId,
    creationDate: "",
    modificationDate: "",
    sourceVertex: el.sourceVertexId,
    targetVertex: el.targetVertexId,
    edgePropertyId: el.edgePropertyId ?? null,
    label: null,
  }));
}
