// MIT License
//
// find-similar.test.ts
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
import type { EdgeREST, IndexDescription, VertexREST } from "../src/api/types";
import { similarSearchesFor } from "../src/lib/findSimilar";
import { vectorQueryText } from "../src/lib/embeddingProperties";

/**
 * "Find elements like this one" (feature element-similarity-search). The decision this function
 * makes is which INDEX can answer the question, and it is the whole reason the gesture is not just
 * a button: the search surface takes a vector and never an element id, so the query has to be
 * composed from the element's own stored embedding, against the index that actually projects it.
 */

const BOUND: IndexDescription = {
  indexId: "arxml-summary",
  pluginType: "VectorIndex",
  embeddingName: "default",
  capabilities: ["vector"],
};

function vertex(overrides: Partial<VertexREST> = {}): VertexREST {
  return {
    id: 8724,
    creationDate: "2026-01-01T10:00:00",
    modificationDate: "2026-01-01T10:00:00",
    label: "signal",
    properties: [
      { propertyId: "arxml.name", propertyValue: "Odo_ST3" },
      { propertyId: "$embedding:default", propertyValue: "[0.1, 0.2, 0.3]" },
    ],
    ...overrides,
  } as VertexREST;
}

describe("the vector comes off the element itself, whole and not previewed", () => {
  it("carries every component, because a 4-component preview is not a query", () => {
    // previewVector caps at four components for the tables; searching with that would search with a
    // different vector and quietly return the wrong neighbours.
    const long = `[${Array.from({ length: 12 }, (_, i) => i / 10).join(", ")}]`;
    expect(vectorQueryText(long)).toBe(long);
    expect(vectorQueryText([0.5, 0.25])).toBe("[0.5, 0.25]");
  });

  it("is null for a property that is not a vector at all", () => {
    expect(vectorQueryText("Odo_ST3")).toBeNull();
    expect(vectorQueryText(42)).toBeNull();
    expect(vectorQueryText([])).toBeNull();
  });
});

describe("which index can answer 'what is like this'", () => {
  it("uses the index BOUND to the element's embedding name, and inherits the label", () => {
    const found = similarSearchesFor(vertex(), [BOUND], false);

    expect(found).toHaveLength(1);
    expect(found[0].embeddingName).toBe("default");
    expect(found[0].prefill).toEqual({
      indexId: "arxml-summary",
      vectorText: "[0.1, 0.2, 0.3]",
      sourceElementId: 8724,
      label: "signal",
      kind: "vertex",
    });
  });

  it("offers nothing when NO index is bound to that embedding", () => {
    // The vectors exist on the element and nowhere else, so there is no corpus to rank against.
    // Navigating to an index that cannot contain this element would answer 0 hits and read as
    // "nothing is similar".
    expect(similarSearchesFor(vertex(), [], false)).toEqual([]);
  });

  it("ignores a vector index bound to a DIFFERENT embedding name", () => {
    const other: IndexDescription = { ...BOUND, embeddingName: "documents" };
    expect(similarSearchesFor(vertex(), [other], false)).toEqual([]);
  });

  it("ignores an UNBOUND vector index, whose contents are not a projection of anything", () => {
    const unbound: IndexDescription = { indexId: "raw", pluginType: "VectorIndex" };
    expect(similarSearchesFor(vertex(), [unbound], false)).toEqual([]);
  });

  it("ignores a bound index of another family, since the name match is not enough", () => {
    const wrongFamily: IndexDescription = {
      indexId: "dict",
      pluginType: "DictionaryIndex",
      embeddingName: "default",
    };
    expect(similarSearchesFor(vertex(), [wrongFamily], false)).toEqual([]);
  });

  it("inherits NO label from an element that has none, rather than an empty constraint", () => {
    const found = similarSearchesFor(vertex({ label: "" }), [BOUND], false);
    expect(found[0].prefill.label).toBeUndefined();
  });

  it("constrains an edge search to edges", () => {
    const edge = {
      ...vertex({ id: 99, label: "sends" }),
      sourceVertex: 1,
      targetVertex: 2,
    } as unknown as EdgeREST;

    // The caller decides this from the element's own shape. Getting it backwards constrains an edge
    // search to vertices, which cannot match anything and looks exactly like "nothing is similar".
    const found = similarSearchesFor(edge, [BOUND], true);
    expect(found[0].prefill.kind).toBe("edge");
    expect(found[0].prefill.sourceElementId).toBe(99);
  });

  it("offers one search per embedding the element carries that is actually projected", () => {
    const twoNames = vertex({
      properties: [
        { propertyId: "$embedding:default", propertyValue: "[0.1, 0.2]" },
        { propertyId: "$embedding:documents", propertyValue: "[0.3, 0.4]" },
        { propertyId: "$embedding:orphan", propertyValue: "[0.5, 0.6]" },
      ],
    });
    const indices: IndexDescription[] = [
      BOUND,
      { indexId: "docs-idx", pluginType: "VectorIndex", embeddingName: "documents" },
    ];

    const found = similarSearchesFor(twoNames, indices, false);
    expect(found.map((f) => f.embeddingName)).toEqual(["default", "documents"]);
  });

  it("skips an embedding property whose value is not a parseable vector", () => {
    const broken = vertex({
      properties: [{ propertyId: "$embedding:default", propertyValue: "not-a-vector" }],
    });
    expect(similarSearchesFor(broken, [BOUND], false)).toEqual([]);
  });

  it("tolerates a missing inventory rather than throwing on a server that reports none", () => {
    expect(similarSearchesFor(vertex(), null, false)).toEqual([]);
    expect(similarSearchesFor(vertex(), undefined, false)).toEqual([]);
  });
});
