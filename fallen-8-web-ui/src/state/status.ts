import { useQuery } from "@tanstack/react-query";
import { getConfig, getStatus } from "../api/endpoints";
import type { InstanceConfig } from "../instances/types";

/**
 * The shared /status cache entry. Same query key as the AppShell health probe, so every
 * consumer reads one cache row and rides its periodic refresh; /status is the cheap
 * discovery surface (available plugins + live index inventory — feature
 * studio-index-discovery), unlike the budgeted Graph-shape pass in graphShape.ts.
 */
export function useStatus(instance: InstanceConfig) {
  return useQuery({
    queryKey: [instance.id, "status"],
    queryFn: ({ signal }) => getStatus(instance, signal),
  });
}

/**
 * The instance's read-only configuration (feature instance-config): the semantic providers
 * and observability posture behind the Connect Configuration section. Fallen-8-level, so it
 * is keyed by the RAW instance id (not per namespace) and API-key gated server-side.
 */
export function useConfig(instance: InstanceConfig) {
  return useQuery({
    queryKey: [instance.id, "config"],
    queryFn: ({ signal }) => getConfig(instance, signal),
    retry: 0,
    // Re-check periodically so model residency (a model loads/unloads in the sidecar over time)
    // updates on its own; the panel also offers a manual Refresh. GET /config's probe is bounded.
    refetchInterval: 10_000,
  });
}
