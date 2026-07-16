import { useQuery } from "@tanstack/react-query";
import { getStatistics } from "../api/endpoints";
import type { GraphStatisticsREST } from "../api/types";
import type { InstanceConfig } from "../instances/types";

/**
 * The per-instance graph-shape snapshot (feature studio-coverage): one react-query cache
 * entry shared by every consumer. `enabled: false` — the endpoint is a budgeted O(V+E)
 * pass behind a rate limiter, so ONLY the Graph shape panel's Compute button (refetch())
 * ever triggers it; other screens just read whatever snapshot exists. This is the schema
 * cache that closes gap G-3: label / property-key / index-id suggestions everywhere ids
 * are free-form, with free-form input still working when no snapshot has been computed.
 */
export function useGraphShape(instance: InstanceConfig) {
  return useQuery<GraphStatisticsREST | null>({
    queryKey: [instance.id, "statistics"],
    queryFn: ({ signal }) => getStatistics(instance, signal),
    enabled: false,
    staleTime: Infinity,
    gcTime: Infinity,
    retry: 0,
  });
}

export interface ShapeSuggestions {
  vertexLabels: string[];
  edgeLabels: string[];
  propertyKeys: string[];
  indexIds: string[];
}

/** Datalist feeds from a snapshot; all empty when none has been computed yet. */
export function shapeSuggestions(
  shape: GraphStatisticsREST | null | undefined,
): ShapeSuggestions {
  const names = (top: { name: string | null }[] | null | undefined) =>
    (top ?? []).map((t) => t.name).filter((n): n is string => Boolean(n));
  return {
    vertexLabels: names(shape?.vertexLabels?.top),
    edgeLabels: names(shape?.edgeLabels?.top),
    propertyKeys: names(shape?.propertyKeys?.top),
    indexIds: (shape?.indices ?? [])
      .map((i) => i.name)
      .filter((n): n is string => Boolean(n)),
  };
}
