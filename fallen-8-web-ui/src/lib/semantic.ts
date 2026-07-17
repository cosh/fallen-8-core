import type { SemanticTraversalSpecification } from "../api/types";
import { parseVector } from "./vector";

/**
 * The declarative semantic-traversal block (feature element-embeddings), as edited in the
 * studio and built into a request. Pure logic here so the gating rules — which mirror the
 * server's one-owner-per-slot and metric constraints — are unit-testable without a screen,
 * and so the Path and Subgraph screens share exactly one implementation.
 *
 * Reference for the rules: features/done/element-embeddings/README.md, "Semantic traversal".
 */

export type SemanticSource = "vector" | "text";
export type SemanticMetric = "Cosine" | "DotProduct" | "L2";

export interface SemanticDraft {
  /** Whether the block is active (sent, and owning its declarative slots). */
  enabled: boolean;
  source: SemanticSource;
  vectorText: string;
  queryText: string;
  embeddingName: string;
  metric: SemanticMetric;
  /** Apply the declarative minScore vertex filter. */
  minScoreEnabled: boolean;
  minScore: string;
  /** Apply the declarative similarity vertex cost (path only, non-DotProduct). */
  costBySimilarity: boolean;
}

export const DEFAULT_SEMANTIC_DRAFT: SemanticDraft = {
  enabled: false,
  source: "vector",
  vectorText: "",
  queryText: "",
  embeddingName: "default",
  metric: "Cosine",
  minScoreEnabled: false,
  minScore: "0.7",
  costBySimilarity: false,
};

/** The block owns the vertex-FILTER slot when its minScore filter is active. */
export function semanticOwnsVertexFilter(draft: SemanticDraft): boolean {
  return draft.enabled && draft.minScoreEnabled;
}

/** The block owns the vertex-COST slot when costBySimilarity is active (path only). */
export function semanticOwnsVertexCost(draft: SemanticDraft): boolean {
  return draft.enabled && draft.costBySimilarity;
}

export type SemanticBuild =
  | { ok: true; spec: SemanticTraversalSpecification | undefined }
  | { ok: false; error: string };

/**
 * Builds the wire spec from a draft, applying the same guards the server enforces so an
 * invalid request is structurally caught before submit. `allowCost` is false on the
 * subgraph screen (costBySimilarity is a path concept the server 400s elsewhere).
 * `providerEnabled` is the resolved provider state (null = unknown/not-yet-computed):
 * queryText needs it true. Returns spec: undefined when the block is disabled.
 */
export function buildSemanticSpec(
  draft: SemanticDraft,
  options: { allowCost: boolean; providerEnabled: boolean | null },
): SemanticBuild {
  if (!draft.enabled) {
    return { ok: true, spec: undefined };
  }

  const spec: SemanticTraversalSpecification = {
    embeddingName: draft.embeddingName.trim() || undefined,
    metric: draft.metric,
  };

  if (draft.source === "text") {
    if (options.providerEnabled !== true) {
      return {
        ok: false,
        error:
          "query text needs the embedding provider enabled on this instance — paste a vector, or enable Fallen8:Embedding.",
      };
    }
    if (!draft.queryText.trim()) {
      return { ok: false, error: "enter query text (or switch to a pasted vector)." };
    }
    spec.queryText = draft.queryText;
  } else {
    const parsed = parseVector(draft.vectorText);
    if (!parsed.ok) {
      return { ok: false, error: `query vector: ${parsed.error}.` };
    }
    spec.queryVector = parsed.vector;
  }

  if (draft.minScoreEnabled) {
    const minScore = Number(draft.minScore);
    if (!Number.isFinite(minScore)) {
      return { ok: false, error: "minScore must be a finite number." };
    }
    spec.minScore = minScore;
  }

  if (draft.costBySimilarity) {
    if (!options.allowCost) {
      return { ok: false, error: "costBySimilarity applies to path queries only." };
    }
    if (draft.metric === "DotProduct") {
      return {
        ok: false,
        error:
          "costBySimilarity is not available under DotProduct (no honest non-negative cost mapping) — use Cosine or L2.",
      };
    }
    spec.costBySimilarity = true;
  }

  return { ok: true, spec };
}
