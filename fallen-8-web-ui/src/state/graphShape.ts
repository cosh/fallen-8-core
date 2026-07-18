import { useQuery } from "@tanstack/react-query";
import { getStatistics } from "../api/endpoints";
import type { EmbeddingProviderStatsREST, GraphStatisticsREST } from "../api/types";
import type { InstanceConfig } from "../instances/types";

/** The reserved property-key prefix element embeddings are stored under (server contract). */
export const EMBEDDING_PROPERTY_PREFIX = "$embedding:";

/** The reserved prefix carrying an embedding's model stamp (server contract, paired with the above). */
export const EMBEDDING_MODEL_PROPERTY_PREFIX = "$embeddingModel:";

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
  /**
   * Embedding names seen on the graph (feature element-embeddings): derived from the
   * reserved "$embedding:<name>" property keys in the shape snapshot, since embeddings
   * are stored as reserved properties. Empty until a shape is computed.
   */
  embeddingNames: string[];
}

/** Datalist feeds from a snapshot; all empty when none has been computed yet. */
export function shapeSuggestions(
  shape: GraphStatisticsREST | null | undefined,
): ShapeSuggestions {
  const names = (top: { name: string | null }[] | null | undefined) =>
    (top ?? []).map((t) => t.name).filter((n): n is string => Boolean(n));
  const propertyKeys = names(shape?.propertyKeys?.top);
  return {
    vertexLabels: names(shape?.vertexLabels?.top),
    edgeLabels: names(shape?.edgeLabels?.top),
    // The reserved embedding keys are folded out of the plain property-key suggestions —
    // a user picking a property to scan should not see "$embedding:default".
    propertyKeys: propertyKeys.filter((k) => !k.startsWith(EMBEDDING_PROPERTY_PREFIX)),
    embeddingNames: propertyKeys
      .filter((k) => k.startsWith(EMBEDDING_PROPERTY_PREFIX))
      .map((k) => k.slice(EMBEDDING_PROPERTY_PREFIX.length))
      .filter(Boolean),
    indexIds: (shape?.indices ?? [])
      .map((i) => i.name)
      .filter((n): n is string => Boolean(n)),
  };
}

/**
 * The embedding-provider snapshot from a computed graph shape (feature embedding-provider).
 * null when no shape has been computed OR the server predates the provider feature — the
 * UI treats "unknown" like "not confirmed enabled": text-in controls stay disabled with a
 * hint to Compute the Graph shape, while bring-your-own-vector paths always work.
 */
export function embeddingProvider(
  shape: GraphStatisticsREST | null | undefined,
): EmbeddingProviderStatsREST | null {
  return shape?.embedding ?? null;
}
