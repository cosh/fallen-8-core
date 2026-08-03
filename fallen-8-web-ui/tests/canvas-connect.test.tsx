// MIT License
//
// canvas-connect.test.tsx
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
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { InstanceConfig } from "../src/instances/types";
import type { EdgeREST, PathElementREST, PathREST, VertexREST } from "../src/api/types";

/**
 * Canvas "Connect" tab (feature canvas-find-connect): pairwise BLS over the canvas vertices with
 * the pair cap, cancel, and the reference-counted add/remove that keeps a shared intermediate
 * alive until its last claiming path is retracted.
 */

const findPathsMock =
  vi.fn<
    (i: InstanceConfig, a: number, b: number, spec: unknown, signal?: AbortSignal) => Promise<PathREST[] | null>
  >();
const getGraphElementMock =
  vi.fn<(i: InstanceConfig, id: number, s?: AbortSignal) => Promise<VertexREST | EdgeREST | null>>();

vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    findPaths: (i: InstanceConfig, a: number, b: number, spec: unknown, signal?: AbortSignal) =>
      findPathsMock(i, a, b, spec, signal),
    getGraphElement: (i: InstanceConfig, id: number, s?: AbortSignal) => getGraphElementMock(i, id, s),
  };
});

vi.mock("../src/canvas/GraphCanvas", () => ({
  GraphCanvas: () => <div data-testid="mock-canvas" />,
}));

import { CanvasScreen } from "../src/screens/CanvasScreen";
import { getInstanceStore, resetInstanceStoresForTests } from "../src/state/instanceStore";
import { SAME_ORIGIN_INSTANCE } from "../src/instances/registry";

function vertex(id: number, label: string | null = "person"): VertexREST {
  return { id, creationDate: "", modificationDate: "", label, kind: "vertex", properties: [] };
}

function el(source: number, target: number, edgeId: number): PathElementREST {
  return { sourceVertexId: source, targetVertexId: target, edgeId, edgePropertyId: "knows", weight: 0 };
}

function path(...elements: PathElementREST[]): PathREST {
  return { pathElements: elements, totalWeight: 0 };
}

const store = () => getInstanceStore(SAME_ORIGIN_INSTANCE.id);

function seed(...ids: number[]) {
  store().getState().mergeIntoCanvas(ids.map((id) => vertex(id)), []);
}

function renderScreen() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <CanvasScreen />
    </QueryClientProvider>,
  );
}

async function openConnect(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByTestId("canvas-tab-connect"));
}

beforeEach(() => {
  resetInstanceStoresForTests();
  localStorage.clear();
  findPathsMock.mockReset().mockResolvedValue([]);
  getGraphElementMock.mockReset().mockImplementation((_i, id) => Promise.resolve(vertex(id)));
});

describe("pair gating", () => {
  it("disables the run below two endpoints", async () => {
    const user = userEvent.setup();
    seed(1);
    renderScreen();
    await openConnect(user);
    expect(screen.getByTestId("connect-pair-count")).toHaveTextContent("1 vertices → 0 pairs");
    expect(screen.getByTestId("connect-run")).toBeDisabled();
  });

  it("refuses to run over the pair cap and offers pick mode to narrow", async () => {
    const user = userEvent.setup();
    seed(...Array.from({ length: 33 }, (_v, i) => i + 1)); // 33 -> 528 pairs > 500
    renderScreen();
    await openConnect(user);
    expect(screen.getByTestId("connect-over-cap")).toBeInTheDocument();
    expect(screen.getByTestId("connect-run")).toBeDisabled();

    await user.click(screen.getByTestId("connect-scope-pick"));
    await user.click(screen.getByTestId("connect-pick-1"));
    await user.click(screen.getByTestId("connect-pick-2"));
    await user.click(screen.getByTestId("connect-pick-3"));
    expect(screen.getByTestId("connect-pair-count")).toHaveTextContent("3 vertices → 3 pairs");
    expect(screen.queryByTestId("connect-over-cap")).not.toBeInTheDocument();
    expect(screen.getByTestId("connect-run")).toBeEnabled();
  });

  it("filters the pick list by id or label without losing the checkbox state", async () => {
    const user = userEvent.setup();
    store().getState().mergeIntoCanvas([vertex(1, "alice"), vertex(2, "bob"), vertex(3, "carol")], []);
    renderScreen();
    await openConnect(user);
    await user.click(screen.getByTestId("connect-scope-pick"));
    await user.type(screen.getByTestId("connect-pick-filter"), "bob");
    expect(screen.getByTestId("connect-pick-2")).toBeInTheDocument();
    expect(screen.queryByTestId("connect-pick-1")).not.toBeInTheDocument();
    expect(screen.queryByTestId("connect-pick-3")).not.toBeInTheDocument();
  });

  it("drops a picked endpoint that has left the canvas before the run", async () => {
    const user = userEvent.setup();
    seed(1, 2, 3);
    renderScreen();
    await openConnect(user);
    await user.click(screen.getByTestId("connect-scope-pick"));
    await user.click(screen.getByTestId("connect-pick-1"));
    await user.click(screen.getByTestId("connect-pick-2"));
    await user.click(screen.getByTestId("connect-pick-3"));
    expect(screen.getByTestId("connect-pair-count")).toHaveTextContent("3 vertices → 3 pairs");

    // Vertex 3 leaves the canvas after being picked; `picked` still holds it, but the run must
    // use only vertices actually on the canvas.
    await act(async () => {
      store().getState().removeFromCanvas("node", 3);
    });
    expect(screen.getByTestId("connect-pair-count")).toHaveTextContent("2 vertices → 1 pair");

    findPathsMock.mockResolvedValue([path(el(1, 2, 100))]);
    await user.click(screen.getByTestId("connect-run"));
    await waitFor(() => expect(screen.getByTestId("connect-summary")).toBeInTheDocument());
    expect(findPathsMock).toHaveBeenCalledTimes(1);
    for (const call of findPathsMock.mock.calls) {
      expect(call[1]).not.toBe(3);
      expect(call[2]).not.toBe(3);
    }
  });
});

describe("running the sweep", () => {
  it("issues one lean BLS query per unordered pair and tallies found vs unreachable", async () => {
    const user = userEvent.setup();
    seed(1, 2, 3);
    findPathsMock.mockImplementation((_i, a, b) => {
      if (a === 1 && b === 2) return Promise.resolve([path(el(1, 2, 100))]);
      if (a === 2 && b === 3) return Promise.resolve([path(el(2, 4, 101), el(4, 3, 102))]);
      return Promise.resolve([]); // (1,3) unreachable
    });
    renderScreen();
    await openConnect(user);
    await user.click(screen.getByTestId("connect-run"));

    await waitFor(() => expect(screen.getByTestId("connect-summary")).toBeInTheDocument());
    expect(findPathsMock).toHaveBeenCalledTimes(3);
    // The spec is the lean, fixed one; the signal rides in the 5th argument.
    for (const call of findPathsMock.mock.calls) {
      expect(call[3]).toMatchObject({ pathAlgorithmName: "BLS", maxDepth: 3, maxResults: 1 });
    }
    expect(screen.getByTestId("connect-summary")).toHaveTextContent("2 connections found");
    expect(screen.getByTestId("connect-summary")).toHaveTextContent("1 unreachable within 3 hops");
    expect(screen.getByTestId("connect-row-1-2")).toBeInTheDocument();
    expect(screen.getByTestId("connect-row-2-3")).toBeInTheDocument();
    expect(screen.queryByTestId("connect-row-1-3")).not.toBeInTheDocument();
  });

  it("counts a failed pair search without aborting the rest of the run", async () => {
    const user = userEvent.setup();
    seed(1, 2, 3);
    findPathsMock.mockImplementation((_i, a, b) => {
      if (a === 1 && b === 3) return Promise.reject(new Error("boom"));
      return Promise.resolve([path(el(a, b, 100 + a + b))]);
    });
    renderScreen();
    await openConnect(user);
    await user.click(screen.getByTestId("connect-run"));

    await waitFor(() => expect(screen.getByTestId("connect-summary")).toBeInTheDocument());
    expect(screen.getByTestId("connect-summary")).toHaveTextContent("2 connections found");
    expect(screen.getByTestId("connect-summary")).toHaveTextContent("1 failed");
  });

  it("reports the hop bound the run used, not a value edited afterwards", async () => {
    const user = userEvent.setup();
    seed(1, 2);
    findPathsMock.mockResolvedValue([]); // both unreachable within the run's bound
    renderScreen();
    await openConnect(user);
    await user.click(screen.getByTestId("connect-run"));
    await waitFor(() => expect(screen.getByTestId("connect-summary")).toHaveTextContent("within 3 hops"));

    // Editing max-hops after the run must not rewrite the already-shown summary.
    fireEvent.change(screen.getByTestId("connect-max-hops"), { target: { value: "6" } });
    expect(screen.getByTestId("connect-summary")).toHaveTextContent("within 3 hops");
    expect(screen.getByTestId("connect-summary")).not.toHaveTextContent("within 6 hops");
  });
});

describe("selective add / remove", () => {
  it("adds only the introduced elements and never clobbers a baseline vertex", async () => {
    const user = userEvent.setup();
    store().getState().mergeIntoCanvas([vertex(1, "person"), vertex(2, "person")], []);
    findPathsMock.mockResolvedValue([path(el(1, 3, 100), el(3, 2, 101))]);
    getGraphElementMock.mockImplementation((_i, id) =>
      Promise.resolve(vertex(id, id === 3 ? "middle" : "person")),
    );
    renderScreen();
    await openConnect(user);
    await user.click(screen.getByTestId("connect-run"));
    await waitFor(() => expect(screen.getByTestId("connect-row-1-2")).toBeInTheDocument());
    // 1 intermediate vertex + 2 hop edges = 3 new elements over the baseline.
    expect(screen.getByTestId("connect-row-1-2")).toHaveTextContent("2 hops, 3 new");

    await user.click(screen.getByTestId("connect-toggle-1-2"));
    await waitFor(() => expect(store().getState().canvasNodes[3]).toBeDefined());
    const state = store().getState();
    expect(state.canvasNodes[3].label).toBe("middle"); // hydrated, not a stub
    expect(state.canvasEdges[100]).toBeDefined();
    expect(state.canvasEdges[101]).toBeDefined();
    // The endpoints were already on the canvas and keep their real label.
    expect(state.canvasNodes[1].label).toBe("person");
    expect(state.canvasNodes[2].label).toBe("person");
  });

  it("a direct-edge path introduces only the edge", async () => {
    const user = userEvent.setup();
    store().getState().mergeIntoCanvas([vertex(1), vertex(2)], []);
    findPathsMock.mockResolvedValue([path(el(1, 2, 200))]);
    renderScreen();
    await openConnect(user);
    await user.click(screen.getByTestId("connect-run"));
    await waitFor(() => expect(screen.getByTestId("connect-row-1-2")).toHaveTextContent("1 hop, 1 new"));

    await user.click(screen.getByTestId("connect-toggle-1-2"));
    await waitFor(() => expect(store().getState().canvasEdges[200]).toBeDefined());
    // No intermediate node was added: the canvas still holds exactly the two endpoints.
    expect(Object.keys(store().getState().canvasNodes).sort()).toEqual(["1", "2"]);
  });

  it("keeps a shared intermediate until the last claiming path is removed", async () => {
    const user = userEvent.setup();
    store().getState().mergeIntoCanvas([vertex(1), vertex(2), vertex(3)], []);
    // (1,2) and (1,3) both route through vertex 9 via edge 100 (shared); each has its own tail.
    findPathsMock.mockImplementation((_i, a, b) => {
      if (a === 1 && b === 2) return Promise.resolve([path(el(1, 9, 100), el(9, 2, 101))]);
      if (a === 1 && b === 3) return Promise.resolve([path(el(1, 9, 100), el(9, 3, 102))]);
      return Promise.resolve([]);
    });
    getGraphElementMock.mockImplementation((_i, id) => Promise.resolve(vertex(id)));
    renderScreen();
    await openConnect(user);
    await user.click(screen.getByTestId("connect-run"));
    await waitFor(() => expect(screen.getByTestId("connect-row-1-2")).toBeInTheDocument());

    await user.click(screen.getByTestId("connect-toggle-1-2"));
    await waitFor(() => expect(store().getState().canvasNodes[9]).toBeDefined());
    await user.click(screen.getByTestId("connect-toggle-1-3"));
    await waitFor(() => expect(store().getState().canvasEdges[102]).toBeDefined());

    // Remove 1-2: vertex 9 and shared edge 100 are still claimed by 1-3, only tail edge 101 leaves.
    await user.click(screen.getByTestId("connect-toggle-1-2"));
    await waitFor(() => expect(store().getState().canvasEdges[101]).toBeUndefined());
    let state = store().getState();
    expect(state.canvasNodes[9]).toBeDefined();
    expect(state.canvasEdges[100]).toBeDefined();

    // Remove 1-3: nothing else claims the shared elements now, so they all leave.
    await user.click(screen.getByTestId("connect-toggle-1-3"));
    await waitFor(() => expect(store().getState().canvasNodes[9]).toBeUndefined());
    state = store().getState();
    expect(state.canvasEdges[100]).toBeUndefined();
    expect(state.canvasEdges[102]).toBeUndefined();
  });

  it("re-adding a removed path restores it", async () => {
    const user = userEvent.setup();
    store().getState().mergeIntoCanvas([vertex(1), vertex(2)], []);
    findPathsMock.mockResolvedValue([path(el(1, 3, 100), el(3, 2, 101))]);
    renderScreen();
    await openConnect(user);
    await user.click(screen.getByTestId("connect-run"));
    await waitFor(() => expect(screen.getByTestId("connect-row-1-2")).toBeInTheDocument());

    await user.click(screen.getByTestId("connect-toggle-1-2")); // add
    await waitFor(() => expect(store().getState().canvasNodes[3]).toBeDefined());
    await user.click(screen.getByTestId("connect-toggle-1-2")); // remove
    await waitFor(() => expect(store().getState().canvasNodes[3]).toBeUndefined());
    await user.click(screen.getByTestId("connect-toggle-1-2")); // re-add
    await waitFor(() => expect(store().getState().canvasNodes[3]).toBeDefined());
  });

  it("adds every found connection at once and disables Add all once all are added", async () => {
    const user = userEvent.setup();
    store().getState().mergeIntoCanvas([vertex(1), vertex(2), vertex(3)], []);
    findPathsMock.mockImplementation((_i, a, b) => {
      if (a === 1 && b === 2) return Promise.resolve([path(el(1, 2, 100))]);
      if (a === 1 && b === 3) return Promise.resolve([path(el(1, 4, 101), el(4, 3, 102))]);
      return Promise.resolve([path(el(2, 3, 103))]);
    });
    getGraphElementMock.mockImplementation((_i, id) => Promise.resolve(vertex(id)));
    renderScreen();
    await openConnect(user);
    await user.click(screen.getByTestId("connect-run"));
    await waitFor(() => expect(screen.getByTestId("connect-add-all")).toBeEnabled());

    await user.click(screen.getByTestId("connect-add-all"));
    await waitFor(() => expect(store().getState().canvasNodes[4]).toBeDefined());
    const state = store().getState();
    for (const id of [100, 101, 102, 103]) expect(state.canvasEdges[id]).toBeDefined();
    // Everything is added, so the bulk action has nothing left to do.
    await waitFor(() => expect(screen.getByTestId("connect-add-all")).toBeDisabled());
  });

  it("keeps a shared intermediate when the retracted path is not the last claimant, unaffected by an external merge", async () => {
    // Regression guard for the removeFromCanvas("node") cascade: an external merge (Show whole
    // graph / Expand) between the run and a retract must not let removing a connection drop a
    // now-first-class vertex and the edges the merge attached to it.
    const user = userEvent.setup();
    store().getState().mergeIntoCanvas([vertex(1), vertex(2)], []);
    findPathsMock.mockResolvedValue([path(el(1, 9, 100), el(9, 2, 101))]);
    getGraphElementMock.mockImplementation((_i, id) => Promise.resolve(vertex(id)));
    renderScreen();
    await openConnect(user);
    await user.click(screen.getByTestId("connect-run"));
    await waitFor(() => expect(screen.getByTestId("connect-row-1-2")).toBeInTheDocument());
    await user.click(screen.getByTestId("connect-toggle-1-2")); // add: 9,100,101
    await waitFor(() => expect(store().getState().canvasNodes[9]).toBeDefined());

    // An external flow merges an unrelated edge that also touches vertex 9.
    await act(async () => {
      store().getState().mergeIntoCanvas([vertex(9), vertex(42)], [
        { id: 900, creationDate: "", modificationDate: "", sourceVertex: 9, targetVertex: 42, edgePropertyId: "x", label: null },
      ]);
    });

    await user.click(screen.getByTestId("connect-toggle-1-2")); // remove
    await waitFor(() => expect(store().getState().canvasEdges[101]).toBeUndefined());
    const state = store().getState();
    // Vertex 9 is still connected to the externally-merged edge 900, so it (and 900) survive.
    expect(state.canvasNodes[9]).toBeDefined();
    expect(state.canvasEdges[900]).toBeDefined();
  });
});

describe("cancel", () => {
  it("keeps the connections found so far and reports the cancellation", async () => {
    const user = userEvent.setup();
    seed(1, 2, 3);
    findPathsMock.mockImplementation((_i, a, b, _spec, signal) => {
      if (a === 1 && b === 2) return Promise.resolve([path(el(1, 2, 100))]);
      if (a === 1 && b === 3) return Promise.resolve([]);
      // (2,3) never resolves; it only settles when the run is cancelled.
      return new Promise((_resolve, reject) => {
        signal?.addEventListener("abort", () => reject(new Error("aborted")));
      });
    });
    renderScreen();
    await openConnect(user);
    await user.click(screen.getByTestId("connect-run"));

    await waitFor(() => expect(screen.getByTestId("connect-cancel")).toBeInTheDocument());
    await user.click(screen.getByTestId("connect-cancel"));

    await waitFor(() => expect(screen.getByTestId("connect-summary")).toBeInTheDocument());
    expect(screen.getByTestId("connect-summary")).toHaveTextContent("cancelled after");
    expect(screen.getByTestId("connect-summary")).toHaveTextContent("1 connection found");
    // The connection resolved before the cancel is still listed.
    expect(screen.getByTestId("connect-row-1-2")).toBeInTheDocument();
  });
});

describe("draft persistence", () => {
  it("persists max hops and scope but not the picked ids", async () => {
    const user = userEvent.setup();
    seed(1, 2, 3);
    const view = renderScreen();
    await openConnect(user);
    await user.click(screen.getByTestId("connect-scope-pick"));
    await user.click(screen.getByTestId("connect-pick-1"));
    // A number input coerces empty->1, so set it directly rather than clear+type.
    fireEvent.change(screen.getByTestId("connect-max-hops"), { target: { value: "5" } });

    expect(store().getState().canvasToolsDraft.connectMaxDepth).toBe(5);
    expect(store().getState().canvasToolsDraft.connectScope).toBe("pick");

    view.unmount();
    renderScreen();
    // Scope + hops survive; the picked vertex does NOT (ephemeral) - its checkbox is unchecked.
    expect(screen.getByTestId("connect-max-hops")).toHaveValue(5);
    const pick1 = screen.getByTestId("connect-pick-1").querySelector("input")!;
    expect(pick1).not.toBeChecked();
  });
});
