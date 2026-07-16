import { apiRequest } from "./client";
import type { InstanceConfig } from "../instances/types";
import type {
  BenchmarkResult,
  DelegateKind,
  DelegateValidationResult,
  EdgeREST,
  SaveGame,
  EdgeSpecification,
  FulltextIndexScanSpecification,
  FulltextSearchResultREST,
  GraphREST,
  GraphStatisticsREST,
  IndexAddToSpecification,
  IndexScanSpecification,
  PathREST,
  PathSpecification,
  PluginSpecification,
  PropertySpecification,
  RangeIndexScanSpecification,
  ScanSpecification,
  SearchDistanceSpecification,
  StatusREST,
  SubGraphSpecification,
  SubGraphSummary,
  VertexREST,
  VertexSpecification,
} from "./types";

/**
 * Typed wrappers over the REST surface (spec §5). Every mutation sends
 * waitForCompletion=true (FR-21) so a rolled-back transaction surfaces as a 4xx/5xx
 * instead of a fire-and-forget 202.
 */

const WAIT = { waitForCompletion: true } as const;

// ---- status + admin (FR-2/3/4) ----

export const getStatus = (i: InstanceConfig, signal?: AbortSignal) =>
  apiRequest<StatusREST>(i, "/status", { signal });

/**
 * Graph-shape snapshot (feature studio-coverage): O(V+E), budgeted and rate-limited
 * server-side — only ever fetched on explicit demand (the Graph shape panel's Compute).
 */
export const getStatistics = (i: InstanceConfig, signal?: AbortSignal) =>
  apiRequest<GraphStatisticsREST>(i, "/statistics", { signal });

export const saveGraph = (i: InstanceConfig, path?: string) =>
  apiRequest<SaveGame>(i, "/save", {
    method: "PUT",
    body: path ? { saveGameLocation: path } : {},
    query: WAIT,
  });

// ---- save games (feature save-games) ----

export const listSaveGames = (i: InstanceConfig, signal?: AbortSignal) =>
  apiRequest<SaveGame[]>(i, "/savegames", { signal });

export const getSaveGame = (i: InstanceConfig, id: string) =>
  apiRequest<SaveGame>(i, `/savegames/${encodeURIComponent(id)}`);

export const loadSaveGame = (i: InstanceConfig, id: string) =>
  apiRequest<SaveGame>(i, `/savegames/${encodeURIComponent(id)}/load`, {
    method: "PUT",
    query: WAIT,
  });

export const deleteSaveGame = (i: InstanceConfig, id: string, deleteFiles: boolean) =>
  apiRequest<void>(i, `/savegames/${encodeURIComponent(id)}`, {
    method: "DELETE",
    query: { deleteFiles },
  });

export const loadGraph = (i: InstanceConfig, path: string, startServices = true) =>
  apiRequest<void>(i, "/load", {
    method: "PUT",
    body: { saveGameLocation: path, startServices },
    query: WAIT,
  });

export const trimGraph = (i: InstanceConfig) =>
  apiRequest<void>(i, "/trim", { method: "HEAD" });

export const tabulaRasa = (i: InstanceConfig) =>
  apiRequest<void>(i, "/tabularasa", { method: "HEAD", query: WAIT });

export const generateSampleGraph = (i: InstanceConfig, nodeCount = 200, edgeCount = 5) =>
  apiRequest<string>(i, "/generate", { query: { nodeCount, edgeCount } });

export const runBenchmark = (i: InstanceConfig, iterations = 1000) =>
  apiRequest<BenchmarkResult>(i, "/benchmark", { query: { iterations } });

// ---- elements (FR-5/6/7) ----

export const getGraph = (i: InstanceConfig, maxElements: number, signal?: AbortSignal) =>
  apiRequest<GraphREST>(i, "/graph", { query: { maxElements }, signal });

export const getVertex = (i: InstanceConfig, id: number, signal?: AbortSignal) =>
  apiRequest<VertexREST>(i, `/vertex/${id}`, { signal });

export const getEdge = (i: InstanceConfig, id: number, signal?: AbortSignal) =>
  apiRequest<EdgeREST>(i, `/edge/${id}`, { signal });

export const getGraphElement = (i: InstanceConfig, id: number, signal?: AbortSignal) =>
  apiRequest<VertexREST | EdgeREST>(i, `/graphelement/${id}`, { signal });

export const getOutEdgeProperties = (i: InstanceConfig, id: number) =>
  apiRequest<string[]>(i, `/vertex/${id}/edges/out`);

export const getInEdgeProperties = (i: InstanceConfig, id: number) =>
  apiRequest<string[]>(i, `/vertex/${id}/edges/in`);

export const getOutEdges = (i: InstanceConfig, id: number, edgePropertyId: string) =>
  apiRequest<number[]>(i, `/vertex/${id}/edges/out/${encodeURIComponent(edgePropertyId)}`);

export const getInEdges = (i: InstanceConfig, id: number, edgePropertyId: string) =>
  apiRequest<number[]>(i, `/vertex/${id}/edges/in/${encodeURIComponent(edgePropertyId)}`);

export const getInDegree = (i: InstanceConfig, id: number) =>
  apiRequest<number>(i, `/vertex/${id}/edges/indegree`);

export const getOutDegree = (i: InstanceConfig, id: number) =>
  apiRequest<number>(i, `/vertex/${id}/edges/outdegree`);

export const getEdgePropertyDegree = (
  i: InstanceConfig,
  id: number,
  direction: "in" | "out",
  edgePropertyId: string,
) =>
  apiRequest<number>(
    i,
    `/vertex/${id}/edges/${direction}/${encodeURIComponent(edgePropertyId)}/degree`,
  );

// These return the endpoint vertex ID (an int), not the vertex object.
export const getEdgeSource = (i: InstanceConfig, id: number) =>
  apiRequest<number>(i, `/edge/${id}/source`);

export const getEdgeTarget = (i: InstanceConfig, id: number) =>
  apiRequest<number>(i, `/edge/${id}/target`);

// ---- scans (FR-8) ----

export const scanProperty = (i: InstanceConfig, propertyId: string, spec: ScanSpecification) =>
  apiRequest<number[]>(i, `/scan/graph/property/${encodeURIComponent(propertyId)}`, {
    method: "POST",
    body: spec,
  });

export const scanIndex = (i: InstanceConfig, spec: IndexScanSpecification) =>
  apiRequest<number[]>(i, "/scan/index/all", { method: "POST", body: spec });

export const scanIndexRange = (i: InstanceConfig, spec: RangeIndexScanSpecification) =>
  apiRequest<number[]>(i, "/scan/index/range", { method: "POST", body: spec });

export const scanFulltext = (i: InstanceConfig, spec: FulltextIndexScanSpecification) =>
  apiRequest<FulltextSearchResultREST>(i, "/scan/index/fulltext", {
    method: "POST",
    body: spec,
  });

export const scanSpatial = (i: InstanceConfig, spec: SearchDistanceSpecification) =>
  apiRequest<number[]>(i, "/scan/index/spatial", { method: "POST", body: spec });

// ---- index management (FR-10) ----

export const createIndex = (i: InstanceConfig, spec: PluginSpecification) =>
  apiRequest<void>(i, "/index", { method: "POST", body: spec, query: WAIT });

export const addToIndex = (i: InstanceConfig, indexId: string, spec: IndexAddToSpecification) =>
  apiRequest<void>(i, `/index/${encodeURIComponent(indexId)}`, {
    method: "PUT",
    body: spec,
    query: WAIT,
  });

export const removeIndexKey = (
  i: InstanceConfig,
  indexId: string,
  key: PropertySpecification,
) =>
  apiRequest<boolean>(i, `/index/${encodeURIComponent(indexId)}/propertyValue`, {
    method: "DELETE",
    body: key,
  });

export const removeFromIndex = (i: InstanceConfig, indexId: string, graphElementId: number) =>
  apiRequest<void>(i, `/index/${encodeURIComponent(indexId)}/${graphElementId}`, {
    method: "DELETE",
    query: WAIT,
  });

export const deleteIndex = (i: InstanceConfig, indexId: string) =>
  apiRequest<void>(i, `/index/${encodeURIComponent(indexId)}`, {
    method: "DELETE",
    query: WAIT,
  });

// ---- path (FR-12/13/14) ----

export const findPaths = (i: InstanceConfig, from: number, to: number, spec: PathSpecification) =>
  apiRequest<PathREST[]>(i, `/path/${from}/to/${to}`, { method: "POST", body: spec });

// ---- subgraph (FR-15/16/17) ----

/** GET /subgraph returns the registered NAMES; fetch summaries per name. */
export const listSubGraphNames = (i: InstanceConfig, signal?: AbortSignal) =>
  apiRequest<string[]>(i, "/subgraph", { signal });

export const listSubGraphSummaries = async (
  i: InstanceConfig,
  signal?: AbortSignal,
): Promise<SubGraphSummary[]> => {
  const names = (await listSubGraphNames(i, signal)) ?? [];
  const summaries = await Promise.all(
    names.map((name) => getSubGraph(i, name).catch(() => null)),
  );
  return summaries.filter((s): s is SubGraphSummary => s !== null);
};

export const getSubGraph = (i: InstanceConfig, name: string) =>
  apiRequest<SubGraphSummary>(i, `/subgraph/${encodeURIComponent(name)}`);

export const getSubGraphContents = (i: InstanceConfig, name: string) =>
  apiRequest<GraphREST>(i, `/subgraph/${encodeURIComponent(name)}/graph`);

export const createSubGraph = (i: InstanceConfig, spec: SubGraphSpecification) =>
  apiRequest<SubGraphSummary>(i, "/subgraph", { method: "PUT", body: spec });

export const recalculateSubGraph = (i: InstanceConfig, name: string) =>
  apiRequest<SubGraphSummary>(i, `/subgraph/${encodeURIComponent(name)}/recalculate`, {
    method: "POST",
  });

export const deleteSubGraph = (i: InstanceConfig, name: string) =>
  apiRequest<void>(i, `/subgraph/${encodeURIComponent(name)}`, { method: "DELETE" });

// ---- mutations (FR-21) ----

export const createVertex = (i: InstanceConfig, spec: VertexSpecification) =>
  apiRequest<void>(i, "/vertex", { method: "PUT", body: spec, query: WAIT });

export const createEdge = (i: InstanceConfig, spec: EdgeSpecification) =>
  apiRequest<void>(i, "/edge", { method: "PUT", body: spec, query: WAIT });

export const setProperty = (
  i: InstanceConfig,
  id: number,
  propertyId: string,
  spec: PropertySpecification,
) =>
  apiRequest<void>(i, `/graphelement/${id}/${encodeURIComponent(propertyId)}`, {
    method: "PUT",
    body: spec,
    query: WAIT,
  });

export const removeProperty = (i: InstanceConfig, id: number, propertyId: string) =>
  apiRequest<void>(i, `/graphelement/${id}/${encodeURIComponent(propertyId)}`, {
    method: "DELETE",
    query: WAIT,
  });

export const removeGraphElement = (i: InstanceConfig, id: number) =>
  apiRequest<void>(i, `/graphelement/${id}`, { method: "DELETE", query: WAIT });

// ---- delegate validation (gap G-2) ----

export const validateDelegate = (
  i: InstanceConfig,
  delegateKind: DelegateKind,
  fragment: string,
  signal?: AbortSignal,
) =>
  apiRequest<DelegateValidationResult>(i, "/delegates/validate", {
    method: "POST",
    body: { delegateKind, fragment },
    signal,
  });
