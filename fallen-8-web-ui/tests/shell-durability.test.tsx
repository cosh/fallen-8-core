// MIT License
//
// shell-durability.test.tsx
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
import type { ReactNode } from "react";
import type { DurabilityREST, StatusREST } from "../src/api/types";
import type { InstanceConfig } from "../src/instances/types";
import { resetInstanceStoresForTests } from "../src/state/instanceStore";

/**
 * The LAST hop of the durability signal (feature platform-integrity-audit W5): the component test
 * (durability-notice.test.tsx) proves the notice can render, this one proves the APP SHELL renders
 * it - on whatever screen the operator is on, which is the whole point. It used to hang off the
 * Dashboard, where the only people who saw "your commits are not reaching disk" were the ones who
 * thought to open a screen of three counters.
 */

let currentPath = "/q/default/browser";
vi.mock("@tanstack/react-router", () => ({
  Link: ({
    to,
    children,
    params: _params,
    ...rest
  }: { to: string; children: ReactNode; params?: unknown } & Record<string, unknown>) => (
    <a href={to} {...rest}>
      {children}
    </a>
  ),
  useNavigate: () => () => Promise.resolve(),
  useRouterState: ({
    select,
  }: {
    select: (s: { location: { pathname: string } }) => unknown;
  }) => select({ location: { pathname: currentPath } }),
}));

vi.mock("../src/state/liveFeed", () => ({
  useLiveChangeFeed: () => "connecting",
}));

const statusMock = vi.fn<(i: InstanceConfig, s?: AbortSignal) => Promise<StatusREST | null>>();
const listIntegrationProvidersMock =
  vi.fn<(i: InstanceConfig, s?: AbortSignal) => Promise<unknown>>();

vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    getStatus: (i: InstanceConfig, s?: AbortSignal) => statusMock(i, s),
    listIntegrationProviders: (i: InstanceConfig, s?: AbortSignal) =>
      listIntegrationProvidersMock(i, s),
  };
});

import { AppShell } from "../src/app/AppShell";
import { SAME_ORIGIN_INSTANCE, useRegistry } from "../src/instances/registry";

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
    apiKeyRequired: false,
    authenticated: false,
    durability,
  };
}

function renderShell(path = "/q/default/browser") {
  currentPath = path;
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <AppShell>
        <div data-testid="screen" />
      </AppShell>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  resetInstanceStoresForTests();
  localStorage.clear();
  listIntegrationProvidersMock.mockReset().mockResolvedValue([]);
  statusMock.mockReset().mockResolvedValue(status(3, healthy));
  useRegistry.setState({
    instances: [SAME_ORIGIN_INSTANCE],
    activeId: SAME_ORIGIN_INSTANCE.id,
  });
  // Reduced motion: were the first-run show to open, it rests instead of running on timers.
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

describe("the shell durability banner", () => {
  it("shows the degraded-log warning from /status on a populated graph", async () => {
    statusMock.mockResolvedValue(status(3, { ...healthy, degraded: true }));

    renderShell();

    await waitFor(() => expect(screen.getByTestId("durability-notice")).toBeInTheDocument());
    expect(screen.getByText(/The write-ahead log is degraded/i)).toBeInTheDocument();
  });

  it("shows the truncated-recovery warning together with the dropped-index count", async () => {
    statusMock.mockResolvedValue(
      status(3, {
        ...healthy,
        lastRecoveryTruncated: true,
        lastRecoveryReplayedEntries: 7,
        lastCheckpointDroppedIndices: 2,
      }),
    );

    renderShell();

    await waitFor(() => expect(screen.getByTestId("durability-notice")).toBeInTheDocument());
    expect(screen.getByText(/The last recovery was truncated/i)).toBeInTheDocument();
    expect(screen.getByText(/dropped 2 index/i)).toBeInTheDocument();
  });

  it("stays silent on a healthy graph, and the screen is still there", async () => {
    renderShell();

    await waitFor(() => expect(screen.getByTestId("health-chip")).toHaveTextContent("online"));
    expect(screen.queryByTestId("durability-notice")).toBeNull();
    expect(screen.getByTestId("screen")).toBeInTheDocument();
  });

  it("says nothing when a server does not report the block at all", async () => {
    statusMock.mockResolvedValue(status(3, null));

    renderShell();

    await waitFor(() => expect(screen.getByTestId("health-chip")).toHaveTextContent("online"));
    expect(screen.queryByTestId("durability-notice")).toBeNull();
  });

  it("warns on a FLAT route too - the signal is shell chrome, not one screen's content", async () => {
    statusMock.mockResolvedValue(status(3, { ...healthy, degraded: true }));

    renderShell("/save-games");

    await waitFor(() => expect(screen.getByTestId("durability-notice")).toBeInTheDocument());
  });

  it("warns on an EMPTY graph, and the first-run show stands down so it is not behind a scrim", async () => {
    // A truncated recovery can be WHY the graph is empty; greeting that with "get started" buries
    // the one signal the operator needs, and a modal cannot be ordered behind its own scrim.
    statusMock.mockResolvedValue(status(0, { ...healthy, lastRecoveryTruncated: true }));

    renderShell();

    await waitFor(() => expect(screen.getByTestId("durability-notice")).toBeInTheDocument());
    expect(screen.getByText(/The last recovery was truncated/i)).toBeInTheDocument();
    expect(screen.queryByTestId("first-run-overlay")).toBeNull();
  });

  it("keeps the empty-graph welcome unpolluted when durability is healthy", async () => {
    statusMock.mockResolvedValue(status(0, healthy));

    renderShell();

    await waitFor(() => expect(screen.getByTestId("first-run-overlay")).toBeInTheDocument());
    expect(screen.queryByTestId("durability-notice")).toBeNull();
  });

  it("does not ask a namespace anything, nor warn, while no instance is registered", async () => {
    useRegistry.setState({ instances: [], activeId: null });

    renderShell();

    await waitFor(() => expect(screen.getByText(/No instance selected/)).toBeInTheDocument());
    expect(statusMock).not.toHaveBeenCalled();
    expect(screen.queryByTestId("durability-notice")).toBeNull();
  });
});
