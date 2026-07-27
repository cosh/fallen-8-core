// MIT License
//
// stored-queries-panel.test.tsx
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
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { InstanceConfig } from "../src/instances/types";
import type { StoredQueryDetailREST, StoredQuerySummaryREST } from "../src/api/types";
import { resetInstanceStoresForTests } from "../src/state/instanceStore";

/**
 * Kind-scoped stored-query management (feature stored-query-scenario-scoped-ux): a stored
 * query is unique to its scenario, so each screen's panel shows ONLY its own kind, and the
 * "Use" action feeds the entry back to the host screen instead of navigating away. Pinned
 * here rather than trusted to the two screens that mount it.
 */

let storedList: StoredQuerySummaryREST[] = [];
const deleteMock = vi.fn<(i: InstanceConfig, name: string) => Promise<void>>();

vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    listStoredQueries: async () => storedList,
    getStoredQuery: async (_i: InstanceConfig, name: string) =>
      ({
        name,
        kind: "Path",
        description: null,
        createdAt: "",
        compileState: "Compiled",
        specificationJson: "{}",
        compileDiagnostics: null,
      }) as StoredQueryDetailREST,
    deleteStoredQuery: (i: InstanceConfig, name: string) => deleteMock(i, name),
  };
});

import { StoredQueriesPanel } from "../src/components/StoredQueriesPanel";

function summary(
  name: string,
  kind: StoredQuerySummaryREST["kind"],
  compileState: StoredQuerySummaryREST["compileState"] = "Compiled",
): StoredQuerySummaryREST {
  return { name, kind, description: null, createdAt: "", compileState };
}

function renderPanel(
  kind: "Path" | "SubGraph",
  onUse: (name: string) => void = () => {},
) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <StoredQueriesPanel kind={kind} onUse={onUse} />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  resetInstanceStoresForTests();
  localStorage.clear();
  storedList = [];
  deleteMock.mockReset().mockResolvedValue(undefined);
});

describe("StoredQueriesPanel (kind-scoped)", () => {
  it("lists only entries of its own kind and titles itself accordingly", async () => {
    storedList = [summary("adults", "Path"), summary("triangle", "SubGraph")];
    renderPanel("Path");

    expect(await screen.findByText("Stored path queries")).toBeInTheDocument();
    expect(await screen.findByText("adults")).toBeInTheDocument();
    // The other kind's entry never leaks onto a Path screen.
    expect(screen.queryByText("triangle")).not.toBeInTheDocument();
  });

  it("shows a kind-named empty state when no entry of that kind exists", async () => {
    storedList = [summary("adults", "Path")]; // a Path entry, but we render the SubGraph panel
    renderPanel("SubGraph");

    expect(
      await screen.findByText(/no stored subgraph queries on this instance/i),
    ).toBeInTheDocument();
    expect(screen.queryByText("adults")).not.toBeInTheDocument();
  });

  it("Use hands the entry name back to the host screen (no navigation)", async () => {
    const user = userEvent.setup();
    const onUse = vi.fn();
    storedList = [summary("adults", "Path")];
    renderPanel("Path", onUse);

    await user.click(await screen.findByTestId("stored-query-use-adults"));
    expect(onUse).toHaveBeenCalledTimes(1);
    expect(onUse).toHaveBeenCalledWith("adults");
  });

  it("a Failed entry cannot be Used (recompile-broken artifacts are not invocable)", async () => {
    storedList = [summary("broken", "Path", "Failed")];
    renderPanel("Path");

    expect(await screen.findByTestId("stored-query-use-broken")).toBeDisabled();
  });

  it("Delete runs through the typed confirmation and calls the endpoint", async () => {
    const user = userEvent.setup();
    storedList = [summary("adults", "Path")];
    renderPanel("Path");

    await user.click(await screen.findByRole("button", { name: "Delete…" }));
    // The gate is the active instance name ("local" same-origin default).
    await user.type(await screen.findByTestId("confirm-typed"), "local");
    await user.click(screen.getByTestId("confirm-action"));

    await waitFor(() => expect(deleteMock).toHaveBeenCalledTimes(1));
    expect(deleteMock.mock.calls[0][1]).toBe("adults");
  });
});
