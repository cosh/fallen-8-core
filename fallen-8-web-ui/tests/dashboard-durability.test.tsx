// MIT License
//
// dashboard-durability.test.tsx
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
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { DurabilityREST, StatusREST } from "../src/api/types";
import type { InstanceConfig } from "../src/instances/types";
import { resetInstanceStoresForTests } from "../src/state/instanceStore";

/**
 * The LAST hop of the durability signal (feature platform-integrity-audit W5): the component test
 * proves the notice can render, this one proves the Dashboard actually renders it - including on an
 * empty graph, where a truncated recovery is a likely cause of the emptiness.
 */

const getStatusMock = vi.fn<(i: InstanceConfig, s?: AbortSignal) => Promise<StatusREST | null>>();

vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    getStatus: (i: InstanceConfig, s?: AbortSignal) => getStatusMock(i, s),
  };
});

const navigateMock = vi.fn();
vi.mock("@tanstack/react-router", () => ({
  useNavigate: () => navigateMock,
}));

import { DashboardScreen } from "../src/screens/DashboardScreen";

const healthy: DurabilityREST = {
  walEnabled: true,
  degraded: false,
  recoveryRan: true,
  lastRecoveryTruncated: false,
  lastRecoveryReplayedEntries: 12,
  lastCheckpointDroppedIndices: 0,
};

function status(vertexCount: number, durability?: DurabilityREST | null): StatusREST {
  return {
    vertexCount,
    edgeCount: 0,
    usedMemory: 1024 * 1024,
    availableIndexPlugins: [],
    availablePathPlugins: [],
    availableAnalyticsPlugins: [],
    availableServicePlugins: [],
    durability,
  };
}

function renderDashboard() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <DashboardScreen />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  resetInstanceStoresForTests();
  localStorage.clear();
  navigateMock.mockReset();
  getStatusMock.mockReset().mockResolvedValue(status(3, healthy));
  // Reduced motion: the first-run show then rests instead of autoplaying on timers.
  window.matchMedia = vi.fn().mockImplementation((query: string) => ({
    matches: query.includes("reduce"),
    media: query,
    onchange: null,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    addListener: vi.fn(),
    removeListener: vi.fn(),
    dispatchEvent: vi.fn(),
  }));
});

describe("Dashboard durability notice", () => {
  it("shows the degraded-log warning from /status on a populated graph", async () => {
    getStatusMock.mockResolvedValue(status(3, { ...healthy, degraded: true }));

    renderDashboard();

    await waitFor(() => expect(screen.getByTestId("durability-notice")).toBeInTheDocument());
    expect(screen.getByText(/The write-ahead log is degraded/i)).toBeInTheDocument();
  });

  it("shows the truncated-recovery warning together with the dropped-index count", async () => {
    getStatusMock.mockResolvedValue(
      status(3, {
        ...healthy,
        lastRecoveryTruncated: true,
        lastRecoveryReplayedEntries: 7,
        lastCheckpointDroppedIndices: 2,
      }),
    );

    renderDashboard();

    await waitFor(() => expect(screen.getByTestId("durability-notice")).toBeInTheDocument());
    expect(screen.getByText(/The last recovery was truncated/i)).toBeInTheDocument();
    expect(screen.getByText(/dropped 2 index\(es\)/i)).toBeInTheDocument();
  });

  it("stays silent on a healthy graph, and the tiles are still there", async () => {
    renderDashboard();

    await waitFor(() => expect(screen.getByText("vertices")).toBeInTheDocument());
    expect(screen.queryByTestId("durability-notice")).toBeNull();
  });

  it("warns on an EMPTY graph too, where the first-run show would otherwise hide it", async () => {
    // A truncated recovery can be why the graph is empty; greeting that with "get started" buries
    // the one signal the operator needs.
    getStatusMock.mockResolvedValue(status(0, { ...healthy, lastRecoveryTruncated: true }));

    renderDashboard();

    await waitFor(() => expect(screen.getByTestId("first-run-show")).toBeInTheDocument());
    expect(screen.getByTestId("durability-notice")).toBeInTheDocument();
    expect(screen.getByText(/The last recovery was truncated/i)).toBeInTheDocument();
  });

  it("keeps the empty-graph welcome unpolluted when durability is healthy", async () => {
    getStatusMock.mockResolvedValue(status(0, healthy));

    renderDashboard();

    await waitFor(() => expect(screen.getByTestId("first-run-show")).toBeInTheDocument());
    expect(screen.queryByTestId("durability-notice")).toBeNull();
  });

  it("says nothing when a server does not report the block at all", async () => {
    getStatusMock.mockResolvedValue(status(3, null));

    renderDashboard();

    await waitFor(() => expect(screen.getByText("vertices")).toBeInTheDocument());
    expect(screen.queryByTestId("durability-notice")).toBeNull();
  });
});
