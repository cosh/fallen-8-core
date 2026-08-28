// MIT License
//
// traverse-namespace-switch.test.tsx
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
import {
  createMemoryHistory,
  createRootRoute,
  createRoute,
  createRouter,
  Outlet,
  RouterProvider,
  useNavigate,
} from "@tanstack/react-router";
import type {
  PathREST,
  StatusREST,
  StoredQuerySummaryREST,
  SubGraphSummary,
} from "../src/api/types";

/**
 * Regression, feature studio-traverse-merge: switching namespace must open the NEW namespace's
 * remembered Traverse tab, and must not overwrite it with the old one's `?tab=`.
 *
 * The shell remounts the screen subtree on a namespace switch (it keys the content on
 * instance+namespace) the moment the registry is written - which is BEFORE the router has
 * committed the match for the new URL. A screen reading the committed match's search
 * (`useSearch`) therefore woke up in the new namespace still holding the PREVIOUS one's
 * `?tab=`, adopted it into the new namespace's persisted store, and so both landed the operator
 * on the wrong tab and destroyed the tab that namespace remembered. Reading the LIVE location
 * (`useRouterState`) sees the new, tab-less URL instead.
 *
 * That gap between "committed match" and "live location" is exactly what a mocked router cannot
 * express - the mock in tests/traverse-screen.test.tsx has one search object and no commit - so
 * this file drives a REAL memory-history router. The harness below is deliberately minimal but
 * mirrors AppShell where it matters: the same remount key and the same two steps of
 * `switchNamespace`, in the same order (registry write, then a tab-less navigation).
 */

// The router's scroll restoration calls window.scrollTo on every navigation, which jsdom answers
// with a "Not implemented" dump on stderr; the stub keeps a real navigation quiet.
vi.stubGlobal("scrollTo", () => {});

vi.mock("../src/delegate/monacoSetup", () => ({ setupMonaco: () => {}, monaco: {} }));
vi.mock("@monaco-editor/react", () => ({
  default: ({ value }: { value: string }) => (
    <textarea data-testid="mock-editor" value={value} readOnly />
  ),
}));

const STATUS: StatusREST = {
  vertexCount: 0,
  edgeCount: 0,
  usedMemory: 0,
  indices: [],
  availableIndexPlugins: [],
  availablePathPlugins: [],
  availableAnalyticsPlugins: [],
  availableServicePlugins: [],
};

vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    getStatus: async () => STATUS,
    findPaths: async () => [] as PathREST[],
    listStoredQueries: async () => [] as StoredQuerySummaryREST[],
    listSubGraphSummaries: async () => [] as SubGraphSummary[],
  };
});

import { TraverseScreen } from "../src/screens/TraverseScreen";
import { SAME_ORIGIN_INSTANCE, useActiveNamespace, useRegistry } from "../src/instances/registry";
import {
  getInstanceStore,
  isTraverseTab,
  resetInstanceStoresForTests,
  type TraverseTab,
} from "../src/state/instanceStore";

/**
 * The shell's two relevant behaviours and nothing else (see AppShell): the content subtree is
 * KEYED on instance id + active namespace, so a switch remounts the screen; and the switcher
 * writes the registry first and then navigates to the same leaf WITHOUT a search param.
 */
function ShellHarness() {
  const ns = useActiveNamespace();
  const navigate = useNavigate();

  const switchNamespace = (name: string) => {
    useRegistry.getState().setActiveNamespace(SAME_ORIGIN_INSTANCE.id, name);
    void navigate({ to: "/q/$ns/traverse", params: { ns: name } });
  };

  return (
    <div>
      <button
        type="button"
        data-testid="switch-namespace"
        onClick={() => switchNamespace("flights")}
      >
        flights
      </button>
      <div key={`${SAME_ORIGIN_INSTANCE.id}/${ns}`}>
        <Outlet />
      </div>
    </div>
  );
}

const rootRoute = createRootRoute({ component: ShellHarness });

/** The real leaf, with the real `?tab=` validation (copied from app/routes.tsx). */
const traverseRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/q/$ns/traverse",
  validateSearch: (search: Record<string, unknown>): { tab?: TraverseTab } =>
    isTraverseTab(search.tab) ? { tab: search.tab } : {},
  component: TraverseScreen,
});

const routeTree = rootRoute.addChildren([traverseRoute]);

const WORKSPACE_KEY = {
  /** The reserved namespace keeps the bare instance key (scopeKey collapses "/default"). */
  default: "f8.workspace.local",
  flights: "f8.workspace.local/flights",
} as const;

/** What a namespace's workspace has persisted about the Traverse screen, straight off storage. */
function persistedTab(key: string): unknown {
  const raw = localStorage.getItem(key);
  return raw === null ? null : (JSON.parse(raw) as { state?: { traverseTab?: unknown } }).state
    ?.traverseTab;
}

function seedTab(key: string, traverseTab: TraverseTab) {
  localStorage.setItem(key, JSON.stringify({ state: { traverseTab }, version: 0 }));
}

async function renderTraverse(initialUrl: string) {
  const router = createRouter({
    routeTree,
    history: createMemoryHistory({ initialEntries: [initialUrl] }),
  });
  await router.load();
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  await waitFor(() => expect(screen.getByTestId("traverse-tab-path")).toBeInTheDocument());
  return router;
}

const tab = (id: TraverseTab) => screen.getByTestId(`traverse-tab-${id}`);

beforeEach(() => {
  resetInstanceStoresForTests();
  localStorage.clear();
  useRegistry.setState({
    instances: [SAME_ORIGIN_INSTANCE],
    activeId: SAME_ORIGIN_INSTANCE.id,
    activeNamespaces: {},
    namespaceSupport: {},
  });
});

describe("switching namespace on the Traverse screen", () => {
  it("opens the new namespace's remembered tab, and does not overwrite it with the old ?tab=", async () => {
    const user = userEvent.setup();
    // "flights" was last left on the subgraph builder; "default" is deep-linked to the library.
    seedTab(WORKSPACE_KEY.flights, "subgraph");
    resetInstanceStoresForTests();

    const router = await renderTraverse("/q/default/traverse?tab=stored");

    // Precondition: the URL wins for the namespace it names.
    expect(tab("stored")).toHaveAttribute("aria-selected", "true");
    expect(screen.getByTestId("stored-queries-all")).toBeVisible();

    await user.click(screen.getByTestId("switch-namespace"));

    // The switch really happened, and it carried no tab of its own: the leaf is rewritten for
    // the new namespace with an empty search, which is the whole reason the store has to answer.
    await waitFor(() => expect(router.state.location.pathname).toBe("/q/flights/traverse"));
    expect(router.state.location.search).toEqual({});

    // The remembered tab of the namespace we arrived IN, not the tab we left the previous one on.
    await waitFor(() => expect(tab("subgraph")).toHaveAttribute("aria-selected", "true"));
    expect(screen.getByTestId("sg-create")).toBeVisible();
    // Hidden panels keep their DOM, so visibility is the only assertion that means anything here.
    expect(screen.getByTestId("stored-queries-all")).not.toBeVisible();
    expect(tab("stored")).toHaveAttribute("aria-selected", "false");

    // And the memory survived the trip: adopting the previous scope's ?tab= would have written
    // "stored" over it, so the next visit to flights would open the library too.
    expect(getInstanceStore("local", "flights").getState().traverseTab).toBe("subgraph");
    expect(persistedTab(WORKSPACE_KEY.flights)).toBe("subgraph");
    // The namespace we left keeps ITS tab: the two scopes are independent, which is what makes
    // the switch lossless in both directions.
    expect(getInstanceStore("local", "default").getState().traverseTab).toBe("stored");
    expect(persistedTab(WORKSPACE_KEY.default)).toBe("stored");
  });
});
