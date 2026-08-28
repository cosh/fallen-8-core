// MIT License
//
// traverse-screen.test.tsx
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
import type {
  PathREST,
  PathSpecification,
  StatusREST,
  StoredQuerySummaryREST,
  SubGraphSummary,
} from "../src/api/types";
import {
  getInstanceStore,
  resetInstanceStoresForTests,
  TRAVERSE_TABS,
  type TraverseTab,
} from "../src/state/instanceStore";

/**
 * The merged Traverse screen (feature studio-traverse-merge): one rail entry, three tabs, and
 * the stored-query library the two scenarios share.
 *
 * The load-bearing guarantee is that a tab switch is NOT a screen change. The panels stay
 * MOUNTED and are hidden, so a path result, an open advanced tier and a half-filled form all
 * survive a trip to the subgraph builder. That is why visibility here is asserted with
 * toBeVisible() and never with a presence check: a hidden panel keeps its whole DOM on purpose,
 * and a presence assertion would pass on every tab.
 *
 * Monaco is mocked to a textarea (the delegate slots pull it in transitively).
 */

vi.mock("../src/delegate/monacoSetup", () => ({ setupMonaco: () => {}, monaco: {} }));
vi.mock("@monaco-editor/react", () => ({
  default: ({ value }: { value: string }) => (
    <textarea data-testid="mock-editor" value={value} readOnly />
  ),
}));

const navigateMock = vi.fn();
/**
 * The LIVE location's search, which is what the screen reads (useRouterState, not useSearch -
 * see the screen's own comment): a test sets it before rendering. What a mocked router cannot
 * express is the moment those two disagree, so the remount-during-a-namespace-switch regression
 * is pinned on a REAL router in tests/traverse-namespace-switch.test.tsx.
 */
let searchParams: Record<string, unknown> = {};
vi.mock("@tanstack/react-router", () => ({
  useNavigate: () => navigateMock,
  useParams: () => ({ ns: "default" }),
  useRouterState: ({
    select,
  }: {
    select: (s: { location: { search: Record<string, unknown> } }) => unknown;
  }) => select({ location: { search: searchParams } }),
}));

const findPathsMock =
  vi.fn<(i: InstanceConfig, from: number, to: number, spec: PathSpecification) => Promise<PathREST[] | null>>();
let storedList: StoredQuerySummaryREST[] = [];
/** Indirected so a test can leave the library PENDING or make it fail (the tab count). */
let library: () => Promise<StoredQuerySummaryREST[]> = async () => storedList;

vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    getStatus: async () => STATUS,
    findPaths: (i: InstanceConfig, from: number, to: number, spec: PathSpecification) =>
      findPathsMock(i, from, to, spec),
    listStoredQueries: () => library(),
    listSubGraphSummaries: async () => [] as SubGraphSummary[],
    createSubGraph: async () => SUBGRAPH,
  };
});

import { TraverseScreen } from "../src/screens/TraverseScreen";

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

const SUBGRAPH: SubGraphSummary = { name: "island", vertexCount: 2, edgeCount: 1 };

const PATH: PathREST = {
  totalWeight: 3,
  pathElements: [
    { sourceVertexId: 1, targetVertexId: 9, edgeId: 4, edgePropertyId: "knows", weight: 3 },
  ],
};

function summary(
  name: string,
  kind: StoredQuerySummaryREST["kind"],
  compileState: StoredQuerySummaryREST["compileState"] = "Compiled",
): StoredQuerySummaryREST {
  return { name, kind, description: null, createdAt: "", compileState };
}

function renderScreen() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <TraverseScreen />
    </QueryClientProvider>,
  );
}

/** The workspace this screen reads: same-origin instance, "default" namespace. */
const workspace = () => getInstanceStore("local", "default").getState();

const tab = (id: TraverseTab) => screen.getByTestId(`traverse-tab-${id}`);

/**
 * A control that exists unconditionally inside each panel. Panel visibility is read through
 * it rather than through the tabpanel wrapper: `hidden` removes the wrapper from the
 * accessibility tree, so a role query would have to opt into hidden nodes and then prove
 * nothing about whether the operator can see the form.
 */
const MARKER: Record<TraverseTab, string> = {
  path: "path-run",
  subgraph: "sg-create",
  stored: "stored-queries-all",
};

function expectActiveTab(active: TraverseTab) {
  for (const id of TRAVERSE_TABS) {
    expect(tab(id), `${id} tab`).toHaveAttribute("aria-selected", String(id === active));
    const marker = screen.getByTestId(MARKER[id]);
    if (id === active) expect(marker, `${id} panel`).toBeVisible();
    else expect(marker, `${id} panel`).not.toBeVisible();
  }
}

beforeEach(() => {
  resetInstanceStoresForTests();
  localStorage.clear();
  navigateMock.mockReset();
  searchParams = {};
  storedList = [];
  library = async () => storedList;
  findPathsMock.mockReset().mockResolvedValue([]);
});

describe("the Traverse tab strip", () => {
  it("renders three tabs, opens on Path finding, and keeps the other forms mounted", () => {
    renderScreen();

    const tabs = screen.getAllByRole("tab");
    expect(tabs).toHaveLength(3);
    expect(tab("path")).toHaveTextContent("Path finding");
    expect(tab("subgraph")).toHaveTextContent("Subgraph builder");
    expect(tab("stored")).toHaveTextContent("Stored queries");

    expectActiveTab("path");
    // Mounted, not rendered-on-demand: this is the whole mechanism behind the state
    // preservation the next test pins.
    expect(screen.getByTestId("sg-create")).toBeInTheDocument();
    expect(screen.getByTestId("stored-queries-all")).toBeInTheDocument();
  });

  it("clicking a tab REPLACES the URL and remembers the choice", async () => {
    const user = userEvent.setup();
    renderScreen();

    await user.click(tab("subgraph"));

    expectActiveTab("subgraph");
    // A tab is not a history entry: Back must leave the screen, not walk the tabs.
    expect(navigateMock).toHaveBeenCalledWith({
      to: "/q/$ns/traverse",
      params: { ns: "default" },
      search: { tab: "subgraph" },
      replace: true,
    });
    // Remembered per instance-and-namespace, because a context switch rewrites the leaf
    // WITHOUT the search param.
    expect(workspace().traverseTab).toBe("subgraph");
  });
});

/**
 * Acceptance criterion 4, and the reason the merge is a tab strip rather than two screens: the
 * draft is persisted and would survive a remount anyway, but the open advanced tier and the
 * result set are local component state and exist only as long as the panel stays mounted.
 */
describe("switching tabs loses nothing", () => {
  it("preserves the path draft, the open advanced tier and the run result across a round trip", async () => {
    const user = userEvent.setup();
    findPathsMock.mockResolvedValue([PATH]);
    renderScreen();

    await user.type(screen.getByTestId("path-from"), "1");
    await user.type(screen.getByTestId("path-to"), "9");
    await user.click(screen.getByTestId("toggle-advanced"));
    expect(screen.getByTestId("advanced-slots")).toBeVisible();

    await user.click(screen.getByTestId("path-run"));
    await waitFor(() => expect(screen.getByTestId("path-weight-0")).toBeInTheDocument());

    await user.click(tab("subgraph"));
    expectActiveTab("subgraph");

    await user.click(tab("path"));
    expectActiveTab("path");
    expect(screen.getByTestId("path-from")).toHaveValue("1");
    expect(screen.getByTestId("path-to")).toHaveValue("9");
    expect(screen.getByTestId("advanced-slots")).toBeVisible();
    expect(screen.getByTestId("path-weight-0")).toHaveTextContent("3");
    // Nothing re-ran on the way back: a tab switch must not re-issue the query whose result
    // it just preserved.
    expect(findPathsMock).toHaveBeenCalledTimes(1);
  });

  it("preserves the subgraph builder's transient result line too", async () => {
    const user = userEvent.setup();
    renderScreen();

    await user.click(tab("subgraph"));
    await user.type(screen.getByTestId("sg-name"), "island");
    await user.click(screen.getByTestId("sg-create"));
    expect(await screen.findByTestId("subgraph-message")).toHaveTextContent("Created 'island'");

    await user.click(tab("path"));
    await user.click(tab("subgraph"));

    // The other panel's local state. Its draft would survive a remount; this line, which is
    // the only record that the create succeeded, would not.
    expect(screen.getByTestId("subgraph-message")).toHaveTextContent("Created 'island'");
  });
});

describe("which tab opens", () => {
  it("honours ?tab=subgraph", () => {
    searchParams = { tab: "subgraph" };
    renderScreen();
    expectActiveTab("subgraph");
  });

  it("honours ?tab=stored", () => {
    searchParams = { tab: "stored" };
    renderScreen();
    expectActiveTab("stored");
  });

  it("remembers a deep-linked tab, so a later context switch does not undo it", () => {
    searchParams = { tab: "stored" };
    renderScreen();

    // The switchers navigate to the leaf with no search param, so the store is the only
    // thing that can carry the operator back to the tab their link named.
    expect(workspace().traverseTab).toBe("stored");
  });

  it("restores the tab this workspace was last left on when no ?tab= is given", () => {
    localStorage.setItem(
      "f8.workspace.local",
      JSON.stringify({ state: { traverseTab: "subgraph" }, version: 0 }),
    );
    resetInstanceStoresForTests();

    renderScreen();
    expectActiveTab("subgraph");
  });

  it("falls back to Path finding on an unknown ?tab= instead of hiding every panel", () => {
    searchParams = { tab: "nonsense" };
    renderScreen();

    // A tab id nothing matches would leave all three panels hidden - a blank screen with a
    // tab strip on top.
    expectActiveTab("path");
  });

  it("falls back to Path finding on an unknown PERSISTED tab as well", () => {
    localStorage.setItem(
      "f8.workspace.local",
      JSON.stringify({ state: { traverseTab: "renamed-in-a-later-build" }, version: 0 }),
    );
    resetInstanceStoresForTests();

    renderScreen();
    expectActiveTab("path");
  });
});

describe("the Stored queries tab", () => {
  it("carries the library count in its label", async () => {
    storedList = [summary("adults", "Path"), summary("triangle", "SubGraph")];
    renderScreen();

    await waitFor(() => expect(tab("stored")).toHaveTextContent("2"));
    expect(tab("stored")).toHaveTextContent("Stored queries");
  });

  it("shows NO count until the library answers", () => {
    library = () => new Promise(() => {});
    renderScreen();

    // "0" is a different claim than "not known yet", and it would also shift the label's
    // width once the real number arrived.
    expect(tab("stored").textContent).toBe("Stored queries");
  });

  it("shows no count when the library request FAILS either", async () => {
    const user = userEvent.setup();
    library = async () => {
      throw new Error("unreachable");
    };
    renderScreen();
    await user.click(tab("stored"));

    // The failure is reported inside the panel; the label just stays silent rather than
    // claiming an empty library.
    await waitFor(() => expect(screen.getByRole("alert")).toBeVisible());
    expect(tab("stored").textContent).toBe("Stored queries");
  });

  it("Use on a SubGraph entry opens the Subgraph builder with the entry selected", async () => {
    const user = userEvent.setup();
    storedList = [summary("adults", "Path"), summary("triangle", "SubGraph")];
    renderScreen();

    await user.click(tab("stored"));
    await user.click(await screen.findByTestId("stored-query-use-triangle"));

    // The entry names its scenario, so the operator never has to: the tab follows the kind.
    expectActiveTab("subgraph");
    expect(workspace().subgraphDraft.filterSource).toBe("stored");
    expect(workspace().subgraphDraft.storedQuery).toBe("triangle");
    // Selected in the picker that actually runs it, not merely stored in the draft.
    expect(screen.getByTestId("stored-query-select")).toHaveValue("triangle");
    expect(workspace().pathDraft.storedQuery).toBe("");
    // Use is a REAL tab change, URL included. Showing the panel without navigating passes every
    // assertion above while leaving the address bar on ?tab=stored, so a reload - or a copied
    // link - reopens the library instead of the builder the entry was selected into.
    expect(navigateMock).toHaveBeenCalledWith({
      to: "/q/$ns/traverse",
      params: { ns: "default" },
      search: { tab: "subgraph" },
      replace: true,
    });
    // The library is no longer under the picker (it is a tab of its own), so the picker has to
    // say where management lives. One string pinned here breaks the gate if it moves again.
    expect(screen.getByTestId("stored-query-picker")).toHaveTextContent(
      "manage on the Stored queries tab",
    );
  });

  it("Use on a Path entry opens Path finding with the entry selected", async () => {
    const user = userEvent.setup();
    storedList = [summary("adults", "Path"), summary("triangle", "SubGraph")];
    renderScreen();

    await user.click(tab("stored"));
    await user.click(await screen.findByTestId("stored-query-use-adults"));

    expectActiveTab("path");
    expect(workspace().pathDraft.filterSource).toBe("stored");
    expect(workspace().pathDraft.storedQuery).toBe("adults");
    expect(screen.getByTestId("stored-query-select")).toHaveValue("adults");
    expect(workspace().subgraphDraft.storedQuery).toBe("");
    // The URL follows the tab here too, even though "path" is the default one: a deep link that
    // kept ?tab=stored would reopen the library over the selection it just made.
    expect(navigateMock).toHaveBeenCalledWith({
      to: "/q/$ns/traverse",
      params: { ns: "default" },
      search: { tab: "path" },
      replace: true,
    });
  });
});
