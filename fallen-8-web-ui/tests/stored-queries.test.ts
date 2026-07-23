import { describe, expect, it } from "vitest";
import {
  buildPathSpecification,
  describeStoredSpecification,
  hasAnyPathFragment,
  normalizePatterns,
  pathBlockFromDraft,
  STORED_QUERY_NAME,
  subGraphBlock,
} from "../src/lib/storedQueries";
import {
  DEFAULT_PATH_DRAFT,
  type PathDraft,
  type SubgraphPatternDraft,
} from "../src/state/instanceStore";
import type { PatternSpecification } from "../src/api/types";

/**
 * Stored-query spec building (concept spec §5.1): the server 400s when storedQuery is
 * mixed with inline fragments, so these builders must make that state unrepresentable —
 * pinned here rather than trusted to JSX.
 */

const inlineDraft: PathDraft = {
  ...DEFAULT_PATH_DRAFT,
  vertexFilter: "return (v) => true;",
  edgeCost: "return (e) => 1.0;",
};

describe("buildPathSpecification", () => {
  it("stored source sends storedQuery and NO filter/cost keys", () => {
    const spec = buildPathSpecification({
      ...inlineDraft,
      filterSource: "stored",
      storedQuery: "adults-shortest",
    });
    expect(spec.storedQuery).toBe("adults-shortest");
    expect(spec).not.toHaveProperty("filter");
    expect(spec).not.toHaveProperty("cost");
    // Numeric bounds and the algorithm stay per-request even in stored mode.
    expect(spec.maxDepth).toBe(DEFAULT_PATH_DRAFT.maxDepth);
    expect(spec.pathAlgorithmName).toBe("BLS");
  });

  it("inline source sends fragments and NO storedQuery key", () => {
    const spec = buildPathSpecification(inlineDraft);
    expect(spec).not.toHaveProperty("storedQuery");
    expect(spec.filter?.vertexFilter).toBe("return (v) => true;");
    expect(spec.filter?.edgeFilter).toBeUndefined();
    expect(spec.cost?.edgeCost).toBe("return (e) => 1.0;");
  });

  it("a stale storedQuery name in the draft does not leak into an inline run", () => {
    const spec = buildPathSpecification({ ...inlineDraft, storedQuery: "leftover" });
    expect(spec).not.toHaveProperty("storedQuery");
  });

  it("attaches the semantic block and omits the fragment slots it owns", () => {
    // minScore owns vertexFilter, costBySimilarity owns vertexCost — the inline fragments
    // must NOT ride along (the server 400s if both fill one slot), but stay in the draft.
    const draft: PathDraft = {
      ...DEFAULT_PATH_DRAFT,
      algorithm: "DIJKSTRA",
      vertexFilter: "return (v) => true;",
      vertexCost: "return (v) => 1.0;",
      edgeFilter: "return (e) => true;",
    };
    const spec = buildPathSpecification(draft, {
      queryVector: [1, 0],
      metric: "Cosine",
      minScore: 0.7,
      costBySimilarity: true,
    });
    expect(spec.semantic?.minScore).toBe(0.7);
    expect(spec.filter?.vertexFilter).toBeUndefined();
    expect(spec.cost?.vertexCost).toBeUndefined();
    // Non-owned fragments are unaffected.
    expect(spec.filter?.edgeFilter).toBe("return (e) => true;");
  });

  it("keeps the fragment when the semantic block does NOT own that slot", () => {
    const spec = buildPathSpecification(
      { ...DEFAULT_PATH_DRAFT, vertexFilter: "return (v) => true;" },
      { queryVector: [1, 0], metric: "Cosine" }, // no minScore -> does not own vertexFilter
    );
    expect(spec.filter?.vertexFilter).toBe("return (v) => true;");
    expect(spec.semantic?.queryVector).toEqual([1, 0]);
  });
});

describe("pathBlockFromDraft / hasAnyPathFragment", () => {
  it("omits empty filter/cost blocks entirely", () => {
    expect(pathBlockFromDraft(DEFAULT_PATH_DRAFT)).toEqual({
      filter: undefined,
      cost: undefined,
    });
    expect(hasAnyPathFragment(DEFAULT_PATH_DRAFT)).toBe(false);
  });

  it("keeps only committed fragments", () => {
    const block = pathBlockFromDraft(inlineDraft);
    expect(block.filter).toEqual({
      vertexFilter: "return (v) => true;",
      edgeFilter: undefined,
      edgePropertyFilter: undefined,
    });
    expect(block.cost).toEqual({ vertexCost: undefined, edgeCost: "return (e) => 1.0;" });
    expect(hasAnyPathFragment(inlineDraft)).toBe(true);
  });
});

/** A draft row with the slot-state fields every builder pattern now carries. */
function draftPattern(
  pattern: Partial<SubgraphPatternDraft> & { key: string; type: PatternSpecification["type"] },
): SubgraphPatternDraft {
  return { filterMode: "everything", semanticMinScore: "0.7", ...pattern };
}

describe("normalizePatterns / subGraphBlock", () => {
  it("strips builder keys and per-type-irrelevant fields", () => {
    const normalized = normalizePatterns([
      draftPattern({ key: "k1", type: "Vertex", patternName: "", direction: "OutgoingEdge" }),
      draftPattern({
        key: "k2",
        type: "Edge",
        patternName: "e",
        direction: "IncomingEdge",
        minLength: 1,
        maxLength: 3,
      }),
      draftPattern({
        key: "k3",
        type: "VariableLengthEdge",
        direction: "OutgoingEdge",
        minLength: 2,
        maxLength: 5,
      }),
    ]);
    expect(normalized[0]).not.toHaveProperty("key");
    expect(normalized[0].direction).toBeUndefined(); // Vertex has no direction
    expect(normalized[0].patternName).toBeUndefined(); // empty → omitted
    expect(normalized[1].minLength).toBeUndefined(); // plain Edge has no lengths
    expect(normalized[1].direction).toBe("IncomingEdge");
    expect(normalized[2].minLength).toBe(2);
    expect(normalized[2].maxLength).toBe(5);
  });

  it("a vertex slot sends exactly what its mode says", () => {
    const normalized = normalizePatterns([
      draftPattern({
        key: "k1",
        type: "Vertex",
        filterMode: "fragment",
        vertexFilter: "return (v) => true;",
      }),
      draftPattern({
        key: "k2",
        type: "Vertex",
        filterMode: "semantic",
        semanticMinScore: "0.6",
        vertexFilter: "return (v) => true;", // stays in the draft, must not travel
      }),
      draftPattern({
        key: "k3",
        type: "Vertex",
        filterMode: "everything",
        vertexFilter: "return (v) => true;", // stale fragment, must not travel
      }),
    ]);
    expect(normalized[0].vertexFilter).toBe("return (v) => true;");
    expect(normalized[0]).not.toHaveProperty("semanticMinScore");
    expect(normalized[1].vertexFilter).toBeUndefined();
    expect(normalized[1].semanticMinScore).toBe(0.6);
    expect(normalized[2].vertexFilter).toBeUndefined();
    expect(normalized[2]).not.toHaveProperty("semanticMinScore");
  });

  it("subGraphBlock omits empty parts", () => {
    expect(subGraphBlock("", "", [])).toEqual({
      vertexFilter: undefined,
      edgeFilter: undefined,
      patterns: undefined,
    });
    const block = subGraphBlock("return (ge) => true;", "", [{ type: "Vertex" }]);
    expect(block.vertexFilter).toBe("return (ge) => true;");
    expect(block.patterns).toHaveLength(1);
  });
});

describe("STORED_QUERY_NAME", () => {
  it("matches the server's safe-path-segment rule", () => {
    expect(STORED_QUERY_NAME.test("adults-shortest")).toBe(true);
    expect(STORED_QUERY_NAME.test("A_1-b")).toBe(true);
    expect(STORED_QUERY_NAME.test("")).toBe(false);
    expect(STORED_QUERY_NAME.test("has space")).toBe(false);
    expect(STORED_QUERY_NAME.test("slash/y")).toBe(false);
    expect(STORED_QUERY_NAME.test("x".repeat(129))).toBe(false);
    expect(STORED_QUERY_NAME.test("x".repeat(128))).toBe(true);
  });
});

describe("describeStoredSpecification", () => {
  it("renders a Path block's committed fragments as labeled rows", () => {
    const preview = describeStoredSpecification(
      "Path",
      JSON.stringify({
        filter: { vertexFilter: "return (v) => true;" },
        cost: { edgeCost: "return (e) => 1.0;" },
      }),
    );
    expect(preview.rows).toEqual([
      { label: "filter.vertexFilter", fragment: "return (v) => true;" },
      { label: "cost.edgeCost", fragment: "return (e) => 1.0;" },
    ]);
    expect(preview.note).toBeNull();
  });

  it("renders SubGraph patterns with type, name, and combined fragments", () => {
    const preview = describeStoredSpecification(
      "SubGraph",
      JSON.stringify({
        vertexFilter: "return (ge) => true;",
        patterns: [
          { type: "Vertex", patternName: "start", vertexFilter: "return (v) => true;" },
          { type: "Edge" },
        ],
      }),
    );
    expect(preview.rows[0]).toEqual({
      label: "vertexFilter",
      fragment: "return (ge) => true;",
    });
    expect(preview.rows[1].label).toBe("pattern #1 Vertex 'start'");
    expect(preview.rows[1].fragment).toBe("return (v) => true;");
    expect(preview.rows[2].fragment).toBe("— no fragments (match everything)");
  });

  it("degrades to a note on empty / unparseable specifications", () => {
    expect(describeStoredSpecification("Path", null).note).toBe(
      "no stored specification",
    );
    expect(describeStoredSpecification("Path", "{not json").note).toBe(
      "unparseable stored specification",
    );
    expect(describeStoredSpecification("Path", "{}").note).toBe(
      "empty template — matches everything",
    );
  });
});
