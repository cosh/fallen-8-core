// MIT License
//
// canvasInteract.ts
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

import { getInDegree, getOutDegree } from "../api/endpoints";
import type { EdgeREST, VectorSearchResultREST, VertexREST } from "../api/types";
import type { InstanceConfig } from "../instances/types";
import { visibleDegrees } from "../canvas/styleEngine";
import type { CanvasEdge, CanvasNode } from "../state/instanceStore";
import { EXPAND_EDGE_CAP, fetchVertexNeighborhood } from "./neighborhood";

/**
 * Pure logic for the Canvas "Interact" tab (feature canvas-interact): filters build a MATCH SET
 * over the canvas vertices, and two view-only verbs (expand, remove) apply to it. Kept DOM-free
 * so every decision below is unit-tested without a render.
 *
 * The verbs are also the single home for expand-on-demand: the Detail panel's "Expand neighbors"
 * runs `expandVertices` over one id, the tab runs it over the match set. See
 * features/open/canvas-interact/spec.md.
 */

/**
 * The most candidates a DATABASE-degree evaluation will sweep. Two requests per candidate, so the
 * cap is about bounding a click, not about correctness; above it the run is refused rather than
 * partially evaluated, and the cheap filters are the narrowing tool. One tuning home.
 */
export const DEGREE_SWEEP_CAP = 1_000;

/**
 * The most matched vertices one Expand may sweep. A single vertex's expand is already several
 * requests (both adjacency listings, then every edge and endpoint), so this is the difference
 * between a click and a thousand-request storm.
 */
export const EXPAND_SWEEP_CAP = 100;

/** How many vertices are expanded (or degree-probed) concurrently in one batched round. */
export const INTERACT_BATCH_SIZE = 8;

/** Which edges a degree comparison counts. */
export type DegreeSource = "database" | "canvas";
export type DegreeDirection = "in" | "out" | "total";
export type DegreeOp = "over" | "under";
export type SemanticDirection = "closer" | "farther";

/**
 * The filters that need no server round trip, so they evaluate live on every render: label and
 * property read the canvas snapshot, and on-canvas degree counts the edges actually loaded.
 * A field is INACTIVE when empty, which is why the degree value is a string - "" has to mean
 * "not filtering", and 0 is a legitimate threshold.
 */
export interface CheapFilters {
  label: string;
  propKey: string;
  propTerm: string;
  degreeSource: DegreeSource;
  degreeDirection: DegreeDirection;
  degreeOp: DegreeOp;
  /** The comparison value as typed; "" (or unparseable) leaves the degree filter inactive. */
  degreeValue: string;
}

/** A degree filter that is actually on: parsed, and known to be the canvas-counted source. */
export interface ActiveDegree {
  direction: DegreeDirection;
  op: DegreeOp;
  value: number;
}

/**
 * The degree filter as a number, or null when it is not filtering at all. A blank or
 * unparseable value is inactive rather than 0: typing "over 0" is a real filter and must not be
 * what an empty box means.
 */
export function activeDegree(filters: CheapFilters): ActiveDegree | null {
  const raw = filters.degreeValue.trim();
  if (raw === "") return null;
  const value = Number(raw);
  if (!Number.isFinite(value)) return null;
  return { direction: filters.degreeDirection, op: filters.degreeOp, value };
}

/** Whether any cheap filter is on, which is what makes an empty match set meaningful. */
export function anyCheapActive(filters: CheapFilters): boolean {
  return (
    filters.label.trim() !== "" ||
    filters.propKey.trim() !== "" ||
    (filters.degreeSource === "canvas" && activeDegree(filters) !== null)
  );
}

/** `over` and `under` are strict, so "over 50" excludes a vertex of exactly 50. */
export function compareDegree(degree: number, op: DegreeOp, value: number): boolean {
  return op === "over" ? degree > value : degree < value;
}

/** One node's degree in the requested direction, from a visibleDegrees entry (absent = 0). */
function directed(entry: { in: number; out: number } | undefined, direction: DegreeDirection): number {
  const inDegree = entry?.in ?? 0;
  const outDegree = entry?.out ?? 0;
  if (direction === "in") return inDegree;
  if (direction === "out") return outDegree;
  return inDegree + outDegree;
}

/** The stringified snapshot value a property term is matched against (case-folded by the caller). */
function propertyText(value: string | number | boolean): string {
  return typeof value === "string" ? value : String(value);
}

/**
 * The canvas vertices surviving every ACTIVE cheap filter, AND-composed.
 *
 * A stub vertex (merged as an edge's unloaded endpoint: no `props`, null label) matches no label
 * and no property filter, because nothing was ever read about it - but an on-canvas degree filter
 * counts its loaded edges like any other vertex's, since that is a fact about the view rather
 * than about the element.
 *
 * On-canvas degree delegates to the style engine's `visibleDegrees`, which is what degree-based
 * node sizing already reads: one home for counting the view's edges.
 */
export function matchCheap(
  nodes: CanvasNode[],
  edges: CanvasEdge[],
  filters: CheapFilters,
): CanvasNode[] {
  const label = filters.label.trim();
  const propKey = filters.propKey.trim();
  const propTerm = filters.propTerm.trim().toLowerCase();
  const degree = filters.degreeSource === "canvas" ? activeDegree(filters) : null;
  const degrees = degree ? visibleDegrees(edges) : null;

  return nodes.filter((node) => {
    if (label !== "" && node.label !== label) return false;

    if (propKey !== "") {
      const value = node.props?.[propKey];
      if (value === undefined) return false;
      if (propTerm !== "" && !propertyText(value).toLowerCase().includes(propTerm)) return false;
    }

    if (degree && degrees) {
      if (!compareDegree(directed(degrees.get(node.id), degree.direction), degree.op, degree.value)) {
        return false;
      }
    }

    return true;
  });
}

/** The ids whose fetched degree satisfies the comparison; an id with no score is not matched. */
export function applyDegree(
  degrees: ReadonlyMap<number, number>,
  op: DegreeOp,
  value: number,
): Set<number> {
  const matched = new Set<number>();
  for (const [id, degree] of degrees) {
    if (compareDegree(degree, op, value)) matched.add(id);
  }
  return matched;
}

/** What a semantic threshold decided, including how much of the input it could not judge. */
export interface SemanticVerdict {
  matched: Set<number>;
  /** Candidates the search returned no score for. They match NOTHING, in either direction. */
  unscored: number;
}

/**
 * The candidates a semantic threshold keeps, oriented by the search's own metric.
 *
 * `higherIsBetter` comes from the server (Cosine/DotProduct: higher is closer; L2: lower is), and
 * the threshold is in that metric's RAW units - the client never re-derives a similarity.
 *
 * A candidate the result carries no score for is UNSCORED and matches neither direction. It has
 * no embedding, or it fell outside the search window, and either way nothing measured it: "I
 * could not look" must never become "it is far", least of all in front of a bulk remove.
 */
export function applySemantic(
  candidateIds: number[],
  result: VectorSearchResultREST | null,
  direction: SemanticDirection,
  threshold: number,
): SemanticVerdict {
  const scores = new Map<number, number>();
  for (const hit of result?.results ?? []) scores.set(hit.graphElementId, hit.score);
  const higherIsBetter = result?.higherIsBetter ?? true;

  const matched = new Set<number>();
  let unscored = 0;
  for (const id of candidateIds) {
    const score = scores.get(id);
    if (score === undefined) {
      unscored++;
      continue;
    }
    // "closer" means better by the metric's own orientation; the threshold is a raw score.
    const closer = higherIsBetter ? score >= threshold : score <= threshold;
    if (direction === "closer" ? closer : !closer) matched.add(id);
  }
  return { matched, unscored };
}

/** How far a batched sweep has got, for the panel's progress line. */
export interface SweepProgress {
  done: number;
  total: number;
}

/**
 * Every candidate's DATABASE degree in one direction, batched and abortable.
 *
 * `total` costs both requests per vertex; a direction costs one. A vertex whose degree cannot be
 * read is left OUT of the map rather than recorded as 0, so it fails the comparison instead of
 * being swept up by "under x" - the same rule the semantic filter applies to an unscored vertex.
 */
export async function degreeSweep(
  instance: InstanceConfig,
  ids: number[],
  options: {
    direction: DegreeDirection;
    signal?: AbortSignal;
    onProgress?: (progress: SweepProgress) => void;
  },
): Promise<Map<number, number>> {
  const degrees = new Map<number, number>();
  const { direction, signal } = options;

  for (let i = 0; i < ids.length; i += INTERACT_BATCH_SIZE) {
    if (signal?.aborted) break;
    const batch = ids.slice(i, i + INTERACT_BATCH_SIZE);
    const results = await Promise.all(
      batch.map(async (id) => {
        try {
          const [inDegree, outDegree] = await Promise.all([
            direction === "out" ? 0 : getInDegree(instance, id, signal),
            direction === "in" ? 0 : getOutDegree(instance, id, signal),
          ]);
          return { id, degree: (inDegree ?? 0) + (outDegree ?? 0) };
        } catch {
          return null;
        }
      }),
    );
    for (const result of results) {
      if (result) degrees.set(result.id, result.degree);
    }
    options.onProgress?.({ done: Math.min(i + batch.length, ids.length), total: ids.length });
  }

  return degrees;
}

/** What an expand sweep actually did, which is never assumed to be "all of it". */
export interface ExpandOutcome {
  /** Vertices whose neighborhood was fetched and merged. */
  done: number;
  total: number;
  /** True when the canvas element budget stopped the sweep before the last vertex. */
  stoppedAtBudget: boolean;
  cancelled: boolean;
  /** Vertices whose neighborhood fetch failed outright (counted, never silent). */
  failed: number;
}

/**
 * Expand a set of vertices: one hop each, merged as every batch lands.
 *
 * The one home for expand-on-demand. `onMerge` is called per batch rather than once at the end,
 * so a long sweep grows the canvas visibly and a cancel keeps what already landed. `skip` is
 * re-read per batch through `liveElementCount`/`skipIds` callbacks rather than captured once,
 * because each merge changes what the next batch would re-fetch.
 *
 * The sweep stops when the canvas reaches `elementBudget` and SAYS so (`stoppedAtBudget`), the
 * same honesty as the whole-graph truncation notice - silently expanding half a match set would
 * look identical to a graph that simply has fewer neighbors.
 */
export async function expandVertices(
  instance: InstanceConfig,
  ids: number[],
  options: {
    /** Ids not to re-hydrate, re-read before each batch (the live canvas). */
    skipIds: () => ReadonlySet<number>;
    /** Current canvas element count, re-read before each batch. */
    liveElementCount?: () => number;
    /** Stop once the canvas holds this many elements. Omitted = no budget. */
    elementBudget?: number;
    onMerge: (vertices: VertexREST[], edges: EdgeREST[]) => void;
    onProgress?: (progress: SweepProgress) => void;
    signal?: AbortSignal;
    /** Per-vertex edge cap; defaults to the standing expand cap. */
    cap?: number;
  },
): Promise<ExpandOutcome> {
  const cap = options.cap ?? EXPAND_EDGE_CAP;
  const outcome: ExpandOutcome = {
    done: 0,
    total: ids.length,
    stoppedAtBudget: false,
    cancelled: false,
    failed: 0,
  };

  for (let i = 0; i < ids.length; i += INTERACT_BATCH_SIZE) {
    if (options.signal?.aborted) {
      outcome.cancelled = true;
      break;
    }
    if (
      options.elementBudget !== undefined &&
      (options.liveElementCount?.() ?? 0) >= options.elementBudget
    ) {
      outcome.stoppedAtBudget = true;
      break;
    }

    const batch = ids.slice(i, i + INTERACT_BATCH_SIZE);
    const skip = options.skipIds();
    const neighborhoods = await Promise.all(
      batch.map((id) =>
        fetchVertexNeighborhood(instance, id, { cap, skipNeighborIds: skip }).catch(() => null),
      ),
    );

    const vertices: VertexREST[] = [];
    const edges: EdgeREST[] = [];
    for (const neighborhood of neighborhoods) {
      if (!neighborhood) {
        outcome.failed++;
        continue;
      }
      vertices.push(...neighborhood.vertices);
      edges.push(...neighborhood.edges);
    }
    // One merge per batch: N store writes per round would re-render the canvas N times.
    if (vertices.length > 0 || edges.length > 0) options.onMerge(vertices, edges);

    outcome.done = Math.min(i + batch.length, ids.length);
    options.onProgress?.({ done: outcome.done, total: ids.length });
  }

  if (options.signal?.aborted) outcome.cancelled = true;
  return outcome;
}
