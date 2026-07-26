import { describe, expect, it } from "vitest";
import {
  buildPluginGenerationPrompt,
  buildPluginRefinePrompt,
  ensureRequiredUsings,
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
    // The exact using that the field failure dropped is pinned verbatim in the prompt.
    expect(system).toContain("using NoSQL.GraphDB.Core.Plugins;");
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

describe("ensureRequiredUsings", () => {
  const functionScaffold = scaffoldFor("function", "Path", "Lala");

  it("re-adds NoSQL.GraphDB.Core.Plugins when the model dropped it (the field failure)", () => {
    // The exact screenshot: four usings, the .Plugins line (IGraphFunction/GraphFunctionResult) gone.
    const draft = `using System;
using System.Collections.Generic;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Plugin;

public sealed class Lala : IGraphFunction { }`;
    const out = ensureRequiredUsings(draft, functionScaffold);
    expect(out).toContain("using NoSQL.GraphDB.Core.Plugins;");
    // ...inserted with the other usings, before the type declaration.
    expect(out.indexOf("using NoSQL.GraphDB.Core.Plugins;")).toBeLessThan(
      out.indexOf("public sealed class"),
    );
  });

  it("leaves a draft that already carries every required using unchanged", () => {
    // The scaffold has all its own usings and no LINQ, so it is a fixed point.
    expect(ensureRequiredUsings(functionScaffold, functionScaffold)).toBe(functionScaffold);
  });

  it("does not reorder existing usings, only appends the missing one", () => {
    const draft = `using System.Collections.Generic;
using System;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Plugin;

public sealed class Lala : IGraphFunction { }`;
    const out = ensureRequiredUsings(draft, functionScaffold);
    expect(out.startsWith("using System.Collections.Generic;")).toBe(true);
    expect(out).toContain("using NoSQL.GraphDB.Core.Plugins;");
    // No duplicates introduced.
    expect(out.match(/using System;/g)).toHaveLength(1);
    expect(out.match(/using NoSQL\.GraphDB\.Core\.Plugins;/g)).toHaveLength(1);
  });

  it("prepends the whole required set when the model emitted no usings at all", () => {
    const draft = "public sealed class Lala : IGraphFunction { }";
    const out = ensureRequiredUsings(draft, functionScaffold);
    expect(out.startsWith("using System;")).toBe(true);
    expect(out).toContain("using NoSQL.GraphDB.Core.Plugins;");
    expect(out.indexOf("using")).toBeLessThan(out.indexOf("public sealed class"));
  });

  it("adds System.Linq when the body calls a LINQ operator without importing it", () => {
    const draft = `${functionScaffold}
// var m = items.Where(x => x.Id > 1).Select(x => x.Label);`;
    const out = ensureRequiredUsings(draft, functionScaffold);
    expect(out).toContain("using System.Linq;");
  });

  it("does not add System.Linq for a body that uses no LINQ", () => {
    const out = ensureRequiredUsings(functionScaffold, functionScaffold);
    expect(out).not.toContain("using System.Linq;");
  });

  it("never duplicates System.Linq when it is already imported", () => {
    const draft = `using System;
using System.Linq;
using System.Collections.Generic;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Plugin;
using NoSQL.GraphDB.Core.Plugins;

public sealed class Lala { void M() { var x = items.Any(y => y > 1); } }`;
    const out = ensureRequiredUsings(draft, functionScaffold);
    expect(out.match(/using System\.Linq;/g)).toHaveLength(1);
  });
});
