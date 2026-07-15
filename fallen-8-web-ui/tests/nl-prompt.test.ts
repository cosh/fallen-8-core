import { describe, expect, it } from "vitest";
import {
  buildGenerationPrompt,
  buildRefinePrompt,
  extractFragment,
} from "../src/delegate/nl/prompt";
import { KIND_INFO } from "../src/delegate/kinds";
import type { DelegateKind } from "../src/api/types";

/**
 * NL-assist prompt contract (nl-assist spec FR-26.5 + §13): per kind, the generation
 * prompt must include - in order - the instruction, the exact §6.1 lambda shape, the
 * usings, the §6.2 member surface incl. the TryGetProperty idiom, and matching few-shot
 * examples; the user's intent travels in the user turn.
 */

const ALL_KINDS: DelegateKind[] = [
  "VertexFilter",
  "EdgeFilter",
  "EdgePropertyFilter",
  "VertexCost",
  "EdgeCost",
  "GraphElementFilter",
];

describe("generation prompt assembly", () => {
  it.each(ALL_KINDS)("includes the full contract for %s", (kind) => {
    const info = KIND_INFO[kind];
    const prompt = buildGenerationPrompt(kind, "only persons older than 30");

    // (a) fragment-only instruction
    expect(prompt.system).toMatch(/ONLY the C# fragment/);
    // (b) exact lambda shape
    expect(prompt.system).toContain(info.lambdaShape);
    // (c) usings
    for (const using of info.usings) {
      expect(prompt.system).toContain(using);
    }
    // (d) idiom
    expect(prompt.system).toContain("TryGetProperty");
    // (e) at least one few-shot example
    expect(prompt.system).toMatch(/return \(.+\) =>/);
    // (f) intent in the user turn
    expect(prompt.user).toContain("only persons older than 30");
    expect(prompt.user).toContain(kind);
  });

  it("orders the sections per the contract (a→e)", () => {
    const { system } = buildGenerationPrompt("VertexFilter", "x");
    const instruction = system.indexOf("ONLY the C# fragment");
    const shape = system.indexOf("(VertexModel v) => bool");
    const usings = system.indexOf("Available usings");
    const members = system.indexOf("Members reachable");
    const examples = system.indexOf("Examples of valid fragments");
    expect(instruction).toBeGreaterThanOrEqual(0);
    expect(shape).toBeGreaterThan(instruction);
    expect(usings).toBeGreaterThan(shape);
    expect(members).toBeGreaterThan(usings);
    expect(examples).toBeGreaterThan(members);
  });

  it("scopes members to the parameter type (string kinds get no graph members)", () => {
    const stringPrompt = buildGenerationPrompt("EdgePropertyFilter", "x").system;
    expect(stringPrompt).toContain("StartsWith");
    expect(stringPrompt).not.toContain("GetOutDegree");

    const vertexPrompt = buildGenerationPrompt("VertexFilter", "x").system;
    expect(vertexPrompt).toContain("GetOutDegree");

    const edgePrompt = buildGenerationPrompt("EdgeFilter", "x").system;
    expect(edgePrompt).toContain("SourceVertex");
    expect(edgePrompt).not.toContain("GetAllNeighbors");
  });

  it("subgraph kinds carry the Algorithms using", () => {
    const { system } = buildGenerationPrompt("GraphElementFilter", "x");
    expect(system).toContain("NoSQL.GraphDB.Core.Algorithms");
  });
});

describe("refine prompt", () => {
  it("feeds the failed fragment and its diagnostics back", () => {
    const refine = buildRefinePrompt("VertexFilter", "return (v) => v.Nope;", [
      {
        line: 1,
        column: 17,
        endLine: 1,
        endColumn: 21,
        id: "CS1061",
        message: "'VertexModel' does not contain a definition for 'Nope'",
        severity: "error",
      },
    ]);
    expect(refine).toContain("return (v) => v.Nope;");
    expect(refine).toContain("CS1061");
    expect(refine).toContain("line 1, col 17");
  });
});

describe("output handling (FR-26.6)", () => {
  it("strips markdown fences", () => {
    expect(
      extractFragment('```csharp\nreturn (v) => v.Label == "person";\n```'),
    ).toBe('return (v) => v.Label == "person";');
    expect(extractFragment("```\nreturn (v) => true;\n```")).toBe("return (v) => true;");
  });

  it("cuts leading prose before the method body", () => {
    expect(
      extractFragment('Sure! Here is the fragment:\nreturn (v) => v.Label == "person";'),
    ).toBe('return (v) => v.Label == "person";');
  });

  it("leaves a clean fragment untouched", () => {
    expect(extractFragment("return (v) => true;")).toBe("return (v) => true;");
  });
});
