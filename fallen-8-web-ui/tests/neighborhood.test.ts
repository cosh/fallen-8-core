import { beforeEach, describe, expect, it, vi } from "vitest";
import type { InstanceConfig } from "../src/instances/types";
import type { EdgeREST, VertexREST } from "../src/api/types";

/**
 * Shared 1-hop neighborhood fetch (feature adjacency-preview): per-property id lists →
 * hydrated edges with edgePropertyId attribution + endpoint vertices; the edge variant
 * finds the parallel bundle by id-list intersection. Also pins buildCanvasModel, the
 * REST-elements → canvas-model conversion both the store merge and the preview use.
 */

const getOutEdgePropertiesMock = vi.fn<(i: InstanceConfig, id: number) => Promise<string[] | null>>();
const getInEdgePropertiesMock = vi.fn<(i: InstanceConfig, id: number) => Promise<string[] | null>>();
const getOutEdgesMock =
  vi.fn<(i: InstanceConfig, id: number, prop: string) => Promise<number[] | null>>();
const getInEdgesMock =
  vi.fn<(i: InstanceConfig, id: number, prop: string) => Promise<number[] | null>>();
const getEdgeMock = vi.fn<(i: InstanceConfig, id: number) => Promise<EdgeREST | null>>();
const getVertexMock = vi.fn<(i: InstanceConfig, id: number) => Promise<VertexREST | null>>();
const getGraphElementMock =
  vi.fn<(i: InstanceConfig, id: number) => Promise<VertexREST | EdgeREST | null>>();

vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    getOutEdgeProperties: (i: InstanceConfig, id: number) => getOutEdgePropertiesMock(i, id),
    getInEdgeProperties: (i: InstanceConfig, id: number) => getInEdgePropertiesMock(i, id),
    getOutEdges: (i: InstanceConfig, id: number, p: string) => getOutEdgesMock(i, id, p),
    getInEdges: (i: InstanceConfig, id: number, p: string) => getInEdgesMock(i, id, p),
    getEdge: (i: InstanceConfig, id: number) => getEdgeMock(i, id),
    getVertex: (i: InstanceConfig, id: number) => getVertexMock(i, id),
    getGraphElement: (i: InstanceConfig, id: number) => getGraphElementMock(i, id),
  };
});

import { fetchEdgeNeighborhood, fetchVertexNeighborhood } from "../src/lib/neighborhood";
import { buildCanvasModel } from "../src/state/instanceStore";

const INSTANCE: InstanceConfig = { id: "t", name: "t", baseUrl: "", auth: { kind: "none" } };

function vertex(id: number, label?: string): VertexREST {
  return { id, creationDate: "c", modificationDate: "m", label, kind: "vertex" };
}

function edge(id: number, source: number, target: number, label?: string): EdgeREST {
  return {
    id,
    creationDate: "c",
    modificationDate: "m",
    label,
    kind: "edge",
    sourceVertex: source,
    targetVertex: target,
  };
}

/** Wire up per-vertex adjacency: { out: { prop: ids }, in: { prop: ids } }. */
function adjacency(
  byVertex: Record<number, { out?: Record<string, number[]>; in?: Record<string, number[]> }>,
) {
  getOutEdgePropertiesMock.mockImplementation(async (_i, id) =>
    Object.keys(byVertex[id]?.out ?? {}),
  );
  getInEdgePropertiesMock.mockImplementation(async (_i, id) =>
    Object.keys(byVertex[id]?.in ?? {}),
  );
  getOutEdgesMock.mockImplementation(async (_i, id, prop) => byVertex[id]?.out?.[prop] ?? []);
  getInEdgesMock.mockImplementation(async (_i, id, prop) => byVertex[id]?.in?.[prop] ?? []);
}

function edgeStore(...edges: EdgeREST[]) {
  const byId = new Map(edges.map((e) => [e.id, e]));
  getEdgeMock.mockImplementation(async (_i, id) => {
    const found = byId.get(id);
    if (!found) throw new Error(`no edge ${id}`);
    return found;
  });
}

beforeEach(() => {
  getOutEdgePropertiesMock.mockReset();
  getInEdgePropertiesMock.mockReset();
  getOutEdgesMock.mockReset();
  getInEdgesMock.mockReset();
  getEdgeMock.mockReset();
  getVertexMock.mockReset();
  getGraphElementMock.mockReset();
});

describe("fetchVertexNeighborhood", () => {
  it("hydrates out+in edges with property attribution and the endpoint vertices", async () => {
    adjacency({ 1: { out: { knows: [10] }, in: { follows: [11] } } });
    edgeStore(edge(10, 1, 2), edge(11, 3, 1));
    getGraphElementMock.mockImplementation(async (_i, id) => vertex(id, `v${id}`));

    const result = await fetchVertexNeighborhood(INSTANCE, 1, {
      cap: 60,
      skipNeighborIds: new Set([1]),
    });

    expect(result.edges).toEqual([
      { ...edge(10, 1, 2), edgePropertyId: "knows" },
      { ...edge(11, 3, 1), edgePropertyId: "follows" },
    ]);
    expect(result.vertices.map((v) => v.id).sort()).toEqual([2, 3]);
    expect(result.truncated).toBe(false);
    // The focus vertex is skipped, not re-fetched.
    expect(getGraphElementMock).not.toHaveBeenCalledWith(expect.anything(), 1);
  });

  it("lists a self-loop once, with the out attribution winning", async () => {
    adjacency({ 1: { out: { self: [10] }, in: { self: [10] } } });
    edgeStore(edge(10, 1, 1));
    getGraphElementMock.mockImplementation(async (_i, id) => vertex(id));

    const result = await fetchVertexNeighborhood(INSTANCE, 1, { cap: 60 });

    expect(result.edges).toHaveLength(1);
    expect(result.edges[0].edgePropertyId).toBe("self");
  });

  it("caps edges and endpoints and reports the truncation", async () => {
    adjacency({ 1: { out: { knows: [10, 11, 12] } } });
    edgeStore(edge(10, 1, 2), edge(11, 1, 3), edge(12, 1, 4));
    getGraphElementMock.mockImplementation(async (_i, id) => vertex(id));

    const result = await fetchVertexNeighborhood(INSTANCE, 1, {
      cap: 2,
      skipNeighborIds: new Set([1]),
    });

    expect(result.edges.map((e) => e.id)).toEqual([10, 11]);
    expect(result.truncated).toBe(true);
    expect(getEdgeMock).toHaveBeenCalledTimes(2);
  });

  it("skips failed hydrations instead of erroring", async () => {
    adjacency({ 1: { out: { knows: [10, 11] } } });
    edgeStore(edge(11, 1, 3)); // edge 10 is gone
    getGraphElementMock.mockImplementation(async (_i, id) => {
      if (id === 3) throw new Error("gone");
      return vertex(id);
    });

    const result = await fetchVertexNeighborhood(INSTANCE, 1, {
      cap: 60,
      skipNeighborIds: new Set([1]),
    });

    expect(result.edges.map((e) => e.id)).toEqual([11]);
    expect(result.vertices).toEqual([]);
  });

  it("tolerates a failing property listing", async () => {
    getOutEdgePropertiesMock.mockRejectedValue(new Error("boom"));
    getInEdgePropertiesMock.mockResolvedValue([]);

    const result = await fetchVertexNeighborhood(INSTANCE, 1, { cap: 60 });

    expect(result).toEqual({ vertices: [], edges: [], truncated: false });
  });
});

describe("fetchEdgeNeighborhood", () => {
  const FOCUS = edge(10, 1, 2, "knows");

  it("returns the parallel bundle in both directions, focus edge first", async () => {
    // 1 -knows-> 2 (10, 11), 2 -likes-> 1 (12), plus unrelated edges on both vertices.
    adjacency({
      1: { out: { knows: [10, 11], other: [90] }, in: { likes: [12], other: [91] } },
      2: { out: { likes: [12], other: [92] }, in: { knows: [10, 11], other: [93] } },
    });
    edgeStore(edge(11, 1, 2), edge(12, 2, 1));
    getVertexMock.mockImplementation(async (_i, id) => vertex(id, `v${id}`));

    const result = await fetchEdgeNeighborhood(INSTANCE, FOCUS, { cap: 60 });

    expect(result.edges.map((e) => e.id)).toEqual([10, 11, 12]);
    expect(result.edges[0]).toMatchObject({ id: 10, edgePropertyId: "knows" });
    expect(result.edges[2]).toMatchObject({ id: 12, edgePropertyId: "likes" });
    expect(result.vertices.map((v) => v.id)).toEqual([1, 2]);
    expect(result.truncated).toBe(false);
    // Only the siblings are hydrated - the focus edge is already in hand, and the
    // unrelated edges (90-93) never leave their id lists.
    expect(getEdgeMock).toHaveBeenCalledTimes(2);
  });

  it("keeps the focus edge even when the listings miss it (raced mutation)", async () => {
    adjacency({ 1: {}, 2: {} });
    getVertexMock.mockImplementation(async (_i, id) => vertex(id));

    const result = await fetchEdgeNeighborhood(INSTANCE, FOCUS, { cap: 60 });

    expect(result.edges).toEqual([{ ...FOCUS, edgePropertyId: null }]);
    expect(getEdgeMock).not.toHaveBeenCalled();
  });

  it("caps the bundle (focus edge counts) and reports the truncation", async () => {
    adjacency({
      1: { out: { knows: [10, 11, 13, 14] } },
      2: { in: { knows: [10, 11, 13, 14] } },
    });
    edgeStore(edge(11, 1, 2), edge(13, 1, 2), edge(14, 1, 2));
    getVertexMock.mockImplementation(async (_i, id) => vertex(id));

    const result = await fetchEdgeNeighborhood(INSTANCE, FOCUS, { cap: 2 });

    expect(result.edges.map((e) => e.id)).toEqual([10, 11]);
    expect(result.truncated).toBe(true);
  });

  it("handles a self-loop bundle without duplicates", async () => {
    const loop = edge(10, 1, 1, "self");
    adjacency({ 1: { out: { self: [10, 11] }, in: { self: [10, 11] } } });
    edgeStore(edge(11, 1, 1));
    getVertexMock.mockImplementation(async (_i, id) => vertex(id));

    const result = await fetchEdgeNeighborhood(INSTANCE, loop, { cap: 60 });

    expect(result.edges.map((e) => e.id)).toEqual([10, 11]);
  });

  it("omits endpoint vertices that fail to hydrate (preview shows stubs)", async () => {
    adjacency({ 1: {}, 2: {} });
    getVertexMock.mockRejectedValue(new Error("gone"));

    const result = await fetchEdgeNeighborhood(INSTANCE, FOCUS, { cap: 60 });

    expect(result.vertices).toEqual([]);
    expect(result.edges).toHaveLength(1);
  });
});

describe("buildCanvasModel", () => {
  it("converts elements, stubs unhydrated endpoints, falls edge labels back to the property id", () => {
    const model = buildCanvasModel(
      [
        {
          ...vertex(1, "person"),
          properties: [{ propertyId: "name", propertyValue: "Ada" }],
        },
      ],
      [{ ...edge(10, 1, 2), edgePropertyId: "knows" }],
    );

    expect(model.nodes[1]).toEqual({ id: 1, label: "person", props: { name: "Ada" } });
    expect(model.nodes[2]).toEqual({ id: 2, label: null });
    expect(model.edges[10]).toMatchObject({
      id: 10,
      source: 1,
      target: 2,
      edgePropertyId: "knows",
      label: "knows",
    });
  });

  it("merges over a base without mutating it", () => {
    const base = buildCanvasModel([vertex(1, "old")], []);
    const merged = buildCanvasModel([vertex(2, "new")], [], base);

    expect(Object.keys(merged.nodes).map(Number).sort()).toEqual([1, 2]);
    expect(base.nodes[2]).toBeUndefined();
  });
});
