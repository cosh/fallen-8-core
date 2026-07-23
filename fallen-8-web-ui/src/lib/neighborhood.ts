import {
  getEdge,
  getGraphElement,
  getInEdgeProperties,
  getInEdges,
  getOutEdgeProperties,
  getOutEdges,
  getVertex,
} from "../api/endpoints";
import type { CanvasEdgeInput, EdgeREST, VertexREST } from "../api/types";
import type { InstanceConfig } from "../instances/types";

/**
 * 1-hop neighborhood fetch over the per-property adjacency routes (feature
 * adjacency-preview) — the one home for "edge-id lists → hydrated edges + endpoint
 * vertices", shared by the Canvas screen's expand-on-demand and the Browser screen's
 * adjacency preview. The REST Edge DTO carries no property id; the per-property listings
 * do, so the attribution happens here. Failed hydrations are skipped, not errors.
 */

/** Edge cap for the adjacency preview — a teaser, not the canvas working set. */
export const PREVIEW_EDGE_CAP = 60;
/** Edge cap for canvas expand-on-demand (pre-feature constant, unchanged). */
export const EXPAND_EDGE_CAP = 200;

export interface Neighborhood {
  /** Hydrated endpoint vertices, minus skipNeighborIds and failed fetches. */
  vertices: VertexREST[];
  edges: CanvasEdgeInput[];
  /** true when the cap cut off edges or endpoint vertices. */
  truncated: boolean;
}

/** One direction's edge ids with their property attribution (first property wins). */
async function directedEdgeIds(
  instance: InstanceConfig,
  vertexId: number,
  direction: "out" | "in",
): Promise<Map<number, string>> {
  const properties =
    (direction === "out"
      ? await getOutEdgeProperties(instance, vertexId).catch(() => [])
      : await getInEdgeProperties(instance, vertexId).catch(() => [])) ?? [];
  const perProperty = await Promise.all(
    properties.map(async (property) => ({
      property,
      ids:
        (direction === "out"
          ? await getOutEdges(instance, vertexId, property).catch(() => [])
          : await getInEdges(instance, vertexId, property).catch(() => [])) ?? [],
    })),
  );
  const byId = new Map<number, string>();
  for (const { property, ids } of perProperty) {
    for (const id of ids) {
      if (!byId.has(id)) byId.set(id, property);
    }
  }
  return byId;
}

async function hydrateEdges(
  instance: InstanceConfig,
  ids: number[],
  propertyById: Map<number, string>,
): Promise<CanvasEdgeInput[]> {
  return (await Promise.all(ids.map((id) => getEdge(instance, id).catch(() => null))))
    .filter((e): e is EdgeREST => e !== null)
    .map((e) => ({ ...e, edgePropertyId: propertyById.get(e.id) ?? null }));
}

/**
 * A vertex's adjacent edges plus their endpoint vertices. Self-loops appear once (the
 * "out" attribution wins). The cap applies to edges and endpoint vertices alike.
 */
export async function fetchVertexNeighborhood(
  instance: InstanceConfig,
  vertexId: number,
  options: { cap: number; skipNeighborIds?: ReadonlySet<number> },
): Promise<Neighborhood> {
  const skip = options.skipNeighborIds ?? new Set<number>();
  const [outIds, inIds] = await Promise.all([
    directedEdgeIds(instance, vertexId, "out"),
    directedEdgeIds(instance, vertexId, "in"),
  ]);
  const propertyById = new Map([...outIds, ...[...inIds].filter(([id]) => !outIds.has(id))]);

  const edgeIds = [...propertyById.keys()];
  const edges = await hydrateEdges(instance, edgeIds.slice(0, options.cap), propertyById);

  const neighborIds = new Set<number>();
  for (const edge of edges) {
    neighborIds.add(edge.sourceVertex);
    neighborIds.add(edge.targetVertex);
  }
  const wanted = [...neighborIds].filter((id) => !skip.has(id));
  const vertices = (
    await Promise.all(
      wanted
        .slice(0, options.cap)
        .map((id) => getGraphElement(instance, id).catch(() => null)),
    )
  ).filter((v): v is VertexREST => v !== null);

  return {
    vertices,
    edges,
    truncated: edgeIds.length > options.cap || wanted.length > options.cap,
  };
}

/**
 * An edge's endpoint vertices plus EVERY edge between them, both directions — found by
 * intersecting the endpoints' id lists (out(s)∩in(t) ∪ in(s)∩out(t)), never by hydrating
 * a supernode's full edge set. The focus edge is always first in the result, even if the
 * listings raced a mutation.
 */
export async function fetchEdgeNeighborhood(
  instance: InstanceConfig,
  edge: EdgeREST,
  options: { cap: number },
): Promise<Neighborhood> {
  const [source, target, sourceOut, sourceIn, targetOut, targetIn] = await Promise.all([
    getVertex(instance, edge.sourceVertex).catch(() => null),
    getVertex(instance, edge.targetVertex).catch(() => null),
    directedEdgeIds(instance, edge.sourceVertex, "out"),
    directedEdgeIds(instance, edge.sourceVertex, "in"),
    directedEdgeIds(instance, edge.targetVertex, "out"),
    directedEdgeIds(instance, edge.targetVertex, "in"),
  ]);

  const propertyById = new Map<number, string>();
  for (const [id, property] of sourceOut) {
    if (targetIn.has(id)) propertyById.set(id, property);
  }
  for (const [id, property] of sourceIn) {
    if (targetOut.has(id) && !propertyById.has(id)) propertyById.set(id, property);
  }

  const siblingIds = [...propertyById.keys()].filter((id) => id !== edge.id);
  const siblings = await hydrateEdges(
    instance,
    siblingIds.slice(0, options.cap - 1),
    propertyById,
  );

  return {
    vertices: [source, target].filter((v): v is VertexREST => v !== null),
    edges: [{ ...edge, edgePropertyId: propertyById.get(edge.id) ?? null }, ...siblings],
    truncated: siblingIds.length > options.cap - 1,
  };
}
