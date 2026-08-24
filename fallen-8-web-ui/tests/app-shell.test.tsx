// MIT License
//
// app-shell.test.tsx
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

/// <reference types="node" />

import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";
import type { InstanceConfig } from "../src/instances/types";
import type { StatusREST } from "../src/api/types";
import { ApiError } from "../src/api/client";

/**
 * Nav gating in the app shell: every entry but Connect stays locked until the ACTIVE
 * instance's /status probe answers AND authorizes the credential (server contract on
 * StatusREST.ApiKeyRequired). Pins all four connection states, the deep-link guard,
 * instance switching, and back-compat with servers predating the auth fields.
 */

let currentPath = "/";
const navigateMock = vi.fn(() => Promise.resolve());
vi.mock("@tanstack/react-router", () => ({
  Link: ({
    to,
    children,
    // Swallow router-only props (params) so they never land on the anchor element.
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

const statusMock =
  vi.fn<(instance: InstanceConfig, signal?: AbortSignal) => Promise<StatusREST>>();
const listIntegrationProvidersMock =
  vi.fn<(instance: InstanceConfig, signal?: AbortSignal) => Promise<unknown>>();
const listNamespacesMock =
  vi.fn<(instance: InstanceConfig, signal?: AbortSignal) => Promise<unknown>>();
vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    getStatus: (i: InstanceConfig, s?: AbortSignal) => statusMock(i, s),
    listIntegrationProviders: (i: InstanceConfig, s?: AbortSignal) =>
      listIntegrationProvidersMock(i, s),
    listNamespaces: (i: InstanceConfig, s?: AbortSignal) => listNamespacesMock(i, s),
  };
});

/** A two-namespace inventory, so the switcher has something to switch TO. */
const INVENTORY = {
  namespaces: [
    {
      name: "default",
      state: "ready" as const,
      vertexCount: 1,
      edgeCount: 0,
      createdAt: "",
      loadOnStartupEnabled: null,
    },
    {
      name: "flights",
      state: "ready" as const,
      vertexCount: 2,
      edgeCount: 1,
      createdAt: "",
      loadOnStartupEnabled: null,
    },
  ],
  maxNamespaces: null,
};

import { AppShell } from "../src/app/AppShell";
import { SAME_ORIGIN_INSTANCE, useRegistry } from "../src/instances/registry";
import { getEventFeed, resetEventFeedsForTests } from "../src/state/eventFeed";

const STATUS: StatusREST = {
  vertexCount: 201,
  edgeCount: 1000,
  usedMemory: 0,
  availableIndexPlugins: [],
  availablePathPlugins: [],
  availableAnalyticsPlugins: [],
  availableServicePlugins: [],
  apiKeyRequired: false,
  authenticated: false,
};

const GATED = [
  "nav-samples",
  "nav-save-games",
  "nav-browser",
  "nav-query",
  "nav-path",
  "nav-subgraph",
  "nav-analytics",
  "nav-plugins",
  "nav-canvas",
  // Namespace-scoped since generation writes the addressed graph, so it is gated like the rest.
  "nav-benchmark",
];

function renderShell(children: ReactNode = <div data-testid="screen" />, path = "/") {
  currentPath = path;
  const client = new QueryClient();
  return render(
    <QueryClientProvider client={client}>
      <AppShell>{children}</AppShell>
    </QueryClientProvider>,
  );
}

function expectLocked(testid: string) {
  const el = screen.getByTestId(testid);
  expect(el.tagName).not.toBe("A");
  expect(el).toHaveAttribute("aria-disabled", "true");
}

function expectUnlocked(testid: string) {
  const el = screen.getByTestId(testid);
  expect(el.tagName).toBe("A");
  expect(el).not.toHaveAttribute("aria-disabled");
}

beforeEach(() => {
  statusMock.mockReset();
  navigateMock.mockClear();
  listIntegrationProvidersMock.mockReset().mockResolvedValue([]);
  listNamespacesMock.mockReset().mockResolvedValue(INVENTORY);
  // The per-instance maps are reset too, not just the instance list: a test that switches the
  // active namespace would otherwise leave "flights" active for every test after it, and the
  // event-feed scope (which collapses "/default" onto the bare id) would silently look elsewhere.
  useRegistry.setState({
    instances: [SAME_ORIGIN_INSTANCE],
    activeId: SAME_ORIGIN_INSTANCE.id,
    activeNamespaces: {},
    namespaceSupport: {},
  });
});

describe("the integrations entry is HIDDEN rather than disabled when the capability is absent", () => {
  it("is absent on a 403, which is what a secured instance answers", async () => {
    statusMock.mockResolvedValue(STATUS);
    listIntegrationProvidersMock.mockRejectedValue(
      new ApiError(403, "/integrations/providers", "capability off"),
    );

    renderShell();

    // Hidden, not disabled: an instance either has an integrations runtime or has nothing to say
    // about integrations, and a permanently greyed icon would advertise a deployable that is not
    // there.
    await waitFor(() => expect(screen.queryByTestId("nav-integrations")).not.toBeInTheDocument());
  });

  it("is absent on a 401 too, which is what an OPEN instance answers", async () => {
    statusMock.mockResolvedValue(STATUS);
    listIntegrationProvidersMock.mockRejectedValue(
      new ApiError(401, "/integrations/providers", "unauthorized"),
    );

    renderShell();

    await waitFor(() => expect(screen.queryByTestId("nav-integrations")).not.toBeInTheDocument());
  });

  it("is present once the runtime answers", async () => {
    statusMock.mockResolvedValue(STATUS);
    listIntegrationProvidersMock.mockResolvedValue([]);

    renderShell();

    await waitFor(() => expect(screen.getByTestId("nav-integrations")).toBeInTheDocument());
  });
});

describe("nav gating on connection state", () => {
  it("locks every entry but Connect while the health probe is pending", () => {
    statusMock.mockReturnValue(new Promise(() => {}));
    renderShell();

    expectUnlocked("nav-connect");
    for (const id of GATED) expectLocked(id);
    expect(screen.getByTestId("health-chip")).toHaveTextContent("checking");
  });

  it("unlocks the nav once the active instance is reachable and authorized", async () => {
    statusMock.mockResolvedValue({ ...STATUS, apiKeyRequired: true, authenticated: true });
    renderShell();

    await waitFor(() => expectUnlocked("nav-samples"));
    for (const id of GATED) expectUnlocked(id);
    expect(screen.getByTestId("health-chip")).toHaveTextContent("online");
  });

  it("keeps the nav locked when the instance rejects the credential (missing/wrong API key)", async () => {
    statusMock.mockResolvedValue({ ...STATUS, apiKeyRequired: true, authenticated: false });
    renderShell();

    await waitFor(() =>
      expect(screen.getByTestId("health-chip")).toHaveTextContent("unauthorized"),
    );
    for (const id of GATED) expectLocked(id);
    expectUnlocked("nav-connect");
  });

  it("keeps the nav locked when the instance is unreachable", async () => {
    statusMock.mockRejectedValue(new Error("connection refused"));
    renderShell();

    await waitFor(() =>
      expect(screen.getByTestId("health-chip")).toHaveTextContent("unreachable"),
    );
    for (const id of GATED) expectLocked(id);
  });

  it("treats a status document without auth fields (older server) as authorized", async () => {
    const { apiKeyRequired: _r, authenticated: _a, ...preAuthStatus } = STATUS;
    statusMock.mockResolvedValue(preAuthStatus as StatusREST);
    renderShell();

    await waitFor(() => expectUnlocked("nav-samples"));
    expect(screen.getByTestId("health-chip")).toHaveTextContent("online");
  });

  it("locks the nav and hides the chip when no instance is registered", () => {
    useRegistry.setState({ instances: [], activeId: null });
    renderShell();

    for (const id of GATED) expectLocked(id);
    expect(screen.queryByTestId("health-chip")).not.toBeInTheDocument();
    expect(screen.getByText(/No instance selected/)).toBeInTheDocument();
  });

  it("re-locks the nav when switching to an instance that rejects the credential", async () => {
    statusMock.mockImplementation((i) =>
      i.id === SAME_ORIGIN_INSTANCE.id
        ? Promise.resolve(STATUS)
        : Promise.resolve({ ...STATUS, apiKeyRequired: true, authenticated: false }),
    );
    renderShell();
    await waitFor(() => expectUnlocked("nav-samples"));

    act(() => {
      const prod = useRegistry
        .getState()
        .addInstance({ name: "prod", baseUrl: "http://prod:17408", auth: { kind: "none" } });
      useRegistry.getState().setActive(prod.id);
    });

    await waitFor(() => expectLocked("nav-samples"));
    await waitFor(() =>
      expect(screen.getByTestId("health-chip")).toHaveTextContent("unauthorized"),
    );
  });
});

/**
 * Top-bar column split. The namespace side carries three things that grow - the switcher with its
 * live counts (a graph can hold millions of vertices and edges), the endpoint prefix, and the
 * right-pinned status chips - while the instance side only ever shows a short registry name. An
 * even split starved it: measured at a 1440px viewport, the namespace group had 326px and both the
 * counts and the endpoint truncated; three quarters gives it 661px and neither does. jsdom computes
 * no layout, so the declaration that decides it is what gets pinned here.
 */
describe("top bar column split", () => {
  it("gives the namespace side three quarters of the bar, not half", () => {
    statusMock.mockResolvedValue(STATUS);
    renderShell();

    const header = document.querySelector("header");
    expect(header).not.toBeNull();
    expect(header!.className).toContain("grid-cols-[1fr_3fr]");
    expect(header!.className).not.toContain("grid-cols-2");
  });
});


/**
 * The icon rail's overflow. The rail is a flex item of a `h-full` row, so `align-items: stretch`
 * pins its height to the shell and its CHILDREN spill out of that box on a short viewport - which
 * is how `bg-panel` and `border-r` came to stop short of the bottom with the last two entries
 * drawn on bare page background. Fixing it by removing an entry would only move the threshold, so
 * what is pinned here is the structure that makes the height irrelevant: the <nav> owns no
 * overflowable content and a child scroll box takes the remaining height.
 *
 * jsdom computes no layout, so the declarations that decide it are the assertion - and one of them
 * is NOT reachable through the DOM: `overflow-y: auto` lives on `.rail-scroll` in src/index.css,
 * which Vitest never applies (no `css: true` in vite.config.ts). Asserting only that the class name
 * is present would let someone delete the whole stylesheet rule with all 1076 tests still green, so
 * the rule itself is read from source, the way section-help.test.tsx reads the docs directory.
 */
describe("icon rail overflow", () => {
  it("scrolls the entries inside the rail instead of overflowing its background", () => {
    statusMock.mockResolvedValue(STATUS);
    renderShell();

    const rail = document.querySelector("nav");
    const items = screen.getByTestId("nav-rail-items");

    // The painted surfaces live on the <nav>, which is never the thing that overflows.
    expect(rail!.className).toContain("bg-panel");
    expect(rail!.className).toContain("border-r");
    expect(rail!.className).not.toContain("overflow");

    // The scroll box is a child of the rail, takes the leftover height, and may shrink to it.
    expect(items.parentElement).toBe(rail);
    expect(items.className).toContain("rail-scroll");
    expect(items.className).toContain("flex-1");
    expect(items.className).toContain("min-h-0");
  });

  it("backs `.rail-scroll` with a real overflow rule, not just a class name", () => {
    // The half the DOM cannot show. Deleting this rule is what would bring the reported bug back,
    // and no rendered assertion in this suite would notice.
    const here = dirname(fileURLToPath(import.meta.url));
    const css = readFileSync(resolve(here, "..", "src", "index.css"), "utf8");
    const rule = /\.rail-scroll\s*\{([^}]*)\}/.exec(css);
    expect(rule, "src/index.css declares no .rail-scroll rule").not.toBeNull();
    expect(rule![1]).toMatch(/overflow-y:\s*auto/);
    // Never `display: none` on the bar: the entries below the fold have to stay reachable, and a
    // hidden scrollbar hides that there is anything down there.
    expect(css).not.toMatch(/\.rail-scroll::-webkit-scrollbar\s*\{[^}]*display:\s*none/);
  });

  it("keeps every entry, and the Intro control, inside that scroll box", () => {
    statusMock.mockResolvedValue(STATUS);
    renderShell();

    const items = screen.getByTestId("nav-rail-items");
    for (const id of ["nav-connect", ...GATED, "nav-replay-intro"]) {
      expect(items.contains(screen.getByTestId(id)), id).toBe(true);
    }
    // The logo is deliberately OUTSIDE it: it stays put while the entries scroll under it.
    expect(items.contains(screen.getByAltText("F8 Studio"))).toBe(false);
  });
});

/**
 * "You stay where you are." A context switch used to fall back to the Dashboard whenever the
 * current route was not a scoped screen, and the fallback was reached for real: switching a
 * namespace while working on the Connect screen threw the operator out of the panel they were in.
 * With no Dashboard there is nothing to fall back TO, so the rule is now explicit.
 */
describe("switching namespace keeps the screen", () => {
  async function switchToFlights() {
    const user = userEvent.setup();
    await waitFor(() => expect(screen.getByTestId("namespace-switcher")).toBeEnabled());
    await user.click(screen.getByTestId("namespace-switcher"));
    await user.click(await screen.findByTestId("namespace-option-flights"));
  }

  it("swaps only the namespace in the URL on a scoped route", async () => {
    statusMock.mockResolvedValue(STATUS);
    renderShell(<div data-testid="screen" />, "/q/default/subgraphs");

    await switchToFlights();

    expect(useRegistry.getState().activeNamespaces[SAME_ORIGIN_INSTANCE.id]).toBe("flights");
    expect(navigateMock).toHaveBeenCalledWith({
      to: "/q/$ns/subgraphs",
      params: { ns: "flights" },
    });
  });

  it("does not navigate at all from a flat route - Connect updates in place", async () => {
    statusMock.mockResolvedValue(STATUS);
    renderShell(<div data-testid="screen" />, "/");

    await switchToFlights();

    expect(useRegistry.getState().activeNamespaces[SAME_ORIGIN_INSTANCE.id]).toBe("flights");
    expect(navigateMock).not.toHaveBeenCalled();
    expect(screen.getByTestId("screen")).toBeInTheDocument();
  });

  it("does not navigate from the other flat routes either (Save games, Integrations)", async () => {
    statusMock.mockResolvedValue(STATUS);
    renderShell(<div data-testid="screen" />, "/save-games");

    await switchToFlights();

    expect(navigateMock).not.toHaveBeenCalled();
  });
});

describe("deep-link guard", () => {
  it("replaces gated screens when the credential is rejected", async () => {
    statusMock.mockResolvedValue({ ...STATUS, apiKeyRequired: true, authenticated: false });
    renderShell(<div data-testid="screen" />, "/q/default/browser");

    await screen.findByTestId("connection-guard");
    expect(screen.queryByTestId("screen")).not.toBeInTheDocument();
  });

  it("still renders the Connect screen when the credential is rejected", async () => {
    statusMock.mockResolvedValue({ ...STATUS, apiKeyRequired: true, authenticated: false });
    renderShell(<div data-testid="screen" />, "/");

    await waitFor(() =>
      expect(screen.getByTestId("health-chip")).toHaveTextContent("unauthorized"),
    );
    expect(screen.getByTestId("screen")).toBeInTheDocument();
  });

  it("keeps the current screen mounted while merely unreachable (a health blip must not discard work)", async () => {
    statusMock.mockRejectedValue(new Error("down"));
    renderShell(<div data-testid="screen" />, "/q/default/browser");

    await waitFor(() =>
      expect(screen.getByTestId("health-chip")).toHaveTextContent("unreachable"),
    );
    expect(screen.getByTestId("screen")).toBeInTheDocument();
    expectLocked("nav-browser");
  });
});

/**
 * The Events bell (feature studio-event-feed): signals without being clicked. The active
 * scope here is SAME_ORIGIN_INSTANCE + the "default" namespace, whose feed collapses onto
 * the bare instance id. useLiveChangeFeed is mocked to "connecting" file-wide, so the
 * bell renders its muted state unless the feed store says otherwise.
 */
describe("events bell", () => {
  beforeEach(() => {
    resetEventFeedsForTests();
    statusMock.mockResolvedValue(STATUS);
  });

  it("renders muted (no badge) while the stream is not live and nothing is unread", () => {
    renderShell();
    const bell = screen.getByTestId("event-feed-bell");
    expect(bell.className).toContain("opacity-60");
    expect(screen.queryByTestId("event-feed-badge")).not.toBeInTheDocument();
  });

  it("shows the interest-filtered unread count, display capped at 99+", () => {
    getEventFeed(SAME_ORIGIN_INSTANCE.id).setState({ unread: 3 });
    renderShell();
    expect(screen.getByTestId("event-feed-badge")).toHaveTextContent("3");

    act(() => getEventFeed(SAME_ORIGIN_INSTANCE.id).setState({ unread: 150 }));
    expect(screen.getByTestId("event-feed-badge")).toHaveTextContent("99+");
    expect(screen.getByTestId("event-feed-bell").getAttribute("aria-label")).toBe(
      "Events (99+ unread)",
    );
  });

  it("treats an unseen resync as a distinct warning, not a +1", () => {
    getEventFeed(SAME_ORIGIN_INSTANCE.id).setState({ resyncSinceOpen: true });
    renderShell();
    const bell = screen.getByTestId("event-feed-bell");
    expect(bell.className).toContain("text-danger");
    expect(screen.getByTestId("event-feed-resync-flag")).toBeInTheDocument();
    expect(bell.title).toMatch(/continuity was lost/);
    // The warning is audible, not just visual: the aria-label carries it too.
    expect(bell.getAttribute("aria-label")).toBe("Events (continuity lost)");
  });

  it("opens the Events panel; opening resets the unread count (visible = read)", async () => {
    getEventFeed(SAME_ORIGIN_INSTANCE.id).setState({ unread: 2 });
    renderShell();

    fireEvent.click(screen.getByTestId("event-feed-bell"));

    expect(await screen.findByTestId("event-feed-panel")).toBeInTheDocument();
    expect(getEventFeed(SAME_ORIGIN_INSTANCE.id).getState().unread).toBe(0);
    expect(screen.queryByTestId("event-feed-badge")).not.toBeInTheDocument();
  });

  it("is absent without an active instance", () => {
    useRegistry.setState({ instances: [], activeId: null });
    renderShell();
    expect(screen.queryByTestId("event-feed-bell")).not.toBeInTheDocument();
  });
});
