/**
 * fallen8-jsonl emitter (feature sample-graphs): builds interchange files the server's
 * POST /bulk/import consumes. ONE emitter, two consumers — the sample build script
 * (scripts/build-samples.ts) and the GitHub dependency card, which transforms an SBOM
 * in the browser. The format contract (typed {type,value} pairs, strict fields, the
 * version-2 System.Single[] encoding) is owned by the server's JsonlGraphFormat; this
 * module only mirrors it.
 */

export interface JsonlProperty {
  type: string;
  value: string;
}

export interface JsonlVertex {
  id: number;
  label?: string;
  properties?: Record<string, JsonlProperty>;
}

export interface JsonlEdge {
  id: number;
  source: number;
  target: number;
  edgePropertyId: string;
  label?: string;
  properties?: Record<string, JsonlProperty>;
}

/** Fixed creationDate for deterministic dataset builds (2026-01-01T00:00:00Z). */
export const SAMPLE_CREATION_DATE = 1_767_225_600;

const SINGLE_ARRAY_TYPE = "System.Single[]";

/** Typed-pair constructors for the property types the datasets use. */
export const prop = {
  string: (value: string): JsonlProperty => ({ type: "System.String", value }),
  int32: (value: number): JsonlProperty => ({
    type: "System.Int32",
    value: String(Math.trunc(value)),
  }),
  double: (value: number): JsonlProperty => ({ type: "System.Double", value: String(value) }),
  /** An embedding vector, rounded to 5 decimals (~1e-5 cosine error, much smaller files). */
  singleArray: (vector: readonly number[]): JsonlProperty => ({
    type: SINGLE_ARRAY_TYPE,
    value: vector.map((component) => String(Math.round(component * 1e5) / 1e5)).join(","),
  }),
};

/**
 * Serializes a whole graph to an importable jsonl string: meta line (exact counts,
 * lowest-sufficient version — 2 only when a Single[] property is present, mirroring the
 * server's export stamping), then vertices, then edges.
 */
export function buildJsonlGraph(vertices: JsonlVertex[], edges: JsonlEdge[]): string {
  const hasArray = [...vertices, ...edges].some(
    (element) =>
      element.properties &&
      Object.values(element.properties).some((pair) => pair.type === SINGLE_ARRAY_TYPE),
  );

  const lines: string[] = [
    JSON.stringify({
      type: "meta",
      format: "fallen8-jsonl",
      version: hasArray ? 2 : 1,
      vertexCount: vertices.length,
      edgeCount: edges.length,
    }),
  ];

  for (const vertex of vertices) {
    lines.push(
      JSON.stringify({
        type: "vertex",
        id: vertex.id,
        ...(vertex.label !== undefined ? { label: vertex.label } : {}),
        creationDate: SAMPLE_CREATION_DATE,
        ...(vertex.properties ? { properties: vertex.properties } : {}),
      }),
    );
  }

  for (const edge of edges) {
    lines.push(
      JSON.stringify({
        type: "edge",
        id: edge.id,
        edgePropertyId: edge.edgePropertyId,
        source: edge.source,
        target: edge.target,
        ...(edge.label !== undefined ? { label: edge.label } : {}),
        creationDate: SAMPLE_CREATION_DATE,
        ...(edge.properties ? { properties: edge.properties } : {}),
      }),
    );
  }

  return lines.join("\n") + "\n";
}
