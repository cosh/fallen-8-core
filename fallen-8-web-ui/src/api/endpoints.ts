// MIT License
//
// endpoints.ts
//
// Copyright (c) 2011-2026 Henning Rauch
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

import {
  apiForm,
  apiRequest,
  apiUpload,
  buildUrl,
  resolveAuthHeaders,
  scopedPath,
  throwIfNotOk,
} from "./client";
import type { UploadProgress } from "./client";
import type { InstanceConfig } from "../instances/types";
import type {
  AnalyticsResultREST,
  DocumentBinding,
  DocumentDetail,
  DocumentEntityList,
  DocumentList,
  DocumentSearchResult,
  DocumentSearchSpecification,
  DocumentSummary,
  FileLimits,
  IngestTextSpecification,
  IntegrationJobRequest,
  IntegrationRunAccepted,
  IntegrationRunState,
  IntegrationProvider,
  NamespaceActivationREST,
  NamespaceEntry,
  NamespacesResponse,
  NamespaceTriState,
  AnalyticsSpecification,
  BenchmarkResult,
  BulkImportResultREST,
  GraphGenerationResult,
  AlgorithmPluginRegistration,
  DelegateKind,
  DelegateValidationResult,
  EdgeREST,
  EmbedElementSpecification,
  FunctionPluginRegistration,
  GraphFunctionInvocation,
  GraphFunctionResultREST,
  PluginAuthoringCategory,
  PluginDetailREST,
  PluginSummaryREST,
  PluginValidationResult,
  PluginValidationSpecification,
  ConfigREST,
  ConfigWriteREST,
  ConfigWriteSpec,
  ChatCompletionSpec,
  ChatCompletionResultREST,
  ChatModelsREST,
  EmbeddingSearchSpecification,
  EmbeddingWriteSpecification,
  SaveGame,
  EdgeSpecification,
  FulltextIndexScanSpecification,
  FulltextSearchResultREST,
  GraphREST,
  GraphStatisticsREST,
  IndexAddToSpecification,
  IndexKeySpecification,
  IndexScanSpecification,
  PartitionMembersREST,
  PathREST,
  PathSpecification,
  PluginSpecification,
  PropertySearchSpecification,
  PropertySpecification,
  RangeIndexScanSpecification,
  ScanSpecification,
  SearchDistanceSpecification,
  GraphElementBatchREST,
  StatusREST,
  StoredQueryDetailREST,
  StoredQuerySpecification,
  StoredQuerySummaryREST,
  SubGraphSpecification,
  SubGraphSummary,
  VectorIndexAddSpecification,
  VectorIndexScanSpecification,
  VectorSearchResultREST,
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

/**
 * The reachability probe, and the ONE call with a deadline (see `RequestOptions.timeoutMs`).
 *
 * `/status` answers in milliseconds or the instance is not usable, so a probe still pending after
 * ten seconds has told us what we needed to know. Without the deadline a hung connection leaves the
 * promise pending for ever, and every screen that renders "checking..." until this succeeds says
 * "checking..." for ever - which is worse than an error, because an error names an address.
 */
export const getStatus = (i: InstanceConfig, signal?: AbortSignal) =>
  apiRequest<StatusREST>(i, "/status", { signal, timeoutMs: 10_000 });

/**
 * Instance configuration (feature instance-config): the read-only operator view of the
 * semantic providers + observability posture. Fallen-8-level (bare /config, never per
 * namespace) and API-key gated like /statistics; secrets are redacted server-side.
 */
export const getConfig = (i: InstanceConfig, signal?: AbortSignal) =>
  apiRequest<ConfigREST>(i, "/config", { signal, scope: "fallen8" });

/**
 * Writes instance configuration (feature writable-instance-config). A null value CLEARS a stored
 * override and restores whatever layer sits below it, which is the undo this surface ships instead of
 * history. Every key is validated before any is stored, so a batch applies whole or changes nothing:
 * a 400 names the key and why, and a 409 means either the environment declares that key (so a stored
 * value could never win) or the instance has nowhere to persist.
 *
 * Needs TWO operator acts server-side, an API key AND Fallen8:Security:EnableConfigurationWrite, or
 * it is refused (401 for an unauthenticated caller, 403 otherwise); ConfigREST.configWriteEnabled is
 * how the panel knows not to offer the write at all.
 */
export const writeConfig = (i: InstanceConfig, spec: ConfigWriteSpec, signal?: AbortSignal) =>
  apiRequest<ConfigWriteREST>(i, "/config", {
    method: "PATCH",
    body: spec,
    signal,
    scope: "fallen8",
  });

/**
 * Chat completion proxied through the instance (feature instance-config): browser -> F8 ->
 * the model backend. Fallen-8-level; the model is server-owned so the request carries none.
 * This is the DEFAULT NL-assist transport (custom endpoints stay browser-direct, off this path).
 */
export const postChat = (i: InstanceConfig, spec: ChatCompletionSpec, signal?: AbortSignal) =>
  apiRequest<ChatCompletionResultREST>(i, "/chat", {
    method: "POST",
    body: spec,
    scope: "fallen8",
    signal,
  });

/**
 * What the RUNNING chat backend catalogues (feature chat-model-catalog): one bounded read that fans
 * out to the backend's own model list. Fallen-8-level and gated exactly like POST /chat (403 when the
 * chat capability is off), so Studio only calls it where the answer is actionable.
 *
 * The answer is not the whole resolvable set: a backend can serve a name it does not catalogue, which
 * is why the picker fed from this stays free-text.
 */
export const getChatModels = (i: InstanceConfig, signal?: AbortSignal) =>
  apiRequest<ChatModelsREST>(i, "/chat/models", { signal, scope: "fallen8" });

/** Authorized iff the instance needs no key or accepted ours (server contract on StatusREST.ApiKeyRequired). */
export const isAuthorized = (s: StatusREST): boolean => !s.apiKeyRequired || s.authenticated === true;

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

/** Saves EVERY namespace into one spanning save-game entry (Fallen-8-level). */
export const saveAllNamespaces = (i: InstanceConfig) =>
  apiRequest<SaveGame>(i, "/save/all", { method: "PUT", scope: "fallen8" });

/** Factory reset: drops every non-default namespace and erases "default" (Fallen-8-level). */
export const tabulaRasaAll = (i: InstanceConfig) =>
  apiRequest<void>(i, "/tabularasa/all", { method: "HEAD", scope: "fallen8" });

// ---- namespaces (feature graph-namespaces) ----
// Management routes are Fallen-8-level: they exist once, never under /ns/{ns}.

export const listNamespaces = (i: InstanceConfig, signal?: AbortSignal) =>
  apiRequest<NamespacesResponse>(i, "/ns", { signal, scope: "fallen8" });

export const createNamespace = (i: InstanceConfig, name: string) =>
  apiRequest<NamespaceEntry>(i, `/ns/${encodeURIComponent(name)}`, {
    method: "PUT",
    scope: "fallen8",
  });

export const renameNamespace = (i: InstanceConfig, name: string, newName: string) =>
  apiRequest<NamespaceEntry>(i, `/ns/${encodeURIComponent(name)}`, {
    method: "PATCH",
    body: { name: newName },
    scope: "fallen8",
  });

/**
 * Sets whether the NEXT boot loads this namespace (feature namespace-startup-load);
 * "inherit" clears the override and falls back to the server's configured default. It never
 * loads or unloads the running process's engine, so nothing observable changes until a
 * restart. Its own function rather than a parameter on renameNamespace, because the server
 * applies the whole PATCH body atomically: a shared caller would have to send a name it was
 * not asked to change, and a stale one would rename the namespace as a side effect of a
 * policy edit. The reserved "default" namespace refuses this field with 409.
 */
export const setNamespaceLoadOnStartup = (
  i: InstanceConfig,
  name: string,
  loadOnStartup: NamespaceTriState,
) =>
  apiRequest<NamespaceEntry>(i, `/ns/${encodeURIComponent(name)}`, {
    method: "PATCH",
    body: { loadOnStartup },
    scope: "fallen8",
  });

/**
 * Loads a cataloged-but-not-loaded namespace into the RUNNING process (feature
 * namespace-startup-load): the server constructs its engine, restores its newest registered save
 * game and replays the write-ahead-log tail on top before it serves anything, so a failed restore
 * leaves it exactly as not-loaded as it was. Idempotent - activating a loaded namespace answers
 * 200 with `activated: false`. It does NOT change the persisted policy, which is what the next
 * boot honours: that is `setNamespaceLoadOnStartup`, and the two are separate on purpose.
 */
export const activateNamespace = (i: InstanceConfig, name: string) =>
  apiRequest<NamespaceActivationREST>(i, `/ns/${encodeURIComponent(name)}/activate`, {
    method: "POST",
    scope: "fallen8",
  });

export const dropNamespace = (i: InstanceConfig, name: string) =>
  apiRequest<void>(i, `/ns/${encodeURIComponent(name)}`, {
    method: "DELETE",
    scope: "fallen8",
  });

// ---- save games (feature save-games; Fallen-8-level - entries can span namespaces) ----

export const listSaveGames = (i: InstanceConfig, signal?: AbortSignal) =>
  apiRequest<SaveGame[]>(i, "/savegames", { signal, scope: "fallen8" });

export const getSaveGame = (i: InstanceConfig, id: string) =>
  apiRequest<SaveGame>(i, `/savegames/${encodeURIComponent(id)}`, { scope: "fallen8" });

/** Restores the entry's namespaces — or exactly one of them via `namespaceName`. */
export const loadSaveGame = (i: InstanceConfig, id: string, namespaceName?: string) =>
  apiRequest<SaveGame>(i, `/savegames/${encodeURIComponent(id)}/load`, {
    method: "PUT",
    query: { ...WAIT, namespace: namespaceName },
    scope: "fallen8",
  });

export const deleteSaveGame = (i: InstanceConfig, id: string, deleteFiles: boolean) =>
  apiRequest<void>(i, `/savegames/${encodeURIComponent(id)}`, {
    method: "DELETE",
    query: { deleteFiles },
    scope: "fallen8",
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

/**
 * Generates a random benchmark graph server-side (edgeCount = out-edges PER VERTEX) INTO THE
 * ACTIVE NAMESPACE. Namespace-scoped like every other write: the server refuses the bare form
 * rather than generating into "default", so the /ns/{ns} prefix is not optional here.
 */
export const generateGraph = (
  i: InstanceConfig,
  nodeCount = 200,
  edgeCount = 5,
  distribution: "uniform" | "preferential" = "uniform",
) =>
  apiRequest<GraphGenerationResult>(i, "/generate", {
    query: { nodeCount, edgeCount, distribution },
  });

/** Runs the edge-traversal benchmark over the ACTIVE namespace's graph. */
export const runBenchmark = (i: InstanceConfig, iterations = 1000) =>
  apiRequest<BenchmarkResult>(i, "/benchmark", { query: { iterations } });

// ---- bulk interchange (concept spec §7) ----
// Raw fetch, not apiRequest: the payload is application/x-ndjson, not JSON.

/** Streams the graph (or a filtered subset) to a Blob for a browser download. */
export async function exportBulk(
  i: InstanceConfig,
  filters?: { vertexLabel?: string; edgeLabel?: string; edgePropertyId?: string },
  signal?: AbortSignal,
): Promise<Blob> {
  const url = buildUrl(i.baseUrl, scopedPath(i, "/bulk/export"), {
    vertexLabel: filters?.vertexLabel,
    edgeLabel: filters?.edgeLabel,
    edgePropertyId: filters?.edgePropertyId,
  });
  const response = await fetch(url, { headers: await resolveAuthHeaders(i), signal });
  await throwIfNotOk(response, url);
  return await response.blob();
}

/** Imports into an EMPTY graph (server 409s otherwise); fail-fast with a line number. */
export async function importBulk(
  i: InstanceConfig,
  file: Blob,
): Promise<BulkImportResultREST | null> {
  const url = buildUrl(i.baseUrl, scopedPath(i, "/bulk/import"), undefined);
  const response = await fetch(url, {
    method: "POST",
    headers: { ...(await resolveAuthHeaders(i)), "Content-Type": "application/x-ndjson" },
    body: file,
  });
  await throwIfNotOk(response, url);
  const text = await response.text();
  return text && text !== "null" ? (JSON.parse(text) as BulkImportResultREST) : null;
}

// ---- elements (FR-5/6/7) ----

export const getGraph = (i: InstanceConfig, maxElements: number, signal?: AbortSignal) =>
  apiRequest<GraphREST>(i, "/graph", { query: { maxElements }, signal });

export const getVertex = (i: InstanceConfig, id: number, signal?: AbortSignal) =>
  apiRequest<VertexREST>(i, `/vertex/${id}`, { signal });

export const getEdge = (i: InstanceConfig, id: number, signal?: AbortSignal) =>
  apiRequest<EdgeREST>(i, `/edge/${id}`, { signal });

export const getGraphElement = (i: InstanceConfig, id: number, signal?: AbortSignal) =>
  apiRequest<VertexREST | EdgeREST>(i, `/graphelement/${id}`, { signal });

/**
 * A whole page of elements in ONE request. What comes back is a complete VertexREST for a vertex;
 * an edge lacks its endpoints, because the route omits adjacency by design - see hydrateElements,
 * which re-reads those singly.
 */
export const getGraphElements = (i: InstanceConfig, ids: number[], signal?: AbortSignal) =>
  apiRequest<GraphElementBatchREST>(i, "/graphelements/get", {
    method: "POST",
    // The raw array: apiRequest serializes the body itself, and the server's [FromBody] List<Int32>
    // rejects a pre-serialized string with a 400.
    body: ids,
    signal,
  });

// These four take a signal for the same reason the degree pair below does: a neighborhood fetch
// fans out over them, and a cancelled expand sweep has to stop ISSUING requests (one vertex can
// cost hundreds), not merely stop reading their answers.
export const getOutEdgeProperties = (i: InstanceConfig, id: number, signal?: AbortSignal) =>
  apiRequest<string[]>(i, `/vertex/${id}/edges/out`, { signal });

export const getInEdgeProperties = (i: InstanceConfig, id: number, signal?: AbortSignal) =>
  apiRequest<string[]>(i, `/vertex/${id}/edges/in`, { signal });

export const getOutEdges = (
  i: InstanceConfig,
  id: number,
  edgePropertyId: string,
  signal?: AbortSignal,
) =>
  apiRequest<number[]>(i, `/vertex/${id}/edges/out/${encodeURIComponent(edgePropertyId)}`, {
    signal,
  });

export const getInEdges = (
  i: InstanceConfig,
  id: number,
  edgePropertyId: string,
  signal?: AbortSignal,
) =>
  apiRequest<number[]>(i, `/vertex/${id}/edges/in/${encodeURIComponent(edgePropertyId)}`, {
    signal,
  });

// The signal is what makes a batched degree sweep cancellable (feature canvas-interact): a
// cancelled sweep must stop issuing requests, not just stop reading them.
export const getInDegree = (i: InstanceConfig, id: number, signal?: AbortSignal) =>
  apiRequest<number>(i, `/vertex/${id}/edges/indegree`, { signal });

export const getOutDegree = (i: InstanceConfig, id: number, signal?: AbortSignal) =>
  apiRequest<number>(i, `/vertex/${id}/edges/outdegree`, { signal });

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

// All-property discovery scan: a case-insensitive contains across every property value.
export const scanProperties = (i: InstanceConfig, spec: PropertySearchSpecification) =>
  apiRequest<number[]>(i, "/scan/graph/properties", { method: "POST", body: spec });

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

export const scanVector = (i: InstanceConfig, spec: VectorIndexScanSpecification) =>
  apiRequest<VectorSearchResultREST>(i, "/scan/index/vector", {
    method: "POST",
    body: spec,
  });

/** Single-element add/replace; bulk embedding ingestion is deliberately curl territory. */
export const addVectorToIndex = (
  i: InstanceConfig,
  indexId: string,
  spec: VectorIndexAddSpecification,
) =>
  apiRequest<boolean>(i, `/index/vector/${encodeURIComponent(indexId)}`, {
    method: "PUT",
    body: spec,
  });

// ---- element embeddings (feature element-embeddings) ----
// The element is the source of truth for its named embedding; a bound vector index
// projects from it. Like every mutation these send waitForCompletion=true (FR-21) so the
// write has committed before we re-read the element, and a rolled-back write surfaces as a
// 4xx/5xx instead of a fire-and-forget 202.

export const putElementEmbedding = (
  i: InstanceConfig,
  id: number,
  name: string,
  spec: EmbeddingWriteSpecification,
) =>
  apiRequest<void>(i, `/graphelement/${id}/embedding/${encodeURIComponent(name)}`, {
    method: "PUT",
    body: spec,
    query: WAIT,
  });

export const deleteElementEmbedding = (i: InstanceConfig, id: number, name: string) =>
  apiRequest<void>(i, `/graphelement/${id}/embedding/${encodeURIComponent(name)}`, {
    method: "DELETE",
    query: WAIT,
  });

// ---- embedding provider (feature embedding-provider) ----
// Capability-gated (403 when Fallen8:Embedding:Enabled is off). Text is embedded once,
// server-side; the vector never touches the browser on these paths.

/** Embed text and store it as the element's named embedding (+ model stamp), one txn. */
export const embedElement = (i: InstanceConfig, spec: EmbedElementSpecification) =>
  apiRequest<boolean>(i, "/embedding/element", { method: "POST", body: spec });

/** Semantic search: embed the query text once, then kNN — same result shape as scanVector. */
export const embeddingSearch = (
  i: InstanceConfig,
  spec: EmbeddingSearchSpecification,
  signal?: AbortSignal,
) =>
  apiRequest<VectorSearchResultREST>(i, "/embedding/search", {
    method: "POST",
    body: spec,
    signal,
  });

// ---- index lifecycle + content (FR-10, surfaced on the Indexes screen) ----

// Answers the server's boolean: false means "not created" (duplicate id, invalid or
// REST-inexpressible plugin options) — surface it, don't report success.
export const createIndex = (i: InstanceConfig, spec: PluginSpecification) =>
  apiRequest<boolean>(i, "/index", { method: "POST", body: spec, query: WAIT });

// The add/remove-element endpoints answer the server's boolean: false means "index or
// element not found" — surface it, don't report success.
export const addToIndex = (i: InstanceConfig, indexId: string, spec: IndexAddToSpecification) =>
  apiRequest<boolean>(i, `/index/${encodeURIComponent(indexId)}`, {
    method: "PUT",
    body: spec,
    query: WAIT,
  });

export const removeIndexKey = (
  i: InstanceConfig,
  indexId: string,
  key: IndexKeySpecification,
) =>
  apiRequest<boolean>(i, `/index/${encodeURIComponent(indexId)}/propertyValue`, {
    method: "DELETE",
    body: key,
  });

export const removeFromIndex = (i: InstanceConfig, indexId: string, graphElementId: number) =>
  apiRequest<boolean>(i, `/index/${encodeURIComponent(indexId)}/${graphElementId}`, {
    method: "DELETE",
    query: WAIT,
  });

export const deleteIndex = (i: InstanceConfig, indexId: string) =>
  apiRequest<void>(i, `/index/${encodeURIComponent(indexId)}`, {
    method: "DELETE",
    query: WAIT,
  });

// ---- path (FR-12/13/14) ----

export const findPaths = (
  i: InstanceConfig,
  from: number,
  to: number,
  spec: PathSpecification,
  signal?: AbortSignal,
) => apiRequest<PathREST[]>(i, `/path/${from}/to/${to}`, { method: "POST", body: spec, signal });

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

/** `fromSubGraph` (optional nesting) travels as a query parameter per the API contract. */
export const createSubGraph = (
  i: InstanceConfig,
  spec: SubGraphSpecification,
  fromSubGraph?: string,
) =>
  apiRequest<SubGraphSummary>(i, "/subgraph", {
    method: "PUT",
    body: spec,
    query: fromSubGraph ? { fromSubGraph } : undefined,
  });

export const recalculateSubGraph = (i: InstanceConfig, name: string) =>
  apiRequest<SubGraphSummary>(i, `/subgraph/${encodeURIComponent(name)}/recalculate`, {
    method: "POST",
  });

export const deleteSubGraph = (i: InstanceConfig, name: string) =>
  apiRequest<void>(i, `/subgraph/${encodeURIComponent(name)}`, { method: "DELETE" });

// ---- graph analytics (concept spec §3) ----

/** Map of algorithm name → one-line description; the picker IS the discovery surface. */
export const listAnalyticsAlgorithms = (i: InstanceConfig, signal?: AbortSignal) =>
  apiRequest<Record<string, string>>(i, "/analytics/algorithms", { signal });

export const runAnalytics = (
  i: InstanceConfig,
  algorithmName: string,
  spec: AnalyticsSpecification,
) =>
  apiRequest<AnalyticsResultREST>(i, `/analytics/${encodeURIComponent(algorithmName)}`, {
    method: "POST",
    body: spec,
  });

export const getPartitionMembers = (
  i: InstanceConfig,
  algorithmName: string,
  partitionId: number,
  spec: AnalyticsSpecification,
) =>
  apiRequest<PartitionMembersREST>(
    i,
    `/analytics/${encodeURIComponent(algorithmName)}/partition/${partitionId}`,
    { method: "POST", body: spec },
  );

// ---- stored query library (concept spec §5) ----

export const listStoredQueries = (i: InstanceConfig, signal?: AbortSignal) =>
  apiRequest<StoredQuerySummaryREST[]>(i, "/storedquery", { signal });

export const getStoredQuery = (i: InstanceConfig, name: string, signal?: AbortSignal) =>
  apiRequest<StoredQueryDetailREST>(i, `/storedquery/${encodeURIComponent(name)}`, {
    signal,
  });

/** Registration compiles C#, so it needs the API key when one is configured (401 otherwise). */
export const registerStoredQuery = (i: InstanceConfig, spec: StoredQuerySpecification) =>
  apiRequest<StoredQuerySummaryREST>(i, "/storedquery", { method: "POST", body: spec });

export const deleteStoredQuery = (i: InstanceConfig, name: string) =>
  apiRequest<void>(i, `/storedquery/${encodeURIComponent(name)}`, { method: "DELETE" });

// ---- plugin registration (feature plugin-registration) ----
// The whole-type sibling of the stored-query library: register C# source per namespace,
// compile-validate it side-effect-free for the editor, and invoke a graph function by name.
// All plugin routes are namespace-scoped (default scope), unlike the Fallen-8-level
// /delegates/validate. Registration + validate require the dynamic-plugin gate (403 when
// disabled); listing/getting/invoking/deleting carry only the standard auth.

export const listPlugins = (i: InstanceConfig, signal?: AbortSignal) =>
  apiRequest<PluginSummaryREST[]>(i, "/plugins", { signal });

export const getPlugin = (i: InstanceConfig, name: string, signal?: AbortSignal) =>
  apiRequest<PluginDetailREST>(i, `/plugins/${encodeURIComponent(name)}`, { signal });

/** Registration compiles C#, so it needs the API key when one is configured, plus the gate. */
export const registerAlgorithmPlugin = (i: InstanceConfig, spec: AlgorithmPluginRegistration) =>
  apiRequest<PluginSummaryREST>(i, "/plugins/algorithm", { method: "POST", body: spec });

export const registerFunctionPlugin = (i: InstanceConfig, spec: FunctionPluginRegistration) =>
  apiRequest<PluginSummaryREST>(i, "/plugins/function", { method: "POST", body: spec });

export const deletePlugin = (i: InstanceConfig, name: string) =>
  apiRequest<void>(i, `/plugins/${encodeURIComponent(name)}`, { method: "DELETE" });

/** Runs a registered graph function by name; the result references existing elements. */
export const invokeGraphFunction = (
  i: InstanceConfig,
  name: string,
  parameters?: Record<string, string>,
) =>
  apiRequest<GraphFunctionResultREST>(i, `/plugins/function/${encodeURIComponent(name)}/invoke`, {
    method: "POST",
    // Typed against the request-body contract (mirrors the sibling plugin endpoints); compile-time
    // check only, the emitted value is unchanged.
    body: { parameters: parameters ?? {} } satisfies GraphFunctionInvocation,
  });

/**
 * Side-effect-free compile+contract check for the authoring editor. `category` selects the
 * endpoint (`algorithm`/`function`); `contract` inside the spec is read only for algorithm.
 */
export const validatePlugin = (
  i: InstanceConfig,
  category: PluginAuthoringCategory,
  spec: PluginValidationSpecification,
  signal?: AbortSignal,
) =>
  apiRequest<PluginValidationResult>(i, `/plugins/${category}/validate`, {
    method: "POST",
    body: spec,
    signal,
  });

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
    scope: "fallen8",
  });

// ---- unstructured ingestion (feature unstructured-ingestion) ----
// Capability-gated (403 when Fallen8:Ingestion:Enabled is off); the Documents screen gates
// its UI on StatusREST.ingestion. Binary formats convert in the docling sidecar server-side;
// the browser only ever talks to Fallen-8.

export const listDocuments = (i: InstanceConfig, signal?: AbortSignal) =>
  apiRequest<DocumentList>(i, "/document", { signal });

export const getDocument = (i: InstanceConfig, id: number, signal?: AbortSignal) =>
  apiRequest<DocumentDetail>(i, `/document/${id}`, { signal });

export const deleteDocument = (i: InstanceConfig, id: number) =>
  apiRequest<void>(i, `/document/${id}`, { method: "DELETE", query: WAIT });

export const ingestText = (i: InstanceConfig, spec: IngestTextSpecification) =>
  apiRequest<DocumentSummary>(i, "/document/text", { method: "POST", body: spec });

/**
 * Multipart upload; `embed=false` ingests without vectors (provider off). `link` is the
 * multipart twin of the JSON body's link block: it rides as the `linkJson` form field, so a
 * binary upload can request the same exact-match structural linking a text ingest can.
 */
export const ingestFile = (
  i: InstanceConfig,
  file: File,
  options: {
    name?: string;
    embed?: boolean;
    sourceUri?: string;
    link?: { indexIds: string[]; maxLinksPerChunk?: number };
  } = {},
) => {
  const form = new FormData();
  form.append("file", file);
  if (options.name) form.append("name", options.name);
  if (options.embed !== undefined) form.append("embed", String(options.embed));
  if (options.sourceUri) form.append("sourceUri", options.sourceUri);
  if (options.link) form.append("linkJson", JSON.stringify(options.link));
  return apiForm<DocumentSummary>(i, "/document", form);
};

/** Fused (dense + lexical) chunk retrieval; hits are chunk vertex ids. */
export const searchDocuments = (i: InstanceConfig, spec: DocumentSearchSpecification) =>
  apiRequest<DocumentSearchResult>(i, "/document/search", { method: "POST", body: spec });

// ---- semantic layer (feature semantic-layer): explicit index binding + the entity network ----
// The layer creates no index implicitly: ingestion is refused (428) until the binding is ready.
// The Knowledge screen's "State" panel reads the binding and offers the one create path.

/** The index binding state: which indices exist, are the right shape, and whether ingest is ready. */
export const getDocumentBinding = (i: InstanceConfig, signal?: AbortSignal) =>
  apiRequest<DocumentBinding>(i, "/document/binding", { signal });

/** Create the required indices (idempotent). The ONLY path that creates a bound index. */
export const ensureDocumentBinding = (i: InstanceConfig) =>
  apiRequest<DocumentBinding>(i, "/document/binding/ensure", { method: "POST" });

/** The deduplicated Entity network, ranked by mention count; each id is a graph seed. */
export const listEntities = (
  i: InstanceConfig,
  options: { type?: string; contains?: string; limit?: number } = {},
  signal?: AbortSignal,
) =>
  apiRequest<DocumentEntityList>(i, "/document/entities", {
    signal,
    query: { type: options.type, contains: options.contains, limit: options.limit },
  });

// ---- integrations (feature integrations) ----
// Fallen-8-level: one runtime serves the whole instance and a job names the namespace it writes
// into, so these are pinned to their bare form and never namespace-prefixed.

/** The integrations this instance's runtime ships. A 403 or 401 means the capability is off. */
export const listIntegrationProviders = (i: InstanceConfig, signal?: AbortSignal) =>
  apiRequest<IntegrationProvider[]>(i, "/integrations/providers", { signal, scope: "fallen8" });

/**
 * What a job may carry on THIS instance: the ceilings already reconciled with the proxy's own
 * transport bound, so there is one number per question and nothing to combine here. Read before
 * staging so an oversized set is refused in the form rather than after an upload.
 *
 * An instance too old to serve it answers 404, which is why every caller has to treat "unknown"
 * as "check nothing" rather than substituting a guess: a hardcoded ceiling here is how Studio
 * came to carry one BELOW the runtime's and refuse jobs the instance would have accepted.
 */
export const getIntegrationLimits = (i: InstanceConfig, signal?: AbortSignal) =>
  apiRequest<FileLimits>(i, "/integrations/limits", { signal, scope: "fallen8" });

/**
 * Starts a run. Answers a run id, NOT a report: the report is read afterwards from
 * getIntegrationRun, because any real source outlives the connection that would have carried it.
 * Everything that can reject the job still fails this call, so a resolved promise means it started.
 *
 * Sent as a MULTIPART FORM (feature integration-file-transport): the document in a `job` part with
 * its `files` map left out, and each file as its own part streamed straight from the browser's
 * handle. There is deliberately no JSON fallback here, even though the instance still accepts one -
 * keeping it would mean keeping a base64 encoder, and that encoder is what capped a job at about
 * 384 MiB regardless of what the instance would have accepted. Against an apiApp too old to accept
 * multipart the answer is a 415, which says so plainly.
 *
 * `onProgress` is how far the SEND has got, which for a multi-gigabyte set of extracts is the
 * difference between a progress bar and an apparent hang.
 */
export const submitIntegrationJob = (
  i: InstanceConfig,
  job: IntegrationJobRequest,
  options: { signal?: AbortSignal; onProgress?: (progress: UploadProgress) => void } = {},
) =>
  apiUpload<IntegrationRunAccepted>(i, "/integrations/job", integrationJobForm(job), {
    scope: "fallen8",
    signal: options.signal,
    onProgress: options.onProgress,
  });

/**
 * The job as a multipart form. Exported so a test can assert on the parts rather than on a mock's
 * arguments: what matters is the shape that goes on the wire.
 *
 * The `job` part is a STRING and not a Blob. Appending a Blob makes the browser declare a filename
 * on that part, which the runtime refuses by name because a value part carrying a filename is how
 * the whole envelope gets sent as a file.
 *
 * The part naming is the runtime's grammar: `files[<key>]` for a setting given one file,
 * `files[<key>][<n>]` numbered from 0 for one given several. The distinction is load-bearing rather
 * than cosmetic - a list of one is a different statement from one file, and a setting the descriptor
 * does not declare `multiple` refuses the list form.
 */
export function integrationJobForm(job: IntegrationJobRequest): FormData {
  const { files, ...document } = job;
  const form = new FormData();
  form.append("job", JSON.stringify(document));

  for (const [key, supplied] of Object.entries(files ?? {})) {
    if (Array.isArray(supplied)) {
      supplied.forEach((entry, index) => {
        form.append(`files[${key}][${index}]`, entry.file, entry.name);
      });
    } else {
      form.append(`files[${key}]`, supplied.file, supplied.name);
    }
  }

  return form;
}

/**
 * One identity's current or most recent run: the phase while it runs, the report once it ends.
 * Answers 404 when the runtime has no slot for that identity, which a caller should read as "not
 * this process" rather than as an error.
 */
export const getIntegrationRun = (i: InstanceConfig, instanceId: string) =>
  apiRequest<IntegrationRunState>(i, `/integrations/run/${encodeURIComponent(instanceId)}`, {
    scope: "fallen8",
  });

/**
 * Asks the run in flight under one identity to stop. Answers 202 and not 200 because a stop is a
 * REQUEST honoured at the run's next safe point, so the run as answered here is the run as it was
 * when the stop was recorded, and getIntegrationRun is what shows it taking effect. A 404 means
 * nothing is in flight: a run that already ended is not cancellable. Cancelling twice is not an error.
 */
export const cancelIntegrationRun = (i: InstanceConfig, instanceId: string) =>
  apiRequest<IntegrationRunState>(i, `/integrations/run/${encodeURIComponent(instanceId)}/cancel`, {
    method: "POST",
    scope: "fallen8",
  });
