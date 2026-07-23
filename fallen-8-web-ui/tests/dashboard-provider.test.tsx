import { beforeEach, afterEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type {
  EmbeddingProviderStatsREST,
  GraphStatisticsREST,
  StatusREST,
} from "../src/api/types";

/**
 * Dashboard embedding-provider card (feature embedding-out-of-box): the provider state
 * comes from the cheap /status surface — always known on a current server — with the
 * graph-shape snapshot as fallback for servers predating the /status field, and the
 * unknown state only when neither reports.
 */

import { DashboardScreen } from "../src/screens/DashboardScreen";

function provider(enabled: boolean, loaded = false): EmbeddingProviderStatsREST {
  return {
    enabled,
    backend: "Ollama",
    modelName: "bge-m3",
    modelVersion: "",
    dimension: 1024,
    intendedMetric: "Cosine",
    loaded,
  };
}

function status(embedding?: EmbeddingProviderStatsREST): StatusREST {
  return {
    vertexCount: 3,
    edgeCount: 2,
    usedMemory: 1024,
    indices: [],
    availableIndexPlugins: [],
    availablePathPlugins: [],
    availableAnalyticsPlugins: [],
    availableServicePlugins: [],
    ...(embedding ? { embedding } : {}),
  };
}

function stats(embedding: EmbeddingProviderStatsREST): GraphStatisticsREST {
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
    embedding,
  };
}

function renderDashboard(statusRest: StatusREST, statistics?: GraphStatisticsREST) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  // The dashboard reads through the NAMESPACE-BOUND instance view, whose id is
  // "<instance-id>/<namespace>" (feature graph-namespaces) - seed the bound keys.
  client.setQueryData(["local/default", "status"], statusRest);
  if (statistics) {
    client.setQueryData(["local/default", "statistics"], statistics);
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
  it("shows the unknown state only when neither /status nor a shape reports", async () => {
    renderDashboard(status(undefined));
    await waitFor(() => expect(screen.getByTestId("embedding-provider-card")).toBeInTheDocument());
    expect(screen.getByTestId("provider-unknown")).toBeInTheDocument();
    expect(screen.queryByTestId("provider-enabled")).not.toBeInTheDocument();
  });

  it("shows the disabled state from /status alone — no graph shape needed", async () => {
    renderDashboard(status(provider(false)));
    await waitFor(() => expect(screen.getByTestId("provider-disabled")).toBeInTheDocument());
    expect(screen.getByTestId("provider-disabled")).toHaveTextContent("F8_EMBEDDINGS");
  });

  it("shows the enabled state from /status with backend, model, dimension, metric, loaded", async () => {
    renderDashboard(status(provider(true, false)));
    await waitFor(() => expect(screen.getByTestId("provider-enabled")).toBeInTheDocument());
    const card = screen.getByTestId("embedding-provider-card");
    expect(card).toHaveTextContent("Ollama");
    expect(card).toHaveTextContent("bge-m3");
    expect(card).toHaveTextContent("1024");
    expect(card).toHaveTextContent("Cosine");
    expect(card).toHaveTextContent("not yet"); // loaded=false
  });

  it("falls back to the graph-shape snapshot for servers predating the /status field", async () => {
    renderDashboard(status(undefined), stats(provider(true)));
    await waitFor(() => expect(screen.getByTestId("provider-enabled")).toBeInTheDocument());
  });

  it("prefers the live /status over a stale shape snapshot", async () => {
    renderDashboard(status(provider(false)), stats(provider(true)));
    await waitFor(() => expect(screen.getByTestId("provider-disabled")).toBeInTheDocument());
    expect(screen.queryByTestId("provider-enabled")).not.toBeInTheDocument();
  });
});
