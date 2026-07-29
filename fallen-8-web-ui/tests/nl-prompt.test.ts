// MIT License
//
// nl-prompt.test.ts
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
  buildGenerationPrompt,
  buildRefinePrompt,
  extractFragment,
} from "../src/delegate/nl/prompt";
import { KIND_INFO } from "../src/delegate/kinds";
import type { DelegateKind } from "../src/api/types";

/**
 * NL-assist prompt contract (nl-assist spec FR-26.5 + §13): per kind, the generation
 * prompt must include - in order - the instruction, the exact §6.1 lambda shape, the
 * usings, the §6.2 member surface incl. the TryGetProperty idiom (where the parameter
 * type carries it - string slots have no TryGetProperty), and matching few-shot
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
    // (d) idiom - only where the parameter type has TryGetProperty, and always
    // spelled with the slot's own parameter name (the 2026-07-17 field failure:
    // a `v` idiom in a `ge` slot made phi4-mini emit an inline-invoked lambda).
    if (info.parameterType !== "string") {
      expect(prompt.system).toContain(
        `${info.parameterName}.TryGetProperty(out int age, "age")`,
      );
    } else {
      // A string parameter has no TryGetProperty - teaching the idiom there is the
      // same "member that does not exist on this parameter" trap.
      expect(prompt.system).not.toContain("TryGetProperty");
    }
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

  it.each(ALL_KINDS)(
    "forbids inline-invoked lambdas but allows predicate arguments, naming the slot's parameter (%s)",
    (kind) => {
      const info = KIND_INFO[kind];
      const { system } = buildGenerationPrompt(kind, "x");
      expect(system).toContain("NEVER invoke a lambda inline");
      expect(system).toContain(
        `((${info.parameterName}) => ...)(${info.parameterName})`,
      );
      // The argument-lambda allowance is load-bearing for AnyPropertyValueMatches
      // (feature element-fulltext-match): its predicate IS a second, non-invoked lambda.
      // Gated off the string slot, whose member surface has no predicate-taking member.
      if (info.parameterType === "string") {
        expect(system).not.toContain("passed as a member ARGUMENT");
      } else {
        expect(system).toContain("passed as a member ARGUMENT");
      }
      expect(system).toMatch(/out variable declared in one && clause/);
    },
  );

  it("lists AnyPropertyValueMatches for element-typed kinds only", () => {
    for (const kind of ["VertexFilter", "EdgeFilter", "GraphElementFilter"] as const) {
      expect(buildGenerationPrompt(kind, "x").system).toContain("AnyPropertyValueMatches");
    }
    expect(buildGenerationPrompt("EdgePropertyFilter", "x").system).not.toContain(
      "AnyPropertyValueMatches",
    );
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

  it("restates the single-lambda shape rule with the slot's parameter", () => {
    const refine = buildRefinePrompt("GraphElementFilter", "return (ge) => true;", []);
    expect(refine).toContain("single (AGraphElementModel ge) => bool lambda");
    expect(refine).toContain('directly on "ge"');
    // Inline-invoked lambdas stay forbidden; a predicate ARGUMENT (the
    // AnyPropertyValueMatches shape, feature element-fulltext-match) is allowed.
    expect(refine).toMatch(/no inline-invoked lambdas/);
    expect(refine).toMatch(/predicate passed as a member argument is fine/);
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
