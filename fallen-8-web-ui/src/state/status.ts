import { useQuery } from "@tanstack/react-query";
import { getStatus } from "../api/endpoints";
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
