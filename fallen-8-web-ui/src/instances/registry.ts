import { create } from "zustand";
import { persist } from "zustand/middleware";
import type { InstanceConfig } from "./types";
import { normalizeBaseUrl } from "./types";

/**
 * Global instance registry (FR-1a) + the single active instance (FR-1b).
 * Persisted in local storage; per-instance workspace state lives elsewhere
 * (state/instanceStore.ts) keyed by instance id (FR-1c).
 */

export interface RegistryState {
  instances: InstanceConfig[];
  activeId: string | null;
  addInstance: (instance: Omit<InstanceConfig, "id">) => InstanceConfig;
  updateInstance: (id: string, patch: Partial<Omit<InstanceConfig, "id">>) => void;
  removeInstance: (id: string) => void;
  setActive: (id: string) => void;
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
    }),
    { name: "f8.instances" },
  ),
);

export function useActiveInstance(): InstanceConfig | null {
  return useRegistry((s) => s.instances.find((instance) => instance.id === s.activeId) ?? null);
}
