// MIT License
//
// studio-config.test.ts
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
import { setStudioConfig, storageKey, themeStyle, type StudioConfig } from "../src/app/studioConfig";
import { applyStudioConfig } from "../src/app/applyStudioConfig";
import {
  applyStudioConfigToRegistry,
  isManagedInstance,
  SAME_ORIGIN_INSTANCE,
  useRegistry,
} from "../src/instances/registry";
import { getInstanceStore, resetInstanceStoresForTests } from "../src/state/instanceStore";
import { useNlAssist } from "../src/delegate/nl/config";
import { useFirstRun } from "../src/firstrun/firstRunStore";
import type { InstanceConfig } from "../src/instances/types";

/**
 * The StudioConfig seam (feature studio-embeddable): host-supplied managed instances flow
 * into the registry through the same merge that standalone-ui built for the config.js
 * default - never persisted, re-synthesized on every hydration - and the host knobs
 * (activeInstanceId, namespace pin, storage namespace) apply on rehydrate. The default
 * config must reproduce standalone behavior exactly.
 */

const KEY = "f8.instances";
const store = (state: unknown) => JSON.stringify({ state, version: 0 });

const tenant = (n: number): InstanceConfig => ({
  id: `tenant-${n}`,
  name: `Tenant ${n}`,
  baseUrl: `https://api.example.com/i/${n}/`,
  auth: { kind: "bearer", getToken: async () => `token-${n}` },
});

/** Registry-only application (the seam most assertions here are about). */
async function applyConfig(config: StudioConfig): Promise<void> {
  setStudioConfig(config);
  await applyStudioConfigToRegistry();
}

beforeEach(async () => {
  localStorage.clear();
  await applyConfig({});
});

afterEach(async () => {
  resetInstanceStoresForTests();
  localStorage.clear();
  await applyConfig({});
});

describe("default config == standalone", () => {
  it("keeps the config.js-seeded default as the only managed instance", () => {
    const s = useRegistry.getState();
    expect(s.instances.map((i) => i.id)).toEqual([SAME_ORIGIN_INSTANCE.id]);
    expect(s.activeId).toBe(SAME_ORIGIN_INSTANCE.id);
    expect(isManagedInstance(SAME_ORIGIN_INSTANCE.id)).toBe(true);
  });

  it("leaves storage keys unprefixed", () => {
    expect(storageKey("f8.instances")).toBe("f8.instances");
  });
});

describe("host-supplied instances", () => {
  it("treats an empty instances array as the standalone default", async () => {
    await applyConfig({ instances: [] });

    expect(useRegistry.getState().instances.map((i) => i.id)).toEqual([SAME_ORIGIN_INSTANCE.id]);
    expect(isManagedInstance(SAME_ORIGIN_INSTANCE.id)).toBe(true);
  });

  it("replace the managed default, normalize the baseUrl, and become the active instance", async () => {
    await applyConfig({ instances: [tenant(1)] });

    const s = useRegistry.getState();
    expect(s.instances.map((i) => i.id)).toEqual(["tenant-1"]);
    expect(s.instances[0].baseUrl).toBe("https://api.example.com/i/1");
    expect(s.activeId).toBe("tenant-1");
    expect(isManagedInstance("tenant-1")).toBe(true);
    expect(isManagedInstance(SAME_ORIGIN_INSTANCE.id)).toBe(false);
  });

  it("are never persisted (a bearer credential is a callback and must not serialize)", async () => {
    await applyConfig({ instances: [tenant(1)] });

    // Any state change triggers a persist write of the partialized state.
    useRegistry.getState().setActiveNamespace("tenant-1", "ops");
    await Promise.resolve();

    const persisted = JSON.parse(localStorage.getItem(KEY) ?? "{}") as {
      state: { instances: unknown[] };
    };
    expect(persisted.state.instances).toEqual([]);
  });

  it("are not removable", async () => {
    await applyConfig({ instances: [tenant(1)] });

    useRegistry.getState().removeInstance("tenant-1");
    expect(useRegistry.getState().instances.map((i) => i.id)).toContain("tenant-1");
  });

  it("honor activeInstanceId over a persisted choice", async () => {
    localStorage.setItem(
      KEY,
      store({ instances: [], activeId: "tenant-1", activeNamespaces: {} }),
    );
    await applyConfig({ instances: [tenant(1), tenant(2)], activeInstanceId: "tenant-2" });

    expect(useRegistry.getState().activeId).toBe("tenant-2");
  });

  it("fall back to the first managed instance when the persisted activeId no longer exists", async () => {
    localStorage.setItem(
      KEY,
      store({ instances: [], activeId: "gone", activeNamespaces: {} }),
    );
    await applyConfig({ instances: [tenant(1)] });

    expect(useRegistry.getState().activeId).toBe("tenant-1");
  });

  it("never resurrect a legacy blob's same-origin record as a personal instance", async () => {
    // Pre-standalone-ui Studio persisted the WHOLE state, "local" record included. "local"
    // is a reserved id, so a host embed on that origin must not adopt it as a personal
    // instance (nor let its persisted activeId point the embed at the host's own origin).
    localStorage.setItem(
      KEY,
      store({
        instances: [
          { id: "local", name: "local", baseUrl: "", auth: { kind: "none" } },
          { id: "i-x", name: "prod", baseUrl: "https://p.example.com", auth: { kind: "none" } },
        ],
        activeId: "local",
        activeNamespaces: {},
      }),
    );
    await applyConfig({ instances: [tenant(1)] });

    const s = useRegistry.getState();
    expect(s.instances.map((i) => i.id)).toEqual(["tenant-1", "i-x"]);
    expect(s.activeId).toBe("tenant-1");
  });

  it("restores the standalone default when a later mount applies the default config", async () => {
    await applyConfig({ instances: [tenant(1)] });
    await applyConfig({});

    const s = useRegistry.getState();
    expect(s.instances.map((i) => i.id)).toEqual([SAME_ORIGIN_INSTANCE.id]);
    expect(s.activeId).toBe(SAME_ORIGIN_INSTANCE.id);
  });
});

describe("namespace pin", () => {
  it("seeds the active namespace when nothing is remembered", async () => {
    await applyConfig({ instances: [tenant(1)], namespace: "ops" });

    expect(useRegistry.getState().activeNamespaces["tenant-1"]).toBe("ops");
  });

  it("lets a remembered namespace win without lockNamespace", async () => {
    localStorage.setItem(
      KEY,
      store({ instances: [], activeId: "tenant-1", activeNamespaces: { "tenant-1": "kept" } }),
    );
    await applyConfig({ instances: [tenant(1)], namespace: "ops" });

    expect(useRegistry.getState().activeNamespaces["tenant-1"]).toBe("kept");
  });

  it("pins over a remembered namespace with lockNamespace", async () => {
    localStorage.setItem(
      KEY,
      store({ instances: [], activeId: "tenant-1", activeNamespaces: { "tenant-1": "kept" } }),
    );
    await applyConfig({ instances: [tenant(1)], namespace: "ops", lockNamespace: true });

    expect(useRegistry.getState().activeNamespaces["tenant-1"]).toBe("ops");
  });
});

describe("storageNamespace", () => {
  it("prefixes every persisted key and leaves the bare keys alone", async () => {
    await applyConfig({ storageNamespace: "acme.", instances: [tenant(1)] });

    useRegistry.getState().setActiveNamespace("tenant-1", "ops");
    await Promise.resolve();

    expect(localStorage.getItem("acme.f8.instances")).not.toBeNull();
    expect(localStorage.getItem(KEY)).toBeNull();
    expect(storageKey("f8.nl-assist")).toBe("acme.f8.nl-assist");
  });

  it("routes the per-instance workspace stores through the prefix", async () => {
    await applyConfig({ storageNamespace: "acme.", instances: [tenant(1)] });

    getInstanceStore("tenant-1").getState().setBrowserDraft({});
    await Promise.resolve();

    expect(localStorage.getItem("acme.f8.workspace.tenant-1")).not.toBeNull();
    expect(localStorage.getItem("f8.workspace.tenant-1")).toBeNull();
  });
});

/**
 * The isolation the prefix is FOR. Two tenants, sequential mounts in one realm (the
 * documented way to reconfigure): neither the module-level stores' state nor the memoized
 * workspace stores may carry across, because both would then be written into the next
 * tenant's storage universe.
 */
describe("cross-tenant isolation between sequential mounts", () => {
  it("does not carry a tenant's workspace store into the next tenant's mount", async () => {
    await applyStudioConfig({ storageNamespace: "a.", instances: [tenant(1)] });
    getInstanceStore("tenant-1").getState().setBrowserDraft({ bulkFilter: "tenant-a-secret" });
    await Promise.resolve();

    await applyStudioConfig({ storageNamespace: "b.", instances: [tenant(1)] });
    const draft = getInstanceStore("tenant-1").getState().browserDraft;

    expect(draft.bulkFilter).not.toBe("tenant-a-secret");
    expect(localStorage.getItem("a.f8.workspace.tenant-1")).not.toBeNull();
  });

  it("does not carry NL-assist config (including its api key) into the next tenant's mount", async () => {
    await applyStudioConfig({ storageNamespace: "a.", instances: [tenant(1)] });
    useNlAssist.getState().setConfig({ mode: "custom", endpoint: "https://a.example.com", apiKey: "sk-tenant-a" });
    await Promise.resolve();

    await applyStudioConfig({ storageNamespace: "b.", instances: [tenant(1)] });

    expect(useNlAssist.getState().config.apiKey).toBeUndefined();
    expect(useNlAssist.getState().config.endpoint).toBe("");
    // ...and the next write cannot smuggle it into tenant B's universe.
    useNlAssist.getState().setConfig({ model: "phi4-f8-mini" });
    await Promise.resolve();
    expect(localStorage.getItem("b.f8.nl-assist")).not.toContain("sk-tenant-a");
  });

  it("does not carry first-run dismissals or the active namespace into the next tenant's mount", async () => {
    await applyStudioConfig({ storageNamespace: "a.", instances: [tenant(1)] });
    useFirstRun.getState().dismiss("tenant-1/ops");
    useRegistry.getState().setActiveNamespace("tenant-1", "tenant-a-ns");
    await Promise.resolve();

    await applyStudioConfig({ storageNamespace: "b.", instances: [tenant(1)] });

    expect(useFirstRun.getState().dismissed).toEqual({});
    expect(useRegistry.getState().activeNamespaces["tenant-1"]).toBeUndefined();
  });
});

describe("themeStyle", () => {
  it("maps token overrides to the CSS custom properties, skipping omitted ones", () => {
    expect(themeStyle({ accent: "#e2001a", panel2: "#101418", fontMono: "monospace" })).toEqual({
      "--color-accent": "#e2001a",
      "--color-panel-2": "#101418",
      "--font-mono": "monospace",
    });
    expect(themeStyle(undefined)).toEqual({});
  });
});
