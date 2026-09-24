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

import { readFileSync, readdirSync } from "node:fs";
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

  // The cadence used to be named in each of the four /ns observers, because each of them declared
  // its own query. They now share one hook, so the constant has moved into the two state modules
  // and the consumers name it nowhere. The invariant is unchanged and is asserted as two halves:
  // the hooks are its only homes, and no consumer hand-rolls an interval of its own.
  it.each(["src/state/status.ts", "src/state/namespaces.ts"] as const)(
    "%s is one of the two homes of the shared cadence",
    (relPath) => {
      expect(read(relPath)).toMatch(/STATUS_POLL_MS/);
    },
  );

  it.each([
    "src/app/AppShell.tsx",
    "src/app/NamespaceScope.tsx",
    "src/components/NamespacesPanel.tsx",
    "src/components/InstanceHealth.tsx",
  ] as const)("%s hand-rolls no poll interval of its own", (relPath) => {
    expect(read(relPath)).not.toMatch(/refetchInterval:\s*\d/);
  });
});

/**
 * The same "one home" rule for the KEY rather than the cadence. Four components declared the /ns
 * query and five sites invalidated it, so the key was spelled in nine places; a tenth that got a
 * character wrong would have observed a row nothing else writes, and nothing would have failed.
 * The hook owns the query and exports the key builder, and this is what keeps it that way.
 */
describe("the namespace inventory has one query key", () => {
  const src = resolve(dirname(fileURLToPath(import.meta.url)), "..", "src");
  const owner = "state/namespaces.ts";

  it("is spelled in state/namespaces.ts and nowhere else under src/", () => {
    const offenders: string[] = [];
    let ownerMatched = false;

    for (const entry of readdirSync(src, { recursive: true, encoding: "utf8" })) {
      const relative = entry.replace(/\\/g, "/");
      if (!/\.tsx?$/.test(relative)) {
        continue;
      }

      // Comments are stripped, for the reason CodeQualityTest documents: pollIntervals.ts
      // describes the key form in its prose, and prose is not a second definition.
      const code = readFileSync(resolve(src, entry), "utf8")
        .split("\n")
        .filter((line) => {
          const trimmed = line.trim();
          return !trimmed.startsWith("//") && !trimmed.startsWith("*") && !trimmed.startsWith("/*");
        })
        .join("\n");

      if (!/,\s*"namespaces"\s*\]/.test(code)) {
        continue;
      }

      if (relative === owner) {
        ownerMatched = true;
        continue;
      }

      offenders.push(relative);
    }

    // The control. A pattern that matched nothing would report an empty offender list and read as
    // a pass, which is the opposite of what it means.
    expect(ownerMatched).toBe(true);
    expect(offenders).toEqual([]);
  });
});
