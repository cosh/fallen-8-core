// MIT License
//
// canvas-view-controls.test.tsx
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
import type { EdgeREST, GraphREST, StatusREST, VertexREST } from "../src/api/types";

/**
 * Canvas view controls (feature canvas-view-controls): "Clear view" empties the whole
 * working set (nodes, edges, path overlay, selection) while style config and result sets
 * survive; "Show whole graph" is an explicit, capped, merge-only load with busy/disabled
 * states, an honest truncation notice, and fetch failures that leave the canvas intact.
 */

const getGraphMock =
  vi.fn<(i: InstanceConfig, maxElements: number, signal?: AbortSignal) => Promise<GraphREST | null>>();
const getStatusMock =
  vi.fn<(i: InstanceConfig, signal?: AbortSignal) => Promise<StatusREST | null>>();
const getGraphElementMock =
  vi.fn<(i: InstanceConfig, id: number, signal?: AbortSignal) => Promise<VertexREST | EdgeREST | null>>();

vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    getGraph: (i: InstanceConfig, maxElements: number, s?: AbortSignal) =>
      getGraphMock(i, maxElements, s),
    getStatus: (i: InstanceConfig, s?: AbortSignal) => getStatusMock(i, s),
    getGraphElement: (i: InstanceConfig, id: number, s?: AbortSignal) =>
      getGraphElementMock(i, id, s),
  };
});

// GraphCanvas stub - why: see neighborhood-preview.test.tsx; here only screen wiring matters.
vi.mock("../src/canvas/GraphCanvas", () => {
  return {
    GraphCanvas: ({
      nodes,
      onSelect,
    }: {
      nodes: Record<number, { id: number }>;
      onSelect: (ref: { kind: "node" | "edge"; id: number } | null) => void;
    }) => (
      <div data-testid="mock-canvas">
        {Object.values(nodes).map((n) => (
          <button key={n.id} type="button" onClick={() => onSelect({ kind: "node", id: n.id })}>
            node-{n.id}
          </button>
        ))}
      </div>
    ),
  };
});

import { CanvasScreen } from "../src/screens/CanvasScreen";
import { CANVAS_ELEMENT_CAP } from "../src/lib/canvasCap";
import { getInstanceStore, resetInstanceStoresForTests } from "../src/state/instanceStore";
import { SAME_ORIGIN_INSTANCE } from "../src/instances/registry";

function vertex(id: number, label = "person"): VertexREST {
  return {
    id,
    creationDate: "2026-01-01",
    modificationDate: "2026-01-01",
    label,
    kind: "vertex",
    properties: [],
  };
}

function edge(id: number, source: number, target: number): EdgeREST {
  return {
    id,
    creationDate: "2026-01-01",
    modificationDate: "2026-01-01",
    label: null,
    kind: "edge",
    sourceVertex: source,
    targetVertex: target,
    properties: [],
  };
}

function status(vertexCount: number, edgeCount: number): StatusREST {
  return {
    vertexCount,
    edgeCount,
    usedMemory: 0,
    indices: [],
    availableIndexPlugins: [],
    availablePathPlugins: [],
    availableAnalyticsPlugins: [],
    availableServicePlugins: [],
  };
}

// The active scope is SAME_ORIGIN_INSTANCE + "default", whose store key collapses onto the
// bare instance id (see getInstanceStore).
const store = () => getInstanceStore(SAME_ORIGIN_INSTANCE.id);

function renderScreen() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <CanvasScreen />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  resetInstanceStoresForTests();
  localStorage.clear();
  getGraphMock.mockReset().mockResolvedValue({ vertices: [], edges: [] });
  getStatusMock.mockReset().mockResolvedValue(status(0, 0));
  getGraphElementMock.mockReset().mockResolvedValue(vertex(1));
});

describe("Clear view (FR-1)", () => {
  it("empties nodes, edges, overlay, and selection; style config and result sets survive", async () => {
    const user = userEvent.setup();
    const s = store().getState();
    s.mergeIntoCanvas([vertex(1), vertex(2)], [edge(10, 1, 2)]);
    s.setPathOverlay({ pathElements: [], totalWeight: 0 });
    s.setStyleConfig({ nodeColorProperty: "ecosystem" });
    s.addResultSet("kept", [1, 2]);
    renderScreen();

    // Select node 1 so the detail panel shows content that a clear must dismiss.
    await user.click(screen.getByRole("button", { name: "node-1" }));
    await waitFor(() => expect(screen.getByText("node #1")).toBeInTheDocument());

    await user.click(screen.getByRole("button", { name: "Clear view" }));

    const after = store().getState();
    expect(after.canvasNodes).toEqual({});
    expect(after.canvasEdges).toEqual({});
    expect(after.pathOverlay).toBeNull();
    expect(after.styleConfig.nodeColorProperty).toBe("ecosystem");
    expect(after.resultSets).toHaveLength(1);
    // The detail panel is back to the empty hint - the selection did not survive.
    expect(screen.getByText(/or show the whole graph/)).toBeInTheDocument();
    expect(screen.queryByText("node #1")).not.toBeInTheDocument();
  });

  it("is disabled while the canvas is empty", () => {
    renderScreen();
    expect(screen.getByRole("button", { name: "Clear view" })).toBeDisabled();
  });
});

describe("Show whole graph (FR-2/FR-3)", () => {
  it("fetches with the shared cap and merges without dropping existing elements", async () => {
    const user = userEvent.setup();
    store().getState().mergeIntoCanvas([vertex(99)], []);
    getGraphMock.mockResolvedValue({ vertices: [vertex(1), vertex(2)], edges: [edge(10, 1, 2)] });
    getStatusMock.mockResolvedValue(status(2, 1));
    renderScreen();

    await user.click(screen.getByTestId("show-whole-graph"));

    await waitFor(() => {
      const s = store().getState();
      expect(Object.keys(s.canvasNodes).map(Number).sort((a, b) => a - b)).toEqual([1, 2, 99]);
      expect(Object.keys(s.canvasEdges)).toEqual(["10"]);
    });
    // The bound instance view carries a compound id ("local/default"); only the cap matters here.
    expect(getGraphMock.mock.calls[0][1]).toBe(CANVAS_ELEMENT_CAP);
    // Nothing was truncated, so no notice.
    expect(screen.queryByTestId("whole-graph-truncation")).not.toBeInTheDocument();
  });

  it("is disabled and shows a busy label while the load is in flight", async () => {
    const user = userEvent.setup();
    let resolveGraph!: (g: GraphREST) => void;
    getGraphMock.mockReturnValue(new Promise<GraphREST>((r) => (resolveGraph = r)));
    renderScreen();

    await user.click(screen.getByTestId("show-whole-graph"));
    const busy = await screen.findByRole("button", { name: "Loading…" });
    expect(busy).toBeDisabled();

    resolveGraph({ vertices: [vertex(1)], edges: [] });
    await waitFor(() =>
      expect(screen.getByRole("button", { name: "Show whole graph" })).toBeEnabled(),
    );
  });

  it("shows the truncation notice exactly when status counts exceed the fetched counts", async () => {
    const user = userEvent.setup();
    getGraphMock.mockResolvedValue({ vertices: [vertex(1), vertex(2)], edges: [edge(10, 1, 2)] });
    getStatusMock.mockResolvedValue(status(153204, 40000));
    renderScreen();

    await user.click(screen.getByTestId("show-whole-graph"));

    const notice = await screen.findByTestId("whole-graph-truncation");
    // Number formatting is locale-dependent; build the expectation the same way the UI does.
    expect(notice).toHaveTextContent(
      `showing the first ${(2).toLocaleString()} of ${(153204).toLocaleString()} vertices and ${(1).toLocaleString()} of ${(40000).toLocaleString()} edges`,
    );

    // A clear drops the notice with the working set it described.
    await user.click(screen.getByRole("button", { name: "Clear view" }));
    await waitFor(() =>
      expect(screen.queryByTestId("whole-graph-truncation")).not.toBeInTheDocument(),
    );
  });

  it("keeps the truncation notice across unmount and remount (persisted with the canvas)", async () => {
    const user = userEvent.setup();
    getGraphMock.mockResolvedValue({ vertices: [vertex(1)], edges: [] });
    getStatusMock.mockResolvedValue(status(5, 0));
    const first = renderScreen();

    await user.click(screen.getByTestId("show-whole-graph"));
    await screen.findByTestId("whole-graph-truncation");
    first.unmount();

    renderScreen();
    expect(screen.getByTestId("whole-graph-truncation")).toBeInTheDocument();
  });

  it("lands a late merge after a mid-flight clear, with its truncation record (FR-5)", async () => {
    const user = userEvent.setup();
    let resolveGraph!: (g: GraphREST) => void;
    getGraphMock.mockReturnValue(new Promise<GraphREST>((r) => (resolveGraph = r)));
    getStatusMock.mockResolvedValue(status(5, 0));
    store().getState().mergeIntoCanvas([vertex(99)], []);
    renderScreen();

    await user.click(screen.getByTestId("show-whole-graph"));
    // Clear while the load is in flight: nothing is cancelled.
    await user.click(screen.getByRole("button", { name: "Clear view" }));
    expect(store().getState().canvasNodes).toEqual({});

    resolveGraph({ vertices: [vertex(1)], edges: [] });
    await waitFor(() => expect(Object.keys(store().getState().canvasNodes)).toEqual(["1"]));
    // The late merge is truncated (1 of 5) and says so.
    expect(await screen.findByTestId("whole-graph-truncation")).toBeInTheDocument();

    // A second clear recovers completely.
    await user.click(screen.getByRole("button", { name: "Clear view" }));
    expect(store().getState().canvasNodes).toEqual({});
    await waitFor(() =>
      expect(screen.queryByTestId("whole-graph-truncation")).not.toBeInTheDocument(),
    );
  });

  it("reports vertex-only truncation without mentioning edges", async () => {
    const user = userEvent.setup();
    getGraphMock.mockResolvedValue({ vertices: [vertex(1)], edges: [] });
    getStatusMock.mockResolvedValue(status(5, 0));
    renderScreen();

    await user.click(screen.getByTestId("show-whole-graph"));

    const notice = await screen.findByTestId("whole-graph-truncation");
    expect(notice).toHaveTextContent("showing the first 1 of 5 vertices");
    expect(notice).not.toHaveTextContent("edges");
  });

  it("leaves the canvas intact and shows the error when the fetch fails", async () => {
    const user = userEvent.setup();
    store().getState().mergeIntoCanvas([vertex(99)], []);
    getGraphMock.mockRejectedValue(new Error("connection refused"));
    renderScreen();

    await user.click(screen.getByTestId("show-whole-graph"));

    await waitFor(() => expect(screen.getByText(/connection refused/)).toBeInTheDocument());
    const s = store().getState();
    expect(Object.keys(s.canvasNodes)).toEqual(["99"]);
    expect(screen.queryByTestId("whole-graph-truncation")).not.toBeInTheDocument();
    // The button recovered for a retry.
    expect(screen.getByRole("button", { name: "Show whole graph" })).toBeEnabled();
  });

  it("leaves the canvas intact when only the status fetch fails", async () => {
    const user = userEvent.setup();
    store().getState().mergeIntoCanvas([vertex(99)], []);
    getGraphMock.mockResolvedValue({ vertices: [vertex(1)], edges: [] });
    getStatusMock.mockRejectedValue(new Error("status unavailable"));
    renderScreen();

    await user.click(screen.getByTestId("show-whole-graph"));

    await waitFor(() => expect(screen.getByText(/status unavailable/)).toBeInTheDocument());
    expect(Object.keys(store().getState().canvasNodes)).toEqual(["99"]);
    expect(screen.queryByTestId("whole-graph-truncation")).not.toBeInTheDocument();
  });

  it("treats a null graph response as an empty merge", async () => {
    const user = userEvent.setup();
    getGraphMock.mockResolvedValue(null);
    getStatusMock.mockResolvedValue(status(0, 0));
    renderScreen();

    await user.click(screen.getByTestId("show-whole-graph"));

    await waitFor(() =>
      expect(screen.getByRole("button", { name: "Show whole graph" })).toBeEnabled(),
    );
    expect(store().getState().canvasNodes).toEqual({});
    expect(screen.queryByTestId("whole-graph-truncation")).not.toBeInTheDocument();
  });
});
