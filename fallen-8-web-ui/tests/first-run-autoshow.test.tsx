// MIT License
//
// first-run-autoshow.test.tsx
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
import { act, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";
import type { DurabilityREST, StatusREST } from "../src/api/types";
import type { InstanceConfig } from "../src/instances/types";
import { resetInstanceStoresForTests } from "../src/state/instanceStore";

/**
 * The first-run auto-show, now a shell-level overlay rather than a screen's empty state
 * (feature studio-first-run). What is pinned here is the decision to open at all and what
 * closing costs, because both are easy to get wrong in a modal: an auto-opening dialog that
 * did not remember being closed would reappear on every navigation, and one that opened over
 * a rejected credential or over a durability warning would be worse than none.
 *
 * <FirstRunShow> itself (beats, stepping, reduced motion, the handoff) is covered by
 * first-run-show.test.tsx, and the dismissal store by first-run-store.test.ts.
 */

const navigateMock = vi.fn(() => Promise.resolve());
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
  useNavigate: () => navigateMock,
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
import { useFirstRun } from "../src/firstrun/firstRunStore";

const healthy: DurabilityREST = {
  walEnabled: true,
  degraded: false,
  recoveryRan: true,
  lastRecoveryTruncated: false,
  lastRecoveryReplayedEntries: 0,
  lastCheckpointDroppedIndices: 0,
};

/** The bound key the dismissal memory is filed under: `<instance id>/<namespace>`. */
const BOUND_KEY = `${SAME_ORIGIN_INSTANCE.id}/default`;

function status(
  vertexCount: number | null,
  extra: Partial<StatusREST> = {},
): StatusREST {
  return {
    vertexCount: vertexCount as number,
    edgeCount: 0,
    usedMemory: 0,
    availableIndexPlugins: [],
    availablePathPlugins: [],
    availableAnalyticsPlugins: [],
    availableServicePlugins: [],
    apiKeyRequired: false,
    authenticated: false,
    durability: healthy,
    ...extra,
  };
}

/** The client the current render is using, so a test can push a refetch through the shared rows. */
let client: QueryClient;

function renderShell(path = "/q/default/browser") {
  currentPath = path;
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
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
  navigateMock.mockClear();
  listIntegrationProvidersMock.mockReset().mockResolvedValue([]);
  statusMock.mockReset().mockResolvedValue(status(0));
  // The per-instance maps are reset too: the re-latch test below switches the active namespace to
  // "flights", and without this it stays active for every test after it - which silently moves
  // BOUND_KEY out from under them, so a dismissal written under one key is read under another.
  useRegistry.setState({
    instances: [SAME_ORIGIN_INSTANCE],
    activeId: SAME_ORIGIN_INSTANCE.id,
    activeNamespaces: {},
    namespaceSupport: {},
  });
  useFirstRun.setState({ dismissed: {}, replayOpen: false });
  // Reduced motion: the show rests on its handoff instead of autoplaying on timers.
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

describe("the auto-show decides to open", () => {
  it("opens for a newcomer on an empty namespace, on whichever graph screen they are on", async () => {
    renderShell("/q/default/query");

    await waitFor(() => expect(screen.getByTestId("first-run-overlay")).toBeInTheDocument());
    // The "auto" wording, not the replay wording: this newcomer's graph really is empty.
    expect(screen.getByText(/Your graph is empty/i)).toBeInTheDocument();
  });

  it("opens on the Canvas too, so it is not one screen's empty state in disguise", async () => {
    renderShell("/q/default/canvas");

    await waitFor(() => expect(screen.getByTestId("first-run-overlay")).toBeInTheDocument());
  });

  it("stays shut on Connect - a newcomer there is mid-registration, not asking for a tour", async () => {
    // Measured, not theorised: an auto-opening modal over Connect lands on top of a half-finished
    // instance registration and blocks the radio that activates it.
    renderShell("/");

    await waitFor(() => expect(screen.getByTestId("health-chip")).toHaveTextContent("online"));
    expect(screen.queryByTestId("first-run-overlay")).toBeNull();
  });

  it("stays shut on the other Fallen-8-level screens (Save games, Integrations)", async () => {
    renderShell("/save-games");

    await waitFor(() => expect(screen.getByTestId("health-chip")).toHaveTextContent("online"));
    expect(screen.queryByTestId("first-run-overlay")).toBeNull();
  });

  it("stays shut on a populated namespace", async () => {
    statusMock.mockResolvedValue(status(3));

    renderShell();

    await waitFor(() => expect(screen.getByTestId("health-chip")).toHaveTextContent("online"));
    expect(screen.queryByTestId("first-run-overlay")).toBeNull();
  });

  it("stays shut once dismissed for that namespace", async () => {
    useFirstRun.setState({ dismissed: { [BOUND_KEY]: true } });

    renderShell();

    await waitFor(() => expect(screen.getByTestId("health-chip")).toHaveTextContent("online"));
    expect(screen.queryByTestId("first-run-overlay")).toBeNull();
  });

  it("stays shut while the instance rejected the credential, even on an empty graph", async () => {
    // /status reports real counts to an unauthorized caller, so "empty" alone is not consent to
    // greet someone: the shell is showing them the credential guard, not a workspace.
    statusMock.mockResolvedValue(status(0, { apiKeyRequired: true, authenticated: false }));

    renderShell();

    await waitFor(() =>
      expect(screen.getByTestId("health-chip")).toHaveTextContent("unauthorized"),
    );
    expect(screen.queryByTestId("first-run-overlay")).toBeNull();
  });

  it("stays shut while the instance is unreachable (no count is known at all)", async () => {
    statusMock.mockRejectedValue(new Error("connection refused"));

    renderShell();

    await waitFor(() =>
      expect(screen.getByTestId("health-chip")).toHaveTextContent("unreachable"),
    );
    expect(screen.queryByTestId("first-run-overlay")).toBeNull();
  });

  it("stays shut for a namespace the server did not load, which reports NO count", async () => {
    // Null counts are not zero. Reading them as empty would greet an operator with a walkthrough
    // over a graph that is intact on disk.
    statusMock.mockResolvedValue(status(null));

    renderShell();

    await waitFor(() => expect(screen.getByTestId("health-chip")).toHaveTextContent("online"));
    expect(screen.queryByTestId("first-run-overlay")).toBeNull();
  });

  it("stays shut while there is no instance to be a newcomer on", async () => {
    useRegistry.setState({ instances: [], activeId: null });

    renderShell();

    await waitFor(() => expect(screen.getByText(/No instance selected/)).toBeInTheDocument());
    expect(screen.queryByTestId("first-run-overlay")).toBeNull();
  });
});

describe("closing an auto-opened show", () => {
  it("records the dismissal from the Close button, so it does not come back", async () => {
    const user = userEvent.setup();
    renderShell();
    await waitFor(() => expect(screen.getByTestId("first-run-overlay")).toBeInTheDocument());

    await user.click(screen.getByTestId("first-run-overlay-close"));

    await waitFor(() => expect(screen.queryByTestId("first-run-overlay")).toBeNull());
    expect(useFirstRun.getState().dismissed[BOUND_KEY]).toBe(true);
  });

  it("records the dismissal from Escape as well - every close route is the same decision", async () => {
    const user = userEvent.setup();
    renderShell();
    await waitFor(() => expect(screen.getByTestId("first-run-overlay")).toBeInTheDocument());

    await user.keyboard("{Escape}");

    await waitFor(() => expect(screen.queryByTestId("first-run-overlay")).toBeNull());
    expect(useFirstRun.getState().dismissed[BOUND_KEY]).toBe(true);
  });

  it("records the dismissal from Explore on my own", async () => {
    const user = userEvent.setup();
    renderShell();
    await waitFor(() => expect(screen.getByTestId("first-run-handoff")).toBeInTheDocument());

    await user.click(screen.getByTestId("first-run-explore"));

    await waitFor(() => expect(screen.queryByTestId("first-run-overlay")).toBeNull());
    expect(useFirstRun.getState().dismissed[BOUND_KEY]).toBe(true);
  });

  it("dismisses AND navigates on Browse sample graphs", async () => {
    const user = userEvent.setup();
    renderShell();
    await waitFor(() => expect(screen.getByTestId("first-run-handoff")).toBeInTheDocument());

    await user.click(screen.getByTestId("first-run-browse-samples"));

    expect(useFirstRun.getState().dismissed[BOUND_KEY]).toBe(true);
    expect(navigateMock).toHaveBeenCalledWith({
      to: "/q/$ns/samples",
      params: { ns: "default" },
    });
  });

  it("dismisses AND navigates to the import screen on Import your own data", async () => {
    const user = userEvent.setup();
    renderShell();
    await waitFor(() => expect(screen.getByTestId("first-run-handoff")).toBeInTheDocument());

    await user.click(screen.getByTestId("first-run-import"));

    expect(useFirstRun.getState().dismissed[BOUND_KEY]).toBe(true);
    expect(navigateMock).toHaveBeenCalledWith({ to: "/save-games" });
  });
});

/**
 * The two bugs the merge council found, both of which the suite above was green through. Neither is
 * about whether the show CAN open - it is about `open` having been derived live from a count that
 * the change feed moves under it, and about `open` having two reasons while closing cleared one.
 */
describe("the auto-show does not ambush work that passes through empty", () => {
  it("stays shut when a POPULATED namespace empties under the operator", async () => {
    // The primary path: loading a sample is a tabula rasa followed by an import, so the count is 0
    // for the duration of the import. Deriving `open` from the live count put the walkthrough over
    // the Samples screen for that whole window, with the loader's own progress behind its scrim.
    statusMock.mockResolvedValue(status(3));
    renderShell("/q/default/samples");
    await waitFor(() => expect(screen.getByTestId("health-chip")).toHaveTextContent("online"));
    expect(screen.queryByTestId("first-run-overlay")).toBeNull();

    // The graph is wiped by the load, and the feed pushes the new count within ~300ms.
    statusMock.mockResolvedValue(status(0));
    await act(async () => {
      await client.invalidateQueries();
    });

    // A graph emptying under you is not a first run.
    expect(screen.queryByTestId("first-run-overlay")).toBeNull();
  });

  it("still opens for a namespace that was ALREADY empty on arrival", async () => {
    // The latch must not be a blanket suppression: the actual newcomer case still has to work,
    // including after the count is refreshed by the feed.
    renderShell("/q/default/samples");
    await waitFor(() => expect(screen.getByTestId("first-run-overlay")).toBeInTheDocument());

    await act(async () => {
      await client.invalidateQueries();
    });

    expect(screen.getByTestId("first-run-overlay")).toBeInTheDocument();
  });

  it("does not come back when the operator POPULATES the namespace it greeted them on", async () => {
    // The regression scenario 13 of the e2e suite caught: the show fires on a fresh namespace, the
    // operator dismisses it and generates a graph, and `clearIfPopulated` re-arms the dismissal -
    // so a latch that still said "arrived empty" re-opened the walkthrough over the Benchmark
    // screen mid-run. Populating a graph is the opposite of needing an introduction to one.
    const user = userEvent.setup();
    renderShell("/q/default/benchmarks");
    await waitFor(() => expect(screen.getByTestId("first-run-overlay")).toBeInTheDocument());
    await user.click(screen.getByTestId("first-run-overlay-close"));
    await waitFor(() => expect(screen.queryByTestId("first-run-overlay")).toBeNull());

    // The generate lands, the feed pushes the count, and the dismissal is re-armed by design.
    statusMock.mockResolvedValue(status(200));
    await act(async () => {
      await client.invalidateQueries();
    });
    await waitFor(() => expect(useFirstRun.getState().dismissed[BOUND_KEY]).toBeUndefined());

    expect(screen.queryByTestId("first-run-overlay")).toBeNull();
  });

  it("re-latches per namespace, so an empty one still greets after a populated one", async () => {
    statusMock.mockImplementation((i) =>
      Promise.resolve(i.namespace === "flights" ? status(0) : status(7)),
    );
    renderShell("/q/default/browser");
    await waitFor(() => expect(screen.getByTestId("health-chip")).toHaveTextContent("online"));
    expect(screen.queryByTestId("first-run-overlay")).toBeNull();

    // Switching namespace changes the bound key, which is what the latch is keyed by.
    await act(async () => {
      useRegistry.getState().setActiveNamespace(SAME_ORIGIN_INSTANCE.id, "flights");
    });

    await waitFor(() => expect(screen.getByTestId("first-run-overlay")).toBeInTheDocument());
  });
});

describe("closing the overlay clears BOTH reasons it can be open", () => {
  it("a replay closed on an empty graph does not hand over to the auto path", async () => {
    // Reachable in two clicks on a fresh install: Intro on the Connect screen (where the auto path
    // is silent), then "Browse sample graphs" - which closed the replay and navigated onto a scoped
    // screen, where the auto path re-opened the dialog the user had just dismissed.
    const user = userEvent.setup();
    currentPath = "/";
    renderShell("/");
    await waitFor(() => expect(screen.getByTestId("nav-replay-intro")).toBeInTheDocument());

    await user.click(screen.getByTestId("nav-replay-intro"));
    await waitFor(() => expect(screen.getByTestId("first-run-overlay")).toBeInTheDocument());
    await user.click(screen.getByTestId("first-run-browse-samples"));

    // The navigation is mocked, so assert the STATE that would have re-opened it: the dismissal is
    // recorded even though it was the replay path that was showing.
    expect(navigateMock).toHaveBeenCalledWith({
      to: "/q/$ns/samples",
      params: { ns: "default" },
    });
    expect(useFirstRun.getState().dismissed[BOUND_KEY]).toBe(true);
    await waitFor(() => expect(screen.queryByTestId("first-run-overlay")).toBeNull());
  });

  it("a replay closed on a POPULATED graph still records nothing", async () => {
    // The other half of the rule: with the auto path not armed, the replay stays a pure viewer.
    const user = userEvent.setup();
    statusMock.mockResolvedValue(status(3));
    renderShell();
    await waitFor(() => expect(screen.getByTestId("nav-replay-intro")).toBeInTheDocument());

    await user.click(screen.getByTestId("nav-replay-intro"));
    await waitFor(() => expect(screen.getByTestId("first-run-overlay")).toBeInTheDocument());
    await user.click(screen.getByTestId("first-run-overlay-close"));

    await waitFor(() => expect(screen.queryByTestId("first-run-overlay")).toBeNull());
    expect(useFirstRun.getState().dismissed[BOUND_KEY]).toBeUndefined();
  });
});

describe("the replay path is independent of the auto path", () => {
  it("re-arms the auto-show once the namespace is seen non-empty", async () => {
    useFirstRun.setState({ dismissed: { [BOUND_KEY]: true } });
    statusMock.mockResolvedValue(status(3));

    renderShell();

    // The dismissal is cleared by observing data, so a namespace that genuinely empties later
    // gets the intro again rather than being silently marked as "seen".
    await waitFor(() => expect(useFirstRun.getState().dismissed[BOUND_KEY]).toBeUndefined());
    expect(screen.queryByTestId("first-run-overlay")).toBeNull();
  });

  it("opens from the rail on a POPULATED namespace, and closing leaves no dismissal behind", async () => {
    const user = userEvent.setup();
    statusMock.mockResolvedValue(status(3));
    renderShell();
    await waitFor(() => expect(screen.getByTestId("nav-replay-intro")).toBeInTheDocument());

    await user.click(screen.getByTestId("nav-replay-intro"));

    await waitFor(() => expect(screen.getByTestId("first-run-overlay")).toBeInTheDocument());
    // The replay wording: nothing is claimed about this graph being empty, because it is not.
    expect(screen.getByText(/Pick up where you were/i)).toBeInTheDocument();

    await user.click(screen.getByTestId("first-run-overlay-close"));

    await waitFor(() => expect(screen.queryByTestId("first-run-overlay")).toBeNull());
    expect(useFirstRun.getState().dismissed[BOUND_KEY]).toBeUndefined();
  });

  it("opens from the rail while DISCONNECTED, where the auto path never would", async () => {
    const user = userEvent.setup();
    statusMock.mockRejectedValue(new Error("down"));
    renderShell();
    await waitFor(() =>
      expect(screen.getByTestId("health-chip")).toHaveTextContent("unreachable"),
    );

    await user.click(screen.getByTestId("nav-replay-intro"));

    await waitFor(() => expect(screen.getByTestId("first-run-overlay")).toBeInTheDocument());
  });
});
