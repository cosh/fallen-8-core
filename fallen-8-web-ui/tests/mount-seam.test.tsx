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
import { setStudioConfig, type StudioConfig } from "../src/app/studioConfig";
import { applyStudioConfigToRegistry, SAME_ORIGIN_INSTANCE, useRegistry } from "../src/instances/registry";

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
  setStudioConfig({});
  await applyStudioConfigToRegistry();
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
  });

  it("keeps the address bar untouched with memory history", async () => {
    const el = mount({ history: "memory" });

    await waitFor(() => {
      expect(el.querySelector('[data-testid="instance-switcher"]')).not.toBeNull();
    });
    expect(window.location.pathname).toBe("/");
  });
});
