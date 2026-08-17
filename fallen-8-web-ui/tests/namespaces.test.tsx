// MIT License
//
// namespaces.test.tsx
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
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { InstanceConfig } from "../src/instances/types";
import type { NamespaceEntry, NamespacesResponse } from "../src/api/types";

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
const policyMock = vi.fn();
const activateMock = vi.fn();

vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    listNamespaces: (i: InstanceConfig, s?: AbortSignal) => listMock(i, s),
    createNamespace: (i: InstanceConfig, name: string) => createMock(i, name),
    dropNamespace: (i: InstanceConfig, name: string) => dropMock(i, name),
    renameNamespace: (i: InstanceConfig, name: string, to: string) => renameMock(i, name, to),
    setNamespaceLoadOnStartup: (i: InstanceConfig, name: string, value: string) =>
      policyMock(i, name, value),
    activateNamespace: (i: InstanceConfig, name: string) => activateMock(i, name),
  };
});

const navigateMock = vi.fn();
/** The URL segment NamespaceScope reads; a test sets it before rendering the layout. */
let scopeParams = { ns: DEFAULT_NAMESPACE };
vi.mock("@tanstack/react-router", () => ({
  useNavigate: () => navigateMock,
  useParams: () => scopeParams,
  Outlet: () => <div data-testid="scope-outlet">the namespaced screen</div>,
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
import { NamespaceScope } from "../src/app/NamespaceScope";
import { StudioConfigContext, type StudioConfig } from "../src/app/studioConfig";
import { ApiError } from "../src/api/client";
import { renderHook } from "@testing-library/react";

const INVENTORY: NamespacesResponse = {
  namespaces: [
    { name: "default", state: "ready", vertexCount: 3, edgeCount: 1, createdAt: "2026-07-23T10:00:00.000Z", loadOnStartupEnabled: true },
    { name: "flights", state: "ready", vertexCount: 191, edgeCount: 1697, createdAt: "2026-07-23T11:00:00.000Z", loadOnStartupEnabled: null },
  ],
  maxNamespaces: 10000,
};

/**
 * A namespace the server catalogs but did not load (feature namespace-startup-load): state
 * "notLoaded" and NULL counts. Every component that renders an inventory entry must survive it,
 * because the switcher lives in the app shell on every screen - one throw there replaces the
 * whole Studio with the error boundary, including the panel an operator would use to undo the
 * exclusion.
 */
const NOT_LOADED: NamespaceEntry = {
  name: "archived",
  state: "notLoaded",
  vertexCount: null,
  edgeCount: null,
  createdAt: "2026-07-23T12:00:00.000Z",
  loadOnStartupEnabled: false,
};

beforeEach(() => {
  resetInstanceStoresForTests();
  localStorage.clear();
  navigateMock.mockReset();
  scopeParams = { ns: DEFAULT_NAMESPACE };
  listMock.mockReset().mockResolvedValue(INVENTORY);
  createMock.mockReset().mockResolvedValue(INVENTORY.namespaces[1]);
  dropMock.mockReset().mockResolvedValue(undefined);
  renameMock.mockReset().mockResolvedValue(INVENTORY.namespaces[1]);
  policyMock.mockReset().mockResolvedValue(INVENTORY.namespaces[1]);
  activateMock.mockReset();
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
    // A loaded active namespace keeps the accent dot: the residency signal must stay OFF here,
    // or the shell would look degraded on every screen of a perfectly healthy Fallen-8.
    expect(screen.getByTestId("namespace-switcher")).toHaveTextContent("●");
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
      { name: "importing", state: "creating", vertexCount: 0, edgeCount: 0, createdAt: "", loadOnStartupEnabled: null },
    ]);
    await user.click(screen.getByTestId("namespace-switcher"));

    expect(screen.getByTestId("namespace-option-importing")).toHaveTextContent("not ready");
  });

  it("renders a not-loaded entry with dashes instead of throwing on its absent counts", async () => {
    const user = userEvent.setup();
    // The switcher's own trigger reads the ACTIVE entry's counts, so make the not-loaded one
    // active: that is the path that took the whole app shell down.
    renderSwitcher([...INVENTORY.namespaces, NOT_LOADED], "archived");

    expect(screen.getByTestId("namespace-switcher")).toHaveTextContent("- v · - e");
    await user.click(screen.getByTestId("namespace-switcher"));

    const archived = screen.getByTestId("namespace-option-archived");
    expect(archived).toHaveTextContent("- v · - e");
    expect(archived).not.toHaveTextContent("0 v");
    // The rest of the inventory still renders: one excluded namespace must not cost the list.
    expect(screen.getByTestId("namespace-option-flights")).toHaveTextContent("191");
  });

  it("tags not-loaded ahead of 'active' and 'bare-URL alias', and dims the trigger dot", async () => {
    const user = userEvent.setup();
    // "default" not loaded is the hostile combination: it is the bare-URL alias AND excluded,
    // and the alias tag would hide the residency the operator needs.
    const excludedDefault: NamespaceEntry = {
      ...INVENTORY.namespaces[0],
      state: "notLoaded",
      vertexCount: null,
      edgeCount: null,
      loadOnStartupEnabled: false,
    };
    renderSwitcher([excludedDefault, INVENTORY.namespaces[1], NOT_LOADED], "archived");

    // The trigger carries the faint dot the dropdown already uses for a non-ready namespace,
    // never a second colour, and never the accent dot that would read as "loaded".
    const trigger = screen.getByTestId("namespace-switcher");
    expect(trigger).toHaveTextContent("◐");
    expect(trigger).not.toHaveTextContent("●");

    await user.click(trigger);
    const archived = screen.getByTestId("namespace-option-archived");
    expect(archived).toHaveTextContent("not loaded");
    expect(archived).not.toHaveTextContent("active");
    const defaultRow = screen.getByTestId("namespace-option-default");
    expect(defaultRow).toHaveTextContent("not loaded");
    expect(defaultRow).not.toHaveTextContent("bare-URL alias");
    // A loaded namespace beside them is untagged: the word marks residency, not the list.
    expect(screen.getByTestId("namespace-option-flights")).not.toHaveTextContent("not loaded");
  });

  it("quick-creates a namespace inline (pattern-gated) and switches to the newborn", async () => {
    createMock.mockResolvedValue({
      name: "fraud-q3", state: "ready", vertexCount: 0, edgeCount: 0, createdAt: "",
      loadOnStartupEnabled: null,
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

  it("cannot exclude the reserved default namespace, and the reason is on the row, not a tooltip", async () => {
    // Its stored override is the INHERITING one, which is what a server that was never asked
    // about it reports: the control must still say "load", because default is loaded whatever
    // the catalog holds - "inherit" would read as "it depends on the server default".
    listMock.mockResolvedValue({
      namespaces: [
        { ...INVENTORY.namespaces[0], loadOnStartupEnabled: null },
        INVENTORY.namespaces[1],
      ],
      maxNamespaces: INVENTORY.maxNamespaces,
    });
    renderPanel();
    await waitFor(() => expect(screen.getByTestId("namespace-startup-default")).toBeInTheDocument());

    const control = screen.getByTestId("namespace-startup-default");
    expect(control).toBeDisabled();
    expect(control).toHaveValue("enabled");
    // Visible text, because a disabled control whose reason is only a hover is a dead end.
    expect(
      within(screen.getByTestId("namespace-row-default")).getByText(/always loaded: bare URLs alias it/),
    ).toBeInTheDocument();
  });

  it("sets the at-startup policy through PATCH and states that it takes effect on restart", async () => {
    const user = userEvent.setup();
    renderPanel();
    await waitFor(() => expect(screen.getByTestId("namespace-startup-flights")).toBeInTheDocument());

    // A null override renders as "inherit" rather than guessing which side the server defaults to.
    expect(screen.getByTestId("namespace-startup-flights")).toHaveValue("inherit");

    await user.selectOptions(screen.getByTestId("namespace-startup-flights"), "disabled");
    await waitFor(() => expect(policyMock).toHaveBeenCalledTimes(1));
    expect(policyMock.mock.calls[0].slice(1)).toEqual(["flights", "disabled"]);

    // The whole point of the guidance register: nothing changed in THIS process.
    await waitFor(() =>
      expect(screen.getByTestId("namespace-message")).toHaveTextContent(
        /skipped at the next start - takes effect on restart/,
      ),
    );
    expect(screen.getByTestId("namespace-startup-hint")).toHaveTextContent(
      /Changes take effect on restart/,
    );
  });

  it("surfaces a refused policy change instead of leaving the control looking applied", async () => {
    const user = userEvent.setup();
    policyMock.mockRejectedValue(new ApiError(409, "/ns/flights", "{}"));
    renderPanel();
    await waitFor(() => expect(screen.getByTestId("namespace-startup-flights")).toBeInTheDocument());

    await user.selectOptions(screen.getByTestId("namespace-startup-flights"), "enabled");

    await waitFor(() => expect(screen.getByRole("alert")).toHaveTextContent("HTTP 409"));
    expect(screen.queryByTestId("namespace-message")).not.toBeInTheDocument();
  });

  it("lists a not-loaded namespace with dashed counts, and its drop dialog claims no zeros", async () => {
    const user = userEvent.setup();
    listMock.mockResolvedValue({
      namespaces: [...INVENTORY.namespaces, NOT_LOADED],
      maxNamespaces: INVENTORY.maxNamespaces,
    });
    renderPanel();
    await waitFor(() => expect(screen.getByTestId("namespace-row-archived")).toBeInTheDocument());

    const archived = screen.getByTestId("namespace-row-archived");
    // Both count cells are a dash. "0" would report a graph that still holds data as empty.
    expect(within(archived).getAllByText("-")).toHaveLength(2);
    expect(within(archived).queryByText("0")).not.toBeInTheDocument();
    // The loaded rows are untouched by the absent counts beside them.
    expect(within(screen.getByTestId("namespace-row-flights")).getByText("191")).toBeInTheDocument();
    // Residency and policy are independent: this one is excluded, so its control reads "skip"
    // while the row itself reports it is not loaded RIGHT NOW.
    expect(screen.getByTestId("namespace-startup-archived")).toHaveValue("disabled");

    // An irreversible drop must not look free just because no count could be read.
    await user.click(screen.getByTestId("namespace-drop-archived"));
    const dialog = screen.getByRole("dialog");
    expect(dialog).toHaveTextContent("its data on disk");
    expect(dialog).not.toHaveTextContent("0 vertices");
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

/**
 * The /q/$ns layout's three branches (feature namespace-startup-load, phase 4). The load-bearing
 * assertion is that a NOT-LOADED namespace never reaches the 404 recover state: that state's
 * primary action recreates the namespace empty, which over data still on disk is destructive.
 * Its way out is "Activate now" (POST /ns/{name}/activate), which loads the namespace into THIS
 * process and deliberately leaves the persisted startup-load policy alone.
 */
describe("NamespaceScope branches", () => {
  function renderScope(config: StudioConfig = {}) {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const view = render(
      <QueryClientProvider client={client}>
        <StudioConfigContext.Provider value={config}>
          <NamespaceScope />
        </StudioConfigContext.Provider>
      </QueryClientProvider>,
    );
    return { ...view, client };
  }

  /**
   * Waits until the inventory has actually landed. Every branch renders the plain Outlet while
   * the query is pending, so an assertion that the Outlet is showing is trivially true on the
   * FIRST poll and proves nothing about the branch decision - which a mutation probe caught.
   */
  async function inventoryLoaded(client: QueryClient) {
    await waitFor(() => {
      const [query] = client.getQueryCache().getAll();
      expect(query?.state.data).toBeDefined();
    });
  }

  beforeEach(() => {
    listMock.mockResolvedValue({
      namespaces: [...INVENTORY.namespaces, NOT_LOADED],
      maxNamespaces: INVENTORY.maxNamespaces,
    });
  });

  it("renders the namespaced screen for a loaded namespace", async () => {
    scopeParams = { ns: "flights" };
    const { client } = renderScope();
    await inventoryLoaded(client);

    expect(screen.getByTestId("scope-outlet")).toBeInTheDocument();
    expect(screen.queryByTestId("namespace-not-loaded")).not.toBeInTheDocument();
    expect(screen.queryByTestId("namespace-recover")).not.toBeInTheDocument();
  });

  it("answers a not-loaded namespace in prose, and NEVER offers to recreate it", async () => {
    scopeParams = { ns: "archived" };
    renderScope();

    const branch = await waitFor(() => screen.getByTestId("namespace-not-loaded"));
    // The recover state and its destructive button are the whole point of a separate branch.
    expect(screen.queryByTestId("namespace-recover")).not.toBeInTheDocument();
    expect(screen.queryByTestId("namespace-recover-recreate")).not.toBeInTheDocument();
    expect(branch).not.toHaveTextContent(/recreate/i);
    // It says the data survived, and how to get it back - in the same register as the
    // read-only configuration view ("takes effect on restart").
    expect(branch).toHaveTextContent("was not loaded into the running process");
    expect(branch).toHaveTextContent("untouched on disk");
    expect(branch).toHaveTextContent("takes effect on restart");
    // The whole point of two separate ways back: activation is for THIS process, the policy is
    // for the next boot. Prose that implied activation is permanent would cost the operator the
    // namespace again at the next restart.
    expect(branch).toHaveTextContent("activating does not change that policy");
    // The screen itself must not render underneath: every route it would call answers 503.
    expect(screen.queryByTestId("scope-outlet")).not.toBeInTheDocument();
  });

  it("still shows the recover state for a namespace that really is gone", async () => {
    // The third branch must not swallow the 404 case: a namespace absent from the inventory has
    // no data to protect, and recreating it empty is the right offer.
    scopeParams = { ns: "ghost" };
    renderScope();

    await waitFor(() => expect(screen.getByTestId("namespace-recover")).toBeInTheDocument());
    expect(screen.getByTestId("namespace-recover-recreate")).toBeInTheDocument();
    expect(screen.queryByTestId("namespace-not-loaded")).not.toBeInTheDocument();
  });

  it("offers a way out when unlocked", async () => {
    const user = userEvent.setup();
    scopeParams = { ns: "archived" };
    renderScope();
    await waitFor(() => expect(screen.getByTestId("namespace-not-loaded")).toBeInTheDocument());

    await user.click(screen.getByTestId("namespace-not-loaded-manage"));
    expect(navigateMock).toHaveBeenCalledWith({ to: "/" });
    await user.click(screen.getByTestId("namespace-not-loaded-switch"));
    expect(navigateMock).toHaveBeenCalledWith({
      to: "/q/$ns/dashboard",
      params: { ns: DEFAULT_NAMESPACE },
    });
  });

  it("activates the namespace and re-renders it loaded, without asking for a reload", async () => {
    const user = userEvent.setup();
    scopeParams = { ns: "archived" };
    // Activation is only observable to this branch through the INVENTORY it decides on, so the
    // fake server changes both: it answers the POST and starts reporting the namespace as ready.
    activateMock.mockImplementation(async () => {
      listMock.mockResolvedValue({
        namespaces: [
          ...INVENTORY.namespaces,
          { ...NOT_LOADED, state: "ready", vertexCount: 12, edgeCount: 4 },
        ],
        maxNamespaces: INVENTORY.maxNamespaces,
      });
      return {
        namespace: { ...NOT_LOADED, state: "ready", vertexCount: 12, edgeCount: 4 },
        activated: true,
        detail: "Restored save game sg-1 and replayed its write-ahead-log tail.",
      };
    });
    renderScope();
    await waitFor(() => expect(screen.getByTestId("namespace-not-loaded")).toBeInTheDocument());

    await user.click(screen.getByTestId("namespace-not-loaded-activate"));
    expect(activateMock.mock.calls[0][1]).toBe("archived");

    // The screen must come back by itself: an operator who just fixed it is not told to reload.
    await waitFor(() => expect(screen.getByTestId("scope-outlet")).toBeInTheDocument());
    expect(screen.queryByTestId("namespace-not-loaded")).not.toBeInTheDocument();
    // Activation answers for THIS process only. Editing the startup-load policy behind the
    // operator's back would make every "load it now" silently change the next boot's selection,
    // which is the distinction the branch's prose promises.
    expect(policyMock).not.toHaveBeenCalled();
  });

  it("shows a refused activation inline, and stays not loaded", async () => {
    const user = userEvent.setup();
    scopeParams = { ns: "archived" };
    activateMock.mockRejectedValue(
      new ApiError(
        500,
        "http://f8.test/ns/archived/activate",
        '{"title":"Namespace activation failed","detail":"Its newest save game could not be restored."}',
      ),
    );
    renderScope();
    await waitFor(() => expect(screen.getByTestId("namespace-not-loaded")).toBeInTheDocument());

    await user.click(screen.getByTestId("namespace-not-loaded-activate"));

    // The loader's own detail is the only thing that tells an operator whether a retry is
    // pointless, so a swallowed failure would leave them clicking a button that does nothing.
    const alert = await waitFor(() => screen.getByRole("alert"));
    expect(alert).toHaveTextContent("HTTP 500");
    expect(alert).toHaveTextContent("could not be restored");
    // A failed load leaves the namespace exactly as not-loaded as it was, and the branch with it.
    expect(screen.getByTestId("namespace-not-loaded")).toBeInTheDocument();
    expect(screen.queryByTestId("scope-outlet")).not.toBeInTheDocument();
  });

  it("renders NO buttons under lockNamespace (an embed must not re-plan the host's boot)", async () => {
    scopeParams = { ns: "archived" };
    renderScope({ lockNamespace: true });

    const branch = await waitFor(() => screen.getByTestId("namespace-not-loaded"));
    expect(within(branch).queryAllByRole("button")).toEqual([]);
    // Activation included: it decides what the HOST's process holds, which is exactly the
    // decision an embed scoped to one graph must not take.
    expect(screen.queryByTestId("namespace-not-loaded-activate")).not.toBeInTheDocument();
    // The explanation is NOT what lockNamespace removes: the embed's user still learns why the
    // screen is empty, they just cannot act on the host's configuration from here.
    expect(branch).toHaveTextContent("was not loaded into the running process");
  });
});
