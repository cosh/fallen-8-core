import { describe, expect, it } from "vitest";
import {
  buildPluginGenerationPrompt,
  buildPluginRefinePrompt,
  extractType,
} from "../src/plugin/nl/pluginPrompt";
import { scaffoldFor } from "../src/plugin/scaffolds";

/**
 * Whole-type NL-assist prompt logic (feature plugin-registration §6). The delegate NL path
 * extracts a single lambda statement; a plugin is a whole file, so these pin that the extractor
 * keeps the whole type intact and the prompts carry the contract + the exact PluginName rule.
 */

describe("extractType", () => {
  it("returns a fenced csharp block whole, never truncating at the first semicolon", () => {
    const raw =
      "Sure, here you go:\n```csharp\nusing System;\npublic sealed class X { public int A => 1; public int B => 2; }\n```\nDone.";
    const out = extractType(raw);
    expect(out.startsWith("using System;")).toBe(true);
    // A fragment extractor would cut at the first ';'; the whole type must survive.
    expect(out).toContain("public int B => 2;");
    expect(out).not.toContain("Sure, here");
    expect(out).not.toContain("Done.");
  });

  it("strips leading prose before the first C# construct when unfenced", () => {
    const raw = "Here is the class:\nusing System;\npublic sealed class Y { }";
    expect(extractType(raw).startsWith("using System;")).toBe(true);
  });

  it("returns the trimmed input when there is nothing to strip", () => {
    expect(extractType("public sealed class Z { }")).toBe("public sealed class Z { }");
  });
});

describe("buildPluginGenerationPrompt", () => {
  it("pins the contract interface, the exact PluginName rule, and embeds the scaffold (algorithm)", () => {
    const scaffold = scaffoldFor("algorithm", "Path", "MyDijkstra");
    const { system, user } = buildPluginGenerationPrompt({
      category: "algorithm",
      contract: "Path",
      name: "MyDijkstra",
      scaffold,
      intent: "weighted shortest path",
    });
    expect(system).toContain("IShortestPathAlgorithm");
    expect(system).toContain('PluginName property MUST return exactly "MyDijkstra"');
    expect(system).toContain("TryCalculateShortestPath");
    expect(system).toContain(scaffold); // the scaffold is the few-shot shape
    expect(user).toContain("weighted shortest path");
  });

  it("uses the IGraphFunction guidance (read-only, GraphFunctionResult) for the function category", () => {
    const { system } = buildPluginGenerationPrompt({
      category: "function",
      contract: "Path", // ignored for functions
      name: "NeighboursOfLabel",
      scaffold: scaffoldFor("function", "Path", "NeighboursOfLabel"),
      intent: "vertices of a label",
    });
    expect(system).toContain("IGraphFunction");
    expect(system).toContain("TryInvoke");
    expect(system).toContain("GraphFunctionResult.FromElements");
    expect(system).toContain("READ-ONLY");
  });

  it("lists prior drafts so a re-draft asks for a different variant", () => {
    const { user } = buildPluginGenerationPrompt({
      category: "function",
      contract: "Path",
      name: "F",
      scaffold: "x",
      intent: "i",
      priorDrafts: ["one", "two"],
    });
    expect(user).toContain("meaningfully different");
    expect(user).toContain("draft 2");
  });
});

describe("buildPluginRefinePrompt", () => {
  it("feeds the diagnostics back and restates the contract + name rule", () => {
    const out = buildPluginRefinePrompt({
      category: "algorithm",
      contract: "Analytics",
      name: "Ranker",
      source: "public sealed class Ranker { }",
      error: "ID: CS0535, Message: does not implement IGraphAnalyticsAlgorithm",
    });
    expect(out).toContain("CS0535");
    expect(out).toContain("IGraphAnalyticsAlgorithm");
    expect(out).toContain('PluginName == "Ranker"');
  });
});
