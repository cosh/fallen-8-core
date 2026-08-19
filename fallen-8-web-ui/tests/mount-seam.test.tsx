// MIT License
//
// mount-seam.test.tsx
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

import { afterEach, describe, expect, it, vi } from "vitest";
import { act, waitFor } from "@testing-library/react";

// The route tree statically imports CanvasScreen -> Canvas2D -> sigma, and sigma needs
// WebGL at import time; the same fakes the canvas tests use keep jsdom collection alive.
vi.mock("sigma", () => ({ default: class {} }));
vi.mock("sigma/rendering", () => ({
  EdgeArrowProgram: class {},
  EdgeRectangleProgram: class {},
  NodeCircleProgram: class {},
}));
vi.mock("@sigma/node-image", () => ({ createNodeImageProgram: () => class {} }));
vi.mock("@sigma/edge-curve", () => ({
  default: class {},
  EdgeCurvedArrowProgram: class {},
  DEFAULT_EDGE_CURVATURE: 0.25,
  indexParallelEdgesIndex: () => {},
}));
vi.mock("graphology-layout-forceatlas2/worker", () => ({
  default: class {
    start() {}
    stop() {}
    kill() {}
  },
}));
vi.mock("graphology-layout-forceatlas2", () => ({
  default: { inferSettings: () => ({}) },
}));
// monacoSetup imports monaco-editor with ?worker specifiers vite-in-vitest cannot resolve.
vi.mock("../src/delegate/monacoSetup", () => ({
  setupMonaco: () => {},
  monaco: {},
}));

import { mountStudio } from "../src/app/mount";
import { registerStudioMount, setStudioConfig, type StudioConfig } from "../src/app/studioConfig";
import { applyStudioConfigToRegistry, SAME_ORIGIN_INSTANCE, useRegistry } from "../src/instances/registry";
import { useFirstRun } from "../src/firstrun/firstRunStore";
import { useNlAssist } from "../src/delegate/nl/config";
import { resetInstanceStoresForTests } from "../src/state/instanceStore";
import type { InstanceConfig } from "../src/instances/types";

/** A host-supplied instance with the bearer credential shape (never persisted). */
const HOST_INSTANCE: InstanceConfig = {
  id: "host",
  name: "host",
  baseUrl: "https://api.example.com/i/1",
  auth: { kind: "bearer", getToken: async () => "tok" },
};

/**
 * The mount seam (feature studio-embeddable): mountStudio(el) with NO config must be the
 * standalone bootstrap - same shell, same seeded registry - and the host knobs (theme,
 * locks, memory history) are strictly opt-in.
 */

// Every health/inventory probe fails fast: the shell renders its disconnected states,
// which is all these assertions need.
vi.stubGlobal(
  "fetch",
  vi.fn(async () => {
    throw new Error("offline");
  }),
);

const mounted: { el: HTMLElement; handle: { unmount(): void } }[] = [];

function mount(config?: StudioConfig) {
  const el = document.createElement("div");
  document.body.appendChild(el);
  let handle!: { unmount(): void };
  act(() => {
    handle = mountStudio(el, config);
  });
  mounted.push({ el, handle });
  return el;
}

afterEach(async () => {
  for (const { el, handle } of mounted.splice(0)) {
    act(() => handle.unmount());
    el.remove();
  }
  resetInstanceStoresForTests();
  setStudioConfig({});
  await applyStudioConfigToRegistry();
  useFirstRun.setState({ dismissed: {}, replayOpen: false });
});

describe("mountStudio with no config (standalone contract)", () => {
  it("renders the shell inside the .f8-studio scope root and seeds the managed default", async () => {
    const el = mount();

    const root = el.querySelector('[data-testid="f8-studio-root"]');
    expect(root).not.toBeNull();
    await waitFor(() => {
      expect(el.querySelector('[data-testid="instance-switcher"]')).not.toBeNull();
    });

    const s = useRegistry.getState();
    expect(s.instances.map((i) => i.id)).toContain(SAME_ORIGIN_INSTANCE.id);
    expect(s.activeId).toBe(SAME_ORIGIN_INSTANCE.id);
    // No theme override: no inline token vars on the root.
    expect((root as HTMLElement).style.getPropertyValue("--color-accent")).toBe("");
  });

  it("unmounts cleanly", async () => {
    const el = mount();
    await waitFor(() => {
      expect(el.querySelector('[data-testid="f8-studio-root"]')).not.toBeNull();
    });

    const { handle } = mounted.pop()!;
    act(() => handle.unmount());
    expect(el.innerHTML).toBe("");
    el.remove();
  });
});

describe("host knobs", () => {
  it("lands theme overrides as CSS variables on the scope root", async () => {
    const el = mount({ theme: { accent: "#e2001a", fontMono: "monospace" } });

    const root = el.querySelector('[data-testid="f8-studio-root"]') as HTMLElement;
    expect(root.style.getPropertyValue("--color-accent")).toBe("#e2001a");
    expect(root.style.getPropertyValue("--font-mono")).toBe("monospace");
  });

  it("replaces the switchers with static labels under lockInstances + lockNamespace", async () => {
    const el = mount({ lockInstances: true, lockNamespace: true });

    await waitFor(() => {
      expect(el.querySelector('[data-testid="instance-locked"]')).not.toBeNull();
    });
    expect(el.querySelector('[data-testid="instance-switcher"]')).toBeNull();
    expect(el.querySelector('[data-testid="instance-add"]')).toBeNull();
    // The Connect screen (route "/") is always reachable, so its own affordances have to be
    // locked too: no row actions, no namespace management, and activation disabled - the
    // switcher's absence would otherwise be bypassable right here.
    expect(el.querySelector('[data-testid="namespaces-panel"]')).toBeNull();
    // The startup-load policy control lives in that panel, and it is the one affordance that
    // re-plans the HOST's next boot rather than this session (feature namespace-startup-load),
    // so it is asserted by name: a future panel-less home for it must not resurface it here.
    expect(el.querySelector('[data-testid^="namespace-startup-"]')).toBeNull();
    // The Configuration panel's setting editor is deliberately NOT asserted here, and the reason is
    // worth stating: this suite stubs fetch to throw, so GET /config always fails and the panel
    // renders config-unavailable with zero setting rows whether or not its gate exists. An assertion
    // would pass for the wrong reason and read as coverage. The gates that matter (the editable region
    // on lockInstances, and the two namespace-policy keys additionally on lockNamespace) are asserted
    // against a panel with real data in tests/connect-config-editor.test.tsx.
    expect([...el.querySelectorAll("button")].map((b) => b.textContent)).not.toContain("Edit");
    const radio = el.querySelector<HTMLInputElement>('input[type="radio"][name="active-instance"]');
    expect(radio).not.toBeNull();
    expect(radio!.disabled).toBe(true);
  });

  it("lists only the host's instances under lockInstances, never a personal one from this origin", async () => {
    localStorage.setItem(
      "f8.instances",
      JSON.stringify({
        state: {
          instances: [
            { id: "i-x", name: "someone-elses", baseUrl: "https://p.example.com", auth: { kind: "none" } },
          ],
          activeId: "i-x",
          activeNamespaces: {},
        },
        version: 0,
      }),
    );
    const el = mount({ lockInstances: true, instances: [HOST_INSTANCE] });

    await waitFor(() => {
      expect(el.querySelector('[data-testid="instance-row-host"]')).not.toBeNull();
    });
    expect(el.querySelector('[data-testid="instance-row-someone-elses"]')).toBeNull();
  });

  it("disables Edit for a host bearer instance so the form cannot convert the credential", async () => {
    const el = mount({ instances: [HOST_INSTANCE] });

    await waitFor(() => {
      expect(el.querySelector('[data-testid="instance-row-host"]')).not.toBeNull();
    });
    const buttons = [
      ...el.querySelectorAll<HTMLButtonElement>('[data-testid="instance-row-host"] button'),
    ];
    const edit = buttons.find((b) => b.textContent === "Edit");
    const remove = buttons.find((b) => b.textContent === "Remove");
    expect(edit?.disabled).toBe(true);
    expect(remove?.disabled).toBe(true);
  });

  it("keeps portalled overlays inside the .f8-studio root", async () => {
    const el = mount();
    await waitFor(() => {
      expect(el.querySelector('[data-testid="f8-studio-root"]')).not.toBeNull();
    });

    act(() => useFirstRun.getState().openReplay());

    // Inside the scope root, not document.body: the modal primitives are scoped to it now,
    // so a portal escaping the root loses all its styling (invisible to jsdom assertions).
    await waitFor(() => {
      expect(
        el.querySelector('[data-testid="f8-studio-root"] [data-testid="first-run-overlay"]'),
      ).not.toBeNull();
    });
  });

  it("routes nl-assist and first-run persistence through storageNamespace", async () => {
    mount({ storageNamespace: "acme." });

    act(() => useFirstRun.getState().dismiss("local/default"));
    act(() => useNlAssist.getState().setConfig({ model: "phi4-f8-mini" }));

    expect(localStorage.getItem("acme.f8.first-run")).not.toBeNull();
    expect(localStorage.getItem("f8.first-run")).toBeNull();
    expect(localStorage.getItem("acme.f8.nl-assist")).not.toBeNull();
    expect(localStorage.getItem("f8.nl-assist")).toBeNull();
  });
});

describe("two simultaneous mounts", () => {
  it("fail loudly instead of silently rebinding the first mount to the second config", () => {
    mount({ instances: [HOST_INSTANCE] });

    expect(() => mount({ instances: [{ ...HOST_INSTANCE, id: "other" }] })).toThrow(
      /already mounted/,
    );
  });

  it("registerStudioMount itself refuses a second live registration (the same-config-object shape)", () => {
    // The render-phase guard is identity-based (it must tolerate StrictMode re-rendering
    // the same config), so a host reusing one module-level config object would slip past
    // it; this count-based guard is what catches that shape.
    const release = registerStudioMount();
    try {
      expect(() => registerStudioMount()).toThrow(/already mounted/);
    } finally {
      release();
    }
  });

  it("a second mount sharing ONE config object crashes at mount instead of silently rebinding the first", async () => {
    const shared: StudioConfig = { instances: [HOST_INSTANCE] };
    const first = mount(shared);
    await waitFor(() => {
      expect(first.querySelector('[data-testid="f8-studio-root"]')).not.toBeNull();
    });

    // The guard fires in the second tree's mount effect; React 19's act rethrows effect
    // errors wrapped in an AggregateError, whose own String() hides the message.
    let messages: string[] = [];
    try {
      mount(shared);
    } catch (thrown) {
      const errors = thrown instanceof AggregateError ? thrown.errors : [thrown];
      messages = errors.map((e) => (e instanceof Error ? e.message : String(e)));
    }

    expect(messages.some((message) => /already mounted/.test(message))).toBe(true);
    // The crashed tree was unmounted by React; the FIRST mount keeps running untouched.
    expect(first.querySelector('[data-testid="f8-studio-root"]')).not.toBeNull();
  });

  it("allow a sequential remount once the first is unmounted", async () => {
    const first = mount({ instances: [HOST_INSTANCE] });
    await waitFor(() => {
      expect(first.querySelector('[data-testid="f8-studio-root"]')).not.toBeNull();
    });
    const { handle } = mounted.pop()!;
    act(() => handle.unmount());
    first.remove();

    expect(() => mount({ instances: [{ ...HOST_INSTANCE, id: "other", name: "other" }] })).not.toThrow();
  });
});
