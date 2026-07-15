import { describe, expect, it } from "vitest";
import { membersForType, memberByName } from "../src/delegate/providers";
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
