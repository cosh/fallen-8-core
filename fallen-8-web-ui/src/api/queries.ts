import type { QueryClient } from "@tanstack/react-query";

/**
 * Invalidates EVERY query of an instance — the raw-keyed Fallen-8-level ones
 * (`[<id>, ...]`: save games, benchmark, the namespace inventory) AND the per-namespace
 * ones keyed by the bound view's compound id (`[<id>/<ns>, ...]`, feature
 * graph-namespaces). Accepts either id shape.
 */
export function invalidateInstanceQueries(
  queryClient: QueryClient,
  instanceId: string,
): Promise<void> {
  const raw = instanceId.split("/")[0];
  return queryClient.invalidateQueries({
    predicate: (query) => {
      const head = query.queryKey[0];
      return typeof head === "string" && (head === raw || head.startsWith(`${raw}/`));
    },
  });
}
