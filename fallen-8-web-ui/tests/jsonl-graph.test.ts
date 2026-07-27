// MIT License
//
// jsonl-graph.test.ts
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

import { describe, expect, it } from "vitest";
import {
  buildJsonlGraph,
  prop,
  SAMPLE_CREATION_DATE,
  type JsonlEdge,
  type JsonlVertex,
} from "../src/lib/jsonlGraph";

/**
 * The emitter mirrors the server's JsonlGraphFormat contract (feature sample-graphs):
 * strict line fields, typed {type,value} string pairs, and the current format version
 * (2) stamped on every file. The server side of the contract is pinned by
 * fallen-8-unittest/BulkImportExportTest.cs.
 */
describe("jsonlGraph emitter", () => {
  const vertex = (id: number, properties?: JsonlVertex["properties"]): JsonlVertex => ({
    id,
    label: "doc",
    properties,
  });
  const edge = (id: number, source: number, target: number): JsonlEdge => ({
    id,
    source,
    target,
    edgePropertyId: "knows",
  });

  it("emits meta + vertices + edges with exact counts and the strict field set", () => {
    const jsonl = buildJsonlGraph(
      [vertex(1, { name: prop.string("Alice") }), vertex(2)],
      [edge(10, 1, 2)],
    );
    const lines = jsonl.trimEnd().split("\n").map((line) => JSON.parse(line));

    expect(lines).toHaveLength(4); // meta + 2 vertices + 1 edge
    expect(lines[0]).toEqual({
      type: "meta",
      format: "fallen8-jsonl",
      version: 2,
      vertexCount: 2,
      edgeCount: 1,
    });
    expect(lines[1]).toEqual({
      type: "vertex",
      id: 1,
      label: "doc",
      creationDate: SAMPLE_CREATION_DATE,
      properties: { name: { type: "System.String", value: "Alice" } },
    });
    expect(lines[3]).toEqual({
      type: "edge",
      id: 10,
      edgePropertyId: "knows",
      source: 1,
      target: 2,
      creationDate: SAMPLE_CREATION_DATE,
    });
    // An absent label must be an ABSENT key (strict fields), not a null/undefined value.
    expect(Object.keys(lines[3])).not.toContain("label");
  });

  it("always stamps version 2 and encodes Single[] embeddings", () => {
    const withVector = buildJsonlGraph(
      [vertex(1, { "$embedding:default": prop.singleArray([0.25, -0.5]) })],
      [],
    );
    expect(JSON.parse(withVector.split("\n")[0]).version).toBe(2);
    expect(withVector).toContain('"$embedding:default":{"type":"System.Single[]","value":"0.25,-0.5"}');

    // No arrays present — still version 2 (standardized, no lowest-sufficient stamping).
    const withoutVector = buildJsonlGraph([vertex(1, { name: prop.string("x") })], []);
    expect(JSON.parse(withoutVector.split("\n")[0]).version).toBe(2);
  });

  it("rounds embedding components to 5 decimals and typed pairs stay strings", () => {
    expect(prop.singleArray([0.123456789, 1]).value).toBe("0.12346,1");
    expect(prop.int32(42.9).value).toBe("42");
    expect(prop.double(0.30000000000000004).value).toBe("0.30000000000000004");
  });
});
