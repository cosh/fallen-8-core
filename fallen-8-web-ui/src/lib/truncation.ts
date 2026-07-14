import type { GraphREST } from "../api/types";

/**
 * FR-7: GET /graph?maxElements=N truncates silently (no paging, no marker). The only
 * reliable signal is the returned element count reaching the requested cap, so whenever
 * that happens the UI must show an explicit "truncated at N" indicator.
 */
export function isTruncated(graph: GraphREST, requestedMax: number): boolean {
  const returned = (graph.vertices?.length ?? 0) + (graph.edges?.length ?? 0);
  return returned >= requestedMax;
}
