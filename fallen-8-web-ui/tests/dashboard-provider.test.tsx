import { beforeEach, afterEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { GraphStatisticsREST, StatusREST } from "../src/api/types";

/**
 * Dashboard embedding-provider card (feature embedding-provider / studio-semantics): the
 * three gating states — unknown (no graph shape computed), disabled, and enabled with the
 * backend/model/dimension/metric/loaded stats.
 */

import { DashboardScreen } from "../src/screens/DashboardScreen";

const STATUS: StatusREST = {
  vertexCount: 3,
  edgeCount: 2,
  usedMemory: 1024,
  indices: [],
  availableIndexPlugins: [],
  availablePathPlugins: [],
  availableAnalyticsPlugins: [],
  availableServicePlugins: [],
};

function stats(enabled: boolean, loaded = false): GraphStatisticsREST {
  return {
    vertexCount: 3,
    edgeCount: 2,
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
      dimension: 384,
      intendedMetric: "Cosine",
      loaded,
    },
  };
}

function renderDashboard(statistics?: GraphStatisticsREST) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(["local", "status"], STATUS);
  if (statistics) {
    client.setQueryData(["local", "statistics"], statistics);
  }
  return render(
    <QueryClientProvider client={client}>
      <DashboardScreen />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  // Any unmocked query (e.g. the stored-queries panel) resolves harmlessly.
  vi.stubGlobal("fetch", vi.fn(async () => new Response("null", { status: 200 })));
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("dashboard embedding-provider card", () => {
  it("shows the unknown state when no graph shape has been computed", async () => {
    renderDashboard(undefined);
    await waitFor(() => expect(screen.getByTestId("embedding-provider-card")).toBeInTheDocument());
    expect(screen.getByTestId("provider-unknown")).toBeInTheDocument();
    expect(screen.queryByTestId("provider-enabled")).not.toBeInTheDocument();
  });

  it("shows the disabled state when the provider is off", async () => {
    renderDashboard(stats(false));
    await waitFor(() => expect(screen.getByTestId("provider-disabled")).toBeInTheDocument());
  });

  it("shows the enabled state with backend, model, dimension, metric, loaded", async () => {
    renderDashboard(stats(true, false));
    await waitFor(() => expect(screen.getByTestId("provider-enabled")).toBeInTheDocument());
    const card = screen.getByTestId("embedding-provider-card");
    expect(card).toHaveTextContent("Onnx");
    expect(card).toHaveTextContent("bge-micro-v2");
    expect(card).toHaveTextContent("384");
    expect(card).toHaveTextContent("Cosine");
    expect(card).toHaveTextContent("not yet"); // loaded=false
  });
});
