// MIT License
//
// vector.test.ts
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
import { parseVector } from "../src/lib/vector";

/**
 * Query-vector parsing (concept spec §6): dimension mismatches and NaNs must be caught
 * client-side, before the server's 400 — vectors are pasted in either JSON-array or
 * separator-delimited form.
 */
describe("parseVector", () => {
  it("parses a JSON array", () => {
    expect(parseVector("[0.12, -0.5, 0.33]")).toEqual({
      ok: true,
      vector: [0.12, -0.5, 0.33],
    });
  });

  it("parses comma/whitespace/semicolon-separated floats", () => {
    expect(parseVector("0.1, 0.2,0.3")).toEqual({ ok: true, vector: [0.1, 0.2, 0.3] });
    expect(parseVector("1 2\n3")).toEqual({ ok: true, vector: [1, 2, 3] });
    expect(parseVector("1;2;3")).toEqual({ ok: true, vector: [1, 2, 3] });
    expect(parseVector("1e-3, -2.5E2")).toEqual({ ok: true, vector: [0.001, -250] });
  });

  it("rejects empty input", () => {
    expect(parseVector("").ok).toBe(false);
    expect(parseVector("   ").ok).toBe(false);
    expect(parseVector("[]").ok).toBe(false);
  });

  it("rejects malformed JSON and non-arrays", () => {
    expect(parseVector("[0.1, 0.2").ok).toBe(false);
    expect(parseVector('{"a": 1}').ok).toBe(false);
  });

  it("rejects non-finite components with a 1-based position", () => {
    const nan = parseVector("[0.1, null, 0.3]");
    expect(nan).toEqual({ ok: false, error: "component 2 is not a finite number" });
    expect(parseVector("0.1, abc").ok).toBe(false);
    expect(parseVector('[1, 2, 1e999]').ok).toBe(false); // Infinity after JSON.parse
    expect(parseVector("[true]").ok).toBe(false); // booleans are not floats
  });
});
