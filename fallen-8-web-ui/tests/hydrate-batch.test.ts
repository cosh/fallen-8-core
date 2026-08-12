// MIT License
//
// hydrate-batch.test.ts
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
import type {
  EdgeREST,
  GraphElementBatchREST,
  GraphElementProjectionREST,
  VertexREST,
} from "../src/api/types";

/**
 * Hydration reads a whole page of ids in ONE request (POST /graphelements/get) instead of one
 * request per element. The subtlety worth pinning: that route omits adjacency by design, so a
 * vertex it returns is complete while an EDGE is missing its endpoints - and the canvas cannot draw
 * an edge without them. So edges, and only edges, are still read one by one.
 */

const getGraphElementsMock =
  vi.fn<(i: InstanceConfig, ids: number[], s?: AbortSignal) => Promise<GraphElementBatchREST>>();
const getGraphElementMock =
  vi.fn<(i: InstanceConfig, id: number, s?: AbortSignal) => Promise<VertexREST | EdgeREST>>();

vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    getGraphElements: (i: InstanceConfig, ids: number[], s?: AbortSignal) => getGraphElementsMock(i, ids, s),
    getGraphElement: (i: InstanceConfig, id: number, s?: AbortSignal) => getGraphElementMock(i, id, s),
  };
});

const { hydrateElements, isEdge } = await import("../src/lib/hydrate");

const instance = { id: "i/default", name: "i", baseUrl: "http://localhost:8080" } as InstanceConfig;

const vertex = (id: number): GraphElementProjectionREST => ({
  id,
  kind: "vertex",
  creationDate: "2026-01-01T00:00:00Z",
  modificationDate: "2026-01-01T00:00:00Z",
  label: "person",
  properties: [],
});

// What the batch route returns for an edge: no endpoints, which is the whole point.
const batchEdge = (id: number): GraphElementProjectionREST => ({
  ...vertex(id),
  kind: "edge",
  label: "knows",
});

// The singular route returns a full element; a vertex from it is a VertexREST proper.
const fullVertex = (id: number): VertexREST => ({ ...vertex(id), kind: "vertex" });

const fullEdge = (id: number): EdgeREST => ({
  ...vertex(id),
  kind: "edge",
  label: "knows",
  sourceVertex: 1,
  targetVertex: 2,
  edgePropertyId: "knows",
});

beforeEach(() => {
  getGraphElementsMock.mockReset();
  getGraphElementMock.mockReset();
});

describe("hydrateElements", () => {
  it("hydrates an all-vertex page with ONE request and no per-element reads", async () => {
    getGraphElementsMock.mockResolvedValue({ elements: [vertex(0), vertex(1), vertex(2)], notFound: [] });

    const { elements, capped } = await hydrateElements(instance, [0, 1, 2]);

    expect(getGraphElementsMock).toHaveBeenCalledTimes(1);
    expect(getGraphElementMock).not.toHaveBeenCalled();
    expect(elements.map((e) => e.id)).toEqual([0, 1, 2]);
    expect(capped).toBe(false);
  });

  it("re-reads an EDGE singly, because the batch route omits its endpoints", async () => {
    getGraphElementsMock.mockResolvedValue({ elements: [vertex(0), batchEdge(7)], notFound: [] });
    getGraphElementMock.mockResolvedValue(fullEdge(7));

    const { elements } = await hydrateElements(instance, [0, 7]);

    expect(getGraphElementMock).toHaveBeenCalledTimes(1);
    expect(getGraphElementMock).toHaveBeenCalledWith(instance, 7, undefined);

    const edge = elements.find((e) => e.id === 7);
    expect(edge).toBeDefined();
    expect(isEdge(edge!)).toBe(true);
    // The endpoints are the whole reason for the second read: without them the canvas cannot draw it.
    expect((edge as EdgeREST).sourceVertex).toBe(1);
    expect((edge as EdgeREST).targetVertex).toBe(2);
  });

  it("falls back to per-element reads when the batch route is unavailable", async () => {
    // An older server answers 404 for the batch route; hydration must still work rather than
    // returning nothing.
    getGraphElementsMock.mockRejectedValue(new Error("404"));
    getGraphElementMock.mockImplementation((_i, id) => Promise.resolve(fullVertex(id)));

    const { elements } = await hydrateElements(instance, [4, 5]);

    expect(getGraphElementMock).toHaveBeenCalledTimes(2);
    expect(elements.map((e) => e.id)).toEqual([4, 5]);
  });

  it("skips ids the server reports as gone", async () => {
    getGraphElementsMock.mockResolvedValue({ elements: [vertex(1)], notFound: [2] });

    const { elements } = await hydrateElements(instance, [1, 2]);

    expect(elements.map((e) => e.id)).toEqual([1]);
    expect(getGraphElementMock).not.toHaveBeenCalled();
  });

  it("reports the cap and never asks for more than it", async () => {
    getGraphElementsMock.mockResolvedValue({ elements: [vertex(1)], notFound: [] });

    const { capped } = await hydrateElements(instance, [1, 2, 3], { cap: 1 });

    expect(capped).toBe(true);
    expect(getGraphElementsMock).toHaveBeenCalledWith(instance, [1], undefined);
  });

  it("asks for nothing at all when there are no ids", async () => {
    const { elements } = await hydrateElements(instance, []);

    expect(elements).toEqual([]);
    expect(getGraphElementsMock).not.toHaveBeenCalled();
    expect(getGraphElementMock).not.toHaveBeenCalled();
  });
});
