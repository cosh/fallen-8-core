import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { InstanceConfig } from "../src/instances/types";
import type {
  EmbeddingWriteSpecification,
  EmbedElementSpecification,
  StatusREST,
  VertexREST,
} from "../src/api/types";

/**
 * Browser Embeddings tab (feature element-embeddings / studio-semantics): lists named
 * embeddings folded out of the property table, sets one by pasted vector, removes one,
 * and gates the text-in path on the embedding provider.
 */

const getGraphElementMock =
  vi.fn<(i: InstanceConfig, id: number) => Promise<VertexREST | null>>();
const putEmbeddingMock =
  vi.fn<
    (i: InstanceConfig, id: number, name: string, spec: EmbeddingWriteSpecification) => Promise<void>
  >();
const deleteEmbeddingMock =
  vi.fn<(i: InstanceConfig, id: number, name: string) => Promise<void>>();
const embedElementMock =
  vi.fn<(i: InstanceConfig, spec: EmbedElementSpecification) => Promise<boolean | null>>();

vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    getGraphElement: (i: InstanceConfig, id: number) => getGraphElementMock(i, id),
    getVertex: (i: InstanceConfig, id: number) => getGraphElementMock(i, id),
    getInEdgeProperties: async () => [],
    getOutEdgeProperties: async () => [],
    getInDegree: async () => 0,
    getOutDegree: async () => 0,
    putElementEmbedding: (
      i: InstanceConfig,
      id: number,
      name: string,
      spec: EmbeddingWriteSpecification,
    ) => putEmbeddingMock(i, id, name, spec),
    deleteElementEmbedding: (i: InstanceConfig, id: number, name: string) =>
      deleteEmbeddingMock(i, id, name),
    embedElement: (i: InstanceConfig, spec: EmbedElementSpecification) => embedElementMock(i, spec),
  };
});

import { BrowserScreen } from "../src/screens/BrowserScreen";

const ELEMENT: VertexREST = {
  id: 42,
  creationDate: "2026-01-01",
  modificationDate: "2026-01-01",
  label: "doc",
  kind: "vertex",
  properties: [
    { propertyId: "title", propertyValue: "Bicycles", fullQualifiedTypeName: "System.String" },
    {
      propertyId: "$embedding:default",
      propertyValue: [0.1, 0.2, 0.3, 0.4],
      fullQualifiedTypeName: "System.Single[]",
    },
    {
      propertyId: "$embeddingModel:default",
      propertyValue: "bge-micro-v2#4#Cosine",
      fullQualifiedTypeName: "System.String",
    },
  ],
};

// Provider state rides the cheap /status surface (feature embedding-out-of-box).
function statusWithProvider(enabled: boolean): StatusREST {
  return {
    vertexCount: 1,
    edgeCount: 0,
    usedMemory: 0,
    indices: [],
    availableIndexPlugins: [],
    availablePathPlugins: [],
    availableAnalyticsPlugins: [],
    availableServicePlugins: [],
    embedding: {
      enabled,
      backend: "Onnx",
      modelName: "bge-micro-v2",
      modelVersion: "",
      dimension: 4,
      intendedMetric: "Cosine",
      loaded: false,
    },
  };
}

function renderScreen(providerEnabled: boolean | null) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  if (providerEnabled !== null) {
    client.setQueryData(["local", "status"], statusWithProvider(providerEnabled));
  }
  return render(
    <QueryClientProvider client={client}>
      <BrowserScreen />
    </QueryClientProvider>,
  );
}

async function lookUp42(user: ReturnType<typeof userEvent.setup>) {
  await user.type(screen.getByTestId("lookup-id"), "42");
  await user.click(screen.getByTestId("lookup-go"));
  await waitFor(() => expect(screen.getByTestId("element-tab-embeddings")).toBeInTheDocument());
}

beforeEach(() => {
  getGraphElementMock.mockReset().mockResolvedValue(ELEMENT);
  putEmbeddingMock.mockReset().mockResolvedValue(undefined);
  deleteEmbeddingMock.mockReset().mockResolvedValue(undefined);
  embedElementMock.mockReset().mockResolvedValue(true);
});

describe("Browser embeddings tab", () => {
  it("folds reserved embedding properties out of the property table, reveals on toggle", async () => {
    const user = userEvent.setup();
    renderScreen(true);
    await lookUp42(user);

    // Properties tab is default: the plain 'title' shows, the reserved keys are hidden.
    const propertiesTab = screen.getByTestId("properties-tab");
    expect(within(propertiesTab).getByText("title")).toBeInTheDocument();
    expect(within(propertiesTab).queryByText("$embedding:default")).not.toBeInTheDocument();

    await user.click(screen.getByTestId("show-reserved"));
    expect(within(propertiesTab).getByText("$embedding:default")).toBeInTheDocument();
  });

  it("lists named embeddings with their model stamp", async () => {
    const user = userEvent.setup();
    renderScreen(true);
    await lookUp42(user);
    await user.click(screen.getByTestId("element-tab-embeddings"));

    const row = screen.getByTestId("embedding-row-default");
    expect(within(row).getByText("default")).toBeInTheDocument();
    expect(within(row).getByText(/d=4/)).toBeInTheDocument();
    expect(within(row).getByText("bge-micro-v2#4#Cosine")).toBeInTheDocument();
  });

  it("sets an embedding from a pasted vector and refreshes", async () => {
    const user = userEvent.setup();
    renderScreen(true);
    await lookUp42(user);
    await user.click(screen.getByTestId("element-tab-embeddings"));

    await user.clear(screen.getByTestId("emb-name"));
    await user.type(screen.getByTestId("emb-name"), "title");
    // Comma-separated (parseVector accepts it) — userEvent.type treats [ ] as key modifiers.
    await user.type(screen.getByTestId("emb-vector"), "1, 0, 0, 0");
    await user.click(screen.getByTestId("emb-write"));

    await waitFor(() => expect(putEmbeddingMock).toHaveBeenCalledTimes(1));
    expect(putEmbeddingMock.mock.calls[0][1]).toBe(42);
    expect(putEmbeddingMock.mock.calls[0][2]).toBe("title");
    expect(putEmbeddingMock.mock.calls[0][3]).toEqual({ vector: [1, 0, 0, 0] });
    // onRefresh re-fetched the element.
    expect(getGraphElementMock.mock.calls.length).toBeGreaterThan(1);
  });

  it("removes an embedding", async () => {
    const user = userEvent.setup();
    renderScreen(true);
    await lookUp42(user);
    await user.click(screen.getByTestId("element-tab-embeddings"));
    await user.click(screen.getByTestId("embedding-remove-default"));

    await waitFor(() => expect(deleteEmbeddingMock).toHaveBeenCalledTimes(1));
    expect(deleteEmbeddingMock.mock.calls[0][2]).toBe("default");
  });

  it("disables text-in embedding when the provider is off", async () => {
    const user = userEvent.setup();
    renderScreen(false);
    await lookUp42(user);
    await user.click(screen.getByTestId("element-tab-embeddings"));
    await user.click(screen.getByTestId("emb-source-text"));

    expect(screen.getByTestId("emb-text")).toBeDisabled();
    expect(screen.getByTestId("emb-text-unavailable")).toHaveTextContent(/provider is off/i);
    expect(embedElementMock).not.toHaveBeenCalled();
  });
});
