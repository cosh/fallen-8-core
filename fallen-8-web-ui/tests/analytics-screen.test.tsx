import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { InstanceConfig } from "../src/instances/types";
import type {
  AnalyticsResultREST,
  AnalyticsSpecification,
  GraphStatisticsREST,
  PartitionMembersREST,
  VertexREST,
} from "../src/api/types";
import { ApiError } from "../src/api/client";
import { resetInstanceStoresForTests } from "../src/state/instanceStore";

/**
 * Analytics screen (feature structural-decomposition, Phase 3 pinning): the AnalyticsRunner
 * happy path (request shape + rendered result), the describeRunError 429/408 mapping, the
 * write-back confirm gate, partition-member paging, and the GraphShapePanel render — pinned
 * BEFORE AnalyticsRunner/GraphShapePanel move out of the screen file.
 */

const listAlgorithmsMock =
  vi.fn<(i: InstanceConfig, signal?: AbortSignal) => Promise<Record<string, string> | null>>();
const runAnalyticsMock =
  vi.fn<(i: InstanceConfig, name: string, spec: AnalyticsSpecification) => Promise<AnalyticsResultREST | null>>();
const partitionMembersMock =
  vi.fn<
    (i: InstanceConfig, name: string, partitionId: number, spec: AnalyticsSpecification) => Promise<PartitionMembersREST | null>
  >();
const getStatisticsMock =
  vi.fn<(i: InstanceConfig, signal?: AbortSignal) => Promise<GraphStatisticsREST | null>>();
const getGraphElementMock =
  vi.fn<(i: InstanceConfig, id: number, signal?: AbortSignal) => Promise<VertexREST | null>>();

vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    listAnalyticsAlgorithms: (i: InstanceConfig, signal?: AbortSignal) =>
      listAlgorithmsMock(i, signal),
    runAnalytics: (i: InstanceConfig, name: string, spec: AnalyticsSpecification) =>
      runAnalyticsMock(i, name, spec),
    getPartitionMembers: (
      i: InstanceConfig,
      name: string,
      partitionId: number,
      spec: AnalyticsSpecification,
    ) => partitionMembersMock(i, name, partitionId, spec),
    getStatistics: (i: InstanceConfig, signal?: AbortSignal) => getStatisticsMock(i, signal),
    getGraphElement: (i: InstanceConfig, id: number, signal?: AbortSignal) =>
      getGraphElementMock(i, id, signal),
  };
});

import { AnalyticsScreen } from "../src/screens/AnalyticsScreen";

const ALGORITHMS: Record<string, string> = {
  ConnectedComponents: "Finds weakly connected components.",
  DegreeCentrality: "Scores vertices by degree.",
  PageRank: "Iterative PageRank over the graph.",
};

function vertex(id: number, label: string): VertexREST {
  return {
    id,
    creationDate: "2026-01-01",
    modificationDate: "2026-01-01",
    label,
    kind: "vertex",
    properties: [],
  };
}

/** All locale-formatted numbers stay < 1000 so toLocaleString is separator-free. */
function resultWith(overrides: Partial<AnalyticsResultREST>): AnalyticsResultREST {
  return {
    algorithm: "DegreeCentrality",
    converged: true,
    iterationsRun: 1,
    elapsedMs: 12,
    budgetExhausted: false,
    vertexCount: 200,
    statistics: null,
    results: null,
    partitions: null,
    writeBack: null,
    ...overrides,
  };
}

const SHAPE: GraphStatisticsREST = {
  vertexCount: 999,
  edgeCount: 314,
  vertexLabels: {
    top: [
      { name: "person", count: 800 },
      { name: "doc", count: 199 },
    ],
    distinctTotal: 2,
  },
  edgeLabels: { top: [{ name: "knows", count: 27 }], distinctTotal: 1 },
  inDegree: { min: 0, max: 42, mean: 4.62, p50: 3, p90: 9, p99: 21 },
  outDegree: { min: 1, max: 17, mean: 2.13, p50: 2, p90: 5, p99: 11 },
  totalDegree: { min: 1, max: 59, mean: 6.6, p50: 5, p90: 14, p99: 32 },
  propertyKeys: { top: [{ name: "age", count: 700 }], distinctTotal: 1 },
  indices: [{ name: "nameIndex", type: "DictionaryIndex", keys: 10, values: 100 }],
  memory: {
    processWorkingSetBytes: 0,
    gcHeapBytes: 0,
    gcLastHeapSizeBytes: 0,
    gcFragmentedBytes: 0,
  },
  computedInMs: 12.4,
  sampled: false,
  sampleStride: 1,
};

function renderScreen() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <AnalyticsScreen />
    </QueryClientProvider>,
  );
}

/** The algorithm dropdown fills once /analytics/algorithms resolves. */
async function pickAlgorithm(user: ReturnType<typeof userEvent.setup>, name: string) {
  await waitFor(() =>
    expect(within(screen.getByTestId("algo-name")).getByText(name)).toBeInTheDocument(),
  );
  await user.selectOptions(screen.getByTestId("algo-name"), name);
}

beforeEach(() => {
  // AnalyticsRunner now persists its input draft in the memoized per-instance store; reset
  // it so a prior test's algorithm/label/write-back can't leak into the next.
  resetInstanceStoresForTests();
  localStorage.clear();
  listAlgorithmsMock.mockReset().mockResolvedValue(ALGORITHMS);
  runAnalyticsMock.mockReset().mockResolvedValue(resultWith({}));
  partitionMembersMock.mockReset().mockResolvedValue(null);
  getStatisticsMock.mockReset().mockResolvedValue(SHAPE);
  getGraphElementMock.mockReset().mockResolvedValue(null);
});

describe("analytics runner", () => {
  it("runs the picked algorithm with the form's request shape and renders the scored result", async () => {
    const user = userEvent.setup();
    runAnalyticsMock.mockResolvedValue(
      resultWith({
        iterationsRun: 3,
        statistics: { components: 3, modularity: 0.4321 },
        results: [
          { graphElementId: 1, score: 0.9 },
          { graphElementId: 2, score: 0.25 },
        ],
      }),
    );
    getGraphElementMock.mockImplementation(async (_i, id) =>
      id === 1 ? vertex(1, "alice") : vertex(2, "bob"),
    );
    renderScreen();

    await pickAlgorithm(user, "DegreeCentrality");
    // Picking an algorithm surfaces its server-provided description.
    expect(screen.getByTestId("algo-description")).toHaveTextContent(
      "Scores vertices by degree.",
    );
    await user.type(screen.getByLabelText("vertex label"), "person");
    await user.selectOptions(screen.getByLabelText("direction"), "out");
    await user.click(screen.getByTestId("algo-run"));

    await waitFor(() => expect(runAnalyticsMock).toHaveBeenCalledTimes(1));
    expect(runAnalyticsMock.mock.calls[0][1]).toBe("DegreeCentrality");
    // Exact wire shape: empty optional fields are undefined (dropped by JSON), the
    // default max results of 100 travels, PageRank-only knobs are absent.
    expect(runAnalyticsMock.mock.calls[0][2]).toEqual({
      vertexLabel: "person",
      direction: "out",
      maxResults: 100,
    });

    // Result header + statistics grid + hydrated top-K table with the score column.
    const result = await screen.findByTestId("analytics-result");
    expect(within(result).getByText(/Result — DegreeCentrality/)).toBeInTheDocument();
    expect(within(result).getByText("converged")).toBeInTheDocument();
    expect(within(result).getByText(/iterations 3/)).toBeInTheDocument();
    expect(within(result).getByText("3")).toBeInTheDocument(); // integer statistic
    expect(within(result).getByText("0.4321")).toBeInTheDocument(); // float statistic
    await waitFor(() => expect(within(result).getByText("alice")).toBeInTheDocument());
    expect(within(result).getByText("bob")).toBeInTheDocument();
    expect(within(result).getByText("top-2 scored")).toBeInTheDocument();
    expect(within(result).getByText("0.9000")).toBeInTheDocument();
    expect(within(result).getByText("0.2500")).toBeInTheDocument();
  });

  it("keeps Run disabled until an algorithm is picked", async () => {
    renderScreen();
    await waitFor(() => expect(listAlgorithmsMock).toHaveBeenCalled());
    expect(screen.getByTestId("algo-run")).toBeDisabled();
    expect(runAnalyticsMock).not.toHaveBeenCalled();
  });

  it("offers DampingFactor/epsilon only for PageRank and sends them typed", async () => {
    const user = userEvent.setup();
    renderScreen();

    await pickAlgorithm(user, "DegreeCentrality");
    expect(screen.queryByLabelText("DampingFactor")).not.toBeInTheDocument();
    expect(screen.queryByLabelText("epsilon")).not.toBeInTheDocument();

    await user.selectOptions(screen.getByTestId("algo-name"), "PageRank");
    await user.type(screen.getByLabelText("DampingFactor"), "0.9");
    await user.type(screen.getByLabelText("epsilon"), "0.001");
    await user.click(screen.getByTestId("algo-run"));

    await waitFor(() => expect(runAnalyticsMock).toHaveBeenCalledTimes(1));
    expect(runAnalyticsMock.mock.calls[0][2]).toEqual({
      maxResults: 100,
      epsilon: 0.001,
      parameters: { DampingFactor: 0.9 },
    });
  });

  it("renders a result with no scored vertices without hydrating anything", async () => {
    const user = userEvent.setup();
    runAnalyticsMock.mockResolvedValue(resultWith({ results: [] }));
    renderScreen();

    await pickAlgorithm(user, "ConnectedComponents");
    await user.click(screen.getByTestId("algo-run"));

    await screen.findByTestId("analytics-result");
    expect(getGraphElementMock).not.toHaveBeenCalled();
    expect(screen.queryByTestId("analytics-to-canvas")).not.toBeInTheDocument();
  });
});

describe("describeRunError mapping", () => {
  async function runInto(error: unknown) {
    const user = userEvent.setup();
    runAnalyticsMock.mockRejectedValue(error);
    renderScreen();
    await pickAlgorithm(user, "DegreeCentrality");
    await user.click(screen.getByTestId("algo-run"));
    await waitFor(() => expect(screen.getByRole("alert")).toBeInTheDocument());
  }

  it("maps 429 to the one-shot/serialized hint", async () => {
    await runInto(new ApiError(429, "/analytics/DegreeCentrality", "busy"));
    expect(screen.getByRole("alert")).toHaveTextContent("HTTP 429");
    expect(screen.getByTestId("run-hint")).toHaveTextContent(/already in progress/);
    expect(screen.getByTestId("run-hint")).toHaveTextContent(/retry when it finishes/);
  });

  it("maps 408 to the budget-exhausted hint", async () => {
    await runInto(new ApiError(408, "/analytics/DegreeCentrality", "budget"));
    expect(screen.getByTestId("run-hint")).toHaveTextContent(/Budget exhausted/);
    expect(screen.getByTestId("run-hint")).toHaveTextContent(/raise the time budget/);
  });

  it("shows only the raw error box for unmapped statuses", async () => {
    await runInto(new ApiError(400, "/analytics/DegreeCentrality", "bad direction"));
    expect(screen.getByRole("alert")).toHaveTextContent("HTTP 400");
    expect(screen.getByRole("alert")).toHaveTextContent("bad direction");
    expect(screen.queryByTestId("run-hint")).not.toBeInTheDocument();
  });
});

describe("write-back confirm gate", () => {
  it("routes a write-back run through the typed confirm and reports the write", async () => {
    const user = userEvent.setup();
    runAnalyticsMock.mockResolvedValue(
      resultWith({
        writeBack: { propertyKey: "analytics.rank", verticesWritten: 200, chunks: 2 },
      }),
    );
    renderScreen();

    await pickAlgorithm(user, "DegreeCentrality");
    await user.click(screen.getByTestId("toggle-write-back"));
    await user.click(screen.getByTestId("write-back-checkbox"));
    await user.type(screen.getByLabelText("property key"), "analytics.rank");
    await user.click(screen.getByTestId("algo-run"));

    // Submitting arms the dialog instead of running; the run fires only after the
    // instance name is typed and confirmed.
    expect(runAnalyticsMock).not.toHaveBeenCalled();
    expect(screen.getByTestId("confirm-action")).toBeDisabled();
    await user.type(screen.getByTestId("confirm-typed"), "local");
    await user.click(screen.getByTestId("confirm-action"));

    await waitFor(() => expect(runAnalyticsMock).toHaveBeenCalledTimes(1));
    expect(runAnalyticsMock.mock.calls[0][2]).toEqual({
      maxResults: 100,
      writeBack: true,
      writeBackPropertyKey: "analytics.rank",
    });
    expect(await screen.findByTestId("write-back-report")).toHaveTextContent(
      /wrote analytics\.rank on 200 vertices in 2 chunk/,
    );
  });
});

describe("partition members", () => {
  it("pages members with the same specification and appends follow-up pages", async () => {
    const user = userEvent.setup();
    runAnalyticsMock.mockResolvedValue(
      resultWith({
        algorithm: "ConnectedComponents",
        converged: false,
        budgetExhausted: true,
        partitions: [{ partitionId: 3, size: 3 }],
      }),
    );
    partitionMembersMock
      .mockResolvedValueOnce({ partitionId: 3, size: 3, offset: 0, members: [11, 12] })
      .mockResolvedValueOnce({ partitionId: 3, size: 3, offset: 2, members: [13] });
    getGraphElementMock.mockImplementation(async (_i, id) => vertex(id, `v${id}`));
    renderScreen();

    await pickAlgorithm(user, "ConnectedComponents");
    await user.type(screen.getByLabelText("vertex label"), "person");
    await user.click(screen.getByTestId("algo-run"));

    const result = await screen.findByTestId("analytics-result");
    // Not-converged and partial-budget outcomes are stated, not hidden.
    expect(within(result).getByText("not converged")).toBeInTheDocument();
    expect(within(result).getByText(/budget exhausted — partial/)).toBeInTheDocument();
    expect(within(result).getByText(/partitions — 1/)).toBeInTheDocument();

    await user.click(within(result).getByRole("button", { name: "Members…" }));
    await waitFor(() => expect(partitionMembersMock).toHaveBeenCalledTimes(1));
    expect(partitionMembersMock.mock.calls[0][1]).toBe("ConnectedComponents");
    expect(partitionMembersMock.mock.calls[0][2]).toBe(3);
    // Members re-run the run's specification, plus the page offset.
    expect(partitionMembersMock.mock.calls[0][3]).toEqual({
      vertexLabel: "person",
      maxResults: 100,
      offset: 0,
    });

    await waitFor(() =>
      expect(screen.getByText("partition 3 — 2 of 3 members")).toBeInTheDocument(),
    );
    expect(screen.getByText("v11")).toBeInTheDocument();
    expect(screen.getByText("v12")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "More members" }));
    await waitFor(() => expect(partitionMembersMock).toHaveBeenCalledTimes(2));
    expect(partitionMembersMock.mock.calls[1][3].offset).toBe(2);
    await waitFor(() =>
      expect(screen.getByText("partition 3 — 3 of 3 members")).toBeInTheDocument(),
    );
    // Follow-up pages append; the first page stays in the table.
    expect(screen.getByText("v11")).toBeInTheDocument();
    expect(screen.getByText("v13")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "More members" })).not.toBeInTheDocument();
  });
});

describe("graph shape panel", () => {
  it("computes only on demand and renders counts, cardinalities, degrees, and indices", async () => {
    const user = userEvent.setup();
    renderScreen();

    // The budgeted pass never runs on mount — only the Compute button triggers it.
    expect(screen.queryByTestId("shape-result")).not.toBeInTheDocument();
    expect(getStatisticsMock).not.toHaveBeenCalled();

    await user.click(screen.getByTestId("shape-compute"));
    const result = await screen.findByTestId("shape-result");
    expect(getStatisticsMock).toHaveBeenCalledTimes(1);

    expect(within(result).getByText("999")).toBeInTheDocument(); // vertices
    expect(within(result).getByText("314")).toBeInTheDocument(); // edges
    expect(within(result).getByText("person")).toBeInTheDocument();
    expect(within(result).getByText("2 distinct")).toBeInTheDocument();
    expect(within(result).getByText("knows")).toBeInTheDocument();
    expect(within(result).getByText("age")).toBeInTheDocument();
    // Degree table: mean renders with one decimal, the rest as plain numbers.
    expect(within(result).getByText("4.6")).toBeInTheDocument();
    expect(within(result).getByText("2.1")).toBeInTheDocument();
    expect(within(result).getByText("59")).toBeInTheDocument();
    // Index inventory row with its scan affordance.
    expect(within(result).getByText("nameIndex")).toBeInTheDocument();
    expect(within(result).getByText("DictionaryIndex")).toBeInTheDocument();
    expect(within(result).getByRole("button", { name: "Scan" })).toBeInTheDocument();
    // Full pass: no sampling badge.
    expect(screen.getByText(/computed in 12 ms/)).toBeInTheDocument();
    expect(screen.queryByTestId("shape-sampled")).not.toBeInTheDocument();
  });

  it("flags a sampled pass with its stride", async () => {
    const user = userEvent.setup();
    getStatisticsMock.mockResolvedValue({ ...SHAPE, sampled: true, sampleStride: 16 });
    renderScreen();

    await user.click(screen.getByTestId("shape-compute"));
    await screen.findByTestId("shape-result");
    expect(screen.getByTestId("shape-sampled")).toHaveTextContent("sampled 1:16");
  });

  it("renders the shape error with a retry that recomputes", async () => {
    const user = userEvent.setup();
    getStatisticsMock.mockRejectedValueOnce(new ApiError(429, "/statistics", "rate limited"));
    renderScreen();

    await user.click(screen.getByTestId("shape-compute"));
    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("HTTP 429");
    expect(alert).toHaveTextContent("rate limited");

    await user.click(within(alert).getByRole("button", { name: "Retry" }));
    await screen.findByTestId("shape-result");
    expect(getStatisticsMock).toHaveBeenCalledTimes(2);
  });

  it("states an empty index inventory instead of an empty table", async () => {
    const user = userEvent.setup();
    getStatisticsMock.mockResolvedValue({ ...SHAPE, indices: [] });
    renderScreen();

    await user.click(screen.getByTestId("shape-compute"));
    const result = await screen.findByTestId("shape-result");
    expect(within(result).getByText("no indices")).toBeInTheDocument();
  });
});
