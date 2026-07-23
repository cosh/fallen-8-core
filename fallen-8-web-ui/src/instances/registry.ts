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

/** The instance the app is served from - always available, never removable by accident. */
export const SAME_ORIGIN_INSTANCE: InstanceConfig = {
  id: "local",
  name: "local",
  baseUrl: "",
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

      removeInstance: (id) =>
        set((s) => {
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
    { name: "f8.instances" },
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
