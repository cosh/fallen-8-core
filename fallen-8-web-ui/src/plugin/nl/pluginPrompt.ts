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

  const system = [
    "You author a COMPLETE C# source file for a Fallen-8 runtime plugin (a whole type, not a fragment).",
    "Output ONLY the C# source — the usings plus exactly ONE public sealed class. No prose, no markdown fences, no explanation.",
    `The class MUST implement ${iface}, have a public parameterless constructor, and its PluginName property MUST return exactly "${name}" — the server rejects any other name.`,
    contractGuidance(category, contract),
    "Complete this scaffold: keep its usings, class name, PluginName and the other IPlugin members exactly; replace only the TODO body with a real implementation of the request. Add usings only for engine types you actually use, and never invent members that do not exist.",
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
