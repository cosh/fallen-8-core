// MIT License
//
// canvas-interact.test.ts
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

import { beforeEach, describe, expect, it, vi } from "vitest";
import type { InstanceConfig } from "../src/instances/types";
import type { EdgeREST, VectorSearchResultREST, VertexREST } from "../src/api/types";

/**
 * Pure logic of the Canvas "Interact" tab (feature canvas-interact): the cheap filters, the two
 * threshold appliers, and the batched database-degree / expand sweeps. No DOM, no render.
 */

const getInDegreeMock = vi.fn<(i: InstanceConfig, id: number, s?: AbortSignal) => Promise<number>>();
const getOutDegreeMock = vi.fn<(i: InstanceConfig, id: number, s?: AbortSignal) => Promise<number>>();

vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    getInDegree: (i: InstanceConfig, id: number, s?: AbortSignal) => getInDegreeMock(i, id, s),
    getOutDegree: (i: InstanceConfig, id: number, s?: AbortSignal) => getOutDegreeMock(i, id, s),
  };
});

const neighborhoodMock = vi.fn<
  (
    i: InstanceConfig,
    id: number,
    o: { cap: number; skipNeighborIds?: ReadonlySet<number> },
  ) => Promise<{ vertices: VertexREST[]; edges: EdgeREST[]; truncated: boolean }>
>();

vi.mock("../src/lib/neighborhood", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/lib/neighborhood")>();
  return {
    ...original,
    fetchVertexNeighborhood: (
      i: InstanceConfig,
      id: number,
      o: { cap: number; skipNeighborIds?: ReadonlySet<number> },
    ) => neighborhoodMock(i, id, o),
  };
});

import {
  DEGREE_SWEEP_CAP,
  EXPAND_SWEEP_CAP,
  activeDegree,
  anyCheapActive,
  applyDegree,
  applySemantic,
  compareDegree,
  degreeSweep,
  expandVertices,
  matchCheap,
  type CheapFilters,
} from "../src/lib/canvasInteract";
import { CANVAS_PROP_MAX_STRING, type CanvasEdge, type CanvasNode } from "../src/state/instanceStore";
import { EXPAND_EDGE_CAP } from "../src/lib/neighborhood";

const instance = { id: "local", name: "local", url: "" } as unknown as InstanceConfig;

function filters(overrides: Partial<CheapFilters> = {}): CheapFilters {
  return {
    label: "",
    propKey: "",
    propTerm: "",
    degreeSource: "database",
    degreeDirection: "total",
    degreeOp: "over",
    degreeValue: "",
    ...overrides,
  };
}

function node(id: number, label: string | null, props?: CanvasNode["props"]): CanvasNode {
  return { id, label, ...(props ? { props } : {}) };
}

function edge(id: number, source: number, target: number): CanvasEdge {
  return { id, source, target, edgePropertyId: "knows", label: null, props: {} };
}

function vertex(id: number): VertexREST {
  return { id, creationDate: "", modificationDate: "", label: "person", kind: "vertex", properties: [] };
}

function restEdge(id: number, source: number, target: number): EdgeREST {
  return {
    id,
    creationDate: "",
    modificationDate: "",
    sourceVertex: source,
    targetVertex: target,
    edgePropertyId: "knows",
    label: null,
  };
}

function scored(
  results: [number, number][],
  higherIsBetter = true,
  metric = "Cosine",
): VectorSearchResultREST {
  return {
    metric,
    higherIsBetter,
    results: results.map(([graphElementId, score]) => ({ graphElementId, score })),
  };
}

beforeEach(() => {
  getInDegreeMock.mockReset().mockResolvedValue(0);
  getOutDegreeMock.mockReset().mockResolvedValue(0);
  neighborhoodMock
    .mockReset()
    .mockImplementation((_i, id) =>
      Promise.resolve({ vertices: [vertex(id * 100)], edges: [restEdge(id * 10, id, id * 100)], truncated: false }),
    );
});

describe("filter activity, which is what makes an empty match set meaningful", () => {
  it("treats a blank degree value as not filtering, and 0 as a real bound", () => {
    expect(activeDegree(filters({ degreeValue: "" }))).toBeNull();
    expect(activeDegree(filters({ degreeValue: "   " }))).toBeNull();
    expect(activeDegree(filters({ degreeValue: "abc" }))).toBeNull();
    expect(activeDegree(filters({ degreeValue: "0" }))).toEqual({
      direction: "total",
      op: "over",
      value: 0,
    });
  });

  it("reports no cheap filter active when every row is blank (the expand-all case)", () => {
    expect(anyCheapActive(filters())).toBe(false);
    expect(anyCheapActive(filters({ label: "person" }))).toBe(true);
    expect(anyCheapActive(filters({ propKey: "name" }))).toBe(true);
  });

  it("counts a degree filter as cheap only for the canvas source", () => {
    // The database source is a server sweep, so it belongs to Preview rather than to the live count.
    expect(anyCheapActive(filters({ degreeValue: "5", degreeSource: "database" }))).toBe(false);
    expect(anyCheapActive(filters({ degreeValue: "5", degreeSource: "canvas" }))).toBe(true);
  });

  it("compares strictly, so a vertex exactly ON the bound is not matched", () => {
    expect(compareDegree(50, "over", 50)).toBe(false);
    expect(compareDegree(51, "over", 50)).toBe(true);
    expect(compareDegree(50, "under", 50)).toBe(false);
    expect(compareDegree(49, "under", 50)).toBe(true);
  });
});

describe("matchCheap", () => {
  const nodes = [
    node(1, "person", { name: "Ada Lovelace", age: 36 }),
    node(2, "person", { name: "Alan Turing", age: 41 }),
    node(3, "pdu", { name: "brake_status" }),
    node(4, null), // stub: an edge endpoint nothing was ever read about
  ];

  it("matches every canvas vertex when no filter is active", () => {
    expect(matchCheap(nodes, [], filters()).map((n) => n.id)).toEqual([1, 2, 3, 4]);
  });

  it("matches a label exactly, and never matches a stub", () => {
    expect(matchCheap(nodes, [], filters({ label: "person" })).map((n) => n.id)).toEqual([1, 2]);
    expect(matchCheap(nodes, [], filters({ label: "Person" }))).toEqual([]);
    expect(matchCheap(nodes, [], filters({ label: "pdu" })).map((n) => n.id)).toEqual([3]);
  });

  it("matches on a property KEY being present when no term is given", () => {
    expect(matchCheap(nodes, [], filters({ propKey: "age" })).map((n) => n.id)).toEqual([1, 2]);
    expect(matchCheap(nodes, [], filters({ propKey: "missing" }))).toEqual([]);
  });

  it("matches a property term case-insensitively, over stringified non-strings", () => {
    expect(matchCheap(nodes, [], filters({ propKey: "name", propTerm: "ada" })).map((n) => n.id)).toEqual([1]);
    expect(matchCheap(nodes, [], filters({ propKey: "name", propTerm: "TURING" })).map((n) => n.id)).toEqual([2]);
    // A number is compared as its text, so "4" finds age 41 - the same contains semantics.
    expect(matchCheap(nodes, [], filters({ propKey: "age", propTerm: "4" })).map((n) => n.id)).toEqual([2]);
  });

  it("cannot see past the snapshot's string cap, which the field help states", () => {
    const long = "x".repeat(CANVAS_PROP_MAX_STRING) + "needle";
    const capped = long.slice(0, CANVAS_PROP_MAX_STRING); // what snapshotProps would have stored
    const nodesWithLong = [node(9, "doc", { text: capped })];

    expect(matchCheap(nodesWithLong, [], filters({ propKey: "text", propTerm: "needle" }))).toEqual([]);
    expect(matchCheap(nodesWithLong, [], filters({ propKey: "text", propTerm: "xxx" })).map((n) => n.id)).toEqual([9]);
  });

  it("ANDs the rows together", () => {
    const matched = matchCheap(nodes, [], filters({ label: "person", propKey: "name", propTerm: "alan" }));
    expect(matched.map((n) => n.id)).toEqual([2]);
  });

  it("counts on-canvas degree over the LOADED edges, per direction", () => {
    // 1 -> 2, 1 -> 3, 3 -> 2 : out(1)=2, in(2)=2, in(3)=1, out(3)=1, node 4 has none.
    const edges = [edge(10, 1, 2), edge(11, 1, 3), edge(12, 3, 2)];

    const over1Total = matchCheap(nodes, edges, filters({ degreeSource: "canvas", degreeValue: "1" }));
    expect(over1Total.map((n) => n.id)).toEqual([1, 2, 3]);

    const inOver1 = matchCheap(
      nodes,
      edges,
      filters({ degreeSource: "canvas", degreeDirection: "in", degreeValue: "1" }),
    );
    expect(inOver1.map((n) => n.id)).toEqual([2]);

    const outUnder1 = matchCheap(
      nodes,
      edges,
      filters({ degreeSource: "canvas", degreeDirection: "out", degreeOp: "under", degreeValue: "1" }),
    );
    expect(outUnder1.map((n) => n.id)).toEqual([2, 4]);
  });

  it("reads a never-expanded vertex as degree 0 on the canvas source, which is the view's answer", () => {
    // The blind spot the field help names: no loaded edges reads as 0 whatever the database knows.
    const matched = matchCheap(nodes, [], filters({ degreeSource: "canvas", degreeOp: "under", degreeValue: "1" }));
    expect(matched.map((n) => n.id)).toEqual([1, 2, 3, 4]);
  });

  it("ignores a degree filter whose source is the database (Preview owns that one)", () => {
    const edges = [edge(10, 1, 2)];
    const matched = matchCheap(nodes, edges, filters({ degreeSource: "database", degreeValue: "99" }));
    expect(matched.map((n) => n.id)).toEqual([1, 2, 3, 4]);
  });
});

describe("applyDegree", () => {
  it("keeps the ids satisfying the comparison and drops the rest", () => {
    const degrees = new Map([
      [1, 100],
      [2, 50],
      [3, 0],
    ]);
    expect([...applyDegree(degrees, "over", 50)]).toEqual([1]);
    expect([...applyDegree(degrees, "under", 50)]).toEqual([3]);
  });

  it("matches nothing for an id whose degree was never read", () => {
    // degreeSweep leaves a failed read OUT of the map; "under x" must not sweep it up.
    expect([...applyDegree(new Map(), "under", 1_000_000)]).toEqual([]);
  });
});

describe("applySemantic", () => {
  it("keeps the closer ones on a higher-is-better metric", () => {
    const verdict = applySemantic([1, 2, 3], scored([[1, 0.9], [2, 0.5], [3, 0.1]]), "closer", 0.5);
    expect([...verdict.matched]).toEqual([1, 2]);
    expect(verdict.unscored).toBe(0);
  });

  it("keeps the farther ones on a higher-is-better metric", () => {
    const verdict = applySemantic([1, 2, 3], scored([[1, 0.9], [2, 0.5], [3, 0.1]]), "farther", 0.5);
    expect([...verdict.matched]).toEqual([3]);
  });

  it("inverts the comparison when lower is better (L2)", () => {
    const result = scored([[1, 0.2], [2, 0.8]], false, "L2");
    expect([...applySemantic([1, 2], result, "closer", 0.5).matched]).toEqual([1]);
    expect([...applySemantic([1, 2], result, "farther", 0.5).matched]).toEqual([2]);
  });

  it("never matches an unscored candidate, in EITHER direction, and counts it", () => {
    // 7 has no embedding (or fell outside the search window). "I could not look" is not "it is far":
    // matching it under "farther" would bulk-remove vertices nothing measured.
    const result = scored([[1, 0.9]]);
    const closer = applySemantic([1, 7], result, "closer", 0.5);
    const farther = applySemantic([1, 7], result, "farther", 0.5);

    expect([...closer.matched]).toEqual([1]);
    expect(closer.unscored).toBe(1);
    expect([...farther.matched]).toEqual([]);
    expect(farther.unscored).toBe(1);
  });

  it("treats a null result as everything unscored rather than everything far", () => {
    const verdict = applySemantic([1, 2], null, "farther", 0.5);
    expect([...verdict.matched]).toEqual([]);
    expect(verdict.unscored).toBe(2);
  });

  it("ignores scores for elements that are not candidates", () => {
    const verdict = applySemantic([1], scored([[1, 0.9], [42, 0.99]]), "closer", 0.5);
    expect([...verdict.matched]).toEqual([1]);
  });

  it("includes a score exactly ON the threshold as closer", () => {
    expect([...applySemantic([1], scored([[1, 0.5]]), "closer", 0.5).matched]).toEqual([1]);
    expect([...applySemantic([1], scored([[1, 0.5]]), "farther", 0.5).matched]).toEqual([]);
  });
});

describe("degreeSweep", () => {
  it("sums both directions for total and asks for one only when the direction is one", async () => {
    getInDegreeMock.mockResolvedValue(3);
    getOutDegreeMock.mockResolvedValue(4);

    const total = await degreeSweep(instance, [1], { direction: "total" });
    expect(total.get(1)).toBe(7);
    expect(getInDegreeMock).toHaveBeenCalledTimes(1);
    expect(getOutDegreeMock).toHaveBeenCalledTimes(1);

    getInDegreeMock.mockClear();
    getOutDegreeMock.mockClear();
    const inOnly = await degreeSweep(instance, [1], { direction: "in" });
    expect(inOnly.get(1)).toBe(3);
    expect(getOutDegreeMock).not.toHaveBeenCalled();

    getInDegreeMock.mockClear();
    getOutDegreeMock.mockClear();
    const outOnly = await degreeSweep(instance, [1], { direction: "out" });
    expect(outOnly.get(1)).toBe(4);
    expect(getInDegreeMock).not.toHaveBeenCalled();
  });

  it("reports progress as batches land, up to the total", async () => {
    const seen: { done: number; total: number }[] = [];
    await degreeSweep(instance, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10], {
      direction: "in",
      onProgress: (p) => seen.push(p),
    });

    expect(seen[seen.length - 1]).toEqual({ done: 10, total: 10 });
    expect(seen.every((p) => p.done <= p.total)).toBe(true);
  });

  it("leaves a failed read out of the map rather than recording it as zero", async () => {
    getInDegreeMock.mockImplementation((_i, id) =>
      id === 2 ? Promise.reject(new Error("boom")) : Promise.resolve(5),
    );

    const degrees = await degreeSweep(instance, [1, 2, 3], { direction: "in" });

    expect(degrees.get(1)).toBe(5);
    expect(degrees.has(2)).toBe(false);
    expect(degrees.get(3)).toBe(5);
  });

  it("stops issuing requests once aborted", async () => {
    const controller = new AbortController();
    controller.abort();

    const degrees = await degreeSweep(instance, [1, 2, 3], {
      direction: "in",
      signal: controller.signal,
    });

    expect(degrees.size).toBe(0);
    expect(getInDegreeMock).not.toHaveBeenCalled();
  });

  it("passes the signal down so in-flight requests are cancelled too", async () => {
    const controller = new AbortController();
    await degreeSweep(instance, [1], { direction: "in", signal: controller.signal });
    expect(getInDegreeMock).toHaveBeenCalledWith(instance, 1, controller.signal);
  });

  it("has a cap high enough to be about bounding a click, not correctness", () => {
    expect(DEGREE_SWEEP_CAP).toBeGreaterThan(EXPAND_SWEEP_CAP);
  });
});

describe("expandVertices", () => {
  it("merges each batch as it lands rather than once at the end", async () => {
    const merges: number[] = [];
    const outcome = await expandVertices(instance, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10], {
      skipIds: () => new Set(),
      onMerge: (vertices) => merges.push(vertices.length),
    });

    expect(outcome.done).toBe(10);
    expect(outcome.total).toBe(10);
    // 10 ids at a batch size of 8 is two rounds, so two merges - not ten, and not one.
    expect(merges.length).toBe(2);
  });

  it("re-reads the skip set per batch, so a merged neighbor is not re-fetched", async () => {
    const canvas = new Set<number>([1, 2]);
    await expandVertices(instance, Array.from({ length: 10 }, (_v, i) => i + 1), {
      skipIds: () => canvas,
      onMerge: (vertices) => {
        for (const v of vertices) canvas.add(v.id);
      },
    });

    // The second round must have been told about what the first round merged.
    const secondRoundSkip = neighborhoodMock.mock.calls[9][2].skipNeighborIds!;
    expect(secondRoundSkip.has(100)).toBe(true);
  });

  it("uses the standing per-vertex edge cap unless told otherwise", async () => {
    await expandVertices(instance, [1], { skipIds: () => new Set(), onMerge: () => {} });
    expect(neighborhoodMock.mock.calls[0][2].cap).toBe(EXPAND_EDGE_CAP);

    await expandVertices(instance, [2], { skipIds: () => new Set(), onMerge: () => {}, cap: 7 });
    expect(neighborhoodMock.mock.calls[1][2].cap).toBe(7);
  });

  it("stops at the element budget and says so", async () => {
    let count = 0;
    const outcome = await expandVertices(instance, Array.from({ length: 40 }, (_v, i) => i + 1), {
      skipIds: () => new Set(),
      liveElementCount: () => count,
      elementBudget: 10,
      onMerge: (vertices, edges) => {
        count += vertices.length + edges.length;
      },
    });

    expect(outcome.stoppedAtBudget).toBe(true);
    expect(outcome.done).toBeLessThan(40);
    expect(outcome.cancelled).toBe(false);
  });

  it("keeps what already landed when cancelled mid-sweep", async () => {
    const controller = new AbortController();
    const merged: number[] = [];
    neighborhoodMock.mockImplementation((_i, id) => {
      // Abort during the first round; the second must not run.
      if (id === 1) controller.abort();
      return Promise.resolve({ vertices: [vertex(id * 100)], edges: [], truncated: false });
    });

    const outcome = await expandVertices(instance, Array.from({ length: 20 }, (_v, i) => i + 1), {
      skipIds: () => new Set(),
      signal: controller.signal,
      onMerge: (vertices) => merged.push(...vertices.map((v) => v.id)),
    });

    expect(outcome.cancelled).toBe(true);
    expect(outcome.done).toBe(8); // the first round completed and was kept
    expect(merged.length).toBe(8);
  });

  it("counts a failed vertex instead of failing the sweep", async () => {
    neighborhoodMock.mockImplementation((_i, id) =>
      id === 2
        ? Promise.reject(new Error("boom"))
        : Promise.resolve({ vertices: [vertex(id * 100)], edges: [], truncated: false }),
    );

    const outcome = await expandVertices(instance, [1, 2, 3], {
      skipIds: () => new Set(),
      onMerge: () => {},
    });

    expect(outcome.failed).toBe(1);
    expect(outcome.done).toBe(3);
  });

  it("does not merge at all when a batch found nothing", async () => {
    neighborhoodMock.mockResolvedValue({ vertices: [], edges: [], truncated: false });
    const merges: number[] = [];

    await expandVertices(instance, [1, 2], {
      skipIds: () => new Set(),
      onMerge: () => merges.push(1),
    });

    expect(merges).toEqual([]);
  });

  it("is a no-op on an empty match set", async () => {
    const outcome = await expandVertices(instance, [], { skipIds: () => new Set(), onMerge: () => {} });
    expect(outcome).toEqual({ done: 0, total: 0, stoppedAtBudget: false, cancelled: false, failed: 0 });
    expect(neighborhoodMock).not.toHaveBeenCalled();
  });
});
