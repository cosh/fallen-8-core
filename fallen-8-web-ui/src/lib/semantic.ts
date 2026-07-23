import type { SemanticTraversalSpecification, SubGraphSemanticSummary } from "../api/types";
import { parseVector } from "./vector";

/**
 * The declarative semantic-traversal drafts (features element-embeddings and
 * subgraph-semantic-thresholds), as edited in the studio and built into requests. Pure
 * logic here so the gating rules — which mirror the server's one-owner-per-slot and
 * metric constraints — are unit-testable without a screen.
 *
 * Two shapes share one query core:
 * - The Path screen edits a {@link SemanticDraft}: the query plus the block-local
 *   minScore filter / costBySimilarity cost switches.
 * - The Subgraph screen edits a bare {@link SemanticQueryDraft}: one query per request;
 *   the thresholds live on the vertex-filter SLOTS (top level and per vertex pattern
 *   step), not in the block.
 *
 * Reference for the rules: features/done/element-embeddings/README.md, "Semantic traversal".
 */

export type SemanticSource = "vector" | "text";
export type SemanticMetric = "Cosine" | "DotProduct" | "L2";

/**
 * How a vertex-filter slot is filled (feature subgraph-semantic-thresholds): nothing, a
 * compiled C# fragment, or a declarative semantic threshold. One owner per slot is
 * structural — a slot has exactly one mode.
 */
export type SlotMode = "everything" | "fragment" | "semantic";

/** The query core: what the traversal scores against, without any slot decisions. */
export interface SemanticQueryDraft {
  source: SemanticSource;
  vectorText: string;
  queryText: string;
  embeddingName: string;
  metric: SemanticMetric;
}

export const DEFAULT_SEMANTIC_QUERY_DRAFT: SemanticQueryDraft = {
  source: "vector",
  vectorText: "",
  queryText: "",
  embeddingName: "default",
  metric: "Cosine",
};

/** The Path screen's block: the query plus its declarative filter/cost switches. */
export interface SemanticDraft extends SemanticQueryDraft {
  /** Whether the block is active (sent, and owning its declarative slots). */
  enabled: boolean;
  /** Apply the declarative minScore vertex filter. */
  minScoreEnabled: boolean;
  minScore: string;
  /** Apply the declarative similarity vertex cost (path only, non-DotProduct). */
  costBySimilarity: boolean;
}

export const DEFAULT_SEMANTIC_DRAFT: SemanticDraft = {
  ...DEFAULT_SEMANTIC_QUERY_DRAFT,
  enabled: false,
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

export type SemanticQueryBuild =
  | { ok: true; spec: SemanticTraversalSpecification }
  | { ok: false; error: string };

/**
 * Builds the query core of a semantic block from a draft — the one implementation both
 * screens validate with. `providerEnabled` is the resolved provider state (null =
 * unknown/not-yet-computed): queryText needs it true.
 */
export function buildSemanticQuery(
  draft: SemanticQueryDraft,
  options: { providerEnabled: boolean | null },
): SemanticQueryBuild {
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

  return { ok: true, spec };
}

/**
 * Threshold text → finite number; empty or non-numeric is undefined (an empty input is
 * NOT silently zero). The one parser every threshold slot builds and validates with.
 */
export function parseThreshold(text: string): number | undefined {
  const trimmed = text.trim();
  if (!trimmed) {
    return undefined;
  }
  const value = Number(trimmed);
  return Number.isFinite(value) ? value : undefined;
}

/**
 * One line for the subgraph list's semantic badge tooltip: what the registered subgraph
 * was bound to and where its thresholds sit.
 */
export function describeSemanticSummary(summary: SubGraphSemanticSummary): string {
  const compare = summary.metric === "L2" ? "≤" : "≥";
  const parts = [`${summary.metric} over '${summary.embeddingName}' (d=${summary.dimension})`];
  if (summary.minScore != null) {
    parts.push(`pre-filter ${compare} ${summary.minScore}`);
  }
  for (const threshold of summary.patternThresholds ?? []) {
    parts.push(`step ${threshold.pattern} ${compare} ${threshold.minScore}`);
  }
  if (summary.queryText) {
    parts.push(`from text "${summary.queryText}"`);
  }
  parts.push("bound at creation — recalculate reuses the stored vector");
  return parts.join(" · ");
}

export type SemanticBuild =
  | { ok: true; spec: SemanticTraversalSpecification | undefined }
  | { ok: false; error: string };

/**
 * Builds the Path screen's block from a draft, applying the same guards the server
 * enforces so an invalid request is structurally caught before submit. `allowCost` is
 * false where costBySimilarity cannot apply. Returns spec: undefined when the block is
 * disabled.
 */
export function buildSemanticSpec(
  draft: SemanticDraft,
  options: { allowCost: boolean; providerEnabled: boolean | null },
): SemanticBuild {
  if (!draft.enabled) {
    return { ok: true, spec: undefined };
  }

  const query = buildSemanticQuery(draft, { providerEnabled: options.providerEnabled });
  if (!query.ok) {
    return query;
  }
  const spec = query.spec;

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
