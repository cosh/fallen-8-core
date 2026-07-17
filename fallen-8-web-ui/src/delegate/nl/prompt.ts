import type { DelegateDiagnostic, DelegateKind } from "../../api/types";
import { KIND_INFO } from "../kinds";
import { membersForType } from "../providers";
import { snippetCodeFor, snippetsForKind } from "../snippets";

/**
 * Generation-prompt assembly (nl-assist spec FR-26.5). The contract fixes the order:
 * (a) emit-only-a-fragment instruction, (b) the slot's exact lambda shape, (c) the
 * available usings, (d) the reachable member surface incl. the TryGetProperty idiom,
 * (e) matching few-shot examples from the snippet library (required - they carry the
 * Python-to-C# gap), (f) the user's plain-language intent.
 */

export interface NlPrompt {
  system: string;
  user: string;
}

export function buildGenerationPrompt(
  kind: DelegateKind,
  intent: string,
  priorDrafts: string[] = [],
): NlPrompt {
  const info = KIND_INFO[kind];

  const members = membersForType(info.parameterType)
    .map((member) => `- ${member.signature}${member.doc ? ` // ${member.doc}` : ""}`)
    .join("\n");

  const fewShot = snippetsForKind(kind)
    .slice(0, 3)
    .map(
      (snippet) =>
        `// ${snippet.title}: ${snippet.description}\n${snippetCodeFor(snippet, info.parameterName)}`,
    )
    .join("\n\n");

  const system = [
    // (a) instruction
    "You draft a single C# fragment for a Fallen-8 graph query: a METHOD BODY that returns a lambda.",
    "Output ONLY the C# fragment - no prose, no markdown fences, no explanations.",
    'The fragment must start with "return" and end with ";".',
    // (b) lambda shape
    `Delegate kind: ${kind}. The lambda shape is exactly: ${info.lambdaShape}. Use the parameter name "${info.parameterName}".`,
    // (c) usings
    `Available usings: ${info.usings.join(", ")}. Nothing else is importable.`,
    // (d) type surface + idiom
    `Members reachable on the parameter (type ${info.parameterType}):\n${members}`,
    `The canonical typed property access is TryGetProperty: ${TRY_GET_PROPERTY_IDIOM_LINE}`,
    "Do not invent members that are not listed above.",
    // (e) few-shot examples
    `Examples of valid fragments for this kind:\n\n${fewShot}`,
  ].join("\n\n");

  // (f) intent; re-drafting the same intent lists prior drafts and asks for a distinct
  // variant, so deterministic sampling doesn't return the same fragment again
  // (nl-assist-ux FR-8).
  const user = [
    `Write the ${kind} fragment for: ${intent}`,
    ...(priorDrafts.length > 0
      ? [
          `Already drafted for this request (do NOT repeat these):\n${priorDrafts
            .map((draft) => `- ${draft}`)
            .join("\n")}\nProduce a meaningfully different valid variant.`,
        ]
      : []),
  ].join("\n\n");

  return { system, user };
}

const TRY_GET_PROPERTY_IDIOM_LINE =
  'return (v) => v.TryGetProperty(out int age, "age") && age > 30;';

/** Follow-up turn for the refine loop (FR-26.7): failed fragment + its diagnostics. */
export function buildRefinePrompt(
  kind: DelegateKind,
  fragment: string,
  diagnostics: DelegateDiagnostic[],
): string {
  const list = diagnostics
    .filter((d) => d.severity === "error")
    .map((d) => `- line ${d.line}, col ${d.column}: ${d.id} ${d.message}`)
    .join("\n");
  return [
    `The ${kind} fragment failed to compile.`,
    "Fragment:",
    fragment,
    "Compiler errors:",
    list,
    "Fix it. Output ONLY the corrected C# fragment, no prose, no markdown.",
  ].join("\n");
}

/**
 * Output handling (FR-26.6): strip markdown fences and stray prose. A fenced block wins;
 * otherwise cut leading prose before the first "return".
 */
export function extractFragment(raw: string): string {
  const fenced = /```(?:csharp|cs|c#)?\s*\n?([\s\S]*?)```/i.exec(raw);
  if (fenced) return fenced[1].trim();

  const trimmed = raw.trim();
  const returnIndex = trimmed.indexOf("return");
  if (returnIndex > 0) {
    // Leading prose before the method body - drop it.
    return trimmed.slice(returnIndex).trim();
  }
  return trimmed;
}
