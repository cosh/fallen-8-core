// MIT License
//
// canvas-interact-panel.test.tsx
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
import type {
  EdgeREST,
  StatusREST,
  VectorSearchResultREST,
  VertexREST,
} from "../src/api/types";

/**
 * Canvas "Interact" tab (feature canvas-interact): the filters that build a match set, the
 * evaluate-then-act lifecycle for the costly ones, and the two view-only verbs. What is asserted
 * hardest is the honesty: a stale match set is never actionable, an unscored vertex is never
 * swept up, and a sweep that stopped early says so.
 */

const getInDegreeMock = vi.fn<(i: InstanceConfig, id: number, s?: AbortSignal) => Promise<number>>();
const getOutDegreeMock = vi.fn<(i: InstanceConfig, id: number, s?: AbortSignal) => Promise<number>>();
const embeddingSearchMock =
  vi.fn<(i: InstanceConfig, spec: unknown, s?: AbortSignal) => Promise<VectorSearchResultREST>>();
const getStatusMock = vi.fn<(i: InstanceConfig, s?: AbortSignal) => Promise<StatusREST>>();
const getGraphElementMock =
  vi.fn<(i: InstanceConfig, id: number, s?: AbortSignal) => Promise<VertexREST | EdgeREST | null>>();

vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    getInDegree: (i: InstanceConfig, id: number, s?: AbortSignal) => getInDegreeMock(i, id, s),
    getOutDegree: (i: InstanceConfig, id: number, s?: AbortSignal) => getOutDegreeMock(i, id, s),
    embeddingSearch: (i: InstanceConfig, spec: unknown, s?: AbortSignal) =>
      embeddingSearchMock(i, spec, s),
    getStatus: (i: InstanceConfig, s?: AbortSignal) => getStatusMock(i, s),
    getGraphElement: (i: InstanceConfig, id: number, s?: AbortSignal) =>
      getGraphElementMock(i, id, s),
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

vi.mock("../src/canvas/GraphCanvas", () => ({
  GraphCanvas: () => <div data-testid="mock-canvas" />,
}));

import { CanvasScreen } from "../src/screens/CanvasScreen";
import { getInstanceStore, resetInstanceStoresForTests } from "../src/state/instanceStore";
import { SAME_ORIGIN_INSTANCE } from "../src/instances/registry";
import { EXPAND_SWEEP_CAP } from "../src/lib/canvasInteract";
import { CANVAS_ELEMENT_CAP } from "../src/lib/canvasCap";

const store = () => getInstanceStore(SAME_ORIGIN_INSTANCE.id);

function vertex(id: number, label: string | null = "person", props: Record<string, unknown> = {}): VertexREST {
  return {
    id,
    creationDate: "",
    modificationDate: "",
    label,
    kind: "vertex",
    properties: Object.entries(props).map(([propertyId, propertyValue]) => ({
      propertyId,
      propertyValue: propertyValue as string | number | boolean,
    })),
  };
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

/** A status with the embedding provider on and one vector index bound, so semantic is offered. */
function status(options: { provider?: boolean; bound?: boolean } = {}): StatusREST {
  const provider = options.provider ?? true;
  const bound = options.bound ?? true;
  return {
    vertexCount: 0,
    edgeCount: 0,
    indices: bound
      ? [{ indexId: "sem", pluginType: "VectorIndex", embeddingName: "default" }]
      : [{ indexId: "plain", pluginType: "SingleValueIndex", embeddingName: null }],
    embedding: {
      enabled: provider,
      backend: "Ollama",
      modelName: "bge-m3",
      modelVersion: null,
      dimension: 1024,
      intendedMetric: "Cosine",
      loaded: true,
    },
  } as unknown as StatusREST;
}

function seedVertices(...vertices: VertexREST[]) {
  store().getState().mergeIntoCanvas(vertices, []);
}

function renderScreen() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <CanvasScreen />
    </QueryClientProvider>,
  );
}

async function openInteract(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByTestId("canvas-tab-interact"));
  return screen.findByTestId("interact-panel");
}

const canvasNodeIds = () => Object.keys(store().getState().canvasNodes).map(Number).sort((a, b) => a - b);

beforeEach(() => {
  resetInstanceStoresForTests();
  localStorage.clear();
  getInDegreeMock.mockReset().mockResolvedValue(0);
  getOutDegreeMock.mockReset().mockResolvedValue(0);
  getStatusMock.mockReset().mockResolvedValue(status());
  getGraphElementMock.mockReset().mockImplementation((_i, id) => Promise.resolve(vertex(id)));
  embeddingSearchMock
    .mockReset()
    .mockResolvedValue({ metric: "Cosine", higherIsBetter: true, results: [] });
  neighborhoodMock
    .mockReset()
    .mockImplementation((_i, id) =>
      Promise.resolve({
        vertices: [vertex(id * 100, "neighbor")],
        edges: [restEdge(id * 10, id, id * 100)],
        truncated: false,
      }),
    );
});

describe("the tab itself", () => {
  it("sits in the strip and persists as the active tab", async () => {
    const user = userEvent.setup();
    seedVertices(vertex(1));
    const view = renderScreen();
    await openInteract(user);

    view.unmount();
    renderScreen();
    // The draft remembered the tab, so Interact renders without clicking anything.
    expect(await screen.findByTestId("interact-panel")).toBeInTheDocument();
  });

  it("leaves the Detail panel visible under the tab", async () => {
    const user = userEvent.setup();
    seedVertices(vertex(1));
    renderScreen();
    await openInteract(user);

    expect(screen.getByText(/Select a node or edge/)).toBeInTheDocument();
  });
});

describe("the match set, with no filter and with cheap ones", () => {
  it("matches every canvas vertex when nothing is filtered, which is expand-all", async () => {
    const user = userEvent.setup();
    seedVertices(vertex(1), vertex(2), vertex(3));
    renderScreen();
    await openInteract(user);

    expect(screen.getByTestId("interact-count")).toHaveTextContent("no filter · 3 of 3 vertices match");
    expect(screen.getByTestId("interact-expand")).toHaveTextContent("Expand (3)");
    expect(screen.getByTestId("interact-expand")).toBeEnabled();
  });

  it("narrows live by label, with no Preview step", async () => {
    const user = userEvent.setup();
    seedVertices(vertex(1, "person"), vertex(2, "pdu"), vertex(3, "person"));
    renderScreen();
    await openInteract(user);

    await user.type(screen.getByTestId("interact-label"), "pdu");

    expect(screen.getByTestId("interact-count")).toHaveTextContent("filtered · 1 of 3 vertices match");
    expect(screen.queryByTestId("interact-preview")).not.toBeInTheDocument();
  });

  it("narrows by a property key and term over the canvas snapshot", async () => {
    const user = userEvent.setup();
    seedVertices(
      vertex(1, "person", { name: "Ada" }),
      vertex(2, "person", { name: "Alan" }),
      vertex(3, "person", {}),
    );
    renderScreen();
    await openInteract(user);

    await user.type(screen.getByTestId("interact-prop-key"), "name");
    expect(screen.getByTestId("interact-count")).toHaveTextContent("2 of 3 vertices match");

    await user.type(screen.getByTestId("interact-prop-term"), "ada");
    expect(screen.getByTestId("interact-count")).toHaveTextContent("1 of 3 vertices match");
  });

  it("counts on-canvas degree live, and recounts when a merge adds edges", async () => {
    const user = userEvent.setup();
    seedVertices(vertex(1), vertex(2), vertex(3));
    renderScreen();
    await openInteract(user);

    await user.click(screen.getByTestId("interact-degree-source-canvas"));
    await user.type(screen.getByTestId("interact-degree-value"), "0");
    // Nothing is connected on the canvas yet, so nothing is "over 0".
    expect(screen.getByTestId("interact-count")).toHaveTextContent("0 of 3 vertices match");
    expect(screen.queryByTestId("interact-preview")).not.toBeInTheDocument();

    store().getState().mergeIntoCanvas([], [restEdge(50, 1, 2)]);

    await waitFor(() =>
      expect(screen.getByTestId("interact-count")).toHaveTextContent("2 of 3 vertices match"),
    );
  });
});

describe("remove", () => {
  it("takes every matched vertex off the canvas with its edges, and leaves the rest", async () => {
    const user = userEvent.setup();
    seedVertices(vertex(1, "person"), vertex(2, "pdu"), vertex(3, "person"));
    store().getState().mergeIntoCanvas([], [restEdge(50, 1, 2)]);
    renderScreen();
    await openInteract(user);

    await user.type(screen.getByTestId("interact-label"), "person");
    await user.click(screen.getByTestId("interact-remove"));

    expect(canvasNodeIds()).toEqual([2]);
    expect(Object.keys(store().getState().canvasEdges)).toEqual([]);
  });

  it("clears a selection it removed, so the Detail panel stops describing what just left", async () => {
    const user = userEvent.setup();
    // A previewed match set gives rows whose id selects into the Detail panel.
    getInDegreeMock.mockImplementation((_i, id) => Promise.resolve(id === 1 ? 40 : 0));
    getOutDegreeMock.mockResolvedValue(0);
    seedVertices(vertex(1), vertex(2));
    renderScreen();
    await openInteract(user);

    await user.type(screen.getByTestId("interact-degree-value"), "1");
    await user.click(screen.getByTestId("interact-preview"));
    await waitFor(() => expect(screen.getByTestId("interact-match-1")).toBeInTheDocument());

    await user.click(screen.getByRole("button", { name: "#1" }));
    await waitFor(() => expect(screen.getByText("node #1")).toBeInTheDocument());

    await user.click(screen.getByTestId("interact-remove"));

    expect(canvasNodeIds()).toEqual([2]);
    expect(screen.getByText(/Select a node or edge/)).toBeInTheDocument();
  });

  it("keeps a selection that was NOT in the match set", async () => {
    const user = userEvent.setup();
    getInDegreeMock.mockImplementation((_i, id) => Promise.resolve(id === 1 ? 40 : 0));
    getOutDegreeMock.mockResolvedValue(0);
    seedVertices(vertex(1), vertex(2));
    renderScreen();
    await openInteract(user);

    // Select #1 off a preview that matched it...
    await user.type(screen.getByTestId("interact-degree-value"), "1");
    await user.click(screen.getByTestId("interact-preview"));
    await waitFor(() => expect(screen.getByTestId("interact-match-1")).toBeInTheDocument());
    await user.click(screen.getByRole("button", { name: "#1" }));
    await waitFor(() => expect(screen.getByText("node #1")).toBeInTheDocument());

    // ...then flip the comparison so the match set becomes #2, and re-evaluate.
    await user.selectOptions(screen.getByTestId("interact-degree-op"), "under");
    await user.click(screen.getByTestId("interact-preview"));
    await waitFor(() => expect(screen.getByTestId("interact-match-2")).toBeInTheDocument());

    await user.click(screen.getByTestId("interact-remove"));

    expect(canvasNodeIds()).toEqual([1]);
    expect(screen.getByText("node #1")).toBeInTheDocument();
  });

  it("is disabled when the match set is empty", async () => {
    const user = userEvent.setup();
    seedVertices(vertex(1, "person"));
    renderScreen();
    await openInteract(user);

    await user.type(screen.getByTestId("interact-label"), "nothing-has-this");
    expect(screen.getByTestId("interact-remove")).toBeDisabled();
    expect(screen.getByTestId("interact-expand")).toBeDisabled();
  });
});

describe("expand", () => {
  it("expands every match and merges what it found", async () => {
    const user = userEvent.setup();
    seedVertices(vertex(1), vertex(2));
    renderScreen();
    await openInteract(user);

    await user.click(screen.getByTestId("interact-expand"));

    await waitFor(() => expect(screen.getByTestId("interact-expand-report")).toBeInTheDocument());
    expect(screen.getByTestId("interact-expand-report")).toHaveTextContent("expanded 2 of 2 vertices");
    // Each seed brought one neighbor and one edge.
    expect(canvasNodeIds()).toEqual([1, 2, 100, 200]);
    expect(Object.keys(store().getState().canvasEdges).sort()).toEqual(["10", "20"]);
  });

  it("skips vertices already on the canvas rather than re-fetching them", async () => {
    const user = userEvent.setup();
    seedVertices(vertex(1), vertex(2));
    renderScreen();
    await openInteract(user);

    await user.click(screen.getByTestId("interact-expand"));
    await waitFor(() => expect(screen.getByTestId("interact-expand-report")).toBeInTheDocument());

    const skip = neighborhoodMock.mock.calls[0][2].skipNeighborIds!;
    expect(skip.has(1)).toBe(true);
    expect(skip.has(2)).toBe(true);
  });

  it("refuses a sweep over the cap and says how to narrow it", async () => {
    const user = userEvent.setup();
    seedVertices(...Array.from({ length: EXPAND_SWEEP_CAP + 1 }, (_v, i) => vertex(i + 1)));
    renderScreen();
    await openInteract(user);

    expect(screen.getByTestId("interact-expand-over-cap")).toHaveTextContent(
      `over the ${EXPAND_SWEEP_CAP} one expand sweeps at once`,
    );
    expect(screen.getByTestId("interact-expand")).toBeDisabled();
    // Removing is still fine: it costs no requests at all.
    expect(screen.getByTestId("interact-remove")).toBeEnabled();
  });

  it("reports a vertex whose expand failed instead of failing the run", async () => {
    const user = userEvent.setup();
    neighborhoodMock.mockImplementation((_i, id) =>
      id === 2
        ? Promise.reject(new Error("boom"))
        : Promise.resolve({ vertices: [vertex(id * 100)], edges: [], truncated: false }),
    );
    seedVertices(vertex(1), vertex(2));
    renderScreen();
    await openInteract(user);

    await user.click(screen.getByTestId("interact-expand"));

    await waitFor(() => expect(screen.getByTestId("interact-expand-report")).toBeInTheDocument());
    expect(screen.getByTestId("interact-expand-report")).toHaveTextContent("1 failed");
  });

  it("stops at the canvas element cap and says so rather than looking like a small graph", async () => {
    const user = userEvent.setup();
    // A canvas already at the cap: the budget is checked BEFORE a batch, so the sweep declines to
    // grow it further. Filled with edges rather than vertices so the match set stays under the
    // expand cap - it is the ELEMENT count that binds here, which is the point.
    seedVertices(...Array.from({ length: 5 }, (_v, i) => vertex(i + 1)));
    store()
      .getState()
      .mergeIntoCanvas(
        [],
        Array.from({ length: CANVAS_ELEMENT_CAP }, (_v, i) => restEdge(1_000 + i, 1, 2)),
      );
    renderScreen();
    await openInteract(user);

    await user.click(screen.getByTestId("interact-expand"));

    await waitFor(() => expect(screen.getByTestId("interact-expand-report")).toBeInTheDocument());
    expect(screen.getByTestId("interact-expand-report")).toHaveTextContent(
      // Grouped by the runtime's locale, like every other count in the studio.
      `stopped at the ${CANVAS_ELEMENT_CAP.toLocaleString()}-element canvas cap`,
    );
    expect(screen.getByTestId("interact-expand-report")).toHaveTextContent("expanded 0 of 5");
    expect(neighborhoodMock).not.toHaveBeenCalled();
  });
});

describe("the database degree filter, which is evaluated rather than live", () => {
  it("does not act until Preview has run", async () => {
    const user = userEvent.setup();
    seedVertices(vertex(1), vertex(2));
    renderScreen();
    await openInteract(user);

    await user.type(screen.getByTestId("interact-degree-value"), "5");

    expect(screen.getByTestId("interact-count")).toHaveTextContent("evaluate to match");
    expect(screen.getByTestId("interact-expand")).toBeDisabled();
    expect(screen.getByTestId("interact-remove")).toBeDisabled();
    expect(screen.getByTestId("interact-preview")).toBeEnabled();
  });

  it("keeps the vertices whose true degree passes, then acts on exactly those", async () => {
    const user = userEvent.setup();
    getInDegreeMock.mockImplementation((_i, id) => Promise.resolve(id === 1 ? 40 : 1));
    getOutDegreeMock.mockImplementation((_i, id) => Promise.resolve(id === 1 ? 40 : 0));
    seedVertices(vertex(1), vertex(2));
    renderScreen();
    await openInteract(user);

    await user.type(screen.getByTestId("interact-degree-value"), "50");
    await user.click(screen.getByTestId("interact-preview"));

    await waitFor(() =>
      expect(screen.getByTestId("interact-count")).toHaveTextContent("1 of 2 vertices match"),
    );
    expect(screen.getByTestId("interact-match-1")).toBeInTheDocument();

    await user.click(screen.getByTestId("interact-remove"));
    expect(canvasNodeIds()).toEqual([2]);
  });

  it("asks only for the direction it needs", async () => {
    const user = userEvent.setup();
    seedVertices(vertex(1));
    renderScreen();
    await openInteract(user);

    await user.selectOptions(screen.getByTestId("interact-degree-direction"), "in");
    await user.type(screen.getByTestId("interact-degree-value"), "1");
    await user.click(screen.getByTestId("interact-preview"));

    await waitFor(() => expect(screen.queryByTestId("interact-progress")).not.toBeInTheDocument());
    expect(getInDegreeMock).toHaveBeenCalled();
    expect(getOutDegreeMock).not.toHaveBeenCalled();
  });

  it("evaluates over the CHEAP survivors only, so narrowing first really costs less", async () => {
    const user = userEvent.setup();
    seedVertices(vertex(1, "person"), vertex(2, "pdu"), vertex(3, "pdu"));
    renderScreen();
    await openInteract(user);

    await user.type(screen.getByTestId("interact-label"), "person");
    await user.type(screen.getByTestId("interact-degree-value"), "0");
    await user.click(screen.getByTestId("interact-preview"));

    await waitFor(() => expect(screen.queryByTestId("interact-progress")).not.toBeInTheDocument());
    // One candidate survived the label filter, so exactly one vertex was probed.
    expect(getInDegreeMock).toHaveBeenCalledTimes(1);
    expect(getInDegreeMock.mock.calls[0][1]).toBe(1);
  });

  it("invalidates an evaluated set when a filter is edited", async () => {
    const user = userEvent.setup();
    getInDegreeMock.mockResolvedValue(10);
    getOutDegreeMock.mockResolvedValue(10);
    seedVertices(vertex(1));
    renderScreen();
    await openInteract(user);

    await user.type(screen.getByTestId("interact-degree-value"), "5");
    await user.click(screen.getByTestId("interact-preview"));
    await waitFor(() =>
      expect(screen.getByTestId("interact-count")).toHaveTextContent("1 of 1 vertices match"),
    );

    await user.type(screen.getByTestId("interact-degree-value"), "0"); // now "50"

    expect(screen.getByTestId("interact-count")).toHaveTextContent("evaluate to match");
    expect(screen.getByTestId("interact-remove")).toBeDisabled();
  });

  it("invalidates an evaluated set when the CANVAS changes under it", async () => {
    const user = userEvent.setup();
    getInDegreeMock.mockResolvedValue(10);
    getOutDegreeMock.mockResolvedValue(10);
    seedVertices(vertex(1));
    renderScreen();
    await openInteract(user);

    await user.type(screen.getByTestId("interact-degree-value"), "5");
    await user.click(screen.getByTestId("interact-preview"));
    await waitFor(() =>
      expect(screen.getByTestId("interact-count")).toHaveTextContent("1 of 1 vertices match"),
    );

    store().getState().mergeIntoCanvas([vertex(99)], []);

    await waitFor(() =>
      expect(screen.getByTestId("interact-count")).toHaveTextContent("evaluate to match"),
    );
    expect(screen.getByTestId("interact-remove")).toBeDisabled();
  });

  it("refuses to evaluate more candidates than the sweep cap, naming the narrowing tools", async () => {
    const user = userEvent.setup();
    seedVertices(...Array.from({ length: 1_001 }, (_v, i) => vertex(i + 1)));
    renderScreen();
    await openInteract(user);

    await user.type(screen.getByTestId("interact-degree-value"), "5");
    await user.click(screen.getByTestId("interact-preview"));

    await waitFor(() => expect(screen.getByTestId("interact-over-cap")).toBeInTheDocument());
    expect(screen.getByTestId("interact-over-cap")).toHaveTextContent("Narrow by label or property");
    expect(getInDegreeMock).not.toHaveBeenCalled();
  });
});

describe("the semantic filter", () => {
  it("says why it cannot run when the provider is off", async () => {
    const user = userEvent.setup();
    getStatusMock.mockResolvedValue(status({ provider: false }));
    seedVertices(vertex(1));
    renderScreen();
    await openInteract(user);

    await waitFor(() =>
      expect(screen.getByTestId("interact-semantic-absent")).toHaveTextContent(
        "needs the embedding provider",
      ),
    );
    expect(screen.queryByTestId("interact-semantic-text")).not.toBeInTheDocument();
  });

  it("says why it cannot run when no index is bound to an embedding", async () => {
    const user = userEvent.setup();
    getStatusMock.mockResolvedValue(status({ bound: false }));
    seedVertices(vertex(1));
    renderScreen();
    await openInteract(user);

    await waitFor(() =>
      expect(screen.getByTestId("interact-semantic-absent")).toHaveTextContent(
        "No vector index is bound",
      ),
    );
  });

  it("keeps the closer vertices, embedding the text once against the bound index", async () => {
    const user = userEvent.setup();
    embeddingSearchMock.mockResolvedValue({
      metric: "Cosine",
      higherIsBetter: true,
      results: [
        { graphElementId: 1, score: 0.9 },
        { graphElementId: 2, score: 0.2 },
      ],
    });
    seedVertices(vertex(1), vertex(2));
    renderScreen();
    await openInteract(user);
    await screen.findByTestId("interact-semantic-text");

    await user.type(screen.getByTestId("interact-semantic-text"), "turbine vibration");
    await user.type(screen.getByTestId("interact-semantic-threshold"), "0.5");
    await user.click(screen.getByTestId("interact-preview"));

    await waitFor(() =>
      expect(screen.getByTestId("interact-count")).toHaveTextContent("1 of 2 vertices match"),
    );
    expect(embeddingSearchMock).toHaveBeenCalledTimes(1);
    expect(embeddingSearchMock.mock.calls[0][1]).toMatchObject({
      indexId: "sem",
      text: "turbine vibration",
      kind: "vertex",
    });
  });

  it("inverts the comparison for a lower-is-better metric without the client re-deriving it", async () => {
    const user = userEvent.setup();
    embeddingSearchMock.mockResolvedValue({
      metric: "L2",
      higherIsBetter: false,
      results: [
        { graphElementId: 1, score: 0.1 },
        { graphElementId: 2, score: 9.0 },
      ],
    });
    seedVertices(vertex(1), vertex(2));
    renderScreen();
    await openInteract(user);
    await screen.findByTestId("interact-semantic-text");

    await user.type(screen.getByTestId("interact-semantic-text"), "brake");
    await user.type(screen.getByTestId("interact-semantic-threshold"), "1");
    await user.click(screen.getByTestId("interact-preview"));

    await waitFor(() =>
      expect(screen.getByTestId("interact-count")).toHaveTextContent("1 of 2 vertices match"),
    );
    expect(screen.getByTestId("interact-match-1")).toBeInTheDocument();
  });

  it("never sweeps up an unscored vertex under 'farther', and counts it", async () => {
    const user = userEvent.setup();
    // #2 carries no embedding, so the search returns no score for it.
    embeddingSearchMock.mockResolvedValue({
      metric: "Cosine",
      higherIsBetter: true,
      results: [{ graphElementId: 1, score: 0.9 }],
    });
    seedVertices(vertex(1), vertex(2));
    renderScreen();
    await openInteract(user);
    await screen.findByTestId("interact-semantic-text");

    await user.type(screen.getByTestId("interact-semantic-text"), "brake");
    await user.type(screen.getByTestId("interact-semantic-threshold"), "0.5");
    await user.selectOptions(screen.getByTestId("interact-semantic-direction"), "farther");
    await user.click(screen.getByTestId("interact-preview"));

    await waitFor(() => expect(screen.getByTestId("interact-unscored")).toBeInTheDocument());
    expect(screen.getByTestId("interact-count")).toHaveTextContent("0 of 2 vertices match");
    expect(screen.getByTestId("interact-unscored")).toHaveTextContent("1 had no score");

    // The verb is disabled precisely because nothing matched: the unscored vertex survives.
    expect(screen.getByTestId("interact-remove")).toBeDisabled();
  });

  it("stays inactive while the threshold is blank, because 'closer than nothing' is not a filter", async () => {
    const user = userEvent.setup();
    seedVertices(vertex(1));
    renderScreen();
    await openInteract(user);
    await screen.findByTestId("interact-semantic-text");

    await user.type(screen.getByTestId("interact-semantic-text"), "brake");

    expect(screen.queryByTestId("interact-preview")).not.toBeInTheDocument();
    expect(screen.getByTestId("interact-count")).toHaveTextContent("1 of 1 vertices match");
  });
});

describe("draft persistence", () => {
  it("remembers the filter inputs but not an evaluated match set", async () => {
    const user = userEvent.setup();
    getInDegreeMock.mockResolvedValue(10);
    getOutDegreeMock.mockResolvedValue(10);
    seedVertices(vertex(1, "person"));
    const view = renderScreen();
    await openInteract(user);

    await user.type(screen.getByTestId("interact-label"), "person");
    await user.type(screen.getByTestId("interact-degree-value"), "5");
    await user.click(screen.getByTestId("interact-preview"));
    await waitFor(() =>
      expect(screen.getByTestId("interact-count")).toHaveTextContent("1 of 1 vertices match"),
    );

    view.unmount();
    renderScreen();
    await screen.findByTestId("interact-panel");

    expect(screen.getByTestId("interact-label")).toHaveValue("person");
    expect(screen.getByTestId("interact-degree-value")).toHaveValue(5);
    // Results are ephemeral like every result in the studio: it must be evaluated again.
    expect(screen.getByTestId("interact-count")).toHaveTextContent("evaluate to match");
  });
});
