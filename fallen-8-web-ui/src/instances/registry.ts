// MIT License
//
// registry.ts
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

import { create } from "zustand";
import { persist } from "zustand/middleware";
import type { InstanceConfig } from "./types";
import { normalizeBaseUrl } from "./types";
import { getInstanceStore } from "../state/instanceStore";

/**
 * Global instance registry (FR-1a) + the single active instance (FR-1b).
 * Persisted in local storage; per-instance workspace state lives elsewhere
 * (state/instanceStore.ts) keyed by instance id (FR-1c).
 */

export const DEFAULT_NAMESPACE = "default";

export interface RegistryState {
  instances: InstanceConfig[];
  activeId: string | null;
  /** The active namespace per instance id (feature graph-namespaces); absent = "default". */
  activeNamespaces: Record<string, string>;
  /**
   * Whether an instance's server supports namespaces (probed via GET /ns): true/false once
   * known, absent while unknown. A pre-namespace server (false) gets UNBOUND instances —
   * bare paths — so the previous release keeps working instead of 404ing on /ns/default.
   */
  namespaceSupport: Record<string, boolean>;
  addInstance: (instance: Omit<InstanceConfig, "id">) => InstanceConfig;
  updateInstance: (id: string, patch: Partial<Omit<InstanceConfig, "id">>) => void;
  removeInstance: (id: string) => void;
  setActive: (id: string) => void;
  setActiveNamespace: (instanceId: string, namespace: string) => void;
  setNamespaceSupport: (instanceId: string, supported: boolean) => void;
}

function newId(): string {
  return `i-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
}

/**
 * The data-plane endpoint injected by config.js (feature standalone-ui): a classic
 * `<script src="/config.js">` sets `window.__F8_CONFIG__` before this module evaluates, so a
 * standalone Studio build can be pointed at any REST origin at container start. Absent or empty
 * means same-origin (the all-in-one default). Read through normalizeBaseUrl so a trailing slash in
 * F8_API_URL cannot corrupt buildUrl concatenation, and as a function (not an inline const) so it
 * stays unit-testable after the global is set.
 */
export function configuredApiUrl(): string {
  const raw = typeof window !== "undefined" ? (window.__F8_CONFIG__?.apiUrl ?? "") : "";
  return normalizeBaseUrl(raw);
}

/** The instance the app is served from - always available, never removable by accident. */
export const SAME_ORIGIN_INSTANCE: InstanceConfig = {
  id: "local",
  name: "local",
  baseUrl: configuredApiUrl(),
  auth: { kind: "none" },
};

export const useRegistry = create<RegistryState>()(
  persist(
    (set) => ({
      instances: [SAME_ORIGIN_INSTANCE],
      activeId: SAME_ORIGIN_INSTANCE.id,
      activeNamespaces: {},
      namespaceSupport: {},

      addInstance: (instance) => {
        const created: InstanceConfig = {
          ...instance,
          baseUrl: normalizeBaseUrl(instance.baseUrl),
          id: newId(),
        };
        set((s) => ({ instances: [...s.instances, created] }));
        return created;
      },

      updateInstance: (id, patch) =>
        set((s) => ({
          instances: s.instances.map((instance) =>
            instance.id === id
              ? {
                  ...instance,
                  ...patch,
                  baseUrl:
                    patch.baseUrl !== undefined
                      ? normalizeBaseUrl(patch.baseUrl)
                      : instance.baseUrl,
                }
              : instance,
          ),
        })),

      // The managed default (config.js-seeded, feature standalone-ui) is never removable: it is
      // synthesized rather than persisted, and the connection gate assumes a default is present.
      removeInstance: (id) =>
        set((s) => {
          if (id === SAME_ORIGIN_INSTANCE.id) return s;
          const instances = s.instances.filter((instance) => instance.id !== id);
          const activeId =
            s.activeId === id ? (instances[0]?.id ?? null) : s.activeId;
          return { instances, activeId };
        }),

      setActive: (id) =>
        set((s) => (s.instances.some((instance) => instance.id === id) ? { activeId: id } : s)),

      setActiveNamespace: (instanceId, namespace) =>
        set((s) => ({
          activeNamespaces: { ...s.activeNamespaces, [instanceId]: namespace },
        })),

      setNamespaceSupport: (instanceId, supported) =>
        set((s) =>
          s.namespaceSupport[instanceId] === supported
            ? s
            : { namespaceSupport: { ...s.namespaceSupport, [instanceId]: supported } },
        ),
    }),
    {
      name: "f8.instances",
      // The managed default instance is config.js-seeded (feature standalone-ui) and is NEVER
      // persisted: only personal (user-added) instances, the active id, and the per-instance
      // active namespace are stored. namespaceSupport is deliberately dropped (a re-probeable /ns
      // cache). See features/open/standalone-ui/.
      partialize: (s) => ({
        instances: s.instances.filter((i) => i.id !== SAME_ORIGIN_INSTANCE.id),
        activeId: s.activeId,
        activeNamespaces: s.activeNamespaces,
      }),
      // Re-inject the freshly synthesized managed default (baseUrl re-read from config.js via
      // configuredApiUrl) ahead of the persisted personal instances on every load, so it is always
      // present and re-synced. zustand's default shallow merge would otherwise let the persisted
      // (personal-only) instances array drop it, leaving a persisted activeId==="local" resolving
      // to null. This also transparently upgrades a legacy blob that persisted the whole state (its
      // stale "local" record is filtered out), so no version bump / migrate is needed.
      merge: (persisted, current) => {
        const p = (persisted ?? {}) as Partial<RegistryState>;
        const personal = (p.instances ?? []).filter((i) => i.id !== SAME_ORIGIN_INSTANCE.id);
        const managed: InstanceConfig = { ...SAME_ORIGIN_INSTANCE, baseUrl: configuredApiUrl() };
        return {
          ...current,
          ...p,
          instances: [managed, ...personal],
          activeId: p.activeId ?? current.activeId,
          activeNamespaces: p.activeNamespaces ?? current.activeNamespaces,
          namespaceSupport: current.namespaceSupport,
        };
      },
    },
  ),
);

/** The active instance's active namespace (feature graph-namespaces); "default" until set. */
export function useActiveNamespace(): string {
  return useRegistry(
    (s) => (s.activeId && s.activeNamespaces[s.activeId]) || DEFAULT_NAMESPACE,
  );
}

export function useActiveInstance(): InstanceConfig | null {
  return useRegistry((s) => s.instances.find((instance) => instance.id === s.activeId) ?? null);
}

/**
 * The active instance plus its per-namespace workspace store - the preamble every connected
 * screen needs (the AppShell connection gate guarantees an active instance, hence the non-null).
 * The returned instance is NAMESPACE-BOUND (feature graph-namespaces): its API calls address
 * /ns/{activeNamespace}/… explicitly, and the workspace store is keyed per namespace so
 * results, drafts and canvas state never cross namespaces.
 */
export function useInstanceStore() {
  const instance = useActiveInstance()!;
  const namespace = useActiveNamespace();
  const supported = useRegistry((s) => s.namespaceSupport[instance.id]);

  // A server known to predate namespaces gets the UNBOUND view: bare paths (which are the
  // whole graph there) and the legacy workspace store — full graceful degradation.
  if (supported === false) {
    return { instance, store: getInstanceStore(instance.id) };
  }

  return {
    instance: {
      ...instance,
      // The bound view's id is "<instance-id>/<namespace>" ON PURPOSE: every react-query
      // key and cache derived from `instance.id` becomes per-namespace at once, so no
      // screen can serve another namespace's cached results. The registry keeps the raw id.
      id: `${instance.id}/${namespace}`,
      namespace,
    },
    store: getInstanceStore(instance.id, namespace),
  };
}
