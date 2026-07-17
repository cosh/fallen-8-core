import { describe, expect, it } from "vitest";
import {
  buildSemanticSpec,
  DEFAULT_SEMANTIC_DRAFT,
  semanticOwnsVertexCost,
  semanticOwnsVertexFilter,
  type SemanticDraft,
} from "../src/lib/semantic";

/**
 * The declarative semantic-traversal rules (feature element-embeddings), tested as pure
 * logic: what the studio builds and refuses to build mirrors the server's one-owner-per-
 * slot and metric constraints, so an invalid request is caught before submit.
 */

const enabled = (patch: Partial<SemanticDraft> = {}): SemanticDraft => ({
  ...DEFAULT_SEMANTIC_DRAFT,
  enabled: true,
  vectorText: "[1, 0]",
  ...patch,
});

describe("buildSemanticSpec", () => {
  it("returns spec: undefined when the block is disabled (never sent)", () => {
    const result = buildSemanticSpec(DEFAULT_SEMANTIC_DRAFT, {
      allowCost: true,
      providerEnabled: true,
    });
    expect(result).toEqual({ ok: true, spec: undefined });
  });

  it("builds a vector query with name + metric", () => {
    const result = buildSemanticSpec(enabled({ embeddingName: "title", metric: "L2" }), {
      allowCost: true,
      providerEnabled: null,
    });
    expect(result).toEqual({
      ok: true,
      spec: { embeddingName: "title", metric: "L2", queryVector: [1, 0] },
    });
  });

  it("rejects an unparseable vector", () => {
    const result = buildSemanticSpec(enabled({ vectorText: "not a vector" }), {
      allowCost: true,
      providerEnabled: true,
    });
    expect(result.ok).toBe(false);
  });

  it("queryText needs the provider enabled; unknown/off are refused", () => {
    const draft = enabled({ source: "text", queryText: "red bicycles" });
    expect(buildSemanticSpec(draft, { allowCost: true, providerEnabled: true })).toEqual({
      ok: true,
      spec: { embeddingName: "default", metric: "Cosine", queryText: "red bicycles" },
    });
    expect(buildSemanticSpec(draft, { allowCost: true, providerEnabled: false }).ok).toBe(false);
    expect(buildSemanticSpec(draft, { allowCost: true, providerEnabled: null }).ok).toBe(false);
  });

  it("empty query text is rejected even with the provider on", () => {
    const draft = enabled({ source: "text", queryText: "  " });
    expect(buildSemanticSpec(draft, { allowCost: true, providerEnabled: true }).ok).toBe(false);
  });

  it("minScore must be finite and is passed through", () => {
    const ok = buildSemanticSpec(enabled({ minScoreEnabled: true, minScore: "0.7" }), {
      allowCost: true,
      providerEnabled: true,
    });
    expect(ok.ok && ok.spec?.minScore).toBe(0.7);

    const bad = buildSemanticSpec(enabled({ minScoreEnabled: true, minScore: "abc" }), {
      allowCost: true,
      providerEnabled: true,
    });
    expect(bad.ok).toBe(false);
  });

  it("costBySimilarity is path-only and never under DotProduct", () => {
    const dotProduct = buildSemanticSpec(
      enabled({ costBySimilarity: true, metric: "DotProduct" }),
      { allowCost: true, providerEnabled: true },
    );
    expect(dotProduct.ok).toBe(false);

    const subgraph = buildSemanticSpec(enabled({ costBySimilarity: true }), {
      allowCost: false,
      providerEnabled: true,
    });
    expect(subgraph.ok).toBe(false);

    const path = buildSemanticSpec(enabled({ costBySimilarity: true, metric: "Cosine" }), {
      allowCost: true,
      providerEnabled: true,
    });
    expect(path.ok && path.spec?.costBySimilarity).toBe(true);
  });
});

describe("slot ownership", () => {
  it("minScore owns the vertex-filter slot only when enabled + active", () => {
    expect(semanticOwnsVertexFilter(DEFAULT_SEMANTIC_DRAFT)).toBe(false);
    expect(semanticOwnsVertexFilter(enabled({ minScoreEnabled: true }))).toBe(true);
    expect(semanticOwnsVertexFilter({ ...enabled({ minScoreEnabled: true }), enabled: false })).toBe(
      false,
    );
  });

  it("costBySimilarity owns the vertex-cost slot only when enabled + active", () => {
    expect(semanticOwnsVertexCost(DEFAULT_SEMANTIC_DRAFT)).toBe(false);
    expect(semanticOwnsVertexCost(enabled({ costBySimilarity: true }))).toBe(true);
  });
});
