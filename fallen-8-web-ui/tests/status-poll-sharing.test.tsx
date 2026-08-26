// MIT License
//
// status-poll-sharing.test.tsx
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

import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { InstanceConfig } from "../src/instances/types";

/**
 * M5: InstanceHealth used to hand-roll its own `/status` useQuery on the exact key AppShell
 * already polls, with a DIFFERENT refetchInterval (20s vs 15s), so the row it renders never
 * refreshed at the cadence it declared (see STATUS_POLL_MS for why). This pins that InstanceHealth
 * now rides the shared useStatus() hook (state/status.ts) instead of opening a second observer of
 * its own, and that the poll cadence has exactly one numeric source.
 */

const useStatusMock = vi.fn();
vi.mock("../src/state/status", () => ({
  useStatus: (...args: unknown[]) => useStatusMock(...args),
}));

import { InstanceHealth } from "../src/components/InstanceHealth";
import { STATUS_POLL_MS } from "../src/lib/pollIntervals";

const INSTANCE: InstanceConfig = {
  id: "i-1",
  name: "local",
  baseUrl: "http://localhost:8080",
  auth: { kind: "none" },
};

function renderCell() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
  return render(
    <QueryClientProvider client={client}>
      <InstanceHealth instance={INSTANCE} />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  useStatusMock.mockReset();
});

describe("InstanceHealth's status probe", () => {
  it("delegates to the shared useStatus() hook, polling, instead of opening its own /status observer", () => {
    useStatusMock.mockReturnValue({
      isPending: true,
      isError: false,
      isSuccess: false,
      data: undefined,
      error: null,
    });

    renderCell();

    // The one call that matters: same instance, poll: true - the exact shape AppShell's own
    // useStatus(active, { poll: true }) call already uses (see namespaceSignals.ts), so both
    // land on ONE shared cache row rather than two competing ones.
    expect(useStatusMock).toHaveBeenCalledWith(INSTANCE, { poll: true });
  });

  it("still renders from whatever the shared hook returns", () => {
    useStatusMock.mockReturnValue({
      isPending: false,
      isError: true,
      isSuccess: false,
      data: undefined,
      error: new Error("down"),
    });

    renderCell();

    expect(screen.getByText(/unreachable/)).toBeInTheDocument();
  });
});

describe("the status/namespace poll interval has one numeric source", () => {
  const here = dirname(fileURLToPath(import.meta.url));
  const read = (relPath: string) => readFileSync(resolve(here, "..", relPath), "utf8");

  it("STATUS_POLL_MS is the 15s cadence the shared hook and every poller describe", () => {
    expect(STATUS_POLL_MS).toBe(15_000);
  });

  it.each([
    "src/app/AppShell.tsx",
    "src/app/NamespaceScope.tsx",
    "src/components/NamespacesPanel.tsx",
    "src/components/InstanceHealth.tsx",
    "src/state/status.ts",
  ] as const)("%s polls through the shared constant, not a numeric literal", (relPath) => {
    const source = read(relPath);
    expect(source).toMatch(/STATUS_POLL_MS/);
    expect(source).not.toMatch(/refetchInterval:\s*\d/);
  });
});
