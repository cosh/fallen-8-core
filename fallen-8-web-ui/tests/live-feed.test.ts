import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { QueryClient } from "@tanstack/react-query";
import { createLiveFeedHandlers } from "../src/state/liveFeed";
import {
  getInstanceStore,
  resetInstanceStoresForTests,
} from "../src/state/instanceStore";
import type { ChangeEvent } from "../src/api/changefeed";
import type { InstanceConfig } from "../src/instances/types";
import type { VertexREST } from "../src/api/types";

/**
 * Live mode semantics (feature change-feed, spec §3.7): feed events become targeted
 * react-query invalidations (debounced) plus the direct canvas minimum (drop removed
 * elements, merge a created edge between on-screen vertices). Resync handling is
 * mandatory: ANY resync re-fetches the instance's visible state; trim/tabulaRasa/load
 * additionally invalidate every held element id (the canvas state is cleared).
 */

const instance: InstanceConfig = {
  id: "live-a",
  name: "live-a",
  baseUrl: "http://f8.test",
  auth: { kind: "none" },
};

const vertex = (id: number, label = "person"): VertexREST => ({
  id,
  creationDate: "",
  modificationDate: "",
  label,
});

const event = (partial: Partial<ChangeEvent> & Pick<ChangeEvent, "kind">): ChangeEvent => ({
  seq: 1,
  ts: "2026-07-15T12:00:00.000Z",
  ...partial,
});

function makeHandlers(debounceMs = 0) {
  const queryClient = new QueryClient();
  const invalidated: unknown[][] = [];
  vi.spyOn(queryClient, "invalidateQueries").mockImplementation(async (filters) => {
    invalidated.push((filters as { queryKey: unknown[] }).queryKey);
  });
  const handlers = createLiveFeedHandlers({ instance, queryClient, debounceMs });
  return { handlers, invalidated, queryClient };
}

const flushDebounce = () => new Promise((resolve) => setTimeout(resolve, 5));

describe("live feed handlers", () => {
  beforeEach(() => {
    resetInstanceStoresForTests();
    window.localStorage.clear();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it("any resync re-fetches the instance's visible state (all its queries)", async () => {
    const { handlers, invalidated } = makeHandlers();
    const store = getInstanceStore(instance.id);
    store.getState().mergeIntoCanvas([vertex(1)], []);

    handlers.onResync(event({ kind: "resync", reason: "overflow" }));

    expect(invalidated).toEqual([[instance.id]]);
    // overflow/seekOutOfRange/delegateWrite do NOT invalidate held ids - the elements
    // still exist, only continuity was lost.
    expect(Object.keys(store.getState().canvasNodes)).toHaveLength(1);
  });

  it.each(["trim", "tabulaRasa", "load"] as const)(
    "resync(%s) additionally treats held element ids as invalid: the canvas is cleared",
    async (reason) => {
      const { handlers, invalidated } = makeHandlers();
      const store = getInstanceStore(instance.id);
      store.getState().mergeIntoCanvas(
        [vertex(1), vertex(2)],
        [
          {
            id: 10,
            creationDate: "",
            modificationDate: "",
            sourceVertex: 1,
            targetVertex: 2,
            edgePropertyId: "knows",
            label: null,
          },
        ],
      );

      handlers.onResync(event({ kind: "resync", reason }));

      expect(Object.keys(store.getState().canvasNodes)).toHaveLength(0);
      expect(Object.keys(store.getState().canvasEdges)).toHaveLength(0);
      expect(invalidated).toEqual([[instance.id]]);
    },
  );

  it("a resync flushes pending debounced invalidations in favour of the full re-fetch", async () => {
    const { handlers, invalidated } = makeHandlers(60_000); // debounce would never fire
    handlers.onEvent(event({ kind: "vertexCreated", element: "vertex", id: 7 }));
    handlers.onResync(event({ kind: "resync", reason: "overflow" }));

    expect(invalidated).toEqual([[instance.id]]); // only the instance-wide invalidation
    handlers.dispose();
  });

  it("vertexRemoved drops the vertex (and incident edges) from the canvas", () => {
    const { handlers } = makeHandlers();
    const store = getInstanceStore(instance.id);
    store.getState().mergeIntoCanvas(
      [vertex(1), vertex(2)],
      [
        {
          id: 10,
          creationDate: "",
          modificationDate: "",
          sourceVertex: 1,
          targetVertex: 2,
          edgePropertyId: "knows",
          label: null,
        },
      ],
    );

    handlers.onEvent(event({ kind: "vertexRemoved", element: "vertex", id: 1 }));

    expect(store.getState().canvasNodes[1]).toBeUndefined();
    expect(store.getState().canvasNodes[2]).toBeDefined();
    expect(Object.keys(store.getState().canvasEdges)).toHaveLength(0);
    handlers.dispose();
  });

  it("edgeRemoved drops the edge from the canvas", () => {
    const { handlers } = makeHandlers();
    const store = getInstanceStore(instance.id);
    store.getState().mergeIntoCanvas(
      [vertex(1), vertex(2)],
      [
        {
          id: 10,
          creationDate: "",
          modificationDate: "",
          sourceVertex: 1,
          targetVertex: 2,
          edgePropertyId: "knows",
          label: null,
        },
      ],
    );

    handlers.onEvent(event({ kind: "edgeRemoved", element: "edge", id: 10 }));

    expect(Object.keys(store.getState().canvasEdges)).toHaveLength(0);
    expect(Object.keys(store.getState().canvasNodes)).toHaveLength(2);
    handlers.dispose();
  });

  it("edgeCreated between two on-screen vertices fetches the edge and merges it", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () =>
        new Response(
          JSON.stringify({
            id: 10,
            creationDate: "",
            modificationDate: "",
            sourceVertex: 1,
            targetVertex: 2,
            label: "knows",
          }),
          { status: 200 },
        ),
      ),
    );
    const { handlers } = makeHandlers();
    const store = getInstanceStore(instance.id);
    store.getState().mergeIntoCanvas([vertex(1), vertex(2)], []);

    handlers.onEvent(
      event({ kind: "edgeCreated", element: "edge", id: 10, source: 1, target: 2 }),
    );

    await vi.waitFor(() => expect(store.getState().canvasEdges[10]).toBeDefined());
    expect(store.getState().canvasEdges[10]).toMatchObject({ source: 1, target: 2 });
    handlers.dispose();
  });

  it("edgeCreated with an off-screen endpoint does not fetch anything", async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    const { handlers } = makeHandlers();
    const store = getInstanceStore(instance.id);
    store.getState().mergeIntoCanvas([vertex(1)], []); // vertex 2 is NOT on screen

    handlers.onEvent(
      event({ kind: "edgeCreated", element: "edge", id: 10, source: 1, target: 2 }),
    );
    await flushDebounce();

    expect(fetchMock).not.toHaveBeenCalled();
    expect(store.getState().canvasEdges[10]).toBeUndefined();
    handlers.dispose();
  });

  it("element events invalidate the status counters and bulk graph (debounced, deduplicated)", async () => {
    const { handlers, invalidated } = makeHandlers(1);

    // A burst - e.g. a CreateVerticesTransaction with three vertices.
    handlers.onEvent(event({ kind: "vertexCreated", element: "vertex", id: 1 }));
    handlers.onEvent(event({ kind: "vertexCreated", element: "vertex", id: 2 }));
    handlers.onEvent(event({ kind: "vertexCreated", element: "vertex", id: 3 }));
    await flushDebounce();

    // One invalidation per key, not one per event.
    expect(invalidated).toContainEqual([instance.id, "status"]);
    expect(invalidated).toContainEqual([instance.id, "graph"]);
    expect(invalidated.filter((k) => k[1] === "status")).toHaveLength(1);
    expect(invalidated.filter((k) => k[1] === "graph")).toHaveLength(1);
    handlers.dispose();
  });

  it("property events re-fetch the displayed element's detail, not the bulk graph", async () => {
    const { handlers, invalidated } = makeHandlers(1);

    handlers.onEvent(
      event({ kind: "propertySet", element: "vertex", id: 42, key: "name" }),
    );
    handlers.onEvent(
      event({ kind: "propertyRemoved", element: "edge", id: 10, key: "since" }),
    );
    await flushDebounce();

    expect(invalidated).toContainEqual([instance.id, "element", "node", 42]);
    expect(invalidated).toContainEqual([instance.id, "vertex", 42]); // adjacency panel keys
    expect(invalidated).toContainEqual([instance.id, "element", "edge", 10]);
    expect(invalidated).toContainEqual([instance.id, "status"]);
    expect(invalidated).not.toContainEqual([instance.id, "graph"]);
    handlers.dispose();
  });

  it("dispose cancels a pending debounced flush", async () => {
    const { handlers, invalidated } = makeHandlers(1);
    handlers.onEvent(event({ kind: "vertexCreated", element: "vertex", id: 1 }));
    handlers.dispose();
    await flushDebounce();
    expect(invalidated).toEqual([]);
  });
});
