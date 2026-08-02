// MIT License
//
// live-feed.test.ts
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

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createElement, type ReactNode } from "react";
import { renderHook } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { createLiveFeedHandlers, useLiveChangeFeed } from "../src/state/liveFeed";
import {
  getInstanceStore,
  resetInstanceStoresForTests,
} from "../src/state/instanceStore";
import { getEventFeed, resetEventFeedsForTests } from "../src/state/eventFeed";
import type { ChangeEvent, StreamChangesOptions } from "../src/api/changefeed";
import type { InstanceConfig } from "../src/instances/types";
import type { VertexREST } from "../src/api/types";

// The hook tests pin WHAT the stream is opened with (the since handoff); the stream
// loop itself is covered in changefeed.test.ts, so it is the one thing mocked here.
const streamChangesMock = vi.fn(
  (_instance: InstanceConfig, _options: StreamChangesOptions): Promise<void> =>
    new Promise(() => {}),
);
vi.mock("../src/api/changefeed", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/changefeed")>();
  return {
    ...original,
    streamChanges: (instance: InstanceConfig, options: StreamChangesOptions) =>
      streamChangesMock(instance, options),
  };
});

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
    resetEventFeedsForTests();
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
            edgePropertyId: "knows",
            label: "friendship",
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
    // The fetched DTO's type and label both land on the canvas edge, untangled.
    expect(store.getState().canvasEdges[10]).toMatchObject({
      source: 1,
      target: 2,
      edgePropertyId: "knows",
      label: "friendship",
    });
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

  it("tees element events into the namespace's event feed with interest accounting", () => {
    const { handlers } = makeHandlers();
    const feed = getEventFeed(instance.id);

    handlers.onEvent(event({ kind: "vertexCreated", element: "vertex", id: 1, label: "person" }));
    expect(feed.getState().entries).toHaveLength(1);
    expect(feed.getState().unread).toBe(1);

    // Narrow the persisted interest filter to edges: the next vertex event still
    // BUFFERS (the ring stores raw) but does not count as unread.
    getInstanceStore(instance.id).getState().setFeedFilter({ elements: ["edge"] });
    handlers.onEvent(event({ kind: "vertexCreated", element: "vertex", id: 2 }));
    expect(feed.getState().entries).toHaveLength(2);
    expect(feed.getState().unread).toBe(1);
    handlers.dispose();
  });

  it("tees resyncs as buffered gap markers that flag the bell instead of counting", () => {
    const { handlers } = makeHandlers();
    const feed = getEventFeed(instance.id);

    handlers.onResync(event({ kind: "resync", reason: "overflow" }));

    expect(feed.getState().entries[0].event.kind).toBe("resync");
    expect(feed.getState().unread).toBe(0);
    expect(feed.getState().resyncSinceOpen).toBe(true);
    handlers.dispose();
  });
});

describe("useLiveChangeFeed catch-up handoff", () => {
  beforeEach(() => {
    resetInstanceStoresForTests();
    resetEventFeedsForTests();
    streamChangesMock.mockClear();
    window.localStorage.clear();
  });

  function renderFeedHook() {
    const client = new QueryClient();
    const wrapper = ({ children }: { children: ReactNode }) =>
      createElement(QueryClientProvider, { client }, children);
    return renderHook(() => useLiveChangeFeed(instance), { wrapper });
  }

  it("the first subscribe of a session starts live (no since)", async () => {
    const { unmount } = renderFeedHook();
    await vi.waitFor(() => expect(streamChangesMock).toHaveBeenCalledTimes(1));
    expect(streamChangesMock.mock.calls[0][1].since).toBeUndefined();
    unmount();
  });

  it("records every frame id and resubscribes from it (catch-up via since)", async () => {
    const first = renderFeedHook();
    await vi.waitFor(() => expect(streamChangesMock).toHaveBeenCalledTimes(1));

    // The stream reports positions as frames arrive; the feed keeps the newest.
    streamChangesMock.mock.calls[0][1].onFrameId?.("0b1e:41");
    streamChangesMock.mock.calls[0][1].onFrameId?.("0b1e:42");
    expect(getEventFeed(instance.id).getState().lastEventId).toBe("0b1e:42");
    first.unmount();

    // Leaving and returning (namespace switch and back): the next stream resumes
    // from the stored position so the server ring replays what was missed.
    const second = renderFeedHook();
    await vi.waitFor(() => expect(streamChangesMock).toHaveBeenCalledTimes(2));
    expect(streamChangesMock.mock.calls[1][1].since).toBe("0b1e:42");
    second.unmount();
  });
});
