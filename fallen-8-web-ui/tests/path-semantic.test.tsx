import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { InstanceConfig } from "../src/instances/types";
import type {
  GraphStatisticsREST,
  PathREST,
  PathSpecification,
} from "../src/api/types";
import { resetInstanceStoresForTests } from "../src/state/instanceStore";

/**
 * Path screen semantic wiring (feature element-embeddings / studio-semantics): the
 * declarative block attaches to the request, and its minScore owns the vertex-filter slot.
 * Monaco is mocked to a textarea (the delegate slots pull it in transitively).
 */

vi.mock("../src/delegate/monacoSetup", () => ({ setupMonaco: () => {}, monaco: {} }));
vi.mock("@monaco-editor/react", () => ({
  default: ({ value }: { value: string }) => <textarea data-testid="mock-editor" value={value} readOnly />,
}));

const findPathsMock =
  vi.fn<(i: InstanceConfig, from: number, to: number, spec: PathSpecification) => Promise<PathREST[] | null>>();

vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    findPaths: (i: InstanceConfig, from: number, to: number, spec: PathSpecification) =>
      findPathsMock(i, from, to, spec),
  };
});

import { PathScreen } from "../src/screens/PathScreen";

function stats(enabled: boolean): GraphStatisticsREST {
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
      modelName: "m",
      modelVersion: "",
      dimension: 2,
      intendedMetric: "Cosine",
      loaded: true,
    },
  };
}

function renderScreen(providerEnabled = true) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(["local", "statistics"], stats(providerEnabled));
  return render(
    <QueryClientProvider client={client}>
      <PathScreen />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  resetInstanceStoresForTests();
  localStorage.clear();
  findPathsMock.mockReset().mockResolvedValue([]);
});

describe("path semantic block", () => {
  it("attaches the semantic spec to the request and owns the vertex-filter slot on minScore", async () => {
    const user = userEvent.setup();
    renderScreen(true);

    await user.type(screen.getByTestId("path-from"), "1");
    await user.type(screen.getByTestId("path-to"), "9");

    await user.click(screen.getByTestId("path-semantic-enable"));
    await user.type(screen.getByTestId("path-sem-vector"), "1, 0");
    await user.click(screen.getByTestId("path-sem-minscore-enable"));

    // Open the advanced (fragment) tier: the vertex-filter slot is now owned by minScore.
    await user.click(screen.getByTestId("toggle-advanced"));
    await waitFor(() =>
      expect(screen.getByTestId("slot-filter-vertexfilter-disabled")).toBeInTheDocument(),
    );

    await user.click(screen.getByTestId("path-run"));
    await waitFor(() => expect(findPathsMock).toHaveBeenCalledTimes(1));
    const spec = findPathsMock.mock.calls[0][3];
    expect(spec.semantic).toEqual({
      embeddingName: "default",
      metric: "Cosine",
      queryVector: [1, 0],
      minScore: 0.7,
    });
  });

  it("blocks the run when the semantic block is enabled but the vector is empty", async () => {
    const user = userEvent.setup();
    renderScreen(true);
    await user.type(screen.getByTestId("path-from"), "1");
    await user.type(screen.getByTestId("path-to"), "9");
    await user.click(screen.getByTestId("path-semantic-enable"));

    expect(screen.getByTestId("path-run")).toBeDisabled();
    expect(screen.getByTestId("path-sem-error")).toBeInTheDocument();
  });
});
