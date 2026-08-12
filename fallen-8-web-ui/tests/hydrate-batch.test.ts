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
 *
 * Also pinned here: scan order, attempt-counted progress, and abort handling (see hydrateElements).
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

const { HYDRATION_BATCH_SIZE, hydrateElements, isEdge } = await import("../src/lib/hydrate");

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

  it("does not report a page as capped when it is exactly the cap", async () => {
    getGraphElementsMock.mockResolvedValue({ elements: [vertex(1), vertex(2)], notFound: [] });

    const { elements, capped } = await hydrateElements(instance, [1, 2], { cap: 2 });

    expect(capped).toBe(false);
    expect(getGraphElementsMock).toHaveBeenCalledWith(instance, [1, 2], undefined);
    expect(elements.map((e) => e.id)).toEqual([1, 2]);
  });

  it("returns a mixed page in the order the caller asked for, not edges last", async () => {
    getGraphElementsMock.mockResolvedValue({
      elements: [vertex(1), batchEdge(2), vertex(3)],
      notFound: [],
    });
    getGraphElementMock.mockImplementation((_i, id) => Promise.resolve(fullEdge(id)));

    const { elements } = await hydrateElements(instance, [1, 2, 3]);

    expect(elements.map((e) => e.id)).toEqual([1, 2, 3]);
  });

  it("counts progress in ATTEMPTS, so a page with a deleted id still reaches its total", async () => {
    getGraphElementsMock.mockResolvedValue({ elements: [vertex(1), vertex(3)], notFound: [2] });
    const seen: { done: number; total: number }[] = [];

    await hydrateElements(instance, [1, 2, 3], { onProgress: (p) => seen.push({ ...p }) });

    expect(seen.at(-1)).toEqual({ done: 3, total: 3 });
    expect(seen.every((p) => p.done <= p.total)).toBe(true);
  });

  it("survives a rejecting single-edge re-read: the vertices stand and progress completes", async () => {
    getGraphElementsMock.mockResolvedValue({ elements: [vertex(1), batchEdge(7)], notFound: [] });
    getGraphElementMock.mockRejectedValue(new Error("500"));
    const seen: { done: number; total: number }[] = [];

    const { elements } = await hydrateElements(instance, [1, 7], {
      onProgress: (p) => seen.push({ ...p }),
    });

    expect(elements.map((e) => e.id)).toEqual([1]);
    expect(seen.at(-1)).toEqual({ done: 2, total: 2 });
  });

  it("issues no request at all when the signal is aborted before it starts", async () => {
    getGraphElementsMock.mockResolvedValue({ elements: [vertex(1)], notFound: [] });
    const controller = new AbortController();
    controller.abort();

    const { elements } = await hydrateElements(instance, [1, 2], { signal: controller.signal });

    expect(elements).toEqual([]);
    expect(getGraphElementsMock).not.toHaveBeenCalled();
    expect(getGraphElementMock).not.toHaveBeenCalled();
  });

  it("reads an aborted batch as an abort, never as a server without the batch route", async () => {
    // The distinction matters: the fallback would fire one doomed request PER ID after the caller
    // already walked away.
    const controller = new AbortController();
    getGraphElementsMock.mockImplementation(() => {
      controller.abort();
      return Promise.reject(new DOMException("Aborted", "AbortError"));
    });
    getGraphElementMock.mockImplementation((_i, id) => Promise.resolve(fullVertex(id)));

    const { elements } = await hydrateElements(instance, [1, 2, 3], { signal: controller.signal });

    expect(elements).toEqual([]);
    expect(getGraphElementMock).not.toHaveBeenCalled();
  });

  it("asks for a repeated id once and returns it once", async () => {
    // Results are keyed by id, so a duplicate that survived into the request list would come back
    // as the same element twice - a duplicate React key on the canvas. No caller passes duplicates
    // today; this pins that a future one cannot break rendering by doing so.
    getGraphElementsMock.mockResolvedValue({ elements: [vertex(4), vertex(9)], notFound: [] });

    const { elements } = await hydrateElements(instance, [4, 9, 4, 9, 4]);

    expect(getGraphElementsMock).toHaveBeenCalledWith(instance, [4, 9], undefined);
    expect(elements.map((e) => e.id)).toEqual([4, 9]);
  });

  it("counts the cap in DISTINCT elements, not in repeats", async () => {
    getGraphElementsMock.mockResolvedValue({ elements: [vertex(1), vertex(2)], notFound: [] });

    const { elements, capped } = await hydrateElements(instance, [1, 2, 1, 2, 1, 2], { cap: 2 });

    expect(capped).toBe(false);
    expect(elements.map((e) => e.id)).toEqual([1, 2]);
  });

  it("stops the single-read rounds at the abort instead of finishing the page", async () => {
    const edgeIds = Array.from({ length: HYDRATION_BATCH_SIZE + 5 }, (_, i) => i + 1);
    getGraphElementsMock.mockResolvedValue({
      elements: edgeIds.map((id) => batchEdge(id)),
      notFound: [],
    });
    const controller = new AbortController();
    getGraphElementMock.mockImplementation((_i, id) => {
      if (id === 1) controller.abort();
      return Promise.resolve(fullEdge(id));
    });

    await hydrateElements(instance, edgeIds, { signal: controller.signal });

    // The round already in flight completes; the next one is never entered.
    expect(getGraphElementMock).toHaveBeenCalledTimes(HYDRATION_BATCH_SIZE);
  });
});
