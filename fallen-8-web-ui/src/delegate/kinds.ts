// MIT License
//
// kinds.ts
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
 * Per-kind editing contract (spec §6.1): parameter name/type, lambda shape, usings, and
 * the opening snippet. Every subgraph/path filter slot is typed (feature
 * subgraph-typed-filters); GraphElementFilter is no longer produced by any slot but stays
 * validatable on /delegates/validate — removal trigger in that feature's spec.
 */

export interface KindInfo {
  kind: DelegateKind;
  parameterName: string;
  parameterType: "VertexModel" | "EdgeModel" | "AGraphElementModel" | "string";
  returnType: "bool" | "double";
  lambdaShape: string;
  usings: string[];
  openingSnippet: string;
}

const PATH_USINGS = ["System", "System.Linq", "NoSQL.GraphDB.Core.Model"];
const SUBGRAPH_USINGS = [...PATH_USINGS, "NoSQL.GraphDB.Core.Algorithms"];

export const KIND_INFO: Record<DelegateKind, KindInfo> = {
  VertexFilter: {
    kind: "VertexFilter",
    parameterName: "v",
    parameterType: "VertexModel",
    returnType: "bool",
    lambdaShape: "(VertexModel v) => bool",
    usings: PATH_USINGS,
    openingSnippet: "return (v) => ",
  },
  EdgeFilter: {
    kind: "EdgeFilter",
    parameterName: "e",
    parameterType: "EdgeModel",
    returnType: "bool",
    lambdaShape: "(EdgeModel e) => bool",
    usings: PATH_USINGS,
    openingSnippet: "return (e) => ",
  },
  EdgePropertyFilter: {
    kind: "EdgePropertyFilter",
    parameterName: "p",
    parameterType: "string",
    returnType: "bool",
    lambdaShape: "(string p) => bool",
    usings: PATH_USINGS,
    openingSnippet: "return (p) => ",
  },
  VertexCost: {
    kind: "VertexCost",
    parameterName: "v",
    parameterType: "VertexModel",
    returnType: "double",
    lambdaShape: "(VertexModel v) => double",
    usings: PATH_USINGS,
    openingSnippet: "return (v) => ",
  },
  EdgeCost: {
    kind: "EdgeCost",
    parameterName: "e",
    parameterType: "EdgeModel",
    returnType: "double",
    lambdaShape: "(EdgeModel e) => double",
    usings: PATH_USINGS,
    openingSnippet: "return (e) => ",
  },
  GraphElementFilter: {
    kind: "GraphElementFilter",
    parameterName: "ge",
    parameterType: "AGraphElementModel",
    returnType: "bool",
    lambdaShape: "(AGraphElementModel ge) => bool",
    usings: SUBGRAPH_USINGS,
    openingSnippet: "return (ge) => ",
  },
};

/** The canonical property-access idiom taught via snippets and the NL prompt (spec §6.2). */
export const TRY_GET_PROPERTY_IDIOM =
  'return (v) => v.TryGetProperty(out int age, "age") && age > 30;';
