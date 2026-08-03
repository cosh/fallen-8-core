// MIT License
//
// canvas-find.test.tsx
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
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { InstanceConfig } from "../src/instances/types";
import type { EdgeREST, VertexREST } from "../src/api/types";

/**
 * Canvas "Find" tab (feature canvas-find-connect): the tool strip (Style default, Find, Connect),
 * the all-property search wired to POST /scan/graph/properties, the live on-canvas indicator, the
 * per-row and send-all canvas adds, and the row->Detail selection. The Detail panel is present
 * under every tab and Style renders on mount, so the pre-existing canvas tests stay valid.
 */

const scanPropertiesMock =
  vi.fn<(i: InstanceConfig, spec: { searchTerm: string; label?: string; resultType: string }) => Promise<number[] | null>>();
const getGraphElementMock =
  vi.fn<(i: InstanceConfig, id: number, s?: AbortSignal) => Promise<VertexREST | EdgeREST | null>>();
const getStatisticsMock = vi.fn<(i: InstanceConfig, s?: AbortSignal) => Promise<null>>();

vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    scanProperties: (i: InstanceConfig, spec: { searchTerm: string; label?: string; resultType: string }) =>
      scanPropertiesMock(i, spec),
    getGraphElement: (i: InstanceConfig, id: number, s?: AbortSignal) => getGraphElementMock(i, id, s),
    getStatistics: (i: InstanceConfig, s?: AbortSignal) => getStatisticsMock(i, s),
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

function edge(id: number, source: number, target: number): EdgeREST {
  return {
    id,
    creationDate: "",
    modificationDate: "",
    label: null,
    kind: "edge",
    sourceVertex: source,
    targetVertex: target,
    edgePropertyId: "knows",
    properties: [],
  };
}

const store = () => getInstanceStore(SAME_ORIGIN_INSTANCE.id);

function renderScreen() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <CanvasScreen />
    </QueryClientProvider>,
  );
}

async function openFind(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByTestId("canvas-tab-find"));
}

beforeEach(() => {
  resetInstanceStoresForTests();
  localStorage.clear();
  scanPropertiesMock.mockReset().mockResolvedValue([]);
  getGraphElementMock.mockReset().mockImplementation((_i, id) => Promise.resolve(vertex(id)));
  getStatisticsMock.mockReset().mockResolvedValue(null);
});

describe("tool strip", () => {
  it("shows Style by default, switches to Find, and keeps the Detail panel visible under both", () => {
    renderScreen();
    expect(screen.getByTestId("style-panel")).toBeInTheDocument();
    expect(screen.getByText(/Select a node or edge/)).toBeInTheDocument();
    expect(screen.queryByTestId("find-panel")).not.toBeInTheDocument();
  });

  it("switches to Find and the active tab persists across a remount", async () => {
    const user = userEvent.setup();
    const view = renderScreen();
    await openFind(user);
    expect(screen.getByTestId("find-panel")).toBeInTheDocument();
    expect(screen.queryByTestId("style-panel")).not.toBeInTheDocument();
    // The Detail panel is independent of the active tab.
    expect(screen.getByText(/Select a node or edge/)).toBeInTheDocument();

    view.unmount();
    renderScreen();
    expect(screen.getByTestId("find-panel")).toBeInTheDocument();
  });
});

describe("find search", () => {
  it("disables run on a blank term and sends the all-property scan once a term is typed", async () => {
    const user = userEvent.setup();
    scanPropertiesMock.mockResolvedValue([12, 40]);
    renderScreen();
    await openFind(user);

    expect(screen.getByTestId("find-run")).toBeDisabled();
    await user.type(screen.getByTestId("find-term"), "acme");
    expect(screen.getByTestId("find-run")).toBeEnabled();

    await user.click(screen.getByTestId("find-run"));
    await waitFor(() => expect(screen.getByTestId("find-row-12")).toBeInTheDocument());
    expect(screen.getByTestId("find-row-40")).toBeInTheDocument();
    expect(scanPropertiesMock).toHaveBeenCalledWith(
      expect.anything(),
      expect.objectContaining({ searchTerm: "acme", resultType: "Both" }),
    );
    // A blank label is omitted from the request, never sent as "".
    expect(scanPropertiesMock.mock.calls[0][1].label).toBeUndefined();
  });

  it("passes a non-empty label restrictor through", async () => {
    const user = userEvent.setup();
    scanPropertiesMock.mockResolvedValue([1]);
    renderScreen();
    await openFind(user);
    await user.type(screen.getByTestId("find-term"), "acme");
    await user.type(screen.getByTestId("find-label"), "company");
    await user.click(screen.getByTestId("find-run"));
    await waitFor(() => expect(scanPropertiesMock).toHaveBeenCalled());
    expect(scanPropertiesMock.mock.calls[0][1].label).toBe("company");
  });

  it("marks elements already on the canvas and flips the indicator after a per-row add", async () => {
    const user = userEvent.setup();
    store().getState().mergeIntoCanvas([vertex(40)], []);
    scanPropertiesMock.mockResolvedValue([12, 40]);
    renderScreen();
    await openFind(user);
    await user.type(screen.getByTestId("find-term"), "acme");
    await user.click(screen.getByTestId("find-run"));

    await waitFor(() => expect(screen.getByTestId("find-oncanvas-40")).toBeInTheDocument());
    // 12 is not on the canvas yet: it offers an add, not an indicator.
    expect(screen.getByTestId("find-add-12")).toBeInTheDocument();
    expect(screen.queryByTestId("find-oncanvas-12")).not.toBeInTheDocument();

    await user.click(screen.getByTestId("find-add-12"));
    expect(store().getState().canvasNodes[12]).toBeDefined();
    await waitFor(() => expect(screen.getByTestId("find-oncanvas-12")).toBeInTheDocument());
  });

  it("adds a lone edge with its stub endpoints", async () => {
    const user = userEvent.setup();
    scanPropertiesMock.mockResolvedValue([50]);
    getGraphElementMock.mockImplementation((_i, id) =>
      Promise.resolve(id === 50 ? edge(50, 1, 2) : vertex(id)),
    );
    renderScreen();
    await openFind(user);
    await user.type(screen.getByTestId("find-term"), "x");
    await user.click(screen.getByTestId("find-run"));

    await waitFor(() => expect(screen.getByTestId("find-add-50")).toBeInTheDocument());
    await user.click(screen.getByTestId("find-add-50"));

    const state = store().getState();
    expect(state.canvasEdges[50]).toBeDefined();
    expect(state.canvasNodes[1]).toBeDefined();
    expect(state.canvasNodes[2]).toBeDefined();
  });

  it("sends all hydrated elements, splitting vertices from edges", async () => {
    const user = userEvent.setup();
    scanPropertiesMock.mockResolvedValue([12, 50]);
    getGraphElementMock.mockImplementation((_i, id) =>
      Promise.resolve(id === 50 ? edge(50, 1, 2) : vertex(id)),
    );
    renderScreen();
    await openFind(user);
    await user.type(screen.getByTestId("find-term"), "x");
    await user.click(screen.getByTestId("find-run"));
    await waitFor(() => expect(screen.getByTestId("find-send-all")).toBeEnabled());
    await user.click(screen.getByTestId("find-send-all"));

    const state = store().getState();
    expect(state.canvasNodes[12]).toBeDefined();
    expect(state.canvasEdges[50]).toBeDefined();
  });

  it("selects a found row into the Detail panel", async () => {
    const user = userEvent.setup();
    scanPropertiesMock.mockResolvedValue([12]);
    renderScreen();
    await openFind(user);
    await user.type(screen.getByTestId("find-term"), "acme");
    await user.click(screen.getByTestId("find-run"));
    await waitFor(() => expect(screen.getByTestId("find-row-12")).toBeInTheDocument());

    await user.click(screen.getByRole("button", { name: "#12" }));
    await waitFor(() => expect(screen.getByText("node #12")).toBeInTheDocument());
  });

  it("caps hydration at 500 and says so", async () => {
    const user = userEvent.setup();
    scanPropertiesMock.mockResolvedValue(Array.from({ length: 600 }, (_v, i) => i + 1));
    renderScreen();
    await openFind(user);
    await user.type(screen.getByTestId("find-term"), "x");
    await user.click(screen.getByTestId("find-run"));

    await waitFor(() =>
      expect(screen.getByTestId("find-count")).toHaveTextContent("600 matches"),
    );
    expect(screen.getByTestId("find-count")).toHaveTextContent("first 500 shown");
  });

  it("marks an edge already on the canvas via the edge indicator", async () => {
    const user = userEvent.setup();
    store().getState().mergeIntoCanvas([], [edge(50, 1, 2)]);
    scanPropertiesMock.mockResolvedValue([50]);
    getGraphElementMock.mockImplementation((_i, id) =>
      Promise.resolve(id === 50 ? edge(50, 1, 2) : vertex(id)),
    );
    renderScreen();
    await openFind(user);
    await user.type(screen.getByTestId("find-term"), "x");
    await user.click(screen.getByTestId("find-run"));

    await waitFor(() => expect(screen.getByTestId("find-oncanvas-50")).toBeInTheDocument());
    expect(screen.queryByTestId("find-add-50")).not.toBeInTheDocument();
  });

  it("treats a stub endpoint as not loaded, so the real vertex can still be added", async () => {
    const user = userEvent.setup();
    // Only edge 50 is on the canvas, so vertices 1 and 2 exist as stub placeholders (no props).
    store().getState().mergeIntoCanvas([], [edge(50, 1, 2)]);
    scanPropertiesMock.mockResolvedValue([1]);
    getGraphElementMock.mockImplementation((_i, id) => Promise.resolve(vertex(id)));
    renderScreen();
    await openFind(user);
    await user.type(screen.getByTestId("find-term"), "x");
    await user.click(screen.getByTestId("find-run"));

    await waitFor(() => expect(screen.getByTestId("find-row-1")).toBeInTheDocument());
    // The stub does not count as "on canvas": the add is offered to hydrate the real vertex.
    expect(screen.getByTestId("find-add-1")).toBeInTheDocument();
    expect(screen.queryByTestId("find-oncanvas-1")).not.toBeInTheDocument();
  });

  it("reports an empty result honestly", async () => {
    const user = userEvent.setup();
    scanPropertiesMock.mockResolvedValue([]);
    renderScreen();
    await openFind(user);
    await user.type(screen.getByTestId("find-term"), "zzz");
    await user.click(screen.getByTestId("find-run"));

    await waitFor(() => expect(screen.getByTestId("find-count")).toHaveTextContent("0 matches"));
    expect(screen.getByText("No elements.")).toBeInTheDocument();
  });

  it("uses the singular label for a single match", async () => {
    const user = userEvent.setup();
    scanPropertiesMock.mockResolvedValue([7]);
    renderScreen();
    await openFind(user);
    await user.type(screen.getByTestId("find-term"), "x");
    await user.click(screen.getByTestId("find-run"));

    await waitFor(() => expect(screen.getByTestId("find-count")).toHaveTextContent("1 match"));
    expect(screen.getByTestId("find-count")).not.toHaveTextContent("1 matches");
  });

  it("persists the search term across a remount", async () => {
    const user = userEvent.setup();
    const view = renderScreen();
    await openFind(user);
    await user.type(screen.getByTestId("find-term"), "acme");
    view.unmount();

    renderScreen();
    expect(screen.getByTestId("find-term")).toHaveValue("acme");
  });
});
