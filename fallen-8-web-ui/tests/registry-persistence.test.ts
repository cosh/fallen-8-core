// MIT License
//
// registry-persistence.test.ts
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

import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { SAME_ORIGIN_INSTANCE, useRegistry } from "../src/instances/registry";

/**
 * The managed-default persistence contract (feature standalone-ui). The config.js-seeded managed
 * default is NEVER persisted; the custom persist `merge` re-injects a freshly synthesized copy
 * (baseUrl re-read from config.js) ahead of the persisted personal instances on every load, so it
 * is always present and re-synced, while personal instances, the active id, and the per-instance
 * active namespace survive. `partialize` keeps the managed default out of storage; a legacy blob
 * that persisted the whole state is upgraded transparently by the same merge.
 */
const KEY = "f8.instances";
const store = (state: unknown) => JSON.stringify({ state, version: 0 });

describe("registry persistence (standalone-ui)", () => {
  beforeEach(() => {
    localStorage.clear();
    delete window.__F8_CONFIG__;
    useRegistry.setState({
      instances: [SAME_ORIGIN_INSTANCE],
      activeId: SAME_ORIGIN_INSTANCE.id,
      activeNamespaces: {},
      namespaceSupport: {},
    });
  });

  afterEach(() => {
    localStorage.clear();
    delete window.__F8_CONFIG__;
  });

  it("re-injects the managed default ahead of persisted personal instances, preserving activeId", async () => {
    localStorage.setItem(
      KEY,
      store({
        instances: [{ id: "i-x", name: "prod", baseUrl: "https://p.example.com", auth: { kind: "none" } }],
        activeId: "i-x",
        activeNamespaces: {},
      }),
    );

    await useRegistry.persist.rehydrate();

    const s = useRegistry.getState();
    expect(s.instances[0].id).toBe(SAME_ORIGIN_INSTANCE.id);
    expect(s.instances.map((i) => i.id)).toContain("i-x");
    expect(s.activeId).toBe("i-x");
  });

  it("drops a legacy persisted managed record and re-syncs its baseUrl from config.js", async () => {
    window.__F8_CONFIG__ = { apiUrl: "https://api.example.com" };
    localStorage.setItem(
      KEY,
      store({
        instances: [{ id: "local", name: "local", baseUrl: "https://stale.example.com", auth: { kind: "none" } }],
        activeId: "local",
        activeNamespaces: {},
      }),
    );

    await useRegistry.persist.rehydrate();

    const managed = useRegistry.getState().instances.filter((i) => i.id === SAME_ORIGIN_INSTANCE.id);
    expect(managed).toHaveLength(1);
    expect(managed[0].baseUrl).toBe("https://api.example.com");
  });

  it("keeps the per-instance active namespace across a reload", async () => {
    localStorage.setItem(
      KEY,
      store({ instances: [], activeId: "local", activeNamespaces: { local: "flights" } }),
    );

    await useRegistry.persist.rehydrate();

    expect(useRegistry.getState().activeNamespaces.local).toBe("flights");
  });

  it("does not persist the managed default (partialize)", async () => {
    const created = useRegistry
      .getState()
      .addInstance({ name: "prod", baseUrl: "https://p.example.com", auth: { kind: "none" } });
    await Promise.resolve();

    const persisted = JSON.parse(localStorage.getItem(KEY) ?? "{}") as {
      state: { instances: { id: string }[]; namespaceSupport?: unknown };
    };
    const ids = persisted.state.instances.map((i) => i.id);
    expect(ids).not.toContain(SAME_ORIGIN_INSTANCE.id);
    expect(ids).toContain(created.id);
    expect(persisted.state.namespaceSupport).toBeUndefined();
  });

  it("refuses to remove the managed default", () => {
    const created = useRegistry
      .getState()
      .addInstance({ name: "prod", baseUrl: "https://p.example.com", auth: { kind: "none" } });

    useRegistry.getState().removeInstance(SAME_ORIGIN_INSTANCE.id);
    expect(useRegistry.getState().instances.map((i) => i.id)).toContain(SAME_ORIGIN_INSTANCE.id);

    // a personal instance is still removable
    useRegistry.getState().removeInstance(created.id);
    expect(useRegistry.getState().instances.map((i) => i.id)).not.toContain(created.id);
  });
});
