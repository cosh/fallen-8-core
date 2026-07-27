// MIT License
//
// jsonlGraph.ts
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
 * fallen8-jsonl emitter (feature sample-graphs): builds interchange files the server's
 * POST /bulk/import consumes. ONE emitter, two consumers — the sample build script
 * (scripts/build-samples.ts) and the GitHub dependency card, which transforms an SBOM
 * in the browser. The format contract (version 2: typed {type,value} pairs, strict
 * fields, the System.Single[] embedding encoding) is owned by the server's
 * JsonlGraphFormat; this module only mirrors it.
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

/** The current fallen8-jsonl version; always stamped (System.Single[] is always available). */
export const JSONL_FORMAT_VERSION = 2;

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
 * Serializes a whole graph to an importable jsonl string: meta line (exact counts, always
 * the current format version), then vertices, then edges.
 */
export function buildJsonlGraph(vertices: JsonlVertex[], edges: JsonlEdge[]): string {
  const lines: string[] = [
    JSON.stringify({
      type: "meta",
      format: "fallen8-jsonl",
      version: JSONL_FORMAT_VERSION,
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
