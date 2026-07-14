import { getGraphElement } from "../api/endpoints";
import type { EdgeREST, VertexREST } from "../api/types";
import type { InstanceConfig } from "../instances/types";

/**
 * Scans return bare id lists (FR-8); this hydrates them into elements via
 * GET /graphelement/{id} in capped, batched rounds with visible progress. Missing ids
 * (deleted between scan and hydration) resolve to null and are skipped, not errors.
 */

export const HYDRATION_BATCH_SIZE = 25;
export const HYDRATION_DEFAULT_CAP = 500;

export interface HydrationProgress {
  done: number;
  total: number;
}

export function isEdge(element: VertexREST | EdgeREST): element is EdgeREST {
  return (element as EdgeREST).sourceVertex !== undefined;
}

export async function hydrateElements(
  instance: InstanceConfig,
  ids: number[],
  options: {
    cap?: number;
    onProgress?: (progress: HydrationProgress) => void;
    signal?: AbortSignal;
  } = {},
): Promise<{ elements: (VertexREST | EdgeREST)[]; capped: boolean }> {
  const cap = options.cap ?? HYDRATION_DEFAULT_CAP;
  const target = ids.slice(0, cap);
  const elements: (VertexREST | EdgeREST)[] = [];

  for (let start = 0; start < target.length; start += HYDRATION_BATCH_SIZE) {
    const batch = target.slice(start, start + HYDRATION_BATCH_SIZE);
    const settled = await Promise.all(
      batch.map((id) => getGraphElement(instance, id, options.signal).catch(() => null)),
    );
    for (const element of settled) {
      if (element !== null) elements.push(element);
    }
    options.onProgress?.({ done: Math.min(start + batch.length, target.length), total: target.length });
    if (options.signal?.aborted) break;
  }

  return { elements, capped: ids.length > cap };
}
