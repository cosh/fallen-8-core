// MIT License
//
// type-model.test.ts
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
import { membersForType, memberByName } from "../src/delegate/providers";
import { buildGenerationPrompt } from "../src/delegate/nl/prompt";
import { snippetCodeFor, snippetsForKind } from "../src/delegate/snippets";

/**
 * Static-model completions per parameter type (FR-22, spec §10 "completions from the
 * static model"): the member surface a slot offers is exactly its parameter type plus
 * the AGraphElementModel base - and nothing else.
 */
describe("static type model", () => {
  it("VertexModel members include base + vertex members", () => {
    const names = membersForType("VertexModel").map((m) => m.name);
    expect(names).toContain("TryGetProperty"); // base
    expect(names).toContain("Label"); // base
    expect(names).toContain("GetOutDegree"); // vertex
    expect(names).toContain("GetAllNeighbors"); // vertex
    expect(names).not.toContain("SourceVertex"); // edge-only
    expect(names).not.toContain("StartsWith"); // string-only
  });

  // Engine members whose CALL fails with CS0012 in a fragment, because the compile does not
  // reference the assembly their signature names. fallen-8-unittest/DelegateAccessorSurfaceTest.cs
  // compiles each of these and asserts it fails, so this list is measured, not assumed.
  const UNCOMPILABLE = [
    "GetAllProperties",
    "GetAllNeighbors",
    "GetIncomingEdgeIds",
    "GetOutgoingEdgeIds",
  ];

  it("flags exactly the members a fragment cannot call, and keeps offering them", () => {
    const vertex = membersForType("VertexModel");
    const flagged = vertex.filter((m) => m.compilable === false).map((m) => m.name);
    expect(flagged.sort()).toEqual([...UNCOMPILABLE].sort());

    // Offered, not deleted: somebody who read the engine source comes looking for these, and
    // finding one struck through with its substitute named beats finding nothing.
    for (const name of UNCOMPILABLE) {
      const member = memberByName(name);
      expect(member, name).toBeDefined();
      expect(member?.doc, name).toContain("NOT callable in a fragment");
      expect(member?.doc, name).toContain("CS0012");
    }
  });

  it("offers the compilable members the model used to omit", () => {
    const vertex = membersForType("VertexModel").map((m) => m.name);
    // Documented at docs/src/content/docs/delegates.mdx (### Accessor surface) and compile-checked
    // by DelegateAccessorSurfaceTest, but absent from this model until the drift was reconciled.
    expect(vertex).toContain("TryGetEmbedding");
    expect(vertex).toContain("TryGetEmbeddingModelStamp");
    expect(vertex).toContain("TryGetOutEdgesSpan");
    expect(vertex).toContain("TryGetInEdgesSpan");
  });

  it("withholds uncompilable members from the NL-assist prompt (they read as sanctioned)", () => {
    const { system } = buildGenerationPrompt("VertexFilter", "anything");
    for (const name of UNCOMPILABLE) {
      expect(system, name).not.toContain(name);
    }
    // The substitutes the model SHOULD reach for are still in front of it.
    expect(system).toContain("GetPropertyCount");
    expect(system).toContain("OutEdges");
    expect(system).toContain("TryGetProperty");
  });

  it("EdgeModel members include base + edge members", () => {
    const names = membersForType("EdgeModel").map((m) => m.name);
    expect(names).toContain("SourceVertex");
    expect(names).toContain("EdgePropertyId");
    expect(names).toContain("TryGetProperty");
    expect(names).not.toContain("GetOutDegree");
  });

  it("string kinds get string members only - no graph model (spec §3.2)", () => {
    const names = membersForType("string").map((m) => m.name);
    expect(names).toContain("StartsWith");
    expect(names).toContain("Contains");
    expect(names).not.toContain("TryGetProperty");
    expect(names).not.toContain("Label");
  });

  it("TryGetProperty carries the out-parameter signature for signature help", () => {
    const member = memberByName("TryGetProperty");
    expect(member?.signature).toContain("out T result");
    expect(member?.signature).toContain("string propertyId");
  });
});

describe("snippet library", () => {
  it("offers matching snippets per kind", () => {
    expect(snippetsForKind("EdgeCost").map((s) => s.title)).toContain("Weighted edge cost");
    expect(snippetsForKind("EdgePropertyFilter").map((s) => s.title)).toContain(
      "Edge property allow-list",
    );
    expect(snippetsForKind("VertexFilter").map((s) => s.title)).toContain(
      "Property + threshold",
    );
  });

  it("rewrites the parameter identifier to the slot's parameter", () => {
    const labelMatch = snippetsForKind("GraphElementFilter").find(
      (s) => s.title === "Label match",
    )!;
    expect(snippetCodeFor(labelMatch, "ge")).toBe('return (ge) => ge.Label == "person";');
  });
});
