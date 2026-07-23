import { beforeEach, describe, expect, it, vi } from "vitest";
import { act, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";
import type { InstanceConfig } from "../src/instances/types";
import type { StatusREST } from "../src/api/types";

/**
 * Nav gating in the app shell: every entry but Connect stays locked until the ACTIVE
 * instance's /status probe answers AND authorizes the credential (server contract on
 * StatusREST.ApiKeyRequired). Pins all four connection states, the deep-link guard,
 * instance switching, and back-compat with servers predating the auth fields.
 */

let currentPath = "/";
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

const statusMock =
  vi.fn<(instance: InstanceConfig, signal?: AbortSignal) => Promise<StatusREST>>();
vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    getStatus: (i: InstanceConfig, s?: AbortSignal) => statusMock(i, s),
  };
});

import { AppShell } from "../src/app/AppShell";
import { SAME_ORIGIN_INSTANCE, useRegistry } from "../src/instances/registry";

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
  "nav-dashboard",
  "nav-save-games",
  "nav-browser",
  "nav-query",
  "nav-path",
  "nav-subgraph",
  "nav-analytics",
  "nav-canvas",
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
  useRegistry.setState({
    instances: [SAME_ORIGIN_INSTANCE],
    activeId: SAME_ORIGIN_INSTANCE.id,
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

    await waitFor(() => expectUnlocked("nav-dashboard"));
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

    await waitFor(() => expectUnlocked("nav-dashboard"));
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
    await waitFor(() => expectUnlocked("nav-dashboard"));

    act(() => {
      const prod = useRegistry
        .getState()
        .addInstance({ name: "prod", baseUrl: "http://prod:17408", auth: { kind: "none" } });
      useRegistry.getState().setActive(prod.id);
    });

    await waitFor(() => expectLocked("nav-dashboard"));
    await waitFor(() =>
      expect(screen.getByTestId("health-chip")).toHaveTextContent("unauthorized"),
    );
  });
});

describe("deep-link guard", () => {
  it("replaces gated screens when the credential is rejected", async () => {
    statusMock.mockResolvedValue({ ...STATUS, apiKeyRequired: true, authenticated: false });
    renderShell(<div data-testid="screen" />, "/dashboard");

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
    renderShell(<div data-testid="screen" />, "/dashboard");

    await waitFor(() =>
      expect(screen.getByTestId("health-chip")).toHaveTextContent("unreachable"),
    );
    expect(screen.getByTestId("screen")).toBeInTheDocument();
    expectLocked("nav-dashboard");
  });
});
