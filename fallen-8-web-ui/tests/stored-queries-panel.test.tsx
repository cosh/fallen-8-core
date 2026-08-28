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
 * Stored-query management over ONE server library (feature studio-traverse-merge): the panel
 * renders the WHOLE `/storedquery` collection of the namespace in one table, with a kind
 * column naming each entry's scenario. It used to take a `kind` prop and show one scenario's
 * entries per screen; the merge left that mode without a caller, so there is exactly one shape
 * now and no client-side filtering at all - whatever the route returns is listed.
 *
 * Pinned here rather than trusted to the Traverse screen that mounts it: the column set, the
 * empty-state prose, and the Use/Delete affordances are this component's contract. "Use" hands
 * the WHOLE entry back instead of navigating, because the host routes it by kind.
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

/** The glyph the table renders for a field the server left null, quoted from the component. */
const PLACEHOLDER = "—";

/** One shape only: the whole library, with the host deciding what "Use" means. */
function renderPanel(
  props: { onUse?: (entry: StoredQuerySummaryREST) => void } = {},
) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <StoredQueriesPanel onUse={props.onUse ?? (() => {})} />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  resetInstanceStoresForTests();
  localStorage.clear();
  storedList = [];
  deleteMock.mockReset().mockResolvedValue(undefined);
});

describe("StoredQueriesPanel (the whole library)", () => {
  it("lists BOTH kinds in one table, names each entry's kind, and titles itself for neither", async () => {
    storedList = [summary("adults", "Path"), summary("triangle", "SubGraph")];
    renderPanel();

    expect(await screen.findByText("Stored queries")).toBeInTheDocument();
    expect(screen.getByTestId("stored-queries-all")).toBeInTheDocument();
    // The kind-scoped panel's testid is gone with the mode: a host still passing `kind` would
    // silently get the whole library, so the old handle must not resolve either.
    expect(screen.queryByTestId("stored-queries-Path")).not.toBeInTheDocument();

    // Five columns, in strip order: the kind one is the whole reason a single table over both
    // scenarios reads at all - without it their entries are indistinguishable rows sharing a
    // name space.
    expect(screen.getAllByRole("columnheader").map((th) => th.textContent)).toEqual([
      "name",
      "kind",
      "state",
      "registered",
      "actions",
    ]);

    expect(await screen.findByText("adults")).toBeInTheDocument();
    expect(screen.getByText("triangle")).toBeInTheDocument();
    expect(screen.getByTestId("stored-query-kind-adults")).toHaveTextContent("Path");
    expect(screen.getByTestId("stored-query-kind-triangle")).toHaveTextContent("SubGraph");
  });

  it("lists an entry the server gave NO kind, with a placeholder instead of a crash", async () => {
    // `kind` is nullable on the wire, and the panel no longer filters on it: a kind-less entry
    // would have been dropped by the scoped mode, so the only view that can show it at all is
    // this one - and it must render the row rather than throw the tab away with it.
    storedList = [summary("orphan", null), summary("adults", "Path")];
    renderPanel();

    expect(await screen.findByText("orphan")).toBeInTheDocument();
    const cell = screen.getByTestId("stored-query-kind-orphan");
    expect(cell).toHaveTextContent(PLACEHOLDER);
    // The placeholder is a dash, never the raw value leaking through as text.
    expect(cell).not.toHaveTextContent(/null|undefined/);
    // It stays invocable (its kind decides only which scenario the HOST opens), and the
    // well-formed entry beside it is untouched.
    expect(screen.getByTestId("stored-query-use-orphan")).toBeEnabled();
    expect(screen.getByTestId("stored-query-kind-adults")).toHaveTextContent("Path");
  });

  it("names both scenarios in the empty state, and spans every column", async () => {
    storedList = [];
    renderPanel();

    const cell = await screen.findByText(/no stored queries on this instance/i);
    // Neither kind's word appears, and the way to register one is where the fragments are.
    expect(cell).toHaveTextContent(/Path finding or Subgraph builder tab/);
    expect(cell).toHaveTextContent(/Save as stored query/);
    // A short span would leave the sentence squeezed under the name column.
    expect(cell).toHaveAttribute("colspan", "5");
  });

  it("Use hands back the WHOLE entry, so the host can route it by kind", async () => {
    const user = userEvent.setup();
    const onUse = vi.fn();
    storedList = [summary("adults", "Path"), summary("triangle", "SubGraph")];
    renderPanel({ onUse });

    const use = await screen.findByTestId("stored-query-use-triangle");
    // The affordance says what Use does, and says it in scenario terms rather than naming one
    // screen: the entry decides where it opens.
    expect(use).toHaveAttribute("title", "select it into its scenario's filter picker");

    await user.click(use);
    expect(onUse).toHaveBeenCalledTimes(1);
    // Only the name would leave the host guessing which scenario to open the entry in.
    expect(onUse).toHaveBeenCalledWith(storedList[1]);
    expect(onUse.mock.calls[0][0].kind).toBe("SubGraph");
  });

  it("still refuses to Use a Failed entry, of either kind, beside usable ones", async () => {
    const user = userEvent.setup();
    const onUse = vi.fn();
    storedList = [summary("broken", "SubGraph", "Failed"), summary("adults", "Path")];
    renderPanel({ onUse });

    const broken = await screen.findByTestId("stored-query-use-broken");
    expect(broken).toBeDisabled();
    // Disabled with the reason on it: a recompile-broken artifact is not invocable, and the
    // way out is delete plus re-register (entries are immutable).
    expect(broken.title).toContain("recompile failed on this instance");
    expect(broken.title).toContain("delete and re-register");
    await user.click(broken);
    expect(onUse).not.toHaveBeenCalled();

    // One unusable artifact must not cost the library: the rest stay invocable.
    expect(screen.getByTestId("stored-query-use-adults")).toBeEnabled();
  });

  it("Delete runs through the typed confirmation and calls the endpoint", async () => {
    const user = userEvent.setup();
    storedList = [summary("adults", "Path")];
    renderPanel();

    await user.click(await screen.findByRole("button", { name: "Delete…" }));
    // The gate is the active instance name ("local" same-origin default).
    expect(screen.getByTestId("confirm-action")).toBeDisabled();
    await user.type(await screen.findByTestId("confirm-typed"), "local");
    await user.click(screen.getByTestId("confirm-action"));

    await waitFor(() => expect(deleteMock).toHaveBeenCalledTimes(1));
    expect(deleteMock.mock.calls[0][1]).toBe("adults");
  });
});
