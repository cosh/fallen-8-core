import type {
  PathCostSpecification,
  PathFilterSpecification,
  PathSpecification,
  PatternSpecification,
  SemanticTraversalSpecification,
  StoredPathQueryBlock,
  StoredSubGraphQueryBlock,
} from "../api/types";
import type { PathDraft } from "../state/instanceStore";

/**
 * Pure stored-query logic (concept spec §5): spec builders that make the server's
 * "storedQuery is mutually exclusive with inline fragments" 400 structurally
 * unreachable, block extraction for "Save as stored query…", and the read-only
 * preview rows rendered under the picker.
 */

/** Server-side name rule: always a safe URL path segment, compared case-sensitively. */
export const STORED_QUERY_NAME = /^[A-Za-z0-9_-]{1,128}$/;

/**
 * The whole spec from a draft — stored and inline fragments never travel together. The
 * semantic block (feature element-embeddings) attaches regardless of the filter source
 * (it supplies the query vector to declarative closures, inline fragments, or a stored
 * path query's fragments alike).
 */
export function buildPathSpecification(
  draft: PathDraft,
  semantic?: SemanticTraversalSpecification,
): PathSpecification {
  const base = {
    pathAlgorithmName: draft.algorithm,
    maxDepth: draft.maxDepth,
    maxResults: draft.maxResults,
    maxPathWeight: draft.maxPathWeight,
    ...(semantic ? { semantic } : {}),
  };
  if (draft.filterSource === "stored") {
    return { ...base, storedQuery: draft.storedQuery };
  }
  // When the semantic block owns a delegate slot (minScore -> vertexFilter,
  // costBySimilarity -> vertexCost) the inline fragment is OMITTED, not sent alongside it:
  // the server 400s if both fill one slot, and the UI already disabled that slot. The
  // fragment stays in the draft, so turning the semantic option off restores it.
  const semanticOwnsFilter = semantic?.minScore !== undefined;
  const semanticOwnsCost = semantic?.costBySimilarity === true;
  return {
    ...base,
    filter: {
      vertexFilter: semanticOwnsFilter ? undefined : draft.vertexFilter || undefined,
      edgeFilter: draft.edgeFilter || undefined,
      edgePropertyFilter: draft.edgePropertyFilter || undefined,
    },
    cost: {
      vertexCost: semanticOwnsCost ? undefined : draft.vertexCost || undefined,
      edgeCost: draft.edgeCost || undefined,
    },
  };
}

/** The draft's committed fragments as a registration block (empty parts omitted). */
export function pathBlockFromDraft(draft: PathDraft): StoredPathQueryBlock {
  const filter: PathFilterSpecification = {
    vertexFilter: draft.vertexFilter || undefined,
    edgeFilter: draft.edgeFilter || undefined,
    edgePropertyFilter: draft.edgePropertyFilter || undefined,
  };
  const cost: PathCostSpecification = {
    vertexCost: draft.vertexCost || undefined,
    edgeCost: draft.edgeCost || undefined,
  };
  const hasAny = (block: Record<string, string | undefined>) =>
    Object.values(block).some(Boolean);
  return {
    filter: hasAny(filter as Record<string, string | undefined>) ? filter : undefined,
    cost: hasAny(cost as Record<string, string | undefined>) ? cost : undefined,
  };
}

export function hasAnyPathFragment(draft: PathDraft): boolean {
  return Boolean(
    draft.vertexFilter ||
      draft.edgeFilter ||
      draft.edgePropertyFilter ||
      draft.vertexCost ||
      draft.edgeCost,
  );
}

/**
 * Strips builder-only keys and normalizes per-type fields the way the create form
 * always has (shared by PUT /subgraph and stored-query registration).
 */
export function normalizePatterns(
  patterns: (PatternSpecification & { key?: string })[],
): PatternSpecification[] {
  return patterns.map(({ key: _key, ...pattern }) => ({
    ...pattern,
    patternName: pattern.patternName || undefined,
    minLength: pattern.type === "VariableLengthEdge" ? pattern.minLength : undefined,
    maxLength: pattern.type === "VariableLengthEdge" ? pattern.maxLength : undefined,
    direction: pattern.type === "Vertex" ? undefined : pattern.direction,
  }));
}

export function subGraphBlock(
  vertexFilter: string,
  edgeFilter: string,
  patterns: PatternSpecification[],
): StoredSubGraphQueryBlock {
  return {
    vertexFilter: vertexFilter || undefined,
    edgeFilter: edgeFilter || undefined,
    patterns: patterns.length > 0 ? patterns : undefined,
  };
}

export interface StoredFragmentRow {
  label: string;
  fragment: string;
}

export interface StoredSpecificationPreview {
  rows: StoredFragmentRow[];
  note: string | null;
}

/**
 * Renders a stored entry's specificationJson as label/fragment rows — best-effort:
 * the JSON is the server's own stored document, but an unparseable one degrades to a
 * note instead of a crash.
 */
export function describeStoredSpecification(
  kind: string | null,
  specificationJson: string | null,
): StoredSpecificationPreview {
  if (!specificationJson) return { rows: [], note: "no stored specification" };
  let spec: Record<string, unknown>;
  try {
    spec = JSON.parse(specificationJson) as Record<string, unknown>;
  } catch {
    return { rows: [], note: "unparseable stored specification" };
  }

  const rows: StoredFragmentRow[] = [];
  const push = (label: string, fragment: unknown) => {
    if (typeof fragment === "string" && fragment) rows.push({ label, fragment });
  };

  if (kind === "Path") {
    const filter = (spec.filter ?? {}) as Record<string, unknown>;
    const cost = (spec.cost ?? {}) as Record<string, unknown>;
    push("filter.vertexFilter", filter.vertexFilter);
    push("filter.edgeFilter", filter.edgeFilter);
    push("filter.edgePropertyFilter", filter.edgePropertyFilter);
    push("cost.vertexCost", cost.vertexCost);
    push("cost.edgeCost", cost.edgeCost);
    return {
      rows,
      note: rows.length === 0 ? "empty template — matches everything" : null,
    };
  }

  // SubGraph
  push("vertexFilter", spec.vertexFilter);
  push("edgeFilter", spec.edgeFilter);
  const patterns = Array.isArray(spec.patterns)
    ? (spec.patterns as Record<string, unknown>[])
    : [];
  patterns.forEach((pattern, index) => {
    const type = typeof pattern.type === "string" ? pattern.type : "?";
    const name =
      typeof pattern.patternName === "string" && pattern.patternName
        ? ` '${pattern.patternName}'`
        : "";
    const fragments = [
      pattern.graphElementFilter,
      pattern.vertexFilter,
      pattern.edgeFilter,
      pattern.edgePropertyFilter,
    ]
      .filter((f): f is string => typeof f === "string" && Boolean(f))
      .join(" · ");
    rows.push({
      label: `pattern #${index + 1} ${type}${name}`,
      fragment: fragments || "— no fragments (match everything)",
    });
  });
  return {
    rows,
    note: rows.length === 0 ? "empty template — matches everything" : null,
  };
}
