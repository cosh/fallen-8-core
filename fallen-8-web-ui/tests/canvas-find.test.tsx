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
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { InstanceConfig } from "../src/instances/types";
import type { EdgeREST, StatusREST, VertexREST } from "../src/api/types";

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
const getStatusMock = vi.fn<(i: InstanceConfig, s?: AbortSignal) => Promise<StatusREST | null>>();
// An EDGE selection takes a different route than a vertex (CanvasScreen detail query), so the edge
// arm of find-similar is only reachable with this mocked.
const getEdgeMock = vi.fn<(i: InstanceConfig, id: number, s?: AbortSignal) => Promise<EdgeREST | null>>();

vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    scanProperties: (i: InstanceConfig, spec: { searchTerm: string; label?: string; resultType: string }) =>
      scanPropertiesMock(i, spec),
    getGraphElement: (i: InstanceConfig, id: number, s?: AbortSignal) => getGraphElementMock(i, id, s),
    getStatistics: (i: InstanceConfig, s?: AbortSignal) => getStatisticsMock(i, s),
    getStatus: (i: InstanceConfig, s?: AbortSignal) => getStatusMock(i, s),
    getEdge: (i: InstanceConfig, id: number, s?: AbortSignal) => getEdgeMock(i, id, s),
  };
});

vi.mock("../src/canvas/GraphCanvas", () => ({
  // Surface the hover spotlight prop so the Find -> canvas wiring is assertable without WebGL.
  GraphCanvas: ({ highlight }: { highlight?: { kind: string; id: number } | null }) => (
    <div
      data-testid="mock-canvas"
      data-highlight={highlight ? `${highlight.kind}:${highlight.id}` : "none"}
    />
  ),
}));

import { CanvasScreen } from "../src/screens/CanvasScreen";
import { FindPanel } from "../src/canvas/FindPanel";
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
  getStatusMock.mockReset().mockResolvedValue(null);
  getEdgeMock.mockReset().mockImplementation((_i, id) => Promise.resolve(edge(id, 1, 2)));
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

describe("hover spotlight", () => {
  async function searchAndRun(user: ReturnType<typeof userEvent.setup>, term = "acme") {
    renderScreen();
    await openFind(user);
    await user.type(screen.getByTestId("find-term"), term);
    await user.click(screen.getByTestId("find-run"));
  }

  it("spotlights the hovered result's node on the canvas and clears on leave", async () => {
    const user = userEvent.setup();
    scanPropertiesMock.mockResolvedValue([12]);
    await searchAndRun(user);
    await waitFor(() => expect(screen.getByTestId("find-row-12")).toBeInTheDocument());

    expect(screen.getByTestId("mock-canvas")).toHaveAttribute("data-highlight", "none");
    await user.hover(screen.getByTestId("find-row-12"));
    expect(screen.getByTestId("mock-canvas")).toHaveAttribute("data-highlight", "node:12");
    await user.unhover(screen.getByTestId("find-row-12"));
    expect(screen.getByTestId("mock-canvas")).toHaveAttribute("data-highlight", "none");
  });

  it("passes an edge row's ref through (the canvas decides node-only spotlighting)", async () => {
    const user = userEvent.setup();
    scanPropertiesMock.mockResolvedValue([50]);
    getGraphElementMock.mockImplementation((_i, id) =>
      Promise.resolve(id === 50 ? edge(50, 1, 2) : vertex(id)),
    );
    await searchAndRun(user, "x");
    await waitFor(() => expect(screen.getByTestId("find-row-50")).toBeInTheDocument());

    await user.hover(screen.getByTestId("find-row-50"));
    expect(screen.getByTestId("mock-canvas")).toHaveAttribute("data-highlight", "edge:50");
  });

  it("clears the spotlight when the Find tab is left", async () => {
    const user = userEvent.setup();
    scanPropertiesMock.mockResolvedValue([12]);
    await searchAndRun(user);
    await waitFor(() => expect(screen.getByTestId("find-row-12")).toBeInTheDocument());
    await user.hover(screen.getByTestId("find-row-12"));
    expect(screen.getByTestId("mock-canvas")).toHaveAttribute("data-highlight", "node:12");

    // Leaving Find unmounts the panel, which must clear any lingering spotlight.
    await user.click(screen.getByTestId("canvas-tab-style"));
    expect(screen.getByTestId("mock-canvas")).toHaveAttribute("data-highlight", "none");
  });

  it("clears a stale spotlight when a new search runs under a stationary cursor", async () => {
    const user = userEvent.setup();
    scanPropertiesMock.mockResolvedValue([12]);
    await searchAndRun(user);
    await waitFor(() => expect(screen.getByTestId("find-row-12")).toBeInTheDocument());
    await user.hover(screen.getByTestId("find-row-12"));
    expect(screen.getByTestId("mock-canvas")).toHaveAttribute("data-highlight", "node:12");

    // Submit WITHOUT moving the pointer off the row (fireEvent, not a click): no mouseleave fires,
    // so only the search's own onHover(null) can clear the spotlight the replaced row summoned.
    scanPropertiesMock.mockResolvedValue([99]);
    fireEvent.submit(screen.getByTestId("find-term").closest("form")!);
    await waitFor(() =>
      expect(screen.getByTestId("mock-canvas")).toHaveAttribute("data-highlight", "none"),
    );
  });

  it("spotlights on keyboard focus of the id button and clears on blur", async () => {
    const user = userEvent.setup();
    scanPropertiesMock.mockResolvedValue([12]);
    await searchAndRun(user);
    await waitFor(() => expect(screen.getByTestId("find-row-12")).toBeInTheDocument());

    const idButton = screen.getByRole("button", { name: "#12" });
    fireEvent.focus(idButton);
    expect(screen.getByTestId("mock-canvas")).toHaveAttribute("data-highlight", "node:12");
    fireEvent.blur(idButton);
    expect(screen.getByTestId("mock-canvas")).toHaveAttribute("data-highlight", "none");
  });

  it("clears the spotlight on unmount even with no mouseleave (FindPanel in isolation)", async () => {
    const user = userEvent.setup();
    const onHover = vi.fn();
    scanPropertiesMock.mockResolvedValue([12]);
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const view = render(
      <QueryClientProvider client={client}>
        <FindPanel onSelect={() => {}} onHover={onHover} />
      </QueryClientProvider>,
    );
    await user.type(screen.getByTestId("find-term"), "acme");
    await user.click(screen.getByTestId("find-run"));
    await waitFor(() => expect(screen.getByTestId("find-row-12")).toBeInTheDocument());
    await user.hover(screen.getByTestId("find-row-12"));
    expect(onHover).toHaveBeenLastCalledWith({ kind: "node", id: 12 });

    onHover.mockClear();
    view.unmount();
    expect(onHover).toHaveBeenCalledWith(null);
  });
});

describe("find similar, from the Detail panel (feature element-similarity-search)", () => {
  const BOUND: StatusREST = {
    vertexCount: 0,
    edgeCount: 0,
    usedMemory: 0,
    indices: [
      {
        indexId: "sim",
        pluginType: "VectorIndex",
        embeddingName: "default",
        capabilities: ["vector"],
        keys: 1,
        values: 1,
      },
    ],
    availableIndexPlugins: ["VectorIndex"],
    availablePathPlugins: [],
    availableAnalyticsPlugins: [],
    availableServicePlugins: [],
  };

  const EMBEDDED = [{ propertyId: "$embedding:default", propertyValue: "[0.1, 0.2, 0.3]" }];

  async function selectFoundElement(
    user: ReturnType<typeof userEvent.setup>,
    id: number,
  ) {
    scanPropertiesMock.mockResolvedValue([id]);
    renderScreen();
    await user.click(screen.getByTestId("canvas-tab-find"));
    await user.type(screen.getByTestId("find-term"), "odo");
    await user.click(screen.getByTestId("find-run"));
    await waitFor(() => expect(screen.getByTestId(`find-row-${id}`)).toBeInTheDocument());
    await user.click(screen.getByRole("button", { name: `#${id}` }));
  }

  it("is offered for an element whose embedding a bound index projects", async () => {
    const user = userEvent.setup();
    getStatusMock.mockResolvedValue(BOUND);
    getGraphElementMock.mockImplementation((_i, id) =>
      Promise.resolve({ ...vertex(id, "signal"), properties: EMBEDDED }),
    );

    await selectFoundElement(user, 12);
    await waitFor(() => expect(screen.getByTestId("find-similar")).toBeInTheDocument());
  });

  it("carries the element's vector, label and id to the Query screen", async () => {
    const user = userEvent.setup();
    getStatusMock.mockResolvedValue(BOUND);
    getGraphElementMock.mockImplementation((_i, id) =>
      Promise.resolve({ ...vertex(id, "signal"), properties: EMBEDDED }),
    );

    await selectFoundElement(user, 12);
    await user.click(await screen.findByTestId("find-similar"));

    expect(store().getState().scanPrefill).toEqual({
      indexId: "sim",
      vectorText: "[0.1, 0.2, 0.3]",
      sourceElementId: 12,
      label: "signal",
      kind: "vertex",
    });
  });

  it("constrains an EDGE to edges, which no other test or live run has ever exercised", async () => {
    // The kind is decided at this call site from selected.kind, and getting it backwards constrains
    // an edge search to vertices - which matches nothing and is indistinguishable from "nothing is
    // similar". An earlier revision of the sibling call site had exactly that defect.
    const user = userEvent.setup();
    getStatusMock.mockResolvedValue(BOUND);
    getEdgeMock.mockImplementation((_i, id) =>
      Promise.resolve({ ...edge(id, 1, 2), label: "sends", properties: EMBEDDED }),
    );
    getGraphElementMock.mockImplementation((_i, id) =>
      Promise.resolve({ ...edge(id, 1, 2), label: "sends", properties: EMBEDDED }),
    );

    await selectFoundElement(user, 21);
    await user.click(await screen.findByTestId("find-similar"));

    expect(store().getState().scanPrefill?.kind).toBe("edge");
    expect(store().getState().scanPrefill?.sourceElementId).toBe(21);
  });

  it("is NOT offered when no bound index projects the embedding", async () => {
    const user = userEvent.setup();
    getStatusMock.mockResolvedValue({ ...BOUND, indices: [] });
    getGraphElementMock.mockImplementation((_i, id) =>
      Promise.resolve({ ...vertex(id, "signal"), properties: EMBEDDED }),
    );

    await selectFoundElement(user, 12);
    await waitFor(() => expect(screen.getByText("node #12")).toBeInTheDocument());
    expect(screen.queryByTestId("find-similar")).not.toBeInTheDocument();
  });

  it("is NOT offered for an element carrying no embedding at all", async () => {
    const user = userEvent.setup();
    getStatusMock.mockResolvedValue(BOUND);
    getGraphElementMock.mockImplementation((_i, id) => Promise.resolve(vertex(id, "signal")));

    await selectFoundElement(user, 12);
    await waitFor(() => expect(screen.getByText("node #12")).toBeInTheDocument());
    expect(screen.queryByTestId("find-similar")).not.toBeInTheDocument();
  });
});
