/**
 * REST DTO shapes, mirroring features/web-ui/openapi-v0.1.json (camelCase on the wire).
 * The contract test (tests/api-contract.test.ts) asserts every route used by the client
 * exists in that OpenAPI snapshot with the method we call it with, so drift between these
 * hand-maintained shapes and the server surfaces loudly. Regenerate machine types with
 * `npm run gen:api` when the snapshot changes.
 */

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
  freeMemory: number;
  availableIndexPlugins: string[];
  availablePathPlugins: string[];
  availableServicePlugins: string[];
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

export interface SubGraphSpecification {
  name: string;
  additionalInformation?: string;
  fromSubGraph?: string;
  vertexFilter?: string;
  edgeFilter?: string;
  patterns?: PatternSpecification[];
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
