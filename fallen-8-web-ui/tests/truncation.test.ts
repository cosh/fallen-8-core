// MIT License
//
// truncation.test.ts
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
import { isTruncated } from "../src/lib/truncation";
import type { GraphREST } from "../src/api/types";

/** FR-7: the only truncation signal is count == requested cap. */

function graph(vertices: number, edges: number): GraphREST {
  return {
    vertices: Array.from({ length: vertices }, (_, i) => ({
      id: i,
      creationDate: "",
      modificationDate: "",
    })),
    edges: Array.from({ length: edges }, (_, i) => ({
      id: 1000 + i,
      creationDate: "",
      modificationDate: "",
      sourceVertex: 0,
      targetVertex: 1,
    })),
  };
}

describe("truncation detection", () => {
  it("flags a result that filled the cap", () => {
    expect(isTruncated(graph(600, 400), 1000)).toBe(true);
  });

  it("does not flag a result under the cap", () => {
    expect(isTruncated(graph(3, 2), 1000)).toBe(false);
  });

  it("counts vertices and edges together", () => {
    expect(isTruncated(graph(999, 0), 1000)).toBe(false);
    expect(isTruncated(graph(999, 1), 1000)).toBe(true);
  });

  it("handles empty graphs", () => {
    expect(isTruncated(graph(0, 0), 1000)).toBe(false);
  });
});
