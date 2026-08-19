// MIT License
//
// types.ts
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

/**
 * REST DTO shapes, mirroring features/done/web-ui/openapi-v0.1.json (camelCase on the wire).
 * This hand-curated file is the intentional client contract. The contract test
 * (tests/api-contract.test.ts) asserts every route used by the client exists in that OpenAPI
 * snapshot with the method we call it with, so route/method drift between these shapes and the
 * server surfaces loudly.
 */

/** A registered index: id + plugin type (on /status and in save-game KPIs). */
export interface IndexDescription {
  indexId: string;
  pluginType: string | null;
  /**
   * For a vector index BOUND to an element embedding (feature element-embeddings), the
   * embedding name it projects; null for a raw/unbound index and every other family. A
   * bound index maintains itself — explicit vector adds are rejected. Populated only by
   * the live /status inventory, never in save-game KPIs. Optional so older servers parse.
   */
  embeddingName?: string | null;
  /** The declared model-identity string a vector index expects, or null. Diagnostic only. */
  model?: string | null;
  /**
   * The query families this index answers (feature index-workspace) — the derivation
   * contract lives on the server's IndexDescriptionREST. Live /status inventory only,
   * absent on older servers (lib/indexCapabilities.ts holds the client fallback).
   */
  capabilities?: string[] | null;
  /** CountOfKeys() / CountOfValues() snapshots — live /status inventory only. */
  keys?: number | null;
  values?: number | null;
}

/** Save-game registry (feature save-games). */
export interface SaveGameKpis {
  vertexCount: number;
  edgeCount: number;
  usedMemoryBytes: number;
  indices: IndexDescription[];
  availableIndexPlugins: string[];
  availablePathPlugins: string[];
  availableServicePlugins: string[];
  subGraphs: string[];
}

/** One namespace inside a save game (feature graph-namespaces, registry schema v2). */
export interface SaveGameNamespace {
  name: string;
  location: string;
  fileCount: number;
  totalBytes: number;
  kpis: SaveGameKpis;
}

export interface SaveGame {
  id: string;
  savedAt: string;
  trigger: "api" | "shutdown" | "imported";
  /** Null on multi-namespace entries (the per-member locations live in `namespaces`). */
  location: string | null;
  fileCount: number;
  totalBytes: number;
  engineVersion: string | null;
  kpis: SaveGameKpis | null;
  /** Null/absent on pre-namespace (v1) entries, which are default-only saves. */
  namespaces?: SaveGameNamespace[] | null;
}

/**
 * The PATCH /ns/{name} override vocabulary (feature namespace-startup-load): "inherit" CLEARS
 * the override and falls back to the server's configured default. One type because the server
 * parses every one of its tri-state fields through one parser.
 */
export type NamespaceTriState = "enabled" | "disabled" | "inherit";

/** GET /ns — one namespace of the Fallen-8 (feature graph-namespaces). */
export interface NamespaceEntry {
  name: string;
  /**
   * "notLoaded" is a namespace this Fallen-8 catalogs but did not load into the running
   * process (feature namespace-startup-load). It is LISTED anyway: hiding it would reach
   * the "recreate or switch" recover state by absence, and that state's primary action
   * recreates the namespace empty over data that is still on disk.
   */
  state: "ready" | "creating" | "notLoaded";
  /**
   * Null for a "notLoaded" namespace: the server reports no count rather than 0, because a
   * zero reads as "this graph is empty" - which is what drives the first-run walkthrough and
   * the Samples panel's no-wipe fast path. Treat null as UNKNOWN, never as empty.
   */
  vertexCount: number | null;
  /** Null for a "notLoaded" namespace (see `vertexCount`). */
  edgeCount: number | null;
  createdAt: string;
  /**
   * Whether the NEXT boot loads this namespace: true/false when set explicitly, null when it
   * inherits the server's Fallen8:Namespaces:LoadOnStartup default. Independent of `state` -
   * it describes the next process, not this one.
   */
  loadOnStartupEnabled: boolean | null;
}

export interface NamespacesResponse {
  namespaces: NamespaceEntry[];
  maxNamespaces: number;
  /**
   * The instance-wide startup-load default this boot ran with, which is what a namespace set to
   * "inherit" resolves to (feature writable-instance-config). Published UNCOMPOSED, i.e. with
   * `startupLoadMode` NOT folded in, so a client can say "the default is skip AND the mode is
   * overriding it" rather than showing a composed value that makes saving skip look broken. Optional
   * so an instance predating the field still parses.
   */
  loadOnStartupDefault?: boolean;
  /**
   * The startup mode this boot ran with. Both "all" and "defaultOnly" SHORT-CIRCUIT every
   * per-namespace preference, so a namespace showing "skip" can still have been loaded.
   */
  startupLoadMode?: "catalog" | "all" | "defaultOnly";
}

/**
 * POST /ns/{name}/activate (feature namespace-startup-load): the namespace as it stands after
 * the call, plus whether THIS call is what loaded it. `activated: false` is a success, not a
 * conflict - the namespace was already loaded and nothing was restored, and this flag is the
 * only way a caller can tell the two apart. It says nothing about the persisted startup-load
 * policy, which activation deliberately leaves alone (`NamespaceEntry.loadOnStartupEnabled`).
 */
export interface NamespaceActivationREST {
  namespace: NamespaceEntry;
  activated: boolean;
  detail: string | null;
}

/** POST /bulk/import success summary (feature bulk-import-export). */
export interface BulkImportResultREST {
  verticesCreated: number;
  edgesCreated: number;
  linesRead: number;
}

/**
 * GET /ns/{ns}/generate - what a benchmark-graph generation created. Generation is ADDITIVE, so
 * the created counts and the resulting totals are both reported, and `namespace` names the graph
 * that grew (the endpoint has no default-namespace alias).
 */
export interface GraphGenerationResult {
  namespace: string;
  verticesCreated: number;
  edgesCreated: number;
  distribution: "uniform" | "preferential";
  elapsedMilliseconds: number;
  vertexCountAfter: number;
  edgeCountAfter: number;
}

/** GET /ns/{ns}/benchmark - structured edge-traversal statistics (TPS = traversals/second). */
export interface BenchmarkResult {
  iterations: number;
  edgesTraversed: number;
  averageTps: number;
  medianTps: number;
  standardDeviationTps: number;
}

export interface StatusREST {
  /**
   * The addressed namespace's residency (feature namespace-startup-load): "ready", or
   * "notLoaded" when the server catalogs this namespace but did not load it. /status is the
   * ONE namespace-scoped route that still answers in that state; every other one refuses with
   * 503. Optional so instances predating the field still parse.
   */
  namespaceState?: "ready" | "creating" | "notLoaded" | null;
  /**
   * Null when the addressed namespace is "notLoaded" - the server omits every engine-derived
   * field rather than reporting zeros, so null means UNKNOWN and must never be read as empty.
   */
  vertexCount: number | null;
  /** Null when the addressed namespace is "notLoaded" (see `vertexCount`). */
  edgeCount: number | null;
  usedMemory: number;
  // Live index inventory (feature studio-index-discovery). Optional so instances
  // predating the field still parse.
  indices?: IndexDescription[] | null;
  availableIndexPlugins: string[];
  availablePathPlugins: string[];
  availableAnalyticsPlugins: string[];
  availableServicePlugins: string[];
  // Auth probe (server contract on StatusREST.ApiKeyRequired). Optional so instances
  // predating the fields keep reading as authorized.
  apiKeyRequired?: boolean;
  authenticated?: boolean;
  // Embedding provider state on the cheap surface (feature embedding-out-of-box).
  // Optional so instances predating the field still parse.
  embedding?: EmbeddingProviderStatsREST | null;
  // Chat gateway capability state (feature instance-config). Optional so instances
  // predating the field still parse; the GPU field is only set on GET /config.
  chat?: ChatProviderStatsREST | null;
  // Unstructured-ingestion capability state (feature unstructured-ingestion). Optional so
  // instances predating the field still parse; the Documents screen gates on it.
  ingestion?: IngestionStats | null;
  // NLP enrichment state (feature semantic-layer). Enrichment is ADDITIVE, so this being off
  // degrades the entity network without failing an ingest. Optional so instances predating
  // the field still parse.
  nlp?: NlpStats | null;
  // Durability and recovery-integrity state (feature platform-integrity-audit W5). Optional so
  // instances predating the block still parse - and absence is NOT "healthy": the Studio says
  // "not reported" rather than inventing a clean answer.
  durability?: DurabilityREST | null;
}

/**
 * What the engine knows about its own durability, read as one group so the fields always describe
 * a single outcome. The reason it is on the cheap status surface at all: a client that reconciles
 * against the graph and then deletes what nothing asserts any more would otherwise draw that
 * conclusion from truncated history with no way to know. The integrations runtime does exactly
 * that and defers deletion on this block; a human watching a dashboard deserves the same signal.
 */
export interface DurabilityREST {
  walEnabled: boolean;
  /** The log's failure fence has tripped, or an anchored log awaits its paired load: commits are
   * landing in memory but not durably. */
  degraded: boolean;
  recoveryRan: boolean;
  /** The last replay stopped before the end of the log, so the graph is a PREFIX of history. */
  lastRecoveryTruncated: boolean;
  lastRecoveryReplayedEntries: number;
  /** Indexes the last checkpoint could not write: they will be absent after the next load. */
  lastCheckpointDroppedIndices: number;
}

// ---- unstructured ingestion (feature unstructured-ingestion) ----

/** The entity/key-term enrichment sidecar's state. Off means chunks land without entities. */
export interface NlpStats {
  enabled: boolean;
  configured: boolean;
  reachable: boolean;
}

export interface IngestionStats {
  enabled: boolean;
  textFormats: string[];
  binaryFormats: string[];
  docling: { configured: boolean; reachable: boolean };
  limits: {
    maxUploadBytes: number;
    maxPages: number;
    maxChunksPerDocument: number;
    maxChunksPerNamespace: number;
    maxLinksPerChunk: number;
  };
  embeddingName: string;
  vectorIndexId?: string | null;
  fulltextIndexId?: string | null;
}

export interface DocumentSummary {
  documentId: number;
  name: string;
  sourceFormat: string;
  sourceUri?: string;
  status: "processing" | "indexed" | "failed";
  error?: string;
  chunkCount: number;
  pageCount?: number;
  contentHash: string;
  converter: string;
  chunkerConfig?: string;
  embeddingModel?: string;
  embeddingDimension?: number;
  embedded: boolean;
  embeddingModelStale: boolean;
}

export interface DocumentList {
  documents: DocumentSummary[];
  namespaceChunkCount: number;
  chunkCeiling: number;
  currentEmbeddingModel?: string;
}

export interface ChunkSummary {
  chunkId: number;
  order: number;
  kind: string;
  headingPath?: string;
  pageFrom?: number;
  pageTo?: number;
  identifiers?: string[];
  textPreview: string;
}

export interface DocumentDetail {
  summary: DocumentSummary;
  chunks: ChunkSummary[];
}

export interface IngestTextSpecification {
  name: string;
  text: string;
  format?: "markdown" | "plain";
  embed?: boolean;
  properties?: Record<string, string>;
  sourceUri?: string;
  replaceDocumentId?: number;
  link?: { indexIds: string[]; maxLinksPerChunk?: number };
}

export interface DocumentSearchSpecification {
  queryText?: string;
  queryVector?: number[];
  mode?: "fused" | "dense" | "lexical";
  k?: number;
  window?: number;
  groupByDocument?: boolean;
}

export interface ChunkHit {
  chunkId: number;
  documentId?: number;
  score: number;
  order: number;
  text: string;
  headingPath?: string;
  pageFrom?: number;
  pageTo?: number;
  identifiers?: string[];
  window?: { chunkId: number; order: number; text: string }[];
}

export interface DocumentSearchResult {
  modeUsed: "fused" | "dense" | "lexical";
  hits?: ChunkHit[];
  documents?: { document?: DocumentSummary; bestScore: number; chunks: ChunkHit[] }[];
}

// ---- semantic layer (feature semantic-layer) ----

/** One index role in the binding: whether it is required, exists, and is usable. */
export interface DocumentBindingRole {
  role: "vector" | "fulltext" | "entity";
  indexId: string;
  required: boolean;
  exists: boolean;
  ready: boolean;
  detail?: string;
}

/** The semantic layer's index binding: ingestion is refused (428) until `ready`. */
export interface DocumentBinding {
  ready: boolean;
  vector: DocumentBindingRole;
  fulltext: DocumentBindingRole;
  entity: DocumentBindingRole;
}

/** A deduplicated entity the corpus mentions; the id is a graph vertex (a valid seed). */
export interface DocumentEntity {
  id: number;
  text: string;
  type: string;
  mentionCount: number;
}

export interface DocumentEntityList {
  entities: DocumentEntity[];
  total: number;
}

export interface PropertyREST {
  propertyId: string;
  propertyValue: unknown;
  fullQualifiedTypeName?: string;
}

export interface AGraphElementREST {
  id: number;
  creationDate: string;
  modificationDate: string;
  label?: string | null;
  properties?: PropertyREST[] | null;
}

export interface VertexREST extends AGraphElementREST {
  kind?: "vertex";
}

// edgePropertyId is the edge's type - its adjacency group (feature edge-type-vs-label).
// Optional so payloads from servers predating the field still parse; neighborhood.ts then
// reconstructs it from the /vertex/{id}/edges/{in|out} lists.
export interface EdgeREST extends AGraphElementREST {
  sourceVertex: number;
  targetVertex: number;
  edgePropertyId?: string | null;
  kind?: "edge";
}

/**
 * One element as POST /graphelements/get returns it: identity, stamps, label, properties and kind -
 * and deliberately NOT adjacency, which is why this is its own type rather than VertexREST | EdgeREST.
 *
 * A `vertex` projection IS a complete VertexREST (a vertex carries no adjacency in this API), so
 * hydration uses it directly. An `edge` projection is NOT a complete EdgeREST: it has no endpoints,
 * and the canvas cannot draw an edge without them, so hydration re-reads those one by one. Typing
 * them the same would have hidden exactly that difference.
 */
export interface GraphElementProjectionREST extends AGraphElementREST {
  kind: "vertex" | "edge";
}

/**
 * Result of POST /graphelements/get: the elements that exist, plus the ids that do not. notFound is
 * explicit because "gone" and "has no properties" are different conclusions.
 */
export interface GraphElementBatchREST {
  elements: GraphElementProjectionREST[];
  notFound: number[];
}

export interface GraphREST {
  vertices: VertexREST[];
  edges: EdgeREST[];
}

/**
 * GET /statistics — graph-shape snapshot (feature observability; surfaced by feature
 * studio-coverage). When sampled=true, per-name counts and distinct totals are
 * within-the-sample; multiply counts by sampleStride to extrapolate.
 */
export interface NamedCountREST {
  name: string | null;
  count: number;
}

export interface CardinalityStatsREST {
  top: NamedCountREST[] | null;
  distinctTotal: number;
}

export interface DegreeStatsREST {
  min: number;
  max: number;
  mean: number;
  p50: number;
  p90: number;
  p99: number;
}

export interface IndexStatsREST {
  name: string | null;
  type: string | null;
  keys: number;
  values: number;
}

export interface MemoryStatsREST {
  processWorkingSetBytes: number;
  gcHeapBytes: number;
  gcLastHeapSizeBytes: number;
  gcFragmentedBytes: number;
}

/**
 * The active embedding provider (feature embedding-provider), surfaced on /statistics.
 * Reading it never loads a model. null when the server predates the provider feature.
 */
export interface EmbeddingProviderStatsREST {
  enabled: boolean;
  backend: string | null;
  modelName: string | null;
  modelVersion: string | null;
  dimension: number;
  intendedMetric: string | null;
  loaded: boolean;
  // Live model residency (Ollama /api/ps), only set on GET /config: true = warm, false = not
  // loaded right now, null/undefined = unknown (non-Ollama backend or probe failed).
  resident?: boolean | null;
  gpu?: boolean | null;
}

// The chat gateway state (feature instance-config); on /status (capability only) and GET /config
// (with the residency/GPU probe).
export interface ChatProviderStatsREST {
  enabled: boolean;
  backend: string | null;
  model: string | null;
  loaded: boolean;
  // Live model residency + GPU (Ollama /api/ps), only set on GET /config; null/undefined = unknown.
  resident?: boolean | null;
  gpu?: boolean | null;
}

// The observability posture surfaced read-only by GET /config (feature instance-config).
export interface ObservabilityConfigREST {
  otlpEnabled: boolean;
  otlpEndpoint: string | null;
  prometheusEnabled: boolean;
  prometheusRequireApiKey: boolean;
  tracingSamplingRatio: number;
  statisticsElementBudget: number;
  statisticsTopN: number;
}

export interface SemanticConfigREST {
  embedding?: EmbeddingProviderStatsREST | null;
  chat?: ChatProviderStatsREST | null;
}

// How a setting may be written, and when a written value takes effect (feature
// writable-instance-config). Exactly two sources mean a stored value can never win, so the editor
// renders those rows read-only: "environment" and "commandLine".
export type ConfigSettingTier = "live" | "restart" | "notWritable";
export type ConfigSettingApplyMode = "live" | "liveForNewWork" | "restart" | "never";
export type ConfigSettingKind = "bool" | "int" | "double" | "string" | "enum" | "array";
export type ConfigSettingSource =
  | "default"
  | "appSettings"
  | "userSecrets"
  | "environment"
  | "commandLine"
  | "host"
  | "override";

export interface SettingREST {
  key: string;
  kind: ConfigSettingKind;
  tier: ConfigSettingTier;
  applyMode: ConfigSettingApplyMode;
  // Absent for a never-writable key, which carries valueWithheld instead: that response is anonymous
  // on an instance with no API key, so those values are deliberately not published.
  value?: string | null;
  valueWithheld?: boolean;
  source: ConfigSettingSource;
  restartPending: boolean;
  minimum?: number | null;
  maximum?: number | null;
  allowedValues?: string[] | null;
  rule?: string | null;
  reason?: string | null;
}

export interface PendingRestartREST {
  key: string;
  runningValue: string | null;
  pendingValue: string | null;
}

// The instance's configuration view (features instance-config and writable-instance-config): the
// operator-facing aggregate behind the Studio Configuration section. Secrets are never present.
export interface ConfigREST {
  semantic: SemanticConfigREST;
  observability: ObservabilityConfigREST;
  apiKeyRequired: boolean;
  /**
   * Whether this instance accepts PATCH /config at all (both operator acts are in place: an API key
   * AND Fallen8:Security:EnableConfigurationWrite). The panel renders the settings read-only when
   * false, because offering a Save that the server would always refuse is worse than saying why.
   * Optional so an older instance parses; absent means no write route exists, so read-only is right.
   */
  configWriteEnabled?: boolean;
  // Optional on purpose: an older instance answers without them, and the panel reads them
  // defensively rather than collapsing when they are missing.
  settings?: SettingREST[];
  pendingRestart?: PendingRestartREST[];
}

// PATCH /config: a null value CLEARS a stored override and restores the layer below it.
export interface ConfigWriteSpec {
  settings: Record<string, string | null>;
}

export interface ConfigWriteResultREST {
  key: string;
  value: string | null;
  coerced: boolean;
  cleared: boolean;
  applyMode: ConfigSettingApplyMode;
  restartPending: boolean;
  // Present when a live setting could not reach the running process: the value IS stored, so the
  // promise is downgraded to restart rather than the write being reported as failed.
  applyFailure?: string | null;
}

export interface ConfigWriteREST {
  results: ConfigWriteResultREST[];
  pendingRestart: PendingRestartREST[];
}

// POST /chat request/response (feature instance-config): the chat completion proxied through
// the instance. The model is server-owned, so the request carries no model field.
export interface ChatMessageREST {
  role: string;
  content: string;
}

export interface ChatCompletionSpec {
  messages: ChatMessageREST[];
  options?: { temperature?: number };
}

export interface ChatCompletionStatsREST {
  promptTokens?: number | null;
  completionTokens?: number | null;
  durationMs?: number | null;
  tokensPerSecond?: number | null;
}

export interface ChatCompletionResultREST {
  content: string;
  model: string | null;
  stats?: ChatCompletionStatsREST | null;
}

export interface GraphStatisticsREST {
  vertexCount: number;
  edgeCount: number;
  vertexLabels: CardinalityStatsREST;
  edgeLabels: CardinalityStatsREST;
  inDegree: DegreeStatsREST;
  outDegree: DegreeStatsREST;
  totalDegree: DegreeStatsREST;
  propertyKeys: CardinalityStatsREST;
  indices: IndexStatsREST[] | null;
  memory: MemoryStatsREST;
  computedInMs: number;
  sampled: boolean;
  sampleStride: number;
  /** The embedding provider snapshot; null on servers predating the provider feature. */
  embedding?: EmbeddingProviderStatsREST | null;
}

/** Typed literal (FR-9): { value | propertyValue, fullQualifiedTypeName } */
export interface LiteralSpecification {
  value: string;
  fullQualifiedTypeName: string;
}

export interface PropertySpecification {
  propertyId: string;
  propertyValue: string;
  fullQualifiedTypeName: string;
}

/** PUT /vertex and PUT /edge return 202 with no body - the created id is not reported. */
export interface VertexSpecification {
  label?: string;
  creationDate: number;
  properties?: PropertySpecification[];
}

export interface EdgeSpecification {
  creationDate: number;
  sourceVertex: number;
  targetVertex: number;
  edgePropertyId: string;
  label?: string;
  properties?: PropertySpecification[];
}

/**
 * BinaryOperator travels as an INTEGER on the wire (the OpenAPI sample "Equal" is one of
 * the stale doc-comment samples spec §5 warns about); resultType travels as a string.
 */
export const BINARY_OPERATORS = {
  Equals: 0,
  Greater: 1,
  GreaterOrEquals: 2,
  Lower: 3,
  LowerOrEquals: 4,
  NotEquals: 5,
} as const;

export type BinaryOperatorName = keyof typeof BINARY_OPERATORS;

export interface ScanSpecification {
  operator: number;
  literal: LiteralSpecification;
  resultType: "Vertices" | "Edges" | "Both";
}

/**
 * The all-property discovery scan (feature all-property-search): a case-insensitive substring
 * over EVERY property value, with no operator or typed literal - just a term. The plural
 * companion of the singular named-key property scan.
 */
export interface PropertySearchSpecification {
  searchTerm: string;
  label?: string;
  resultType: "Vertices" | "Edges" | "Both";
}

export interface IndexScanSpecification extends ScanSpecification {
  indexId: string;
}

export interface RangeIndexScanSpecification {
  indexId: string;
  leftLimit: LiteralSpecification;
  rightLimit: LiteralSpecification;
  includeLeft: boolean;
  includeRight: boolean;
  resultType: "Vertices" | "Edges" | "Both";
}

export interface FulltextIndexScanSpecification {
  indexId: string;
  requestString: string;
}

export interface SearchDistanceSpecification {
  indexId: string;
  graphElementId: number;
  distance: number;
}

export interface FulltextSearchResultElementREST {
  graphElementId: number;
  highlights: string[];
  score: number;
}

export interface FulltextSearchResultREST {
  maximumScore: number;
  elements: FulltextSearchResultElementREST[];
}

export interface PluginSpecification {
  uniqueId: string;
  pluginType: string;
  pluginOptions?: Record<string, PropertySpecification>;
}

/**
 * Vector index (feature vector-index; surfaced by studio-coverage). Scores are RAW —
 * interpret via metric/higherIsBetter (L2: lower is better), never re-derive client-side.
 */
export interface VectorIndexScanSpecification {
  indexId: string;
  query: number[];
  k: number;
  kind?: "vertex" | "edge" | "any";
  label?: string;
}

export interface VectorScoredElementREST {
  graphElementId: number;
  score: number;
}

export interface VectorSearchResultREST {
  metric: string | null;
  higherIsBetter: boolean;
  results: VectorScoredElementREST[] | null;
}

/** Exactly one mode: explicit vector, or propertyId naming a float[] property. */
export interface VectorIndexAddSpecification {
  graphElementId: number;
  vector?: number[];
  propertyId?: string;
}

/**
 * Element embeddings (feature element-embeddings). A named embedding is durable element
 * state written through the typed /graphelement/{id}/embedding/{name} routes; the element
 * is the source of truth and a bound vector index projects from it. The studio reads a
 * stored embedding straight off the element's (folded) reserved properties, so there is no
 * client GET helper — only the write DTO. (The server GET returns ElementEmbeddingREST.)
 */
export interface EmbeddingWriteSpecification {
  vector: number[];
}

/**
 * Text-in embedding (feature embedding-provider) — capability-gated (403 when the
 * provider is off). name defaults to "default" server-side.
 */
export interface EmbedElementSpecification {
  graphElementId: number;
  text: string;
  name?: string;
}

/** Semantic search: embed a query text once, then kNN against a vector index. */
export interface EmbeddingSearchSpecification {
  indexId: string;
  text: string;
  k: number;
  kind?: "vertex" | "edge" | "any";
  label?: string;
}

/**
 * An index key on the wire: PropertySpecification minus the property id — the server's
 * add/remove-key endpoints read only propertyValue + type (see GraphController.Index).
 */
export interface IndexKeySpecification {
  propertyValue: string;
  fullQualifiedTypeName: string;
}

export interface IndexAddToSpecification {
  graphElementId: number;
  key: IndexKeySpecification;
}

/**
 * The declarative semantic block (feature element-embeddings) on POST /path and
 * PUT /subgraph. Carries the query vector (or queryText, embedded once by the provider —
 * mutually exclusive) plus code-free similarity filter/cost. Pure data: it compiles no C#.
 * minScore filters vertices by similarity; costBySimilarity (path only) weights a DIJKSTRA
 * search by it. See the element-embeddings README.
 */
export interface SemanticTraversalSpecification {
  queryVector?: number[];
  queryText?: string;
  embeddingName?: string;
  metric?: "Cosine" | "DotProduct" | "L2";
  minScore?: number;
  costBySimilarity?: boolean;
}

export interface PathFilterSpecification {
  vertexFilter?: string;
  edgeFilter?: string;
  edgePropertyFilter?: string;
}

export interface PathCostSpecification {
  vertexCost?: string;
  edgeCost?: string;
}

export interface PathSpecification {
  pathAlgorithmName: string;
  maxDepth: number;
  maxResults: number;
  maxPathWeight: number;
  filter?: PathFilterSpecification;
  cost?: PathCostSpecification;
  /** Stored query of kind Path — mutually exclusive with filter/cost (server 400s on mix). */
  storedQuery?: string;
  /** Declarative semantic block (feature element-embeddings); pure data, compiles no C#. */
  semantic?: SemanticTraversalSpecification;
}

export interface PathElementREST {
  sourceVertexId: number;
  targetVertexId: number;
  edgeId: number;
  edgePropertyId?: string | null;
  direction?: string;
  weight: number;
}

export interface PathREST {
  pathElements: PathElementREST[];
  totalWeight: number;
}

export interface PatternSpecification {
  type: "Vertex" | "Edge" | "VariableLengthEdge";
  patternName?: string;
  direction?: "OutgoingEdge" | "IncomingEdge" | "UndirectedEdge";
  minLength?: number;
  maxLength?: number;
  vertexFilter?: string;
  edgeFilter?: string;
  edgePropertyFilter?: string;
  /**
   * Declarative semantic threshold for a Vertex step (feature
   * subgraph-semantic-thresholds) — scores against the request's semantic query. Owns the
   * step's filter slot (400 together with vertexFilter); 400 on edge steps, without a
   * semantic block, and in stored SubGraph templates.
   */
  semanticMinScore?: number;
}

// Nesting (fromSubGraph) is a QUERY parameter on PUT /subgraph, not a body field —
// a body-level fromSubGraph is silently dropped by the server's deserializer.
export interface SubGraphSpecification {
  name: string;
  additionalInformation?: string;
  vertexFilter?: string;
  edgeFilter?: string;
  patterns?: PatternSpecification[];
  /** Stored query of kind SubGraph — mutually exclusive with filters/patterns (server 400s on mix). */
  storedQuery?: string;
  /**
   * Declarative semantic block (feature element-embeddings), bound at REGISTRATION;
   * minScore becomes the vertex pre-filter. costBySimilarity is path-only (400 here); not
   * available on a stored-template invocation (400).
   */
  semantic?: SemanticTraversalSpecification;
}

/**
 * The bound semantic state echoed on a registered subgraph's summary (feature
 * subgraph-semantic-thresholds) — never the raw vector, only its dimension.
 */
export interface SubGraphSemanticSummary {
  embeddingName: string;
  metric: string;
  dimension: number;
  /** The registration queryText, when one was used (the bound vector stays the truth). */
  queryText?: string | null;
  /** The top-level vertex pre-filter threshold, when set. */
  minScore?: number | null;
  /** Vertex pattern steps carrying a threshold (pattern = patternName or step index). */
  patternThresholds?: { pattern: string; minScore: number }[] | null;
}

export interface SubGraphSummary {
  name: string;
  vertexCount: number;
  edgeCount: number;
  algorithmPluginName?: string | null;
  sourceFallen8Id?: string | null;
  canRecalculate?: boolean;
  additionalInformation?: string | null;
  /** Present only for semantic subgraphs. */
  semantic?: SubGraphSemanticSummary | null;
}

/**
 * Stored query library (feature stored-query-library; surfaced by studio-coverage).
 * Blocks hold exactly the per-template parts; numeric bounds and instance names stay
 * per-request. Entries are immutable: delete + re-register is the edit flow.
 */
export type StoredQueryKind = "Path" | "SubGraph";

export interface StoredPathQueryBlock {
  filter?: PathFilterSpecification;
  cost?: PathCostSpecification;
}

export interface StoredSubGraphQueryBlock {
  vertexFilter?: string;
  edgeFilter?: string;
  patterns?: PatternSpecification[];
}

export interface StoredQuerySpecification {
  name: string;
  kind: StoredQueryKind;
  description?: string;
  path?: StoredPathQueryBlock;
  subGraph?: StoredSubGraphQueryBlock;
}

/** compileState: Compiled (invocable) | Failed (invoking 409s) | SourceOnly. */
export interface StoredQuerySummaryREST {
  name: string | null;
  kind: string | null;
  description: string | null;
  createdAt: string;
  compileState: string | null;
}

export interface StoredQueryDetailREST extends StoredQuerySummaryREST {
  specificationJson: string | null;
  compileDiagnostics: string | null;
}

/**
 * Plugin registration (feature plugin-registration) — the whole-TYPE sibling of the
 * stored-query library: C# source authored in the browser, compile-validated and
 * registered per namespace, then invoked by name. An `algorithm` implements a contract
 * (Path/SubGraph/Analytics) and runs transparently through the existing path/subgraph/
 * analytics endpoints; a `function` implements IGraphFunction and is invoked here. Entries
 * are immutable: delete + re-register is the edit flow. Registration + validate require the
 * dynamic-plugin gate (server 403 when disabled).
 */
export type PluginAuthoringCategory = "algorithm" | "function";
export type AlgorithmContract = "Path" | "SubGraph" | "Analytics";

/** POST /plugins/algorithm body. */
export interface AlgorithmPluginRegistration {
  name: string;
  contract: AlgorithmContract;
  description?: string;
  sourceCode: string;
}

/** POST /plugins/function body (the function category has one contract — no discriminator). */
export interface FunctionPluginRegistration {
  name: string;
  description?: string;
  sourceCode: string;
}

/**
 * POST /plugins/{algorithm,function}/validate body: shares the registration fields, minus
 * side effects. `contract` is read only on the algorithm endpoint (ignored for function).
 */
export interface PluginValidationSpecification {
  name: string;
  contract?: AlgorithmContract;
  sourceCode: string;
}

/** POST /plugins/{algorithm,function}/validate result — side-effect-free compile check. */
export interface PluginValidationResult {
  valid: boolean;
  error: string | null;
}

/** POST /plugins/function/{name}/invoke body — string-valued parameter bag in v1. */
export interface GraphFunctionInvocation {
  parameters?: Record<string, string>;
}

/**
 * POST /plugins/function/{name}/invoke result — a view of existing elements, projected with
 * the SAME Vertex/Edge DTOs as GET /vertex/{id} / GET /edge/{id}.
 */
export interface GraphFunctionResultREST {
  vertices: VertexREST[];
  edges: EdgeREST[];
}

/**
 * category: "Algorithm" | "Function"; contract: "Path" | "SubGraph" | "Analytics" |
 * "GraphFunction". compileState: Compiled (invocable) | Failed (invoking 409s) | SourceOnly.
 */
export interface PluginSummaryREST {
  name: string | null;
  category: string | null;
  contract: string | null;
  description: string | null;
  createdAt: string;
  compileState: string | null;
}

export interface PluginDetailREST extends PluginSummaryREST {
  sourceCode: string | null;
  compileDiagnostics: string | null;
}

/**
 * Graph analytics (feature graph-analytics; surfaced by studio-coverage). Runs are
 * synchronous one-shots with budgets — there is no job store, and the UI must not
 * fabricate one. Top-K/partition rows are the response; full results travel via
 * write-back (snapshot-durable only).
 */
export interface AnalyticsSpecification {
  vertexLabel?: string;
  edgePropertyId?: string;
  direction?: "in" | "out" | "both";
  maxIterations?: number;
  epsilon?: number;
  timeBudgetSeconds?: number;
  parameters?: Record<string, number>;
  maxResults?: number;
  offset?: number;
  writeBack?: boolean;
  writeBackPropertyKey?: string;
}

export interface ScoredVertexREST {
  graphElementId: number;
  score: number;
}

export interface PartitionSummaryREST {
  partitionId: number;
  size: number;
}

export interface WriteBackResultREST {
  propertyKey: string | null;
  verticesWritten: number;
  chunks: number;
}

export interface AnalyticsResultREST {
  algorithm: string | null;
  converged: boolean;
  iterationsRun: number;
  elapsedMs: number;
  budgetExhausted: boolean;
  vertexCount: number;
  statistics: Record<string, number> | null;
  results: ScoredVertexREST[] | null;
  partitions: PartitionSummaryREST[] | null;
  writeBack: WriteBackResultREST | null;
}

/** One partition's membership page — re-runs the specification (exact only when quiescent). */
export interface PartitionMembersREST {
  partitionId: number;
  size: number;
  offset: number;
  members: number[] | null;
}

/** POST /delegates/validate (gap G-2, added by this feature) */
export type DelegateKind =
  | "VertexFilter"
  | "EdgeFilter"
  | "EdgePropertyFilter"
  | "VertexCost"
  | "EdgeCost"
  | "GraphElementFilter";

export interface DelegateDiagnostic {
  line: number;
  column: number;
  endLine: number;
  endColumn: number;
  id: string;
  message: string;
  severity: "error" | "warning" | "info";
}

export interface DelegateValidationResult {
  valid: boolean;
  diagnostics: DelegateDiagnostic[];
}

// ---- integrations (feature integrations) ----
// The runtime is a separate deployable and the API proxies four routes to it, forwarding bodies
// untouched, so these shapes are the RUNTIME'S contract rather than the API app's. Every field is
// what the runtime serialises; newer ones stay optional so an instance predating them still parses.

/** What kind of value a provider setting takes, which is all a form needs to render it. */
export type SettingKind = "Text" | "Number" | "Boolean" | "Url" | "Credential";

/** One setting, as data. `help` says where to find the value in the source system. */
export interface IntegrationSetting {
  key: string;
  label: string;
  kind: SettingKind;
  required: boolean;
  help: string;
  defaultValue?: string | null;
}

/**
 * What an integration IS, as data that is true before any run exists. A form is rendered from
 * `kind`, `required` and `help` alone, which is what makes "a fourth integration needs no Studio
 * change" true rather than aspirational.
 */
export interface IntegrationProvider {
  id: string;
  displayName: string;
  description: string;
  settings: IntegrationSetting[];
  entityKinds: string[];
  claimTypes: string[];
  relationTypes: string[];
  canObserveCompleteState: boolean;
  readOnly: boolean;
  entitySummaryTemplate?: string | null;
}

/** Something a run needs a reader to know, with a stable code to grep for and alert on. */
export interface IntegrationDiagnostic {
  code: string;
  message: string;
  subject?: string | null;
}

/**
 * The whole configuration of one run. A credential setting's value arrives in `credentialValues` and
 * never in `settings`: the runtime leases and redacts the first and treats the second as ordinary data.
 */
export interface IntegrationJobRequest {
  providerId: string;
  integrationInstanceId: string;
  namespace?: string;
  settings: Record<string, string>;
  /** Secrets. Held for the run and dropped; never persisted here, never echoed back. */
  credentialValues: Record<string, string>;
  embedSummaries?: boolean;
}

/** The only account of a job, because the runtime keeps none. */
export interface IntegrationJobReport {
  providerId: string;
  integrationInstanceId: string;
  startedUtc: string;
  durationMilliseconds: number;
  elementsCreated: number;
  elementsMatched: number;
  edgesCreated: number;
  claimsWithdrawn: number;
  elementsDeleted: number;
  deletionsDeferred: number;
  issuedMutations: boolean;
  summariesEmbedded?: number;
  error?: string | null;
  errorKind?: string | null;
  credentialFingerprint?: string | null;
  diagnostics: IntegrationDiagnostic[];
}
