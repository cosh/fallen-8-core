import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { InstanceConfig } from "../src/instances/types";
import type { NamespacesResponse } from "../src/api/types";

/**
 * Feature graph-namespaces, Studio side: the always-explicit /ns/{ns} prefix seam, the
 * per-namespace workspace-store keys (with legacy adoption as "default"), the registry's
 * per-instance active namespace with the compound bound-view id, and the Connect screen's
 * NAMESPACES panel (create gating, reserved default, typed drop confirmation).
 */

const listMock = vi.fn<(i: InstanceConfig, s?: AbortSignal) => Promise<NamespacesResponse>>();
const createMock = vi.fn();
const dropMock = vi.fn();
const renameMock = vi.fn();

vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    listNamespaces: (i: InstanceConfig, s?: AbortSignal) => listMock(i, s),
    createNamespace: (i: InstanceConfig, name: string) => createMock(i, name),
    dropNamespace: (i: InstanceConfig, name: string) => dropMock(i, name),
    renameNamespace: (i: InstanceConfig, name: string, to: string) => renameMock(i, name, to),
  };
});

const navigateMock = vi.fn();
vi.mock("@tanstack/react-router", () => ({
  useNavigate: () => navigateMock,
}));

import { scopedPath } from "../src/api/client";
import { isValidNamespaceName } from "../src/lib/namespaceName";
import {
  getInstanceStore,
  migrateInstanceStore,
  purgeInstanceStore,
  resetInstanceStoresForTests,
} from "../src/state/instanceStore";
import {
  DEFAULT_NAMESPACE,
  SAME_ORIGIN_INSTANCE,
  useInstanceStore,
  useRegistry,
} from "../src/instances/registry";
import { NamespacesPanel } from "../src/components/NamespacesPanel";
import { NamespaceSwitcher } from "../src/components/NamespaceSwitcher";
import { ApiError } from "../src/api/client";
import { renderHook } from "@testing-library/react";

const INVENTORY: NamespacesResponse = {
  namespaces: [
    { name: "default", state: "ready", vertexCount: 3, edgeCount: 1, createdAt: "2026-07-23T10:00:00.000Z" },
    { name: "flights", state: "ready", vertexCount: 191, edgeCount: 1697, createdAt: "2026-07-23T11:00:00.000Z" },
  ],
  maxNamespaces: 10000,
};

beforeEach(() => {
  resetInstanceStoresForTests();
  localStorage.clear();
  navigateMock.mockReset();
  listMock.mockReset().mockResolvedValue(INVENTORY);
  createMock.mockReset().mockResolvedValue(INVENTORY.namespaces[1]);
  dropMock.mockReset().mockResolvedValue(undefined);
  renameMock.mockReset().mockResolvedValue(INVENTORY.namespaces[1]);
  useRegistry.setState({
    instances: [SAME_ORIGIN_INSTANCE],
    activeId: SAME_ORIGIN_INSTANCE.id,
    activeNamespaces: {},
    namespaceSupport: {},
  });
});

describe("isValidNamespaceName (client mirror of the server rule)", () => {
  it("accepts any case, spaces, punctuation, unicode up to 63 chars", () => {
    for (const ok of ["a", "fraud-q3", "Flights", "code repo test", "under_score", "dot.name", "fraud!(q3)#2", "ümlaut-Ω", "a".repeat(63)]) {
      expect(isValidNamespaceName(ok)).toBe(true);
    }
  });

  it("rejects only the URL hazards (slash/backslash/control), the ends-dots, padding, empty, and over-length", () => {
    for (const bad of ["", "   ", "a".repeat(64), " leading", "trailing ", ".", "..", "slash/name", "back\\slash", "tab\tname"]) {
      expect(isValidNamespaceName(bad)).toBe(false);
    }
  });
});

describe("the /ns prefix seam", () => {
  const bound: InstanceConfig = { ...SAME_ORIGIN_INSTANCE, namespace: "flights" };

  it("prefixes namespace-scoped paths for a bound instance — explicitly, default included", () => {
    expect(scopedPath(bound, "/vertex")).toBe("/ns/flights/vertex");
    expect(scopedPath({ ...bound, namespace: "default" }, "/status")).toBe("/ns/default/status");
  });

  it("leaves an unbound instance's paths bare (pre-namespace servers keep working)", () => {
    expect(scopedPath(SAME_ORIGIN_INSTANCE, "/vertex")).toBe("/vertex");
  });
});

describe("per-namespace workspace stores", () => {
  it("adopts the legacy store key as the default namespace's (no migration)", () => {
    const legacy = getInstanceStore("inst-a");
    expect(getInstanceStore("inst-a", "default")).toBe(legacy);
    expect(getInstanceStore("inst-a/default")).toBe(legacy);
  });

  it("keys other namespaces separately, matching the bound view's compound id", () => {
    const flights = getInstanceStore("inst-a", "flights");
    expect(flights).not.toBe(getInstanceStore("inst-a"));
    expect(getInstanceStore("inst-a/flights")).toBe(flights);

    flights.getState().setBrowserDraft({ idInput: "42" });
    expect(getInstanceStore("inst-a").getState().browserDraft.idInput).not.toBe("42");
    expect(localStorage.getItem("f8.workspace.inst-a/flights")).toContain("42");
  });
});

describe("registry active namespace + bound view", () => {
  it("defaults to 'default' and persists per instance", () => {
    const registry = useRegistry.getState();
    expect(registry.activeNamespaces[SAME_ORIGIN_INSTANCE.id]).toBeUndefined();

    registry.setActiveNamespace(SAME_ORIGIN_INSTANCE.id, "flights");
    registry.setActiveNamespace("other", "scratch");
    expect(useRegistry.getState().activeNamespaces).toEqual({
      [SAME_ORIGIN_INSTANCE.id]: "flights",
      other: "scratch",
    });
  });

  it("binds useInstanceStore to the active namespace with the compound id", () => {
    useRegistry.getState().setActiveNamespace(SAME_ORIGIN_INSTANCE.id, "flights");
    const { result } = renderHook(() => useInstanceStore());

    expect(result.current.instance.namespace).toBe("flights");
    // The compound id makes every derived react-query key per-namespace.
    expect(result.current.instance.id).toBe("local/flights");
    expect(result.current.store).toBe(getInstanceStore("local", "flights"));
  });

  it("stays on 'default' until a namespace is chosen", () => {
    const { result } = renderHook(() => useInstanceStore());
    expect(result.current.instance.namespace).toBe(DEFAULT_NAMESPACE);
    expect(result.current.instance.id).toBe("local/default");
    expect(result.current.store).toBe(getInstanceStore("local"));
  });

  it("degrades to the UNBOUND view on a server known to predate namespaces", () => {
    // The /ns capability probe 404ed: bare paths and the legacy store, so the previous
    // release keeps working instead of 404ing on /ns/default/… .
    useRegistry.getState().setNamespaceSupport(SAME_ORIGIN_INSTANCE.id, false);
    const { result } = renderHook(() => useInstanceStore());

    expect(result.current.instance.namespace).toBeUndefined();
    expect(result.current.instance.id).toBe("local");
    expect(scopedPath(result.current.instance, "/vertex")).toBe("/vertex");
    expect(result.current.store).toBe(getInstanceStore("local"));
  });
});

describe("workspace store lifecycle on rename / drop", () => {
  it("migrates the persisted workspace to the renamed namespace", () => {
    getInstanceStore("inst-a", "flights").getState().setBrowserDraft({ idInput: "42" });

    migrateInstanceStore("inst-a", "flights", "fl-eu");

    expect(localStorage.getItem("f8.workspace.inst-a/flights")).toBeNull();
    expect(getInstanceStore("inst-a", "fl-eu").getState().browserDraft.idInput).toBe("42");
  });

  it("purges the workspace on drop so a namesake starts fresh", () => {
    getInstanceStore("inst-a", "flights").getState().setBrowserDraft({ idInput: "42" });

    purgeInstanceStore("inst-a", "flights");

    expect(localStorage.getItem("f8.workspace.inst-a/flights")).toBeNull();
    expect(getInstanceStore("inst-a", "flights").getState().browserDraft.idInput).not.toBe("42");
  });
});

describe("namespace switcher dropdown", () => {
  const onSwitch = vi.fn();

  function renderSwitcher(entries = INVENTORY.namespaces, activeNamespace = "flights") {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return render(
      <QueryClientProvider client={client}>
        <NamespaceSwitcher
          instance={SAME_ORIGIN_INSTANCE}
          entries={entries}
          maxNamespaces={INVENTORY.maxNamespaces}
          activeNamespace={activeNamespace}
          onSwitch={onSwitch}
        />
      </QueryClientProvider>,
    );
  }

  beforeEach(() => onSwitch.mockReset());

  it("shows the active namespace with counts; the dropdown lists rows, tags, and the quota", async () => {
    const user = userEvent.setup();
    renderSwitcher();

    expect(screen.getByTestId("namespace-switcher")).toHaveTextContent("flights");
    await user.click(screen.getByTestId("namespace-switcher"));

    const flights = screen.getByTestId("namespace-option-flights");
    expect(flights).toHaveTextContent("active");
    expect(flights).toHaveTextContent(/191/);
    expect(screen.getByTestId("namespace-option-default")).toHaveTextContent("bare-URL alias");
    expect(screen.getByTestId("namespace-dropdown-footer")).toHaveTextContent(/2 \/ 10[., ]000/);
  });

  it("filters rows and switches on click", async () => {
    const user = userEvent.setup();
    renderSwitcher();
    await user.click(screen.getByTestId("namespace-switcher"));

    await user.type(screen.getByTestId("namespace-filter"), "def");
    expect(screen.queryByTestId("namespace-option-flights")).not.toBeInTheDocument();

    await user.click(screen.getByTestId("namespace-option-default"));
    expect(onSwitch).toHaveBeenCalledWith("default");
    expect(screen.queryByTestId("namespace-dropdown")).not.toBeInTheDocument();
  });

  it("marks a non-ready namespace as not ready", async () => {
    const user = userEvent.setup();
    renderSwitcher([
      ...INVENTORY.namespaces,
      { name: "importing", state: "creating", vertexCount: 0, edgeCount: 0, createdAt: "" },
    ]);
    await user.click(screen.getByTestId("namespace-switcher"));

    expect(screen.getByTestId("namespace-option-importing")).toHaveTextContent("not ready");
  });

  it("quick-creates a namespace inline (pattern-gated) and switches to the newborn", async () => {
    createMock.mockResolvedValue({
      name: "fraud-q3", state: "ready", vertexCount: 0, edgeCount: 0, createdAt: "",
    });
    const user = userEvent.setup();
    renderSwitcher();
    await user.click(screen.getByTestId("namespace-switcher"));
    await user.click(screen.getByTestId("namespace-new"));

    expect(screen.getByTestId("namespace-quick-create")).toBeDisabled();
    await user.type(screen.getByTestId("namespace-quick-create-name"), "bad/name");
    expect(screen.getByTestId("namespace-quick-create")).toBeDisabled();

    await user.clear(screen.getByTestId("namespace-quick-create-name"));
    await user.type(screen.getByTestId("namespace-quick-create-name"), "fraud-q3");
    await user.click(screen.getByTestId("namespace-quick-create"));

    await waitFor(() => expect(createMock).toHaveBeenCalledTimes(1));
    expect(createMock.mock.calls[0][1]).toBe("fraud-q3");
    await waitFor(() => expect(onSwitch).toHaveBeenCalledWith("fraud-q3"));
  });

  it("surfaces a 409/422 on quick-create instead of closing", async () => {
    createMock.mockRejectedValue(new ApiError(422, "/ns/x", "{}"));
    const user = userEvent.setup();
    renderSwitcher();
    await user.click(screen.getByTestId("namespace-switcher"));
    await user.click(screen.getByTestId("namespace-new"));
    await user.type(screen.getByTestId("namespace-quick-create-name"), "x");
    await user.click(screen.getByTestId("namespace-quick-create"));

    await waitFor(() =>
      expect(screen.getByTestId("namespace-quick-create-error")).toHaveTextContent("quota exceeded (422)"),
    );
    expect(screen.getByTestId("namespace-dropdown")).toBeInTheDocument();
  });
});

describe("NAMESPACES panel", () => {
  function renderPanel() {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return render(
      <QueryClientProvider client={client}>
        <NamespacesPanel />
      </QueryClientProvider>,
    );
  }

  it("lists namespaces with counts, the quota, and the URL prefix; default is undeletable", async () => {
    renderPanel();
    await waitFor(() => expect(screen.getByTestId("namespace-row-flights")).toBeInTheDocument());

    // toLocaleString is locale-dependent (10,000 vs 10.000) - match either separator.
    expect(screen.getByTestId("namespaces-quota")).toHaveTextContent(/2 \/ 10[., ]000/);
    const flights = screen.getByTestId("namespace-row-flights");
    expect(within(flights).getByText("191")).toBeInTheDocument();
    expect(within(flights).getByText("/ns/flights/*")).toBeInTheDocument();

    const defaultRow = screen.getByTestId("namespace-row-default");
    expect(within(defaultRow).getByText("alias of bare URLs")).toBeInTheDocument();
    expect(screen.getByTestId("namespace-drop-default")).toBeDisabled();
    expect(screen.getByTestId("namespace-rename-default")).toBeDisabled();
  });

  it("gates Create on the URL-safety rule and shows the live URL preview", async () => {
    const user = userEvent.setup();
    renderPanel();
    await waitFor(() => expect(screen.getByTestId("namespace-create")).toBeInTheDocument());

    expect(screen.getByTestId("namespace-create")).toBeDisabled();
    // A slash can't be a single path segment — still rejected.
    await user.type(screen.getByTestId("namespace-create-name"), "bad/name");
    expect(screen.getByTestId("namespace-create")).toBeDisabled();

    // A spaced, mixed-case name is now allowed (the permissive rule).
    await user.clear(screen.getByTestId("namespace-create-name"));
    await user.type(screen.getByTestId("namespace-create-name"), "Fraud Q3");
    expect(screen.getByTestId("namespace-url-preview")).toHaveTextContent("PUT /ns/Fraud Q3");
    expect(screen.getByTestId("namespace-create")).toBeEnabled();

    await user.click(screen.getByTestId("namespace-create"));
    await waitFor(() => expect(createMock).toHaveBeenCalledTimes(1));
    expect(createMock.mock.calls[0][1]).toBe("Fraud Q3");
  });

  it("drops only after the namespace name is typed", async () => {
    const user = userEvent.setup();
    renderPanel();
    await waitFor(() => expect(screen.getByTestId("namespace-drop-flights")).toBeInTheDocument());

    await user.click(screen.getByTestId("namespace-drop-flights"));
    expect(screen.getByTestId("confirm-action")).toBeDisabled();
    expect(dropMock).not.toHaveBeenCalled();

    await user.type(screen.getByTestId("confirm-typed"), "flights");
    expect(screen.getByTestId("confirm-action")).toBeEnabled();
    await user.click(screen.getByTestId("confirm-action"));

    await waitFor(() => expect(dropMock).toHaveBeenCalledTimes(1));
    expect(dropMock.mock.calls[0][1]).toBe("flights");
  });

  it("switches namespace: registry updated and navigation to the namespaced dashboard", async () => {
    const user = userEvent.setup();
    renderPanel();
    await waitFor(() => expect(screen.getByTestId("namespace-switch-flights")).toBeInTheDocument());

    await user.click(screen.getByTestId("namespace-switch-flights"));
    expect(useRegistry.getState().activeNamespaces[SAME_ORIGIN_INSTANCE.id]).toBe("flights");
    expect(navigateMock).toHaveBeenCalledWith({ to: "/q/$ns/dashboard", params: { ns: "flights" } });
  });
});
