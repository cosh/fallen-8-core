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
import { getStudioConfig, storageKey } from "../app/studioConfig";

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

/**
 * The MANAGED instances: supplied from outside the registry, synthesized fresh on every
 * (re)hydration, never persisted. Two producers feed this one seam (feature studio-embeddable
 * on top of standalone-ui): the config.js-seeded same-origin default (standalone, the
 * fallback), or the host's StudioConfig.instances (embed). Personal instances the user
 * registers on the Connect screen are everything managed is not: persisted, editable,
 * removable.
 */
function managedInstances(): InstanceConfig[] {
  const configured = getStudioConfig().instances;
  if (configured && configured.length > 0) {
    return configured.map((instance) => ({
      ...instance,
      baseUrl: normalizeBaseUrl(instance.baseUrl),
    }));
  }
  return [{ ...SAME_ORIGIN_INSTANCE, baseUrl: configuredApiUrl() }];
}

export function isManagedInstance(id: string): boolean {
  const configured = getStudioConfig().instances;
  if (configured && configured.length > 0) return configured.some((i) => i.id === id);
  return id === SAME_ORIGIN_INSTANCE.id;
}

/**
 * Ids that may never be treated as a personal (persisted, removable) instance. The managed
 * ids of the CURRENT config, plus "local" unconditionally: it is the reserved id of the
 * same-origin default, so a legacy blob that persisted the whole state (pre-standalone-ui)
 * must keep being filtered out even while a host config owns the registry - otherwise its
 * stale "local" record would resurrect as a personal instance, and a persisted
 * activeId: "local" would resolve to it instead of the host's instance.
 */
function isReservedInstanceId(id: string): boolean {
  return id === SAME_ORIGIN_INSTANCE.id || isManagedInstance(id);
}

export const useRegistry = create<RegistryState>()(
  persist(
    (set) => ({
      instances: managedInstances(),
      activeId: managedInstances()[0].id,
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

      // A managed instance (config.js-seeded default or a host-supplied one, see
      // managedInstances) is never removable: it is synthesized rather than persisted, and
      // the connection gate assumes a default is present.
      removeInstance: (id) =>
        set((s) => {
          if (isManagedInstance(id)) return s;
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
      name: storageKey("f8.instances"),
      // This store is created at module import, BEFORE any mount has set a StudioConfig, so
      // it must not hydrate from the bare `f8.instances` key on the way past: a prefixed
      // embed would inherit that state (zustand's hydrate merges `undefined` over the
      // CURRENT state when its key is empty, keeping whatever the import-time read left).
      // Every mount path calls applyStudioConfig -> rehydrate against the resolved key.
      skipHydration: true,
      // Managed instances (config.js-seeded default or host-supplied, see managedInstances)
      // are NEVER persisted: only personal (user-added) instances, the active id, and the
      // per-instance active namespace are stored. namespaceSupport is deliberately dropped
      // (a re-probeable /ns cache). See features/done/standalone-ui/.
      partialize: (s) => ({
        instances: s.instances.filter((i) => !isReservedInstanceId(i.id)),
        activeId: s.activeId,
        activeNamespaces: s.activeNamespaces,
      }),
      // Re-inject the freshly synthesized managed instances (config.js baseUrl re-read, host
      // StudioConfig re-read) ahead of the persisted personal instances on every load, so they
      // are always present and re-synced. zustand's default shallow merge would otherwise let
      // the persisted (personal-only) instances array drop them, leaving a persisted managed
      // activeId resolving to null. This also transparently upgrades a legacy blob that
      // persisted the whole state (its stale managed records are filtered out), so no version
      // bump / migrate is needed. The host-embed knobs live here too, so they apply on every
      // (re)hydration: activeInstanceId wins over a persisted choice, an activeId pointing at
      // an instance that no longer exists falls back to the first managed one, and
      // config.namespace seeds each managed instance's active namespace (a persisted choice
      // wins unless lockNamespace pins it).
      //
      // Every persisted field is derived from STORAGE + CONFIG ONLY, never from `current`:
      // hydrating against an empty key (a mount that switched storageNamespace) calls
      // merge(undefined, current), so a `current` fallback would carry the previous mount's
      // state into the new tenant's universe and persist it there on the next write. Only
      // the action functions come from `current`.
      merge: (persisted, current) => {
        const config = getStudioConfig();
        const p = (persisted ?? {}) as Partial<RegistryState>;
        const personal = (p.instances ?? []).filter((i) => !isReservedInstanceId(i.id));
        const managed = managedInstances();
        const instances = [...managed, ...personal];
        const requestedActive = config.activeInstanceId ?? p.activeId ?? null;
        const activeNamespaces = { ...(p.activeNamespaces ?? {}) };
        if (config.namespace) {
          for (const instance of managed) {
            if (config.lockNamespace || !activeNamespaces[instance.id]) {
              activeNamespaces[instance.id] = config.namespace;
            }
          }
        }
        return {
          ...current,
          ...p,
          instances,
          activeId: instances.some((i) => i.id === requestedActive)
            ? requestedActive
            : managed[0].id,
          activeNamespaces,
          // A re-probeable /ns cache: never persisted, and never carried across a mount.
          namespaceSupport: {},
        };
      },
    },
  ),
);

/**
 * Re-point and re-hydrate the registry after a mount set its StudioConfig (feature
 * studio-embeddable). The store is created at module import, before any config exists, so a
 * host mount re-runs persistence against the (possibly prefixed) storage key; `merge` above
 * then injects the host's managed instances and applies its activeInstanceId/namespace knobs.
 * The standalone mount runs this too, with the default config it is a no-op re-hydration.
 */
export async function applyStudioConfigToRegistry(): Promise<void> {
  useRegistry.persist.setOptions({ name: storageKey("f8.instances") });
  await useRegistry.persist.rehydrate();
}

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
 * The NAMESPACE-BOUND view of an instance (feature graph-namespaces), and the one home for that
 * rule: a bound instance addresses /ns/{namespace}/… explicitly, and its id is
 * "<instance-id>/<namespace>" ON PURPOSE, so every react-query key and cache derived from
 * `instance.id` becomes per-namespace at once and no screen can serve another namespace's cached
 * results. The registry keeps the raw id.
 *
 * A server known to predate namespaces (namespaceSupported === false) gets the UNBOUND view
 * instead: bare paths, which are the whole graph there - full graceful degradation.
 */
function boundInstance(
  instance: InstanceConfig,
  namespace: string,
  namespaceSupported: boolean | undefined,
): InstanceConfig {
  if (namespaceSupported === false) return instance;
  return { ...instance, id: `${instance.id}/${namespace}`, namespace };
}

/**
 * The active instance plus its per-namespace workspace store - the preamble every connected
 * screen needs (the AppShell connection gate guarantees an active instance, hence the non-null).
 * The instance is bound per `boundInstance`, and the workspace store is keyed per namespace so
 * results, drafts and canvas state never cross namespaces.
 */
export function useInstanceStore() {
  const instance = useActiveInstance()!;
  const namespace = useActiveNamespace();
  const supported = useRegistry((s) => s.namespaceSupport[instance.id]);

  return {
    instance: boundInstance(instance, namespace, supported),
    store:
      supported === false
        ? getInstanceStore(instance.id)
        : getInstanceStore(instance.id, namespace),
  };
}

/**
 * The bound active instance for SHELL-level consumers, or null when no instance is active.
 * `useInstanceStore` is this plus the workspace store, but it asserts an instance because it is
 * only ever called from a screen behind the connection gate; the shell itself (the durability
 * banner, the first-run auto-show) renders before that gate and must tolerate "none".
 */
export function useBoundInstance(): InstanceConfig | null {
  const instance = useActiveInstance();
  const namespace = useActiveNamespace();
  const supported = useRegistry((s) => (instance ? s.namespaceSupport[instance.id] : undefined));
  return instance ? boundInstance(instance, namespace, supported) : null;
}
