import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { InstanceConfig } from "../src/instances/types";
import type { EdgeREST, StatusREST, VertexREST } from "../src/api/types";

/**
 * Browser screen non-embedding surface (feature structural-decomposition, Phase 3
 * pinning): ElementDetail header/tabs, PropertiesTab plain-property display, and the
 * AdjacencyPanel degree + in/out edge listing — pinned BEFORE these inner components move
 * out of the screen file. The Embeddings tab is pinned in embedding-browser.test.tsx.
 */

const getGraphElementMock =
  vi.fn<(i: InstanceConfig, id: number, signal?: AbortSignal) => Promise<VertexREST | EdgeREST | null>>();
const getVertexMock =
  vi.fn<(i: InstanceConfig, id: number, signal?: AbortSignal) => Promise<VertexREST | null>>();
const getEdgeMock =
  vi.fn<(i: InstanceConfig, id: number, signal?: AbortSignal) => Promise<EdgeREST | null>>();
const getStatusMock =
  vi.fn<(i: InstanceConfig, signal?: AbortSignal) => Promise<StatusREST | null>>();
const getOutEdgePropertiesMock = vi.fn<(i: InstanceConfig, id: number) => Promise<string[] | null>>();
const getInEdgePropertiesMock = vi.fn<(i: InstanceConfig, id: number) => Promise<string[] | null>>();
const getOutEdgesMock =
  vi.fn<(i: InstanceConfig, id: number, edgePropertyId: string) => Promise<number[] | null>>();
const getInEdgesMock =
  vi.fn<(i: InstanceConfig, id: number, edgePropertyId: string) => Promise<number[] | null>>();
const getInDegreeMock = vi.fn<(i: InstanceConfig, id: number) => Promise<number | null>>();
const getOutDegreeMock = vi.fn<(i: InstanceConfig, id: number) => Promise<number | null>>();

vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    getGraphElement: (i: InstanceConfig, id: number, s?: AbortSignal) =>
      getGraphElementMock(i, id, s),
    getVertex: (i: InstanceConfig, id: number, s?: AbortSignal) => getVertexMock(i, id, s),
    getEdge: (i: InstanceConfig, id: number, s?: AbortSignal) => getEdgeMock(i, id, s),
    getStatus: (i: InstanceConfig, s?: AbortSignal) => getStatusMock(i, s),
    getOutEdgeProperties: (i: InstanceConfig, id: number) => getOutEdgePropertiesMock(i, id),
    getInEdgeProperties: (i: InstanceConfig, id: number) => getInEdgePropertiesMock(i, id),
    getOutEdges: (i: InstanceConfig, id: number, prop: string) => getOutEdgesMock(i, id, prop),
    getInEdges: (i: InstanceConfig, id: number, prop: string) => getInEdgesMock(i, id, prop),
    getInDegree: (i: InstanceConfig, id: number) => getInDegreeMock(i, id),
    getOutDegree: (i: InstanceConfig, id: number) => getOutDegreeMock(i, id),
  };
});

// Sigma needs WebGL at import time; the preview's model/select contract is pinned in
// neighborhood-preview.test.tsx - here only the screen wiring matters. GraphCanvas is
// the module's only runtime export, so no importOriginal.
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

import { BrowserScreen } from "../src/screens/BrowserScreen";

const STATUS: StatusREST = {
  vertexCount: 1,
  edgeCount: 1,
  usedMemory: 0,
  indices: [],
  availableIndexPlugins: [],
  availablePathPlugins: [],
  availableAnalyticsPlugins: [],
  availableServicePlugins: [],
};

const VERTEX: VertexREST = {
  id: 42,
  creationDate: "2026-01-01T10:00:00",
  modificationDate: "2026-02-02T11:00:00",
  label: "person",
  kind: "vertex",
  properties: [
    { propertyId: "name", propertyValue: "Alice", fullQualifiedTypeName: "System.String" },
    { propertyId: "age", propertyValue: 30, fullQualifiedTypeName: "System.Int32" },
    { propertyId: "untyped", propertyValue: "x" },
  ],
};

const EDGE: EdgeREST = {
  id: 7,
  creationDate: "2026-01-01",
  modificationDate: "2026-01-01",
  label: "knows",
  kind: "edge",
  sourceVertex: 1,
  targetVertex: 2,
  properties: [],
};

function renderScreen() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <BrowserScreen />
    </QueryClientProvider>,
  );
}

async function lookUp(user: ReturnType<typeof userEvent.setup>, id: string) {
  await user.type(screen.getByTestId("lookup-id"), id);
  await user.click(screen.getByTestId("lookup-go"));
}

beforeEach(() => {
  getGraphElementMock.mockReset().mockResolvedValue(VERTEX);
  getVertexMock.mockReset().mockResolvedValue(VERTEX);
  getEdgeMock.mockReset().mockResolvedValue(EDGE);
  getStatusMock.mockReset().mockResolvedValue(STATUS);
  getOutEdgePropertiesMock.mockReset().mockResolvedValue([]);
  getInEdgePropertiesMock.mockReset().mockResolvedValue([]);
  getOutEdgesMock.mockReset().mockResolvedValue([]);
  getInEdgesMock.mockReset().mockResolvedValue([]);
  getInDegreeMock.mockReset().mockResolvedValue(0);
  getOutDegreeMock.mockReset().mockResolvedValue(0);
});

describe("element detail", () => {
  it("renders a vertex header with label, dates, and the properties tab by default", async () => {
    const user = userEvent.setup();
    renderScreen();
    await lookUp(user, "42");

    await waitFor(() => expect(screen.getByText("vertex #42")).toBeInTheDocument());
    expect(screen.getByText("person")).toBeInTheDocument();
    expect(
      screen.getByText(/created 2026-01-01T10:00:00 · modified 2026-02-02T11:00:00/),
    ).toBeInTheDocument();
    expect(screen.getByTestId("properties-tab")).toBeInTheDocument();
    expect(screen.queryByTestId("embeddings-tab")).not.toBeInTheDocument();
  });

  it("renders an edge header with endpoint links and the neighborhood panel", async () => {
    const user = userEvent.setup();
    getGraphElementMock.mockResolvedValue(EDGE);
    renderScreen();
    await lookUp(user, "7");

    await waitFor(() => expect(screen.getByText("edge #7")).toBeInTheDocument());
    expect(screen.getByText(/endpoints/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "#1" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "#2" })).toBeInTheDocument();
    // An edge gets the rendered neighborhood, never the vertex adjacency stats.
    expect(screen.getByText("neighborhood")).toBeInTheDocument();
    expect(await screen.findByTestId("mock-canvas")).toBeInTheDocument();
    expect(screen.queryByTestId("degrees")).not.toBeInTheDocument();
    expect(getInDegreeMock).not.toHaveBeenCalled();
  });

  it("navigates to an endpoint via its link — an in-screen lookup, not a dead URL", async () => {
    const user = userEvent.setup();
    getGraphElementMock.mockResolvedValue(EDGE);
    renderScreen();
    await lookUp(user, "7");
    await waitFor(() => expect(screen.getByText("edge #7")).toBeInTheDocument());

    getGraphElementMock.mockResolvedValue(VERTEX);
    await user.click(screen.getByRole("button", { name: "#1" }));

    expect(getGraphElementMock).toHaveBeenCalledWith(expect.anything(), 1, undefined);
    await waitFor(() => expect(screen.getByText("vertex #42")).toBeInTheDocument());
    expect(screen.getByTestId("lookup-id")).toHaveValue("1");
  });

  it("routes the lookup kind to the matching endpoint", async () => {
    const user = userEvent.setup();
    renderScreen();

    await user.selectOptions(screen.getByLabelText("kind"), "vertex");
    await lookUp(user, "42");
    await waitFor(() => expect(getVertexMock).toHaveBeenCalledTimes(1));
    expect(getGraphElementMock).not.toHaveBeenCalled();

    await user.selectOptions(screen.getByLabelText("kind"), "edge");
    await user.clear(screen.getByTestId("lookup-id"));
    await lookUp(user, "7");
    await waitFor(() => expect(getEdgeMock).toHaveBeenCalledTimes(1));
    expect(getGraphElementMock).not.toHaveBeenCalled();
  });

  it("states a missing element instead of erroring", async () => {
    const user = userEvent.setup();
    getGraphElementMock.mockResolvedValue(null);
    renderScreen();
    await lookUp(user, "999");

    await waitFor(() =>
      expect(
        screen.getByText(/No element with that id \(missing = empty, not an error\)\./),
      ).toBeInTheDocument(),
    );
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });
});

describe("properties tab", () => {
  it("lists each property with value and type, dash for a missing type", async () => {
    const user = userEvent.setup();
    renderScreen();
    await lookUp(user, "42");

    const tab = await screen.findByTestId("properties-tab");
    const nameRow = within(tab).getByText("name").closest("tr")!;
    expect(within(nameRow).getByText("Alice")).toBeInTheDocument();
    expect(within(nameRow).getByText("System.String")).toBeInTheDocument();
    const ageRow = within(tab).getByText("age").closest("tr")!;
    expect(within(ageRow).getByText("30")).toBeInTheDocument();
    expect(within(ageRow).getByText("System.Int32")).toBeInTheDocument();
    const untypedRow = within(tab).getByText("untyped").closest("tr")!;
    expect(within(untypedRow).getByText("—")).toBeInTheDocument();
    // No reserved embedding properties on this element — no reveal toggle either.
    expect(screen.queryByTestId("show-reserved")).not.toBeInTheDocument();
  });

  it("states 'no properties' for a bare element", async () => {
    const user = userEvent.setup();
    getGraphElementMock.mockResolvedValue({ ...VERTEX, properties: [] });
    renderScreen();
    await lookUp(user, "42");

    const tab = await screen.findByTestId("properties-tab");
    expect(within(tab).getByText("no properties")).toBeInTheDocument();
  });
});

describe("adjacency panel", () => {
  it("defaults to the rendered graph view with the focus vertex on the canvas", async () => {
    const user = userEvent.setup();
    renderScreen();
    await lookUp(user, "42");

    expect(await screen.findByTestId("mock-canvas")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "node-42" })).toBeInTheDocument();
    // The degree line stays visible above the preview.
    await waitFor(() =>
      expect(screen.getByTestId("degrees")).toHaveTextContent("degree 0 · in 0 · out 0"),
    );
  });

  it("navigates when a preview element is clicked", async () => {
    const user = userEvent.setup();
    getOutEdgePropertiesMock.mockResolvedValue(["knows"]);
    getOutEdgesMock.mockResolvedValue([7]);
    getEdgeMock.mockResolvedValue(EDGE);
    renderScreen();
    await lookUp(user, "42");

    await user.click(await screen.findByRole("button", { name: "node-2" }));

    expect(getGraphElementMock).toHaveBeenCalledWith(expect.anything(), 2, undefined);
    expect(screen.getByTestId("lookup-id")).toHaveValue("2");
  });

  it("keeps the current element and preview mounted while a hop is in flight", async () => {
    const user = userEvent.setup();
    getOutEdgePropertiesMock.mockResolvedValue(["knows"]);
    getOutEdgesMock.mockResolvedValue([7]);
    renderScreen();
    await lookUp(user, "42");
    await screen.findByRole("button", { name: "node-2" });

    let resolveHop!: (v: VertexREST) => void;
    getGraphElementMock.mockImplementation((_i, id) =>
      id === 2
        ? new Promise((resolve) => {
            resolveHop = resolve;
          })
        : Promise.resolve(VERTEX),
    );
    await user.click(screen.getByRole("button", { name: "node-2" }));

    // No unmount, no flicker: the previous element and its preview stay visible.
    expect(screen.getByText("vertex #42")).toBeInTheDocument();
    expect(screen.getByTestId("mock-canvas")).toBeInTheDocument();

    resolveHop({ ...VERTEX, id: 2 });
    await waitFor(() => expect(screen.getByText("vertex #2")).toBeInTheDocument());
  });

  it("does not reload anything when the shown element is clicked again", async () => {
    const user = userEvent.setup();
    renderScreen();
    await lookUp(user, "42");
    const focusNode = await screen.findByRole("button", { name: "node-42" });

    const lookupsBefore = getGraphElementMock.mock.calls.length;
    await user.click(focusNode);

    expect(getGraphElementMock.mock.calls.length).toBe(lookupsBefore);
    expect(screen.getByText("vertex #42")).toBeInTheDocument();
  });

  it("switches between the graph and stats views", async () => {
    const user = userEvent.setup();
    getOutEdgePropertiesMock.mockResolvedValue(["knows"]);
    renderScreen();
    await lookUp(user, "42");
    await screen.findByTestId("mock-canvas");

    await user.click(screen.getByTestId("adjacency-view-stats"));
    expect(screen.queryByTestId("mock-canvas")).not.toBeInTheDocument();
    expect(await screen.findByRole("button", { name: "knows" })).toBeInTheDocument();

    await user.click(screen.getByTestId("adjacency-view-graph"));
    expect(await screen.findByTestId("mock-canvas")).toBeInTheDocument();
  });

  it("shows the combined degree line and the out/in edge-property buttons", async () => {
    const user = userEvent.setup();
    getInDegreeMock.mockResolvedValue(2);
    getOutDegreeMock.mockResolvedValue(3);
    getOutEdgePropertiesMock.mockResolvedValue(["knows", "likes"]);
    getInEdgePropertiesMock.mockResolvedValue([]);
    renderScreen();
    await lookUp(user, "42");
    await user.click(await screen.findByTestId("adjacency-view-stats"));

    await waitFor(() =>
      expect(screen.getByTestId("degrees")).toHaveTextContent("degree 5 · in 2 · out 3"),
    );
    expect(screen.getByRole("button", { name: "knows" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "likes" })).toBeInTheDocument();
    // No incoming edge properties — stated as "none".
    expect(screen.getByText("none")).toBeInTheDocument();
    expect(getOutEdgePropertiesMock).toHaveBeenCalledWith(expect.anything(), 42);
    expect(getInEdgePropertiesMock).toHaveBeenCalledWith(expect.anything(), 42);
  });

  it("expands an outgoing property into its edge-id links, then an incoming one", async () => {
    const user = userEvent.setup();
    getOutEdgePropertiesMock.mockResolvedValue(["knows"]);
    getInEdgePropertiesMock.mockResolvedValue(["follows"]);
    getOutEdgesMock.mockResolvedValue([7, 9]);
    getInEdgesMock.mockResolvedValue([13]);
    renderScreen();
    await lookUp(user, "42");
    await user.click(await screen.findByTestId("adjacency-view-stats"));

    await user.click(await screen.findByRole("button", { name: "knows" }));
    await waitFor(() => expect(getOutEdgesMock).toHaveBeenCalledWith(expect.anything(), 42, "knows"));
    expect(screen.getByText(/out · knows edge ids/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "#7" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "#9" })).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "follows" }));
    await waitFor(() => expect(getInEdgesMock).toHaveBeenCalledWith(expect.anything(), 42, "follows"));
    expect(screen.getByText(/in · follows edge ids/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "#13" })).toBeInTheDocument();
    // The expansion is exclusive: the previous out-listing is replaced.
    expect(screen.queryByRole("button", { name: "#7" })).not.toBeInTheDocument();
  });

  it("states an empty expansion as 'none'", async () => {
    const user = userEvent.setup();
    getOutEdgePropertiesMock.mockResolvedValue(["knows"]);
    getInEdgePropertiesMock.mockResolvedValue(["follows"]);
    getOutEdgesMock.mockResolvedValue([]);
    renderScreen();
    await lookUp(user, "42");
    await user.click(await screen.findByTestId("adjacency-view-stats"));

    await user.click(await screen.findByRole("button", { name: "knows" }));
    await waitFor(() => expect(screen.getByText(/out · knows edge ids/)).toBeInTheDocument());
    const expansion = screen.getByText(/out · knows edge ids/).parentElement!;
    expect(within(expansion).getByText("none")).toBeInTheDocument();
  });
});
