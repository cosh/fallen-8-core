// MIT License
//
// instance-health.test.tsx
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
import type { InstanceConfig } from "../src/instances/types";
import type { NamespaceEntry, NamespacesResponse, StatusREST } from "../src/api/types";

/**
 * Feature instance-level-health: the Instances row's health cell. The regression it pins is the
 * reported one - a `/status` probe is namespace-scoped, so on an unbound row it describes the
 * reserved `default` alone, and an instance with five populated namespaces read `0 v · 0 e`.
 */

const statusMock = vi.fn<(i: InstanceConfig, s?: AbortSignal) => Promise<StatusREST>>();
const listMock = vi.fn<(i: InstanceConfig, s?: AbortSignal) => Promise<NamespacesResponse>>();

vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    getStatus: (i: InstanceConfig, s?: AbortSignal) => statusMock(i, s),
    listNamespaces: (i: InstanceConfig, s?: AbortSignal) => listMock(i, s),
  };
});

import { ApiError, ApiTimeoutError } from "../src/api/client";
import { InstanceHealth } from "../src/components/InstanceHealth";

const INSTANCE: InstanceConfig = {
  id: "i-1",
  name: "local",
  baseUrl: "http://localhost:8080",
  auth: { kind: "none" },
};

/** The bare `/status` answer from the reported instance: the reserved default is empty. */
const EMPTY_DEFAULT: StatusREST = {
  vertexCount: 0,
  edgeCount: 0,
  usedMemory: 270520320,
  availableIndexPlugins: [],
  availablePathPlugins: [],
  availableAnalyticsPlugins: [],
  availableServicePlugins: [],
  apiKeyRequired: false,
  authenticated: false,
};

const ns = (
  name: string,
  vertexCount: number | null,
  edgeCount: number | null,
): NamespaceEntry => ({
  name,
  state: vertexCount === null ? "notLoaded" : "ready",
  vertexCount,
  edgeCount,
  createdAt: "2026-08-23T10:00:00.000Z",
  loadOnStartupEnabled: null,
});

const inventory = (...namespaces: NamespaceEntry[]): NamespacesResponse => ({
  namespaces,
  maxNamespaces: 10000,
});

/** The reporter's instance (measured 2026-08-23): four populated namespaces beside an empty default. */
const REPORTED = inventory(
  ns("Movie", 191, 1697),
  ns("default", 0, 0),
  ns("f8", 1013, 1774),
  ns("unify", 91, 115),
  ns("wind farm", 199, 344),
);

function renderCell(instance: InstanceConfig = INSTANCE) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });
  return render(
    <QueryClientProvider client={client}>
      <InstanceHealth instance={instance} />
    </QueryClientProvider>,
  );
}

const cell = () => screen.findByTestId("instance-health");

beforeEach(() => {
  statusMock.mockReset();
  listMock.mockReset();
});

describe("InstanceHealth", () => {
  it("reports the instance, not the reserved default the probe happens to address", async () => {
    statusMock.mockResolvedValue(EMPTY_DEFAULT);
    listMock.mockResolvedValue(REPORTED);

    renderCell();

    const health = await cell();
    expect(health).toHaveTextContent("5 ns · 1,494 v · 3,930 e");
    // The exact reported symptom: the probe says 0/0 and the cell must not repeat it.
    expect(health.textContent).not.toBe("0 v · 0 e");
    expect(health).toHaveAttribute("title", "Totals across all 5 namespaces on this instance.");
  });

  it("marks the total as a lower bound when a namespace was not loaded", async () => {
    statusMock.mockResolvedValue(EMPTY_DEFAULT);
    listMock.mockResolvedValue(inventory(ns("default", 4, 2), ns("archived", null, null)));

    renderCell();

    const health = await cell();
    expect(health).toHaveTextContent("2 ns · >=4 v · >=2 e");
    expect(health.getAttribute("title")).toContain("1 did not");
  });

  it("shows the absent glyph instead of a zero when no namespace reported", async () => {
    statusMock.mockResolvedValue(EMPTY_DEFAULT);
    listMock.mockResolvedValue(inventory(ns("a", null, null), ns("b", null, null)));

    renderCell();

    const health = await cell();
    expect(health).toHaveTextContent("2 ns · - v · - e");
    expect(health.textContent).not.toContain("0");
  });

  it("still reads a genuinely empty instance as zero", async () => {
    statusMock.mockResolvedValue(EMPTY_DEFAULT);
    listMock.mockResolvedValue(inventory(ns("default", 0, 0)));

    renderCell();

    expect(await cell()).toHaveTextContent("1 ns · 0 v · 0 e");
  });

  it("shows no count at all while the inventory is in flight", async () => {
    statusMock.mockResolvedValue(EMPTY_DEFAULT);
    let release: (value: NamespacesResponse) => void = () => {};
    listMock.mockReturnValue(
      new Promise<NamespacesResponse>((resolve) => {
        release = resolve;
      }),
    );

    renderCell();

    // The probe has answered here (its counts are in hand) and they are deliberately NOT shown:
    // a default-only number must not flash on the way to the instance total.
    await waitFor(() => expect(listMock).toHaveBeenCalled());
    expect(screen.getByText("checking…")).toBeInTheDocument();
    expect(screen.queryByTestId("instance-health")).not.toBeInTheDocument();

    release(REPORTED);
    expect(await cell()).toHaveTextContent("5 ns · 1,494 v · 3,930 e");
  });

  it("reads unreachable, and asks for no inventory, when the probe fails", async () => {
    statusMock.mockRejectedValue(new Error("connection refused"));
    listMock.mockResolvedValue(REPORTED);

    renderCell();

    await waitFor(() => expect(screen.getByText(/unreachable/)).toBeInTheDocument());
    expect(listMock).not.toHaveBeenCalled();
    expect(screen.queryByTestId("instance-health")).not.toBeInTheDocument();
  });

  it("reads no answer, and names the address, when the probe times out instead of failing", async () => {
    // The reported failure: a published port whose IPv6 loopback forward was dead ACCEPTED the
    // connection and never replied, so the fetch never settled, the query never errored, and this
    // cell said "checking…" indefinitely - for a server that was healthy and answering on IPv4.
    // A refusal and a silence are different faults and must read differently.
    statusMock.mockRejectedValue(new ApiTimeoutError("http://localhost:8080/status", 10_000));
    listMock.mockResolvedValue(REPORTED);

    renderCell();

    await waitFor(() => expect(screen.getByText(/no answer/)).toBeInTheDocument());
    expect(screen.getByTestId("timeout-hint")).toHaveTextContent("127.0.0.1");
    expect(screen.getByTestId("timeout-hint")).toHaveTextContent("10s");
    expect(screen.queryByText(/unreachable/)).not.toBeInTheDocument();
    expect(listMock).not.toHaveBeenCalled();
  });

  it("reads unauthorized, and asks for no inventory, when the credential is refused", async () => {
    // /ns is not [AllowAnonymous], so firing it here would only produce a 401 the probe already
    // diagnosed - the cell has to keep the wording that tells an operator what to fix.
    statusMock.mockResolvedValue({ ...EMPTY_DEFAULT, apiKeyRequired: true, authenticated: false });
    listMock.mockResolvedValue(REPORTED);

    renderCell();

    await waitFor(() =>
      expect(screen.getByText("unauthorized — check the API key")).toBeInTheDocument(),
    );
    expect(listMock).not.toHaveBeenCalled();
  });

  it("keeps the host-token wording for a bearer instance", async () => {
    statusMock.mockResolvedValue({ ...EMPTY_DEFAULT, apiKeyRequired: true, authenticated: false });

    renderCell({ ...INSTANCE, auth: { kind: "bearer", getToken: () => Promise.resolve("t") } });

    await waitFor(() =>
      expect(
        screen.getByText("unauthorized: the host session token was rejected"),
      ).toBeInTheDocument(),
    );
    expect(listMock).not.toHaveBeenCalled();
  });

  it("falls back to the probe's whole graph on a server that predates namespaces", async () => {
    statusMock.mockResolvedValue({ ...EMPTY_DEFAULT, vertexCount: 7, edgeCount: 5 });
    listMock.mockRejectedValue(new ApiError(404, "/ns", "not found"));

    renderCell();

    const health = await cell();
    expect(health).toHaveTextContent("7 v · 5 e");
    expect(health.textContent).not.toContain("ns");
    expect(health.getAttribute("title")).toContain("predates namespaces");
  });

  it("labels a probe-only reading as the default namespace when the inventory fails", async () => {
    statusMock.mockResolvedValue({ ...EMPTY_DEFAULT, vertexCount: 7, edgeCount: 5 });
    listMock.mockRejectedValue(new ApiError(500, "/ns", "boom"));

    renderCell();

    const health = await cell();
    expect(health).toHaveTextContent("default: 7 v · 5 e");
    expect(health.getAttribute("title")).toContain("HTTP 500");
  });

  it("keys the inventory by the raw instance id, so the active row rides the shared cache", async () => {
    // The /ns observers in AppShell and NamespacesPanel use [rawId, "namespaces"]; a divergent key
    // here would silently double every Connect-screen poll.
    statusMock.mockResolvedValue(EMPTY_DEFAULT);
    listMock.mockResolvedValue(REPORTED);
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={client}>
        <InstanceHealth instance={INSTANCE} />
      </QueryClientProvider>,
    );

    await waitFor(() => expect(listMock).toHaveBeenCalledTimes(1));
    expect(client.getQueryData([INSTANCE.id, "namespaces"])).toEqual(REPORTED);
  });
});
