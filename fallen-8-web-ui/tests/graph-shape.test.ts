import { describe, expect, it } from "vitest";
import { shapeSuggestions } from "../src/state/graphShape";
import type { GraphStatisticsREST } from "../src/api/types";

/**
 * The schema-cache suggestion feeds (feature studio-coverage): datalists must be
 * empty (never crash) without a snapshot, and null names — legal on the wire — are
 * filtered rather than rendered as "null" options.
 */

const shape: GraphStatisticsREST = {
  vertexCount: 10,
  edgeCount: 20,
  vertexLabels: {
    top: [
      { name: "person", count: 6 },
      { name: null, count: 4 },
    ],
    distinctTotal: 2,
  },
  edgeLabels: { top: [{ name: "knows", count: 20 }], distinctTotal: 1 },
  inDegree: { min: 0, max: 5, mean: 2, p50: 2, p90: 4, p99: 5 },
  outDegree: { min: 0, max: 5, mean: 2, p50: 2, p90: 4, p99: 5 },
  totalDegree: { min: 0, max: 10, mean: 4, p50: 4, p90: 8, p99: 10 },
  propertyKeys: {
    top: [
      { name: "age", count: 10 },
      { name: "name", count: 9 },
    ],
    distinctTotal: 2,
  },
  indices: [
    { name: "ages", type: "RangeIndex", keys: 5, values: 10 },
    { name: null, type: "DictionaryIndex", keys: 1, values: 1 },
  ],
  memory: {
    processWorkingSetBytes: 1,
    gcHeapBytes: 1,
    gcLastHeapSizeBytes: 1,
    gcFragmentedBytes: 0,
  },
  computedInMs: 1.5,
  sampled: false,
  sampleStride: 1,
};

describe("shapeSuggestions", () => {
  it("returns empty feeds when no snapshot has been computed", () => {
    for (const value of [undefined, null]) {
      const s = shapeSuggestions(value);
      expect(s.vertexLabels).toEqual([]);
      expect(s.edgeLabels).toEqual([]);
      expect(s.propertyKeys).toEqual([]);
      expect(s.indexIds).toEqual([]);
    }
  });

  it("extracts names and drops nulls", () => {
    const s = shapeSuggestions(shape);
    expect(s.vertexLabels).toEqual(["person"]);
    expect(s.edgeLabels).toEqual(["knows"]);
    expect(s.propertyKeys).toEqual(["age", "name"]);
    expect(s.indexIds).toEqual(["ages"]);
  });

  it("tolerates null top lists and a missing indices array", () => {
    const sparse = {
      ...shape,
      vertexLabels: { top: null, distinctTotal: 0 },
      indices: null,
    };
    const s = shapeSuggestions(sparse);
    expect(s.vertexLabels).toEqual([]);
    expect(s.indexIds).toEqual([]);
  });
});
