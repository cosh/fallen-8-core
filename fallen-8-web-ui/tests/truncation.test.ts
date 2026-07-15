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
