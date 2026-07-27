// MIT License
//
// snippets.ts
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
 * (field failure, 2026-07-17). It only rewrites identifier-with-dot occurrences (plus the
 * parameter list), so keep fragment string literals free of `v.`/`e.`/`ge.`/`p.` and
 * dot-less parameter references out of cross-kind fragments.
 */
export function rewriteParameterName(code: string, parameterName: string): string {
  return code
    .replace(/\((v|e|ge|p)\)\s*=>/, `(${parameterName}) =>`)
    .replace(/\b(v|e|ge|p)\./g, `${parameterName}.`);
}

export function snippetCodeFor(snippet: Snippet, parameterName: string): string {
  return rewriteParameterName(snippet.code, parameterName);
}
