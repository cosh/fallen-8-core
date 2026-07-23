import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { InstanceConfig } from "../src/instances/types";
import type {
  EmbeddingSearchSpecification,
  PluginSpecification,
  StatusREST,
  VectorSearchResultREST,
} from "../src/api/types";
import { resetInstanceStoresForTests } from "../src/state/instanceStore";

/**
 * Embedding semantics across the split screens (features element-embeddings /
 * studio-semantics / index-workspace): bound-index create options and the bound badge +
 * content guard on the Indexes screen; semantic search by text on the Query screen.
 */

const getStatusMock = vi.fn<(i: InstanceConfig) => Promise<StatusREST | null>>();
const createIndexMock = vi.fn<(i: InstanceConfig, spec: PluginSpecification) => Promise<boolean | null>>();
const embeddingSearchMock =
  vi.fn<(i: InstanceConfig, spec: EmbeddingSearchSpecification) => Promise<VectorSearchResultREST | null>>();

vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    getStatus: (i: InstanceConfig) => getStatusMock(i),
    createIndex: (i: InstanceConfig, spec: PluginSpecification) => createIndexMock(i, spec),
    embeddingSearch: (i: InstanceConfig, spec: EmbeddingSearchSpecification) =>
      embeddingSearchMock(i, spec),
  };
});

import { QueryScreen } from "../src/screens/QueryScreen";
import { IndexesScreen } from "../src/screens/IndexesScreen";

const STATUS: StatusREST = {
  vertexCount: 0,
  edgeCount: 0,
  usedMemory: 0,
  indices: [
    { indexId: "raw", pluginType: "VectorIndex", capabilities: ["vector"] },
    {
      indexId: "emb",
      pluginType: "VectorIndex",
      embeddingName: "default",
      model: null,
      capabilities: ["vector"],
    },
  ],
  availableIndexPlugins: ["DictionaryIndex", "VectorIndex"],
  availablePathPlugins: [],
  availableAnalyticsPlugins: [],
  availableServicePlugins: [],
};

// Provider state rides /status (feature embedding-out-of-box), same mock as the inventory.
function statusWithProvider(enabled: boolean): StatusREST {
  return {
    ...STATUS,
    embedding: {
      enabled,
      backend: "Onnx",
      modelName: "bge-micro-v2",
      modelVersion: "",
      dimension: 4,
      intendedMetric: "Cosine",
      loaded: true,
    },
  };
}

function renderScreen(
  Screen: typeof QueryScreen | typeof IndexesScreen,
  providerEnabled?: boolean,
) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  if (providerEnabled !== undefined) {
    getStatusMock.mockResolvedValue(statusWithProvider(providerEnabled));
  }
  return render(
    <QueryClientProvider client={client}>
      <Screen />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  resetInstanceStoresForTests();
  localStorage.clear();
  getStatusMock.mockReset().mockResolvedValue(STATUS);
  createIndexMock.mockReset().mockResolvedValue(true);
  embeddingSearchMock.mockReset().mockResolvedValue({
    metric: "Cosine",
    higherIsBetter: true,
    results: [],
  });
});

describe("bound vector index (Indexes screen)", () => {
  it("shows a bound badge in the inventory", async () => {
    renderScreen(IndexesScreen);
    await waitFor(() => expect(screen.getByTestId("index-bound-emb")).toBeInTheDocument());
    expect(screen.getByTestId("index-bound-emb")).toHaveTextContent("bound:default");
  });

  it("create sends embeddingName/model as typed literals when set", async () => {
    const user = userEvent.setup();
    renderScreen(IndexesScreen);
    await waitFor(() => expect(screen.getByTestId("index-type").tagName).toBe("SELECT"));
    await user.selectOptions(screen.getByTestId("index-type"), "VectorIndex");
    await user.type(screen.getByLabelText(/index id/i), "bound2");
    await user.type(screen.getByTestId("vector-embedding-name"), "default");
    await user.click(screen.getByRole("button", { name: "Create" }));

    await waitFor(() => expect(createIndexMock).toHaveBeenCalledTimes(1));
    const options = createIndexMock.mock.calls[0][1].pluginOptions!;
    expect(options.embeddingName).toEqual({
      propertyId: "embeddingName",
      propertyValue: "default",
      fullQualifiedTypeName: "System.String",
    });
    expect(options.dimension.propertyValue).toBe("384");
    expect(options.model).toBeUndefined();
  });

  it("offers no content forms against a bound index, with the reason", async () => {
    const user = userEvent.setup();
    renderScreen(IndexesScreen);
    await waitFor(() => expect(screen.getByTestId("index-row-emb")).toBeInTheDocument());
    await user.click(screen.getByTestId("index-row-emb"));

    await waitFor(() =>
      expect(screen.getByTestId("bound-content-note")).toBeInTheDocument(),
    );
    expect(screen.queryByTestId("vector-add")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Add vector" })).not.toBeInTheDocument();
  });
});

describe("semantic search by text (Query screen)", () => {
  it("routes the vector query to /embedding/search when the source is text", async () => {
    const user = userEvent.setup();
    renderScreen(QueryScreen, true);
    await user.selectOptions(screen.getByTestId("query-mode"), "index");
    await waitFor(() => expect(screen.getByTestId("index-select")).toBeInTheDocument());
    await user.selectOptions(screen.getByTestId("index-select"), "emb");
    // vector is the index's only capability — the kNN form is active without a toggle.
    await user.click(screen.getByTestId("vector-source-text"));
    await user.type(screen.getByTestId("vector-search-text"), "red bicycles");
    await user.click(screen.getByTestId("scan-run"));

    await waitFor(() => expect(embeddingSearchMock).toHaveBeenCalledTimes(1));
    expect(embeddingSearchMock.mock.calls[0][1]).toEqual({
      indexId: "emb",
      text: "red bicycles",
      k: 10,
      kind: undefined,
      label: undefined,
    });
  });

  it("keeps the text source disabled when the provider is off", async () => {
    const user = userEvent.setup();
    renderScreen(QueryScreen, false);
    await user.selectOptions(screen.getByTestId("query-mode"), "index");
    await waitFor(() => expect(screen.getByTestId("index-select")).toBeInTheDocument());
    await user.selectOptions(screen.getByTestId("index-select"), "raw");
    expect(screen.getByTestId("vector-source-text")).toBeDisabled();
  });
});
