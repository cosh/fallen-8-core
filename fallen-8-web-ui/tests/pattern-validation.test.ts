// MIT License
//
// pattern-validation.test.ts
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
import { validatePatternSequence } from "../src/lib/patternValidation";
import type { PatternSpecification } from "../src/api/types";

/** Pattern-sequence rules (FR-16, spec §10 "pattern-sequence builder validation"). */

const V: PatternSpecification = { type: "Vertex" };
const E: PatternSpecification = { type: "Edge", direction: "OutgoingEdge" };
const VLE = (min: number, max: number): PatternSpecification => ({
  type: "VariableLengthEdge",
  direction: "OutgoingEdge",
  minLength: min,
  maxLength: max,
});

describe("pattern sequence validation", () => {
  it("accepts an empty sequence", () => {
    expect(validatePatternSequence([])).toBeNull();
  });

  it("accepts vertex-edge alternation starting with a vertex", () => {
    expect(validatePatternSequence([V])).toBeNull();
    expect(validatePatternSequence([V, E, V])).toBeNull();
    expect(validatePatternSequence([V, VLE(1, 3), V, E, V])).toBeNull();
  });

  it("accepts a level-0 edge start", () => {
    expect(validatePatternSequence([E])).toBeNull();
    expect(validatePatternSequence([E, V, E])).toBeNull();
  });

  it("rejects two vertex steps in a row", () => {
    expect(validatePatternSequence([V, V])).not.toBeNull();
  });

  it("rejects two edge steps in a row (incl. variable-length)", () => {
    expect(validatePatternSequence([V, E, VLE(1, 2)])).not.toBeNull();
  });

  it("rejects min > max on a variable-length edge", () => {
    expect(validatePatternSequence([V, VLE(5, 2), V])).toMatch(/minLength/);
  });

  it("rejects max above the API cap of 100", () => {
    expect(validatePatternSequence([V, VLE(1, 101), V])).toMatch(/100/);
  });

  it("accepts max exactly at the cap", () => {
    expect(validatePatternSequence([V, VLE(1, 100), V])).toBeNull();
  });
});
