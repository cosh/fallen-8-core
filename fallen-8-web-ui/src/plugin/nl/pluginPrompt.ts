// MIT License
//
// pluginPrompt.ts
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

import type { NlPrompt } from "../../delegate/nl/prompt";
import type { AlgorithmContract, PluginAuthoringCategory } from "../../api/types";
import { contractInterface } from "../scaffolds";

/**
 * Whole-type generation prompt assembly for plugin authoring (feature plugin-registration §6).
 * The delegate NL path (src/delegate/nl/prompt.ts) drafts a single lambda BODY; a plugin is a
 * whole type, so the shape rules, the per-contract member guidance, the few-shot (the scaffold
 * itself), and the output extractor all differ. The model backend transport (generate.ts) and
 * the backend-config store (config.ts) are shared unchanged.
 */

interface PluginPromptInput {
  category: PluginAuthoringCategory;
  contract: AlgorithmContract;
  /** The exact registration name — must be the type's PluginName (server-validated). */
  name: string;
  /** The current scaffold: the shape to complete (correct usings, class, IPlugin members). */
  scaffold: string;
  intent: string;
  priorDrafts?: string[];
}

/** A plain `using Some.Namespace;` directive (not `using static`, not an alias). */
const USING_DIRECTIVE = /^\s*using\s+([A-Za-z_][\w.]*)\s*;\s*$/;

/** The namespaces imported by plain using directives in `source`, in source order. */
function usingNamespaces(source: string): string[] {
  const namespaces: string[] = [];
  for (const line of source.split("\n")) {
    const match = USING_DIRECTIVE.exec(line);
    if (match) namespaces.push(match[1]);
  }
  return namespaces;
}

/** The contract method to implement + how to read the graph / build the result, per contract. */
function contractGuidance(category: PluginAuthoringCategory, contract: AlgorithmContract): string {
  if (category === "function") {
    return [
      "Implement: bool TryInvoke(out GraphFunctionResult result, IDictionary<string, object> parameters).",
      "Read the graph through the IFallen8 captured in Initialize (e.g. _graph.GetAllVertices(label), _graph.GetAllEdges()); build the result with GraphFunctionResult.FromElements(vertices, edges).",
      "It is READ-ONLY: return a view of EXISTING vertices/edges; never mutate. Return true on success (an empty result is still true); return false only for an expected failure such as a missing parameter.",
    ].join(" ");
  }
  switch (contract) {
    case "Path":
      return "Implement: bool TryCalculateShortestPath(out List<Path> result, ShortestPathDefinition definition). Compute paths between definition.SourceVertex and definition.TargetVertex; set result and return true when at least one path is found.";
    case "SubGraph":
      return "Implement: bool TryCreateSubgraph(out SubGraphResult result, SubGraphDefinition definition). Build the subgraph from definition.VertexFilter / EdgeFilter / Pattern; set result and return true on success.";
    case "Analytics":
      return "Implement: bool TryRunAnalytics(out GraphAnalyticsResult result, GraphAnalyticsDefinition definition). Score or partition the in-scope graph and return a GraphAnalyticsResult; return true on success.";
  }
}

export function buildPluginGenerationPrompt(input: PluginPromptInput): NlPrompt {
  const { category, contract, name, scaffold, intent, priorDrafts = [] } = input;
  const iface = contractInterface(category, contract);
  const requiredUsings = usingNamespaces(scaffold)
    .map((ns) => `using ${ns};`)
    .join(" ");

  const system = [
    "You author a COMPLETE C# source file for a Fallen-8 runtime plugin (a whole type, not a fragment).",
    "Output ONLY the C# source — the usings plus exactly ONE public sealed class. No prose, no markdown fences, no explanation.",
    `The class MUST implement ${iface}, have a public parameterless constructor, and its PluginName property MUST return exactly "${name}" — the server rejects any other name.`,
    contractGuidance(category, contract),
    "Complete this scaffold: keep its usings, class name, PluginName and the other IPlugin members exactly; replace only the TODO body with a real implementation of the request. Add usings only for engine types you actually use, and never invent members that do not exist.",
    `Reproduce these using directives verbatim at the top of the file: ${requiredUsings} The contract interface and its result type live in those namespaces; dropping one makes them "could not be found". Add "using System.Linq;" as well if the body uses LINQ.`,
    "```csharp\n" + scaffold + "\n```",
  ].join("\n\n");

  const user = [
    `Author the ${iface} plugin named "${name}" that does: ${intent}`,
    ...(priorDrafts.length > 0
      ? [
          "You already produced these drafts for this request — produce a meaningfully different, still-complete variant (do NOT repeat one):\n" +
            priorDrafts.map((_, i) => `- draft ${i + 1}`).join("\n"),
        ]
      : []),
  ].join("\n\n");

  return { system, user };
}

/** Follow-up turn for the refine loop: the failed source + the compiler/contract diagnostics. */
export function buildPluginRefinePrompt(input: {
  category: PluginAuthoringCategory;
  contract: AlgorithmContract;
  name: string;
  source: string;
  error: string;
}): string {
  const iface = contractInterface(input.category, input.contract);
  return [
    "The plugin failed to compile or did not satisfy its contract.",
    "Source:",
    "```csharp\n" + input.source + "\n```",
    "Diagnostics:",
    input.error || "(no diagnostics returned)",
    `Fix it. Output ONLY the corrected COMPLETE C# source. It must still be exactly one public sealed class implementing ${iface}, with a public parameterless constructor and PluginName == "${input.name}". No prose, no markdown.`,
  ].join("\n");
}

/**
 * Output handling for a whole type: prefer a fenced ```csharp block; otherwise strip any leading
 * prose before the first C# construct. Unlike the fragment extractor it does NOT truncate at the
 * first ";" — a plugin is a whole file, not a single statement.
 */
export function extractType(raw: string): string {
  const fenced = /```(?:csharp|cs|c#)?\s*\n?([\s\S]*?)```/i.exec(raw);
  const candidate = (fenced ? fenced[1] : raw).trim();

  const anchors = ["using ", "namespace ", "public ", "internal ", "sealed ", "["];
  let cut = -1;
  for (const anchor of anchors) {
    const index = candidate.indexOf(anchor);
    if (index >= 0 && (cut < 0 || index < cut)) {
      cut = index;
    }
  }
  return (cut > 0 ? candidate.slice(cut) : candidate).trim();
}

/**
 * LINQ operators a plugin body commonly reaches for. A small model that writes `.Where`/`.Any`/
 * `.SelectMany` in the body but forgets `using System.Linq;` produces the exact CS1061 "does not
 * contain a definition for ..." seen in the field; ensureRequiredUsings adds the import when it
 * sees one of these called as `.Op(` or `.Op<`.
 */
const LINQ_OPERATORS = [
  "Select", "SelectMany", "Where", "Any", "All", "First", "FirstOrDefault",
  "Single", "SingleOrDefault", "Last", "LastOrDefault", "OrderBy", "OrderByDescending",
  "ThenBy", "GroupBy", "Distinct", "Take", "Skip", "Count", "Sum", "Min", "Max",
  "Average", "Aggregate", "ToList", "ToArray", "ToDictionary", "ToHashSet",
  "Concat", "Union", "Intersect", "Except", "Reverse", "Zip",
];

const LINQ_CALL = new RegExp(`\\.(?:${LINQ_OPERATORS.join("|")})\\s*[(<]`);

/**
 * Guarantees a model draft carries the usings its contract needs, whatever the model dropped
 * (feature plugin-registration §6). The whole-type plugin surface is the ONLY NL path where the
 * model authors its own using directives (the delegate/fragment path emits a bare lambda body and
 * the server harness supplies the usings), and a small model routinely regenerates the scaffold
 * minus one line, most damagingly `NoSQL.GraphDB.Core.Plugins`, which holds IGraphFunction and
 * GraphFunctionResult, so the type "could not be found". This unions the scaffold's known-required
 * usings into the draft (adding usings never breaks the compile: the scaffold's own set compiles,
 * so a subset re-added to a draft cannot introduce an ambiguity the scaffold does not already
 * have), and adds System.Linq when the body calls a LINQ operator without it. It repairs only the
 * import lines; genuine body mistakes still surface through the compile-and-refine loop.
 */
export function ensureRequiredUsings(draft: string, scaffold: string): string {
  const present = new Set(usingNamespaces(draft));
  const missing = usingNamespaces(scaffold).filter((ns) => !present.has(ns));
  if (LINQ_CALL.test(draft) && !present.has("System.Linq") && !missing.includes("System.Linq")) {
    missing.push("System.Linq");
  }
  if (missing.length === 0) return draft;

  const lines = draft.split("\n");
  // Insert after the last existing using directive; if the draft has none, at the very top.
  let insertAt = 0;
  for (let i = 0; i < lines.length; i++) {
    if (USING_DIRECTIVE.test(lines[i])) insertAt = i + 1;
  }
  lines.splice(insertAt, 0, ...missing.map((ns) => `using ${ns};`));
  return lines.join("\n");
}
