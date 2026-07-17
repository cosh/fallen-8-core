import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { InstanceConfig } from "../src/instances/types";
import type {
  EmbeddingSearchSpecification,
  GraphStatisticsREST,
  PluginSpecification,
  StatusREST,
  VectorSearchResultREST,
} from "../src/api/types";

/**
 * Query screen semantics (feature element-embeddings / studio-semantics): bound-index
 * create options, the bound badge + add-vector guard, and semantic search by text.
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

const STATUS: StatusREST = {
  vertexCount: 0,
  edgeCount: 0,
  usedMemory: 0,
  indices: [
    { indexId: "raw", pluginType: "VectorIndex" },
    { indexId: "emb", pluginType: "VectorIndex", embeddingName: "default", model: null },
  ],
  availableIndexPlugins: ["DictionaryIndex", "VectorIndex"],
  availablePathPlugins: [],
  availableAnalyticsPlugins: [],
  availableServicePlugins: [],
};

function statsWithProvider(enabled: boolean): GraphStatisticsREST {
  return {
    vertexCount: 0,
    edgeCount: 0,
    vertexLabels: { top: [], distinctTotal: 0 },
    edgeLabels: { top: [], distinctTotal: 0 },
    inDegree: { min: 0, max: 0, mean: 0, p50: 0, p90: 0, p99: 0 },
    outDegree: { min: 0, max: 0, mean: 0, p50: 0, p90: 0, p99: 0 },
    totalDegree: { min: 0, max: 0, mean: 0, p50: 0, p90: 0, p99: 0 },
    propertyKeys: { top: [], distinctTotal: 0 },
    indices: [],
    memory: { processWorkingSetBytes: 0, gcHeapBytes: 0, gcLastHeapSizeBytes: 0, gcFragmentedBytes: 0 },
    computedInMs: 1,
    sampled: false,
    sampleStride: 1,
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

function renderScreen(providerEnabled?: boolean) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  if (providerEnabled !== undefined) {
    client.setQueryData(["local", "statistics"], statsWithProvider(providerEnabled));
  }
  return render(
    <QueryClientProvider client={client}>
      <QueryScreen />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  getStatusMock.mockReset().mockResolvedValue(STATUS);
  createIndexMock.mockReset().mockResolvedValue(true);
  embeddingSearchMock.mockReset().mockResolvedValue({
    metric: "Cosine",
    higherIsBetter: true,
    results: [],
  });
});

describe("bound vector index", () => {
  it("shows a bound badge in the inventory", async () => {
    renderScreen();
    await waitFor(() => expect(screen.getByTestId("index-bound-emb")).toBeInTheDocument());
    expect(screen.getByTestId("index-bound-emb")).toHaveTextContent("bound:default");
  });

  it("create sends embeddingName/model as typed literals when set", async () => {
    const user = userEvent.setup();
    renderScreen();
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

  it("disables add-vector against a bound index with a reason", async () => {
    const user = userEvent.setup();
    renderScreen();
    await waitFor(() => expect(screen.getByTestId("index-type").tagName).toBe("SELECT"));
    await user.type(screen.getByLabelText(/index id/i), "emb");
    await user.click(screen.getByTestId("toggle-vector-add"));
    await user.type(screen.getByLabelText(/element id/i), "1");

    expect(screen.getByTestId("bound-add-note")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Add vector" })).toBeDisabled();
  });
});

describe("semantic search by text", () => {
  it("routes the vector scan to /embedding/search when the source is text", async () => {
    const user = userEvent.setup();
    const { container } = renderScreen(true);
    await user.selectOptions(screen.getByTestId("scan-kind"), "vector");
    await user.click(screen.getByTestId("vector-source-text"));
    // Two "index id" fields exist once the vector scan renders (scan + index mgmt); target
    // the scan form's input by id.
    await user.type(container.querySelector("#scan-index")!, "emb");
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
    renderScreen(false);
    await user.selectOptions(screen.getByTestId("scan-kind"), "vector");
    expect(screen.getByTestId("vector-source-text")).toBeDisabled();
  });
});
