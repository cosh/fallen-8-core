import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { InstanceConfig } from "../src/instances/types";
import type { EdgeREST, VertexREST } from "../src/api/types";
import type { Neighborhood } from "../src/lib/neighborhood";

/**
 * NeighborhoodPreview (feature adjacency-preview): canvas model contents, focus-element
 * emphasis, click-to-inspect, caption and truncation badge. The canvas itself is mocked -
 * Sigma needs WebGL; the model/emphasis/select contract is what this component owns.
 */

const fetchVertexNeighborhoodMock =
  vi.fn<(i: InstanceConfig, id: number, o: unknown) => Promise<Neighborhood>>();
const fetchEdgeNeighborhoodMock =
  vi.fn<(i: InstanceConfig, e: EdgeREST, o: unknown) => Promise<Neighborhood>>();

vi.mock("../src/lib/neighborhood", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/lib/neighborhood")>();
  return {
    ...original,
    fetchVertexNeighborhood: (i: InstanceConfig, id: number, o: unknown) =>
      fetchVertexNeighborhoodMock(i, id, o),
    fetchEdgeNeighborhood: (i: InstanceConfig, e: EdgeREST, o: unknown) =>
      fetchEdgeNeighborhoodMock(i, e, o),
  };
});

// No importOriginal here: loading the real module pulls in Sigma, which needs WebGL
// at import time. GraphCanvas is the module's only runtime export.
vi.mock("../src/canvas/GraphCanvas", () => {
  return {
    GraphCanvas: ({
      nodes,
      edges,
      emphasis,
      onSelect,
    }: {
      nodes: Record<number, { id: number; label: string | null }>;
      edges: Record<number, { id: number }>;
      emphasis?: { nodeIds: readonly number[]; edgeIds: readonly number[] } | null;
      onSelect: (ref: { kind: "node" | "edge"; id: number } | null) => void;
    }) => (
      <div data-testid="mock-canvas" data-emphasis={JSON.stringify(emphasis)}>
        {Object.values(nodes).map((n) => (
          <button
            key={n.id}
            type="button"
            onClick={() => onSelect({ kind: "node", id: n.id })}
          >
            node-{n.id}
            {n.label ? `-${n.label}` : ""}
          </button>
        ))}
        {Object.values(edges).map((e) => (
          <button
            key={e.id}
            type="button"
            onClick={() => onSelect({ kind: "edge", id: e.id })}
          >
            edge-{e.id}
          </button>
        ))}
      </div>
    ),
  };
});

import { NeighborhoodPreview } from "../src/components/NeighborhoodPreview";

const VERTEX: VertexREST = {
  id: 42,
  creationDate: "c",
  modificationDate: "m",
  label: "person",
  kind: "vertex",
};

const EDGE: EdgeREST = {
  id: 10,
  creationDate: "c",
  modificationDate: "m",
  label: "knows",
  kind: "edge",
  sourceVertex: 1,
  targetVertex: 2,
};

function neighborhood(partial: Partial<Neighborhood>): Neighborhood {
  return { vertices: [], edges: [], truncated: false, ...partial };
}

function renderPreview(element: VertexREST | EdgeREST, onInspect = vi.fn()) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <NeighborhoodPreview element={element} onInspect={onInspect} />
    </QueryClientProvider>,
  );
  return onInspect;
}

beforeEach(() => {
  fetchVertexNeighborhoodMock.mockReset();
  fetchEdgeNeighborhoodMock.mockReset();
});

describe("vertex mode", () => {
  it("seeds the focus vertex, renders neighbors + edges, emphasizes the focus node", async () => {
    fetchVertexNeighborhoodMock.mockResolvedValue(
      neighborhood({
        vertices: [{ ...VERTEX, id: 7, label: "city" }],
        edges: [{ ...EDGE, sourceVertex: 42, targetVertex: 7, edgePropertyId: "knows" }],
      }),
    );
    renderPreview(VERTEX);

    // Focus vertex comes from the already-loaded element, not a re-fetch.
    expect(await screen.findByRole("button", { name: "node-42-person" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "node-7-city" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "edge-10" })).toBeInTheDocument();
    expect(screen.getByTestId("mock-canvas").dataset.emphasis).toBe(
      JSON.stringify({ nodeIds: [42], edgeIds: [] }),
    );
    expect(screen.getByTestId("preview-caption")).toHaveTextContent(
      "1 edge · click an element to inspect it",
    );
    expect(screen.queryByTestId("preview-truncation")).not.toBeInTheDocument();
  });

  it("navigates on node and edge clicks", async () => {
    const user = userEvent.setup();
    fetchVertexNeighborhoodMock.mockResolvedValue(
      neighborhood({
        vertices: [{ ...VERTEX, id: 7, label: null }],
        edges: [{ ...EDGE, sourceVertex: 42, targetVertex: 7 }],
      }),
    );
    const onInspect = renderPreview(VERTEX);

    await user.click(await screen.findByRole("button", { name: "node-7" }));
    expect(onInspect).toHaveBeenCalledWith(7);
    await user.click(screen.getByRole("button", { name: "edge-10" }));
    expect(onInspect).toHaveBeenCalledWith(10);
  });

  it("shows the truncation badge when the cap cut the neighborhood", async () => {
    fetchVertexNeighborhoodMock.mockResolvedValue(neighborhood({ truncated: true }));
    renderPreview(VERTEX);

    expect(await screen.findByTestId("preview-truncation")).toHaveTextContent(/first \d+ edges/);
  });

  it("surfaces a failed fetch as an error box", async () => {
    fetchVertexNeighborhoodMock.mockRejectedValue(new Error("boom"));
    renderPreview(VERTEX);

    expect(await screen.findByRole("alert")).toHaveTextContent("boom");
  });
});

describe("edge mode", () => {
  it("renders endpoints + parallel bundle, emphasizes the focus edge, states the count", async () => {
    fetchEdgeNeighborhoodMock.mockResolvedValue(
      neighborhood({
        vertices: [
          { ...VERTEX, id: 1, label: "a" },
          { ...VERTEX, id: 2, label: "b" },
        ],
        edges: [
          { ...EDGE, edgePropertyId: "knows" },
          { ...EDGE, id: 11, edgePropertyId: "knows" },
          { ...EDGE, id: 12, sourceVertex: 2, targetVertex: 1, edgePropertyId: "likes" },
        ],
      }),
    );
    renderPreview(EDGE);

    expect(await screen.findByRole("button", { name: "node-1-a" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "node-2-b" })).toBeInTheDocument();
    for (const id of [10, 11, 12]) {
      expect(screen.getByRole("button", { name: `edge-${id}` })).toBeInTheDocument();
    }
    expect(screen.getByTestId("mock-canvas").dataset.emphasis).toBe(
      JSON.stringify({ nodeIds: [], edgeIds: [10] }),
    );
    expect(screen.getByTestId("preview-caption")).toHaveTextContent(
      "3 edges between #1 and #2",
    );
  });

  it("navigates to an endpoint and to a sibling edge", async () => {
    const user = userEvent.setup();
    fetchEdgeNeighborhoodMock.mockResolvedValue(
      neighborhood({
        vertices: [
          { ...VERTEX, id: 1, label: null },
          { ...VERTEX, id: 2, label: null },
        ],
        edges: [
          { ...EDGE, edgePropertyId: null },
          { ...EDGE, id: 11, edgePropertyId: null },
        ],
      }),
    );
    const onInspect = renderPreview(EDGE);

    await user.click(await screen.findByRole("button", { name: "node-1" }));
    expect(onInspect).toHaveBeenCalledWith(1);
    await user.click(screen.getByRole("button", { name: "edge-11" }));
    expect(onInspect).toHaveBeenCalledWith(11);
  });

  it("still renders stub endpoints when the vertices failed to hydrate", async () => {
    fetchEdgeNeighborhoodMock.mockResolvedValue(
      neighborhood({ edges: [{ ...EDGE, edgePropertyId: null }] }),
    );
    renderPreview(EDGE);

    // buildCanvasModel stubs the endpoints so the edge can render.
    expect(await screen.findByRole("button", { name: "node-1" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "node-2" })).toBeInTheDocument();
  });
});
