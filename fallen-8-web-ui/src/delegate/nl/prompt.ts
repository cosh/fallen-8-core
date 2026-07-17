import type { DelegateDiagnostic, DelegateKind } from "../../api/types";
import { firstTopLevelSemicolon } from "./format";
import { KIND_INFO, TRY_GET_PROPERTY_IDIOM } from "../kinds";
import { membersForType } from "../providers";
import { rewriteParameterName, snippetCodeFor, snippetsForKind } from "../snippets";

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
    // (b) lambda shape. The single-lambda rule is part of the shape: a real session
    // (phi4-mini, 2026-07-17) wrapped the TryGetProperty idiom in an inline-invoked
    // lambda - `((v) => v.TryGetProperty(out int age, "age"))(ge)` - which neither
    // compiles nor keeps `age` in scope for the next clause.
    `Delegate kind: ${kind}. The lambda shape is exactly: ${info.lambdaShape}. Use the parameter name "${info.parameterName}".`,
    // The counter-example is spelled in the slot's own parameter: small models copy
    // negated examples, and a `v` here would re-seed the very mismatch being forbidden.
    `The fragment is that ONE lambda and nothing else. NEVER define a second lambda inside it and NEVER invoke a lambda inline like ((${info.parameterName}) => ...)(${info.parameterName}) - call members directly on "${info.parameterName}". An out variable declared in one && clause stays usable in the clauses after it.`,
    // (c) usings
    `Available usings: ${info.usings.join(", ")}. Nothing else is importable.`,
    // (d) type surface + idiom
    `Members reachable on the parameter (type ${info.parameterType}):\n${members}`,
    // The idiom and the built-in-vs-user-property steering (nl-assist-ux FR-10) apply
    // only where TryGetProperty exists - a string parameter has neither. Both examples
    // are rewritten to the slot's parameter name; a `v` example in a `ge` slot is what
    // triggered the inline-lambda failure above.
    ...(info.parameterType !== "string"
      ? [
          `The canonical typed property access is TryGetProperty, called directly on "${info.parameterName}": ${rewriteParameterName(TRY_GET_PROPERTY_IDIOM, info.parameterName)}`,
          `Label and Id are BUILT-IN members - test them directly: ${info.parameterName}.Label == "person", ${info.parameterName}.Id < 10. NEVER call TryGetProperty for "label" or "id"; TryGetProperty is only for user-defined properties such as "age" or "name".`,
        ]
      : []),
    "Do not invent members that are not listed above. Add only the checks the request asks for - nothing speculative.",
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

/** Follow-up turn for the refine loop (FR-26.7): failed fragment + its diagnostics. */
export function buildRefinePrompt(
  kind: DelegateKind,
  fragment: string,
  diagnostics: DelegateDiagnostic[],
): string {
  const info = KIND_INFO[kind];
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
    // Small models rarely restructure from bare diagnostics; restate the shape rule.
    `Fix it. The fragment must stay a single ${info.lambdaShape} lambda calling members directly on "${info.parameterName}" - no nested or inline-invoked lambdas. Output ONLY the corrected C# fragment, no prose, no markdown.`,
  ].join("\n");
}

/**
 * Output handling (FR-26.6): strip markdown fences and stray prose. A fenced block wins;
 * otherwise cut leading prose before the first "return" and trailing prose after the
 * statement's closing `;` (models append parenthetical notes there - seen in the field).
 */
export function extractFragment(raw: string): string {
  const fenced = /```(?:csharp|cs|c#)?\s*\n?([\s\S]*?)```/i.exec(raw);
  const candidate = fenced ? fenced[1].trim() : raw.trim();

  const returnIndex = candidate.indexOf("return");
  const fromReturn = returnIndex > 0 ? candidate.slice(returnIndex) : candidate;

  // The fragment is a single statement: everything after its first top-level `;` is
  // prose (`;` inside string literals or brackets is skipped by the scanner).
  const end = firstTopLevelSemicolon(fromReturn);
  return (end >= 0 ? fromReturn.slice(0, end + 1) : fromReturn).trim();
}
