// MIT License
//
// connect-paths.test.ts
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

import { beforeEach, describe, expect, it } from "vitest";
import {
  buildPairs,
  introducedSets,
  pairCount,
  removalSet,
  synthesizeEdges,
  type IntroducedSets,
} from "../src/lib/connectPaths";
import {
  DEFAULT_CANVAS_TOOLS_DRAFT,
  getInstanceStore,
  resetInstanceStoresForTests,
} from "../src/state/instanceStore";
import type { PathElementREST, PathREST } from "../src/api/types";

/**
 * Feature canvas-find-connect - the DOM-free logic the Connect tab stands on: unordered pair
 * enumeration with the pair cap, the run-baseline introduced-set diff, and the reference-counted
 * removal that keeps a shared intermediate alive until its last claiming path is retracted.
 */

function el(source: number, target: number, edgeId: number, type = "knows"): PathElementREST {
  return { sourceVertexId: source, targetVertexId: target, edgeId, edgePropertyId: type, weight: 0 };
}

function path(...elements: PathElementREST[]): PathREST {
  return { pathElements: elements, totalWeight: 0 };
}

const intro = (nodeIds: number[], edgeIds: number[]): IntroducedSets => ({
  nodeIds: new Set(nodeIds),
  edgeIds: new Set(edgeIds),
});

describe("buildPairs", () => {
  it("returns no pairs for empty or single-vertex input", () => {
    expect(buildPairs([])).toEqual([]);
    expect(buildPairs([7])).toEqual([]);
  });

  it("enumerates every unordered pair as [low, high] in ascending order", () => {
    expect(buildPairs([1, 2, 3])).toEqual([
      [1, 2],
      [1, 3],
      [2, 3],
    ]);
  });

  it("sorts and orders lower id first regardless of input order", () => {
    expect(buildPairs([5, 2, 9])).toEqual([
      [2, 5],
      [2, 9],
      [5, 9],
    ]);
  });

  it("collapses duplicate ids and excludes self-pairs", () => {
    expect(buildPairs([3, 1, 2, 1, 3])).toEqual([
      [1, 2],
      [1, 3],
      [2, 3],
    ]);
  });

  it("produces exactly N*(N-1)/2 pairs for N distinct ids", () => {
    const ids = [10, 20, 30, 40, 50];
    expect(buildPairs(ids)).toHaveLength(10);
    expect(buildPairs(ids)).toHaveLength(pairCount(ids.length));
  });
});

describe("pairCount", () => {
  it("is zero below two vertices and matches N*(N-1)/2 otherwise", () => {
    expect(pairCount(0)).toBe(0);
    expect(pairCount(1)).toBe(0);
    expect(pairCount(2)).toBe(1);
    expect(pairCount(5)).toBe(10);
    expect(pairCount(32)).toBe(496);
  });
});

describe("introducedSets", () => {
  const baseline = { nodes: new Set([1, 2]), edges: new Set<number>() };

  it("introduces only the edge for a direct-edge path (endpoints already on canvas)", () => {
    const result = introducedSets(path(el(1, 2, 100)), baseline);
    expect([...result.nodeIds]).toEqual([]);
    expect([...result.edgeIds]).toEqual([100]);
  });

  it("introduces intermediates and hop edges for a multi-hop path", () => {
    // 1 -> 3 -> 4 -> 2, endpoints 1 and 2 on the canvas.
    const result = introducedSets(path(el(1, 3, 100), el(3, 4, 101), el(4, 2, 102)), baseline);
    expect([...result.nodeIds].sort((a, b) => a - b)).toEqual([3, 4]);
    expect([...result.edgeIds].sort((a, b) => a - b)).toEqual([100, 101, 102]);
  });

  it("introduces nothing when every element is already on the canvas", () => {
    const fullBaseline = { nodes: new Set([1, 2, 3]), edges: new Set([100, 101]) };
    const result = introducedSets(path(el(1, 3, 100), el(3, 2, 101)), fullBaseline);
    expect(result.nodeIds.size).toBe(0);
    expect(result.edgeIds.size).toBe(0);
  });

  it("does not re-introduce an edge already in the baseline", () => {
    const withEdge = { nodes: new Set([1, 2]), edges: new Set([100]) };
    const result = introducedSets(path(el(1, 3, 100), el(3, 2, 101)), withEdge);
    expect([...result.nodeIds]).toEqual([3]);
    expect([...result.edgeIds]).toEqual([101]);
  });
});

describe("removalSet", () => {
  it("removes everything for a disjoint path with no other claimants", () => {
    const target = intro([3, 4], [100, 101]);
    const result = removalSet(target, []);
    expect([...result.nodeIds].sort((a, b) => a - b)).toEqual([3, 4]);
    expect([...result.edgeIds].sort((a, b) => a - b)).toEqual([100, 101]);
  });

  it("keeps an intermediate still claimed by another added path", () => {
    // Both paths route through vertex 3 / edge 100; removing one must keep 3 and 100.
    const target = intro([3, 4], [100, 101]);
    const other = intro([3], [100]);
    const result = removalSet(target, [other]);
    expect([...result.nodeIds]).toEqual([4]);
    expect([...result.edgeIds]).toEqual([101]);
  });

  it("releases the shared element only when the last other claimant is gone", () => {
    const target = intro([3], [100]);
    // With another claimant present, nothing of the shared set leaves.
    expect(removalSet(target, [intro([3], [100])]).nodeIds.size).toBe(0);
    // As the last path holding 3/100, its removal drops them.
    const solo = removalSet(target, []);
    expect([...solo.nodeIds]).toEqual([3]);
    expect([...solo.edgeIds]).toEqual([100]);
  });

  it("honours claims spread across two other paths", () => {
    const target = intro([3, 4, 5], [100, 101, 102]);
    const a = intro([3], [100]);
    const b = intro([5], [102]);
    const result = removalSet(target, [a, b]);
    expect([...result.nodeIds]).toEqual([4]);
    expect([...result.edgeIds]).toEqual([101]);
  });
});

describe("synthesizeEdges", () => {
  it("maps each hop to a canvas edge with verbatim endpoints and type", () => {
    const edges = synthesizeEdges(path(el(1, 3, 100, "knows"), el(3, 2, 101, "likes")));
    expect(edges).toEqual([
      {
        id: 100,
        creationDate: "",
        modificationDate: "",
        sourceVertex: 1,
        targetVertex: 3,
        edgePropertyId: "knows",
        label: null,
      },
      {
        id: 101,
        creationDate: "",
        modificationDate: "",
        sourceVertex: 3,
        targetVertex: 2,
        edgePropertyId: "likes",
        label: null,
      },
    ]);
  });

  it("renders a missing edge type as null rather than undefined", () => {
    const [edge] = synthesizeEdges(
      path({ sourceVertexId: 1, targetVertexId: 2, edgeId: 5, weight: 0 }),
    );
    expect(edge.edgePropertyId).toBeNull();
  });
});

describe("canvasToolsDraft store", () => {
  beforeEach(() => {
    resetInstanceStoresForTests();
    window.localStorage.clear();
  });

  it("defaults to the style tab with the lean Connect defaults", () => {
    expect(getInstanceStore("inst-a").getState().canvasToolsDraft).toEqual(
      DEFAULT_CANVAS_TOOLS_DRAFT,
    );
    expect(DEFAULT_CANVAS_TOOLS_DRAFT).toMatchObject({
      tab: "style",
      connectMaxDepth: 3,
      connectScope: "all",
      findResultType: "Both",
    });
  });

  it("patches the draft, scoped per instance", () => {
    const a = getInstanceStore("inst-a");
    const b = getInstanceStore("inst-b");
    a.getState().setCanvasToolsDraft({ tab: "find", findTerm: "acme" });

    expect(a.getState().canvasToolsDraft).toMatchObject({ tab: "find", findTerm: "acme" });
    // Untouched fields keep their defaults; the sibling instance is unaffected.
    expect(a.getState().canvasToolsDraft.connectMaxDepth).toBe(3);
    expect(b.getState().canvasToolsDraft).toEqual(DEFAULT_CANVAS_TOOLS_DRAFT);
  });

  it("rehydrates a persisted draft and defaults the fields for an old workspace", () => {
    window.localStorage.setItem(
      "f8.workspace.inst-old",
      JSON.stringify({
        state: { canvasToolsDraft: { tab: "connect", connectMaxDepth: 5 } },
        version: 0,
      }),
    );

    const draft = getInstanceStore("inst-old").getState().canvasToolsDraft;
    // The persisted fields survive...
    expect(draft.tab).toBe("connect");
    expect(draft.connectMaxDepth).toBe(5);
    // ...and every field absent from the old blob picks up its default (no migration).
    expect(draft.connectScope).toBe("all");
    expect(draft.findResultType).toBe("Both");
    expect(draft.findTerm).toBe("");
  });
});
