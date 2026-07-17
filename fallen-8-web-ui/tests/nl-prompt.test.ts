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

  it("steers built-in members away from TryGetProperty (nl-assist-ux FR-10)", () => {
    const { system } = buildGenerationPrompt("GraphElementFilter", "x");
    expect(system).toContain("Label and Id are BUILT-IN members");
    expect(system).toMatch(/NEVER call TryGetProperty for "label" or "id"/);
    // The guidance names the slot's own parameter.
    expect(system).toContain('ge.Label == "person"');

    // String-parameter kinds have no Label/Id members - no such guidance.
    const stringPrompt = buildGenerationPrompt("EdgePropertyFilter", "x").system;
    expect(stringPrompt).not.toContain("BUILT-IN members");
  });

  it("few-shots include the combined Label + property example for element kinds", () => {
    for (const kind of ["VertexFilter", "EdgeFilter", "GraphElementFilter"] as const) {
      const { system } = buildGenerationPrompt(kind, "x");
      expect(system).toContain('.Label == "person" && ');
    }
  });

  it("re-drafting lists prior drafts and asks for a distinct variant (nl-assist-ux FR-8)", () => {
    const first = buildGenerationPrompt("VertexFilter", "small ids");
    expect(first.user).not.toMatch(/do NOT repeat/i);

    const redraft = buildGenerationPrompt("VertexFilter", "small ids", [
      "return (v) => v.Id < 30;",
    ]);
    expect(redraft.user).toContain("return (v) => v.Id < 30;");
    expect(redraft.user).toMatch(/do NOT repeat/i);
    expect(redraft.user).toMatch(/different valid variant/i);
    // The system half is unchanged — the variant request travels in the user turn.
    expect(redraft.system).toBe(first.system);
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

  it("cuts trailing prose after the statement's semicolon (field example)", () => {
    expect(
      extractFragment(
        'return (v) => v.Label == "person" && v.GetAge() > 30; (Note that GetAge is not listed above as a member of VertexModel.)',
      ),
    ).toBe('return (v) => v.Label == "person" && v.GetAge() > 30;');
    // Leading and trailing prose combined.
    expect(
      extractFragment("Sure! Here you go:\nreturn (v) => true; Hope this helps."),
    ).toBe("return (v) => true;");
    // Semicolons inside string literals do not end the fragment.
    expect(extractFragment('return (p) => p.Contains(";") && p.Length > 1; done')).toBe(
      'return (p) => p.Contains(";") && p.Length > 1;',
    );
  });
});
