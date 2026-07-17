import type { DelegateKind } from "../api/types";

/**
 * Snippet library (FR-22), mirroring the prototype's set. Each entry names the kinds it
 * fits; the NL assist reuses the matching entries as required few-shot examples
 * (nl-assist spec FR-26.5).
 */

export interface Snippet {
  title: string;
  description: string;
  kinds: DelegateKind[];
  code: string;
}

export const SNIPPET_LIBRARY: Snippet[] = [
  {
    title: "Property + threshold",
    description: "Typed property access with a numeric threshold",
    kinds: ["VertexFilter", "EdgeFilter", "GraphElementFilter"],
    code: 'return (v) => v.TryGetProperty(out int age, "age") && age > 30;',
  },
  {
    title: "Label match",
    description: "Keep only elements with a given label",
    kinds: ["VertexFilter", "EdgeFilter", "GraphElementFilter"],
    code: 'return (v) => v.Label == "person";',
  },
  {
    title: "Label + property",
    description: "Built-in Label member combined with a typed property test",
    kinds: ["VertexFilter", "EdgeFilter", "GraphElementFilter"],
    code: 'return (v) => v.Label == "person" && v.TryGetProperty(out int age, "age") && age > 30;',
  },
  {
    title: "Edge property allow-list",
    description: "Traverse only over the named edge properties",
    kinds: ["EdgePropertyFilter"],
    code: 'return (p) => p == "knows" || p == "worksWith";',
  },
  {
    title: "Weighted edge cost",
    description: "Read a double property as the edge cost (1.0 fallback)",
    kinds: ["EdgeCost"],
    code: 'return (e) => e.TryGetProperty(out double weight, "weight") ? weight : 1.0;',
  },
  {
    title: "Uniform vertex cost",
    description: "Every vertex costs the same",
    kinds: ["VertexCost"],
    code: "return (v) => 1.0;",
  },
  {
    title: "Degree filter",
    description: "Keep only well-connected vertices",
    kinds: ["VertexFilter"],
    code: "return (v) => v.GetOutDegree() + v.GetInDegree() >= 2;",
  },
];

export function snippetsForKind(kind: DelegateKind): Snippet[] {
  return SNIPPET_LIBRARY.filter((snippet) => snippet.kinds.includes(kind));
}

/**
 * Rewrites a fragment's parameter identifier to the slot's parameter name so inserting
 * "Label match" into an EdgeFilter yields `return (e) => e.Label == ...`. Every fragment
 * shown to the user or the NL model must go through this - a `v` example in a `ge` slot
 * is exactly the mismatch that made phi4-mini wrap the idiom in an inline-invoked lambda
 * (field failure, 2026-07-17).
 */
export function rewriteParameterName(code: string, parameterName: string): string {
  return code
    .replace(/\((v|e|ge|p)\)\s*=>/, `(${parameterName}) =>`)
    .replace(/\b(v|e|ge|p)\./g, `${parameterName}.`);
}

export function snippetCodeFor(snippet: Snippet, parameterName: string): string {
  return rewriteParameterName(snippet.code, parameterName);
}
