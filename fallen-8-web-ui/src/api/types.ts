/**
 * REST DTO shapes, mirroring features/done/web-ui/openapi-v0.1.json (camelCase on the wire).
 * The contract test (tests/api-contract.test.ts) asserts every route used by the client
 * exists in that OpenAPI snapshot with the method we call it with, so drift between these
 * hand-maintained shapes and the server surfaces loudly. Regenerate machine types with
 * `npm run gen:api` when the snapshot changes.
 */

/** Save-game registry (feature save-games). */
export interface SaveGameIndex {
  indexId: string;
  pluginType: string | null;
}

export interface SaveGameKpis {
  vertexCount: number;
  edgeCount: number;
  usedMemoryBytes: number;
  indices: SaveGameIndex[];
  availableIndexPlugins: string[];
  availablePathPlugins: string[];
  availableServicePlugins: string[];
  subGraphs: string[];
}

export interface SaveGame {
  id: string;
  savedAt: string;
  trigger: "api" | "shutdown" | "imported";
  location: string;
  fileCount: number;
  totalBytes: number;
  engineVersion: string | null;
  kpis: SaveGameKpis;
}

/** POST /bulk/import success summary (feature bulk-import-export). */
export interface BulkImportResultREST {
  verticesCreated: number;
  edgesCreated: number;
  linesRead: number;
}

/** GET /benchmark - structured edge-traversal statistics (TPS = traversals/second). */
export interface BenchmarkResult {
  iterations: number;
  edgesTraversed: number;
  averageTps: number;
  medianTps: number;
  standardDeviationTps: number;
}

export interface StatusREST {
  vertexCount: number;
  edgeCount: number;
  usedMemory: number;
  availableIndexPlugins: string[];
  availablePathPlugins: string[];
  availableAnalyticsPlugins: string[];
  availableServicePlugins: string[];
  // Auth probe (server contract on StatusREST.ApiKeyRequired). Optional so instances
  // predating the fields keep reading as authorized.
  apiKeyRequired?: boolean;
  authenticated?: boolean;
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

// The REST Edge DTO carries no edge-property id (only PathElementREST does); the
// edge-property grouping is discovered via the /vertex/{id}/edges/{in|out} lists.
export interface EdgeREST extends AGraphElementREST {
  sourceVertex: number;
  targetVertex: number;
  kind?: "edge";
}

/** Input to the canvas merge: an EdgeREST, optionally tagged with the property it came from. */
export type CanvasEdgeInput = EdgeREST & { edgePropertyId?: string | null };

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

export interface IndexAddToSpecification {
  graphElementId: number;
  key: LiteralSpecification;
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
  pathAlgorithmName: "BLS" | "DIJKSTRA";
  maxDepth: number;
  maxResults: number;
  maxPathWeight: number;
  filter?: PathFilterSpecification;
  cost?: PathCostSpecification;
  /** Stored query of kind Path — mutually exclusive with filter/cost (server 400s on mix). */
  storedQuery?: string;
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
  graphElementFilter?: string;
  vertexFilter?: string;
  edgeFilter?: string;
  edgePropertyFilter?: string;
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
}

export interface SubGraphSummary {
  name: string;
  vertexCount: number;
  edgeCount: number;
  algorithmPluginName?: string | null;
  sourceFallen8Id?: string | null;
  canRecalculate?: boolean;
  additionalInformation?: string | null;
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
